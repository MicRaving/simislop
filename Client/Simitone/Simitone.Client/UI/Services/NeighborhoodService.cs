/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Content;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Threads;
using FSO.SimAntics.Model.TS1Platform;
using FSO.SimAntics.Utils;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;

namespace Simitone.Client.UI.Services
{
    /// <summary>
    /// The neighbourhood half of <c>TS1GameScreen</c>: showing the lot picker, entering a house,
    /// moving a family in and evicting one.
    ///
    /// <para>
    /// This is the hybrid-mode flow. A TS1 session starts outside a lot, on
    /// <see cref="UINeighborhoodSelectionPanel"/> plus its <see cref="UINeighbourhoodSwitcher"/>.
    /// Choosing a house either plays it directly or - when the player arrived here through CAS to
    /// move a family in - confirms, charges the lot, and then plays it. Eviction runs the opposite
    /// way: refund, release the lot, and re-select it in the picker.
    /// </para>
    ///
    /// <para>
    /// The screen keeps its state (<c>TS1NeighPanel</c>, <c>MoveInFamily</c>,
    /// <c>ActiveFamily</c>, <c>Downtown</c>, <c>CurrentNeighborhoodMode</c>); the service only
    /// performs the steps, through the screen's own public members, so the ordering guarantees are
    /// unchanged - in particular the neighbourhood is committed <em>before</em> the lot is loaded on
    /// a move-in and <em>after</em> the refund is applied on an eviction.
    /// </para>
    /// </summary>
    public sealed class NeighborhoodService
    {
        /// <summary>
        /// The screen whose state this service drives.
        /// </summary>
        private readonly TS1GameScreen _screen;

        /// <summary>
        /// The switcher belonging to the <see cref="UINeighborhoodSelectionPanel"/> that is
        /// currently on screen.
        ///
        /// <para>
        /// This is a field rather than a closure capture on purpose. The house-select handler used
        /// to be an anonymous lambda that captured the switcher created alongside the panel, and
        /// nothing could ever detach it. Naming the handler and keeping the switcher here means
        /// <see cref="NeighSelection"/> can unsubscribe from the previous panel before building a
        /// new one, so a removed panel no longer holds the screen alive through its delegate -
        /// see the note on <see cref="NeighSelection"/>.
        /// </para>
        /// </summary>
        private UINeighbourhoodSwitcher _switcher;

        /// <summary>
        /// The generated-3D refresh currently in flight, if any.
        ///
        /// <para>
        /// A field rather than a local because the refresh is started in one place and waited on in
        /// another: <see cref="NeighSelection(ushort, bool)"/> starts it and waits for the layout
        /// before the panel is built, and a second <c>NeighSelection</c> for the same neighbourhood
        /// has to be able to see that one is already running rather than start a second set of
        /// twenty IFF reads.
        /// </para>
        /// </summary>
        private NeighbourhoodRefreshJob _generatedWorld;

        /// <summary>
        /// The lot the player is inside, or <c>null</c> when they are on the neighbourhood screen.
        ///
        /// <para>
        /// Set by <see cref="PlayHouse"/> and cleared by the next
        /// <see cref="NeighSelection(ushort, bool)"/>. It exists for one reason: the return trip
        /// from a lot goes through <c>TS1GameScreen.ExitLot</c>, which is not this file's to edit,
        /// and that method's only neighbourhood call is <c>NeighSelection</c>. Remembering which lot
        /// was left is what lets the refresh name the one that is most likely to have changed, so
        /// "the world I came back to is stale" is a line in the log rather than a mystery.
        /// </para>
        /// </summary>
        private short? _lotBeingPlayed;

        /// <summary>
        /// The lot switch B3 put behind a seam, used by the picker path of
        /// <see cref="PlayHouse"/> when the travel capability is on.
        ///
        /// <para>
        /// A static field because <see cref="LotTravelService"/> is stateless and holds nothing -
        /// it makes every decision from the host it is handed - so one instance serves a whole
        /// session and nothing in it can leak from one lot to the next. It is created here rather
        /// than per call for the same reason, and because a test that wanted one would be testing
        /// the seam's contract, not this service's.
        /// </para>
        /// </summary>
        private static readonly LotTravelService LotTravel = new LotTravelService();

        /// <summary>
        /// Creates a neighbourhood service driving <paramref name="screen"/>.
        /// </summary>
        public NeighborhoodService(TS1GameScreen screen)
        {
            _screen = screen;
        }

        /// <summary>
        /// The neighbourhood a mode opens on, as the two-digit id the switcher buttons use.
        /// </summary>
        /// <remarks>
        /// Pure: the residential neighbourhood is where every mode starts from, and only the magic
        /// move-in flow opens somewhere else. The id comes from <see cref="NeighbourhoodModeMap"/>
        /// so the service and the panel cannot disagree about which map "residential" is - which
        /// map, specifically, is <see cref="NeighbourhoodModeMap.ResidentialDefault"/>'s decision
        /// and not this method's. The base map is still one button away; see
        /// <see cref="NeighbourhoodModeMap.TravelTargets"/>.
        /// </remarks>
        /// <param name="mode">How the player got to the neighbourhood screen.</param>
        public static ushort NeighbourhoodIdFor(NeighSelectionMode mode)
        {
            return (ushort)((mode == NeighSelectionMode.MoveInMagic)
                ? NeighbourhoodModeMap.Magictown
                : NeighbourhoodModeMap.ResidentialDefault);
        }

        /// <summary>
        /// The neighbourhood a lot belongs to, as the two-digit id the switcher buttons and
        /// <c>UINeighborhoodSelectionPanel</c> use.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Delegates to <see cref="NeighbourhoodModeMap.ForHouse"/> so the panel, the switcher and
        /// this service share one table. The lot number ranges are the ones the rest of the game
        /// already agrees on - see <c>LotPersistence</c> (21-31, 40-49, 81-89, 90-99),
        /// <c>UIMainPanel.ResolveLotType</c> (the same four ranges) and the <c>LotLocations</c> STR
        /// tables the panel reads. Anything outside them is the base residential map.
        /// </para>
        /// <para>
        /// Studio ends at 89 and not 90, deliberately. The original upstream version of this mapping
        /// used <c>81..90</c> for Studio and listed the magic range afterwards, so the overlap made
        /// lot 90 - the first magic-town lot - resolve to Studio. This tree already fixed that
        /// off-by-one in <c>ResolveLotType</c>; the two tables have to agree, or "return to
        /// neighbourhood" from a magic-town lot would open the studio map.
        /// </para>
        /// <para>
        /// A residential lot comes back to <see cref="NeighbourhoodModeMap.ResidentialDefault"/>,
        /// which is the same map the game opens on, so returning from a lot does not move the
        /// player onto a different background than the one they chose.
        /// </para>
        /// </remarks>
        /// <param name="house">The lot number the player is standing on.</param>
        /// <returns>The neighbourhood id, never 0 and never outside the panel's table.</returns>
        public static ushort NeighbourhoodIdForHouse(short house)
        {
            return NeighbourhoodModeMap.ForHouse(house);
        }

        /// <summary>
        /// Records which family is about to move in, so the next lot the player picks is bought
        /// for them rather than played directly.
        /// </summary>
        /// <param name="familyID">The FAMI chunk id of the family to move in.</param>
        public void StartMoveIn(int familyID)
        {
            _screen.MoveInFamily = familyID;
        }

        /// <summary>
        /// Builds (or rebuilds) the neighbourhood lot picker and its switcher, and puts them on the
        /// screen.
        /// </summary>
        /// <remarks>
        /// <b>Subscription lifetime.</b> This used to do
        /// <c>TS1NeighPanel.OnHouseSelect += (house) =&gt; {...}</c> against a freshly built panel
        /// on every call, with nothing ever removing it. The handler is now
        /// <see cref="OnHouseSelect"/>, and the previous panel - if there was one - is detached
        /// from first. That is the only behavioural difference in this service, and it is a leak
        /// fix rather than a change of behaviour: the detached panel had already been removed from
        /// the screen, so no click could ever reach its handler, but the closure kept the panel,
        /// the switcher and the screen reachable for as long as the screen lived. Nothing about
        /// what a click does differs, because the switcher the handler needs is the one belonging
        /// to the panel that is actually on screen.
        /// </remarks>
        /// <remarks>
        /// A thin wrapper: this resolves the mode to a neighbourhood and hands over to
        /// <see cref="NeighSelection(ushort, bool)"/>, which is what "return to neighbourhood" uses
        /// now that it can name a neighbourhood other than the one the mode implies.
        /// </remarks>
        /// <param name="mode">How the player got to the neighbourhood screen.</param>
        public void NeighSelection(NeighSelectionMode mode)
        {
            NeighSelection(NeighbourhoodIdFor(mode), mode != NeighSelectionMode.Normal);
        }

        /// <summary>
        /// Builds (or rebuilds) the picker and switcher for one specific neighbourhood.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the real entry point; <see cref="NeighSelection(NeighSelectionMode)"/> is the
        /// convenience wrapper that turns an entry mode into a neighbourhood. It is a separate
        /// overload rather than a change of that method's signature because the two callers want
        /// genuinely different things: a mode describes <em>how the player got here</em> (start of
        /// session, or a move-in out of CAS, which hides the travel buttons), whereas an
        /// explicit id describes <em>which neighbourhood to show</em>.
        /// </para>
        /// <para>
        /// The second argument is deliberately not derived from the id, exactly as before: the
        /// magic move-in is the one non-residential id that still has to suppress the travel
        /// buttons, so the caller states move-in mode rather than having it inferred.
        /// </para>
        /// </remarks>
        /// <param name="neighborhoodId">The neighbourhood to open on - see
        /// <see cref="NeighbourhoodIdFor"/> and <see cref="NeighbourhoodIdForHouse"/>.</param>
        /// <param name="moveInMode">True when the next lot picked buys a lot for a family waiting
        /// to move in, which hides the travel buttons in the switcher.</param>
        public void NeighSelection(ushort neighborhoodId, bool moveInMode = false)
        {
            Content.Get().Neighborhood.PreparePersonDataFromObject = PersonGeneratorHelper.PreparePersonDataFromObject;
            Content.Get().Neighborhood.AddMissingNeighbors();

            //The generated 3D neighbourhood has to be current before the panel is built, because
            //the panel's world is built synchronously inside its own constructor and reads the
            //layout sidecar. This is §8's "build/refresh layout on NeighSelection": a lot that was
            //edited, bulldozed or rezoned since the last visit has its placement repaired here, so
            //what the player comes back to is the map they left. It costs nothing when the 3D
            //neighbourhood is off, which is the 2D option on the Graphics page.
            //
            //Coming back from a lot is the case that matters: TS1GameScreen.ExitLot is not this
            //file's to edit and its only neighbourhood call is this one, so the lot the player just
            //left is named here - it is the one whose HouseNN.iff is most likely to have moved.
            var returningFrom = _lotBeingPlayed;
            _lotBeingPlayed = null;
            RefreshGeneratedNeighbourhood(neighborhoodId, returningFrom);

            //The park. If a panel is parked for this very view and its world is still the world this
            //neighbourhood has, it is brought back rather than replaced - so returning from a lot
            //costs a resume, not a new terrain renderer, a new mesh build and a new upload of a map
            //whose raster never moved. The two ways it can fail both fall through to the rebuild that
            //was the only behaviour before: a different view, or a world the return-trip repair moved
            //underneath the panel.
            var parked = _screen.TS1NeighPanel;
            if (parked != null && parked.IsSuspendedForLot
                && parked.Neighbourhood == neighborhoodId
                && parked.WorldIsCurrent(CurrentGeneratedWorld(neighborhoodId)))
            {
                parked.OnHouseSelect += OnHouseSelect;
                parked.ResumeAfterLotEntry();
                //Rebuilt rather than merely shown if it is not there: RefreshNeighbourhoodUi removes
                //the switcher before re-entering here, and a map with no chrome on it - no travel, no
                //create-a-family button - is not a resumed neighbourhood.
                if (_switcher == null)
                {
                    _switcher = new UINeighbourhoodSwitcher(parked, neighborhoodId, moveInMode);
                    _screen.Add(_switcher);
                }
                else
                {
                    _switcher.SetMode(neighborhoodId, moveInMode);
                }
                _switcher.Visible = true;
                FocusOnReturn(parked, returningFrom);
                return;
            }

            //detach from the panel we are about to replace, if any
            if (_screen.TS1NeighPanel != null)
            {
                _screen.TS1NeighPanel.OnHouseSelect -= OnHouseSelect;
                if (_screen.TS1NeighPanel.IsSuspendedForLot)
                {
                    //A park that cannot come back - a different view, or a world that moved. It is
                    //taken out of the screen properly now, which is what finally disposes the scene
                    //it has been holding; leaving it parked would keep a neighbourhood nobody is
                    //looking at resident in the scene layer for the rest of the session.
                    _screen.Remove(_screen.TS1NeighPanel);
                }
                _screen.TS1NeighPanel = null;
            }
            if (_switcher != null)
            {
                _screen.Remove(_switcher);
                _switcher = null;
            }

            _screen.TS1NeighPanel = new UINeighborhoodSelectionPanel(neighborhoodId);
            _switcher = new UINeighbourhoodSwitcher(_screen.TS1NeighPanel, neighborhoodId, moveInMode);
            _screen.TS1NeighPanel.OnHouseSelect += OnHouseSelect;
            _screen.Add(_screen.TS1NeighPanel);
            _screen.Add(_switcher);
            FocusOnReturn(_screen.TS1NeighPanel, returningFrom);
        }

        /// <summary>
        /// The generated world a neighbourhood currently has, without refreshing anything. The cache
        /// is the lifecycle's own store of what the last refresh produced, so asking it is asking
        /// "what would an attach right now get", which is the question the park decision is about.
        /// </summary>
        /// <param name="neighborhoodId">The neighbourhood view being opened.</param>
        private static NeighbourhoodGeneratedWorld CurrentGeneratedWorld(ushort neighborhoodId)
        {
            try
            {
                return NeighbourhoodWorldCache.Peek(neighborhoodId);
            }
            catch (Exception)
            {
                //"I cannot tell" and "there is none" both mean the park cannot be trusted, and both
                //fall to the rebuild that was the only behaviour before this file changed.
                return null;
            }
        }

        /// <summary>
        /// Aims a freshly attached or resumed neighbourhood view at the lot the player is coming back
        /// from, without opening anything over it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A camera transition, not a new screen.</b> §8.1 wants the lot taken "in the same map",
        /// and the cheapest honest form of that is to come back to the map already looking at the
        /// house: the world is the same world, only the camera has moved. It is the same camera target
        /// a click produces, without the selection and without the lot panel - so nothing is opened
        /// over a map the player has not clicked on.
        /// </para>
        /// <para>
        /// <b>Only when there is something to look at.</b> A lot this view does not contain - a lot
        /// picked on another map, a numbered sentinel - leaves the camera where it is, which is the
        /// zoomed-out whole neighbourhood the panel starts on. That is also the first-run case: a
        /// fresh session arrives with no lot behind it and therefore sees the whole connected map,
        /// which is §8.1's first requirement and the reason this is a no-op rather than a rule.
        /// </para>
        /// </remarks>
        /// <param name="panel">The panel now on screen.</param>
        /// <param name="returningFrom">The lot being left, or <c>null</c>.</param>
        private static void FocusOnReturn(UINeighborhoodSelectionPanel panel, short? returningFrom)
        {
            if (panel == null || !returningFrom.HasValue) return;
            panel.FocusOnLot(returningFrom.Value);
        }

        /// <summary>
        /// The refresh in flight, or <c>null</c>. Exposed for the screen and for a test that wants
        /// to know whether the neighbourhood is still settling.
        /// </summary>
        public NeighbourhoodRefreshJob GeneratedNeighbourhoodRefresh => _generatedWorld;

        /// <summary>
        /// Brings the generated 3D world for a neighbourhood up to date, off the game thread, and
        /// waits for the part the panel needs.
        ///
        /// <remarks>
        /// <para>
        /// <b>Why there is a wait at all.</b> <c>UINeighborhoodSelectionPanel</c> builds its world
        /// inside its own constructor and reads the layout sidecar while doing it, so a repair that
        /// has to re-place a lot has to have been written before the panel exists. The wait is
        /// bounded, and it is on the layout only - the 512x512 raster, which is the expensive half,
        /// is generated on the worker afterwards and lands in the shared cache for the in-lot
        /// surroundings and the next visit.
        /// </para>
        /// <para>
        /// <b>Why a timeout is survivable.</b> If the wait expires the panel is built from whatever
        /// the sidecar says, which is the behaviour this game has always had: an old layout and a
        /// correct-looking neighbourhood. The next visit repairs it. That is the whole of the 2D
        /// fallback's promise generalised to the layout, and it is why a stalled disk cannot take
        /// the neighbourhood screen with it.
        /// </para>
        /// <para>
        /// <b>Nothing happens at all with the setting off.</b> That check is the first statement
        /// after the gate, and it is why §0 decision 1 holds: a player who never opted into the 3D
        /// neighbourhood reads no sidecar, stats no lot and generates no tile.
        /// </para>
        /// </remarks>
        /// <param name="neighborhoodId">The neighbourhood slot being opened, 1..8.</param>
        /// <param name="returningFromLot">
        /// The lot the player was standing in, when this selection is the return from it, or
        /// <c>null</c>. Used only for the log: the repair itself finds the changed lots by hash,
        /// which is the only statement that can be true. Naming the lot makes the log say "lot 21
        /// was the one you were in" instead of leaving a reader to work it out.
        /// </param>
        public void RefreshGeneratedNeighbourhood(ushort neighborhoodId, short? returningFromLot = null)
        {
            if (!NeighbourhoodLifecycle.WantsWorld) return;

            //A lot load never refreshes the neighbourhood. This is the guard for the case that was
            //reported: the player entered a lot, the generated world for the slot was dropped as
            //stale, and the view - which the lot is now standing in front of - rebuilt itself and
            //drew over the lot. Whether a lot is up is the whole of the question, so it is asked
            //here rather than inferred from who called.
            if (_screen.InLot) return;

            //already refreshing this neighbourhood: a second request would be a second set of
            //reads and a second layout write, and the first one is still going to land
            if (_generatedWorld != null && _generatedWorld.Slot == neighborhoodId && !_generatedWorld.IsComplete)
            {
                return;
            }

            var userDir = FSOEnvironment.UserDir;
            var from = returningFromLot;
            //The content source is built on the worker rather than captured across the thread
            //boundary: a TS1LotContentSource caches the lot records it has read, and sharing one
            //between a refresh and the panel would be sharing a Dictionary across threads. What it
            //reads off the provider is read-only - a path, an IffFile opened from it, two
            //dictionaries - and the reads do not overlap the panel's, because the panel is not
            //built until the wait below returns.
            _generatedWorld = NeighbourhoodLifecycle.RefreshInBackground(
                userDir, neighborhoodId, neighborhoodId, NeighbourhoodLifecycle.RequestedView,
                () => new TS1LotContentSource(Content.Get().Neighborhood, neighborhoodId),
                null);

            if (from.HasValue)
            {
                SimitoneLog.Info("Returning to neighbourhood " + neighborhoodId + " from lot "
                    + from.Value + "; the generated world is being brought up to date with it.");
            }

            if (!_generatedWorld.WaitForLayout(NeighbourhoodLifecycle.LayoutWait))
            {
                SimitoneLog.Warn("The generated neighbourhood for " + neighborhoodId
                    + " was still being rebuilt after "
                    + (int)NeighbourhoodLifecycle.LayoutWait.TotalSeconds
                    + " seconds, so the neighbourhood is being drawn from the layout that was"
                    + " already on disk. It will be repaired the next time this neighbourhood is"
                    + " opened.");
            }
        }

        /// <summary>
        /// Tears the neighbourhood UI down and builds it again from whatever the provider has open
        /// now, keeping the map the player was on.
        ///
        /// <para>
        /// This is the whole of what a neighbourhood <em>switch</em> needs from this side. The
        /// provider is re-initialised by the caller - it is the one that knows the old slot id and
        /// has to be able to put it back if the switch fails - and everything the picker and its
        /// switcher show is read out of the provider's files: <c>PopulateScreen</c> asks for
        /// <c>LotLocations</c> and then opens a <c>House{NN}.iff</c> per lot, so a panel built
        /// before the switch is showing the previous neighbourhood's houses no matter what the
        /// buttons are.
        /// </para>
        ///
        /// <para>
        /// The map is kept, deliberately. The nine slots and the six views are different things -
        /// downtown, vacation, studio and magic all live inside the one slot that is open - so
        /// this re-reads <see cref="TS1GameScreen.CurrentNeighborhoodMode"/> out of the new folder
        /// and leaves it alone. The old panel has to be detached before it is dropped, or its
        /// <c>OnHouseSelect</c> handler keeps the removed panel, this switcher and the screen
        /// alive for as long as the screen lives - the same leak
        /// <see cref="NeighSelection(ushort, bool)"/> already undoes on every rebuild.
        /// </para>
        /// </summary>
        public void RefreshNeighbourhoodUi()
        {
            if (_screen.TS1NeighPanel != null)
            {
                _screen.TS1NeighPanel.OnHouseSelect -= OnHouseSelect;
                _screen.Remove(_screen.TS1NeighPanel);
                _screen.TS1NeighPanel = null;
            }

            if (_switcher != null)
            {
                _screen.Remove(_switcher);
                _switcher = null;
            }

            //re-runs exactly what a first build runs: the person data hook, the missing-neighbour
            //pass, and a fresh panel/switcher pair attached to this screen.
            NeighSelection(_screen.CurrentNeighborhoodMode);
        }

        /// <summary>
        /// A lot was picked in the picker. Plays it, or asks the player to confirm buying it for
        /// the family waiting to move in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Named so it can be attached and detached - see <see cref="NeighSelection"/>.
        /// </para>
        /// <para>
        /// Note this confirmation does <b>not</b> call <c>TryBeginConfirm</c>: there is nothing to
        /// re-enter, the "yes" button calls <see cref="MoveInAndPlay"/> directly, so the latch has
        /// no job suppressing anything. It therefore takes no claim of its own, exactly as before,
        /// and only shares the dialog construction - <c>ShowConfirm</c> claims it as it shows, so
        /// the dialog is still visible to <c>CloseAttempt</c> and to the screen's other
        /// confirmations. That used not to be true, and the cost was a second modal: while this
        /// was up, the quit confirmation saw a free latch and put itself on top of it. The family
        /// id is read inside the "yes" handler, not captured when the dialog opens, so it is the
        /// value at press time that buys the lot.
        /// </para>
        /// </remarks>
        /// <param name="house">The lot number that was clicked.</param>
        public void OnHouseSelect(int house)
        {
            var switcher = _switcher;
            if (_screen.MoveInFamily != null)
            {
                //move them in first
                //confirm it
                var dialogs = _screen.Dialogs;
                dialogs.ShowConfirm(
                    GameFacade.Strings.GetString("132", "0"),
                    GameFacade.Strings.GetString("132", "1"),
                    () => MoveInAndPlay((short)house, _screen.MoveInFamily.Value, switcher),
                    null);
                return;
            }

            PlayHouse((short)house, switcher);
        }

        /// <summary>
        /// Enters a house as the active family's lot and tears the neighbourhood UI down.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The lot is recorded as belonging to the neighbourhood the player picked it on, so
        /// "return to neighbourhood" later reopens that map instead of always falling back to the
        /// residential one. Without it a family moved into a magic-town lot - the only
        /// non-residential move-in there is, since move-in mode hides the travel buttons - was sent
        /// back to Old Town afterwards, and a lot picked off the vacation or studio map lost the
        /// context of where it had been bought.
        /// </para>
        /// <para>
        /// Only the picker and move-in paths write this. Travelling to a lot
        /// (<c>TS1GameScreen</c>'s lot switch) deliberately does not: the player did not arrive on
        /// that lot from its own map, and a map the family's house is not on would strand them
        /// there, so a travelled lot returns to the residential map as before.
        /// </para>
        /// <para>
        /// <b>How the lot is loaded depends on one flag.</b> With
        /// <see cref="NeighbourhoodWorldFlag.Travel"/> off - the default - the call is the direct
        /// <c>InitializeLot</c> it has always been, in the same place in this method and with the
        /// same arguments. With it on, the load is asked for through
        /// <see cref="LotTravelService.TryTransition"/> as a <see cref="TransitionReason.Picker"/>
        /// transition, which is what puts the picker's lot entry on the one switch path the in-VM
        /// travel request uses. Nothing else here branches on the flag, so everything above the
        /// branch - the family, the neighbourhood and the return-trip lot - is recorded the same
        /// way either way.
        /// </para>
        /// </remarks>
        /// <param name="house">The lot number to play.</param>
        /// <param name="switcher">The switcher to remove, or <c>null</c> to leave it alone.</param>
        public void PlayHouse(short house, UIElement switcher)
        {
            _screen.ActiveFamily = Content.Get().Neighborhood.GetFamilyForHouse((short)house);
            _screen.CurrentNeighborhoodMode = NeighbourhoodIdForHouse(house);
            //remembered so the next NeighSelection - which is how the player comes back, via
            //TS1GameScreen.ExitLot - can say which lot it is coming back from. See the field.
            _lotBeingPlayed = house;
            //the lot number is threaded through so InitializeLot can key a cross-session travel
            //slot before the lot exists. it is always the family's own house here, so
            //TryRestoreTravelSlot declines and the template loads as before.
            //
            //InitializeLot is also what takes the neighbourhood view down: every route into a lot -
            //this one, the travel switch, a marshalled lot - goes through it, and the view has to
            //go with all of them rather than only with the one a player reaches from the map.
            //
            //B3 (NeighbourhoodWorldFlag.Travel on): the load is asked for through
            //LotTravelService.TryTransition with TransitionReason.Picker, so the picker and the
            //in-VM travel request cannot drift into subtly different lot-preservation behaviour -
            //the stash-before-load order, downtown before load, preserved-state-first. The flag is
            //the whole gate: with it off, the travel service is never even asked, and the call below
            //is the one that has always been made from this line.
            //
            //A refusal is a false return (with the reason in the log) and a throw is caught, rather
            //than either reaching the picker: a failure here must not cost the player their lot, so
            //the direct call runs in both cases.
            if (NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel)
                && TryTravelIntoPickedLot(house))
            {
                //loaded through LotTravelService above. the switcher below still has to go, and that
                //is the only thing the direct path's call is followed by.
            }
            else
            {
                _screen.InitializeLot(Content.Get().Neighborhood.GetHousePath(house), false, house);// "UserData/Houses/House21.iff"
            }
            //The switcher is hidden, not removed. InitializeLot above ran
            //SuspendNeighbourhoodForLot, which parks the whole neighbourhood view - panel and switcher -
            //so that coming back does not build a second neighbourhood (see that method). Removing
            //the switcher here would undo half of it: the panel would come back on resume and the
            //chrome with it would not, leaving the player on a map with no travel buttons and no way
            //to create a family.
            if (switcher is UINeighbourhoodSwitcher hiddenSwitcher)
            {
                hiddenSwitcher.Visible = false;
            }
            else if (switcher != null)
            {
                //Anything else is a switcher from before the park existed, or one handed in by a
                //caller that owns it. Those are removed as they always were.
                _screen.Remove(switcher);
            }

            //The lot's own file is about to be rewritten by whatever the player does in there, and
            //the return trip re-reads it: NeighSelection -> RefreshGeneratedNeighbourhood diffs
            //every lot's house hash against the layout sidecar and re-places the ones that moved,
            //which is the same cheap per-lot repair a bulldoze does. Nothing is invalidated here and
            //nothing is regenerated: the world in memory is the very one this lot's surroundings
            //are about to be drawn from, and dropping it would make the lot load regenerate
            //512x512 tiles of a neighbourhood the player is not looking at.
        }

        /// <summary>
        /// Asks <see cref="LotTravelService"/> to bring <paramref name="house"/> up as a picker
        /// transition, which is the B3 path for entering a lot from the neighbourhood view.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A refusal is not an error the player sees: the reason goes to the log and the caller
        /// falls back to <c>InitializeLot</c>. The one refusal the service can issue after it has
        /// started - the destination having no house file - is exactly the case where the old
        /// direct call is no better, so nothing is lost by trying it; and the refusals issued
        /// before the first call on the host (no host, or a number that is not a lot) have left the
        /// lot state untouched, so the fallback is clean.
        /// </para>
        /// <para>
        /// <b>The host is the screen's own.</b> <c>_screen.TravelHost</c> is the same
        /// <see cref="ILotTravelHost"/> the screen's own <c>SwitchLot</c> block performs its switch
        /// on, so the picker path gets the real stash, the real per-lot store and the real transit
        /// writer rather than a second adapter built over this file's view of the screen's public
        /// members. That adapter existed while the screen's host was private, and it was not a
        /// harmless substitute: it could not marshal the lot being left, could not resume a lot the
        /// player had already stood on, and wrote no hop at all - so a lot entered from the picker
        /// could never be returned to the way it was left, while the same lot entered by travelling
        /// could. One host, one behaviour, whichever way the player arrives.
        /// </para>
        /// <para>
        /// Nothing about the picker's own situation changes by using it. The neighbourhood screen
        /// has no <c>VM</c>, so the host reports lot 0 and there is nothing to stash and no
        /// <c>SavedLots</c> entry to resume - the same numbers the old adapter reported, reached
        /// the same way. The destination is in any case the family's own house, which is the one
        /// lot <c>TryRestoreTravelSlot</c> declines to restore from. What is new is only what was
        /// missing: a hop from lot 0 to the picked lot is a hop, and the world's transit table and
        /// the provider mirror now hear about it.
        /// </para>
        /// </remarks>
        /// <param name="house">The lot number the player picked.</param>
        /// <returns>True when the lot was loaded, false when the caller should fall back.</returns>
        private bool TryTravelIntoPickedLot(short house)
        {
            try
            {
                string reason;
                if (LotTravel.TryTransition(
                    _screen.TravelHost,
                    new LotTransitionRequest(house, TransitionReason.Picker),
                    out reason))
                {
                    SimitoneLog.Info("B3 picker travel: lot " + house + " - " + reason + ".");
                    return true;
                }

                //"the switch did nothing" is the case most worth reading in a log, so the
                //service's own one-line account is passed through rather than replaced by a
                //second wording of it.
                SimitoneLog.Warn("B3 picker travel did not load lot " + house + ", so the lot is "
                    + "being opened the way it was before: " + reason + ".");
                return false;
            }
            catch (Exception ex)
            {
                //The lot is not loaded yet at either point this can throw, so the fallback is a
                //clean lot load rather than a half-finished one - and the log says which path is
                //about to run, because "the log stops here" is the symptom otherwise.
                SimitoneLog.Warn("B3 picker travel threw for lot " + house + " (" + ex.Message
                    + "), so the lot is being opened the way it was before.");
                return false;
            }
        }

        /// <summary>
        /// Parks the neighbourhood view because the player has gone into a lot: the picker and its
        /// switcher stop drawing and stop taking mouse, their generated 3D world stops drawing but is
        /// not disposed, and nothing is refreshed or invalidated on the way out.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called by <c>TS1GameScreen.InitializeLot</c>, i.e. by every route into a lot rather than
        /// only by the one a player reaches from the map. Idempotent, and a no-op when there is no
        /// panel: a travel switch, a marshalled lot and a CAS-launched lot all pass through here
        /// with the panel long gone.
        /// </para>
        /// <para>
        /// <b>Removed, not parked - the connected-mechanics parking is reverted.</b> The rework
        /// parked the panel and kept its generated world resident across the lot load, so entering
        /// and leaving a lot did not rebuild the scene. That was the mode-merging glue the owner
        /// asked to be taken out: the two modes are independent again, so entering a lot tears the
        /// neighbourhood view down and leaving it builds a fresh one, which is exactly what the
        /// game did before the rework. The panel's <c>Removed</c> disposes the scene, so the lot
        /// load never holds a reference to the neighbourhood's renderer or world.
        /// </para>
        /// </remarks>
        public void SuspendNeighbourhoodForLot()
        {
            var decision = NeighbourhoodLotEntry.For(lotLoaded: true);
            var hadWorld = false;
            var panel = _screen.TS1NeighPanel;
            if (panel != null)
            {
                //Unsubscribed rather than deleted: the handler is this service's, and a travel switch
                //or a marshalled lot can arrive with the panel already gone, in which case there is
                //nothing to unsubscribe from and the subscription is gone with it.
                if (!panel.IsSuspendedForLot) panel.OnHouseSelect -= OnHouseSelect;
                hadWorld = panel.HasGeneratedWorld;
                panel.SuspendForLotEntry();
                //Take it out of the screen, which disposes the generated world with it - see the
                //type remarks. Leaving it parked would keep a neighbourhood nobody is looking at
                //resident in the scene layer for the rest of the session.
                _screen.Remove(panel);
                _screen.TS1NeighPanel = null;
            }

            if (_switcher != null)
            {
                _screen.Remove(_switcher);
                _switcher = null;
            }

            SimitoneLog.Info("The neighbourhood view was removed for a lot load ("
                + (hadWorld ? "its generated 3D world was torn down with it"
                    : "there was no generated 3D world attached")
                + "): " + decision.Reason + ".");
        }

        /// <summary>
        /// Buys <paramref name="house"/> for <paramref name="family"/> and plays it.
        /// </summary>
        /// <remarks>
        /// The neighbourhood is saved immediately after the purchase is charged and before the lot
        /// is loaded, so the lot cannot be lost - or charged twice - if anything goes wrong later
        /// in the session.
        /// </remarks>
        /// <param name="house">The lot number being bought.</param>
        /// <param name="family">The FAMI chunk id moving in.</param>
        /// <param name="switcher">The switcher to remove once the lot is up.</param>
        public void MoveInAndPlay(short house, int family, UIElement switcher)
        {
            _screen.MoveInFamily = null;
            var neigh = Content.Get().Neighborhood;
            var fami = neigh.GetFamily((ushort)family);
            neigh.SetFamilyForHouse(house, fami, true);
            //SetFamilyForHouse just charged the lot price out of fami.Budget in memory. commit
            //the neighbourhood now, before the lot is loaded, so the purchase cannot be lost -
            //or spent twice - if anything goes wrong later in the session.
            neigh.SaveNeighbourhood(false);
            PlayHouse(house, switcher);
        }

        /// <summary>
        /// Moves a family out of a lot, refunds what they are owed, bulldozes the lot and puts it
        /// back in the picker.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The order is the contract. The refund is worked out from the lot's SIMI <em>before</em>
        /// anything is bulldozed, so the family gets back the price they were charged on move-in
        /// rather than the price of the empty shell they are leaving behind. The lot itself is
        /// reset after <c>MoveOut</c> (so ownership is already released if the reset fails) and
        /// before the neighbourhood is written, so the file on disk never says "empty lot" while
        /// the neighbourhood still says "this house has a family in it".
        /// </para>
        ///
        /// <para>
        /// The reset is best effort. A failure to bulldoze costs the next family a lot full of
        /// somebody else's furniture; a failure to refund or to release the house costs the player
        /// money and a home. The order above, and the fact that <see cref="ResetHouse"/> never
        /// throws, is what keeps those two apart.
        /// </para>
        /// </remarks>
        /// <param name="family">The family leaving.</param>
        /// <param name="houseID">The lot number being vacated.</param>
        public void EvictLot(FAMI family, short houseID)
        {
            //refund the lot purchase price as well as the furniture value. move-in charged
            //SIMI.PurchaseValue against the budget; refunding only ValueInArch destroyed that
            //money on every eviction, so a family that moved house a few times went broke.
            var lotValue = GetLotPurchaseValue(houseID);
            family.Budget = LotAccounting.RefundOnEvict(family.Budget, family.ValueInArch, lotValue);
            family.ValueInArch = 0;
            Content.Get().Neighborhood.MoveOut(houseID);
            //and actually empty the house. without this the "move out" only ever moved the family
            //out of the neighbourhood database: the lot file kept the furniture, the objects value
            //and the lot price of the family that had just left, and the next family to pick the
            //lot moved straight into it.
            ResetHouse(houseID);
            //persist the refund and the now-empty lot before handing control back to the lot
            //picker, so a crash here cannot leave the family charged for a lot it no longer owns.
            Content.Get().Neighborhood.SaveNeighbourhood(false);
            //the lot's house file has just been rewritten, so both the facade that was built from
            //the old one and the generated ground and roads around it are now wrong. The generated
            //world is optional and may not even exist, so this is a request rather than an
            //assumption - and it names the lot, because §4.2's cheap invalidation is per-lot: only
            //this one is re-placed and the rest of the street keeps its arrangement.
            InvalidateGeneratedNeighbourhood(houseID, "the lot was bulldozed");
            _screen.TS1NeighPanel.SelectHouse(houseID);
        }

        /// <summary>
        /// Tells the neighbourhood view that the world it may have generated is out of date.
        ///
        /// <para>
        /// Called after anything that changes a lot's house file or its zoning <em>while the
        /// neighbourhood screen is up</em>: a bulldoze, a reset, a rezone. The generated world's
        /// terrain does not depend on most of those, but its lot facades do - they are rendered from
        /// the lot's own <c>HouseNN.iff</c> - and the panel is the thing that owns that cache, so it
        /// is the thing that has to be told. With the 3D neighbourhood switched off there is nothing
        /// to do and this is a null check.
        /// </para>
        /// <para>
        /// <b>Two halves, deliberately.</b> The panel's own world is dropped and rebuilt on the spot,
        /// because the player is looking at it. The shared generated-world cache is dropped too, but
        /// only when there was something in it: that cache is what the in-lot surroundings and the
        /// next visit to this neighbourhood read, and an eviction on a 2D neighbourhood must not
        /// leave a stale world lying in it either.
        /// </para>
        /// <para>
        /// <b>Entering a lot does not come through here.</b> It used to, and both halves were wrong
        /// for that case: the panel that owns the world is no longer on screen to be looked at, and
        /// the world in the cache is the very one the lot is about to be surrounded with. See
        /// <see cref="SuspendNeighbourhoodForLot"/>.
        /// </para>
        /// </summary>
        /// <param name="lotNumber">
        /// The lot that changed, or <c>null</c> when the whole neighbourhood did. Only used for the
        /// log line and for the per-lot wording; the cache is dropped per slot either way, because a
        /// raster is a function of the whole layout and there is nothing to salvage at tile level.
        /// </param>
        /// <param name="why">A short clause for the log, e.g. "the lot was rezoned".</param>
        public void InvalidateGeneratedNeighbourhood(short? lotNumber = null, string why = null)
        {
            if (lotNumber.HasValue)
            {
                NeighbourhoodLifecycle.InvalidateLot(
                    _screen.CurrentNeighborhoodMode, lotNumber.Value, why);
            }
            else
            {
                NeighbourhoodLifecycle.InvalidateSlot(_screen.CurrentNeighborhoodMode, why);
            }

            _screen.TS1NeighPanel?.InvalidateGeneratedWorld();
        }

        /// <summary>
        /// Bulldozes a vacated lot: everything the evicted family bought and every Sim in the house
        /// is removed from the lot's saved data, and the lot is written back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The lot's saved state lives in the <c>FSOv</c> chunk of its <c>House{NN}.iff</c>, and
        /// that chunk is the whole of what a family owns on a lot -
        /// <c>VMBlueprintRestoreCmd</c> looks for it first and loads it in preference to converting
        /// the original TS1 data. So an eviction is a matter of rewriting that chunk.
        /// </para>
        ///
        /// <para>
        /// What survives is decided by <see cref="LotEviction"/>, not here: the lot's fabric (walls,
        /// floors, doors, stairs, portals), the lot's infrastructure (mailbox, bin, phone) and the
        /// controller objects, the last of which are kept but wiped. What does not survive is the
        /// family: its Sims and everything in the buy catalog that they put on the lot.
        /// </para>
        ///
        /// <para>
        /// A lot with no <c>FSOv</c> chunk is left exactly as it is, and that is a deliberate gap
        /// rather than an oversight. No chunk means the lot has never been played or saved in
        /// Simitone, so its file is still the shipped TS1 template and there is nothing of the family
        /// that just left in it to take away. The only way to "empty" such a lot is to convert its
        /// original <c>OBJM</c> and filter the result, and that conversion needs a live VM to build
        /// against - which is exactly what an eviction, happening on the neighbourhood screen with
        /// no lot loaded, does not have. It is also not obviously the right thing to do: the
        /// furniture in that file is the furniture the lot was shipped with, not something the
        /// evicted family bought, and bulldozing it would be a change to the game content rather
        /// than to a family's house.
        /// </para>
        ///
        /// <para>
        /// Never throws. This runs from a button handler, after the family's money and house have
        /// already been dealt with, and a corrupt or unreadable lot file is a lost bulldoze rather
        /// than a lost eviction.
        /// </para>
        /// </remarks>
        /// <param name="houseID">The lot number to empty.</param>
        public void ResetHouse(int houseID)
        {
            try
            {
                var neigh = Content.Get().Neighborhood;
                var houseIff = neigh.GetHouse(houseID);

                var fsov = houseIff.Get<FSOV>(1);
                if (fsov == null || fsov.Data == null || fsov.Data.Length == 0)
                {
                    //No file was rewritten, so no HouseHash moved and there is nothing to
                    //invalidate. The caller's own invalidation still covers the neighbourhood
                    //database write that an eviction does, so there is nothing to add here.
                    SimitoneLog.Info("Lot " + houseID + " has no Simitone lot data, so there is nothing"
                        + " to bulldoze. The lot file was left as it is.");
                    return;
                }

                var marshal = new VMMarshal();
                using (var reader = new BinaryReader(new MemoryStream(fsov.Data)))
                {
                    marshal.Deserialize(reader);
                }

                if (marshal.Context == null || marshal.Entities == null)
                {
                    SimitoneLog.Warn("Lot " + houseID + " has lot data that is not a readable VMMarshal."
                        + " The lot file was left as it is - the house is still empty, but its"
                        + " contents could not be cleared.");
                    return;
                }

                marshal.GlobalState = LotEviction.ResetLotGlobals(marshal.GlobalState);

                var controllerGuids = ControllerGuids();
                var preservedIds = new HashSet<short>();
                var keptEntities = new List<VMEntityMarshal>();
                var keptThreads = new List<VMThreadMarshal>();
                int removed = 0;

                foreach (var entity in marshal.Entities)
                {
                    //resolve lazily and defensively: an object content does not know about cannot
                    //be classified, and LotEviction.Classify treats that as "remove".
                    OBJD objd = null;
                    try
                    {
                        objd = Content.Get().WorldObjects.Get(entity.GUID)?.OBJ;
                    }
                    catch (Exception)
                    {
                        //leave objd null - an unresolvable object is a removed object
                    }

                    var disposition = LotEviction.Classify(
                        entity.GUID,
                        entity is VMAvatarMarshal,
                        controllerGuids.Contains(entity.GUID),
                        IsOutOfWorld(entity),
                        objd != null,
                        objd?.BuildModeType ?? -1,
                        objd?.FunctionFlags ?? 0);

                    if (disposition == LotEviction.Disposition.Remove)
                    {
                        removed++;
                        continue;
                    }

                    if (disposition == LotEviction.Disposition.PreserveAndReset && entity.ObjectData != null)
                    {
                        //the controller stays, but the per-visit state the evicted family left on it
                        //("already visited" and friends) does not. Blanking the object data and the
                        //thread is what a vanilla OBJM load produces, with no EP0 ever run on it -
                        //and it is why the controllers are kept at all rather than being stripped
                        //and re-created, which would run EP0 and hand them the previous visit back.
                        Array.Clear(entity.ObjectData, 0, entity.ObjectData.Length);
                    }

                    preservedIds.Add(entity.ObjectID);
                    keptEntities.Add(entity);
                    //surviving objects are doors, infrastructure and controllers: none of them have
                    //business carrying a thread the evicted family left queued.
                    keptThreads.Add(LotEviction.CreateBlankThread());
                }

                marshal.Entities = keptEntities.ToArray();
                marshal.Threads = keptThreads.ToArray();

                //a multitile group is only kept whole: half a sofa is a group whose lead object no
                //longer exists.
                var keptGroups = new List<VMMultitileGroupMarshal>();
                if (marshal.MultitileGroups != null)
                {
                    foreach (var group in marshal.MultitileGroups)
                    {
                        if (group != null && LotEviction.KeepsGroup(group.Objects, preservedIds))
                        {
                            keptGroups.Add(group);
                        }
                    }
                }
                marshal.MultitileGroups = keptGroups.ToArray();

                //the old family's object ids are gone, so the lot's free-id counter has to be
                //recomputed rather than left where they left it.
                marshal.ObjectId = LotEviction.NextObjectId(preservedIds);

                ResetLotValues(marshal);

                var bytes = BuildResetHouseFileBytes(marshal, houseIff);
                var path = neigh.GetHousePath(houseID);
                //same stage / backup / commit the save path uses: an eviction must not be able to
                //leave a truncated House{NN}.iff behind it.
                var bakPath = AtomicFileCommit.Write(path, bytes);

                SimitoneLog.Info("Bulldozed lot " + houseID + ": removed " + removed + " of "
                    + (removed + keptEntities.Count) + " objects, kept " + keptEntities.Count
                    + " and " + keptGroups.Count + " multitile group(s), and wrote " + path
                    + (bakPath == null ? " (no previous lot file to back up)" : " (previous lot kept at " + bakPath + ")") + ".");

                //The lot file on disk is now a different length with a different write time, so
                //its HouseHash has moved and the generated world is stale. Said here rather than
                //only in EvictLot because ResetHouse is reachable on its own: the management panel
                //offers a bulldoze, and a bulldoze that forgot to invalidate would leave the
                //neighbourhood showing the house the player just emptied. EvictLot's own
                //invalidation still runs, and is idempotent - the second one finds nothing cached
                //and says so in one clause.
                InvalidateGeneratedNeighbourhood((short)houseID, "the lot was bulldozed");
            }
            catch (Exception ex)
            {
                //the family's money and house are already settled by the time this runs, so a lot
                //that cannot be bulldozed must not take the eviction down with it.
                SimitoneLog.Error("Could not bulldoze lot " + houseID
                    + ". The family has been refunded and the house released, but the lot still holds"
                    + " the previous family's contents.", ex);
            }
        }

        /// <summary>
        /// Moves a lot between the two zones TS1 has - residential and community - by rewriting
        /// that lot's line in <c>LotZoning.iff</c> and telling the neighbourhood about it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rule this implements: a lot with a family in it cannot be rezoned.</b> A lot's
        /// zone is what decides whether a family may move into it, so changing it underneath a
        /// household is a change that household never agreed to, and the game that this data comes
        /// from required the player to evict first. The player is told that rather than offered a
        /// second confirm that would do it anyway: the panel's own <b>Evict</b> button is the
        /// only way to empty a lot, and it is on the same panel.
        /// </para>
        ///
        /// <para>
        /// Which line may be rewritten, and when the answer is "none of them", is
        /// <see cref="LotZoning.Rezoned"/> - the pure decision, and the part worth testing. The
        /// short version is that only lots the file has a real zoning line for can be rezoned at
        /// all, and that a line the provider would ignore or overwrite with a later line is
        /// refused rather than written: a rezone that reverted on the next launch would be worse
        /// than no rezone, because the player would believe it.
        /// </para>
        ///
        /// <para>
        /// The order of the steps is the contract. The file is rewritten and committed before the
        /// in-memory dictionary is touched, so a failed write cannot leave the session showing a
        /// zone that the next launch will disagree with; and the dictionary is updated before
        /// <c>SaveNeighbourhood</c>, so anything the save path reaches sees the new zone.
        /// </para>
        /// </remarks>
        /// <param name="houseID">The lot to rezone.</param>
        /// <param name="zone"><see cref="LotZoning.ZoneCommunity"/> or
        /// <see cref="LotZoning.ZoneResidential"/>.</param>
        /// <returns>What the request actually did, for the panel to report.</returns>
        public RezoneLotResult RezoneLot(short houseID, short zone)
        {
            var neigh = Content.Get().Neighborhood;

            if (neigh.GetFamilyForHouse(houseID) != null)
            {
                SimitoneLog.Info("Refused to rezone lot " + houseID + ": a family still lives there."
                    + " A lot has to be evicted before it can be zoned.");
                return RezoneLotResult.Occupied;
            }

            var path = Path.Combine(neigh.UserPath, LotZoningFileName);
            IffFile iff;
            STR str;
            try
            {
                //a fresh read every time: the provider's own copy of this file was decoded once
                //when the neighbourhood loaded and is not kept, and the one being written is
                //committed over the top of the file this read came from.
                iff = new IffFile(path);
                str = iff.Get<STR>(1);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("Could not read " + path + ", so lot " + houseID
                    + " was not rezoned. " + ex.Message);
                return RezoneLotResult.ZoneFileMissing;
            }

            if (str == null)
            {
                SimitoneLog.Warn(path + " has no STR chunk 1, so lot " + houseID
                    + " was not rezoned.");
                return RezoneLotResult.ZoneFileMissing;
            }

            var lines = ReadZoningLines(str);
            var plan = LotZoning.Rezoned(lines, houseID, zone, false);
            switch (plan.Decision)
            {
                case LotZoning.RezoneDecision.Occupied:
                    //unreachable: the family check above is the same rule, and it is done first so
                    //that an occupied lot never reads the file.
                    return RezoneLotResult.Occupied;
                case LotZoning.RezoneDecision.NoZoningEntry:
                    SimitoneLog.Info("Refused to rezone lot " + houseID + ": " + path
                        + " has no zoning line for it. Lots the file does not name cannot be"
                        + " rezoned from inside the game.");
                    return RezoneLotResult.NoZoningEntry;
                case LotZoning.RezoneDecision.ShadowedEntry:
                    SimitoneLog.Info("Refused to rezone lot " + houseID + ": a later line in " + path
                        + " also names that lot, so the zoning line is not the one the game reads."
                        + " Rewriting it would be undone the next time the neighbourhood loads.");
                    return RezoneLotResult.ShadowedEntry;
                case LotZoning.RezoneDecision.UnknownZone:
                    SimitoneLog.Warn("Refused to rezone lot " + houseID + " to zone " + zone
                        + ": the zoning file only holds residential (0) and community (1).");
                    return RezoneLotResult.UnknownZone;
                case LotZoning.RezoneDecision.Unchanged:
                    SimitoneLog.Info("Lot " + houseID + " is already "
                        + LotZoning.ZoneToken(plan.Zone).Trim() + "; nothing to rezone.");
                    return RezoneLotResult.AlreadyThatZone;
            }

            //the switch above maps every outcome that does not write. A decision added later
            //without one would fall through to here, and a plan with no line to write must never
            //reach the writer.
            if (!plan.WritesFile)
            {
                SimitoneLog.Error("Rezoning lot " + houseID + " came out as " + plan.Decision
                    + ", which is not mapped to a result. The zoning file was left as it is.");
                return RezoneLotResult.WriteFailed;
            }

            //apply the plan. SetString is only called where the line actually differs, so the
            //other 999 lines - which include the map's camera coordinates - are never rewritten,
            //and a line that came back null from the read is never handed back to the writer.
            var rewritten = plan.ApplyTo(lines);
            for (int i = 0; i < rewritten.Length; i++)
            {
                if (!string.Equals(rewritten[i], lines[i], StringComparison.Ordinal))
                {
                    str.SetString(i, rewritten[i]);
                }
            }

            string backup;
            try
            {
                byte[] bytes;
                using (var stream = new MemoryStream())
                {
                    iff.Write(stream);
                    bytes = stream.ToArray();
                }
                //staged / backed up / committed rather than opened for writing in place, for the
                //same reason an eviction's bulldoze is: this is a player save, and a half-written
                // LotZoning.iff would cost the player the zoning of the whole neighbourhood.
                backup = AtomicFileCommit.Write(path, bytes);
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Could not write " + path + "; lot " + houseID
                    + " has not been rezoned and no other lot was touched. " + ex.Message, ex);
                return RezoneLotResult.WriteFailed;
            }

            //the file is on disk; now tell the running session. The panel reads this dictionary
            //for the lot's own caption, and UIMainPanel reads it for the buy catalog's lot type,
            //so leaving it stale would make the game disagree with itself until the next launch.
            neigh.ZoningDictionary[houseID] = plan.Zone;
            //zoning does not live in Neighborhood.iff - that file is already committed - but the
            //neighbourhood database is the save unit every other mutation in this service commits
            //through, and a rezone is a change to the neighbourhood the same way an eviction is, so
            //it is closed out the same way.
            neigh.SaveNeighbourhood(false);

            //a rezone changes which zone's facades should be shown, and the generated world's
            //layout is packed from the zoning, so both are now a version behind. Named per lot
            //because that is the granularity §4.2's cheap invalidation works at: this one lot is
            //re-placed, and the rest of the neighbourhood keeps the arrangement the player knows.
            InvalidateGeneratedNeighbourhood(houseID, "the lot was rezoned");

            SimitoneLog.Info("Rezoned lot " + houseID + " as " + LotZoning.ZoneToken(plan.Zone).Trim()
                + " (line " + plan.Index + " of " + path + " became \"" + plan.Line + "\""
                + (backup == null ? ", no previous file to back up)" : ", previous file kept at " + backup + ")") + ".");
            return RezoneLotResult.Rezoned;
        }

        /// <summary>
        /// The zoning table's file name inside the user data folder. See
        /// <c>TS1NeighborhoodProvider.InitSpecific</c>, which is the only thing that reads it.
        /// </summary>
        private const string LotZoningFileName = "LotZoning.iff";

        /// <summary>
        /// The zoning chunk's lines, in file order.
        /// </summary>
        /// <remarks>
        /// A line can come back null (an empty entry in the chunk), which every reader here
        /// tolerates and the writer never gets to see: only a line that differs is written back.
        /// </remarks>
        private static string[] ReadZoningLines(STR str)
        {
            var lines = new string[str.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = str.GetString(i);
            }
            return lines;
        }

        /// <summary>
        /// The GUIDs of the lot's controller objects - the ones that must be kept on an eviction but
        /// must not keep the state the last family left on them.
        /// </summary>
        /// <remarks>
        /// Content keeps this as a plain list of references, and a provider that has not been
        /// through the TS1 loader leaves it empty, which simply means "no controllers to classify"
        /// rather than an error.
        /// </remarks>
        private static HashSet<uint> ControllerGuids()
        {
            var result = new HashSet<uint>();
            var controllers = Content.Get().WorldObjects?.ControllerObjects;
            if (controllers == null) return result;

            foreach (var controller in controllers)
            {
                result.Add((uint)controller.ID);
            }
            return result;
        }

        /// <summary>
        /// Whether a saved entity is parked out of bounds. Matches
        /// <c>LotTilePos.OUT_OF_WORLD</c>'s x coordinate, which is how the IFF converter encodes
        /// "this object exists but is not on the lot".
        /// </summary>
        /// <remarks>
        /// No null check, because <c>VMEntityMarshal.Position</c> is a <c>LotTilePos</c> - a
        /// struct, and a struct field is never null. There used to be an
        /// <c>entity.Position != null</c> half here, which C# lifts and which is therefore
        /// always true: a guard that reads as protection and protects nothing, and which would
        /// silently become a real check (and a compile error nobody would expect) if the field
        /// were ever made nullable.
        /// </remarks>
        private static bool IsOutOfWorld(VMEntityMarshal entity)
        {
            return entity.Position.x == short.MinValue;
        }

        /// <summary>
        /// Restamps the lot's values chunk for a family that has just left: no furniture value, the
        /// architecture worth only what is still standing, no budget history, and no family
        /// recorded against the lot.
        /// </summary>
        /// <remarks>
        /// The lot's purchase price is derived from these three numbers
        /// (<c>SIMI.PurchaseValue</c>), so leaving the old ones in place is what makes the next
        /// family pay for the last family's furniture.
        /// </remarks>
        /// <param name="marshal">The lot state being rebuilt.</param>
        private static void ResetLotValues(VMMarshal marshal)
        {
            var lotState = marshal.PlatformState as VMTS1LotState;
            if (lotState == null) return;

            //the family is gone, so the lot no longer belongs to one. this is transient state that
            //is re-established by whoever loads the lot next (see InitializeLot), and leaving a
            //stale family here is what makes VerifyFamily try to spawn Sims for a family that has
            //moved house.
            lotState.CurrentFamily = null;

            var simi = lotState.SimulationInfo;
            if (simi == null) return;

            simi.ObjectsValue = 0;
            //SIMI.Write hardcodes 0x3E while taking the item count from this field, so a lot left
            //at any other version writes a chunk that disagrees with itself.
            simi.Version = LotEviction.NormalizedSimiVersion;

            //the walls and floors are kept - an eviction empties a house, it does not flatten it -
            //so the architecture value is simply re-read from the architecture that is staying.
            //Nothing that contributes to it is ever removed: the build-mode objects that make up
            //the rest of it (doors, windows, stairs) are all preserved by the filter, which is why
            //this is the architecture's value on its own and not a sum with the surviving groups.
            simi.ArchitectureValue = LotEviction.ArchitectureValueAfterEvict(
                PriceLotArchitecture(marshal.Context?.Architecture), 0);

            //the budget window is the evicted family's week. zeroing the "valid" flag is how SIMI
            //itself stores an empty day, and it is read back as a set of zeroes.
            if (simi.BudgetDays != null)
            {
                foreach (var day in simi.BudgetDays)
                {
                    if (day != null) day.Valid = 0;
                }
            }
        }

        /// <summary>
        /// The value of the walls and floors a lot is being left with, as the game itself prices
        /// them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>VMArchitectureStats.GetArchValue</c> walks a live <c>VMArchitecture</c>, and the only
        /// way to get one from saved lot data is through a <c>VMContext</c> and a
        /// <c>Blueprint</c> - i.e. a running world, which an eviction on the neighbourhood screen
        /// does not have. So the walk is done over a throwaway architecture carrying the saved lot's
        /// own wall, floor and buildable-area arrays.
        /// </para>
        /// <para>
        /// That is deliberately not a re-implementation: the floor, wall and pattern prices come from
        /// content, and they change, so a local copy of the walk would quietly go stale. Only the
        /// container is built here. It is discarded as soon as the value is read.
        /// </para>
        /// <para>
        /// A failure is reported as 0 rather than thrown. Understating what a lot is worth is a much
        /// smaller problem than stranding an eviction part way through, and the failure is logged.
        /// </para>
        /// </remarks>
        /// <param name="saved">The lot's saved architecture, or <c>null</c>.</param>
        /// <returns>The walls-and-floors value, or 0 if it cannot be worked out.</returns>
        private static int PriceLotArchitecture(VMArchitectureMarshal saved)
        {
            if (saved == null) return 0;
            if (saved.Walls == null || saved.Floors == null || saved.FineBuildableArea == null) return 0;

            try
            {
                //no blueprint and no context: the pricing walk reads neither, and passing nulls keeps
                //the throwaway architecture from reaching into a world that is not there.
                var arch = new VMArchitecture(saved.Width, saved.Height, null, null)
                {
                    Stories = saved.Stories,
                    Walls = saved.Walls,
                    Floors = saved.Floors,
                    FineBuildableArea = saved.FineBuildableArea,
                };

                return VMArchitectureStats.GetArchValue(arch);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("Could not price the architecture of the lot being bulldozed; "
                    + "recording an architecture value of 0. " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Lays out the bulldozed <c>House{NN}.iff</c>: the rebuilt lot data, the restamped values
        /// chunk, and the lot's existing thumbnails.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two pictures are copied from the lot's own file rather than re-rendered. Rendering a
        /// thumbnail needs a <c>GraphicsDevice</c> and a live world, neither of which exists on the
        /// neighbourhood screen where an eviction happens, and the picture in the file is the only
        /// one available - so the next family inherits a thumbnail of the furniture the bulldoze
        /// just removed, which is a cosmetic staleness rather than a wrong lot.
        /// </para>
        ///
        /// <para>
        /// The thumbnails must be copied with their raw bytes intact. <c>IffFile</c> reads a chunk
        /// lazily and drops its <c>ChunkData</c> once it has been decoded, and a chunk with no
        /// <c>Write</c> of its own - <c>THMB</c> - is written back out of <c>OriginalData</c>, so
        /// the bytes are stashed before the copy asks for them.
        /// </para>
        ///
        /// <para>
        /// Chunks the game understands but that are not part of a Simitone lot save (the original
        /// TS1 <c>OBJM</c>/<c>ARRY</c> payload, for instance) are deliberately not carried over. A
        /// lot with an <c>FSOv</c> chunk never reads them, and the save path does not write them
        /// either, so keeping them would only make the file drift.
        /// </para>
        /// </remarks>
        /// <param name="marshal">The rebuilt lot state.</param>
        /// <param name="originalHouse">The lot file the state came from, read for its
        /// thumbnails.</param>
        /// <returns>The complete IFF file bytes, ready to be committed.</returns>
        public static byte[] BuildResetHouseFileBytes(VMMarshal marshal, IffFile originalHouse)
        {
            byte[] marshalBytes;
            using (var stream = new MemoryStream())
            {
                marshal.SerializeInto(new BinaryWriter(stream));
                marshalBytes = stream.ToArray();
            }

            var iff = new IffFile();

            var fsov = new FSOV();
            fsov.ChunkLabel = "Simitone Lot Data";
            fsov.ChunkID = 1;
            fsov.ChunkProcessed = true;
            fsov.ChunkType = "FSOV";
            fsov.AddedByPatch = true;
            fsov.Data = marshalBytes;
            iff.AddChunk(fsov);

            var lotState = marshal.PlatformState as VMTS1LotState;
            if (lotState?.SimulationInfo != null)
            {
                //IffFile.WriteChunk writes the label unconditionally and a null label takes the whole
                //write down. The values chunk normally arrives from a marshal, which gives it an
                //empty label; anything that has not been through a marshal may have none at all, and
                //an empty one is what the marshal path would have written anyway.
                if (lotState.SimulationInfo.ChunkLabel == null)
                {
                    lotState.SimulationInfo.ChunkLabel = string.Empty;
                }
                //the game addresses this chunk by id - the lot price is read with Get<SIMI>(1), and
                //the IFF load path looks for the same id - so the rebuilt file states it rather than
                //inheriting whatever the chunk happened to be carrying. A lot whose values chunk
                //landed at a different id would be a lot that prices at zero on the next move-in.
                lotState.SimulationInfo.ChunkID = 1;
                lotState.SimulationInfo.ChunkProcessed = true;
                lotState.SimulationInfo.AddedByPatch = true;
                iff.AddChunk(lotState.SimulationInfo);
            }

            if (originalHouse != null)
            {
                CopyChunksRaw<BMP>(originalHouse, iff);
                CopyChunksRaw<PNG>(originalHouse, iff);
                CopyChunksRaw<THMB>(originalHouse, iff);
            }

            using (var houseStream = new MemoryStream())
            {
                iff.Write(houseStream);
                return houseStream.ToArray();
            }
        }

        /// <summary>
        /// Copies every chunk of one kind from a lot file into a rebuilt one, keeping the raw bytes.
        /// </summary>
        /// <remarks>
        /// <c>IffFile.List&lt;T&gt;</c> matches on the chunk's exact type, not on assignability -
        /// which matters here, because <c>PNG</c> derives from <c>BMP</c> and asking for the BMPs
        /// must not also hand back the PNGs, or the two calls would add every thumbnail twice and
        /// collide on its chunk id.
        /// </remarks>
        /// <typeparam name="T">The chunk type to carry over.</typeparam>
        /// <param name="source">The lot file to read from.</param>
        /// <param name="dest">The rebuilt lot file to add the chunks to.</param>
        private static void CopyChunksRaw<T>(IffFile source, IffFile dest) where T : IffChunk
        {
            //stash the bytes before asking for the chunks. Listing them decodes each one and then
            //throws its raw data away, and a chunk with no Write of its own (THMB) is written back
            //out of OriginalData - which is null unless something put it there.
            foreach (var chunk in source.SilentListAll())
            {
                if (chunk.ChunkData != null) chunk.OriginalData = chunk.ChunkData;
            }

            var chunks = source.List<T>();
            if (chunks == null) return; //the lot has no chunk of this kind

            foreach (var chunk in chunks)
            {
                dest.AddChunk(chunk);
            }
        }

        /// <summary>
        /// Reads a lot's purchase price from its SIMI chunk, reporting 0 for a lot with no SIMI
        /// chunk, no house file, or an unreadable house file. IffFile's path constructor throws
        /// when the file is missing, and an eviction must not fail halfway through with the
        /// family's budget already half-refunded.
        /// </summary>
        /// <param name="houseID">The lot number to price.</param>
        public static int GetLotPurchaseValue(short houseID)
        {
            try
            {
                return Content.Get().Neighborhood.GetHouse(houseID)?.Get<SIMI>(1)?.PurchaseValue ?? 0;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("Could not read the purchase value of lot " + houseID
                    + "; evicting with a 0 lot refund. " + ex.Message);
                return 0;
            }
        }
    }

    /// <summary>
    /// What entering a lot does to the generated neighbourhood, as a value, decided without a
    /// screen, a panel, a scene layer or a disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug this exists for: a player clicked a house, and the neighbourhood's 3D world was
    /// dropped as stale and rebuilt - on top of the lot. The world lives in
    /// <c>GameFacade.Scenes</c>, the lot view draws in the same layer, and the panel that owned the
    /// world had by then been removed from the screen, so nothing in the session was left that
    /// could ever take it down. The player was left with an immovable map over a lot they were
    /// standing in.
    /// </para>
    /// <para>
    /// So entering a lot <em>removes</em> the view, and does not refresh it. Three of the four
    /// answers below are therefore the same in every case, and the fourth is the one that used to be
    /// wrong: no generation and no invalidation while a lot is up. The stale-lot repair the
    /// invalidation was standing in for is done by the return trip, which diffs every lot's house
    /// hash against the layout sidecar and re-places only the ones that moved.
    /// </para>
    /// </remarks>
    public readonly struct NeighbourhoodLotEntry
    {
        private NeighbourhoodLotEntry(
            bool detachPanel, bool removeScene, bool refresh, bool invalidate, string reason)
        {
            DetachesPanel = detachPanel;
            RemovesScene = removeScene;
            RefreshesGeneratedWorld = refresh;
            InvalidatesGeneratedWorld = invalidate;
            Reason = reason;
        }

        /// <summary>
        /// The neighbourhood panel and its switcher leave the screen, and the panel stops being able
        /// to attach a world at all. Always true for a lot load.
        /// </summary>
        public bool DetachesPanel { get; }

        /// <summary>
        /// The panel's <c>Neighbourhood3DView</c> and its <c>NeighbourhoodWorldProxy</c> are removed
        /// from <c>GameFacade.Scenes</c>. Always true for a lot load, and the part a player sees.
        /// </summary>
        public bool RemovesScene { get; }

        /// <summary>
        /// Whether the generated world is rebuilt or regenerated on the way into the lot. Always
        /// false: the view is being left, not refreshed.
        /// </summary>
        public bool RefreshesGeneratedWorld { get; }

        /// <summary>
        /// Whether the shared generated world is dropped for being stale. Always false: it is what
        /// the lot's own surroundings are drawn from, and the return trip re-reads the lot.
        /// </summary>
        public bool InvalidatesGeneratedWorld { get; }

        /// <summary>One sentence saying what was decided, for the log. Never null or blank.</summary>
        public string Reason { get; }

        /// <summary>
        /// The decision for a lot load, and the decision for not being in one.
        /// </summary>
        /// <param name="lotLoaded">
        /// Whether a lot is up. False is the shape the same rule takes on the neighbourhood screen,
        /// where nothing is detached because there is nothing to detach.
        /// </param>
        public static NeighbourhoodLotEntry For(bool lotLoaded)
        {
            if (lotLoaded)
            {
                return new NeighbourhoodLotEntry(true, true, false, false,
                    "a lot load removes the neighbourhood view; it does not refresh it, and nothing"
                    + " is regenerated while the lot is up");
            }
            return new NeighbourhoodLotEntry(false, false, true, false,
                "the neighbourhood screen is up, so nothing is torn down and the world is brought up"
                + " to date instead");
        }

        public override string ToString()
        {
            return "detach the panel: " + DetachesPanel
                + ", remove the scene: " + RemovesScene
                + ", refresh: " + RefreshesGeneratedWorld
                + ", invalidate: " + InvalidatesGeneratedWorld
                + " (" + Reason + ")";
        }
    }

    /// <remarks>
    /// One value per answer the panel has to give the player, so a refusal is never reported as a
    /// success. The pure part of the decision - which of these the data calls for - is
    /// <c>LotZoning.RezoneDecision</c>; the two file outcomes exist here rather than there
    /// because <c>LotZoning</c> never touches the disk.
    /// </remarks>
    public enum RezoneLotResult
    {
        /// <summary>The zoning line was rewritten, committed, and the session told.</summary>
        Rezoned,

        /// <summary>The lot already had the requested zone. Nothing was written.</summary>
        AlreadyThatZone,

        /// <summary>A family lives on the lot, so it cannot be rezoned. See the rule on
        /// <see cref="NeighborhoodService.RezoneLot"/>.</summary>
        Occupied,

        /// <summary>The zoning file has no line for this lot, so there is nothing to rewrite.</summary>
        NoZoningEntry,

        /// <summary>A later line in the same file also names this lot and wins, so a rewrite of
        /// the zoning line would not survive a reload.</summary>
        ShadowedEntry,

        /// <summary>The requested zone is not a value the zoning file can hold.</summary>
        UnknownZone,

        /// <summary>The zoning file is missing or unreadable, so nothing was attempted.</summary>
        ZoneFileMissing,

        /// <summary>
        /// The rewrite could not be committed. The file on disk is unchanged and so is the
        /// session's zoning, so the lot is exactly as it was.
        /// </summary>
        WriteFailed,
    }
}
