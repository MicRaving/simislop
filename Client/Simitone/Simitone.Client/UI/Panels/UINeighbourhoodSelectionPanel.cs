using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework;
using FSO.Common.Rendering.Framework.IO;
using FSO.Content.Framework;
using FSO.Content.Model;
using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Common.Rendering.Framework.Model;
using FSO.HIT;
using FSO.Client;
using FSO.Content;
using FSO.Common.Utils;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using FSO.Common;
using System.IO;
using FSO.Files.RC;

namespace Simitone.Client.UI.Panels
{
    public class UINeighborhoodSelectionPanel : UIContainer
    {
        /// <summary>
        /// How a <see cref="UINeighborhoodSelectionPanel"/> should behave, for the callers that
        /// are not the plain "pick a house to play" flow.
        /// </summary>
        /// <remarks>
        /// The defaults describe the normal case: every lot is selectable, the lot panel's
        /// primary button reads "Enter House" and offers the "More" options. The visit-a-lot
        /// flow (see <c>UITS1NeighborhoodVisitLotPanel</c>) uses the same panel with a
        /// different primary caption, no options button, and residential lots taken out of the
        /// map because you cannot visit someone's home.
        /// </remarks>
        public class UINeighborhoodSelectionOptions
        {
            /// <summary>
            /// The options used when a caller does not supply any. Shared, so treat it as
            /// read-only: pass a new instance rather than changing this one.
            /// </summary>
            public static UINeighborhoodSelectionOptions Default = new UINeighborhoodSelectionOptions();

            /// <summary>
            /// Lot zones that cannot be picked on this view. A lot whose zone is listed here
            /// gets a button that ignores the mouse. Zone 0 is residential and 1 is community.
            /// </summary>
            public List<short> LotZoneExclusionFilter { get; } = new List<short>();

            /// <summary>
            /// Caption of the lot panel's primary button, when not moving a family in.
            ///
            /// <para>
            /// Null - the default - means "use the ordinary 'Enter House' caption", and the lot
            /// panel resolves that from Simitone's own string table (id 48) when it is built.
            /// Deliberately not defaulted to the literal here: this default lives on a static
            /// field, so a literal would be read out of the string tables' empty state at type
            /// initialisation and would freeze English into every panel that did not override it,
            /// translation or not.
            /// </para>
            /// </summary>
            public string PrimaryButtonText { get; set; }

            /// <summary>Whether the lot panel offers its "More" options at all.</summary>
            public bool MoreButtonEnabled { get; set; } = true;
        }

        public static NeighborhoodViewConfig[] Neighborhoods = new NeighborhoodViewConfig[]
        {
            new NeighborhoodViewConfig()
            {
                Graphic = "Nbhd\\NScreen.BMP",
                Scale = 1f,
                FullImageAnimations = new NeighborhoodImageAnim[] {new NeighborhoodImageAnim("Nbhd\\DiffN1-N2_8.bmp", "Nbhd\\DiffN1-N3_8.bmp", "Nbhd\\DiffN1-N4_8.bmp") }
            },
            new NeighborhoodViewConfig()
            {
                Graphic = "Downtown\\DScreen.bmp",
                Music = "station_dtnhood",
                Scale = 1f,
                // The sea and the rock jetty in the lower right, the map's one animated layer.
                // Downtown's traffic is painted into DScreen.bmp and its cars are lot objects, so
                // there is no car overlay here - see NeighbourhoodAnimations.DowntownWavePosition,
                // which also explains why dscreen05.bmp is not a sixth frame.
                FullImageAnimations = new NeighborhoodImageAnim[] {
                    new NeighborhoodImageAnim(NeighbourhoodAnimations.DowntownWavePosition, NeighbourhoodAnimations.DowntownFrames()),
                }
            },
            new NeighborhoodViewConfig()
            {
                Graphic = "VIsland\\visland.bmp",
                Music = "station_vacation",
                Scale = 1f,
                FullImageAnimations = new NeighborhoodImageAnim[] {
                    new NeighborhoodImageAnim("VIsland\\visland_waves001.bmp", "VIsland\\visland_waves002.bmp", "VIsland\\visland_waves003.bmp", "VIsland\\visland_waves004.bmp", "VIsland\\visland_waves005.bmp"),
                    new NeighborhoodImageAnim("VIsland\\visland_port.bmp"),
                    new NeighborhoodImageAnim("VIsland\\visland_trees.bmp"),
                }
            },
            new NeighborhoodViewConfig()
            {
                Graphic = "Community\\NScreen_unleashed.bmp",
                Scale = 2f,
                Pulsate = false,
                FullImageAnimations = new NeighborhoodImageAnim[] {
                    new NeighborhoodImageAnim("Community\\NScreen_unleashed_waves001.bmp", "Community\\NScreen_unleashed_waves002.bmp", "Community\\NScreen_unleashed_waves003.bmp", "Community\\NScreen_unleashed_waves004.bmp", "Community\\NScreen_unleashed_waves005.bmp", "Community\\NScreen_unleashed_waves006.bmp"),
                    // Nessie, the sea serpent, in the river. The art only exists under
                    // Nbhd\ and Community\ (there is no VIsland\ copy), and the Community river
                    // scene is its natural home; see NeighbourhoodAnimations.NessiePosition.
                    new NeighborhoodImageAnim(NeighbourhoodAnimations.NessiePosition, NeighbourhoodAnimations.NessieFrames()),
                },
                BGSound = "river_loop"
            },
            new NeighborhoodViewConfig()
            {
                Graphic = "Studiotown\\DScreen.bmp",
                Music = "station_superstar",
                Scale = 1f,
                FullImageAnimations = new NeighborhoodImageAnim[] {
                    new NeighborhoodImageAnim("Studiotown\\DScreen_top_layer.bmp"),
                    // One car idling in the parking bay. See NeighbourhoodAnimations.StudioCarFrames
                    // for why a single car pair is used rather than the whole series.
                    new NeighborhoodImageAnim(NeighbourhoodAnimations.StudioCarPosition, NeighbourhoodAnimations.StudioCarFrames()),
                }
            },
            new NeighborhoodViewConfig(),
            new NeighborhoodViewConfig()
            {
                Graphic = "Magicland\\DScreen.bmp",
                Pulsate = false,
                Music = "music_magictown",
                BGSound = "mt_river_loop",
                Scale = 1f,
                FullImageAnimations = new NeighborhoodImageAnim[] {
                    new NeighborhoodImageAnim(new Vector2(0, 62), "Magicland\\DScreen_waves1.bmp", "Magicland\\DScreen_waves2.bmp", "Magicland\\DScreen_waves3.bmp", "Magicland\\DScreen_waves4.bmp", "Magicland\\DScreen_waves5.bmp", "Magicland\\DScreen_waves6.bmp"),
                    // The airship over the meadow. Pulsate is off for this neighbourhood, so the
                    // 16 rotation frames loop 1..16 exactly.
                    new NeighborhoodImageAnim(NeighbourhoodAnimations.MagicBlimpPosition, NeighbourhoodAnimations.MagicBlimpFrames()),
                }
            },
        };

        public TS1Provider Provider;
        public event Action<int> OnHouseSelect;
        public HITSound BgSound;
        public Dictionary<int, Vector2> HousePositions;

        /// <summary>
        /// How this panel was configured by whoever built it. Never null.
        /// </summary>
        private readonly UINeighborhoodSelectionOptions options;

        /// <summary>
        /// The lot the player last clicked, i.e. the one the lot panel currently belongs to.
        /// <see langword="null"/> when nothing is selected.
        /// </summary>
        /// <remarks>
        /// This used to be inferred from whether a <see cref="UIHouseSelectPanel"/> happened to
        /// be open, which meant the camera could be zoomed in on a lot with no panel (or vice
        /// versa) and nothing could work out where it was. The camera state and the panel are
        /// now both driven off this.
        /// </remarks>
        private int? _selectedHouse;

        /// <summary>Which of the two camera states the view is in. See <see cref="NeighCameraMode"/>.</summary>
        private NeighCameraMode _selectedCameraMode = NeighCameraMode.FullScreen;

        private Vector2 _cp = new Vector2(800, 600) / 2;
        public float CenterPositionX
        {
            get
            {
                return _cp.X;
            }
            set
            {
                _cp.X = value;
            }
        }

        public float CenterPositionY
        {
            get
            {
                return _cp.Y;
            }
            set
            {
                _cp.Y = value;
                UpdatePosition();
            }
        }

        private float _z = 1f;
        public float Zoom
        {
            get
            {
                return _z;
            }
            set
            {
                _z = value;
            }
        }

        public UINeighborhoodSelectionPanel(ushort mode, UINeighborhoodSelectionOptions Options = null)
        {
            options = Options ?? UINeighborhoodSelectionOptions.Default;
            Provider = Content.Get().TS1Global;
            //The filter set is a property of the map, so it starts empty on every map. It is NOT
            //reset in PopulateScreen, which is called again on every neighbourhood switch: keeping
            //it would carry "show me the empty lots" from Downtown onto a map with none, and drop
            //the selection to nothing on arrival. PopulateScreen only re-applies it.
            ActiveLotFilters = NeighLotFilter.None;
            PopulateScreen(mode);
            GameResized();
        }

        public void UpdatePosition()
        {
            base.GameResized();
            var scale = GlobalSettings.Default.GraphicsHeight / 600.0f;
            ScaleX = ScaleY = scale * Zoom;

            X = (GlobalSettings.Default.GraphicsWidth) / 2 - _cp.X * ScaleX;
            Y = (GlobalSettings.Default.GraphicsHeight) / 2 - _cp.Y * ScaleY;

            //the 3D world follows the same numbers the bitmap does, every time they change - which
            //means during the tween as well as at its end, so panning and zooming drag the world
            //under the buttons rather than snapping it when the tween finishes
            UpdateWorldCamera();
        }

        /// <summary>
        /// Aims the generated 3D world at wherever the 2D camera currently is, using the panel's
        /// live zoom, centre and selection rather than a remembered target.
        /// </summary>
        private void UpdateWorldCamera()
        {
            if (_world3D == null) return;
            Vector2? housePosition = null;
            if (_selectedHouse.HasValue && HousePositions != null)
            {
                Vector2 found;
                if (HousePositions.TryGetValue(_selectedHouse.Value, out found)) housePosition = found;
            }
            _world3D.Focus(
                new NeighCameraTarget(_selectedCameraMode, Zoom, _cp.X, _cp.Y, _selectedHouse),
                housePosition);
        }

        public override void GameResized()
        {
            UpdatePosition();

            //A parked panel is not the screen. The camera follows the resize above - a placement is a
            //function of the aspect ratio - but the lot panel must not be rebuilt: it was closed when
            //the panel was parked, and reopening it here would put a "Enter House" button over a lot
            //the player is standing inside.
            if (_suspendedForLot) return;

            // The window boundaries moved, so anything measured against them has to be worked
            // out again. Zoomed out that is just the camera; zoomed in the lot panel was laid
            // out against the old screen size, so it has to be rebuilt too.
            if (_selectedCameraMode == NeighCameraMode.HouseFocus)
            {
                if (!_selectedHouse.HasValue)
                {
                    ResetZoom(); // selection went away, so there is nothing to stay zoomed in on
                }
                else
                {
                    SetCameraMode();
                }
            }
        }

        private Texture2D texture;
        private int Mode;

        // ------------------------------------------------------------------ lot facts and filters

        /// <summary>
        /// The two facts a lot filter can ask about, for every lot on this map, in the order the
        /// lot buttons are stacked: top of the map first, so "next lot" walks the map rather than
        /// the numbering.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One entry per <em>lot</em>, not per button. The shipped location tables carry a
        /// duplicate lot number in a couple of places - Studio Town lists lot 55 twice - and a
        /// filter has to have one opinion about a lot, not two. The duplicate is dropped here
        /// rather than in <see cref="LotLocationParser"/> because the parser's job is to report
        /// what the file says and both of those lines are real positions on the map.
        /// </para>
        /// <para>
        /// Rebuilt by <see cref="RefreshLotState"/>, which the filter bar calls before it touches
        /// anything: occupancy and zoning both change under a standing filter (an eviction empties
        /// a lot, a rezone re-zones one) and a filter set that has quietly gone stale is a filter
        /// that shows the wrong lots with no way for the player to tell.
        /// </para>
        /// </remarks>
        private readonly List<NeighLotFacts> _lotFacts = new List<NeighLotFacts>();

        /// <summary>The lot buttons currently on the map, in the order they were added.</summary>
        private readonly List<UINeighborhoodHouseButton> _lotButtons = new List<UINeighborhoodHouseButton>();

        /// <summary>
        /// The lot numbers the player can actually pick right now, in map order: on this map, not
        /// filtered out, and not one the view has taken off limits. This is what the Next and
        /// Previous buttons step through, so neither of them can land on a lot that is not there.
        /// </summary>
        private readonly List<int> _selectableLots = new List<int>();

        /// <summary>
        /// Which lot filters are switched on. See <see cref="NeighLotFilters"/> for what a filter
        /// is and why a filter that nothing matches is drawn dead rather than refused.
        /// </summary>
        public NeighLotFilter ActiveLotFilters { get; private set; } = NeighLotFilter.None;

        /// <summary>How many lots this map has, filtered or not.</summary>
        public int TotalLotCount
        {
            get { return _lotFacts.Count; }
        }

        /// <summary>
        /// Whether the map currently on screen has a lot-location table at all. False for a map
        /// whose STR chunk is missing, which is the only way this tree reaches a view with no lots
        /// in it - the six-slot band, a stripped neighbourhood folder, a hand-edited table.
        /// <see cref="NeighbourhoodModeMap"/> can name the lot numbers a view draws from, but a
        /// view with no table has no lots to put in them, and the two are different facts.
        /// </summary>
        public bool HasLotLocationTable { get; private set; }

        /// <summary>
        /// Every lot on this map with the two facts a filter asks about, in map order. Rebuilt by
        /// <see cref="RefreshLotState"/>, so read it after calling that rather than caching it.
        /// </summary>
        public IReadOnlyList<NeighLotFacts> LotFacts
        {
            get { return _lotFacts; }
        }

        /// <summary>How many of them are on show at the moment.</summary>
        public int VisibleLotCount
        {
            get { return _selectableLots.Count; }
        }

        /// <summary>
        /// The lot the player last clicked, i.e. the one whose panel is open. Null when nothing is
        /// selected. The camera already has it in <c>_selectedHouse</c>; this is the same value
        /// under a name that says what it means, for <see cref="StepToAdjacentLot"/>'s callers and
        /// for anything else that has to ask the map what it is showing.
        /// </summary>
        public int? SelectedLot
        {
            get { return _selectedHouse; }
        }

        /// <summary>
        /// The lot numbers the player can pick right now, in map order.
        /// </summary>
        public IReadOnlyList<int> SelectableLots
        {
            get { return _selectableLots; }
        }

        /// <summary>
        /// Re-reads every lot's occupancy and zone out of the neighbourhood and re-applies the
        /// filters.
        /// </summary>
        /// <remarks>
        /// Cheap enough to call whenever the filter bar is about to be pressed: a neighbourhood
        /// map is tens of lots and both questions are dictionary lookups. It is also the only way
        /// the bar's own state - which filters are on, which of them anything matches - can be
        /// right after a lot was evicted or rezoned from the lot panel that was open over the top
        /// of it.
        /// </remarks>
        public void RefreshLotState()
        {
            _lotFacts.Clear();
            var seen = new HashSet<int>();
            foreach (var button in _lotButtons)
            {
                if (!seen.Add(button.HouseNumber)) continue;
                _lotFacts.Add(ReadLotFacts(button.HouseNumber));
            }
            ApplyFilters();
        }

        /// <summary>
        /// One lot's two facts, read the same way the lot panel reads them so the two can never
        /// disagree: the zone out of the loaded dictionary with
        /// <see cref="LotZoning.DefaultZoneForLot"/> behind it, and occupancy from the family
        /// that owns the lot.
        /// </summary>
        private static NeighLotFacts ReadLotFacts(int lot)
        {
            var neigh = Content.Get().Neighborhood;
            short zone = LotZoning.DefaultZoneForLot(lot);
            if (neigh?.ZoningDictionary != null
                && neigh.ZoningDictionary.TryGetValue((short)lot, out var loaded))
            {
                zone = loaded;
            }
            var occupied = neigh?.GetFamilyForHouse((short)lot) != null;
            return new NeighLotFacts(lot, occupied, zone);
        }

        /// <summary>
        /// Switches one filter on or off and re-applies the set. Refuses a filter that is not one
        /// of the four, and a filter nothing on this map can match - the same rule the button's
        /// dead state advertises, so a press from a stale button cannot empty the map.
        /// </summary>
        /// <returns>True when the set changed, i.e. when the bar has anything to redraw.</returns>
        public bool ToggleLotFilter(NeighLotFilter filter)
        {
            if (!NeighLotFilters.IsSingle(filter)) return false;

            //Switching one *on* needs something to switch it to: a filter nothing on this map can
            //match would empty the map for a button the bar has already drawn dead. Switching one
            //off never does, which is the whole of the way back out of that state - a filter can
            //become unmatchable under a player who has it on (a rezone can move the last
            //residential lot across the line), and the press that gets the map back has to be the
            //one that turns it off.
            var alreadyOn = (ActiveLotFilters & filter) != 0;
            if (!alreadyOn && !NeighLotFilters.IsAvailable(filter, _lotFacts)) return false;

            var next = NeighLotFilters.Toggle(ActiveLotFilters, filter);
            if (next == ActiveLotFilters) return false;
            ActiveLotFilters = next;
            ApplyFilters();
            return true;
        }

        /// <summary>
        /// Turns every filter off, and is a no-op returning false when none were on - so the
        /// filter bar's "clear" step does not redraw the map for nothing.
        /// </summary>
        public bool ClearLotFilters()
        {
            if (ActiveLotFilters == NeighLotFilter.None) return false;
            ActiveLotFilters = NeighLotFilter.None;
            ApplyFilters();
            return true;
        }

        /// <summary>
        /// Hides the lots the current filter set rules out, and rebuilds the list Next and Previous
        /// step through.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A filtered-out lot is <em>hidden</em>, not greyed out. Retail's buttons had a disabled
        /// state, but that state is for a filter that matches nothing anywhere on the map - a
        /// control the player cannot use - and greying out thirty lots to say "these are not in
        /// your filter" would leave a map of dead spots that is harder to read than one with the
        /// lots simply gone.
        /// </para>
        /// <para>
        /// <b>A filter that hides the selected lot drops the selection.</b> Otherwise the camera
        /// would be zoomed in on a lot with no button under it and no way to tell why clicking the
        /// map does not seem to do what it used to. The reset is the ordinary one - the lot panel
        /// is killed and the camera eases back out - so it is the same state a player gets by
        /// clicking the background, and it is the only way out of the filtered view, as it is the
        /// only way out of a zoomed-in one.
        /// </para>
        /// </remarks>
        private void ApplyFilters()
        {
            var passes = LotPasses();

            //Two loops over the same list, back to back, so "on show" and "can be stepped to"
            //cannot disagree: the second reads the visibility the first just set. A lot the view
            //takes off limits - a residential lot in the visit-a-lot flow - stays off limits
            //whatever the filters say, and stays out of the Next/Previous list: the panel can open
            //no panel for it, so stepping onto it would move the camera somewhere with nothing to
            //click. The visibility itself is <see cref="ApplyLotVisibility"/> rather than an
            //expression written here, because the 3D swap has to be able to put the same answer
            //back and there must only be one of them.
            _selectableLots.Clear();
            ApplyLotVisibility(passes);
            foreach (var button in _lotButtons)
            {
                if (button.Visible && !_selectableLots.Contains(button.HouseNumber))
                {
                    _selectableLots.Add(button.HouseNumber);
                }
            }

            //a selection that is gone, whether because a filter hid it or because the map changed
            //under it, has to go with it - the camera must not be left zoomed in on a lot with no
            //button under it
            if (_selectedHouse.HasValue
                && (!passes.TryGetValue(_selectedHouse.Value, out var stillThere) || !stillThere))
            {
                ResetZoom();
            }

            //A filter change under a 3D world has to be told, or the buttons the loop above just
            //put back would stay drawn over the houses the world is standing in.
            SyncPaintedMapLayers();
        }

        /// <summary>
        /// What the current filter set says about every lot, by lot number.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ApplyLotVisibility"/> because
        /// <see cref="ApplyFilters"/> needs the same answers twice - once to draw with and once to
        /// decide whether the selected lot has been filtered out of existence - and because the
        /// answer is about a lot, not about a button: a lot this view has no button for still has
        /// one, and a button whose lot has no facts is treated as filtered out.
        /// </remarks>
        private Dictionary<int, bool> LotPasses()
        {
            var passes = new Dictionary<int, bool>(_lotFacts.Count);
            foreach (var facts in _lotFacts)
            {
                passes[facts.Lot] = NeighLotFilters.Passes(ActiveLotFilters, facts);
            }
            return passes;
        }

        /// <summary>
        /// Gives every lot button the visibility the filters and this view's own rules allow: on
        /// show for a lot on this map that passes the set and is not one the view takes off limits.
        /// </summary>
        /// <remarks>
        /// The one place that answer lives, because it is asked twice - by
        /// <see cref="ApplyFilters"/> when the set changes, and by
        /// <see cref="SyncPaintedMapLayers"/> when the painted map comes back from under a 3D
        /// world. Two copies of it would be two chances for "the button you can see" and "the
        /// button you can click" to stop meaning the same thing.
        /// </remarks>
        /// <param name="passes">What the filter set says, by lot number.</param>
        private void ApplyLotVisibility(Dictionary<int, bool> passes)
        {
            foreach (var button in _lotButtons)
            {
                bool shown;
                button.Visible = button.Enabled
                    && passes.TryGetValue(button.HouseNumber, out shown) && shown;
            }
        }

        /// <summary>
        /// The Next and Previous buttons: picks the neighbouring lot and selects it.
        /// </summary>
        /// <remarks>
        /// Routed through <see cref="SelectHouse"/> with the click-to-deselect behaviour turned
        /// off, so the camera, the lot panel and the 3D world's focus all move by exactly the same
        /// path a click on the lot takes - there is no second, simpler way for a lot to become
        /// selected, which is what keeps the three in step. Turning the toggle off matters: with
        /// one lot left under a filter, a second press of Next would otherwise land on the lot
        /// that is already selected and deselect it, which is not what a pair of arrows means.
        /// </remarks>
        /// <param name="direction">Positive for next, negative for previous.</param>
        /// <returns>The lot now selected, or null when there was nowhere to step to.</returns>
        public int? StepToAdjacentLot(int direction)
        {
            var next = NeighLotNavigator.Step(_selectableLots, _selectedHouse, direction);
            if (!next.HasValue) return null;
            SelectHouse(next.Value, false);
            return next;
        }

        /// <summary>
        /// The Add Lot button.
        /// </summary>
        /// <remarks>
        /// Always routes through <see cref="NeighAddLot.Plan"/> and shows whatever comes back, so
        /// the button is never a dead control: on a build with a lot-creation path it would create
        /// one, and on this one it says why it cannot. <see cref="NeighAddLot"/> is where the
        /// honest answer lives, and its remarks are the reason.
        /// </remarks>
        public void RequestAddLot()
        {
            var screen = UIScreen.Current as TS1GameScreen;
            RefreshLotState();

            //Every lot on the map, not the ones currently on show: a filter hides lots, it does
            //not free them, and "every lot in use" has to be answered about the map rather than
            //about what the player happens to be looking at.
            var lots = new List<int>(_lotFacts.Count);
            foreach (var facts in _lotFacts) lots.Add(facts.Lot);

            var plan = NeighAddLot.Plan(new NeighAddLotFacts(
                onMapScreen: screen != null && screen.TS1NeighPanel == this,
                lotsOnMap: lots,
                //no table means no runs, which is how "this map has no lot table" is spelled. A
                //map with a table always gets its view's real runs, so the four refusals are all
                //reachable and the order they are asked in is the one that matters.
                lotRanges: HasLotLocationTable
                    ? NeighbourhoodModeMap.LotRangesForMode((ushort)Mode)
                    : new NeighLotRange[0],
                //The one thing this tree cannot do. See NeighAddLot: a lot is a line in a fixed
                //table, a house file, a family record and a zoning line, and nothing here writes
                //any of them.
                lotCreationAvailable: false));

            if (!plan.IsRefusal)
            {
                //Unreachable today, and deliberately not a stub: a writer would set the lot up here
                //and then RefreshLotState() would pick it up, because the bar is already asking
                //for the state on every press.
                SimitoneLog.Warn("Neighbourhood lot " + plan.Lot + " was planned but this build has no"
                    + " lot-creation path, so nothing was created. See NeighAddLot.");
                return;
            }

            if (screen == null)
            {
                SimitoneLog.Warn("Add Lot was pressed with no game screen, so the refusal could not"
                    + " be shown: " + plan.Message);
                return;
            }

            screen.Dialogs.ShowAlert(plan.Title, plan.Message, plan.Detail);
        }

        /// <summary>
        /// The generated 3D world drawn behind this panel's controls, or <c>null</c> when there is
        /// no world. Owned by the panel and torn down with it.
        /// </summary>
        /// <remarks>
        /// <b>Being non-null does not mean it is being looked at.</b> The painted map is the
        /// guaranteed backdrop: the world is attached as soon as it can be built and stays
        /// attached while it warms up, but it only takes the screen once it has demonstrably
        /// produced something. <see cref="BackdropDecision"/> is what says which is on top.
        /// </remarks>
        private Neighbourhood3DView _world3D;

        /// <summary>
        /// The scene actually added to <c>GameFacade.Scenes</c>: the world wrapped in a proxy that
        /// counts its draw failures and notices a device reset, so the panel can tell a world that
        /// is quietly not drawing from one that never started.
        /// </summary>
        private NeighbourhoodWorldProxy _worldProxy;

        /// <summary>The generated neighbourhood the 3D view is drawing, or <c>null</c>.</summary>
        private NeighbourhoodSceneSource _sceneSource;

        /// <summary>
        /// Set once the player has entered a lot, after which this panel never attaches a generated
        /// world again. See <see cref="SuspendForLotEntry"/> - the scene lives in
        /// <c>GameFacade.Scenes</c> and nothing else would ever take it down again.
        /// </summary>
        private bool _suspendedForLot;

        /// <summary>How long the attached world has been attached. The mesh upload window is measured
        /// against this, and it restarts from zero whenever the world is rebuilt or the device
        /// resets, because either way the world owes the player a fresh demonstration that it
        /// works.</summary>
        private double _worldAttachedSeconds;

        /// <summary>
        /// The refresh this panel is waiting on, or <c>null</c>. Kept so
        /// <see cref="DetachWorld"/> can tell a generation that finished after the panel was torn
        /// down from one that finished while it was still up: the first is dropped, the second is
        /// what <see cref="AttachWorld"/> was waiting for.
        /// </summary>
        private NeighbourhoodRefreshJob _refreshJob;

        /// <summary>
        /// Whether this panel has asked the lifecycle for a world and does not have one yet.
        /// Read by <see cref="IsRefreshing"/>.
        /// </summary>
        /// <remarks>
        /// Set when a refresh is asked for and cleared only when a world is attached, so it covers
        /// both halves of the wait: a generation still running on a worker, and one that finished
        /// without a raster. The second half is the reason this is a flag rather than a query
        /// against the job - a slot the lifecycle has already refused answers every request with
        /// the same refusal, and a per-frame <see cref="AttachWorld"/> would be asking for a
        /// second generation sixty times a second for a neighbourhood that cannot have one.
        /// </summary>
        private bool _refreshPending;

        /// <summary>
        /// Whether the "drawn in 2D" line has already been said for this attempt, so
        /// <see cref="RefreshBackdrop"/> re-entering <see cref="AttachWorld"/> every frame while
        /// nothing is attached says it once.
        /// </summary>
        private bool _pendingLogged;

        /// <summary>
        /// The sentence a pending generation gets. Named rather than written out twice, because
        /// "still generating" and "could not be built" are opposite answers and a log that
        /// cannot tell them apart is the log a player cannot read.
        /// </summary>
        private const string PendingGenerationReason =
            "the generated world is still being generated on a worker, so the painted map stays up"
            + " until it lands";

        /// <summary>
        /// What the generated raster ought to produce, counted once per attach by
        /// <see cref="ProbeGeometry"/> and read every frame afterwards.
        /// </summary>
        private int _probeGroundQuads = NeighbourhoodWorldHealth.Unknown;
        private int _probeRoadQuads = NeighbourhoodWorldHealth.Unknown;
        private int _probeLots;

        /// <summary>
        /// A 1x1 fully transparent texture, swapped in for the painted map once the generated
        /// world is carrying the picture.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The painted map is not removed, it is emptied. <c>bg</c> is the element that carries
        /// "clicking the backdrop resets the zoom", and a hidden element is not necessarily a
        /// non-clickable one - keeping it in the tree, visible and hit-testable but drawing nothing
        /// is what makes the backdrop click behave identically in both views without a second
        /// input path.
        /// </para>
        /// <para>
        /// <b>The element is not the whole click target, and no longer can be.</b> It is shrunk to
        /// 1x1 here, and a region registered through <c>UIImage.ListenForMouse</c> is resized with
        /// it, so the element-sized region is one pixel of screen. That is why <c>PopulateScreen</c>
        /// registers a second, fixed-size region on the same element: the click target is the map's
        /// area, not the picture's size.
        /// </para>
        /// </remarks>
        private Texture2D _transparentBackdrop;

        /// <summary>The painted map element, the one that carries the backdrop click.</summary>
        private UIImage _backdrop;

        /// <summary>The painted map's own texture, restored whenever the bitmap is wanted back.</summary>
        private Texture2D _backdropTexture;

        /// <summary>
        /// The painted map's natural size in panel coordinates.
        ///
        /// <para>
        /// Recorded because <c>UIImage</c> only fills its width and height from the texture when
        /// they are still zero, so shrinking the element to carry a transparent quad and then
        /// putting the map back would otherwise leave it one pixel wide - which is the "distorted
        /// look" from the other end: not a blank field, but a postage stamp.
        /// </para>
        /// </summary>
        private Vector2 _backdropSize;

        /// <summary>
        /// What the backdrop element is currently showing. Initialised to
        /// <see cref="NeighbourhoodBackdropFrame.OverWorld"/> so that the first
        /// <see cref="ApplyFrame"/> with a painted map in hand always does its work rather than
        /// deciding there is nothing to do.
        /// </summary>
        private NeighbourhoodBackdropFrame _backdropFrame =
            NeighbourhoodBackdropFrame.OverWorld;

        /// <summary>
        /// The animation overlays drawn over the painted map: the waves, the river, the blimp,
        /// Nessie, the car. They are placed in the map's own 800x600 pixel space, so over a 3D world
        /// they would be water lying across a different picture rather than water in it, and they
        /// are taken away by <see cref="PaintedMapLayersVisible"/> for exactly as long as the
        /// generated world is carrying that picture.
        /// </summary>
        /// <remarks>
        /// Every element here is a <see cref="UINeighborhoodAnimationLayer"/>, and its
        /// <c>Draw</c> has to test <see cref="UIElement.Visible"/> for itself - a container calls
        /// every child's <c>Draw</c> without asking. See the note above
        /// <see cref="UINeighborhoodAnimationLayer.Draw"/>.
        /// </remarks>
        private readonly List<UIElement> _mapOverlays = new List<UIElement>();

        /// <summary>
        /// Whether the painted map's own layers - its animation overlays and its lot sprites - belong
        /// on screen for a decision.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The whole rule, and there is only one answer that says no.</b> Everything the painted
        /// map draws on top of itself is part of <em>its</em> picture: the waves, the river and
        /// Nessie are placed in the map's own 800x600 pixel space, and the lot sprites are the map's
        /// own house cut-outs. Over a generated neighbourhood neither of them labels something in
        /// the picture - an overlay is a 2D river lying across a 3D world, and a sprite is the
        /// second of two neighbourhoods drawn on top of the first. So the one state that takes them
        /// away is <see cref="NeighbourhoodViewDecision.Show3D"/>: the world is carrying the
        /// picture, and the picture's own layers are what the picture was made of.
        /// </para>
        /// <para>
        /// <b>The other two states keep them, which is the whole point of the warm-up.</b>
        /// <see cref="NeighbourhoodBackdrop.PaintedMapAndWorld"/> is a world still streaming in
        /// behind the painted map, and the map is still what the player is looking at; taking its
        /// water off it would leave a picture of a neighbourhood with the river missing. So the
        /// overlays come back for 2D and for the warm-up, and only the promotion takes them away -
        /// in the same frame that takes the bitmap off the backdrop element.
        /// </para>
        /// <para>
        /// <b>Not gated on the capability flags.</b> This panel has always emptied the backdrop
        /// element itself for a generated world whatever <see cref="NeighbourhoodWorldFlags"/>
        /// says - see <see cref="ApplyFrame"/>, which is the code the overlays used to follow -
        /// so gating the rest of the painted map's layers on a flag could produce a screen that is
        /// neither 2D nor 3D: the map with its river taken off it, or the 3D world with a 2D river
        /// lying across it. That second one is what a player reported, from a build with the
        /// capability off and the view set to 3D. The flags therefore govern the click instead;
        /// see <see cref="WorldClickIsEnabled"/>.
        /// </para>
        /// <para>
        /// A static rule over a value rather than another method on the panel because it is asked
        /// twice per frame - by <see cref="ApplyFrame"/> and by <see cref="SyncPaintedMapLayers"/>,
        /// which are the only two writers of these elements - and because a rule that can be asked
        /// without a graphics device, a screen or a neighbourhood is a rule that can be asserted.
        /// One answer, two callers, and the possibility of the two disagreeing is not on the table.
        /// </para>
        /// </remarks>
        /// <param name="decision">What the backdrop rule decided, this frame.</param>
        public static bool PaintedMapLayersVisible(NeighbourhoodViewDecision decision)
        {
            return !decision.Show3D;
        }

        /// <summary>
        /// Whether a click on the map area is a click on the generated world - and so picks the lot
        /// under the mouse and takes control of it - rather than a click on the painted bitmap,
        /// which resets the zoom.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rule is the one that hid the buttons, which is the whole of the fix.</b> Part 2's
        /// D1: this gate used to ask <see cref="NeighbourhoodWorldFlags"/>, while
        /// <see cref="PaintedMapLayersVisible"/> asked <see cref="NeighbourhoodViewDecision.Show3D"/>
        /// - and the two could disagree. The shipped configuration is exactly the disagreeing one:
        /// the view setting defaults to 3D and all seven capability flags default off, so the world
        /// was carrying the picture, the painted map's lot sprites were hidden because nothing was
        /// left to click, and the click that was supposed to replace them was refused. A fresh
        /// install could not open a house by clicking one; Next and Previous were the only way.
        /// </para>
        /// <para>
        /// So this is now the exact negation of <see cref="PaintedMapLayersVisible"/>: whatever hides
        /// the lot buttons enables the world click, and there is no second switch that can produce a
        /// screen with neither. <see cref="OnBackdropMouseDown"/> asks it with the same decision the
        /// hide is computed from, which is what makes "the buttons and the click agree" a property of
        /// the code rather than of a coincidence between two defaults.
        /// </para>
        /// <para>
        /// <b>Off means the input is exactly what it was.</b> A click on the painted map resets the
        /// zoom and the lot buttons' own regions do the picking.
        /// </para>
        /// </remarks>
        /// <param name="decision">What the backdrop rule decided, this frame.</param>
        public static bool WorldClickIsEnabled(NeighbourhoodViewDecision decision)
        {
            return decision.Show3D;
        }

        /// <summary>
        /// Whether <see cref="SyncPaintedMapLayers"/> has taken the lot buttons and the overlays
        /// away, so it knows whether the filters' answer has to be rebuilt or is already on the
        /// buttons.
        /// </summary>
        private bool _paintedMapLayersHidden;

        /// <summary>
        /// Which neighbourhood view's <em>artwork</em> is on screen. Normally <see cref="Mode"/>,
        /// but a view whose background is missing from the install falls back to one that has
        /// one; the lot table, the lot numbers and the travel buttons all still come from
        /// <see cref="Mode"/>, so only the picture changes.
        /// </summary>
        private ushort _bitmapMode;

        /// <summary>
        /// The decision this panel ended up with - 2D or 3D and why - for the log. Never
        /// <c>null</c>.
        /// </summary>
        public NeighbourhoodViewSelection ViewSelection { get; private set; }

        /// <summary>
        /// Which backdrop is being drawn, and why: the guarantee that the painted map stays up
        /// until the generated world has demonstrably produced something, and comes straight back
        /// when it stops.
        /// </summary>
        public NeighbourhoodViewDecision BackdropDecision { get; private set; }

        /// <summary>
        /// Whether the generated 3D world is carrying the picture.
        /// </summary>
        /// <remarks>
        /// Not the same as "a world is attached": a world that is still uploading its mesh is
        /// attached and is not yet what the player is looking at. Callers that want "is the player
        /// looking at 3D" want this; callers that want "is there a world being maintained" want
        /// <see cref="HasGeneratedWorld"/>.
        /// </remarks>
        public bool IsShowing3D
        {
            get { return BackdropDecision.Show3D; }
        }

        /// <summary>Whether a generated world is attached, whether or not it is being drawn yet.</summary>
        public bool HasGeneratedWorld
        {
            get { return _world3D != null; }
        }

        /// <summary>
        /// Drops the generated world and re-decides which view to draw. Called by
        /// <c>NeighborhoodService</c> after a lot is bulldozed or rezoned, so the facades that were
        /// built from the old house files do not outlive them.
        /// </summary>
        public void InvalidateGeneratedWorld()
        {
            if (_suspendedForLot) return;
            DetachWorld();
            AttachWorld();
        }

        /// <summary>
        /// Whether the player has gone into a lot, which is the one moment this panel must not
        /// stand a 3D world up again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Entering a lot tears the neighbourhood view down; it does not refresh it. The view lived
        /// in <c>GameFacade.Scenes</c>, which is a different tree from the screen's own children, so
        /// removing this panel from the screen took the painted map away and left the generated
        /// world in front of the lot. Then entering the lot asked for the neighbourhood to be brought
        /// up to date, and <see cref="InvalidateGeneratedWorld"/> answered that by rebuilding the
        /// world and putting it back - on top of a lot, with nothing left in the session to ever
        /// remove it, because the panel that owned it is no longer in the tree.
        /// </para>
        /// <para>
        /// So this is a park rather than a teardown: it hides the panel and stops its world drawing,
        /// and disposes nothing. The scene stays in <c>GameFacade.Scenes</c> with its uploaded
        /// buffers, and coming back shows it again rather than building a second neighbourhood -
        /// which is §8.1's "entering or leaving a lot must not tear down and rebuild the world".
        /// </para>
        /// <para>
        /// <b>Two flags, and that is the whole mechanism.</b> <c>UIElement.WillDraw</c> walks the
        /// parent chain, so one <c>Visible = false</c> here silences the lot buttons, the animation
        /// overlays and the backdrop's own click regions at once; and <c>_3DLayer.Draw</c> and
        /// <c>PreDraw</c> skip an invisible scene, so the generated world stops clearing the frame
        /// and drawing over the lot. <c>Update</c> is still called, which is what keeps the terrain's
        /// slice queue and the facade queue moving while the player is away - so the map is warm and
        /// in the same place when they come back.
        /// </para>
        /// <para>
        /// <b>What this is not yet.</b> The lot view still brings up its own <c>World</c>, so this is
        /// "the same world is still there, hidden, and comes back without being rebuilt" rather than
        /// "the lot is drawn inside the map as one scene with two camera states". That is the larger
        /// half of §8.3 and the plan sequences it after this one; what lands here is the part that
        /// makes it possible - one shared world, no teardown - and the seam is written down rather
        /// than papered over.
        /// </para>
        /// </summary>
        public void SuspendForLotEntry()
        {
            if (_suspendedForLot) return;
            _suspendedForLot = true;

            //The lot panel is the screen's rather than this panel's - OpenHouseSelectPanel adds
            //itself straight to the UI screen - so hiding this panel does not hide it. It is closed
            //here, which is right anyway: it is about the lot the player is walking into.
            //
            //The selection and the camera state are deliberately left alone. A park means "the map is
            //still where the player left it", and ResumeAfterLotEntry puts the camera on the lot they
            //came from. Clearing the selection here and leaving the camera where it was would be the
            //one state the NeighCameraMode remarks call out as unreachable: zoomed in, with no panel
            //to get out of it.
            if (LastHS != null)
            {
                LastHS.Kill();
                LastHS = null;
            }

            if (_worldProxy != null)
            {
                //Left in GameFacade.Scenes rather than removed from it. Removed stops Update, and
                //Update is where the terrain's slice queue and the facade queue are driven; invisible
                //is enough to keep the frame, and keeping the calls is what makes the return instant.
                _worldProxy.Visible = false;
            }
            Visible = false;

            SimitoneLog.Info("The neighbourhood view for " + Mode + " was parked because the player"
                + " entered a lot. Its generated world is still built and still in the scene layer ("
                + (_world3D != null ? _world3D.Describe() : "none attached")
                + "), and is not rebuilt while the lot is up.");
        }

        /// <summary>
        /// Whether this panel is parked for a lot. The screen asks rather than inferring it from the
        /// field being null, because a parked panel is still a live object: it is the thing holding
        /// the world.
        /// </summary>
        public bool IsSuspendedForLot
        {
            get { return _suspendedForLot; }
        }

        /// <summary>
        /// Which neighbourhood view this panel is showing. The screen's own
        /// <c>CurrentNeighborhoodMode</c> is which view a lot belongs to; this is which one is on
        /// screen, which is what decides whether a parked panel may be brought back rather than
        /// rebuilt.
        /// </summary>
        public ushort Neighbourhood
        {
            get { return (ushort)Mode; }
        }

        /// <summary>
        /// Whether the world this panel is holding is still the world its neighbourhood has, so a
        /// parked view can be brought back as it was.
        /// </summary>
        /// <remarks>
        /// <b>Asked after the return-trip refresh, which is the only moment it means anything.</b>
        /// Coming back from a lot runs the same per-lot repair as any other visit, and a repair
        /// produces a <em>new</em> layout and a new raster. So "this panel has a world" is not the
        /// question; "this panel's world is the current one" is, and the answer is reference
        /// identity against the shared authority's entry rather than anything derived from it - a
        /// rebuilt world is a new object by construction, which is exactly what we want to notice.
        /// </remarks>
        /// <param name="world">The world the neighbourhood now has, or <c>null</c>.</param>
        public bool WorldIsCurrent(NeighbourhoodGeneratedWorld world)
        {
            if (world == null || !world.HasWorld) return false;
            if (_sceneSource == null) return false;
            return ReferenceEquals(_sceneSource.Layout, world.Layout)
                && ReferenceEquals(_sceneSource.Map, world.Map);
        }

        /// <summary>
        /// Brings a parked view back: the panel and its world start drawing again, with the camera
        /// where the player left it, and nothing is rebuilt.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="SuspendForLotEntry"/>, and only called once the caller has
        /// established that the world did not move underneath the panel - see
        /// <see cref="WorldIsCurrent"/>. A panel whose world has been replaced has to be rebuilt
        /// rather than shown, and <see cref="InvalidateGeneratedWorld"/> is what does that.
        /// </remarks>
        /// <returns>Whether there was a world to bring back.</returns>
        public bool ResumeAfterLotEntry()
        {
            if (!_suspendedForLot) return false;
            _suspendedForLot = false;
            Visible = true;
            if (_worldProxy != null) _worldProxy.Visible = true;
            //The camera did not move while this was parked, so it does not need telling where it is -
            //but a placement is a function of the aspect ratio, and the window may have been resized
            //while a lot was up.
            UpdatePosition();
            SimitoneLog.Info("The parked neighbourhood view for " + Mode + " is back on screen with the"
                + " world it already had (" + (_world3D != null ? _world3D.Describe() : "none") + ").");
            return _world3D != null;
        }

        public void PopulateScreen(ushort mode)
        {
            var childClone = new List<UIElement>(Children);

            // The mode comes from the frontend and the location STR table, so it is not guaranteed
            // to index the config array. Slot 6 is also a deliberately empty config whose Graphic
            // is null. Either would otherwise throw inside the texture provider rather than
            // degrade. Fall back to the base neighbourhood view and say so in the log.
            if (mode < 1 || mode > Neighborhoods.Length || Neighborhoods[mode - 1]?.Graphic == null)
            {
                SimitoneLog.Warn("No neighbourhood view config for mode " + mode + "; falling back to the base neighbourhood view.");
                mode = 1;
            }

            var config = Neighborhoods[mode - 1];
            foreach (var child in childClone) Remove(child);

            //The artwork is resolved separately from the lot data, because they can disagree: a
            //view whose background is not in this install still has its lots, its lot numbers and
            //its travel button, and taking the whole view away over a missing bitmap would be a
            //much bigger loss than drawing a different one. So the picture falls back and the data
            //does not - see ResolveBitmapMode, and the warning below.
            _bitmapMode = ResolveBitmapMode(mode, ViewIsInstalled, out var bitmapReason);
            if (_bitmapMode == 0)
            {
                SimitoneLog.Error("The neighbourhood view " + mode + " cannot be drawn: "
                    + bitmapReason + " The lot buttons will be shown over an empty field; the"
                    + " install looks incomplete.");
            }
            else if (_bitmapMode != mode)
            {
                SimitoneLog.Warn("The background for neighbourhood view " + mode + " is not"
                    + " in this install (" + bitmapReason + "). The lot table is view " + mode
                    + "'s, so the lots stay where they are, but view " + _bitmapMode + "'s"
                    + " artwork ('" + BitmapOf(_bitmapMode) + "') is being drawn underneath them.");
            }
            var artwork = _bitmapMode == 0 ? config : Neighborhoods[_bitmapMode - 1];

            AddOutline();

            var backdropTexture = LoadBackdropTexture(_bitmapMode);
            var bg = new UIImage(backdropTexture);
            Add(bg);
            _backdrop = bg;
            _backdropTexture = backdropTexture;
            _backdropSize = backdropTexture == null
                ? new Vector2(NeighbourhoodAnimations.BackgroundWidth, NeighbourhoodAnimations.BackgroundHeight)
                : new Vector2(backdropTexture.Width, backdropTexture.Height);
            _transparentBackdrop = _transparentBackdrop ?? MakeTransparentBackdrop();

            //The backdrop carries the map's click. Registered twice, and the second registration is
            //the one that survives the 3D swap: UIImage.ListenForMouse sizes its region from the
            //element, and ApplyFrame shrinks the element to a 1x1 transparent quad once the world is
            //carrying the picture - so a region that tracks the element is a 1x1 click target, and a
            //player clicking a 3D house would hit nothing at all. A region in panel coordinates,
            //fixed at the painted map's own size, keeps the whole map area a target in both views.
            //Which of the two regions is topmost is not decided here and does not need to be: both
            //call the same handler, and the lot buttons, which sit above the backdrop and would
            //otherwise shadow it, are hidden while the world is up (see SyncPaintedMapLayers).
            bg.ListenForMouse((evt, state) =>
            {
                if (evt == UIMouseEventType.MouseDown) OnBackdropMouseDown(state);
            });
            bg.ListenForMouse(
                new Rectangle(0, 0, (int)_backdropSize.X, (int)_backdropSize.Y),
                (evt, state) =>
                {
                    if (evt == UIMouseEventType.MouseDown) OnBackdropMouseDown(state);
                });

            _mapOverlays.Clear();
            //The new map starts with its buttons and overlays on show, so the hide state starts off
            //too - otherwise a world that is already up would find nothing to restore on the way
            //back and leave the map bare.
            _paintedMapLayersHidden = false;
            //The element starts out carrying the map, so the frame has to start out disagreeing
            //with it - otherwise the first decision would find "nothing to do" and leave the map
            //up over the 3D world for ever.
            _backdropFrame = NeighbourhoodBackdropFrame.OverWorld;

            HousePositions = new Dictionary<int, Vector2>();
            var locationIff = Content.Get().Neighborhood.LotLocations;
            var locations = locationIff.Get<STR>(mode);
            if (locations == null)
            {
                // This map has no location table, so there are no lot buttons to build. The
                // camera and any open lot panel still have to be dealt with though, or switching
                // into a map like this leaves the previous map's zoomed-in state behind with
                // nothing left to click to get out of it.
                DetachWorld();
                HasLotLocationTable = false;
                ViewSelection = NeighbourhoodViewSelection.Bitmap(NeighbourhoodView.Off,
                    "this map has no lot location table, so there is nothing to place in a world");
                //Straight assignment rather than ApplyDecision, because this path is not a fallback
                //from anything and must not log as one - so the painted map's own layers are put
                //back here by hand. DetachWorld above ran the same rule against the *previous* map's
                //decision, which on a cold switch can still have said 3D.
                BackdropDecision = NeighbourhoodViewDecision.ForSetting(NeighbourhoodView.Off);
                SyncPaintedMapLayers();

                //The lot state goes before FinishPopulate, not after: the previous map's buttons
                //are about to be torn down, and a filter bar that had been asked to refresh against
                //them would be describing lots that no longer exist.
                _lotButtons.Clear();
                _lotFacts.Clear();
                _selectableLots.Clear();
                FinishPopulate();
                return;
            }

            var rawLocations = new List<string>(locations.Length);
            for (int i = 0; i < locations.Length; i++) rawLocations.Add(locations.GetString(i));

            HasLotLocationTable = true;
            var buttons = new List<UINeighborhoodHouseButton>();
            foreach (var loc in LotLocationParser.ParseLotLocationTable(rawLocations))
            {
                var button = new UINeighborhoodHouseButton(loc.Number, SelectHouse, artwork.Scale);
                button.Position = new Vector2(loc.X, loc.Y);
                HousePositions[loc.Number] = button.Position;
                buttons.Add(button);

                // A lot can be shown here but be off limits in this context - a residential lot
                // is shown on the map but cannot be visited - so the button goes dead instead of
                // letting the player open a panel whose action cannot work.
                if (Content.Get().Neighborhood.ZoningDictionary.TryGetValue((short)loc.Number, out var zone)
                    && options.LotZoneExclusionFilter.Contains(zone))
                {
                    button.Enabled = false;
                }
            }

            //Map order, not table order: the buttons are stacked top of the map first so the top
            //lot is drawn over the ones below it, and the filter bar's Next/Previous walk the same
            //list. The lot state is built from this list rather than from `buttons`, so it is in
            //the same order and holds one entry per lot even where the table lists a lot twice.
            var ordered = buttons.OrderBy(x => x.Y).ToList();
            _lotButtons.Clear();
            _lotButtons.AddRange(ordered);
            foreach (var btn in ordered) Add(btn);

            //The filter set is deliberately kept across a neighbourhood switch (see the
            //constructor) and simply re-applied, so a player who asked for the empty lots gets the
            //empty lots of the map they travelled to - and, on a map with no empty lots, the
            //button that filter belongs to draws itself dead.
            RefreshLotState();

            // Read the layers through the helper rather than walking the array here: it drops
            // null entries, which would otherwise throw inside the layer constructor while this
            // screen is still being built. The overlays come from the view whose artwork is
            // actually on screen, not from the view whose lot table is in use - Nessie belongs to
            // the Community river and the car belongs to the studio lot, and putting either over
            // the wrong map is exactly the mistake the folder filter exists to catch.
            foreach (var layer in NeighbourhoodAnimations.OverlaysForMode(
                Neighborhoods, _bitmapMode == 0 ? mode : _bitmapMode))
            {
                var lelem = new UINeighborhoodAnimationLayer(layer, artwork.Pulsate, artwork.FrameDuration);
                lelem.Position = layer.Position;
                Add(lelem);
                _mapOverlays.Add(lelem);
            }

            BgSound?.RemoveOwner(-25);
            if (artwork.Music != null) FSO.HIT.HITVM.Get().PlaySoundEvent(artwork.Music);
            if (artwork.BGSound != null)
            {
                BgSound = HITVM.Get().PlaySoundEvent(artwork.BGSound);
                BgSound.AddOwner(-25);
            }
            Mode = mode;
            MaybeExportNeighbourhood3D(locations);

            //after Mode is set, because the world is generated per mode (§0 decision 4) and the
            //seed and sidecar slot are both derived from it
            DetachWorld();
            AttachWorld();

            FinishPopulate();
        }

        // -------------------------------------------------------------------------------------
        // The generated 3D neighbourhood.
        //
        // §7.3. Everything below is additive: the painted map, its overlays, the house buttons
        // and the switcher are all built exactly as they were, and the 3D world is either drawn
        // behind them or is not built at all.
        //
        // THE CONTRACT. The painted map is the guaranteed backdrop; the generated world is a guest
        // in it. Attaching a world no longer empties the map. The map stays up - opaque, carrying
        // the picture - until the world has demonstrably produced a *neighbourhood*: its mesh has
        // uploaded, the raster it was built from actually had ground and roads in it, the frame is
        //submitting geometry frame after frame, and the lots have their houses on them. Only then
        //does the map become the 1x1 transparent quad that keeps the backdrop click working. Any
        //later failure - a draw that throws, a device that resets, a mesh that never lands, a
        //facade pass that submits nothing - puts the map straight back, in the same frame, without
        //rebuilding anything. There is no state in which neither is drawn, which is why
        //NeighbourhoodBackdrop has three values and none of them is "neither".
        //
        // While the houses are still being built the map stays up and the world keeps drawing
        //underneath it, which is the whole of the answer to a slow facade build: the painted
        //neighbourhood is on screen the entire time, and the cut to the generated world happens on
        // the first frame the world has a street of houses to show rather than a bare plain. The
        // world cannot be drawn *behind* the map, because GameFacade.Scenes is the graphics layer
        // in front of the UI one - so "the map stays until the world is ready" is the sequence,
        // rather than a cross-dissolve between two neighbourhoods.
        //
        // Which of the three is current is NeighbourhoodBackdropRule.Decide over one frame's
        // NeighbourhoodWorldHealth. Both are pure, both live in NeighbourhoodViewSetting.cs, and
        // both are asserted headlessly - so "generation failed", "TryCreate refused", "the mesh
        // never uploaded", "the map has no geometry", "a draw threw" and "the setting changed with
        // the map up" are tests rather than screenshots. ViewSelection and BackdropDecision carry
        // the answer with a sentence explaining it, because a player who asked for 3D and got the
        // painted map needs to be able to find out why from the log.
        //
        // Nothing here can stop the neighbourhood opening. Every failure - a missing lot location
        // table, an unreadable HouseNN.iff, a road graph that will not connect, a GPU that will not
        // take a vertex buffer - ends in the painted map, which is the plan's non-negotiable.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Builds the generated world if the setting asks for one and attaches it. The painted map
        /// is deliberately left exactly as <see cref="PopulateScreen"/> built it: the swap to 3D
        /// happens in <see cref="RefreshBackdrop"/>, and only once the world has earned it.
        /// </summary>
        /// <remarks>
        /// <para><b>The generation is asynchronous, so this returns before there is a world.</b>
        /// <see cref="NeighbourhoodLifecycle.Ensure"/> runs the 262 144 tiles on a worker and
        /// hands the finished world back through the apply step, on the game thread, which is
        /// where this method is entered again from. On a cold visit this therefore builds a scene
        /// on a later frame and not on this one - and the painted map, which is what
        /// <see cref="PopulateScreen"/> already put up, covers the gap. On a warm one the world is
        /// already in <see cref="NeighbourhoodWorldCache"/>, the very first
        /// <see cref="BuildSceneSource"/> hands it back, and nothing is generated at all.</para>
        ///
        /// <para><b>One refresh, not one per frame.</b> <see cref="RefreshBackdrop"/> re-enters
        /// here on every frame until a world is attached, so this asks the lifecycle only when it
        /// has nothing in hand and nothing running; a refresh that finished without a world is a
        /// refusal the lifecycle remembers, and asking again is what
        /// <see cref="IsRefreshing"/> exists to stop.</para>
        /// </remarks>
        private void AttachWorld()
        {
            //A panel the player has left behind by walking into a lot. It is not in the tree any
            //more, so nothing will ever remove a world it attached - see SuspendForLotEntry.
            if (_suspendedForLot) return;

            var requested = NeighbourhoodViewSetting.Resolve(CurrentNeighbourhoodView());
            if (requested != NeighbourhoodView.World3D)
            {
                ViewSelection = NeighbourhoodViewSelection.ForSetting(requested);
                ApplyDecision(NeighbourhoodViewDecision.ForSetting(requested));
                SimitoneLog.Info("Neighbourhood " + Mode + " is drawn in 2D: " + ViewSelection.Reason + ".");
                return;
            }

            //A world that is already here is the only way out of this method that does not ask
            //for one, and it is deliberately checked first: the apply below re-enters here with
            //the finished world in the cache, and asking again at that point would start a second
            //generation to produce the world this panel is already holding.
            string failure = null;
            var source = BuildSceneSource(Mode, out failure);
            if (source != null && source.HasWorld)
            {
                //Counted before the GPU is touched, because the counts are what decide whether this
                //raster is worth a scene at all.
                ProbeGeometry(source);

                string reason;
                var view = Neighbourhood3DView.TryCreate(GameFacade.GraphicsDevice, source, out reason);
                if (view == null)
                {
                    ViewSelection = NeighbourhoodViewSelection.AfterGeneration(requested, null, reason);
                    ApplyDecision(NeighbourhoodBackdropRule.Decide(requested, HealthFor(null, reason)));
                    LogDrawnIn2DOnce();
                    return;
                }

                _sceneSource = source;
                _world3D = view;
                _worldProxy = new NeighbourhoodWorldProxy(GameFacade.GraphicsDevice, view);
                _worldAttachedSeconds = 0d;
                GameFacade.Scenes.Add(_worldProxy);

                //The refresh this panel was waiting for has produced something, so the wait is
                //over and the next failure is allowed to say so.
                _refreshPending = false;
                _pendingLogged = false;

                //The painted map has NOT been touched. It is still the backdrop, and RefreshBackdrop()
                //keeps it that way until this world has produced geometry.
                ApplyDecision(NeighbourhoodBackdropRule.Decide(requested, HealthFor(view)));

                ViewSelection = NeighbourhoodViewSelection.ThreeD(requested,
                    "the generated world was built from the neighbourhood's own lots");
                SimitoneLog.Info("Neighbourhood " + Mode + " has a generated world attached; the painted"
                    + " map stays up until it draws something: " + BackdropDecision.Reason + ".");
                return;
            }

            //Nothing to draw yet. Either a refresh for this neighbourhood is already running -
            //on a cold visit BuildSceneSource's own RefreshNow is what started it, or the
            //neighbourhood service started it before this panel existed - or one has finished
            //without a world, and the lifecycle is remembering that refusal on purpose.
            ProbeGeometry(source);
            if (RefreshInFlight())
            {
                _refreshPending = true;
                JoinRefresh();
                ViewSelection = NeighbourhoodViewSelection.Bitmap(requested, PendingGenerationReason);
                //Fault None on purpose: nothing has failed, the world is simply not here yet, and
                //a decision that said otherwise would log a fallback that did not happen.
                ApplyDecision(new NeighbourhoodViewDecision(requested,
                    NeighbourhoodBackdrop.PaintedMap, false, NeighbourhoodWorldFault.None,
                    PendingGenerationReason));
                return;
            }

            ViewSelection = NeighbourhoodViewSelection.AfterGeneration(requested, null, failure);
            ApplyDecision(NeighbourhoodBackdropRule.Decide(requested, HealthFor(null, failure)));
            LogDrawnIn2DOnce();
        }

        /// <summary>
        /// Whether a generation for this neighbourhood is still running on a worker.
        /// </summary>
        /// <remarks>
        /// Asked of the lifecycle rather than of <see cref="_refreshJob"/>, because the refresh is
        /// usually started by <see cref="NeighbourhoodLifecycle.RefreshNow"/> - which
        /// <see cref="BuildSceneSource"/> calls, and which does not start one when the lifecycle
        /// remembers a refusal - so on a cold visit the worker is already running a moment before
        /// this panel has a job of its own to ask. A generation somebody else started is a
        /// generation this panel has to wait for, not one to start a second of.
        ///
        /// <para>Narrower than <see cref="IsRefreshing"/> on purpose: this is "is there work left
        /// to wait for", which is the only state in which asking for a world is worth doing.</para>
        /// </remarks>
        private bool RefreshInFlight()
        {
            return NeighbourhoodLifecycle.RefreshInFlight(Mode) != null;
        }

        /// <summary>
        /// Asks the lifecycle for this mode's world on a worker, and keeps the job so the finished
        /// world arrives back on the game thread.
        /// </summary>
        /// <remarks>
        /// <b>Why the job is kept rather than thrown away.</b> The refresh outlives the panel that
        /// asked for it - a first visit to a cold neighbourhood is seconds of generation - and the
        /// apply is the panel's own <see cref="AttachWorld"/>. <see cref="DetachWorld"/> clears
        /// <see cref="_refreshJob"/>, and the guard in the callback is what turns that into "a
        /// panel that has been removed, or has moved to another neighbourhood, is not called back
        /// into" rather than "a world is stood up behind a screen that is no longer up".
        ///
        /// <para><b>Why the source is a factory.</b> The provider is read on the worker rather than
        /// captured across the thread boundary, and a fresh one is built per call because
        /// <c>TS1LotContentSource</c> caches the lot records it has read in a Dictionary - sharing
        /// one would share that Dictionary across two threads.</para>
        /// </remarks>
        private void JoinRefresh()
        {
            _refreshJob = NeighbourhoodLifecycle.Ensure(
                FSOEnvironment.UserDir, Mode, Mode, NeighbourhoodLifecycle.RequestedView,
                () => new TS1LotContentSource(Content.Get().Neighborhood, Mode),
                world =>
                {
                    if (_refreshJob == null) return;
                    AttachWorld();
                });
        }

        /// <summary>
        /// Whether this panel is still waiting for a world it has asked the lifecycle for.
        /// </summary>
        /// <remarks>
        /// True while a generation runs <em>and</em> for one that finished without a raster; see
        /// <see cref="_refreshPending"/>. <see cref="RefreshBackdrop"/> passes it to
        /// <see cref="NeighbourhoodBackdropRule.Reconcile"/> as "a world is on its way", which is
        /// what stops the per-frame reconcile from re-entering <see cref="AttachWorld"/> while a
        /// generation is in flight - which, before this, was one "drawn in 2D" warning per frame
        /// for as long as the generation took.
        /// </remarks>
        private bool IsRefreshing()
        {
            return _refreshPending;
        }

        /// <summary>
        /// Says once that this neighbourhood is being drawn in 2D, and not once per frame.
        /// </summary>
        /// <remarks>
        /// Never while a generation is still running: "still generating" and "could not be built"
        /// are opposite answers and only the second is a warning. And only the first time per
        /// attempt - <see cref="RefreshBackdrop"/> re-enters <see cref="AttachWorld"/> on every
        /// frame until a world is attached or something invalidates the slot, and a warning per
        /// frame is several thousand lines saying one thing.
        /// </remarks>
        private void LogDrawnIn2DOnce()
        {
            if (_pendingLogged) return;
            _pendingLogged = true;
            SimitoneLog.Warn("Neighbourhood " + Mode + " is drawn in 2D: " + ViewSelection.Reason + ".");
        }

        /// <summary>
        /// Tears the generated world down and puts the painted map back, immediately and whatever
        /// the last decision said.
        /// </summary>
        private void DetachWorld()
        {
            if (_worldProxy != null)
            {
                GameFacade.Scenes.Remove(_worldProxy);
                _worldProxy.Dispose();
                _worldProxy = null;
            }
            _world3D = null;
            _sceneSource = null;
            //The panel is not waiting for a world any more, so the apply step must not call back
            //into it: a generation that finishes a second later than the panel was removed - or
            //than the neighbourhood was switched - would otherwise stand a world up behind a
            //screen that is no longer up, and nothing would ever take it down.
            _refreshJob = null;
            _refreshPending = false;
            _pendingLogged = false;
            _worldAttachedSeconds = 0d;
            _probeGroundQuads = NeighbourhoodWorldHealth.Unknown;
            _probeRoadQuads = NeighbourhoodWorldHealth.Unknown;
            _probeLots = 0;
            ApplyFrame(NeighbourhoodBackdropFrame.PaintedMap);
            //And the lot buttons and overlays the 3D swap took away, in the same statement. Every
            //caller of this that still has a live panel follows it with an ApplyDecision in the same
            //frame - which would restore them anyway - so this is not what makes them come back; it
            //is what makes "the painted map is back" mean the whole painted map rather than the part
            //of it that is one element.
            SyncPaintedMapLayers();
        }

        // -------------------------------------------------------------------------------------
        // Keeping the guarantee: the per-frame half of the backdrop contract.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Re-decides which backdrop is on show and moves it there. Called every frame, because
        /// every input except the setting is something the renderer does over time: the mesh
        /// uploads on a later frame, a draw throws on a later frame, a device resets on a later
        /// frame.
        /// </summary>
        /// <remarks>
        /// Also where the setting is honoured while the map is up. Switching 3D to 2D drops the
        /// world on the next frame rather than at the next neighbourhood visit, and switching 2D
        /// to 3D builds one - which then goes through the same warm-up, so turning 3D on while
        /// the painted map is up never flashes an empty field.
        /// </remarks>
        /// <param name="state">This frame's update state, for its elapsed time.</param>
        private void RefreshBackdrop(UpdateState state)
        {
            var requested = CurrentNeighbourhoodView();
            //A world that is attached and a generation this panel is still waiting for are the
            //same answer for this rule: something is on its way, so there is nothing to attach or
            //detach. The second half of that is what stops the per-frame reconcile re-entering
            //AttachWorld while the worker runs, which - with a generation taking seconds - was one
            //"drawn in 2D" warning per frame and one lifecycle request per frame with it.
            switch (NeighbourhoodBackdropRule.Reconcile(requested, _world3D != null || IsRefreshing()))
            {
                case NeighbourhoodBackdropAction.Attach:
                    AttachWorld();
                    return;
                case NeighbourhoodBackdropAction.Detach:
                    DetachWorld();
                    ViewSelection = NeighbourhoodViewSelection.ForSetting(requested);
                    ApplyDecision(NeighbourhoodViewDecision.ForSetting(requested));
                    SimitoneLog.Info("Neighbourhood " + Mode + " is drawn in 2D: the 3D world was"
                        + " switched off (" + NeighbourhoodViewSetting.Describe(requested)
                        + "); the painted map is back.");
                    return;
            }

            //Nothing is attached and nothing is coming this frame - either a generation is still
            //running, or one finished without a raster. Both were answered above, by AttachWorld,
            //with the painted map up and the reason in the log; re-deciding here against a health
            //report of zeroes would only replace that answer with "generation failed", and do it
            //on every frame.
            if (_world3D == null && IsRefreshing()) return;

            if (_worldProxy != null && state?.Time != null)
            {
                _worldAttachedSeconds += state.Time.ElapsedGameTime.TotalSeconds;

                //A device reset takes the world's buffers with it, so the world owes the player a
                //fresh demonstration before it may hide the painted map again. The clock restarts
                //rather than the world being torn down, because the world is usually fine - a
                //minimise is the ordinary case - and rebuilding it would be seconds of work for
                //nothing.
                if (_worldProxy.WasReset)
                {
                    _worldProxy.AcknowledgeReset();
                    _worldAttachedSeconds = 0d;
                    SimitoneLog.Info("The graphics device was reset with the generated neighbourhood"
                        + " up, so the painted map goes back until the world draws again.");
                }
            }

            ApplyDecision(NeighbourhoodBackdropRule.Decide(requested, HealthFor(_world3D)));
        }

        // -------------------------------------------------------------------------------------
        // The painted map's own layers, and the click on the 3D world.
        //
        // §7.3 says the world is drawn "behind the existing buttons/switcher", and it is - but
        // behind is only the right relationship while the 2D lot sprites are the picture's own
        // labels. Once the world carries the picture they are not labels, they are the flat 2D
        // house cut-outs lying on top of the 3D houses, and the map reads as two neighbourhoods
        // drawn on top of each other rather than as one neighbourhood.
        //
        // So the same frame that empties the painted map also empties its lot buttons and its
        // overlays, and while they are gone they are not hit-testable either (UIElement.HitTestArea
        // refuses an invisible element, so visibility is also the click's off switch). What takes
        // their place as a target is the backdrop element's own mouse region, which the click
        // below turns into a world position.
        //
        // Note what "invisible" has to mean, because it is the whole of this section's fix:
        // UIElement says of its Visible field that it "must be implemented in the super class" and
        // that "UIElement does nothing with this variable" - UIContainer tests its *own* Visible
        // and then calls every child's Draw without asking. So an element is drawn exactly as far
        // as its own Draw pays attention to Visible, which is why UINeighborhoodHouseButton checks
        // it and, since the displaced-river report, so does UINeighborhoodAnimationLayer. Setting
        // the flag and not drawing the guard is not a slightly worse view: it is the flag having
        // no effect at all, and the water stays on the 3D world for ever.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Takes the lot buttons and the painted map's animation overlays away for as long as the
        /// generated world is carrying the picture, and puts them back - at the visibility the
        /// filters say, not at blanket visibility - the moment it stops.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called from <see cref="ApplyDecision"/>, so it follows the same per-frame decision that
        /// moves the backdrop: there is no second rule that can disagree about which of the two
        /// views is up, and a world that gives up mid-frame has its buttons back in that frame
        /// rather than the next one.
        /// </para>
        /// <para>
        /// <b>Put back by the filters, not by "visible".</b> A lot the current filter set hides has
        /// to stay hidden when the painted map returns, or a filter would appear to switch itself
        /// off every time the world drew. <see cref="ApplyLotVisibility"/> is the same answer
        /// <see cref="ApplyFilters"/> gives, which is the point of it being one method.
        /// </para>
        /// <para>
        /// The overlays are put back by <see cref="PaintedMapLayersVisible"/> rather than to
        /// <c>true</c> or to the frame's own answer, so that the two writers of these elements -
        /// this one and <see cref="ApplyFrame"/> - cannot end up in different states. The frame is
        /// not asked because it is not the authority: a view whose bitmap would not load is given
        /// <see cref="NeighbourhoodBackdropFrame.PaintedMap"/> anyway (there is nothing to shrink
        /// the element to), and reading the overlays off that would draw the map's water over the
        /// generated world with the map it belongs to missing.
        /// </para>
        /// <para>
        /// <b>Not gated on the capability flags, which is the change this rule records.</b> This
        /// method used to ask <see cref="WorldClickIsEnabled"/> as well, so with
        /// <see cref="NeighbourhoodWorldFlags"/> off - the default, on a build that has simply not
        /// turned the capabilities on - a world could still be promoted to 3D while the painted
        /// map's lot sprites and its river stayed drawn on top of it, because nothing in this method
        /// was allowed to take them away. The view setting is the player's instruction and defaults
        /// to 3D, so that screen was reachable and was reported. The gate stays where it belongs,
        /// on the click.
        /// </para>
        /// </remarks>
        private void SyncPaintedMapLayers()
        {
            var visible = PaintedMapLayersVisible(BackdropDecision);
            if (!visible)
            {
                //Not guarded on "already hidden", and neither is the branch below: the filter bar
                //and ApplyFilters both write these same flags, and a button or an overlay one of
                //them put back would draw over the world until the next decision. Re-asserting is
                //a few dozen field writes on a panel that is already walking both lists.
                _paintedMapLayersHidden = true;
                foreach (var button in _lotButtons) button.Visible = false;
                foreach (var overlay in _mapOverlays) overlay.Visible = false;
                return;
            }

            _paintedMapLayersHidden = false;
            //The filters are not re-applied on every frame of a view that is already showing the
            //painted map: that would allocate a dictionary of every lot on the map sixty times a
            //second to produce the answer they were last given. The overlays are written on every
            //call regardless, because they cost three field writes and they are the elements this
            //whole fix is about.
            if (_paintedMapLayersHidden) ApplyLotVisibility(LotPasses());
            foreach (var overlay in _mapOverlays) overlay.Visible = true;
        }

        /// <summary>
        /// A click on the map area, which is a click on the generated world while the world is
        /// carrying the picture: pick the lot under the mouse, and do exactly what a click on the
        /// painted map does when there is no lot there.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Mouse down, not mouse up.</b> The backdrop has always reset the zoom on
        /// <see cref="UIMouseEventType.MouseDown"/> and the lot buttons select on
        /// <see cref="UIMouseEventType.MouseUp"/>. This is the backdrop's click - same event, same
        /// place - and one event for both outcomes means the lot and the no-lot case cannot be half
        /// a frame apart, which is the failure where a click selects a lot and then resets the
        /// camera away from it.
        /// </para>
        /// <para>
        /// <b>Nothing is read off the 2D view to find the lot.</b> The click is in window
        /// coordinates and the world fills the window, so it goes to
        /// <see cref="TryWorldTileUnder"/> and then to the packed rectangles in the layout the
        /// panel already holds - the same rectangles the 3D houses are standing on, which is the
        /// only thing the 3D view can honestly be asked "what is under this point" about.
        /// </para>
        /// </remarks>
        /// <param name="state">This frame's input, for the mouse position.</param>
        private void OnBackdropMouseDown(UpdateState state)
        {
            if (!WorldClickIsEnabled(BackdropDecision))
            {
                //The painted map is what the player is looking at, so this is the 2D behaviour: a
                //click on a house sprite or a reset. One rule with the hide - see
                //WorldClickIsEnabled - so the buttons and this cannot disagree.
                ResetZoom();
                return;
            }

            var lot = TryLotUnderMouse(state);
            if (lot < 0)
            {
                //Connective ground: the same state a click on the painted map's empty ground
                //leaves, and the only way out of a zoomed-in lot, so it has to be here too.
                ResetZoom();
                return;
            }

            HITVM.Get().PlaySoundEvent(FSO.Client.UI.Model.UISounds.NeighborhoodClick);
            TakeControlOfLot(lot);
        }

        /// <summary>
        /// A lot on the generated world was clicked: zoom to it in place and take control of it,
        /// without the lot panel in between.
        ///
        /// <remarks>
        /// <para>
        /// <b>One click, per §8.1's fourth requirement.</b> The chain is
        /// <see cref="FocusOnLot"/> (which moves the camera and nothing else, because the lot panel
        /// is not opened) then <see cref="OnHouseSelect"/>, which is the handler
        /// <c>NeighborhoodService</c> already attaches: it confirms a move-in if a family is waiting,
        /// and otherwise calls <c>PlayHouse</c>. So the click reaches the lot through the same door a
        /// button press always used, and the lot panel is no longer a mandatory second step on the
        /// 3D map.
        /// </para>
        /// <para>
        /// <b>Why the lot panel is not opened here.</b> It is the management surface - bulldoze,
        /// evict, rezone, edit family, and the price of an empty lot - and the player still wants it.
        /// It is reached from the painted map's own house sprites and from the filter bar's Next and
        /// Previous buttons, both of which go through <see cref="SelectHouse(int)"/> and open it
        /// exactly as before. Only the click on the 3D world itself is collapsed, because that is the
        /// gesture §8.1 says must take control.
        /// </para>
        /// <para>
        /// <b>The camera moves first, on the same world.</b> Nothing here detaches or rebuilds the
        /// scene: <see cref="FocusOnLot"/> aims the generated world at the target and then tweens the
        /// painted camera onto the same numbers, and <see cref="UpdateWorldCamera"/> keeps it there on
        /// every frame of that tween. The lot is then loaded over that same map - the lot view's
        /// surround reads the very source this panel is drawing, through
        /// <see cref="NeighbourhoodSharedWorld"/> - so the player watches the camera travel to the
        /// house and then steps into it.
        /// </para>
        /// </remarks>
        /// <param name="lot">The lot number under the mouse.</param>
        private void TakeControlOfLot(int lot)
        {
            if (lot < 0) return;

            //Move-in is a confirmation, not a walk-in: the panel that comes up for a lot a family is
            //waiting to move into is where the price is, so that one click still opens it.
            if (AwaitingMoveIn)
            {
                SelectHouse(lot);
                return;
            }

            //The lot panel, if one is up, belongs to the lot the player is leaving. Killing it before
            //the camera starts moving is the same order FinishPopulate uses, so nothing slides off
            //screen underneath the tween.
            if (LastHS != null)
            {
                LastHS.Kill();
                LastHS = null;
            }

            FocusOnLot(lot);
            OnHouseSelect?.Invoke(lot);
        }

        /// <summary>
        /// Aims the camera at a lot without selecting it, and without opening anything over it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The camera move on its own, which is what both "clicking a lot takes control of it" and
        /// "coming back from a lot is a camera transition" need, and what
        /// <see cref="SelectHouse(int)"/> does before it opens the lot panel. Split out because those
        /// two callers want opposite halves of it: a click wants the move and then the lot, a return
        /// wants the move and nothing else.
        /// </para>
        /// <para>
        /// <b>The target is the ordinary house-focus target</b>, so the zoom and the offset are the
        /// same numbers a sprite click produces and the generated world is aimed at them on every
        /// frame of the tween - the map does not move anywhere, the camera moves over it. A lot this
        /// view has no position for falls back to the view's own centre rather than refusing, which
        /// is the same answer <see cref="SetCameraMode"/> gives.
        /// </para>
        /// </remarks>
        /// <param name="lot">The lot number to look at.</param>
        public void FocusOnLot(int lot)
        {
            if (!NeighCameraMachine.TryHouseFocus(lot, HousePositions, (ushort)Mode,
                new Vector2(CenterPositionX, CenterPositionY), out var target))
            {
                //No such lot on this map. Left where the camera is: the whole neighbourhood is the
                //right thing to be looking at when there is nothing to look at.
                return;
            }

            //The 3D world's focus follows these same numbers through UpdateWorldCamera, so the map and
            //the painted camera move together rather than the map catching up when the tween ends.
            _world3D?.Focus(target, HouseBitmapPositionOf(lot));
            GameFacade.Screens.Tween.To(this, NeighCameraMachine.CameraTweenDuration,
                target.TweenArgs(), TweenQuad.EaseOut);
        }

        /// <summary>
        /// Where a lot's sprite sits on the current bitmap, or <c>null</c> when this view has none -
        /// which is what <see cref="Neighbourhood3DView.Focus"/> wants for a lot the generated layout
        /// does not contain.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        private Vector2? HouseBitmapPositionOf(int lot)
        {
            if (HousePositions == null) return null;
            Vector2 found;
            return HousePositions.TryGetValue(lot, out found) ? found : (Vector2?)null;
        }

        /// <summary>
        /// Whether a family is waiting to move in, which is the one state in which clicking a lot is a
        /// question rather than a command. Read from the screen rather than carried here, because the
        /// screen owns it: it is set by the CAS hand-off and cleared by the purchase.
        /// </summary>
        private static bool AwaitingMoveIn
        {
            get { return (UIScreen.Current as TS1GameScreen)?.MoveInFamily != null; }
        }

        /// <summary>
        /// The lot whose packed rectangle the mouse is over on the generated world, or -1 for
        /// connective ground, a lot this view has taken off limits, or a world the click could not
        /// be resolved against.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The packed rectangle, not the projected one.</b> The lot the 3D view draws is the
        /// rectangle in <see cref="NeighbourhoodLayout.Lots"/>, which is where the packer put it
        /// after relaxing overlaps - and which is not in general where the 2D sprite is. The
        /// projection (<see cref="NeighbourhoodWorldMapping.BitmapToWorld"/>) is what lines the two
        /// views up, not what a click can be tested against, and a click tested against it would
        /// select the wrong lot exactly where the packer had to move one.
        /// </para>
        /// <para>
        /// <b>Only lots the player may pick.</b> The list walked is
        /// <see cref="_selectableLots"/> rather than the layout's own lots, because that is the
        /// panel's answer to "can this lot be opened": on this map, passing the filter set, and not
        /// one the view has taken off limits. A lot this view drew a sprite for but will not open a
        /// panel for must not become selectable by being clicked in 3D instead.
        /// </para>
        /// <para>
        /// <b>Overlapping rectangles are the map's own ambiguity.</b> The packer relaxes overlaps,
        /// so a point can be inside more than one; the nearest centre wins, and <c>_selectableLots</c>
        /// is walked in map order, so a tie goes to the lot the 2D view stacks on top - the same
        /// answer the player would have got by clicking the top sprite.
        /// </para>
        /// </remarks>
        /// <param name="state">This frame's input, for the mouse position.</param>
        /// <returns>A lot number, or -1.</returns>
        private int TryLotUnderMouse(UpdateState state)
        {
            //MouseState is a value in MonoGame, so a null UpdateState is the only thing to rule out
            //here - there is no "no mouse" state to ask it about.
            if (state == null) return -1;

            var layout = _sceneSource?.Layout;
            if (layout == null) return -1;

            Vector2 tile;
            if (!TryWorldTileUnder(state.MouseState.Position, out tile)) return -1;

            var best = -1;
            var bestDistance = float.MaxValue;
            foreach (var number in _selectableLots)
            {
                var lot = layout.FindLot(number);
                if (lot == null) continue;
                var size = Math.Max(0, lot.Size);
                var inside = tile.X >= lot.WorldX && tile.X < lot.WorldX + size
                    && tile.Y >= lot.WorldY && tile.Y < lot.WorldY + size;
                if (!inside) continue;

                //Nearest centre, for a point that is inside two rectangles the packer left
                //overlapping. Distance rather than a house size, so a small lot does not outrank a
                //large one for a click that is inside both.
                var distance = (NeighbourhoodWorldMapping.PackedCentre(lot) - tile).LengthSquared();
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = number;
            }
            return best;
        }

        /// <summary>
        /// The generated-map tile a screen point is over, by inverting the camera the world is
        /// actually drawn with and intersecting the ray with the ground plane.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The same matrix the frame was drawn with.</b> The placement's
        /// <see cref="NeighbourhoodCameraPlacement.ViewProjection"/> is the one the cull and the
        /// draw are built from, and the placement is kept current by
        /// <see cref="UpdateWorldCamera"/> - so the tween's intermediate frames, where the camera is
        /// moving and the buttons are moving with it, are all resolvable. A ray built from a
        /// separately-computed camera would be right only once the tween had finished.
        /// </para>
        /// <para>
        /// <b>The ground plane, not the terrain.</b> The generated map has hills on it, and reading
        /// a height back out of the raster for a click would be a second, noisier answer to a
        /// question the lot rectangles answer exactly: which plot is this. The plane through the
        /// camera's target height is the map's own mean ground, so the point the centre of the
        /// screen maps to is the point the camera is looking at, which is the one invariant worth
        /// being able to assert.
        /// </para>
        /// <para>
        /// False rather than a guess for every way this cannot be answered: no world, no device, no
        /// viewport, and a ray that never meets the ground. A click that cannot be resolved is a
        /// click on nothing, which the caller turns into the backdrop's own answer - and that is the
        /// right answer rather than a fallback, because "I could not tell which lot that was" and
        /// "there is no lot there" lead the player to the same place.
        /// </para>
        /// </remarks>
        /// <param name="screen">The mouse, in window coordinates.</param>
        /// <param name="tile">The tile it is over.</param>
        /// <returns>Whether the point could be resolved at all.</returns>
        private bool TryWorldTileUnder(Point screen, out Vector2 tile)
        {
            tile = Vector2.Zero;
            if (_world3D == null) return false;
            var device = GameFacade.GraphicsDevice;
            if (device == null) return false;

            //GraphicsDevice.Viewport is a struct, so the check is on the device and the viewport is
            //only read once it is known to be there.
            var viewport = device.Viewport;
            if (viewport.Width <= 0 || viewport.Height <= 0) return false;

            //Fully qualified because UIElement has a float[] member called Matrix, which the simple
            //name would bind to inside this class - and float[] has its own Invert extension.
            Microsoft.Xna.Framework.Matrix viewProjection;
            try
            {
                viewProjection = _world3D.Placement.ViewProjection(
                    1f, 4000f, viewport.AspectRatio);
            }
            catch (Exception ex)
            {
                //A placement from a device that has just been reset can carry values that make the
                //matrix unusable. A click is not worth taking the screen down for, and the
                //backdrop's own answer - back out to the whole view - is the right one to give.
                SimitoneLog.Warn("A click on the generated neighbourhood could not be turned into a"
                    + " world position, so it was treated as a click on empty ground: " + ex.Message, ex);
                return false;
            }

            //The static form, which is the one the rest of the engine uses; this MonoGame's
            //instance form is ref/ref. A singular matrix comes back as the identity rather than as
            //a failure, which is harmless here: the identity maps every screen point into the unit
            //cube, and no lot's packed rectangle is inside that.
            var inverse = Microsoft.Xna.Framework.Matrix.Invert(viewProjection);

            //Normalised device coordinates: the world fills the window, so the window is the whole
            //frame, and y runs the other way up from the mouse's.
            var ndcX = screen.X * 2f / viewport.Width - 1f;
            var ndcY = 1f - screen.Y * 2f / viewport.Height;
            var near = Unproject(inverse, ndcX, ndcY, 0f);
            var far = Unproject(inverse, ndcX, ndcY, 1f);
            var direction = far - near;

            //A ray that does not climb or fall never meets the ground, and dividing by this would
            //be an infinity rather than an answer.
            if (Math.Abs(direction.Y) < 1e-5f) return false;

            var groundHeight = _world3D.Placement.Target.Y;
            var distance = (groundHeight - near.Y) / direction.Y;
            //Behind the camera: the point is on the plane, but not on anything the player can see.
            if (distance < 0f) return false;

            var hit = near + direction * distance;
            tile = new Vector2(hit.X, hit.Z);
            return true;
        }

        /// <summary>
        /// One point of a viewport, back in world space.
        /// </summary>
        /// <remarks>
        /// The homogeneous divide is the whole of the method and is not optional: a perspective
        /// projection puts w at something other than one everywhere except the centre of the
        /// screen, so dropping it gives a point that is in the right direction and the wrong
        /// distance - and a click resolved by a wrong distance is a click on the wrong lot.
        /// </remarks>
        /// <param name="inverse">The inverted view-projection.</param>
        /// <param name="x">Normalised device x, -1 to 1.</param>
        /// <param name="y">Normalised device y, -1 to 1.</param>
        /// <param name="z">Normalised device depth: 0 at the near plane, 1 at the far one.</param>
        private static Vector3 Unproject(
            Microsoft.Xna.Framework.Matrix inverse, float x, float y, float z)
        {
            var point = Vector4.Transform(new Vector4(x, y, z, 1f), inverse);
            var w = Math.Abs(point.W) < 1e-5f ? 1f : point.W;
            return new Vector3(point.X / w, point.Y / w, point.Z / w);
        }

        /// <summary>
        /// What is known about the generated world right now, as the one value the pure rule is
        /// handed.
        ///
        /// <para>
        /// <b>The static half comes from the raster, not from the device.</b> Asking the mesh probe
        /// what a generated map <em>should</em> produce is what lets a degenerate world be caught
        /// at attach time: a map with no ground quads, or none for the roads, can never draw a
        /// neighbourhood whatever the GPU does, so there is no reason to make the player watch it
        /// try. The live half comes from the proxy, which owns the only two facts nobody else can
        /// see - whether a draw threw, and whether the device went away.
        /// </para>
        /// </summary>
        /// <param name="view">The attached world, or <c>null</c>.</param>
        /// <param name="failure">Why there is no world, when there is not one.</param>
        /// <param name="source">
        /// The source to count geometry from, when a world was not attached. Defaults to the one
        /// this panel is holding.
        /// </param>
        private NeighbourhoodWorldHealth HealthFor(
            Neighbourhood3DView view, string failure = null)
        {
            //The static counts were taken once, by ProbeGeometry, when the source was built. They
            //describe a raster, which does not change while a view is up, so re-walking 512x512 of
            //it every frame to answer a question with the same answer would be a quarter of a
            //million iterations per frame spent on nothing.
            var lots = _probeLots;
            var ground = _probeGroundQuads;
            var roads = _probeRoadQuads;

            if (view == null)
            {
                return new NeighbourhoodWorldHealth(false, false, 0, NeighbourhoodWorldHealth.Unknown,
                    ground, roads, lots, 0, 0, false, 0d, failure);
            }

            //A scene that publishes its own numbers is preferred over the ones derived here.
            //Neighbourhood3DView implements INeighbourhoodRenderSignal, so this is the scene's own
            //answer rather than an inference - and the two that matter are the ones an inference
            //got wrong. "Uploaded" is now the mesh's own statement rather than "the device holds a
            //triangle", and the promotion gate reads what the last frame actually submitted. A
            //world that holds 315424 triangles and submits none of them is a blank screen, and the
            //derived version of that was a world that had "demonstrably produced something".
            var signal = (view as object) as INeighbourhoodRenderSignal;
            var triangles = signal != null ? signal.TerrainTriangles : SafeTriangles(view);
            var uploaded = signal != null ? signal.MeshUploaded : triangles > 0;
            var facades = signal != null ? signal.FacadeCount : SafeFacades(view);
            //A scene's own count and the proxy's count are the same event seen from either side,
            //so they are merged rather than one of them being picked. Reading only the scene's - as
            //this did - made the proxy's counter dead: the proxy caught a draw that threw, raised
            //its count, and the rule never saw it, so a world that threw twice in a row was never
            //put back. See NeighbourhoodWorldHealth.MergeDrawFailures.
            var drawFailures = NeighbourhoodWorldHealth.MergeDrawFailures(
                signal != null ? signal.DrawFailures : 0,
                _worldProxy?.DrawFailures ?? 0);
            var deviceLost = NeighbourhoodWorldHealth.MergeDeviceLost(
                signal != null && signal.DeviceLost,
                _worldProxy?.WasReset ?? false);

            //Submitted geometry is the gate, and a signal that does not report it leaves the count
            //at Unknown, which the rule reads as "nobody is counting" and falls back to the mesh's
            //triangle count. Deliberate: a signal that cannot see the submission is not a reason to
            //fall back to the painted map, because the signal may just not have been implemented.
            var submitted = signal != null
                ? signal.SubmittedTriangles : NeighbourhoodWorldHealth.Unknown;
            var proven = signal != null
                ? signal.SubmittedFrames : NeighbourhoodWorldHealth.Unknown;

            //The houses, off the scene's own frame report. This is the half the field report was
            //missing: terrain drew 60660 triangles on three consecutive frames and the world was
            //promoted over the painted map with the lot facades still streaming in, so the player
            //got a bare plain. FacadeTriangles says whether there is any house geometry on the
            //screen this frame, and FacadeEligible is the number the build fraction is taken
            //against - both straight off the scene, so they are what the device actually did rather
            //than anything inferred here.
            //
            //Unknown rather than zero when the scene does not report them, and the same reasoning as
            //the submission counts above: a scene that cannot see its own facades is not a scene
            //with no houses on them, and reading it as one would pin the painted map up for ever.
            var facadeTriangles = NeighbourhoodWorldHealth.Unknown;
            var facadesVisible = NeighbourhoodWorldHealth.Unknown;
            var frame = view.LastFrame;
            if (frame != null)
            {
                facadeTriangles = frame.FacadeTriangles;
                facadesVisible = frame.FacadesVisible;
            }

            return new NeighbourhoodWorldHealth(
                attached: true,
                meshUploaded: uploaded,
                terrainTriangles: triangles,
                roadTriangles: signal != null ? signal.RoadTriangles : NeighbourhoodWorldHealth.Unknown,
                groundQuads: ground,
                roadQuads: roads,
                lotCount: lots,
                facadeCount: facades,
                drawFailures: drawFailures,
                deviceLost: deviceLost,
                secondsSinceAttach: _worldAttachedSeconds,
                //The proxy's message is the one that named the exception a caught draw threw, so it
                //is preferred over the scene's construction failure when both exist.
                failure: NeighbourhoodWorldHealth.MergeDrawFailure(
                    signal?.LastDrawFailure, _worldProxy?.LastDrawFailure, failure),
                submittedTriangles: submitted,
                submittedFrames: proven,
                facadeTriangles: facadeTriangles,
                facadesVisible: facadesVisible,
                //How many lots could have a house at all, from the layout the scene is drawing.
                //Zero is a real answer and waives the gate: a neighbourhood of empty plots has no
                //house it can be failing to draw, and the terrain pass is the whole of the picture.
                facadeEligible: SafeFacadeEligible(view));
        }

        /// <summary>
        /// How many lots a complete facade pass would build on this map, or
        /// <see cref="NeighbourhoodWorldHealth.Unknown"/> if the scene will not say.
        /// </summary>
        /// <param name="view">The attached world.</param>
        private static int SafeFacadeEligible(Neighbourhood3DView view)
        {
            //It walks the layout's lots, so it is not free - but a neighbourhood is tens of lots
            //and this runs once a frame, and reading it wrong (as an exception) would silently
            //disable the house gate, which is the bug this whole change is for.
            try { return view.FacadeEligible; }
            catch (Exception) { return NeighbourhoodWorldHealth.Unknown; }
        }

        /// <summary>
        /// Counts what the generated raster ought to produce, once per attach.
        /// </summary>
        /// <remarks>
        /// <b>This is what lets a degenerate world be caught before the GPU is touched.</b> A map
        /// with no ground quads, or none for the roads on a neighbourhood that has lots in it, can
        /// never draw a neighbourhood whatever the device does - so there is no reason to build a
        /// scene for it, and every reason not to make the player watch it try.
        /// </remarks>
        /// <param name="source">The source that was generated.</param>
        private void ProbeGeometry(NeighbourhoodSceneSource source)
        {
            _probeGroundQuads = NeighbourhoodWorldHealth.Unknown;
            _probeRoadQuads = NeighbourhoodWorldHealth.Unknown;
            _probeLots = source?.Layout?.LotCount ?? 0;
            if (source?.Map == null) return;

            try
            {
                var probe = NeighbourhoodCityMeshProbe.Probe(source.Map);
                _probeGroundQuads = probe.TotalLayerQuads;
                _probeRoadQuads = probe.RoadQuads + probe.CornerQuads;
                SimitoneLog.Info("Neighbourhood " + Mode + ": the generated raster should produce "
                    + _probeGroundQuads + " ground quad(s) and " + _probeRoadQuads + " road quad(s)"
                    + " over " + _probeLots + " lot(s).");
            }
            catch (Exception ex)
            {
                //The probe is a diagnostic that happens to be decisive. When it will not run, the
                //honest answer is "unknown" rather than "empty": an unknown count skips the static
                //rules and leaves the decision to the live ones, which is the safe direction,
                //because a world that has demonstrably drawn is still promoted.
                SimitoneLog.Warn("The generated neighbourhood's geometry could not be counted, so"
                    + " it is being judged only on what it actually draws: " + ex.Message, ex);
            }
        }

        private static int SafeTriangles(Neighbourhood3DView view)
        {
            try { return view.TerrainPrimitives; }
            catch (Exception) { return 0; }
        }

        private static int SafeFacades(Neighbourhood3DView view)
        {
            try { return view.FacadeCount; }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// Moves the backdrop to match a decision, and says so when the decision has actually
        /// changed.
        /// </summary>
        /// <remarks>
        /// The swap is a hard cut rather than a fade, deliberately. The painted map lives in the UI
        /// layer and the world in the graphics layer drawn in front of it, so a fade would run a
        /// cross-dissolve between two neighbourhoods - the exact "distorted look" this guarantee
        /// exists to prevent - rather than easing one out and the other in. A hard cut between a
        /// background and the thing standing in for it is also very nearly invisible at this size,
        /// because the two agree on where every lot is.
        /// </remarks>
        /// <param name="decision">What should be on show.</param>
        private void ApplyDecision(NeighbourhoodViewDecision decision)
        {
            var previous = BackdropDecision;
            BackdropDecision = decision;

            ApplyFrame(NeighbourhoodBackdropFrame.For(decision, _backdropTexture != null));
            SyncPaintedMapLayers();

            if (previous.Equals(decision)) return;

            //Two lines for two very different events, because they mean opposite things to a
            //player reading the log: one is the neighbourhood changing how it is drawn, the other
            //is the generated world giving up.
            if (decision.FellBack && !previous.FellBack)
            {
                SimitoneLog.Warn("Neighbourhood " + Mode + " is drawn in 2D: " + decision.Fault
                    + ": " + decision.Reason);
            }
            else
            {
                SimitoneLog.Info("Neighbourhood " + Mode + " is drawn in "
                    + (decision.Show3D ? "3D"
                        : decision.ShowBoth ? "3D (warming up over the painted map)"
                        : "2D")
                    + ": " + decision.Reason);
            }
        }

        /// <summary>
        /// Puts the backdrop element into <paramref name="frame"/>, and does nothing at all when it
        /// is already there.
        /// </summary>
        /// <param name="frame">What the element should be showing.</param>
        private void ApplyFrame(NeighbourhoodBackdropFrame frame)
        {
            //The overlays follow the decision, not the frame. They used to follow the frame, and a
            //view whose bitmap would not load is given PaintedMap as its frame whatever the world
            //is doing - there is nothing to shrink the element to - which drew the painted map's
            //river over the generated world with the map it belongs to missing. One rule for both
            //writers, and the decision is the one that knows whether there is a map on screen.
            foreach (var overlay in _mapOverlays) overlay.Visible = PaintedMapLayersVisible(BackdropDecision);
            if (_backdrop == null) return;
            if (!_backdropFrame.DiffersFrom(frame)) return;
            _backdropFrame = frame;

            if (frame.IsPaintedMap)
            {
                if (_backdropTexture == null) return;
                _backdrop.Texture = _backdropTexture;
                //The size goes back with the texture. UIImage only takes its size from a texture
                //while the size is still zero, so restoring the texture without restoring the size
                //would leave the map one pixel wide - which is the same missing-background report
                //from the other end.
                _backdrop.SetSize(_backdropSize.X, _backdropSize.Y);
                return;
            }

            if (_backdropTexture == null || _transparentBackdrop == null) return;
            _backdrop.Texture = _transparentBackdrop;
            _backdrop.SetSize(1, 1);
        }

        /// <summary>
        /// Brings this mode's generated world up to date through <see cref="NeighbourhoodLifecycle"/>
        /// and hands back the scene source - or the reason, in one sentence, that there is not one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this asks the lifecycle rather than generating.</b> It used to read the layout
        /// sidecar, pack it if it was missing, and then run
        /// <see cref="NeighbourhoodMapGenerator.Generate"/> over it, every single time the panel
        /// attached a world - which is once per neighbourhood visit and once per invalidation. The
        /// 512x512 raster is 262 144 tiles of pure arithmetic derived from (layout, seed), so on
        /// the overwhelmingly common visit - the player came back from a lot they did not edit -
        /// that was a quarter of a million tiles of work to produce the bytes already sitting in
        /// <see cref="NeighbourhoodWorldCache"/>. <see cref="NeighbourhoodLifecycle.RefreshNow"/>
        /// is the one place that knows how to tell the two cases apart, so asking it is the whole
        /// of the change; the decision it makes is not restated here.
        /// </para>
        /// <para>
        /// <b>What did not change.</b> This is still a synchronous call on the game thread, because
        /// <see cref="AttachWorld"/> has to have a <see cref="NeighbourhoodSceneSource"/> before it
        /// can build a <see cref="Neighbourhood3DView"/> at all - and on a cache hit the
        /// synchronous work is one small sidecar read. It is still wrapped in the same catch-all,
        /// and a world that is not there is still a <see cref="NeighbourhoodSceneSource"/>
        /// carrying the reason, which <see cref="AttachWorld"/> turns into the 2D bitmap. The
        /// panel still owns the <see cref="Neighbourhood3DView"/> - through the
        /// <see cref="NeighbourhoodWorldProxy"/> it adds to the scene layer - and still tears it
        /// down itself on <see cref="Removed"/> and on <see cref="DetachWorld"/>; the shared cache
        /// is not the panel's to drop, and it does not drop it. What invalidates that cache is
        /// <c>NeighborhoodService</c>, which drops it and then calls
        /// <see cref="InvalidateGeneratedWorld"/> - so a lot that changed is repaired by the same
        /// per-lot rule M5 defines, rather than by this panel re-packing the whole neighbourhood.
        /// </para>
        /// </remarks>
        /// <param name="mode">The neighbourhood view being shown, 1..8.</param>
        /// <param name="failure">Why generation did not produce a map.</param>
        private static NeighbourhoodSceneSource BuildSceneSource(ushort mode, out string failure)
            => BuildSceneSource((int)mode, out failure);

        private static NeighbourhoodSceneSource BuildSceneSource(int mode, out string failure)
        {
            failure = null;
            try
            {
                //§0 decision 4: one generated world per mode, matching the per-mode 2D maps. The
                //mode is therefore also the sidecar slot - and so the cache slot - so switching
                //neighbourhoods switches worlds and no two maps share a cache entry.
                //
                //A fresh source per call, deliberately. TS1LotContentSource caches the lot records
                //it has read in a Dictionary, and this is the game thread; handing the same one to
                //the background refresh in NeighborhoodService would be sharing that Dictionary
                //across two threads. The reads are cheap against the alternative and, on the path
                //that matters - a cache hit - the panel does not pack or rasterise anything.
                var world = RefreshCachedWorld(FSOEnvironment.UserDir, mode, mode,
                    NeighbourhoodLifecycle.RequestedView,
                    new TS1LotContentSource(Content.Get().Neighborhood, mode));

                return SceneSourceFor(world, out failure);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                SimitoneLog.Error("The generated world for neighbourhood " + mode
                    + " could not be built. The neighbourhood view is unaffected; see the log for the reason.", ex);
                return new NeighbourhoodSceneSource(null, failure);
            }
        }

        /// <summary>
        /// The panel's whole route to a generated world: ask <see cref="NeighbourhoodLifecycle"/> to
        /// bring one up to date, and record it in <see cref="NeighbourhoodWorldCache"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The store is the half that is easy to leave out, and leaving it out makes the whole swap
        /// worth nothing. <see cref="NeighbourhoodLifecycle.RefreshNow"/> decides and does the work
        /// but does not store, because the cache is the <em>caller's</em> to fill: the other caller,
        /// <c>NeighborhoodService</c>, fills it through <see cref="NeighbourhoodRefreshJob"/>, whose
        /// last step is the store. This panel runs its refresh synchronously, so this method is that
        /// step - and without it the second attach would find an empty cache and regenerate the
        /// 512x512 raster anyway, which is the exact cost the swap exists to remove.
        /// </para>
        /// <para>
        /// <b>Only a world with a raster is stored.</b> A refresh that came back empty is left alone
        /// rather than pushed in, because the cache evicts its slot for a world with no map, and
        /// dropping a shared entry is <c>NeighborhoodService</c>'s job - it is the one that knows the
        /// lot number and the reason. The panel stores worlds and never drops them. A stale entry
        /// cannot be served in place of a repair either: <see cref="NeighbourhoodRefreshPlan"/>
        /// only treats the cached raster as current when the cached layout is field-for-field the
        /// one on disk, and a repair that has just been written makes those differ.
        /// </para>
        /// <para>
        /// Public and static so the cost claim is testable. Everything here is a value rule over a
        /// temp directory and an <see cref="ILotContentSource"/>, so a test can drive the panel's
        /// real route - twice, with a fake neighbourhood - and assert that the second one reused
        /// the first one's raster instead of rebuilding it. What is <em>not</em> here is the
        /// graphics device, the provider and the screen, which is why the panel's own constructor
        /// still cannot be stood up in a test.
        /// </para>
        /// </remarks>
        /// <param name="userDir">The user directory the layout sidecar lives in.</param>
        /// <param name="slot">The neighbourhood slot, which is also the sidecar's and the cache's.</param>
        /// <param name="mode">The neighbourhood view, 1..8.</param>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="source">Where the neighbourhood's lots are read from.</param>
        /// <returns>What the refresh produced, or a world describing why it produced nothing.</returns>
        public static NeighbourhoodGeneratedWorld RefreshCachedWorld(
            string userDir, int slot, int mode, NeighbourhoodView requested,
            ILotContentSource source)
        {
            //the seed is not a parameter: RefreshNow derives it from the slot, which is what keeps
            //the panel, the in-lot surroundings and the sidecar describing the same world
            var world = NeighbourhoodLifecycle.RefreshNow(userDir, slot, mode, requested, source);

            if (world == null || !world.HasWorld) return world;

            //What was in the cache before this call. Read before the store, so it is the answer to
            //"did this refresh have anything to reuse" rather than to "is there a world now".
            var already = NeighbourhoodWorldCache.Peek(slot);

            NeighbourhoodWorldCache.Store(world);

            //RefreshNow logs a rebuild of its own accord, so this is deliberately only the case it
            //stays quiet about: a refresh that reused what was already there. Without it, "the
            //panel did not regenerate" and "the panel regenerated and the log was truncated" look
            //identical from the outside, which is the one distinction a player reporting a stutter
            //on the neighbourhood screen needs.
            //
            //Identity, not world.Plan.IsCacheHit: a world's plan is the plan that *built* it, so a
            //world handed back out of the cache still carries the plan from the visit that made it -
            //a first visit's NoSidecar plan, say - and asking that plan whether it was a hit gets
            //the wrong answer by construction. Reference identity against the entry that was there
            //before the call is the observation that cannot be wrong: RefreshNow returns the cached
            //instance itself on a hit and a freshly built one otherwise.
            if (ReferenceEquals(world, already))
            {
                SimitoneLog.Info("Neighbourhood " + slot + " reused the generated world that was"
                    + " already in memory rather than generating 512x512 tiles of a new one ("
                    + world.Map.Width + "x" + world.Map.Height + ", "
                    + (world.Layout?.LotCount ?? 0) + " lots; " + NeighbourhoodWorldCache.Stats.Describe() + ").");
            }

            return world;
        }

        /// <summary>
        /// Turns whatever <see cref="NeighbourhoodLifecycle.RefreshNow"/> handed back into the scene
        /// source this panel draws from, and the reason there is not one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Split out of <see cref="BuildSceneSource(int, out string)"/> so the part with behaviour
        /// in it is a pure rule over a value. Everything upstream of it needs a live content
        /// provider and a user directory; this needs neither, so "a refresh that came back empty",
        /// "a refresh that failed with a reason" and "a refresh that produced a world" are all
        /// assertions rather than observations, and the 2D fallback's contract is testable without
        /// standing up a graphics device.
        /// </para>
        /// <para>
        /// <b>A null world is treated as a failure, not as success.</b> <see cref="RefreshNow"/>
        /// never returns null today, and a refresh that threw is a refresh that did not happen -
        /// so a null here is a broken invariant and the safe reading of it is the bitmap with a
        /// reason, which is what this method does.
        /// </para>
        /// <para>
        /// <b>Every view builds its own source.</b> The connected-mechanics rework routed this
        /// through <c>NeighbourhoodSharedWorld</c> so the map view and the lot view held one raster
        /// object; the revert removes that sharing, because the two modes must be independent. Each
        /// view gets its own clone of the map, which is what the game did before the rework and what
        /// keeps a lot load from reaching into the neighbourhood's world.
        /// </para>
        /// </remarks>
        /// <param name="world">What the lifecycle returned, or <c>null</c>.</param>
        /// <param name="failure">
        /// Why there is no map to draw, or <c>null</c> when <paramref name="world"/> is usable.
        /// </param>
        public static NeighbourhoodSceneSource SceneSourceFor(
            NeighbourhoodGeneratedWorld world, out string failure)
        {
            if (world == null)
            {
                failure = "the generated world refresh produced nothing";
                return new NeighbourhoodSceneSource(null, failure);
            }
            if (!world.HasWorld)
            {
                //The specific reason first - the road graph, the generation input, the allocation -
                //because that is the one worth chasing; the plan's own selection reason is the
                //fallback for a world that failed without saying why.
                failure = string.IsNullOrEmpty(world.Failure)
                    ? world.Plan.Selection.Reason
                    : world.Failure;
                return new NeighbourhoodSceneSource(world.Layout, failure);
            }
            failure = null;
            return new NeighbourhoodSceneSource(world.Layout, world.Map);
        }

        /// <summary>
        /// The view the player has chosen, from the graphics preferences snapshot. Reading it
        /// through <see cref="GraphicsPreferences"/> rather than straight off the setting keeps the
        /// "one place that maps what was asked for onto what the renderer is told" rule the rest
        /// of the options follow.
        /// </summary>
        private static NeighbourhoodView CurrentNeighbourhoodView()
        {
            try
            {
                return GraphicsPreferences.From(GlobalSettings.Default).NeighbourhoodView;
            }
            catch (Exception)
            {
                //a settings object that will not read is the same as "never configured", which is 2D
                return NeighbourhoodViewSetting.Default;
            }
        }

        private Texture2D MakeTransparentBackdrop()
        {
            try
            {
                var tex = new Texture2D(GameFacade.GraphicsDevice, 1, 1);
                tex.SetData(new[] { new Color(0, 0, 0, 0) });
                return tex;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // -------------------------------------------------------------------------------------
        // The painted map: which view's artwork to draw when the install may not have all of it.
        //
        // The 2D path is the guarantee everything else leans on, so it has to survive an install
        // that is missing one of the seven backgrounds. A missing bitmap is not a missing
        // neighbourhood: the lot table, the lot numbers, the travel buttons and the camera all
        // come from the view that was asked for, and only the picture is borrowed. Losing a whole
        // view - and with it every lot on it - over a missing file would be a far worse answer
        // than drawing the wrong background, and a blank field would be worse still.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// The background file name a view draws from, or <c>null</c> when the slot has none.
        /// </summary>
        /// <param name="view">The 1-based neighbourhood view.</param>
        public static string BitmapOf(ushort view)
        {
            if (view < 1 || view > Neighborhoods.Length) return null;
            return Neighborhoods[view - 1]?.Graphic;
        }

        /// <summary>
        /// Which view's background to draw: the one that was asked for when this install has it,
        /// otherwise the first that it does, otherwise none.
        /// </summary>
        /// <param name="mode">The neighbourhood view being shown, 1..8.</param>
        /// <param name="available">
        /// Whether a view's background file is in this install. A delegate rather than a set of
        /// names so the rule can be tested against any install at all, and so the panel answers
        /// "is it there" rather than "is the name in the table".
        /// </param>
        /// <param name="reason">
        /// Why the answer is not <paramref name="mode"/>, naming every file that was looked for.
        /// Null when the answer is <paramref name="mode"/>.
        /// </param>
        /// <returns>The view to draw, or 0 when this install has no background at all.</returns>
        /// <remarks>
        /// <para>
        /// The order is the view that was asked for first, then the table from the top. That
        /// makes the fallback the base residential map rather than whichever view happens to be
        /// later in the array, which is what makes it predictable: a player with an incomplete
        /// install gets the same picture on every neighbourhood rather than a different one each
        /// time they travel.
        /// </para>
        /// <para>
        /// A slot with no <c>Graphic</c> - the deliberately empty slot 6 - is never a candidate,
        /// and neither is one outside the table, because "no background" is not "a background
        /// that happens to be missing".
        /// </para>
        /// </remarks>
        public static ushort ResolveBitmapMode(ushort mode, Func<ushort, bool> available, out string reason)
        {
            if (available == null)
            {
                //No way to ask: the answer is the view that was asked for, and the reason says the
                //check could not be made rather than pretending it was made and passed.
                reason = "this install's contents could not be checked, so the view's own background was assumed";
                return mode;
            }

            if (HasBackground(mode) && available(mode))
            {
                reason = null;
                return mode;
            }

            var wanted = BitmapOf(mode);
            var declared = HasBackground(mode);
            var wantedName = declared ? "'" + wanted + "'" : "nothing (view " + mode + " declares no background)";
            var tried = new System.Text.StringBuilder();
            AppendTried(tried, mode, wanted, declared);

            for (var candidate = 1; candidate <= Neighborhoods.Length; candidate++)
            {
                var view = (ushort)candidate;
                if (view == mode || !HasBackground(view) || !available(view)) continue;
                reason = "this install has no " + wantedName + " for neighbourhood view " + mode
                    + ", so view " + view + "'s '" + BitmapOf(view) + "' is drawn instead";
                return view;
            }

            reason = "this install has no background for neighbourhood view " + mode + " ("
                + tried + ") or for any other view, so there is no painted map to fall back to";
            return 0;
        }

        private static void AppendTried(System.Text.StringBuilder text, ushort view, string file, bool declared)
        {
            if (text.Length > 0) text.Append(", ");
            text.Append(declared ? "'" + file + "'" : "view " + view + " (no background declared)");
        }

        /// <summary>Whether a view declares a background file at all.</summary>
        /// <param name="view">The 1-based neighbourhood view.</param>
        public static bool HasBackground(ushort view)
        {
            if (view < 1 || view > Neighborhoods.Length) return false;
            return !string.IsNullOrEmpty(Neighborhoods[view - 1]?.Graphic);
        }

        /// <summary>
        /// Whether this install actually carries a view's background, without decoding it: a
        /// provider miss is the case worth catching early, and it costs no graphics memory to
        /// rule in or out.
        /// </summary>
        /// <param name="view">The 1-based neighbourhood view.</param>
        private bool ViewIsInstalled(ushort view)
        {
            var file = BitmapOf(view);
            if (string.IsNullOrEmpty(file)) return false;
            try
            {
                return Provider != null && Provider.Get(file) is ITextureRef;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("Could not check whether this install has '" + file
                    + "', the background for neighbourhood view " + view + ": " + ex.Message, ex);
                return false;
            }
        }

        /// <summary>
        /// Loads a view's background, naming the file when it will not load.
        ///
        /// <para>
        /// A provider hit is not a loaded texture: the file can be in the archive and still fail
        /// to decode, and the old code's unchecked <c>.Get(device)</c> turned that into a
        /// <c>NullReferenceException</c> in the middle of building the neighbourhood screen. Now
        /// it is a warning that names the file and a background that stays as the frame's clear
        /// colour, which is a bad picture rather than no picture at all.
        /// </para>
        /// </summary>
        /// <param name="view">The 1-based neighbourhood view, or 0 for none.</param>
        private Texture2D LoadBackdropTexture(ushort view)
        {
            var file = BitmapOf(view);
            if (string.IsNullOrEmpty(file)) return null;
            try
            {
                var reference = Provider?.Get(file) as ITextureRef;
                return reference?.Get(GameFacade.GraphicsDevice);
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("The background for neighbourhood view " + view + ", '"
                    + file + "', could not be loaded. The neighbourhood will show lot buttons over"
                    + " an empty field; see the log for the reason.", ex);
                return null;
            }
        }

        /// <summary>
        /// The 9-slice frame around the neighbourhood map. Guarded, because a client that is
        /// missing <c>ngbh_outline.png</c> should still show the map rather than throw while
        /// building the screen.
        /// </summary>
        private void AddOutline()
        {
            var outline = LoadCustomTexture("ngbh_outline.png");
            if (outline == null) return;
            var shad = new UIImage(outline).With9Slice(24, 24, 24, 24);
            shad.SetSize(800 + 48, 600 + 48);
            shad.Position = new Vector2(-24);
            Add(shad);
        }

        private Texture2D LoadCustomTexture(string name)
        {
            try
            {
                var reference = Content.Get()?.CustomUI?.Get(name);
                return reference?.Get(GameFacade.GraphicsDevice);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("The neighbourhood screen's '" + name + "' could not be loaded:"
                    + " " + ex.Message + " The neighbourhood itself is unaffected.", ex);
                return null;
            }
        }

        // -------------------------------------------------------------------------------------
        // 3D neighbourhood OBJ export.
        //
        // SimitoneNeighOBJExporter.SaveOBJ was called unconditionally here and was commented out
        // rather than deleted, because it works but it is not free and not something to do behind
        // a player's back: it stands a whole 3D world and a VM up per lot in turn, and writes a
        // .obj plus a .mtl (and a PNG per floor material) into the user's documents folder. On a
        // 50-lot neighbourhood that is minutes of the game thread and tens of megabytes, once
        // per time the neighbourhood view is built.
        //
        // It is therefore opt-in through a debug flag rather than a settings row: the exporter is
        // an authoring tool (its own header says it exists so people can mesh neighbourhood
        // surroundings), it has no in-game use, and a checkbox on a shipping Options page would
        // be advertising it as a feature. Set the environment variable to 1 and the export runs
        // again for every map that is opened; leave it unset (the normal case) and the code path
        // costs one dictionary-free string compare.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Environment variable that turns the 3D neighbourhood export on. Deliberately an
        /// environment variable and not a <c>config.ini</c> key: this is a developer switch for a
        /// tool that is never part of a play session, and it must not be discoverable by, or
        /// persisted for, a player who has no use for it.
        /// </summary>
        public const string ExportEnvironmentVariable = "SIMITONE_EXPORT_NEIGHBOURHOOD_OBJ";

        /// <summary>
        /// Whether <paramref name="value"/> - the raw environment variable - asks for the
        /// export. Split out from the environment read so the rule is testable headlessly, and
        /// so there is exactly one answer to "what counts as switched on".
        /// </summary>
        /// <remarks>
        /// Only an exact "1" (after trimming) counts. The alternatives a boolean-ish environment
        /// variable usually invites are all ways to write a value nobody chose deliberately -
        /// an empty variable, a bare "true", a typo - and a typo that silently started writing
        /// hundreds of megabytes into the player's documents folder is the worst possible
        /// behaviour for this switch.
        /// </remarks>
        public static bool IsObjExportRequested(string value)
        {
            return string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
        }

        /// <summary>
        /// The folder one neighbourhood's export is written to, under the game's user directory.
        /// Kept as arithmetic rather than inline so the answer can be asserted, and so the log
        /// line and the file the exporter creates cannot disagree about where it went.
        /// </summary>
        /// <param name="userDir">
        /// The game's user directory (<c>FSOEnvironment.UserDir</c>).
        /// </param>
        /// <param name="mode">The neighbourhood mode being exported, 1..8.</param>
        public static string ObjExportPath(string userDir, int mode)
        {
            var dir = string.IsNullOrEmpty(userDir) ? string.Empty : userDir;
            return Path.Combine(dir, "NeighModel" + mode);
        }

        /// <summary>
        /// Exports this neighbourhood to a .obj, if the debug flag is set, and says where it went.
        /// </summary>
        /// <remarks>
        /// Runs before <see cref="FinishPopulate"/> on purpose - the call is a long block on the
        /// game thread, and doing it while the outgoing map's lot panel is still hidden means the
        /// wait happens over a settled screen rather than one whose panel is about to be removed.
        /// Every failure is swallowed and logged: an export that cannot complete is a missing
        /// authoring artefact, never a reason to fail the neighbourhood view.
        /// </remarks>
        /// <param name="locations">The map's location string table, as the exporter takes it.</param>
        private void MaybeExportNeighbourhood3D(STR locations)
        {
            if (locations == null) return;

            string requested;
            try
            {
                requested = Environment.GetEnvironmentVariable(ExportEnvironmentVariable);
            }
            catch (Exception ex)
            {
                // A locked-down host can refuse the read outright. That is not a reason to fail
                // the map; it just means the switch is off.
                SimitoneLog.Warn("Could not read " + ExportEnvironmentVariable
                    + ", so the 3D neighbourhood export is off for this map.", ex);
                return;
            }

            if (!IsObjExportRequested(requested)) return;

            var destPath = ObjExportPath(FSOEnvironment.UserDir, Mode);
            try
            {
                SimitoneLog.Info("Exporting neighbourhood " + Mode + " to " + destPath
                    + " (" + ExportEnvironmentVariable + " is set). This is slow and writes a .obj,"
                    + " a .mtl and one PNG per floor material.");
                SimitoneNeighOBJExporter.SaveOBJ(destPath, locations);
                SimitoneLog.Info("Finished the 3D export of neighbourhood " + Mode + "; the files are in " + destPath + ".");
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("The 3D export of neighbourhood " + Mode + " to " + destPath
                    + " did not finish. The neighbourhood view is unaffected; see the log for the reason.", ex);
            }
        }

        /// <summary>
        /// The last step of <see cref="PopulateScreen"/>: back the camera out to the whole view
        /// and dispose of the lot panel that belonged to the map that was just replaced.
        /// </summary>
        /// <remarks>
        /// A new map starts zoomed out with nothing selected, so the incoming state is always
        /// <see cref="NeighCameraMode.FullScreen"/>. Doing it explicitly rather than by resetting
        /// the fields is what guarantees the lot panel, the selection and the camera all agree
        /// afterwards.
        /// </remarks>
        private void FinishPopulate()
        {
            // The outgoing lot panel is about to be torn down. Hiding it first stops it from
            // sliding off screen underneath (and then behind) the reset tween, which reads as
            // two things happening at once.
            if (LastHS != null) LastHS.Visible = false;
            ResetZoom();

            // the reset above only starts a tween; place the new artwork now so the very first
            // frame is in the right place rather than a tween step behind
            UpdatePosition();
        }

        /// <summary>
        /// Drops the lot selection and eases the camera back to the whole neighbourhood.
        /// </summary>
        /// <remarks>
        /// Safe to call at any time, from any state. Declining the move-in confirmation calls
        /// this, and so do clicking the background and switching neighbourhood, so the camera
        /// always has a way back rather than being able to get stranded zoomed in on a lot
        /// whose panel has gone.
        /// </remarks>
        public void ResetZoom()
        {
            _selectedHouse = null;
            _selectedCameraMode = NeighCameraMode.FullScreen;

            // deliberately not guarded on LastHS being open: the camera may be out of sync with
            // the panel (a resize that dropped the selection, say) and the camera is the thing
            // that has to be fixed either way
            if (LastHS != null)
            {
                LastHS.Kill();
                LastHS = null;
            }

            var target = NeighCameraMachine.FullScreen();
            GameFacade.Screens.Tween.To(this, NeighCameraMachine.CameraTweenDuration, target.TweenArgs(), TweenQuad.EaseOut);
        }

        public UIHouseSelectPanel LastHS;

        /// <summary>
        /// A lot on the map was picked. Also the <see cref="Action{T}"/> every lot button is built
        /// with, so it has to be a one-argument method rather than one with an optional second
        /// parameter - a method group with two parameters does not convert to <c>Action&lt;int&gt;</c>.
        /// </summary>
        /// <param name="house">The lot number.</param>
        public void SelectHouse(int house)
        {
            SelectHouse(house, true);
        }

        /// <summary>
        /// <see cref="SelectHouse(int)"/>, with the click-to-deselect behaviour under the caller's
        /// control.
        /// </summary>
        /// <param name="house">The lot number.</param>
        /// <param name="toggleSelection">
        /// True for a click, which deselects a lot that is already selected. False for the Next and
        /// Previous buttons, which step rather than toggle: with one lot left under a filter, a
        /// second press of Next lands on the lot that is already open, and a toggle there would
        /// close the panel the player is using the arrows to move between.
        /// </param>
        public void SelectHouse(int house, bool toggleSelection)
        {
            if (toggleSelection && LastHS != null && LastHS.HouseID == house)
            {
                // clicking the same lot again toggles the selection off. ResetZoom clears
                // _selectedHouse as well as the panel.
                ResetZoom();
            }
            else
            {
                _selectedHouse = house;
                SetCameraMode(NeighCameraMode.HouseFocus);
            }
        }

        /// <summary>
        /// Opens a lot panel for <paramref name="house"/>, replacing any panel already open.
        /// </summary>
        private void OpenHouseSelectPanel(int house)
        {
            if (LastHS != null) LastHS.Kill();
            LastHS = new UIHouseSelectPanel(house, options);

            LastHS.OnSelected += (h) =>
            {
                OnHouseSelect?.Invoke(h);
                //HITVM.Get().PlaySoundEvent("bkground_fade");
            };
            GameFacade.Screens.CurrentUIScreen.Add(LastHS);
        }

        /// <summary>
        /// Applies the camera settings for the current state - or for <paramref name="NewMode"/>,
        /// switching to it first - and makes the lot panel match.
        /// </summary>
        /// <param name="NewMode">The state to move to, or <see langword="null"/> to simply
        /// re-apply the current one (which is what a window resize needs).</param>
        private void SetCameraMode(NeighCameraMode? NewMode = null)
        {
            if (NewMode.HasValue) _selectedCameraMode = NewMode.Value;

            NeighCameraTarget target;
            switch (_selectedCameraMode)
            {
                case NeighCameraMode.HouseFocus:
                    if (!NeighCameraMachine.TryHouseFocus(_selectedHouse, HousePositions, (ushort)Mode,
                        new Vector2(CenterPositionX, CenterPositionY), out target))
                    {
                        // the selection was cleared while we were moving into this state, so
                        // there is no lot to zoom in on; back out to the whole view instead
                        ResetZoom();
                        return;
                    }
                    OpenHouseSelectPanel(target.House.Value);
                    break;

                case NeighCameraMode.FullScreen:
                default:
                    target = NeighCameraMachine.FullScreen();
                    break;
            }

            GameFacade.Screens.Tween.To(this, NeighCameraMachine.CameraTweenDuration, target.TweenArgs(), TweenQuad.EaseOut);
        }

        public override void Removed()
        {
            base.Removed();
            BgSound?.RemoveOwner(-25);
            //the 3D world lives in GameFacade.Scenes, not in this panel's children, so removing
            //the panel from the screen does not take it with it: it has to be torn down here or it
            //would go on drawing behind whatever came next
            DetachWorld();
            _transparentBackdrop?.Dispose();
            _transparentBackdrop = null;
        }

        public override void Draw(UISpriteBatch batch)
        {
            base.Draw(batch);
            //DrawLocalTexture(batch, texture, new Vector2());
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            //A parked panel is hidden, not dead, so its own update is still called - but the per-frame
            //backdrop reconcile has nothing to reconcile while a lot is up. Running it anyway would
            //re-decide the frame against a scene that is not drawing (the rule reads submitted
            //triangles, which are zero for an invisible scene), so it would put the painted map back
            //and remember that it had, and the resume would start from a decision made in the dark.
            if (_suspendedForLot) return;

            //The backdrop contract is re-decided every frame rather than once at attach: the mesh
            //upload, a draw that throws and a device reset all happen after this panel was built,
            //and every one of them is a reason to put the painted map back. Wrapped, because a
            //rule that throws here would stop the UI updating for the rest of the session.
            try
            {
                RefreshBackdrop(state);
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Deciding which neighbourhood backdrop to draw failed. The"
                    + " painted map stays up; see the log for the reason.", ex);
            }
        }
    }

    /// <summary>
    /// The two states the neighbourhood view's camera can be in.
    /// </summary>
    /// <remarks>
    /// The camera used to be moved by whichever bit of code happened to need it - clicking a
    /// lot zoomed in, clicking the background zoomed out, switching neighbourhood reset the
    /// numbers without touching an open lot panel - and nothing recorded which of those it
    /// was currently in. That left states reachable that nothing could get out of, e.g. the
    /// camera zoomed in with no lot panel because the "No" on the move-in prompt just closed
    /// the dialog. Everything now goes through <see cref="NeighCameraMachine"/> instead.
    /// </remarks>
    public enum NeighCameraMode
    {
        /// <summary>Zoomed out, showing the whole neighbourhood.</summary>
        FullScreen,
        /// <summary>Zoomed in on a lot the player picked, with its lot panel open.</summary>
        HouseFocus
    }

    /// <summary>
    /// Where the neighbourhood camera should end up for a given state, and which lot (if any)
    /// needs a panel opening as part of getting there.
    /// </summary>
    /// <remarks>
    /// Plain data on purpose: the decision of where to go is worth testing without standing up
    /// a graphics device, a neighbourhood and a screen to hang panels off. Everything here is
    /// in the neighbourhood bitmap's own 800x600 coordinate space; the panel applies the
    /// window scale and zoom on top.
    /// </remarks>
    public readonly struct NeighCameraTarget
    {
        public NeighCameraTarget(NeighCameraMode mode, float zoom, float centerX, float centerY, int? house)
        {
            Mode = mode;
            Zoom = zoom;
            CenterX = centerX;
            CenterY = centerY;
            House = house;
        }

        /// <summary>The state this target puts the camera in.</summary>
        public NeighCameraMode Mode { get; }

        /// <summary>Zoom multiplier to tween to.</summary>
        public float Zoom { get; }

        /// <summary>Point in the bitmap the camera should centre on, X.</summary>
        public float CenterX { get; }

        /// <summary>Point in the bitmap the camera should centre on, Y.</summary>
        public float CenterY { get; }

        /// <summary>The lot to open a <see cref="UIHouseSelectPanel"/> for, or null for none.</summary>
        public int? House { get; }

        /// <summary>
        /// The tween arguments, in the name/value shape <c>UITween.To</c> reflects over the
        /// target object's properties.
        /// </summary>
        public Dictionary<string, float> TweenArgs()
        {
            return new Dictionary<string, float>
            {
                { "Zoom", Zoom },
                { "CenterPositionX", CenterX },
                { "CenterPositionY", CenterY }
            };
        }
    }

    /// <summary>
    /// The decisions behind <see cref="UINeighborhoodSelectionPanel"/>'s camera: which state a
    /// given event leads to, and where the camera sits in each. No graphics, no content, no
    /// game state.
    /// </summary>
    public static class NeighCameraMachine
    {
        /// <summary>Centre of the base 800x600 neighbourhood bitmap.</summary>
        public const float DefaultCenterX = 400f;
        public const float DefaultCenterY = 300f;

        public const float FullScreenZoom = 1f;

        /// <summary>Zoom used when focusing a lot, leaving the lot panel half the screen.</summary>
        public const float HouseFocusZoom = 1.5f;

        /// <summary>
        /// Unleashed draws its map at half size, so it starts further out and has to come in
        /// further to end up at the same apparent zoom.
        /// </summary>
        public const ushort UnleashedMode = 4;
        public const float UnleashedHouseFocusZoom = 3f;

        /// <summary>
        /// How far left of the lot the camera centres, so the lot ends up in the half of the
        /// screen the lot panel is not covering.
        /// </summary>
        public const float HouseFocusCenterOffset = 180f;
        public const float UnleashedHouseFocusCenterOffset = 90f;

        /// <summary>Seconds the camera takes to get where it is going.</summary>
        public const float CameraTweenDuration = 0.5f;

        /// <summary>
        /// The zoomed-out view: the whole neighbourhood, no lot panel.
        /// </summary>
        public static NeighCameraTarget FullScreen()
        {
            return new NeighCameraTarget(NeighCameraMode.FullScreen, FullScreenZoom, DefaultCenterX, DefaultCenterY, null);
        }

        /// <summary>
        /// The zoomed-in view of <paramref name="house"/>, which lives at
        /// <paramref name="housePosition"/> in the current bitmap.
        /// </summary>
        public static NeighCameraTarget HouseFocus(int house, Vector2 housePosition, ushort mode)
        {
            bool unleashed = (mode == UnleashedMode);
            return new NeighCameraTarget(NeighCameraMode.HouseFocus,
                unleashed ? UnleashedHouseFocusZoom : HouseFocusZoom,
                housePosition.X - (unleashed ? UnleashedHouseFocusCenterOffset : HouseFocusCenterOffset),
                housePosition.Y,
                house);
        }

        /// <summary>
        /// Works out where to zoom to for the current selection, or reports that there is
        /// nothing to zoom to.
        /// </summary>
        /// <param name="house">The selected lot, or null if the selection has gone.</param>
        /// <param name="housePositions">Where each lot in the current view is, from the
        /// neighbourhood's location string.</param>
        /// <param name="mode">The neighbourhood currently being shown.</param>
        /// <param name="fallbackPosition">Where to point the camera for a lot this view does
        /// not have a position for.</param>
        /// <param name="target">The resulting target; the full-screen one when this returns
        /// false.</param>
        /// <returns>False when there is no lot to focus on.</returns>
        public static bool TryHouseFocus(int? house, IReadOnlyDictionary<int, Vector2> housePositions, ushort mode,
            Vector2 fallbackPosition, out NeighCameraTarget target)
        {
            target = FullScreen();
            if (house == null || house < 0) return false;
            target = HouseFocus(house.Value, ResolveHousePosition(housePositions, house.Value, fallbackPosition), mode);
            return true;
        }

        /// <summary>
        /// Looks a lot up in the current view.
        /// </summary>
        /// <remarks>
        /// Not every lot that can be selected has a position in the current view:
        /// <c>HousePositions</c> only holds the lots in this neighbourhood's location string,
        /// which skips lot 99 and any entry it cannot parse, and a lot can be selected after
        /// the player has already moved to a different neighbourhood. Falling back to the
        /// view's own centre means the panel still opens rather than the whole screen going
        /// down on a <see cref="System.Collections.Generic.KeyNotFoundException"/>.
        /// </remarks>
        public static Vector2 ResolveHousePosition(IReadOnlyDictionary<int, Vector2> housePositions, int house, Vector2 fallback)
        {
            if (housePositions != null && housePositions.TryGetValue(house, out var found)) return found;
            return fallback;
        }
    }

    public class UINeighborhoodAnimationLayer : UIElement
    {

        public Texture2D[] Frames;
        public int FrameNum;
        public int SubFrame;
        public int FrameTime;
        private int TotalFrames;
        public NeighborhoodImageAnim Anim;

        public UINeighborhoodAnimationLayer(NeighborhoodImageAnim anim, bool pulsate, int frameTime)
        {
            var provider = Content.Get().TS1Global;
            // A single unknown frame name would previously become a null ITextureRef and then a
            // NullReferenceException while the neighbourhood screen was still being constructed.
            // Drop frames the provider cannot resolve; a layer with none simply draws nothing.
            Frames = anim.Frames
                .Select(x => provider.Get(x))
                .Where(x => x != null)
                .Select(x => ((ITextureRef)x).Get(GameFacade.GraphicsDevice))
                .ToArray();
            SubFrame = frameTime;
            FrameTime = frameTime;
            FrameTime *= GlobalSettings.Default.TargetRefreshRate;
            FrameTime /= 60;
            if (FrameTime < 1) FrameTime = 1;
            TotalFrames = pulsate ? (Frames.Length * 2 - 2) : Frames.Length;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            if (Frames.Length == 0) return;
            if (--SubFrame <= 0)
            {
                SubFrame = FrameTime;
                FrameNum++;
                if (TotalFrames != 0) FrameNum %= TotalFrames;
            }
        }

        public override void Draw(UISpriteBatch batch)
        {
            //The visibility test UIContainer does not do for us. UIElement documents Visible as
            //something "the super class" must implement, and UIContainer tests its own flag and
            //then calls every child's Draw without asking - so without this guard, hiding this layer
            //does nothing at all and the painted map's river, waves and Nessie stay drawn on top of
            //the generated 3D world, in the painted map's own 800x600 pixel space. That is the
            //"displaced 2D river overlay": a flag that was set correctly and never read.
            //UINeighborhoodHouseButton.Draw checks it for the same reason.
            if (!Visible) return;
            if (Frames.Length == 0) return;
            var realFrame = (FrameNum >= Frames.Length) ? ((Frames.Length - 2) - (FrameNum - Frames.Length)) : FrameNum;
            var fm2 = (TotalFrames != 0) ? ((FrameNum + 1) % TotalFrames) : 0;
            var realFrame2 = (fm2 >= Frames.Length) ? ((Frames.Length - 2) - (fm2 - Frames.Length)) : fm2;
            DrawLocalTexture(batch, Frames[Math.Max(0, realFrame)], Vector2.Zero);
            DrawLocalTexture(batch, Frames[Math.Max(0, realFrame2)], null, Vector2.Zero, Vector2.One, Color.White * (1 - ((float)SubFrame / FrameTime)));
        }
    }

    public class NeighborhoodViewConfig
    {
        public string Graphic;
        public float Scale;
        public NeighborhoodImageAnim[] FullImageAnimations = new NeighborhoodImageAnim[0];
        public int FrameDuration = 15;
        public bool Pulsate = true;
        public string Music = "bkground_nhood1";
        public string BGSound;
    }

    public class NeighborhoodImageAnim
    {
        public string[] Frames;
        public Vector2 Position;

        public NeighborhoodImageAnim(params string[] frames)
        {
            Frames = frames;
        }

        public NeighborhoodImageAnim(Vector2 position, params string[] frames) : this(frames)
        {
            Position = position;
        }
    }

    public class UINeighborhoodHouseButton : UIElement
    {
        private Texture2D HouseTex;
        private Texture2D HouseOpenTex;
        private float HouseScale;
        private bool Hovered;
        private THMB Offsets;
        public float AlphaTime { get; set; }

        /// <summary>
        /// False for lots that this view must not offer. See
        /// <see cref="UINeighborhoodSelectionOptions.LotZoneExclusionFilter"/>.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The lot this button stands for. Read by the filter bar and the Next/Previous buttons
        /// through <see cref="UINeighborhoodSelectionPanel.RefreshLotState"/>, which cannot ask
        /// the click callback what lot it was given without capturing it here.
        /// </summary>
        public int HouseNumber { get; }

        public UINeighborhoodHouseButton(int houseNumber, Action<int> selectionCallback, float scale)
        {
            HouseNumber = houseNumber;
            AlphaTime = 0;
            var house = Content.Get().Neighborhood.GetHouse(houseNumber);
            HouseTex = house.Get<BMP>(513)?.GetTexture(GameFacade.GraphicsDevice);
            if (HouseTex != null) {
                HouseOpenTex = house.Get<BMP>(512).GetTexture(GameFacade.GraphicsDevice);
                Offsets = house.Get<THMB>(512); //get offsets before scaling
            } else
            {
                HouseTex = house.Get<PNG>(513).GetTexture(GameFacade.GraphicsDevice);
                HouseOpenTex = house.Get<PNG>(512).GetTexture(GameFacade.GraphicsDevice);
                Offsets = new THMB() { Width = HouseTex.Width / 2, Height = HouseTex.Height / 2 };
            }

            HouseScale = scale;

            var w = (int)(HouseTex.Width / HouseScale);
            var h = (int)(HouseTex.Height / HouseScale);
            var clickHandler =
                ListenForMouse(new Rectangle(w / -2, w / -4, w, h/2), (evt, state) =>
                {
                    if (!Enabled) return;
                    switch (evt)
                    {
                        case UIMouseEventType.MouseUp:
                            HITVM.Get().PlaySoundEvent(FSO.Client.UI.Model.UISounds.NeighborhoodClick);
                            selectionCallback(houseNumber); break;
                        case UIMouseEventType.MouseOver:
                            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "AlphaTime", 1f } });
                            HITVM.Get().PlaySoundEvent(FSO.Client.UI.Model.UISounds.NeighborhoodRollover);
                            Hovered = true; break;
                        case UIMouseEventType.MouseOut:
                            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "AlphaTime", 0f } });
                            Hovered = false; break;
                    }
                });
        }

        public override void Draw(UISpriteBatch batch)
        {
            //UIContainer.Draw calls every child's Draw without asking, so the visibility test that
            //UIImage and UIButton do for themselves has to be here too. A lot filter hides buttons
            //by setting Visible, and without this a hidden lot would still be drawn.
            if (!Visible) return;

            var yOff = new Vector2(Offsets.XOff, -Offsets.BaseYOff) / HouseScale;
            var yOff2 = yOff;
            yOff2.Y -= Offsets.AddYOff / (HouseScale);
            if (AlphaTime > 0)
            {
                DrawLocalTexture(batch, HouseOpenTex, null, new Vector2(-Offsets.Width, -Offsets.Height)/ HouseScale + yOff2, new Vector2(1f / HouseScale, 1f / HouseScale));
            }
            DrawLocalTexture(batch, HouseTex, null, new Vector2(-Offsets.Width, -Offsets.Height) / HouseScale + yOff, new Vector2(1f / HouseScale, 1f / HouseScale), Color.White * (1 - AlphaTime));
        }

        public override void Removed()
        {
            HouseTex?.Dispose();
            HouseOpenTex?.Dispose();
        }
    }

    /// <summary>
    /// The generated neighbourhood scene, wrapped so the panel can tell a world that is quietly
    /// not drawing from one that never started.
    ///
    /// <para>
    /// <b>Why a wrapper and not a property on the scene.</b> A generated world that builds
    /// successfully and then renders nothing is the failure the backdrop contract exists for, and
    /// from outside a <c>_3DScene</c> there is no way to see it: no exception escapes
    /// <c>Draw</c> because the layer does not catch one either, and a device loss looks exactly
    /// like a healthy frame until the next draw throws. Adding those counters to
    /// <c>Neighbourhood3DView</c> would mean the scene had to know it was being watched; this
    /// puts the knowledge where the watching happens and leaves the scene alone.
    /// </para>
    ///
    /// <para>
    /// <b>What it forwards.</b> Everything. The panel's camera work goes through
    /// <see cref="World"/>'s own <c>Focus</c>, so the only thing that has to cross this boundary
    /// is the per-frame lifecycle, and every part of it is forwarded. The one thing deliberately
    /// not forwarded is <see cref="_3DAbstract.DeviceReset"/>: the inner scene has its own
    /// subscription to the same event, and calling its handler as well would build the terrain
    /// content twice per reset. What this class does with a reset is note it, which is the whole
    /// reason it is here.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldProxy : _3DScene
    {
        private readonly Neighbourhood3DView _view;
        private readonly INeighbourhoodRenderSignal _signal;

        public NeighbourhoodWorldProxy(GraphicsDevice device, Neighbourhood3DView view) : base(device)
        {
            _view = view;
            //A scene that publishes its own numbers is preferred over the derived ones, so the day
            //Neighbourhood3DView distinguishes "mesh ready" from "mesh has triangles" the proxy
            //picks it up without anything else changing.
            _signal = view as object as INeighbourhoodRenderSignal;
            ID = view == null ? null : "NeighbourhoodWorldProxy";
        }

        /// <summary>The scene being watched.</summary>
        public Neighbourhood3DView World
        {
            get { return _view; }
        }

        /// <summary>
        /// The scene's own render signal, when it publishes one. The scene reports the numbers only
        /// it can see - upload state and what a frame submitted - and the panel merges those with
        /// this proxy's own <see cref="DrawFailures"/>/<see cref="WasReset"/> rather than choosing
        /// between them, because a caught draw and a counted draw are the same event seen from
        /// either side. See <see cref="NeighbourhoodWorldHealth.MergeDrawFailures"/>.
        /// </summary>
        public INeighbourhoodRenderSignal Signal
        {
            get { return _signal; }
        }

        /// <summary>
        /// Consecutive draws that threw. Reset by the first successful draw, so a single bad frame
        /// is a hiccup rather than a verdict.
        /// </summary>
        public int DrawFailures { get; private set; }

        /// <summary>The message from the most recent failed draw, or <c>null</c>.</summary>
        public string LastDrawFailure { get; private set; }

        /// <summary>
        /// Whether the graphics device has been reset since the last acknowledged one and the
        /// world has not drawn since. The world's buffers went with the old device, so until it
        /// draws again it cannot be said to be showing anything.
        /// </summary>
        public bool WasReset { get; private set; }

        /// <summary>Called by the panel once it has put the painted map back over this.</summary>
        public void AcknowledgeReset()
        {
            WasReset = false;
        }

        public override void Update(UpdateState state)
        {
            if (_view == null) return;
            _view.Update(state);
        }

        public override void PreDraw(GraphicsDevice device)
        {
            if (_view == null) return;
            _view.PreDraw(device);
        }

        public override void Draw(GraphicsDevice device)
        {
            if (_view == null) return;
            try
            {
                _view.Draw(device);
                DrawFailures = 0;
                LastDrawFailure = null;
                //A frame that drew proves the device is back, whatever it was before.
                WasReset = false;
            }
            catch (Exception ex)
            {
                DrawFailures++;
                LastDrawFailure = ex.Message;
                //One bad frame is not a broken world and must not spam the log; a run of them is,
                //and by then the painted map is already back and this is the receipt.
                if (DrawFailures == NeighbourhoodBackdropRule.DrawFailureLimit)
                {
                    SimitoneLog.Error("Drawing the generated neighbourhood failed " + DrawFailures
                        + " times in a row, so it will not be used as the backdrop and the painted"
                        + " map goes back. First failure: " + ex.Message, ex);
                }
            }
        }

        public override void DeviceReset(GraphicsDevice device)
        {
            //Deliberately does not forward: _view has its own DeviceReset subscription and
            //calling its handler as well would rebuild its content twice per reset. All the panel
            //needs to know is that the world now owes it a fresh demonstration.
            WasReset = true;
        }

        public override void Dispose()
        {
            _view?.Dispose();
            base.Dispose();
        }
    }
}
