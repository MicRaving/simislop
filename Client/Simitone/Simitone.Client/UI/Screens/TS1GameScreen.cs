
using FSO.Client;
using FSO.Client.Debug;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Model;
using FSO.Common;
using FSO.Common.Rendering.Framework;
using FSO.Common.Utils;
using FSO.Content;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.RC;
using FSO.HIT;
using FSO.LotView;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Hollow;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.NetPlay.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.UI.Panels.WorldUI;
using Simitone.Client.UI.Services;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Screens
{
    public class TS1GameScreen : FSO.Client.UI.Framework.GameScreen
    {
        public bool Downtown;
        public bool Desktop = !FSOEnvironment.SoftwareKeyboard;

        public UILotControl LotControl { get; set; }
        public UISimitoneFrontend Frontend { get; set; }
        private FSO.LotView.World World;
        public FSO.SimAntics.VM vm { get; set; }
        public VMNetDriver Driver;
        public UISimitoneBg Bg;

        //for TS1 hybrid mode
        public UINeighborhoodSelectionPanel TS1NeighPanel;
        public FAMI ActiveFamily;

        /// <summary>
        /// The cosmetic weather for the lot being played: what the rain loop is doing, when the
        /// next thunder crack is due, whether the terrain is being held as snow, and the saved
        /// weather waiting for this lot's blueprint. See <see cref="LotWeatherState"/> - all of the
        /// decisions live there, and this screen only reads the engine's weather, asks the state
        /// what to do, and applies the answer.
        /// </summary>
        private readonly LotWeatherState Weather = new LotWeatherState();

        /// <summary>
        /// The neighbourhood the player's lot was picked on, as the id
        /// <c>UINeighborhoodSelectionPanel</c> and the switcher's buttons use (4 residential,
        /// 2 downtown, 3 vacation, 5 studio, 7 magic).
        ///
        /// <para>
        /// This is what "return to neighbourhood" opens. It used to be hard-coded to the
        /// residential map via <see cref="NeighSelectionMode.Normal"/>, which sent a player who had
        /// bought a lot in magic town, or picked a vacation or studio lot off its own map, back to
        /// Old Town - a map that has none of those lots on it. <see cref="PlayHouse"/> records the
        /// neighbourhood here from the lot number, and <see cref="ExitLot"/> reopens it.
        /// </para>
        ///
        /// <para>
        /// Only <see cref="PlayHouse"/> writes it, i.e. the neighbourhood picker and the move-in
        /// flow. A lot reached by travelling (<see cref="VMLotSwitch"/>) leaves it alone, so a
        /// session that starts on the residential map - or moves a family into a house there -
        /// still comes back to a map with the family's own house on it. It is deliberately not
        /// reset by <see cref="ExitLot"/> either: it describes where the current lot lives, and
        /// leaving the lot does not change that.
        /// </para>
        /// </summary>
        public ushort CurrentNeighborhoodMode = 4;

        public bool InLot
        {
            get
            {
                return (vm != null);
            }
        }

        private int m_ZoomLevel;

        /// <summary>
        /// The discrete lot zoom ladder (1 = near, 2 = medium, 3 = far), kept for the UI that
        /// addresses zoom in whole steps.
        ///
        /// Zoom has one authority: <see cref="UILotControl.TargetZoom"/>. The mouse wheel and
        /// the pinch gesture write it continuously, the desktop zoom buttons and zoom-hold
        /// write it, the 3D camera easing reads it, and <see cref="UILotControl.Update"/> is
        /// what turns it into camera state. This property is a view onto that field, not a
        /// second copy of it: getting it derives the level the target zoom currently resolves
        /// to (so the desktop zoom buttons grey out correctly after a wheel zoom too), and
        /// setting it writes the level's canonical target zoom back.
        ///
        /// Setting the level also keeps the lot-view visibility behaviour this property has
        /// always had - it shows the lot, hides the title-screen background and then hands the
        /// level to the lot control. With no lot loaded there is nothing to show, so the level
        /// is not recorded.
        /// </summary>
        public int ZoomLevel
        {
            get
            {
                if (LotControl == null) return m_ZoomLevel;
                return LotZoomMapping.DiscreteLevelFor(LotControl.TargetZoom);
            }
            set
            {
                value = Math.Max(1, Math.Min(3, value));

                if (value < 4)
                {
                    if (vm == null || LotControl == null)
                    {
                        //No lot to zoom, or a lot whose control has not been built (or has already
                        //been torn down) - CleanupLastWorld reaches here with a live vm and no
                        //control, and the getter above already treats that as "no lot to zoom".
                        return;
                    }
                    else
                    {
                        LotZoomMapping.Resolve2D(LotZoomMapping.TargetZoomForLevel(value), out WorldZoom targ, out float _);
                        //HITVM.Get().PlaySoundEvent(UIMusic.None);
                        LotControl.Visible = true;
                        Bg.Visible = false;
                        World.Visible = true;
                        //ucp.SetMode(UIUCP.UCPMode.LotMode);
                        LotControl.SetTargetZoom(targ);
                        if (m_ZoomLevel != value) World.InitiateSmoothZoom(targ);
                        World.State.Zoom = targ;
                        m_ZoomLevel = value;
                    }
                }
                else //cityrenderer! we'll need to recreate this if it doesn't exist...
                {
                    if (m_ZoomLevel < 4)
                    { //coming from lot view... snap zoom % to 0 or 1
                        if (World != null)
                        {
                            LotControl.Visible = false;
                        }
                    }
                    m_ZoomLevel = value;
                }
            }
        }

        /// <summary>
        /// Which way the lot is turned, as a quarter-turn index 0-3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Named for what it is rather than <c>Rotation</c>, which is the trap this used to be.
        /// <see cref="UIElement"/> declares <c>public virtual float Rotation</c> - a float, in
        /// radians, feeding the element's own draw matrix. A <c>public int Rotation</c> here
        /// therefore could not be an override, and it hid the base member: any call site that
        /// reached it through a <see cref="UIElement"/>-typed reference would have written the
        /// float and quietly skipped the re-centring and the world rotation below, so the lot
        /// would appear to rotate and then snap back. Renaming removes the ambiguity instead of
        /// papering over it with <c>new</c>, which would leave the two members equally reachable
        /// and equally divergent.
        /// </para>
        /// <para>
        /// Anything out of range falls back to 0 rather than being remembered: the world only
        /// has four rotations, and a value it cannot name is not a direction the lot can be in.
        /// </para>
        /// </remarks>
        private int _worldRotationStep = 0;
        public int WorldRotationStep
        {
            get
            {
                return _worldRotationStep;
            }
            set
            {
                _worldRotationStep = ((value % 4) + 4) % 4;
                World.State.CenterTile = World.EstTileAtPosWithScroll(new Vector2(ScreenWidth / 2, ScreenHeight / 2));
                if (World != null)
                {
                    switch (_worldRotationStep)
                    {
                        case 0:
                            World.State.Rotation = WorldRotation.TopLeft; break;
                        case 1:
                            World.State.Rotation = WorldRotation.TopRight; break;
                        case 2:
                            World.State.Rotation = WorldRotation.BottomRight; break;
                        case 3:
                            World.State.Rotation = WorldRotation.BottomLeft; break;
                    }
                }
                World.RestoreTerrainToCenterTile();
            }
        }

        public sbyte Level
        {
            get
            {
                if (World == null) return 1;
                else return World.State.Level;
            }
            set
            {
                if (World != null)
                {
                    World.State.Level = value;
                }
            }
        }

        public VMAvatar SelectedAvatar
        {
            get
            {
                return vm.GetAvatarByPersist(vm.MyUID);
            }
        }

        public TS1GameScreen(NeighSelectionMode mode) : base()
        {
            //the services are built first: NeighSelection below runs inside this constructor and
            //needs its neighbourhood service already in place.
            Dialogs = new DialogService();
            SaveService = new SaveService(Dialogs);
            FailedContentService = new FailedContentService(Dialogs);
            NeighborhoodService = new NeighborhoodService(this);
            LotLifecycleService = new LotLifecycleService(this);

            Bg = new UISimitoneBg();
            Bg.Position = (new Vector2(ScreenWidth, ScreenHeight)) / 2;
            Add(Bg);

            if (Content.Get().TS1)
            {
                NeighSelection(mode);
            }
        }
        public int? MoveInFamily;

        /// <summary>
        /// The screen's dialogs, and the single confirm latch they share. One instance per screen,
        /// so "a confirmation is already up" means exactly what it meant when this was a nullable
        /// <c>UIMobileAlert</c> field.
        /// </summary>
        public DialogService Dialogs { get; private set; }

        /// <summary>Lot saving. See <see cref="SaveService"/>.</summary>
        private SaveService SaveService { get; set; }

        /// <summary>
        /// The "these custom content files did not load" warning. See
        /// <see cref="FailedContentService"/>.
        /// </summary>
        private FailedContentService FailedContentService { get; set; }

        /// <summary>Neighbourhood lot picker, move-in and eviction. See <see cref="NeighborhoodService"/>.</summary>
        private NeighborhoodService NeighborhoodService { get; set; }

        /// <summary>Resuming a marshalled lot and resetting to a blueprint. See <see cref="LotLifecycleService"/>.</summary>
        private LotLifecycleService LotLifecycleService { get; set; }

        // -------------------------------------------------------------------------------------
        // The generated neighbourhood, seen from inside a lot. §7.4.
        //
        // The screen owns this because the lot view owns nothing else: World.Surroundings is a
        // field on FSO.LotView.World, it is set the moment the World is created and cleared when
        // the World is disposed, and the World is this screen's. Everything expensive - the
        // terrain mesh, the generated ground textures, the neighbouring lot facades - is owned by
        // the SimitoneSurroundings and goes away with it in CleanupLastWorld, so leaving a lot
        // gives the graphics device its buffers back.
        //
        // The generated world itself (a packed layout and a NeighbourhoodMap, no GPU resources)
        // is cached in a single slot. The neighbourhood screen generates the very same world from
        // the very same seed, so this is one world with two render entry points rather than two
        // worlds, and a player walking between two lots in one neighbourhood pays for generation
        // once. One slot, not a dictionary: the next milestone owns invalidation, and a cache
        // that can go stale is worse than a cache that cannot.
        // -------------------------------------------------------------------------------------

        /// <summary>The generated world the current lot's surroundings are drawn from, or null.</summary>
        private SimitoneSurroundings Surroundings;

        /// <summary>The generated world the last lot's surroundings were built from.</summary>
        private NeighbourhoodSceneSource SurroundingsWorld;

        /// <summary>
        /// The neighbourhood id <see cref="SurroundingsWorld"/> was generated for, or -1 when
        /// there is none. One slot, so this is what tells a cache hit from a cache miss.
        /// </summary>
        private int SurroundingsWorldSlot = -1;

        /// <summary>
        /// The lot number of the lot being brought up, set by whichever entry point knows it
        /// before <see cref="InitializeLot()"/> runs.
        /// </summary>
        /// <remarks>
        /// It has to be a field rather than a parameter because the surround is attached inside
        /// <see cref="InitializeLot()"/>, and at that point there is no VM to ask (the VM is
        /// built three lines later) and no blueprint to read a lot number out of. The two callers
        /// that know the number - a house path and the travel switch - set it first; a load that
        /// reaches <see cref="InitializeLot()"/> with neither simply does not get surroundings,
        /// which is the same answer as the setting being off.
        /// </remarks>
        private int? LoadingHouseNumber;

        /// <summary>
        /// Counts lot loads, so a generation that finishes after the lot it was asked for is gone
        /// attaches to nothing. Bumped by every <see cref="InitializeLot"/> and by
        /// <see cref="DetachSurroundings"/>.
        /// </summary>
        /// <remarks>
        /// A counter rather than a reference to the job because the job is the wrong thing to watch:
        /// a refresh for a slot outlives the lot that asked for it on purpose, and the world it
        /// produces belongs to the neighbourhood. What must not happen is a neighbourhood generated
        /// for one lot being attached to another, and the lot number plus this counter are enough to
        /// say that.
        /// </remarks>
        private int SurroundingsToken;

        /// <summary>
        /// The lot a generation in flight was asked for, or -1 when nothing is waiting. Its own field
        /// rather than <see cref="LoadingHouseNumber"/>, which the load consumes on the frame the
        /// generation is started.
        /// </summary>
        private int SurroundingsHouse = -1;

        /// <summary>
        /// The generation a lot is waiting for, kept so the job is not mistaken for a leak. The
        /// lifecycle owns the work and the shared cache owns the answer; this screen only has to
        /// hold the handle so the callback stays alive and the log can name it.
        /// </summary>
        private NeighbourhoodRefreshJob SurroundingsJob;

        /// <summary>
        /// The generated world for the lot being brought up, or the reason there is not one. This
        /// is the whole of the "build it once, hand it to both views" step, and it is exactly the
        /// sequence <c>UINeighbourhoodSelectionPanel.AttachWorld</c> runs for the same mode: the
        /// mode is the sidecar slot, the seed is derived from the slot, the layout comes from the
        /// sidecar or is packed, and the map is generated from the layout. Deterministic, so the
        /// two views agree without sharing an object.
        /// </summary>
        /// <summary>
        /// The generated world for the lot being brought up, requested from the lifecycle rather
        /// than generated here. Never throws and never blocks: on a cold neighbourhood it answers
        /// "still generating" immediately, and the finished world is handed to
        /// <see cref="AttachSurroundingsWhenReady"/> on the game thread.
        /// </summary>
        /// <remarks>
        /// The same sequence <c>UINeighborhoodSelectionPanel.AttachWorld</c> runs for the same mode -
        /// the mode is the sidecar slot, the seed is derived from the slot, the layout comes from the
        /// sidecar or is packed, and the map is generated from the layout - and it is deterministic,
        /// so the lot's surroundings and the neighbourhood map are the same neighbourhood rather
        /// than two that happen to agree.
        ///
        /// <para>
        /// A fresh content source per call, deliberately: <c>TS1LotContentSource</c> caches the lot
        /// records it has read in a Dictionary, and the worker this hands it to is not this thread -
        /// the same reason the panel builds one per call.
        /// </para>
        /// </remarks>
        private static NeighbourhoodGeneratedWorld RequestSurroundingsWorld(int slot)
        {
            try
            {
                return NeighbourhoodLifecycle.RefreshNow(
                    FSOEnvironment.UserDir, slot, slot, NeighbourhoodLifecycle.RequestedView,
                    new TS1LotContentSource(Content.Get().Neighborhood, slot));
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("The generated world for neighbourhood " + slot
                    + " could not be built, so the lot will be played without surroundings. See"
                    + " the log for the reason.", ex);
                return null;
            }
        }

        /// <summary>
        /// The settings' answer about lot surroundings, read once. Public so the settings read
        /// cannot drift between the gate and the late attach.
        /// </summary>
        private static SimitoneSurroundingsSelection SurroundingsSelection()
        {
            var preferences = GraphicsPreferences.From(GlobalSettings.Default);
            return SimitoneSurroundingsSelection.For(
                preferences.NeighbourhoodView, preferences.SurroundingLots);
        }

        /// <summary>
        /// Puts the generated neighbourhood around the lot, if the settings ask for it, as soon as
        /// there is a world to put there.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This used to generate the 512x512 map on the game thread, inside
        /// <see cref="InitializeLot"/>: twenty IFF reads, a relaxation pass and a quarter of a
        /// million tiles, before the lot's first frame - the twenty seconds a player reports as
        /// being "stuck in the final loading stage". It never generates now. A world already in
        /// memory for this slot is attached on this frame, because that costs a placement and a mesh
        /// and nothing else; otherwise the lifecycle is asked for one and the finished world is
        /// applied on the game thread, exactly as the neighbourhood screen has done since generation
        /// was moved onto a worker. The decision itself is
        /// <see cref="SimitoneSurroundingsAttachPlan"/>, so "the lot never waits for a generation"
        /// is an assertion rather than a comment.
        /// </para>
        /// <para>
        /// Every refusal leaves <c>World.Surroundings</c> null, which is the acceptance criterion
        /// for this whole feature: with the setting off (or the world unbuildable) a player gets a
        /// lot that is byte-for-byte the lot they got before - the only thing that runs is one ini
        /// read and two enum comparisons, and no layout, no map, no mesh and no facade is ever
        /// built. The setting now defaults to 3D, so the common case is the connected world; this
        /// paragraph is the guarantee that turning it off costs nothing.
        /// </para>
        /// <para>
        /// The gate itself is <see cref="SimitoneSurroundingsSelection"/>, and the generated world
        /// is only asked for once that gate is open, so the expensive half is downstream of the
        /// cheap half rather than the other way round.
        /// </para>
        /// </remarks>
        private void AttachSurroundings()
        {
            var selection = SurroundingsSelection();
            if (!selection.Enabled)
            {
                SimitoneLog.Info("Lot surroundings are off: " + selection.Reason + ".");
                return;
            }

            if (!LoadingHouseNumber.HasValue)
            {
                //No entry point knew the lot number - see LoadingHouseNumber. Nothing to place
                //the world against, and a guessed position would be worse than none.
                SimitoneLog.Info("Lot surroundings are off: the lot being loaded has no number this"
                    + " screen can place a generated world against.");
                return;
            }
            var house = LoadingHouseNumber.Value;
            var slot = SurroundingsSlotFor(house);

            //The world this screen is already holding, if it is the right one: the second lot of a
            //session in one neighbourhood costs no generation at all, which is the whole of the
            //"build it once, hand it to both views" arrangement.
            var generationPending = false;
            if (SurroundingsWorld == null || SurroundingsWorldSlot != slot)
            {
                var world = RequestSurroundingsWorld(slot);
                //A private source, not a shared one. The connected-mechanics rework handed the lot
                //the map view's own world through NeighbourhoodSharedWorld; the revert removes that
                //sharing so a lot load never touches the neighbourhood's renderer or world. The
                //surround gets its own clone, which is what the game did before the rework.
                SurroundingsWorld = world != null && world.HasWorld
                    ? new NeighbourhoodSceneSource(world.Layout, world.Map)
                    : null;
                SurroundingsWorldSlot = SurroundingsWorld == null ? -1 : slot;
                //a refresh this call started is already running, so the late attach joins it rather
                //than asking for another: one generation per slot, however many lots ask for it
                generationPending = world != null && world.Pending;
                if (SurroundingsWorld == null && !generationPending)
                {
                    //no world and nothing coming: a refusal, which is the lot as it is. Asking
                    //again on the next lot would be a second generation of a neighbourhood that has
                    //already said it cannot have one.
                    SimitoneLog.Warn("Lot " + house + " is played without a generated neighbourhood: "
                        + (world?.Failure ?? world?.Plan.Selection.Reason
                            ?? "the generated world could not be built") + ".");
                    return;
                }
            }

            var plan = SimitoneSurroundingsAttachPlan.Decide(selection, SurroundingsWorld != null);
            SimitoneLog.Info("Lot " + house + " surroundings: " + plan + ".");
            if (plan.AttachesNow)
            {
                AttachSurroundingsNow(house, selection);
                return;
            }
            if (plan.IsAsynchronous && generationPending) AttachSurroundingsWhenReady(house, slot);
        }

        /// <summary>
        /// Attaches the surround from a world that is already in memory. Never throws: a surround
        /// that will not build is a reason in the log, not a failed lot load.
        /// </summary>
        /// <param name="house">The lot to surround.</param>
        /// <param name="selection">The settings' answer, carried so the log line quotes it.</param>
        private void AttachSurroundingsNow(int house, SimitoneSurroundingsSelection selection)
        {
            //the settings are asked again here because the late attach can be seconds after the lot
            //came up, and a player who has since switched the 3D neighbourhood off must not get it
            //attached anyway
            if (!selection.Enabled)
            {
                SimitoneLog.Info("Lot " + house + " is played without a generated neighbourhood: "
                    + selection.Reason + ".");
                return;
            }
            if (SurroundingsWorld == null) return;

            //TryCreate is the authority on whether this can be built at all - it refuses a missing
            //device, a missing map and a lot the layout does not contain, and hands back the
            //sentence for whichever of those it was. AfterPlacement below only supplies the log
            //line, from the same placement, so the two can never disagree.
            string reason;
            var surroundings = SimitoneSurroundings.TryCreate(
                GameFacade.GraphicsDevice, SurroundingsWorld, house, out reason);
            if (surroundings == null)
            {
                SimitoneLog.Warn("Lot " + house + " is played without a generated neighbourhood: "
                    + (reason ?? "it could not be prepared") + ".");
                return;
            }

            SimitoneSurroundingsPlacement placement;
            string placementReason;
            SimitoneSurroundingsPlacement.TryFor(SurroundingsWorld.Layout, SurroundingsWorld.Map,
                house, out placement, out placementReason);
            selection = SimitoneSurroundingsSelection.AfterPlacement(selection, placement, placementReason);

            Surroundings = surroundings;
            if (World != null) World.Surroundings = surroundings;

            //The lot keeps BaseAlt at zero, so its ground is at its own absolute altitude and the
            //surround (measured down to the lot's ground) is lifted to meet it. Done here for the
            //first frame and again every frame in UpdateSurroundings, which picks up a lot load or
            //a save reload that replaces the altitude array.
            surroundings.FollowLotGround(vm?.Context?.Blueprint);

            SimitoneLog.Info("Lot " + house + " is surrounded by the generated neighbourhood: "
                + selection.Reason + " (" + surroundings.Placement + ").");

            //B2, asked again now that the placement exists. A cold neighbourhood reaches the lot
            //load with a generation in flight, so every call the load paths made above found no
            //placement to anchor a plan to and declined without saying anything. The blueprint is
            //very likely there by now too, so this is normally the frame the neighbours are built
            //on rather than the pump's. One attempt per lot either way - see
            //AttachHollowNeighbours.
            AttachHollowNeighbours();
        }

        /// <summary>
        /// Asks the lifecycle for a world on a worker, and attaches the surround when it lands on
        /// the game thread. The lot is up and playable throughout; the horizon fills in afterwards.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why the job is kept.</b> It outlives this call by as long as a generation takes, and
        /// the lot it was asked for may not: a travel switch can bring a different lot up in the
        /// meantime, and the finished world belongs to the neighbourhood rather than to the load
        /// that asked for it. So the callback re-checks the token
        /// (<see cref="SurroundingsToken"/>) and the lot number and does nothing at all if either has
        /// moved on, which is what stops a neighbourhood generated for one lot being attached to
        /// another. The world itself is kept either way, because the next lot in the same
        /// neighbourhood is going to want it.
        /// </para>
        /// </remarks>
        /// <param name="house">The lot being brought up.</param>
        /// <param name="slot">The neighbourhood slot, which is also the cache's and the sidecar's.</param>
        private void AttachSurroundingsWhenReady(int house, int slot)
        {
            var token = SurroundingsToken;
            //its own record of which lot is waiting, because LoadingHouseNumber is consumed by the
            //load that set it: a generation that lands seconds later has to be able to say "lot 68
            //asked for this" without asking a field that has been nulled since
            SurroundingsHouse = house;
            SurroundingsJob = NeighbourhoodLifecycle.Ensure(
                FSOEnvironment.UserDir, slot, slot, NeighbourhoodLifecycle.RequestedView,
                () => new TS1LotContentSource(Content.Get().Neighborhood, slot),
                world =>
                {
                    if (world == null || !world.HasWorld) return;
                    //A private source, not the map view's shared one - see AttachSurroundings. The
                    //two modes are independent, so the lot never borrows the neighbourhood's world.
                    //Kept when it already is this neighbourhood's, so a landing does not replace a
                    //surround source that a lot is already drawn from.
                    if (SurroundingsWorld == null || SurroundingsWorldSlot != slot)
                    {
                        SurroundingsWorld = new NeighbourhoodSceneSource(world.Layout, world.Map);
                        SurroundingsWorldSlot = slot;
                    }
                    if (token != SurroundingsToken) return;
                    if (SurroundingsHouse != house) return;
                    if (World == null || Surroundings != null) return;
                    SurroundingsHouse = -1;
                    SimitoneLog.Info("The generated world for neighbourhood " + slot + " has landed,"
                        + " so lot " + house + " is being surrounded by it now.");
                    AttachSurroundingsNow(house, SurroundingsSelection());
                });
        }

        /// <summary>
        /// Takes the generated neighbourhood off the lot and frees everything it held.
        /// </summary>
        /// <remarks>
        /// Called from <see cref="CleanupLastWorld"/> <em>before</em> its "no lot, nothing to do"
        /// early return, so it runs on every teardown rather than only the ones that had a VM.
        /// Clearing the field before disposing the surround is the order that matters: the lot
        /// view is disposed immediately afterwards and must not be able to call back into a
        /// surround whose buffers have already gone.
        /// </remarks>
        private void DetachSurroundings()
        {
            //bumped first, so a generation already in flight cannot attach itself to whatever lot
            //is up by the time it lands: the world it produces is kept (it is the neighbourhood's),
            //but nothing is hung on a lot that did not ask for it
            SurroundingsToken++;
            SurroundingsHouse = -1;
            if (World != null) World.Surroundings = null;
            Surroundings?.Dispose();
            Surroundings = null;

            //B2's per-lot latches go with the surround, because both of them are about "the lot that
            //is up" and the lot that is up is gone. Cleared here rather than in the bridge itself so
            //that a lot which is torn down mid-attach and the next lot which is brought up cannot
            //share an answer.
            HollowNeighboursAttached = false;
            HollowNeighboursWaitingLogged = false;
            ClearHollowTopUp();

            //And B8's map-screen line: the player is about to be sent back to the neighbourhood
            //view, so the next time they are standing on it the policy is worth saying again.
            MapScreenLightLogged = false;
        }

        /// <summary>
        /// The sidecar slot - and therefore the generated world - a lot number belongs to. The
        /// same table the neighbourhood screen opens a map with, so standing on a lot and looking
        /// at its map are two views of one neighbourhood rather than two neighbourhoods.
        /// </summary>
        private static int SurroundingsSlotFor(int houseNumber)
        {
            return SimitoneSurroundings.SlotForLot(houseNumber);
        }

        /// <summary>
        /// Per-frame upkeep for the generated neighbourhood: the lot's own light and the
        /// neighbouring house meshes that are still due.
        /// </summary>
        /// <remarks>
        /// The light is the lot's, not a constant - the same sun vector the lot's own terrain
        /// shades with, and the same outdoor colour the lot clears its background to, which is
        /// what <c>VMArchitecture.SetTimeOfDay</c> writes. That is §7.4's "the lot's fog/lighting":
        /// the fog arrives through the engine's own <c>DrawSurrounding</c> argument, and the light
        /// arrives here because the hook's signature has no room for it.
        /// </remarks>
        private void UpdateSurroundings()
        {
            if (Surroundings == null) return;
            Surroundings.FollowLotGround(vm?.Context?.Blueprint);
            var state = World?.State;
            if (state != null)
            {
                Surroundings.SetLighting(state.Light?.LightVec, state.OutsideColor);
            }
            Surroundings.Update();
        }

        // -------------------------------------------------------------------------------------
        // Part B: the neighbourhood world, behind the seven NeighbourhoodWorldFlags (§7).
        //
        // Every site below is inside an `if (NeighbourhoodWorldFlags.IsOn(...))`, and every one of
        // the seven flags defaults off. That is the entire contract of the flag table: with all
        // seven off this screen does exactly what it did before Part B, and the only work added is
        // one cached ini read per site. Nothing here is built in the constructor either, so a
        // session with no capability on allocates none of it - a world over an empty layout would
        // otherwise be a per-screen object that no code path could ever reach.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// How far past the player's own property line a neighbour may sit to be built as a real
        /// sub-world, in tiles of gap. <b>The drawn region's own radius</b>, so there is no band of
        /// facades between the real lots and the edge of the ground.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It used to be eight, and that was the whole complaint.</b> The surround draws
        /// <see cref="SimitoneSurroundingsPlacement.CullRadiusTiles"/> tiles of ground on every side
        /// of the property, so with a radius of eight a player standing in a middle lot saw two or
        /// three real houses, a ring of facades, and then real-looking ground with nothing on it -
        /// "some houses are real and some are cardboard". Two radii for two questions was a
        /// reasonable answer until the questions turned out to have the same answer: the ground is
        /// drawn because the lots on it are wanted, and the lots on it are wanted because the ground
        /// is drawn. So this is now the plan's constant rather than a literal here, and
        /// <see cref="SimitoneSurroundingsPlan.RadiusIsShared"/> is the assertion that the two
        /// numbers are one number.
        /// </para>
        /// <para>
        /// <b>Why forty tiles of ground is affordable.</b> It is not forty lots. The region spans
        /// the property plus the margin on every side - 144 tiles for a 64-tile TS1 lot - and at the
        /// packer's own two-tile margin between lots that is three plots across and three deep: eight
        /// neighbours, which is exactly what the radius of eight was reaching anyway, plus the row
        /// behind it. The extra thirty-odd tiles of ground were always being drawn and were always
        /// showing facades on top of it.
        /// </para>
        /// <para>
        /// <b>What still bounds it.</b> Not the radius -
        /// <see cref="HollowNeighbourBudget"/> is. See <see cref="SimitoneSurroundingsPlan.MaxSubWorlds"/>
        /// for what a sub-world costs and why the count is the thing to change when a machine is
        /// struggling, not the radius.
        /// </para>
        /// </remarks>
        private const int HollowNeighbourRadiusTiles = SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles;

        /// <summary>
        /// The most neighbouring lots this screen will stand up as real sub-worlds around the lot
        /// the player is in. See <see cref="SimitoneSurroundingsPlan.MaxSubWorlds"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A <see cref="FSO.LotView.Components.SubWorldComponent"/> is a <c>World</c>: its own world
        /// state, architecture, entities, room map and light map, walked every frame by the lot
        /// view's <c>PreDraw</c> and <c>SubDraw</c>, and every one of them built inside a single lot
        /// load by <c>SimitoneSurroundingsRestore.TryRestore</c>. So the budget is on the count, the
        /// order is nearest-first, and anything over the line is drawn as a facade rather than left
        /// as a hole - see <see cref="SimitoneSurroundingsPlan.ApplyBudget"/>.
        /// </para>
        /// <para>
        /// Nothing in this screen overrides it. It exists as a named constant at the call site
        /// because this is the one place that decides what a player's lot load is worth, and a
        /// number somebody has to find before they can change it is a number they will not change.
        /// </para>
        /// </remarks>
        private const int HollowNeighbourBudget = SimitoneSurroundingsPlan.MaxSubWorlds;

        /// <summary>
        /// Whether <see cref="AttachHollowNeighbours"/> has already had its one attempt at the lot
        /// that is up, so the bridge runs once per lot however many load paths and frames reach it.
        /// Cleared with the surround in <see cref="DetachSurroundings"/>.
        /// </summary>
        ///
        /// <remarks>
        /// The latch rather than a position in the load order, because the two facts the bridge
        /// needs - a blueprint and a generated placement - arrive on different frames and in no
        /// fixed order: a template load queues a blueprint restore as a VM command, a cold
        /// neighbourhood generates its placement on a worker, and a marshal resume runs the
        /// blueprint through the lifecycle. Every load path therefore asks the bridge, and the
        /// bridge answers at most once.
        /// </remarks>
        private bool HollowNeighboursAttached;

        /// <summary>
        /// Whether the "waiting for the lot's blueprint" line has already been written for the lot
        /// that is up, so a blueprint that a queued restore writes a few frames later is one line
        /// in the log rather than one line per frame. Cleared with the surround.
        /// </summary>
        private bool HollowNeighboursWaitingLogged;

        /// <summary>
        /// The neighbours the first restore built blank because its build budget ran out, and
        /// what becomes of each on later frames. Null when there is nothing deferred. Cleared with
        /// the surround.
        /// </summary>
        private SimitoneHollowTopUp HollowTopUp;

        /// <summary>The loader the first restore used, kept for its cache and the later builds.</summary>
        private NeighbourHollowLoader HollowTopUpLoader;

        /// <summary>The plan as the first restore resolved it, updated as neighbours are topped up.</summary>
        private SimitoneSurroundingsPlan HollowTopUpPlan;

        /// <summary>Frames the top-up has been pumped for, which is the clock its delays are in.</summary>
        private long HollowTopUpFrame;

        /// <summary>
        /// The one authority for this neighbourhood's time and transit table: the session object
        /// that holds <see cref="SimitoneNeighbourhoodWorld"/>, keyed by the neighbourhood slot of
        /// the lot being played. It is built from the neighbourhood lifecycle's own layout and map,
        /// not from the 3D surround, so it exists whenever a capability needs it and a lot with a
        /// known number is up - whatever the 2D/3D view setting says and whether or not any
        /// surround was ever attached. Empty when no lot is up.
        /// </summary>
        private readonly NeighbourhoodWorldSession WorldSession = new NeighbourhoodWorldSession();

        /// <summary>
        /// Every lot the player has left during this session and not yet gone back to, keyed by lot
        /// number: the one stash, which used to be a private dictionary of this screen's and is now
        /// the <see cref="LotStore"/> the world reads. It outlives any one world - lot numbers are
        /// global, and home -> downtown -> vacation crosses neighbourhoods - so the world is built
        /// over it rather than owning it.
        /// </summary>
        private readonly LotStore SessionLots = new LotStore();

        /// <summary>
        /// The lot being played, or 0 when none is up or its number is not known. Set by every lot
        /// load before anything asks for the world, and what the world's active lot follows.
        /// </summary>
        private int PlayedLot;

        /// <summary>The neighbourhood slot <see cref="PlayedLot"/> belongs to, or no slot.</summary>
        private int PlayedSlot
        {
            get
            {
                return PlayedLot > 0
                    ? SurroundingsSlotFor(PlayedLot)
                    : NeighbourhoodWorldSession.NoSlot;
            }
        }

        /// <summary>
        /// A lot load has named the lot now being played. A world held for another neighbourhood is
        /// saved and dropped, and the world, if there is one, is told which lot the player is in so
        /// the off-screen simulator leaves that lot alone.
        /// </summary>
        /// <param name="lot">The lot number, or 0 when the load named none.</param>
        private void SetPlayedLot(int lot)
        {
            PlayedLot = lot;
            var held = WorldSession.Slot;
            var ended = WorldSession.Retarget(PlayedSlot);
            if (ended != null)
            {
                SimitoneLog.Info("The neighbourhood world for slot " + held + " was dropped: the lot"
                    + " now being played (" + (lot > 0 ? lot.ToString() : "none") + ") is in another"
                    + " neighbourhood.");
                SaveNeighbourhoodWorld(ended, held);
            }

            var world = WorldSession.World;
            if (world != null) world.SetActiveLot(lot);
        }

        /// <summary>
        /// The player is leaving the lot for the neighbourhood screen: the lot's time goes to the
        /// world, the world's session is written, and the world is dropped along with the stash.
        /// Called before the lot is torn down, while there is still a clock to read.
        /// </summary>
        private void EndNeighbourhoodWorld()
        {
            var world = WorldSession.World;
            if (world != null)
            {
                var slot = WorldSession.Slot;
                try
                {
                    var lotClock = vm?.Context?.Clock;
                    if (lotClock != null) world.PullClockFrom(lotClock);
                    //not in any lot any more, so the session is saved as resting on the map
                    world.SetActiveLot(SimitoneNeighbourhoodWorld.NoActiveLot);
                }
                catch (Exception ex)
                {
                    SimitoneLog.Warn("The neighbourhood world could not take the lot's last reading"
                        + " on the way out, so it is saved as it was.", ex);
                }
                SaveNeighbourhoodWorld(world, slot);
                SimitoneLog.Info("The neighbourhood world for slot " + slot + " was dropped: the"
                    + " player left the lot (" + world + ").");
            }
            WorldSession.End();
            PlayedLot = 0;
        }

        /// <summary>
        /// Writes a world's session into its neighbourhood's sidecar (B7). Nothing happens unless
        /// the persist capability is on, and a failure is a log line: the lot has already been
        /// saved or left, and a session that did not reach the disk is not worth a dialog.
        /// </summary>
        private static void SaveNeighbourhoodWorld(SimitoneNeighbourhoodWorld world, int slot)
        {
            if (world == null || !NeighbourhoodLifecycle.PersistsWorldState) return;
            try
            {
                NeighbourhoodWorldPersistence.Save(FSOEnvironment.UserDir, slot, world);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("The neighbourhood world for slot " + slot + " could not be saved,"
                    + " so the stored session is the one from before.", ex);
            }
        }

        /// <summary>
        /// A lot has been taken out of the stash to be played again: forget it there, and tell the
        /// world, so the off-screen simulator starts its next absence from now.
        /// </summary>
        private void TakeStashedLot(short lot)
        {
            SessionLots.Remove(lot);
            WorldSession.World?.NoteLotRestored(lot);
        }

        /// <summary>
        /// B1's per-frame upkeep. While a lot is up its clock is the authority and the world
        /// follows it (see <see cref="SimitoneNeighbourhoodWorld.FollowLotClock"/>); the world ticks
        /// by wall-clock time only when there is no lot clock to follow.
        /// </summary>
        /// <remarks>
        /// Nothing here logs. This runs on every frame of every lot, so a log line would be a
        /// per-frame cost; the only lines this feature writes are the one-shot refusals.
        /// </remarks>
        /// <param name="deltaSeconds">How long this frame was.</param>
        private void UpdateNeighbourhoodWorld(float deltaSeconds)
        {
            //The capability is asked at the call site, next to the other three, so that all six
            //flags are read in one place a reader can see. This method therefore assumes it is on
            //and has one private caller.
            if (!TryGetNeighbourhoodWorld(out var world)) return;

            try
            {
                var lotClock = vm?.Context?.Clock;
                if (lotClock != null) world.FollowLotClock(lotClock);
                else world.Tick(deltaSeconds);
            }
            catch (Exception ex)
            {
                // A world that will not advance is dropped rather than retried every frame: a
                // neighbourhood whose time is stuck is a far smaller problem than a lot load that
                // throws sixty times a second. The slot stays refused until the session ends.
                var slot = WorldSession.Slot;
                WorldSession.End();
                WorldSession.Refuse(slot, "the world threw while advancing: " + ex.Message);
                SimitoneLog.Warn("The neighbourhood world could not be advanced, so this session's"
                    + " neighbourhood time is not being kept. The lot itself is unaffected.", ex);
            }
        }

        /// <summary>
        /// The neighbourhood world for the lot being played, asking the lifecycle for it the first
        /// time it is wanted for a slot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A cache hit builds the world on this call; a cold neighbourhood is generated on a worker
        /// and the world appears on the frame it lands, so this answers false until then. Every
        /// reason it is missing is logged once - see <see cref="RequestNeighbourhoodWorld"/>.
        /// </para>
        /// <para>
        /// Lazy, because the world constructs a <see cref="NeighbourhoodQuery"/> - which rasterises
        /// the layout and flood-fills its road network - and a session with every capability off
        /// must not pay for any of that: the capabilities ask, nothing else does.
        /// </para>
        /// </remarks>
        /// <param name="world">The world, or null when there is not one to use.</param>
        /// <returns>Whether a world was available.</returns>
        private bool TryGetNeighbourhoodWorld(out SimitoneNeighbourhoodWorld world)
        {
            world = WorldSession.World;
            if (world != null) return true;

            RequestNeighbourhoodWorld();
            world = WorldSession.World;
            return world != null;
        }

        /// <summary>
        /// Asks the lifecycle for the layout and map of the slot being played and builds the world
        /// from them, once per slot. The view is asked for as 3D whatever the setting says: the
        /// world needs a layout and a road map, and the 2D/3D choice is about what the map screen
        /// draws, not about whether the neighbourhood exists.
        /// </summary>
        private void RequestNeighbourhoodWorld()
        {
            if (vm == null) return;

            var slot = PlayedSlot;
            if (slot == NeighbourhoodWorldSession.NoSlot)
            {
                if (WorldSession.NoteUnavailable("the lot being played has no number"))
                {
                    SimitoneLog.Info("There is no neighbourhood world for this lot: it was loaded"
                        + " without a lot number, so there is no neighbourhood to place it in.");
                }
                return;
            }
            if (!WorldSession.NeedsRequest(slot)) return;
            WorldSession.MarkRequested(slot);
            var epoch = WorldSession.Epoch;

            NeighbourhoodGeneratedWorld generated;
            try
            {
                generated = NeighbourhoodLifecycle.RefreshNow(
                    FSOEnvironment.UserDir, slot, slot, NeighbourhoodView.World3D,
                    new TS1LotContentSource(Content.Get().Neighborhood, slot));
            }
            catch (Exception ex)
            {
                WorldSession.Refuse(slot, "the neighbourhood could not be generated: " + ex.Message);
                SimitoneLog.Warn("There is no neighbourhood world for slot " + slot + ": the layout"
                    + " could not be requested. The lot itself is unaffected.", ex);
                return;
            }

            if (generated != null && generated.HasWorld)
            {
                AdoptNeighbourhoodWorld(slot, generated, false);
                return;
            }
            if (generated == null || !generated.Pending)
            {
                var why = generated?.Failure ?? generated?.Plan.Selection.Reason
                    ?? "the generated world could not be built";
                if (WorldSession.Refuse(slot, why))
                {
                    SimitoneLog.Warn("There is no neighbourhood world for slot " + slot + ": " + why + ".");
                }
                return;
            }

            //Still being generated on a worker: join it, and build the world on the frame it lands.
            //The epoch says whether the answer is still wanted - leaving the lot or moving to
            //another neighbourhood in the meantime ends the request.
            NeighbourhoodLifecycle.Ensure(
                FSOEnvironment.UserDir, slot, slot, NeighbourhoodView.World3D,
                () => new TS1LotContentSource(Content.Get().Neighborhood, slot),
                landed =>
                {
                    if (epoch != WorldSession.Epoch) return;
                    if (landed == null || !landed.HasWorld)
                    {
                        var why = landed?.Failure ?? "the generation finished without a world";
                        if (WorldSession.Refuse(slot, why))
                        {
                            SimitoneLog.Warn("There is no neighbourhood world for slot " + slot + ": "
                                + why + ".");
                        }
                        return;
                    }
                    AdoptNeighbourhoodWorld(slot, landed, true);
                });
        }

        /// <summary>
        /// Builds the session's world from a finished generation, applies the stored session (B7)
        /// before anything reads the clock, and tells it which lot the player is in.
        /// </summary>
        /// <param name="slot">The neighbourhood slot.</param>
        /// <param name="generated">The generated world, which has a layout and a map.</param>
        /// <param name="seedFromLot">
        /// Whether to take the played lot's time now. True for a generation that landed after the
        /// lot was up - the lot is the authority from then on; false when the world is built inside
        /// the load itself, where the clock handshake that follows decides.
        /// </param>
        private void AdoptNeighbourhoodWorld(int slot, NeighbourhoodGeneratedWorld generated, bool seedFromLot)
        {
            SimitoneNeighbourhoodWorld replaced;
            bool created;
            var world = WorldSession.Adopt(slot, generated.Layout, generated.Map, SessionLots,
                out replaced, out created);
            if (replaced != null) SaveNeighbourhoodWorld(replaced, slot);
            if (!created) return;

            try
            {
                var restored = NeighbourhoodWorldPersistence.Restore(world, generated.WorldState);
                //after the stored session: it names whichever lot was active when it was saved
                world.SetActiveLot(PlayedLot);
                var lotClock = vm?.Context?.Clock;
                if (seedFromLot && lotClock != null) world.FollowLotClock(lotClock);
                SimitoneLog.Info("The neighbourhood world for slot " + slot + " is up, playing lot "
                    + PlayedLot + (restored ? ", stored session applied" : string.Empty) + " (" + world + ").");
            }
            catch (Exception ex)
            {
                //the world is built and usable; what failed is reading the stored session into it
                SimitoneLog.Warn("The neighbourhood world for slot " + slot + " is up, but the stored"
                    + " session could not be applied to it.", ex);
            }
        }

        /// <summary>
        /// How far the neighbourhood clock has to move before the live lot's outside colour is
        /// rewritten, as a fraction of a day. The rule now lives in
        /// <see cref="NeighbourhoodWorldLightApplyGate"/>, where it can be asked without a lot.
        /// </summary>
        private const double WorldLightApplyStep = NeighbourhoodWorldLightApplyGate.WorldLightApplyStep;

        /// <summary>
        /// Whether the "the map screen keeps its own light" line has been written for this visit to
        /// the neighbourhood view, so a screen the player is sitting on at midnight says it once.
        /// </summary>
        private bool MapScreenLightLogged;

        /// <summary>
        /// The time of day the live lot's outside colour was last written at, and whether the sun was
        /// up at the time. A latch rather than two bare fields, so that a lot load can reset it - see
        /// <see cref="DetachSurroundings"/> - and so the decision itself is the tested one.
        /// </summary>
        private NeighbourhoodWorldLightApplyGate LightingApplied;

        /// <summary>
        /// The time of day, as a fraction of a day, the lot view's own sun model is driven at.
        /// </summary>
        ///
        /// <remarks>
        /// <para>
        /// The engine's own expression, over the neighbourhood's clock: see
        /// <c>VMArchitecture.SetTimeOfDay()</c>, which does exactly this with a
        /// <c>VMClock</c>. While a lot is up the world follows that lot's clock minute by minute
        /// (<see cref="SimitoneNeighbourhoodWorld.FollowLotClock"/>), so the two agree to the minute
        /// and the world's clock is also the one every other lot in the neighbourhood is handed.
        /// </para>
        /// <para>
        /// <b>Read, never written into the lot.</b> This used to be handed straight to
        /// <c>VMArchitecture.SetTimeOfDay</c> on the live lot, several times a second, and that was
        /// the one thing on this path that reached into the engine's own seam on a lot that is being
        /// played: <see cref="UpdateNeighbourhoodWorldLighting"/> says what replaced it. It is still
        /// the right answer to "what time is it in this neighbourhood", which is the only question
        /// the outside colour and the sun-up decision actually ask.
        /// </para>
        /// </remarks>
        private static double DayFractionOf(WorldClock clock)
        {
            return clock.Hours / 24.0 + clock.Minutes / 1440.0 + clock.Seconds / 86400.0;
        }

        /// <summary>
        /// B8's per-frame upkeep: hand the lot view the world's light, so a lot in a neighbourhood
        /// is lit by the neighbourhood's clock rather than by a table keyed on the lot's own.
        /// Gated on the capability at the call site, and never applied to the map screen.
        /// </summary>
        ///
        /// <remarks>
        /// <para>
        /// <b>The map screen is not lit by the world clock, and this is the line that says so.</b>
        /// The neighbourhood view is a screen, not a lot: it has no VM, no clock and no lot view of
        /// its own, and it is on screen at exactly the moment a player is most likely to want to
        /// read it. A world clock that runs from sunrise to sunset therefore dims the map to an
        /// unreadable blue at dusk and keeps it there all night, which is the one thing a map
        /// must never do. So the application is <b>gated off for the map screen</b>: when the panel
        /// is up this returns without touching anything, the map keeps its own fixed, readable
        /// light, and the world's clock carries on running underneath for the lot the player is
        /// about to walk into. The branch is stated rather than inferred from
        /// <c>vm == null</c>, and it asks whether the neighbourhood panel is <em>on screen</em>
        /// rather than whether it exists. <c>NeighborhoodService.SuspendNeighbourhoodForLot</c>
        /// currently removes the panel on lot entry (the parking was reverted), so this is
        /// effectively a null check today, but a hidden-and-attached panel must still be lit.
        /// </para>
        /// <para>
        /// <b>It writes the outside colour, and nothing else.</b> This used to call
        /// <c>VMArchitecture.SetTimeOfDay</c> on the live lot several times a second, which was
        /// wrong twice over. It made this screen a <em>second writer</em> of fields the engine
        /// already owns and writes itself: <c>VMArchitecture.Tick</c> calls
        /// <c>SetTimeOfDay()</c> from the lot's own clock on <em>every tick</em>
        /// (<c>VMArchitecture.cs</c>, the last statement of that method), and this screen runs a few
        /// hundred lines earlier in the same frame - so the lot ended each frame lit by whichever of
        /// the two ran last, from two clocks that the §5.2 handshake seeds once and then lets drift
        /// apart. And it reached into the engine's seam from outside the VM's tick, at a moment of its
        /// own choosing, on the one object a player is editing on: <c>SetTimeOfDay</c> rewrites the
        /// outside colour, the sky strip, <c>Blueprint.OutsideTime</c>, the outdoor light map and
        /// <c>Blueprint.Terrain.LightVec</c> - the last of which is the vector the lot's terrain, and
        /// therefore its build grid, is shaded with. The lot's own sun is the engine's business and is
        /// still the engine's business; the part of "one sun, one sky, one weather" that the engine
        /// has no term for is the <em>background fill</em> under fog and cloud, and that is the one
        /// field written here. <c>UpdateSurroundings</c> then reads
        /// <c>World.State.Light.LightVec</c> every frame, so the surround still follows the lot's
        /// real sun rather than a second one.
        /// </para>
        /// <para>
        /// <b>The latch is reset on every lot load</b>, in <see cref="DetachSurroundings"/>. It was
        /// two bare fields before, with nowhere to reset them, so the second lot of a session could
        /// arrive within a quarter hour of the first lot's hour and simply never be written at all -
        /// and could not be told apart from a lot that was written and looked the same.
        /// </para>
        /// <para>
        /// <b>Nothing is applied before the world has features.</b>
        /// <c>CurrentLighting</c> is <c>default</c> for a world that has never built them, and
        /// <c>default</c> here is a midnight with a black sun: applying it would black out a lot on
        /// the first frame. The world's own features are built by its gated tick, so a lot that
        /// loads before the world's first tick keeps the engine's light until the next frame.
        /// </para>
        /// <para>
        /// <b>With the capability off, nothing here runs at all</b> - the call site is a single
        /// enum comparison, and no world is asked for, so a default session is byte-identical.
        /// </para>
        /// </remarks>
        private void UpdateNeighbourhoodWorldLighting()
        {
            if (!TryGetNeighbourhoodWorld(out var world)) return;

            if (TS1NeighPanel != null && !TS1NeighPanel.IsSuspendedForLot)
            {
                //The policy, and the only place it is written down. Asked before anything is
                //written, so it holds on a frame where there is no lot view to write to at all -
                //which is every frame of a normal visit to the map. Not a per-frame line: the
                //panel is up for as long as the player is looking at it.
                if (!MapScreenLightLogged)
                {
                    MapScreenLightLogged = true;
                    SimitoneLog.Info("B8: the neighbourhood map is up, so the lot view's light is"
                        + " left alone and the map keeps its own readable light whatever time the"
                        + " neighbourhood clock says (" + world.Clock + "). The world's light is"
                        + " applied to the lot the player walks into next.");
                }
                return;
            }

            var state = World?.State;
            if (state == null || vm?.Context == null) return;

            if (!world.HasFeatures) return;

            var lighting = world.CurrentLighting;
            var dayFraction = DayFractionOf(world.Clock);
            //The tested decision, and the only thing standing between a per-frame write and a
            //quarter-hourly one. See NeighbourhoodWorldLightApplyGate.
            if (!LightingApplied.ShouldApply(dayFraction, lighting.IsSunUp, WorldLightApplyStep))
            {
                return;
            }
            LightingApplied.Apply(dayFraction, lighting.IsSunUp);

            try
            {
                //The one value the world alone has, and the only one this screen writes: the lot
                //view's background fill under fog and cloud, which the engine derives from a ramp of
                //its own and which this lot's terrain and surround both read.
                var outside = new Color(lighting.OutsideColour.R, lighting.OutsideColour.G,
                    lighting.OutsideColour.B);
                state.OutsideColor = outside;
                var blueprint = vm.Context.Blueprint;
                if (blueprint != null) blueprint.OutsideColor = outside;
            }
            catch (Exception ex)
            {
                //The lot's own light is left as the engine had it, which is the same answer as not
                //having run at all, so the lot is playable and the latch above is not re-armed:
                //a lot view that throws here would otherwise write this line sixty times a second.
                //The next quarter hour of world time tries again.
                SimitoneLog.Warn("The neighbourhood's light could not be applied to this lot's"
                    + " view, so the lot is lit by its own clock's sun model for now. The lot"
                    + " itself is unaffected.", ex);
            }
        }

        /// <summary>
        /// Where a completed lot hop is recorded, in one place: the world's own <c>Transit</c>
        /// table, and the mirror of it into the provider's cross-lot state.
        /// </summary>
        /// <remarks>
        /// The world's own <c>Transit</c> table is the destination, and it is written through
        /// <see cref="TransitStore.Add(NeighbourhoodTransitRecord)"/> rather than by touching the
        /// table, because the table keeps a per-Sim index and a list that have to agree. With no
        /// world - B1 off, or no generated neighbourhood - the hop is dropped rather than recorded
        /// somewhere else: there is nowhere else yet, and an in-memory list nothing reads would be
        /// a second authority on where a sim is.
        ///
        /// <para>
        /// The mirror is the second half of <c>ILotTravelHost.RecordTransit</c> - the reason that
        /// member is one property rather than two writes at two call sites. See
        /// <see cref="MirrorTransitToProvider"/> for what it projects and what it refuses to
        /// write.
        /// </para>
        /// </remarks>
        /// <param name="hop">The hop that completed.</param>
        private void RecordNeighbourhoodTransit(NeighbourhoodTransitRecord hop)
        {
            if (hop == null) return;
            if (!TryGetNeighbourhoodWorld(out var world)) return;
            try
            {
                world.Transit.Add(hop);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("A completed lot hop could not be recorded in the neighbourhood"
                    + " world: " + hop, ex);
            }

            //Written after the world, and outside the try above, so a world that refused the hop
            //still gets the provider told what the world actually holds - the mirror reads the
            //table rather than the argument, which is what keeps it a projection of the
            //authority rather than a second opinion about this one hop.
            MirrorTransitToProvider(world.Transit);
        }

        /// <summary>
        /// The world's transit, projected onto the provider's <c>TS1GameState</c> so the shipped
        /// TS1 primitives keep reading a state they already understand (§5.4, §11 item 6).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The world is authoritative and this is a projection.</b> Every write is recomputed
        /// from <paramref name="transit"/> and nothing is ever read back, so the provider's copy
        /// cannot drift into being a second authority: it is whatever the world said, at the moment
        /// the world said it. A field the world has nothing to say about is left exactly as it was
        /// found, rather than blanked.
        /// </para>
        /// <para>
        /// <b><c>LotTransitInfo</c> is "how many Sims the world has between lots".</b> That is
        /// the question the shipped calls ask of it - <c>VMGenericTS1Call</c>'s travel and
        /// vacation-arrival cases read <c>&gt;= 1</c> as "the family is coming too" and <c>0</c>
        /// as "the controlled Sim arrived alone" - and a per-Sim count is the same vocabulary one
        /// per traveller, so a later continuous hop needs no new meaning for the field. B3's
        /// discrete switch writes its hop at
        /// <see cref="NeighbourhoodTransitRecord.MaxProgress"/>, i.e. already arrived, so a
        /// completed switch mirrors 0. That clearing is the case that earns its keep: a leftover
        /// <c>1</c> from an earlier pay-booth trip would make the next vacation arrival build a
        /// family that nobody sent.
        /// </para>
        /// <para>
        /// <b>Two fields are mirrored only when the world can name a value for them.</b>
        /// <c>DowntownSimGUID</c> is the object a downtown arrival spawns and
        /// <c>ActiveFamily</c> is the family it selects members from - both are read by the same
        /// shipped calls, and a zero in either is a null object and a null family rather than a
        /// harmless sentinel. B3's hop carries sim 0 ("this hop belongs to nobody"), and this
        /// screen's family is the only family the world knows about, so neither is written from a
        /// switch that has no real value: a mirror never clobbers a good GUID with 0. What the
        /// screen's own code reads - <c>InitializeLot</c>'s downtown-time lookup - therefore keeps
        /// the GUID the pay booth set, and picks it up from a record only when a record names a
        /// sim.
        /// </para>
        /// <para>
        /// <b>Gated on the capability that owns the switch, and it never throws.</b> The only
        /// callers are the ones <see cref="LotTravelService"/> makes with travel on, so the gate
        /// is the invariant stated locally rather than a behaviour change; and a mirror that threw
        /// would take down a lot switch whose own work is already done.
        /// </para>
        /// </remarks>
        /// <param name="transit">The world's table, which is the thing being mirrored.</param>
        private void MirrorTransitToProvider(TransitStore transit)
        {
            if (!NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel)) return;
            if (transit == null) return;

            try
            {
                var gameState = Content.Get().Neighborhood?.GameState;
                if (gameState == null) return;

                var travelling = 0;
                var travellingSim = 0;
                var held = transit.Active;
                for (int i = 0; i < held.Count; i++)
                {
                    //an arrived hop is not a traveller, and a null entry is a corrupt table rather
                    //than a fact about anybody
                    if (held[i] == null || held[i].IsComplete) continue;
                    travelling++;
                    if (travellingSim == 0 && held[i].Sim > 0) travellingSim = held[i].Sim;
                }

                //Only a real traveller is written. A completed switch has nobody in flight, and
                //writing 0 then erased what VMGenericTS1Call mode 17 had just set for the arrival
                //script ("the family comes too"), so the vacation arrival built one sim. The script
                //rewrites the field on every trip, so leaving it alone cannot leave a stale value.
                if (travelling > 0)
                {
                    gameState.LotTransitInfo = (short)Math.Min(travelling, short.MaxValue);
                }

                //the family's own reference first, then the guid: the family is a FAMI chunk the
                //screen already holds, and the guid is only written from a record that names a sim
                var family = ActiveFamily;
                if (family != null) gameState.ActiveFamily = family;
                if (travellingSim > 0) gameState.DowntownSimGUID = (uint)travellingSim;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("The neighbourhood's transit could not be mirrored into the"
                    + " provider's cross-lot state, so the shipped TS1 primitives will go on"
                    + " reading the last value they were given.", ex);
            }
        }

        /// <summary>
        /// B2's bridge: plan which surrounding lots become real sub-worlds of this lot, and stand
        /// them up. Gated on the <c>NeighbourhoodWorldSurroundings</c> capability, and with every
        /// flag off this is a single enum comparison.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The plan is built from what is <em>already attached</em> - the surround's own
        /// <see cref="SimitoneSurroundings.Placement"/>, its lot number, and the layout and map
        /// <see cref="SurroundingsWorld"/> holds - rather than by resolving any of it again. That is
        /// what "reuse the existing source resolution" is for: a second placement would be a second
        /// answer to "where is this lot in the generated map", and the facade standing where a
        /// hollow lot does not is exactly the bug §7.4 was written to prevent.
        /// </para>
        /// <para>
        /// The lot store and the reachability query come from <see cref="NeighbourhoodWorld"/>,
        /// because <see cref="SimitoneSurroundingsPlan.ForQuery"/> can take both and a plan that
        /// built its own query would rasterise a second copy of the layout. Neither is required:
        /// with no world the neighbours are all built blank, which is the first cut's shape and the
        /// one a lot can be surrounded with before B1 exists. Asking here does build a world if
        /// there is not one - §7 lists B2 as depending on B1, so with B2 on one is expected to be
        /// there, and a screen told to build hollow neighbours that silently builds blank ones
        /// because it had not built a clock first is worse than one that builds both.
        /// </para>
        /// <para>
        /// <b>It needs a blueprint, and it waits for one rather than refusing for want of
        /// it.</b> A <c>SubWorldComponent</c> is created on the lot view's graphics device and
        /// added to the lot's own blueprint, and neither of those exists on the frame a lot is
        /// constructed: a template load queues its blueprint as a <c>VMBlueprintRestoreCmd</c>, and
        /// a marshal resume runs it through <c>LotLifecycleService</c> after this screen's
        /// constructor has returned. Called from the end of <see cref="InitializeLot()"/> it
        /// therefore answered with <c>SimitoneSurroundingsRestore</c>'s own refusal - "the lot has
        /// no blueprint, so there is nowhere to put the neighbours" - and every neighbour the plan
        /// had selected stayed a facade, which is the whole of what B2 exists to stop. So the
        /// blueprint is the gate, the call is a <em>deferral</em> rather than a refusal, and the
        /// per-frame pump in <c>Update</c> makes the same call on the frame the blueprint lands.
        /// The log line below says which of the two happened, so the two are distinguishable from
        /// the log alone.
        /// </para>
        /// <para>
        /// <b>Called from every load path, and safe to call from all of them.</b> The three
        /// routes into a lot - the template string load, the marshal resume that a travel switch
        /// and the lot picker both use, and the surround that a cold neighbourhood attaches late
        /// - each ask here, and <see cref="HollowNeighboursAttached"/> makes the first one to
        /// succeed the only one that builds anything.
        /// </para>
        /// <para>
        /// <b>A lot that is built must also stop being drawn as a facade.</b> Standing a
        /// neighbour up is only half of B2 task 5: the surround's facade cache independently
        /// decides to build a low-poly house for every lot inside the drawn region, and a lot
        /// that is both a real sub-world and a facade is a house standing inside a house. So a
        /// successful restore is followed by <see cref="SimitoneSurroundings.AttachSubWorlds"/>
        /// with the same plan, which is what withholds those lots from the facade pass. A refused
        /// restore withholds nothing, because nothing was built and withholding a facade for a lot
        /// that is not there opens a hole in the street instead.
        /// </para>
        /// </remarks>
        private void AttachHollowNeighbours()
        {
            if (!NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.NeighbourSubWorlds)) return;
            if (vm == null) return;
            if (HollowNeighboursAttached) return;

            //The flag is the one gate: attaching a neighbour writes to the blueprint and the sky
            //bounds of the lot the player is standing in, and B2's contract is exactly "the
            //neighbours you can see are real hollow lots rather than Part A's facades". There is
            //no second static switch beside it.
            var surround = Surroundings;
            if (surround == null || !surround.Placement.IsValid)
            {
                //No surround means no placement, and a placement is what the plan is anchored on -
                //every sub-world offset is relative to it. There is nothing to log: the surround's
                //own refusal is already in the log and says which of the reasons it was.
                return;
            }

            var placement = surround.Placement;
            var layout = SurroundingsWorld?.Layout;
            var map = SurroundingsWorld?.Map;
            if (layout == null || map == null) return;

            //After the surround, because a surround that has not landed has nothing to anchor the
            //neighbours to, and before anything is planned, because a plan that cannot be hung
            //anywhere is a plan built for nothing.
            var blueprint = vm.Context?.Blueprint;
            if (blueprint == null)
            {
                if (!HollowNeighboursWaitingLogged)
                {
                    HollowNeighboursWaitingLogged = true;
                    SimitoneLog.Info("Lot " + surround.ActiveLot + " hollow neighbours: waiting for"
                        + " this lot's blueprint, which a queued blueprint restore has not written"
                        + " yet. They are built on the frame it lands, and until then the"
                        + " surrounding lots are facades.");
                }
                return;
            }

            //The world is only consulted for the two things it alone has - which lots are hollow,
            //and which of them can be walked to - and a refusal leaves both null rather than
            //abandoning the bridge, so a lot with no world still gets neighbours, blank.
            var query = null as NeighbourhoodQuery;
            var store = null as LotStore;
            if (TryGetNeighbourhoodWorld(out var world))
            {
                query = world.Query;
                store = world.LotStore;
            }

            SimitoneSurroundingsPlan plan;
            try
            {
                //The plan at the drawn region's own radius and under the sub-world budget, so every
            //lot the surround draws ground for is a real lot unless the count says otherwise - and
            //the ones the count says otherwise to are facades rather than holes.
            plan = SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout, store,
                    HollowNeighbourRadiusTiles, HollowNeighbourBudget);
            }
            catch (Exception ex)
            {
                //One attempt per lot, so this is the only line this lot will ever write about it.
                HollowNeighboursAttached = true;
                SimitoneLog.Warn("The surrounding lots for lot " + surround.ActiveLot
                    + " could not be planned, so this lot is played with facades only.", ex);
                return;
            }

            string reason;
            SimitoneSurroundingsPlan resolvedPlan;
            //The loader is kept for the lot, not made and dropped inside the restore: the first
            //pass builds as many neighbours as its budget allows and the rest are topped up on
            //later frames (TopUpHollowNeighbours) from the same loader and its cache.
            var device = vm.Context?.World?.State?.Device;
            var loader = device == null ? null : new NeighbourHollowLoader(device,
                NeighbourHollowLoader.DefaultMaxBuilds, NeighbourHollowLoader.DefaultMaxMilliseconds);
            var built = SimitoneSurroundingsRestore.TryRestore(vm, plan, out reason, out resolvedPlan, loader);
            //The restore itself is one attempt per lot whatever the answer, because neither the
            //plan nor the restore depends on anything that will change under it: the placement,
            //the map, the lot store and the blueprint are all the same objects on the next frame.
            //What was deferred is tracked per neighbour from here (HollowTopUp).
            HollowNeighboursAttached = true;
            if (built && loader != null) BeginHollowTopUp(resolvedPlan, loader);

            //The other half of the same decision, asked only once the neighbours are actually
            //standing up. This is the call that stops a lot being drawn twice: the surround's
            //facade cache builds a low-poly house for every lot inside the drawn region, and until
            //it is told which of them are real sub-worlds it keeps building and submitting one for
            //each of these - a house standing inside a house. AttachSubWorlds hands the plan to
            //NeighbourhoodFacades.UseSubWorlds, which is the one place that decides which lots are
            //withheld from the facade pass, and it is given the very same plan object that was
            //just handed to the restore, so the two halves cannot disagree about which lots are
            //real.
            //
            //Only on success. A plan the restore refused built nothing, and withholding those
            //facades would open a gap in the street rather than close a double draw - which is the
            //exact failure AttachSubWorlds's own SubWorldsInside check is there to prevent.
            if (built)
            {
                //Only the neighbours that carry a house withhold their facade. A blank one (past the
                //per-load hollow-build budget, or with an unreadable house file) is an empty plot,
                //and withholding its facade as well left "4 built, 8 blank".
                surround.AttachSubWorlds(resolvedPlan.WithoutBlankNeighbours());
            }

            //Always logged, on both answers, and the plan's own Describe either way: a gap in the
            //street a player reports is only diagnosable with the number of neighbours that were
            //planned next to the number that were built. The blueprint's own size is in the line
            //because "the lot has no blueprint" used to be the whole of what this logged, and the
            //only way to tell that fix apart from the old one is to see what it was hung on.
            SimitoneLog.Info("Lot " + surround.ActiveLot + " hollow neighbours ("
                + NeighbourhoodWorldFlags.Describe(NeighbourhoodWorldFlag.NeighbourSubWorlds) + "): "
                + plan.Describe() + ", onto this lot's " + blueprint.Width + "x" + blueprint.Height
                + " blueprint. " + (built ? "built." : "not built: " + reason));

            //And the shared radius, said as a fact rather than as a comment: a plan narrower than
            //the drawn region would put a ring of facades between the real lots and the edge of
            //the ground, which is the thing this screen used to do and the thing a player reports
            //as "some of these houses aren't real". The plan's own warnings already say what the
            //budget deferred, so the two lines together are everything needed to tell a layout
            //problem from a budget one.
            if (built && !plan.RadiusSharedWith(placement.Cull))
            {
                SimitoneLog.Warn("Lot " + surround.ActiveLot + "'s neighbour plan reaches "
                    + plan.RadiusTiles + " tile(s) past the property while the surround draws "
                    + placement.Cull.RadiusTiles + ", so the lots between the two are facades"
                    + " rather than real lots.");
            }
        }

        /// <summary>
        /// Starts tracking the neighbours the first restore built blank because its build budget
        /// ran out, so <see cref="TopUpHollowNeighbours"/> can build them on later frames. A
        /// neighbour that has no house file, or whose build already failed, is not tracked: no
        /// later frame would read it either.
        /// </summary>
        private void BeginHollowTopUp(SimitoneSurroundingsPlan resolved, NeighbourHollowLoader loader)
        {
            var pending = new List<short>();
            for (int i = 0; i < resolved.Neighbours.Count; i++)
            {
                var neighbour = resolved.Neighbours[i];
                if (!neighbour.IsValid || neighbour.IsHollow) continue;
                if (NeighbourHollowLoader.BuildRefusalFor(neighbour.Lot) != null) continue;
                if (loader.HasFailed(neighbour.Lot.Number)) continue;
                pending.Add(neighbour.Lot.Number);
            }
            if (pending.Count == 0) return;

            //one real build per pass from here on, each with the time allowance the first pass had
            loader.MaxBuilds = 1;
            HollowTopUpLoader = loader;
            HollowTopUpPlan = resolved;
            HollowTopUp = new SimitoneHollowTopUp();
            HollowTopUp.Begin(pending);
            SimitoneLog.Info("Lot " + Surroundings?.ActiveLot + " hollow neighbours: " + pending.Count
                + " built blank on the first pass are topped up one at a time on later frames.");
        }

        /// <summary>
        /// One later-frame build of a blank neighbour, at most one every few frames: reads its
        /// contents, swaps its blank sub-world for a real one and withholds its facade. Nothing is
        /// rebuilt - a lot the loader has cached is answered from the cache and a built one is not
        /// offered again - and a lot that fails is retried once after a delay and then left blank,
        /// never once per frame.
        /// </summary>
        private void TopUpHollowNeighbours()
        {
            var topUp = HollowTopUp;
            if (topUp == null) return;
            var surround = Surroundings;
            var loader = HollowTopUpLoader;
            var plan = HollowTopUpPlan;
            if (vm == null || surround == null || loader == null || plan == null
                || vm.Context?.Blueprint == null || topUp.IsComplete)
            {
                if (topUp.IsComplete) EndHollowTopUp(topUp);
                return;
            }

            HollowTopUpFrame++;
            short lotNumber;
            if (!topUp.TryNext(HollowTopUpFrame, out lotNumber)) return;
            var neighbour = plan.NeighbourFor(lotNumber);
            if (!neighbour.IsValid || neighbour.IsHollow)
            {
                topUp.MarkUnreadable(lotNumber, HollowTopUpFrame);
                return;
            }

            loader.BeginPass();
            VMHollowMarshal marshal;
            string why;
            if (!loader.TryLoad(neighbour.Lot, out marshal, out why))
            {
                //a fresh pass always has budget for one build, so a refusal here is the lot itself
                topUp.MarkUnreadable(lotNumber, HollowTopUpFrame);
                SimitoneLog.Warn("Lot " + surround.ActiveLot + " hollow neighbour " + lotNumber
                    + " stays blank: " + why);
                return;
            }
            loader.Publish(plan.Store, lotNumber, marshal);

            SimitoneSurroundingsPlan upgraded;
            if (!SimitoneSurroundingsRestore.TryUpgradeBlank(vm, plan, lotNumber, marshal, out upgraded, out why))
            {
                var again = topUp.MarkFailed(lotNumber, HollowTopUpFrame);
                SimitoneLog.Warn("Lot " + surround.ActiveLot + " hollow neighbour " + lotNumber
                    + " could not be built" + (again ? ", trying once more later" : ", leaving it blank")
                    + ": " + why);
            }
            else
            {
                topUp.MarkBuilt(lotNumber, HollowTopUpFrame);
                HollowTopUpPlan = upgraded;
                //withhold its facade now that it is real, from the one plan the two halves share
                surround.AttachSubWorlds(upgraded.WithoutBlankNeighbours());
            }

            if (topUp.IsComplete) EndHollowTopUp(topUp);
        }

        private void EndHollowTopUp(SimitoneHollowTopUp topUp)
        {
            SimitoneLog.Info("Lot " + Surroundings?.ActiveLot + " hollow neighbours: " + topUp.Describe() + ".");
            ClearHollowTopUp();
        }

        private void ClearHollowTopUp()
        {
            HollowTopUp?.Clear();
            HollowTopUp = null;
            HollowTopUpLoader = null;
            HollowTopUpPlan = null;
            HollowTopUpFrame = 0;
        }

        // -------------------------------------------------------------------------------------
        // §5.2's clock handshake.
        //
        // It is behind its flag, refuses to touch anything with the capability off, and does not
        // allocate until the flag is asked. §5.2's rule is the whole of the handshake: a
        // lot's clock is seeded from the world on load and the lot pushes back on the way out, so
        // the minutes a player spends inside a house are the neighbourhood's minutes rather than
        // a number that dies with the VM.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// The two things the handshake needs before either half of it can run: a lot whose clock
        /// is being read or written, and a neighbourhood world to read or write it against.
        /// </summary>
        /// <remarks>
        /// Every refusal here is a no-op rather than an error. The flag is asked first, so with
        /// <see cref="NeighbourhoodWorldFlag.World"/> off this frame does not even look at the VM
        /// or the world; a lot that has not been built yet has no clock; and a session with no
        /// generated neighbourhood has no world to be the authority for anything.
        /// </remarks>
        /// <param name="world">The neighbourhood world, or null when there is not one to use.</param>
        /// <param name="lotClock">The lot's own clock, or null when there is no lot.</param>
        /// <returns>Whether the handshake can run at all.</returns>
        private bool TryBeginClockHandshake(out SimitoneNeighbourhoodWorld world, out VMClock lotClock)
        {
            world = null;
            lotClock = null;
            if (!NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World)) return false;
            lotClock = vm?.Context?.Clock;
            if (lotClock == null) return false;
            if (!TryGetNeighbourhoodWorld(out world))
            {
                lotClock = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The half of the handshake that runs on the way <em>into</em> a lot: the neighbourhood
        /// is what the lot opens at.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Called on every route into a lot</b> - after <c>vm.Init</c> for a fresh VM, after
        /// <see cref="LotLifecycleService.ResumeFromMarshal"/> for a resumed one, and after
        /// <see cref="LotLifecycleService.BlueprintReset"/> for a template - because the clock is
        /// not something a load path guarantees and this is the one place all three converge.
        /// </para>
        /// <para>
        /// <b>Direction, and why it is what it is.</b> The world takes the lot's time while it has
        /// none of its own, and gives the lot the world's once it does - <c>PullClockFrom</c> and
        /// then <c>PushClockTo</c>, the two statements that world exposes for exactly this. The
        /// first half is what makes it a handshake rather than a copy: a brand new
        /// <see cref="WorldClock"/> reads as 1 June 1997 at midnight, and imposing that on the
        /// first lot of a session would reset a shipped blueprint's own time to a value the player
        /// never chose. The second half is §5.2's "a lot's clock is seeded from the world on load",
        /// and it is why a lot loaded from its own file opens where the neighbourhood has got to
        /// rather than where the file was saved.
        /// </para>
        /// </remarks>
        private void HandshakeClockOnLoad()
        {
            if (!TryBeginClockHandshake(out var world, out var lotClock)) return;

            try
            {
                //world.Clock is the WorldClock rather than the read-only INeighbourhoodClock
                //exactly because this decision needs to ask it whether it has a time yet.
                if (!world.Clock.IsAvailable) world.PullClockFrom(lotClock);
                else world.PushClockTo(lotClock);
            }
            catch (Exception ex)
            {
                //A handshake that throws is dropped rather than retried every frame: the lot is
                //already up and playable, and its own clock is untouched either way, so this is a
                //loss of neighbourhood time and nothing more.
                SimitoneLog.Warn("The neighbourhood's time could not be handed to the lot that"
                    + " just loaded, so this lot is keeping its own. The lot itself is unaffected.",
                    ex);
            }
        }

        /// <summary>
        /// The half of the handshake that runs on the way <em>out</em> of a lot: the lot's time
        /// becomes the neighbourhood's. §5.2's "pushes back on save or tick".
        /// </summary>
        /// <remarks>
        /// Called at the two places a lot's state is marshalled - the stash a travel switch makes
        /// before the VM is torn down, and the save the player asks for - because those are the
        /// two moments at which the lot's clock stops being about anything. The transfer is
        /// unconditional, for the reason <c>PullClockFrom</c>'s own remarks give: the caller has
        /// already decided it happens, and comparing the two clocks first is how a handshake ends
        /// up skipped.
        /// </remarks>
        private void HandshakeClockOnSave()
        {
            if (!TryBeginClockHandshake(out var world, out var lotClock)) return;

            try
            {
                world.PullClockFrom(lotClock);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("The lot's time could not be handed to the neighbourhood, so the"
                    + " neighbourhood's own clock is being kept. The lot itself is unaffected.", ex);
            }
        }

        /// <summary>
        /// The family an avatar's own family number names, which is the family authority B5's
        /// global link is handed: a resolver that answers null falls back to the lot's current
        /// family, so a neighbourhood that has no record of this family behaves like one that is
        /// not there.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Content, so it is read through the content manager on every call rather than cached:
        /// a family moves house during a session, and a cache here would be a second copy of a
        /// fact the content manager already owns. It is also the seam the link's own remarks ask
        /// for - a resolver that answers null falls back to the lot's current family, so a
        /// neighbourhood that has no record of this family behaves like one that is not there.
        /// </para>
        /// <para>
        /// It is a method rather than a lambda at the construction site so the screen's own
        /// content read is one named, documented thing rather than an expression buried in a
        /// four-argument call.
        /// </para>
        /// <para>
        /// <b>A chunk id, not a house number.</b> The number this is called with is an avatar's
        /// <c>VMPersonDataVariable.TS1FamilyNumber</c>, and
        /// <c>VMTS1LotState.ActivateFamily</c> writes <c>CurrentFamily.ChunkID</c> into that - so
        /// it names a <c>FAMI</c> chunk. The provider's own family table is keyed on
        /// <c>FAMI.HouseNumber</c> instead, and in a shipped neighbourhood the two ranges overlap,
        /// because families and lots are both numbered from one. Looking a family number up in that
        /// table answers with a real family and the wrong one: every build charge, every purchase
        /// and every interaction that spends money is then priced against another household's
        /// budget, which refuses the player's builds and purchases outright whenever that household
        /// happens to be broke - a lot in which nothing can be built, bought or interacted with. So
        /// the chunk is fetched by its own id, and <see cref="SimitoneGlobalLink.IsTheFamilyAskedFor"/>
        /// is what makes that answer checkable rather than trusted.
        /// </para>
        /// </remarks>
        /// <param name="familyId">The avatar's family number, which is a <c>FAMI</c> chunk id.</param>
        /// <returns>The family, or null when the neighbourhood does not know it.</returns>
        private FAMI ResolveFamilyByChunkId(short familyId)
        {
            if (familyId < 0) return null;
            try
            {
                var family = Content.Get()?.Neighborhood?.GetFamily((ushort)familyId);
                //A family whose own chunk id is not the number that was asked for is not this
                //avatar's family, and is reported the same way as no answer at all: null, which the
                //link falls back from to the lot's own current family.
                return SimitoneGlobalLink.IsTheFamilyAskedFor(familyId, family) ? family : null;
            }
            catch (Exception e)
            {
                //Content not loaded, or a neighbourhood file that has not opened. The link treats a
                //null as knowing nothing, so this is a log line and not a failed lot.
                SimitoneLog.Warn("B5: the neighbourhood could not be asked which family number "
                    + familyId + " is, so builds and purchases are charged to the lot's own current"
                    + " family - which is what they were charged to before this capability existed.",
                    e);
                return null;
            }
        }

        /// <summary>
        /// The screen as an <see cref="ILotTravelHost"/>, so
        /// <see cref="LotTravelService"/> can perform a lot switch without owning a VM, a content
        /// manager or a graphics device.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A nested type rather than a separate class so it can reach the screen's private store,
        /// its private switch field and its two lot-load entry points, all of which the seam needs
        /// and none of which are worth publishing. It holds the screen and nothing else: every
        /// answer it gives is read or delegated straight through, which is what makes "the service
        /// is pure and the host is the screen" a structural claim rather than a promise.
        /// </para>
        /// <para>
        /// One instance per screen, built on first use. It is stateless - all of its state lives on
        /// the screen - so it would be equally correct to make a new one per switch, and one is
        /// kept so the delegate it hands back for <see cref="RecordTransit"/> is allocated once.
        /// </para>
        /// </remarks>
        private sealed class LotTravelHost : ILotTravelHost
        {
            private readonly TS1GameScreen screen;
            private readonly Action<NeighbourhoodTransitRecord> recordTransit;

            /// <summary>Binds the adapter to the screen it fronts.</summary>
            /// <param name="screen">The screen that owns the live lot and the per-lot store.</param>
            public LotTravelHost(TS1GameScreen screen)
            {
                this.screen = screen;
                recordTransit = screen.RecordNeighbourhoodTransit;
            }

            /// <summary>
            /// The lot the player is standing on, as the VM's global 10 - the same read the inline
            /// switch did, so a travel path and a service path name the same lot. Zero when there
            /// is no lot, which is not a lot number and is why the service refuses to stash it.
            /// </summary>
            public int CurrentHouseId
            {
                get { return screen.vm == null ? 0 : screen.vm.GetGlobalValue(10); }
            }

            /// <summary>
            /// The active family's own house number, or null when no family is in play. Null rather
            /// than a made-up number because the service's downtown test is a comparison against
            /// it, and with no family there is no home to be downtown about.
            /// </summary>
            public int? ActiveFamilyHouseNumber
            {
                get
                {
                    var family = screen.ActiveFamily;
                    return family == null ? (int?)null : family.HouseNumber;
                }
            }

            /// <summary>
            /// The lot's own house file. On the seam because the root of a neighbourhood is
            /// content, and a service that built the path itself would need the content manager -
            /// the dependency that makes a switch unrunnable without a game.
            /// </summary>
            /// <param name="houseId">The lot number.</param>
            /// <returns>The path, or null/empty when the lot has no house file on disk.</returns>
            public string GetHousePath(int houseId)
            {
                return Content.Get().Neighborhood.GetHousePath(houseId);
            }

            /// <summary>
            /// Marshal the live lot out and keep it in the per-lot store, keyed by its own house
            /// number. The marshal comes back as well because the interface promises it, though the
            /// service has no use for it: the lot about to load is a different lot's state, and the
            /// lot being loaded is never stashed at all.
            /// </summary>
            /// <returns>The lot's marshalled state.</returns>
            public VMMarshal SaveCurrentLot()
            {
                var house = (short)CurrentHouseId;
                //§5.2's handshake, on the way out of a lot. Before the marshal rather than after it,
                // and before the VM is torn down by the load this precedes, because the lot's clock
                //is the neighbourhood's only reading of what time it is where the player has been.
                screen.HandshakeClockOnSave();
                var marshal = screen.vm.Save();
                screen.SessionLots.SetActive(house, marshal);
                return marshal;
            }

            /// <summary>
            /// The in-session stash, which outranks the on-disk travel slot: a lot held in memory
            /// is resumed from memory. The store holds only non-null marshals, so "held" means a
            /// usable marshal and nothing else.
            /// </summary>
            /// <param name="lot">The lot number.</param>
            /// <param name="marshal">The held state, when there is one.</param>
            /// <returns>Whether usable state is held for this lot.</returns>
            public bool TryRestoreSaved(short lot, out VMMarshal marshal)
            {
                return screen.SessionLots.TryGetActive(lot, out marshal);
            }

            /// <summary>
            /// Consume a lot's entry, so a later visit reloads it from disk rather than replaying a
            /// snapshot of a session that has since moved on.
            /// </summary>
            /// <param name="lot">The lot number.</param>
            public void RemoveSaved(short lot)
            {
                screen.TakeStashedLot(lot);
            }

            /// <summary>
            /// Record that the lot now loading is a travel lot rather than a home lot. A field
            /// write, before the load, because the load reads it to pick the family and to apply
            /// the downtown time.
            /// </summary>
            /// <param name="downtown">Whether the destination is downtown.</param>
            public void SetDowntown(bool downtown)
            {
                screen.Downtown = downtown;
            }

            /// <summary>
            /// Load a lot by path, passing the lot number on. The number is what the generated
            /// neighbourhood is placed against, and a path alone does not reliably carry it.
            /// </summary>
            /// <param name="path">The lot's own house path.</param>
            /// <param name="houseId">The lot number.</param>
            public void LoadLot(string path, int houseId)
            {
                screen.InitializeLot(path, false, houseId);
            }

            /// <summary>
            /// Resume a lot from preserved state, with the lot number that state belongs to.
            /// </summary>
            /// <param name="marshal">The lot state to resume.</param>
            /// <param name="houseId">The lot number.</param>
            public void LoadLot(VMMarshal marshal, int houseId)
            {
                screen.InitializeLot(marshal, houseId);
            }

            /// <summary>
            /// Where a completed hop is reported: the screen's own method, so the world is only
            /// asked for on a switch that actually happened.
            /// </summary>
            public Action<NeighbourhoodTransitRecord> RecordTransit
            {
                get { return recordTransit; }
            }
        }

        /// <summary>
        /// The <see cref="ILotTravelHost"/> this screen presents to
        /// <see cref="LotTravelService"/>, and the one travel seam in the session.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Public because the seam is not this screen's alone. The service is asked for a switch
        /// from two callers - this screen's own <c>SwitchLot</c> block and
        /// <c>NeighborhoodService.PlayHouse</c>, i.e. the lot picker - and the three members only
        /// the screen can answer are the ones that carry the behaviour: the stash
        /// (<c>vm.Save</c> into the session stash), the per-lot store that resume reads, and the
        /// transit writer. A second adapter built over the screen's public surface can only stub
        /// those out - a picker host that cannot stash cannot put back the lot the player left five
        /// minutes ago, and one with no transit writer loses the hop - so two lot-preservation
        /// behaviours would exist and only one of them would be the real one. One host, one stash,
        /// one store, one record.
        /// </para>
        /// <para>
        /// The adapter itself stays a private nested type, because everything it does is a
        /// delegation into this class: the seam is the interface, and the interface is what
        /// <see cref="LotTravelService"/> and the picker hold.
        /// </para>
        /// <para>
        /// Lazy for the same reason the world is: with the travel capability off nothing asks for
        /// one, so nothing is built and the screen's own inline switch is the only one that ever
        /// runs. Deliberately not gated on the flag - a caller that asks off-flag gets a host and
        /// the service's documented refusal, rather than a null and a different failure; the gate
        /// lives at the call sites, where the behaviour it selects is visible.
        /// </para>
        /// </remarks>
        public ILotTravelHost TravelHost
        {
            get
            {
                if (TravelHostInstance == null) TravelHostInstance = new LotTravelHost(this);
                return TravelHostInstance;
            }
        }

        /// <summary>The one adapter instance, created on first use by <see cref="TravelHost"/>.</summary>
        private ILotTravelHost TravelHostInstance;

        /// <summary>
        /// The lot switch B3 owns, as a call rather than a block: reject an impossible request,
        /// stash the lot being left, set downtown, resume preserved state or read the house file,
        /// and record the hop last. The same five steps, in the same order, as the inline version
        /// this replaced - the service's own remarks are the authority on why the order is fixed.
        /// </summary>
        /// <remarks>
        /// The reason is logged on both answers, including a refusal, because "the switch did
        /// nothing" is the case most worth reading about: a travel request that names a number
        /// which is not a lot, or a destination with no house file, leaves the player exactly where
        /// they were with no other symptom.
        /// </remarks>
        /// <param name="houseId">The lot to load, which is <see cref="SwitchLot"/>.</param>
        /// <returns>
        /// Whether the destination lot was loaded. False means the switch was refused and the
        /// player is still standing in the lot that <see cref="VMLotSwitch"/> paused, so the
        /// caller is the one that has to end that pause - see
        /// <see cref="RestoreSpeedAfterFailedLotSwitch"/>.
        /// </returns>
        private bool PerformLotSwitch(int houseId)
        {
            string reason;
            var switched = LotTravelCoordinator.TryTransition(
                TravelHost,
                new LotTransitionRequest(houseId, TransitionReason.VmRequest),
                out reason);
            SimitoneLog.Info("Lot travel: " + (switched ? "switched. " : "did not switch. ") + reason);
            //A refusal here is the one way a switch request can be consumed with the lot still
            //standing - the service answers "no lot number" or "no house file" without touching
            //the lot - and the pause VMLotSwitch took to let the requesting tick finish has then
            //got nothing left to end it. A switch that did happen has replaced the VM, so the
            //restore in here is a no-op on that path.
            if (!switched) RestoreSpeedAfterFailedLotSwitch();
            return switched;
        }

        /// <summary>
        /// The one lot-switch service. Stateless, so one instance serves a whole session and two
        /// switches cannot see each other - see <see cref="LotTravelService"/>'s remarks.
        /// </summary>
        private readonly LotTravelService LotTravelCoordinator = new LotTravelService();

        public void StartMoveIn(int familyID)
        {
            NeighborhoodService.StartMoveIn(familyID);
        }

        public void NeighSelection(NeighSelectionMode mode)
        {
            NeighborhoodService.NeighSelection(mode);
        }

        /// <summary>
        /// Opens the lot picker on one specific neighbourhood. See
        /// <see cref="NeighborhoodService.NeighbourhoodIdForHouse"/> for the ids.
        /// </summary>
        /// <param name="neighborhoodMode">The neighbourhood to open on.</param>
        /// <param name="moveInMode">True when the next lot picked buys a lot for a family waiting
        /// to move in.</param>
        public void NeighSelection(ushort neighborhoodMode, bool moveInMode = false)
        {
            NeighborhoodService.NeighSelection(neighborhoodMode, moveInMode);
        }

        /// <summary>
        /// Rebuilds the neighbourhood lot picker and its switcher from whatever the neighbourhood
        /// provider has open now, keeping the map the player is on.
        ///
        /// <para>
        /// One method, and the whole of what this screen owes a slot switch. Re-initialising the
        /// provider (<c>InitSpecific</c>) is the caller's, because only the caller knows which slot
        /// was open and so can put it back if it fails; everything the picker draws is read out of
        /// the provider's files, so it has to be rebuilt afterwards or the player is left clicking
        /// a map of the neighbourhood they were just switched away from. The service owns that
        /// rebuild - see <c>NeighborhoodService.RefreshNeighbourhoodUi</c> - exactly as it owns the
        /// first build of the same two elements.
        /// </para>
        /// </summary>
        public void RefreshNeighbourhoodUi()
        {
            NeighborhoodService.RefreshNeighbourhoodUi();
        }

        public void PlayHouse(short house, UIElement switcher)
        {
            NeighborhoodService.PlayHouse(house, switcher);
        }

        public void MoveInAndPlay(short house, int family, UIElement switcher)
        {
            NeighborhoodService.MoveInAndPlay(house, family, switcher);
        }

        public void EvictLot(FAMI family, short houseID)
        {
            NeighborhoodService.EvictLot(family, houseID);
        }

        /// <summary>
        /// Moves a lot between residential and community. See
        /// <see cref="NeighborhoodService.RezoneLot"/> - including the rule that an occupied lot
        /// is refused, and why.
        /// </summary>
        /// <param name="houseID">The lot to rezone.</param>
        /// <param name="zone"><c>LotZoning.ZoneResidential</c> or <c>LotZoning.ZoneCommunity</c>.</param>
        /// <returns>What the request did, so the lot panel can report a refusal rather than
        /// pretending it worked.</returns>
        public RezoneLotResult RezoneLot(short houseID, short zone)
        {
            return NeighborhoodService.RezoneLot(houseID, zone);
        }

        /// <summary>
        /// Use this function to change the game language at runtime.
        /// </summary>
        /// <remarks>
        /// The strings are reloaded by <see cref="SimitoneGame.ChangeLanguage"/>, but a panel only
        /// reads its captions once when it is built, so the swap is hidden behind a transition
        /// wipe - which doubles as the moment the old UI is torn down and the new one built.
        /// </remarks>
        /// <param name="NewLanguage">The language to switch to.</param>
        public void RuntimeChangeLanguage(STRLangCode NewLanguage) =>
            UITransDialog.CreateWithCustomUIIcon("opt_lang.png", () => (GameFacade.Game as SimitoneGame)?.ChangeLanguage(NewLanguage));

        public override void GameResized()
        {
            base.GameResized();
            Bg.Position = (new Vector2(ScreenWidth, ScreenHeight)) / 2;
            World?.GameResized();
        }

        public void Initialize(string propertyName, bool external)
        {
            GameFacade.CurrentCityName = propertyName;
            ZoomLevel = 1; //screen always starts at near zoom
            InitializeLot(propertyName, external);
        }

        private int SwitchLot = -1;

        /// <summary>
        /// The Options dialog currently up, if any.
        ///
        /// <para>
        /// Tracked so that opening the menu a second time re-opens it rather than stacking a
        /// second copy on top of the first. It is per screen, not static: there is one live-mode
        /// screen at a time, and a static would keep a dialog - and the whole UI it holds - alive
        /// after the screen that made it was torn down.
        /// </para>
        /// </summary>
        private UIOptionsAlert _openOptionsDialog;

        /// <summary>
        /// Opens a new <see cref="UIOptionsAlert"/>, replacing one that is already up.
        /// </summary>
        public void ShowOptionsDialog()
        {
            if (_openOptionsDialog != null)
                UIScreen.RemoveDialog(_openOptionsDialog);

            _openOptionsDialog = new UIOptionsAlert(GlobalSettings.Default);
            UIScreen.GlobalShowDialog(_openOptionsDialog, true);
        }

        /// <summary>
        /// Sets the gameplay speed, playing the speed-change sound that goes with the transition.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one place the speed changes. The desktop speed buttons used to carry their
        /// own copy of the sound table and their own mapping, which is why they and the number
        /// keys could disagree; both now come through here.
        /// </para>
        ///
        /// <para>
        /// <paramref name="speed"/> is a <see cref="GameplaySpeed"/> value, i.e. the button index
        /// 1..4 where 4 is pause - <b>not</b> the VM's own multiplier (0 for pause, 1/3/10 for the
        /// speeds). The VM value is reached through <see cref="GameplaySpeed.ReverseRemap"/>, so
        /// the two numbering schemes are never mixed up.
        /// </para>
        ///
        /// <para>
        /// A lot in Buy/Build, and a lot with no sims in it, hold the speed at
        /// <see cref="GameplaySpeed.Disabled"/>: requests are refused rather than applied, so the
        /// speed buttons cannot be used to sneak past the pause.
        /// </para>
        /// </remarks>
        /// <param name="speed">The requested speed, as a <see cref="GameplaySpeed"/> value.</param>
        public void ChangeSpeedTo(int speed)
        {
            if (vm == null) return;
            if (vm.SpeedMultiplier == GameplaySpeed.Disabled) return;

            switch (vm.SpeedMultiplier)
            {
                case 0:
                    switch (speed)
                    {
                        case GameplaySpeed.Play:
                            HITVM.Get().PlaySoundEvent(UISounds.SpeedPTo1); break;
                        case GameplaySpeed.FastForward:
                            HITVM.Get().PlaySoundEvent(UISounds.SpeedPTo2); break;
                        case GameplaySpeed.FastFastForward:
                            HITVM.Get().PlaySoundEvent(UISounds.SpeedPTo3); break;
                    }
                    break;
                case 1:
                    switch (speed)
                    {
                        case GameplaySpeed.Pause:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed1ToP); break;
                        case GameplaySpeed.FastForward:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed1To2); break;
                        case GameplaySpeed.FastFastForward:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed1To3); break;
                    }
                    break;
                case 3:
                    switch (speed)
                    {
                        case GameplaySpeed.Pause:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed2ToP); break;
                        case GameplaySpeed.Play:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed2To1); break;
                        case GameplaySpeed.FastFastForward:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed2To3); break;
                    }
                    break;
                case 10:
                    switch (speed)
                    {
                        case GameplaySpeed.Pause:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed3ToP); break;
                        case GameplaySpeed.Play:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed3To1); break;
                        case GameplaySpeed.FastForward:
                            HITVM.Get().PlaySoundEvent(UISounds.Speed3To2); break;
                    }
                    break;
            }

            switch (speed)
            {
                case GameplaySpeed.Pause: vm.SpeedMultiplier = GameplaySpeed.ReverseRemap[GameplaySpeed.Pause]; break;
                case GameplaySpeed.Play: vm.SpeedMultiplier = GameplaySpeed.ReverseRemap[GameplaySpeed.Play]; break;
                case GameplaySpeed.FastForward: vm.SpeedMultiplier = GameplaySpeed.ReverseRemap[GameplaySpeed.FastForward]; break;
                case GameplaySpeed.FastFastForward: vm.SpeedMultiplier = GameplaySpeed.ReverseRemap[GameplaySpeed.FastFastForward]; break;
            }
            vm.ResetTickAlign();
        }

        /// <summary>
        /// The gameplay speed that was in force before <see cref="LockSimSpeed"/> froze it, so
        /// that leaving the mode which froze it can put it back instead of forcing a pause.
        ///
        /// <para>
        /// The point of holding the old speed rather than remembering "was it paused?" is that
        /// Buy/Build is not a pause. A player who was on speed 3, opened Buy to place a lamp and
        /// closed it again should find the game at speed 3, not stopped.
        /// </para>
        ///
        /// <para>
        /// A -1 here means "nothing worth restoring" - the lot was simless before anything locked
        /// it, or the lock happened before the VM existed - and the unlock falls back to speed 1
        /// rather than leaving the lot disabled.
        /// </para>
        /// </summary>
        private int _underlyingSimSpeed = GameplaySpeed.Disabled;

        /// <summary>
        /// If leaving a mode that is forcing paused gameplay speed, resets the gameplay speed to
        /// the one set before <see cref="LockSimSpeed"/>.
        /// </summary>
        internal void UnlockSimSpeed()
        {
            if (vm == null) return;
            if (vm.SpeedMultiplier != GameplaySpeed.Disabled) return; // unlocked already
            vm.SpeedMultiplier = _underlyingSimSpeed;
            if (vm.SpeedMultiplier < 0) // oops? - nothing worth restoring
                vm.SpeedMultiplier = GameplaySpeed.ReverseRemap[GameplaySpeed.Play]; // force speed 1
            vm.ResetTickAlign();
        }

        /// <summary>
        /// If the gameplay is not already locked (<see cref="GameplaySpeed.Disabled"/>), saves the
        /// current gameplay speed and disables changing gameplay speed.
        /// </summary>
        internal void LockSimSpeed()
        {
            if (vm == null) return;
            if (vm.SpeedMultiplier == GameplaySpeed.Disabled) return; // locked already
            _underlyingSimSpeed = vm.SpeedMultiplier;
            vm.SpeedMultiplier = GameplaySpeed.Disabled;
        }

        public override void Update(FSO.Common.Rendering.Framework.Model.UpdateState state)
        {
            GameFacade.Game.IsFixedTimeStep = (vm == null || vm.Ready);
            
            Visible = World?.Visible != false && World?.State.Cameras.HideUI != true;
            GameFacade.Game.IsMouseVisible = Visible;

            //a text input holding keyboard focus (the cheat box, for one) is consuming
            //keystrokes, so the single-key speed shortcuts must not also fire. Same reasoning as
            //the desktop UCP's shortcut gate.
            var nofocus = state.InputManager.GetFocus() == null;

            //speed keys, in the same 1..4 numbering the speed buttons use (4 = pause)
            if (nofocus && state.NewKeys.Contains(Keys.D1)) ChangeSpeedTo(GameplaySpeed.Play);
            if (nofocus && state.NewKeys.Contains(Keys.D2)) ChangeSpeedTo(GameplaySpeed.FastForward);
            if (nofocus && state.NewKeys.Contains(Keys.D3)) ChangeSpeedTo(GameplaySpeed.FastFastForward);
            if (nofocus && state.NewKeys.Contains(Keys.P)) ChangeSpeedTo(GameplaySpeed.Pause);
            if (nofocus && state.NewKeys.Contains(Keys.D0))
            {
                //frame advance
                ChangeSpeedTo(GameplaySpeed.Play);
                GameThread.NextUpdate((FSO.Common.Rendering.Framework.Model.UpdateState ustate) => ChangeSpeedTo(GameplaySpeed.Pause));
            }
            base.Update(state);

            //The screen's own background is an opaque UI element, and the UI layer is drawn *after*
            //GameFacade.Scenes. So while the generated neighbourhood is carrying the picture, the
            //panel makes its painted-map backdrop transparent - and without this, what shows through
            //is the background (and the 2D lot buttons), not the 3D world behind it. Hiding the
            //background for exactly as long as the panel says it is showing 3D lets the scene show
            //through the transparent backdrop. Only while the neighbourhood panel is the screen: the
            //lot view sets Bg.Visible itself and there is no panel then.
            //
            //"Is actually on screen", not "exists": SuspendNeighbourhoodForLot now removes the panel
            //and nulls TS1NeighPanel on lot entry, so in practice this is a null check, but the
            //IsSuspendedForLot test keeps a panel that is still attached and hidden from blanking
            //the lot view.
            var neighPanel = TS1NeighPanel;
            if (neighPanel != null && !neighPanel.IsSuspendedForLot)
            {
                Bg.Visible = !neighPanel.IsShowing3D;
            }

            //weather audio and the snowed-in terrain. after base.Update so the frame's keystrokes
            //(including a weather typed into the cheat box) are in.
            UpdateWeatherEffects(FrameSeconds(state));

            //the generated neighbourhood around this lot: the lot's own light handed to it, and
            //whatever neighbouring houses are still waiting for a mesh. after the weather, so the
            //light it reads is the light the weather has just set.
            UpdateSurroundings();

            //B2's hollow neighbours, asked again every frame until one attempt has been made for the
            //lot that is up. This is the pump the load paths cannot be: a queued blueprint restore
            //and a cold neighbourhood's generation both land on a frame of their own, and this is
            //the one place that is running on that frame. Gated here rather than inside the bridge
            //for the reason every other site gives - all seven flags are read in one place a reader
            //can see - so with the capability off this frame does one cached ini read and a
            //comparison, and no plan, no neighbour and no sub-world.
            if (NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.NeighbourSubWorlds))
            {
                AttachHollowNeighbours();
                //and the neighbours that attempt had to leave blank, one at a time
                TopUpHollowNeighbours();
            }

            //B1's world clock, kept while the player is standing in this lot: the world follows
            //the lot's own clock while one is up, and ticks by wall-clock time (the frame length,
            //read the way the weather reads it) only when there is none. Gated here rather
            //than inside the call so that with the capability off this frame does not even read
            //the frame length - the whole of what Part B adds to a default session is a handful of
            //cached ini reads and a comparison each.
            //
            //B6 (NeighbourhoodWorldOffscreen) is the second capability that needs this tick: it
            //is what "the world moves on while the player is elsewhere" means, and the only place
            //this screen can advance it is this frame. Either flag is therefore enough, and B6
            //alone builds the world - the two are listed as dependent in §7, and a player told
            //to keep a neighbourhood running off-lot and given no clock is not being refused.
            if (NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World)
                || NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Offscreen))
            {
                UpdateNeighbourhoodWorld(FrameSeconds(state));
            }

            //B8's world lighting, applied to the lot view and to nothing else. Its own block and
            //its own flag read rather than a third arm on the one above, because the world clock
            //is B1's and a player who asked for B6 alone has asked for a clock, not for a sky. The
            //method decides for itself whether the thing being lit is a lot or the map screen.
            if (NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World))
            {
                UpdateNeighbourhoodWorldLighting();
            }


            if (state.NewKeys.Contains(Microsoft.Xna.Framework.Input.Keys.F12) && GraphicsModeControl.Mode != GlobalGraphicsMode.Full2D)
            {
                GraphicsModeControl.ChangeMode((GraphicsModeControl.Mode == GlobalGraphicsMode.Full3D) ? GlobalGraphicsMode.Hybrid2D : GlobalGraphicsMode.Full3D);
            }

            if (SwitchLot > 0)
            {
                //travel is deferred to here so the VM finishes the tick that requested the
                //switch. there is only one VM, so leaving this lot means marshalling it out and
                //handing the state to the per-lot store keyed by its own house number - that is
                //what makes home -> downtown -> vacation -> home put every lot back as the
                //player left it instead of reloading downtown/vacation from the template.
                //
                //B3 (NeighbourhoodWorldTravel) performs the very same five steps, in the same
                //order, behind ILotTravelHost - see LotTravelService's remarks for why the order
                //is the feature. The inline body is kept verbatim underneath rather than deleted,
                //because with the capability off this screen must behave byte for byte as it did
                //before Part B, and a "temporary" second copy that outlives its flag is how two
                //lot-preservation behaviours come to exist.
                if (NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel))
                {
                    PerformLotSwitch(SwitchLot);
                }
                else
                {
                    var currentHouse = (short)vm.GetGlobalValue(10);
                    if (LotTravel.ShouldStashCurrentLot(currentHouse, SwitchLot))
                    {
                        //§5.2's handshake, on the way out of a lot, on this path as well as the
                        //service's. With B1 off both are one comparison, and this is the same lot
                        //being stashed in the same place it always was.
                        HandshakeClockOnSave();
                        SessionLots.SetActive(currentHouse, vm.Save());
                    }
                    //the lot about to load is "downtown" (i.e. not a home lot) whenever it is not
                    //the active family's own house. InitializeLot reads this to decide whether to
                    //select the family and whether to apply the downtown time.
                    Downtown = SwitchLot != (ActiveFamily?.HouseNumber ?? SwitchLot);

                    VMMarshal preserved;
                    if (SessionLots.TryGetActive((short)SwitchLot, out preserved))
                    {
                        //a preserved copy exists: resume that lot as it was and drop the entry so a
                        //later visit reloads it from disk rather than replaying a stale snapshot.
                        //this is the highest-precedence source of lot state - an in-session snapshot
                        //always beats the on-disk cross-session slot, which InitializeLot(string,
                        //bool) only falls back to when the stash has nothing for this lot.
                        TakeStashedLot((short)SwitchLot);
                        //SwitchLot is the lot number either way, which is the whole point of passing
                        //it: the generated neighbourhood can only be placed around a lot it can name
                        InitializeLot(preserved, SwitchLot);
                    }
                    else
                    {
                        //first visit this session (or the entry was spent): load from House{NN}.iff -
                        //or, for a travel lot, from the family's own TravelLots slot if one was saved
                        //in an earlier session. SwitchLot is the lot number either way.
                        InitializeLot(Content.Get().Neighborhood.GetHousePath(SwitchLot), false, SwitchLot);
                    }
                }
                //cleared last, on both paths and for the same reason as before: the two loads above
                //read SwitchLot as the destination lot number, so clearing it earlier would take
                //the number with it. A refused request clears it too, which is what stops one
                //impossible lot number being retried every frame for ever.
                SwitchLot = -1;
            }
            //vm.Context.Clock.Hours = 12;
            if (vm != null) vm.Update();
            UpdateLotDiagnostics();
        }

        /// <summary>
        /// The frame time this screen should measure the weather against.
        /// </summary>
        /// <remarks>
        /// The engine's own clock is not used for the thunder gap: it pauses with the game, so a
        /// thunder roll would freeze in place and then fire a burst on resume. The wall clock is
        /// what "every five to fifteen seconds" means.
        /// </remarks>
        /// <param name="state">This frame's update state.</param>
        private static float FrameSeconds(FSO.Common.Rendering.Framework.Model.UpdateState state)
        {
            var elapsed = state?.Time?.ElapsedGameTime.TotalSeconds;
            if (!elapsed.HasValue) return 1f / 60f;
            return LotWeatherState.ClampDelta((float)elapsed.Value);
        }

        /// <summary>
        /// Drives the cosmetic weather for this lot: hands the lot its saved weather once the
        /// blueprint exists, then turns the engine's weather state into rain, thunder and
        /// snowed-in-terrain actions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The saved weather is applied here rather than at load time because a blueprint load is
        /// queued as a VM command: on the frame the lot is created there is no blueprint to write
        /// it to yet, and the lot would silently come up in whatever the last lot was doing.
        /// </para>
        ///
        /// <para>
        /// This is a no-op with no lot. The neighbourhood map has no VM at all - the picker draws
        /// itself - so the weather belongs to lots only, and the map is where the player is sent
        /// to get away from it.
        /// </para>
        /// </remarks>
        /// <param name="deltaSeconds">How long this frame was, from <see cref="FrameSeconds"/>.</param>
        private void UpdateWeatherEffects(float deltaSeconds)
        {
            var weather = vm?.Context?.Blueprint?.Weather;
            if (weather == null) return;

            if (Weather.TryTakePendingWeatherData(out short pendingWeather))
            {
                //the engine recomputes intensity and particles on its next update, so the sample
                //read below is one frame behind the data just handed over. That is harmless: the
                //frame after this one sees the change and reacts to it.
                weather.SetWeather(pendingWeather);
                SimitoneLog.Info("Weather set to " + WeatherCommandParser.Describe(pendingWeather) + " for this lot.");
            }

            var sample = new LotWeatherState.Sample(
                weather.WeatherType,
                weather.IsThunder,
                weather.WeatherIntensity,
                //0 is pause and GameplaySpeed.Disabled (-1) is buy/build - neither is gameplay
                //advancing, and both mean the rain bed should hold its breath.
                vm.SpeedMultiplier > 0);

            ApplyWeatherAction(Weather.Tick(sample, deltaSeconds), sample.Intensity);
        }

        /// <summary>
        /// Applies what <see cref="LotWeatherState.Tick"/> asked for, in the order that keeps a
        /// stop from racing the start that replaces it.
        /// </summary>
        /// <param name="action">The actions to apply.</param>
        /// <param name="intensity">This frame's weather intensity, for the rain and thunder levels.</param>
        private void ApplyWeatherAction(WeatherAudioAction action, float intensity)
        {
            if (action == WeatherAudioAction.None) return;

            //stop before start: a stop and a start in one result is a weather type change, and
            //two loops at once would leave the old one running forever.
            if ((action & WeatherAudioAction.StopRain) != 0) WeatherSounds.StopRain();
            if ((action & WeatherAudioAction.PlayRain) != 0) WeatherSounds.PlayRain(intensity);
            //after the start, so a rain bed that begins while the game is paused is created and
            //then paused in the same frame rather than playing under a pause.
            if ((action & WeatherAudioAction.PauseRain) != 0) WeatherSounds.PauseRain();
            if ((action & WeatherAudioAction.ResumeRain) != 0) WeatherSounds.ResumeRain();
            if ((action & WeatherAudioAction.PlayThunder) != 0)
            {
                WeatherSounds.PlayThunder(intensity * LotWeatherState.ThunderVolumeScale);
            }

            var snow = action & (WeatherAudioAction.ApplySnow | WeatherAudioAction.ClearSnow);
            if (snow != 0)
            {
                var terrain = vm?.Context?.Blueprint?.Terrain;
                if (terrain != null)
                {
                    //0 is winter (grass becomes snow), 1 is summer (snow becomes grass) - the
                    //same tuning values the engine's own snow override uses.
                    //TODO: this ignores the snowed-in state a vacation lot ships with, so snowing
                    //on one of those lots un-snows it when the weather stops.
                    terrain.ForceSnow((snow & WeatherAudioAction.ApplySnow) != 0 ? 0f : 1f);
                    terrain.UpdateLotType();
                    terrain.TerrainDirty = true;
                }
            }
        }

        /// <summary>
        /// Records the weather this lot is playing, so the next launch comes back to it.
        /// </summary>
        /// <remarks>
        /// Written after the lot save rather than before it, and only when there is a blueprint to
        /// read: a sidecar holding a guessed "clear" would silently overwrite the weather the
        /// player last chose the next time any lot was loaded.
        /// </remarks>
        private void PersistWeather()
        {
            var data = vm?.Context?.Blueprint?.Weather?.WeatherData;
            if (!data.HasValue) return;
            LotWeatherState.WriteSidecar(LotWeatherState.SidecarPath(FSOEnvironment.UserDir), data.Value);
        }

        public override void PreDraw(UISpriteBatch batch)
        {
            base.PreDraw(batch);
            vm?.PreDraw();
        }

        public void CleanupLastWorld()
        {
            //the rain bed is stopped before the "no lot" early return below: a loop left running
            //would follow the player off the lot and keep going on the neighbourhood map. The
            //lot's snowed-in terrain is left alone on purpose - it dies with the world.
            ApplyWeatherAction(Weather.EndLot(), 0f);

            //The generated neighbourhood goes before that early return for the same reason: a
            //teardown that finds no VM has still got a World with a surround hanging off it if
            //anything went wrong on the way up, and that surround is holding vertex buffers,
            //textures and a facade cache. The generated world itself is kept - it is the same
            //world the neighbourhood map draws and it holds no device resources - so walking
            //between two lots in one neighbourhood does not regenerate it.
            DetachSurroundings();

            if (vm == null) return;

            //clear our cache too, if the setting lets us do that
            TimedReferenceController.Clear();

            if (ZoomLevel < 4) ZoomLevel = 5;

            //B2's hollow neighbours, before anything is unhooked and long before vm is dropped.
            //A SubWorldComponent owns a light map, an ambient light texture and a world
            //architecture - all of them graphics resources - so leaving a handful alive across a
            //hundred lot switches is a device exhaustion with nothing to attribute it to. It has
            //to happen here rather than on the way out of a switch, because a switch tears the old
            //lot down through InitializeLot, which is the only other place this screen reaches a
            //blueprint that could be holding them. Harmless with the capability off: the call
            //walks an empty list, so there is nothing for it to do and nothing for it to log.
            SimitoneSurroundingsRestore.DisposeSubWorlds(vm);

            vm.Context.Ambience.Kill();
            foreach (var ent in vm.Entities)
            { //stop object sounds
                var threads = ent.SoundThreads;
                for (int i = 0; i < threads.Count; i++)
                {
                    threads[i].Sound.RemoveOwner(ent.ObjectID);
                }
                threads.Clear();
            }
            //unhook the events InitializeLot attached. without this the VM keeps a reference to
            //this screen through its handlers, and a later subscription would call back into a
            //half torn down world. detaching twice is harmless (-= is a no-op when absent).
            vm.OnFullRefresh -= VMRefreshed;
            vm.OnRequestLotSwitch -= VMLotSwitch;
            vm.OnGenericVMEvent -= Vm_OnGenericVMEvent;
            //and the minigame host, which is this screen's child. The subscription is dropped
            //before the host is, so nothing can be signalled into a half torn-down tree. The
            //panel is closed first: the sim's interaction thread is blocked on the plugin, and
            //walking off the lot is the one moment where that has to be resolved.
            if (LotControl?.EODs != null)
            {
                vm.OnEODMessage -= LotControl.EODs.OnEODMessage;
                LotControl.EODs.CloseActive();
                this.Remove(LotControl.EODs);
            }
            vm.CloseNet(VMCloseNetReason.LeaveLot);
            GameFacade.Scenes.Remove(World);
            World.Dispose();
            //LotControl.Dispose();
            this.Remove(LotControl);
            this.Remove(Frontend);
            vm.SuppressBHAVChanges();
            vm = null;
            World = null;
            Driver = null;
            LotControl = null;
            //the lot's UI (and every button holding a rendered head) went with LotControl, so
            //the head icons can be released rather than lingering until the 64-entry cap
            Simitone.Client.UI.Model.UIIconCache.ClearAvatarHeadCache();
        }

        /// <summary>
        /// Brings up an empty world and VM for a lot load. Every lot load goes through here -
        /// the template path, a travel switch and a marshal resume alike - which is why the
        /// weather is set up here rather than on any one of those paths.
        /// </summary>
        public void InitializeLot()
        {
            //Every route into a lot comes through here - a house picked on the map, a travel switch,
            //a marshalled lot, a lot launched from CAS - so this is where the neighbourhood view is
            //taken down. NeighborhoodService.SuspendNeighbourhoodForLot removes the panel (and with
            //it the generated 3D world); the connected-mechanics "park it and keep it resident"
            //behaviour was reverted, so leaving a lot builds a fresh view.
            NeighborhoodService.SuspendNeighbourhoodForLot();

            CleanupLastWorld();
            World = new FSO.LotView.World(GameFacade.GraphicsDevice);

            //B8's per-lot lighting latch. A new lot view has never been lit by the world's clock,
            //so the first frame must apply rather than compare against whatever the lot the player
            //just left was written at - which is why it is cleared here, on the one path every load
            //route takes, rather than in the lighting method. A latch with a Reset that nothing calls
            //is a latch that outlives its lot, and the failure is silent: a lot left with the
            //previous lot's sky.
            LightingApplied.Reset();

            //The lot now being played, before anything below asks for the world: a world held for
            //another neighbourhood is saved and dropped, and the world's active lot moves with the
            //player so the off-screen simulator never decays the lot they are standing in.
            SetPlayedLot(LoadingHouseNumber ?? 0);

            //The lot diagnostics' per-lot state, cleared for the same reason and on the same path:
            //a new lot has a new speed, a new tick stream and a new set of waiting threads, so the
            //last lot's answers would be reported as this lot's changes.
            _diagSpeed = int.MinValue;
            _diagLastTick = 0;
            _diagStallFrames = 0;
            _diagReportedStall = false;
            _switchPausedVm = null;
            _switchResumeSpeed = -1;

            //The generated neighbourhood. The lot view reads World.Surroundings from its very first
            //frame, so a world that is already in memory is attached here; one that is not is
            //generated on a worker and attached when it lands, which is what keeps the 512x512
            //raster off the lot load. Gated on the NeighbourhoodView setting and on FreeSO's own
            //surrounding-lot detail, both through SimitoneSurroundingsSelection - with either of
            //them off this returns without building a layout, a map, a mesh or a facade, and the
            //lot is exactly the lot it has always been.
            AttachSurroundings();
            //consumed: the next load has to say its own lot number rather than inherit this one
            LoadingHouseNumber = null;

            //the weather this lot should open with, applied by UpdateWeatherEffects as soon as
            //the blueprint exists. no sidecar means "leave the engine's own default alone".
            Weather.BeginLot(LotWeatherState.ReadSidecar(LotWeatherState.SidecarPath(FSOEnvironment.UserDir)));

            World.Opacity = 1;
            GameFacade.Scenes.Add(World);

            //B5's global link, when the NeighbourhoodWorldServices capability is on: the stub with
            //two things injected rather than reached for - the family resolver (so a transaction
            //is charged to the family the avatar's own family number names, not to whichever family
            //the lot happens to have activated) and the lot-transition hook (so a local player
            //leaving their own lot sets the switch instead of shutting the lot's driver down and
            //stranding them). The hook only assigns SwitchLot; the VM's own lot-switch signal
            //reaches VMLotSwitch right after it and resolves the "go home" sentinel, so the two
            //agree by construction rather than by being kept in step. With the flag off this is
            //the stub, exactly as before.
            IVMTSOGlobalLink globalLink;
            if (NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Services))
            {
                globalLink = new SimitoneGlobalLink(
                    ResolveFamilyByChunkId,
                    houseId => SwitchLot = houseId,
                    true);
            }
            else
            {
                globalLink = new VMTS1GlobalLinkStub();
            }
            Driver = new VMServerDriver(globalLink);

            vm = new VM(new VMContext(World), Driver, new UIHeadlineRendererProvider());
            vm.ListenBHAVChanges();
            vm.Init();

            //§5.2's handshake, on the way into a lot. The three load routes converge here - a
            //template reset and a marshal resume both call this method first - so this is the one
            //place a freshly constructed VM is guaranteed to have the neighbourhood's time. A no-op
            //with the capability off: one cached ini read and a comparison.
            HandshakeClockOnLoad();

            //B2's hollow neighbours are NOT built here. This used to be the only call, and it could
            //not have worked: on the frame a lot is constructed there is no blueprint, and a
            //SubWorldComponent can only be created on a lot view's device and added to that
            //blueprint, so the bridge answered with its own "the lot has no blueprint" and the
            //street stayed facades. It is asked instead from every load path once the blueprint
            //exists - after BlueprintReset below, after LotLifecycleService.ResumeFromMarshal, and
            //from the per-frame pump in Update for the frames on which a queued restore or a late
            //generation lands - and the first of those to find both a blueprint and a placement is
            //the one that builds them.

            //the engine's content pass has finished by the time a lot is loaded, so anything that
            //failed to load is known now. deferred a frame because this is called from the
            //neighbourhood map's button handler and from Update (a travel switch), and a dialog
            //put up from inside either of those lands in the middle of the frame's own update.
            GameThread.NextUpdate(state => FailedContentService.ReportFailedContentOnce());

            LotControl = new UILotControl(vm, World);
            this.AddAt(0, LotControl);

            //The end-of-day minigame host. It is parented here rather than by the lot control
            //because an EOD is modal over the whole lot: it has to draw above the frontend, which
            //the lot control sits under. The host raises itself again each time a panel opens, so
            //the order it is added in does not matter.
            //
            //The host is always constructed, so this is unconditional; whether the framework
            //actually started is EODHost.Started, and a host that did not start is invisible and
            //drops every message. On a TS1 install that is the normal state today - the EOD UI
            //layout scripts ship with The Sims Online, not The Sims 1 - and the reason is in the
            //log rather than on screen. See EODPluginPolicy.
            this.Add(LotControl.EODs);

            if (m_ZoomLevel > 3)
            {
                World.Visible = false;
                LotControl.Visible = false;
            }

            ZoomLevel = Math.Max(ZoomLevel, 4);

            if (IDEHook.IDE != null) IDEHook.IDE.StartIDE(vm);

            vm.OnFullRefresh += VMRefreshed;
            //This is the line that was commented out, and it is the whole of the EOD back-port on
            //the client side. The minigame plugins have been running in the VM the entire time -
            //Simitone drives the lot with FreeSO's server VM, so VMEODHost is constructed, ticked
            //every frame, and hands out plugin ids - and every message they sent was being
            //dropped because nothing listened for them.
            //
            //Unconditional because the host is always there: one that did not start drops the
            //message, so the lot behaves exactly as it did before the port and there is no branch
            //here to get wrong. A host that never starts is indistinguishable, from the lot's
            //point of view, from one that was never written.
            vm.OnEODMessage += LotControl.EODs.OnEODMessage;
            vm.OnRequestLotSwitch += VMLotSwitch;
            vm.OnGenericVMEvent += Vm_OnGenericVMEvent;

            LogLotDiagnostics("lot loaded");
        }

        // ---- lot diagnostics -----------------------------------------------------------------------
        //
        // One line per lot load and one line per change, and nothing per frame. The report exists
        // because "a sim's interaction was queued and never ran" has three quite different causes
        // that look identical from the outside - the lot is paused, the VM is stopped loading, and
        // a thread is blocked on an async answer that will never come - and the one line below
        // names which of the three it is.

        private int _diagSpeed = int.MinValue;
        private uint _diagLastTick;
        private int _diagStallFrames;
        private bool _diagReportedStall;

        /// <summary>
        /// How many frames the VM's tick stream may stand still before that is worth a line.
        ///
        /// <para>
        /// Four seconds' worth at 60fps. The tick stream genuinely stands still whenever
        /// <c>SpeedMultiplier &lt;= 0</c> - <c>VMServerDriver.Tick</c> only advances its tick id
        /// at a positive speed - so a paused lot is not a fault and must not be reported as one.
        /// Four seconds is long enough that a lot sitting in a dialog, a buy mode or a loading
        /// screen is not reported, and short enough that a player who has already noticed
        /// something is wrong will not have to wait to find out whether the VM is moving.
        /// </para>
        /// </summary>
        private const int LotDiagnosticsStallFrames = 240;

        /// <summary>
        /// The per-frame half of the diagnostics: a speed change, or a tick stream that has stood
        /// still for <see cref="LotDiagnosticsStallFrames"/>. Two integer comparisons and a
        /// reference read on the common path.
        /// </summary>
        private void UpdateLotDiagnostics()
        {
            if (vm == null) return;
            var lastTick = Driver?.LastTick ?? 0;

            if (vm.SpeedMultiplier != _diagSpeed)
            {
                _diagSpeed = vm.SpeedMultiplier;
                _diagStallFrames = 0;
                _diagReportedStall = false;
                LogLotDiagnostics("gameplay speed changed");
                return;
            }

            if (lastTick == _diagLastTick)
            {
                if (++_diagStallFrames < LotDiagnosticsStallFrames || _diagReportedStall) return;
                _diagReportedStall = true;
                LogLotDiagnostics("the lot's tick has not moved for about four seconds");
                return;
            }

            _diagLastTick = lastTick;
            if (_diagStallFrames > 0) _diagStallFrames = 0;
            if (!_diagReportedStall) return;
            _diagReportedStall = false;
            LogLotDiagnostics("the lot's tick is moving again");
        }

        /// <summary>
        /// One line naming the state a "nothing is happening" report needs, and why.
        /// </summary>
        /// <param name="cause">Why this line is being written, in a few words.</param>
        private void LogLotDiagnostics(string cause)
        {
            try
            {
                var clock = vm?.Context?.Clock;
                //read, never built: a diagnostic must not be what rasterises a neighbourhood
                var world = WorldSession.World;
                var waiting = DescribeWaitingThreads();

                SimitoneLog.Info("lot diagnostics (" + cause + "): speed="
                    + (vm?.SpeedMultiplier.ToString() ?? "no lot")
                    + " (" + SpeedName(vm?.SpeedMultiplier) + ")"
                    + ", ready=" + (vm?.Ready.ToString() ?? "-")
                    + ", asyncLoading=" + (vm?.FSOVAsyncLoading.ToString() ?? "-")
                    + ", clockTicksPerMinute=" + (clock?.TicksPerMinute.ToString() ?? "-")
                    + ", clock=" + (clock == null ? "none" : clock.Hours + ":" + clock.Minutes
                        + "+" + clock.MinuteFractions)
                    + ", worldClockTicksPerMinute=" + (world == null ? "none"
                        : world.Clock.TicksPerMinute.ToString())
                    + ", worldClock=" + (world == null ? "none" : world.Clock.ToString())
                    + ", tick=" + (Driver?.LastTick.ToString() ?? "-")
                    + "/" + (Driver?.GreatestTick.ToString() ?? "-")
                    + ", entities=" + (vm?.Entities.Count.ToString() ?? "-")
                    + ", waiting=" + waiting
                    + ", archQueue=" + DescribeArchitectureQueue()
                    + ", flags=" + NeighbourhoodWorldFlags.DescribeSet());
            }
            catch (Exception e)
            {
                //A diagnostic must not be able to be the thing that breaks a frame.
                SimitoneLog.Warn("lot diagnostics could not be collected.", e);
            }
        }

        /// <summary>
        /// The gameplay speed as a name rather than a number, so a reader does not have to know
        /// that 0 is pause and -1 is "build mode".
        /// </summary>
        private static string SpeedName(int? speed)
        {
            if (!speed.HasValue) return "no lot";
            switch (speed.Value)
            {
                case 0: return "paused";
                case -1: return "build/buy, held";
                case 1: return "play";
                case 3: return "speed 2";
                case 10: return "speed 3";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Which of this lot's sims are sitting in an async wait, and on what, as one clause.
        /// Sampled only when a line is written, because it walks every entity.
        /// </summary>
        private string DescribeWaitingThreads()
        {
            if (vm?.Entities == null) return "no lot";
            var counts = new Dictionary<string, int>();
            var avatars = 0;
            foreach (var ent in vm.Entities)
            {
                if (!(ent is VMAvatar)) continue;
                avatars++;
                var blocking = ent.Thread?.BlockingState;
                if (blocking == null) continue;
                var name = blocking.GetType().Name;
                counts.TryGetValue(name, out var n);
                counts[name] = n + 1;
            }
            if (counts.Count == 0) return "none of " + avatars;
            var names = new List<string>();
            foreach (var pair in counts) names.Add(pair.Key + "x" + pair.Value);
            names.Sort();
            return string.Join(",", names.ToArray()) + " of " + avatars;
        }

        /// <summary>
        /// The Part B global link's architecture back-pressure, when the link has one. Zero is the
        /// answer for the stub and for no lot, and is not a fault.
        /// </summary>
        private string DescribeArchitectureQueue()
        {
            var link = Driver?.GlobalLink as SimitoneGlobalLink;
            if (link == null) return "n/a";
            return link.PendingArchitectureCommands + (link.WaitingOnArchitecture ? ", waiting" : "");
        }

        public void InitializeLot(VMMarshal marshal)
        {
            //a marshal carries no lot number this screen can read before the world is up, so the
            //default answer is "no lot number" and the lot plays without a generated
            //neighbourhood. The travel switch knows the number and uses the overload below; see
            //LoadingHouseNumber. Assigned rather than left alone, so a marshal load can never
            //inherit the previous lot's number.
            LoadingHouseNumber = null;
            LotLifecycleService.ResumeFromMarshal(marshal);

            //B2's hollow neighbours, on the marshal path. Asked immediately after the call above,
            //because that is what builds the lot's own blueprint, and it may still defer on a resume
            //that queues the blueprint rather than writing it - which the per-frame pump in Update
            //then picks up. It will also decline, quietly and without a line in the log, on this
            //overload in particular: a load that named no lot number has no placement to anchor a
            //plan to, and that is the same outcome as the setting being off.
            AttachHollowNeighbours();
        }

        /// <summary>
        /// Resumes a marshalled lot whose number the caller already knows, so the generated
        /// neighbourhood can be placed around it. §7.4.
        /// </summary>
        /// <param name="marshal">The lot state to resume.</param>
        /// <param name="houseNumber">
        /// The lot number, or <c>null</c> for a lot whose number the caller does not know - which
        /// is the same outcome as <see cref="InitializeLot(VMMarshal)"/>, not a different one.
        /// </param>
        public void InitializeLot(VMMarshal marshal, int? houseNumber)
        {
            LoadingHouseNumber = houseNumber;
            LotLifecycleService.ResumeFromMarshal(marshal);

            //B2's hollow neighbours, on the resume path every travel switch and every lot-picker
            //arrival comes through. After the call above, because that is what builds the lot's own
            //blueprint; may still defer on a resume that queues the blueprint rather than writing
            //it, which the per-frame pump in Update then picks up.
            AttachHollowNeighbours();

            //§5.2's handshake again, and the only one that matters for a resume: the marshal's own
            //clock was loaded into the VM by the call above, so this is the first moment there is
            //a real lot time to seed an unset neighbourhood with - or, with a world that has been
            //running, the first moment this lot can be put at the neighbourhood's time rather than
            //at wherever this snapshot was taken.
            HandshakeClockOnLoad();
        }

        public void ShowLoadErrors(List<VMLoadError> errors, bool verbose)
        {
            var errorMsg = GameFacade.Strings.GetString("153", "16");

            if (verbose)
            {
                errorMsg += "\n";
                foreach (var error in errors)
                {
                    errorMsg += "\n" + error.ToString();
                }
            }

            //signal thru the VM so we can stop time appropriately
            vm.LastSpeedMultiplier = vm.SpeedMultiplier;
            vm.SpeedMultiplier = 0;
            vm.SignalDialog(new VMDialogInfo
            {
                Block = true,
                Caller = null,
                // TS1's own table 153 has an OK, but this dialog is raised by the VM rather than
                // drawn by a UILabel, so the caption has to be supplied here - and it was a
                // literal. Id 440 of Simitone's own table, with the English as the second
                // argument, so a build with no table still says "OK".
                Yes = SimitoneStrings.Get("440", "OK"),
                DialogID = 0,
                Title = GameFacade.Strings.GetString("153", "17"),
                Message = errorMsg,
            });

        }

        /// <summary>
        /// Loads a lot from its <c>House{NN}.iff</c>, after giving a cross-session travel slot
        /// the chance to take priority over the shipped template.
        /// </summary>
        /// <param name="lotName">Path to the lot's house file.</param>
        /// <param name="external">True to load without any neighbourhood context (unused by
        /// Simitone's own callers; the template load is skipped entirely).</param>
        public void InitializeLot(string lotName, bool external)
        {
            InitializeLot(lotName, external, null);
        }

        /// <summary>
        /// Loads a lot from its <c>House{NN}.iff</c> or, for a travel lot, from the family's own
        /// saved slot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The decision has to be made before the lot exists, because the two sources disagree
        /// about what the lot is: the template knows nothing about the player, while a slot was
        /// written from a real session. The VM's global 10 (house number) only becomes the
        /// destination lot's number once a lot has been loaded, so it cannot answer the question
        /// beforehand - hence <paramref name="houseId"/>, which the two callers that know it pass
        /// in, with the house path itself as the fallback.
        /// </para>
        ///
        /// <para>
        /// Precedence, once, explicitly:
        /// <list type="number">
        /// <item>the in-session stash (<c>SessionLots</c>) wins for any lot currently held in
        /// memory - <c>Update</c> checks it and calls <see cref="InitializeLot(VMMarshal)"/>
        /// before this method is ever reached;</item>
        /// <item>otherwise a valid slot for the active family wins, and the marshal is handed to
        /// the same <see cref="InitializeLot(VMMarshal)"/> the in-session path uses;</item>
        /// <item>otherwise the shipped template is loaded with <c>BlueprintReset</c>, exactly as
        /// before.</item>
        /// </list>
        /// A slot is only honoured for a travel lot whose header names the family now being
        /// played, so a slot can never be loaded into the family's home lot or into another
        /// family's game. Every failure mode - unreadable file, bad magic, wrong version, foreign
        /// family, truncated marshal - falls back to the template with a logged warning; this
        /// method never throws on a bad slot, because failing to load a lot would be far worse
        /// than losing a saved one.
        /// </para>
        /// </remarks>
        /// <param name="lotName">Path to the lot's house file.</param>
        /// <param name="external">True to load without any neighbourhood context.</param>
        /// <param name="houseId">The lot number when the caller already knows it, or null to
        /// recover it from <paramref name="lotName"/>.</param>
        public void InitializeLot(string lotName, bool external, int? houseId)
        {
            if (lotName == "" || lotName[0] == '!') return;

            //The lot number, worked out here rather than inside InitializeLot() because neither of
            //the two branches below can produce it later: the marshal branch has a house path and
            //no VM, and the template branch has neither. The house path is the fallback
            //TryRestoreTravelSlot already uses, so the two agree on what lot this is. Assigned
            //unconditionally - including when nothing can be parsed, which is then the honest
            //answer and the same outcome as the setting being off.
            int parsedFromPath;
            LoadingHouseNumber = houseId
                ?? (TravelSave.TryParseHouseIdFromPath(lotName, out parsedFromPath) ? parsedFromPath : (int?)null);

            VMMarshal slot;
            if (!external && TryRestoreTravelSlot(lotName, houseId, out slot))
            {
                //the family subset is transient state that is deliberately not saved into a
                //marshal, so it has to be re-established by whoever sets the load up - exactly
                //as the template branch below does, and under the same condition.
                if (!Downtown && ActiveFamily != null)
                {
                    ActiveFamily.SelectWholeFamily();
                }

                //resume the family-owned slot through the marshal path, exactly like a lot held
                //in the stash. BlueprintReset would instead load the shipped template and throw
                //away everything the player did here.
                InitializeLot(slot, LoadingHouseNumber);
                return;
            }

            InitializeLot();
            
            if (!external)
            {
                if (!Downtown && ActiveFamily != null)
                {
                    ActiveFamily.SelectWholeFamily();
                    vm.TS1State.ActivateFamily(vm, ActiveFamily);
                }
                BlueprintReset(lotName, null);

                //B2's hollow neighbours, on the template path. After BlueprintReset, because that
                //is the call that writes the blueprint - queued as a VM command, so this may still
                //find none and defer, which the per-frame pump in Update then picks up. Asked here
                //rather than at the end of InitializeLot() so a cached rebuild, which does write the
                //blueprint synchronously, is built on this frame rather than the next one.
                AttachHollowNeighbours();

                //§5.2's handshake, on the template path. The template load stamps the lot's clock
                //from its own SIMI globals - into the VM directly for a cached rebuild, or through
                //a queued VMBlueprintRestoreCmd for the blueprint itself - so this is where the lot
                //has a time for the first time. With the capability off this is one comparison and
                //the blueprint's own time is what the lot opens at, exactly as before.
                HandshakeClockOnLoad();

                if (vm.LoadErrors.Count > 0)
                {
                    //deferred a frame; if another lot has replaced this VM by then the errors belong
                    //to a lot that is gone, and ShowLoadErrors would signal a dialog on the wrong VM
                    var erroredVm = vm;
                    GameThread.NextUpdate((state) =>
                    {
                        if (vm == erroredVm) ShowLoadErrors(vm.LoadErrors, true);
                    });
                }

                CenterOnLotCentre();

                vm.MyUID = 65537;
                var settings = GlobalSettings.Default;
                var myClient = new VMNetClient
                {
                    PersistID = 1,
                    RemoteIP = "local",
                    AvatarState = new VMNetAvatarPersistState()
                    {
                        Name = settings.LastUser ?? "",
                        DefaultSuits = new VMAvatarDefaultSuits(settings.DebugGender),
                        BodyOutfit = settings.DebugBody,
                        HeadOutfit = settings.DebugHead,
                        PersistID = 1,
                        SkinTone = (byte)settings.DebugSkin,
                        Gender = (short)(settings.DebugGender ? 1 : 0),
                        Permissions = FSO.SimAntics.Model.TSOPlatform.VMTSOAvatarPermissions.Admin,
                        Budget = 1000000
                    }
                };

                if (Downtown)
                {
                    var ngbh = Content.Get().Neighborhood;
                    var crossData = ngbh.GameState;
                    var neigh = ngbh.GetNeighborIDForGUID(crossData.DowntownSimGUID);
                    if (neigh != null) {
                        var inv = ngbh.GetInventoryByNID(neigh.Value);
                        if (inv != null) {
                            var hr = inv.FirstOrDefault(x => x.Type == 2 && x.GUID == 7)?.Count ?? 0;
                            var min = inv.FirstOrDefault(x => x.Type == 2 && x.GUID == 8)?.Count ?? 0;
                            Driver.SendCommand(new VMNetSetTimeCmd()
                            {
                                Hours = hr,
                                Minutes = min,
                            });
                        }
                    }
                }

                var server = (VMServerDriver)Driver;
                server.ConnectClient(myClient);
                //surrounding lots are not implemented in this mode: the old LoadSurrounding was a
                //no-op (bare "return") that read a hard-coded developer path for its NBHm data.

                GameFacade.Cursor.SetCursor(CursorType.Normal);
                ZoomLevel = 1;
            }

            Frontend = new UISimitoneFrontend(this);
            this.Add(Frontend);
        }

        /// <summary>
        /// Points the 2D camera at the middle of the lot that was just loaded, and makes that
        /// point the pivot a rotation turns around.
        ///
        /// <para>
        /// A freshly constructed world starts centred on tile (0,0), which is the top left
        /// corner of the lot. For a lot whose contents are all somewhere in the middle - which is
        /// every residential lot, and most of downtown - that means the game opens on an empty
        /// corner of the world and the player has to scroll to find their own house.
        /// </para>
        ///
        /// <para>
        /// This is only the starting point, not the destination. <see cref="UILotControl"/> takes
        /// the camera again on the first frame it finds a controllable sim, centring on the sim
        /// (or, on a lot with nobody in it, on the mailbox or the middle of the lot), and a
        /// rotation is anchored here so that turning the lot a quarter turn spins it about its
        /// middle rather than about wherever the camera happened to be.
        /// </para>
        ///
        /// <para>
        /// 2D only: the 3D camera has its own position/target pair and is driven by
        /// <see cref="UILotControl"/>'s rotation and zoom handling, so there is nothing here to
        /// set that would not fight it.
        /// </para>
        /// </summary>
        private void CenterOnLotCentre()
        {
            if (World == null) return;
            if ((int)World.State.CameraMode >= (int)CameraRenderMode._3D) return; // 2D mode only

            var arch = World.Architecture?.Blueprint;
            if (arch == null) return;
            if (World.State.Camera2D == null) return; //camera not built yet - nothing to place

            //center on the center of the lot by default
            var anchor = new Vector3(arch.Width / 2, arch.Height / 2, 0);
            World.State.CenterTile = World.State.Project2DCenterTile(anchor);
            World.State.Camera2D.RotationAnchor = anchor;
        }

        /// <summary>
        /// Looks for a cross-session travel slot for the lot <paramref name="lotName"/> points at
        /// and deserialises it if - and only if - it is a travel lot whose slot belongs to the
        /// family now being played.
        /// </summary>
        /// <remarks>
        /// Every rejection is quiet apart from a log line, and every one of them means the same
        /// thing to the caller: no slot, load the template. That covers a null or empty
        /// <paramref name="lotName"/>, a house id that cannot be recovered, a home lot, a lot
        /// with no slot file, a slot from a different build or a different family, and a slot
        /// whose marshal payload is truncated or corrupt. Nothing here throws.
        /// </remarks>
        /// <param name="lotName">The house path the caller is about to load.</param>
        /// <param name="houseId">The lot number if the caller knows it, else null.</param>
        /// <param name="marshal">The deserialised lot state, or <c>null</c> when no slot is
        /// usable.</param>
        /// <returns><c>true</c> when a usable slot was loaded.</returns>
        private bool TryRestoreTravelSlot(string lotName, int? houseId, out VMMarshal marshal)
        {
            return SaveService.TryRestoreTravelSlot(lotName, houseId, ActiveFamily, out marshal);
        }

        public void BlueprintReset(string path, VM vm)
        {
            LotLifecycleService.BlueprintReset(path, vm);
        }


        private void Vm_OnGenericVMEvent(VMEventType type, object data)
        {
            switch (type)
            {
                case VMEventType.TS1BuildBuyChange:
                    Frontend?.ModeSwitcher?.UpdateBuildBuy();
                    Frontend?.DesktopUCP?.UpdateBuildBuy();
                    break;
            }
        }

        /// <summary>
        /// The VM's lot-switch signal: pause the lot so the tick that asked finishes, then name the
        /// lot to switch to. The switch itself happens in <c>Update</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The pause is only ever on behalf of a switch that is about to happen</b>, because the
        /// pause exists so the VM finishes the tick that requested the switch before the lot is
        /// torn down - see the switch block in <c>Update</c>. That block only runs for
        /// <c>SwitchLot &gt; 0</c>, and a destination this screen cannot act on leaves the pause
        /// behind with nothing to end it: <c>vm.SpeedMultiplier</c> stays 0 for the rest of the
        /// session, so nothing ticks and the lot reports as completely dead - no interactions, no
        /// build/buy, no autonomous sims. Two ways in: a family with no house, whose
        /// <c>HouseNumber</c> is 0, and a lot number that arrives as <c>uint.MaxValue</c> and
        /// truncates back to 0 as a <c>short</c>. So the pause is taken back when no switch is
        /// pending. It is the pre-switch speed that is restored, and only from a running speed, so
        /// a deliberately paused lot stays paused.
        /// </para>
        /// <para>
        /// <b>That freeze is a hazard the Part B flags turn from "unreachable" into "reachable".</b>
        /// Before them the only caller was a pay booth naming a real lot; with
        /// <see cref="NeighbourhoodWorldFlag.Services"/> on,
        /// <c>SimitoneGlobalLink.LeaveLot</c> raises this signal too, and it raises
        /// <c>(uint)-1</c> whenever the link cannot name a home lot of its own - which is exactly
        /// the family-with-no-house case above.
        /// </para>
        ///
        /// <para>
        /// <b>And a destination this screen cannot act on is the other way in, so the range is
        /// checked here as well as in the link.</b> The <c>-1</c> sentinel is resolved against
        /// <see cref="ActiveFamily"/>'s own house number, and a family's house number is not
        /// constrained to being a loadable lot. A number outside
        /// <see cref="LotTravelService.IsLotNumber"/> is treated as "no lot to switch to" and
        /// takes the same restoring branch a family with no house takes - because with
        /// <see cref="NeighbourhoodWorldFlag.Travel"/> on the alternative is
        /// <c>LotTravelService.TryTransition</c> refusing it a frame later, and a refusal there
        /// arrives <em>after</em> this pause. That used to be the frozen lot: the clock stopped
        /// (<c>VM.InternalTick</c> skips <c>Context.Clock.Tick</c> whenever
        /// <c>SpeedMultiplier &lt;= 0</c>), every autonomous sim stopped, and a script that was
        /// half way through serving a group meal stayed <c>Occupied</c> for the rest of the
        /// session, which is what makes the pie menu refuse every interaction on that object.
        /// </para>
        /// </remarks>
        private void VMLotSwitch(uint lotId)
        {
            var resuming = vm.SpeedMultiplier;
            vm.SpeedMultiplier = 0;
            if ((short)lotId == -1)
            {
                //A family that is not in a house of its own answers 0 rather than throwing. A
                //throw here would escape through the VM's event invoke into the caller's tick -
                //and, with the capability on, into a leave-lot request inside an interaction - and
                //would leave the pause above set, which is the frozen lot.
                lotId = (uint)(ActiveFamily?.HouseNumber ?? 0);
            }

            //same bound the travel service applies, asked here so a house number that is not a
            //lot is the "no lot to switch to" answer rather than a request that is refused a
            //frame later with this pause already taken.
            if (!LotTravelService.IsLotNumber((int)lotId))
            {
                SwitchLot = -1;
                if (resuming > 0)
                {
                    SimitoneLog.Info("A lot switch to lot " + lotId + " was asked for but this screen"
                        + " has no lot to switch to, so the lot keeps running at the speed it had.");
                    vm.SpeedMultiplier = resuming;
                }
                return;
            }

            //remembered so that a request which is consumed without a switch - a walk that
            //cannot start, or a travel destination the service refuses - can put the speed back
            //rather than leaving the lot paused with nothing left to end it.
            _switchPausedVm = vm;
            _switchResumeSpeed = resuming;
            SwitchLot = (int)lotId;
        }

        /// <summary>
        /// The lot <see cref="VMLotSwitch"/> paused, and the speed to put back if the request it
        /// paused the lot for turns out to be one no switch ever acts on.
        ///
        /// <para>
        /// Both are cleared on every lot load rather than on every restore, because the
        /// interesting case is a request that outlives its own switch: a walk consumes
        /// <c>SwitchLot</c> on the frame it starts and switches several frames later, and the
        /// reference test in <see cref="RestoreSpeedAfterFailedLotSwitch"/> is what tells the
        /// two apart - after a switch the screen's <c>vm</c> is a different object, so a late
        /// restore is a no-op rather than a resurrection of the old lot's speed.
        /// </para>
        /// </summary>
        private VM _switchPausedVm;
        private int _switchResumeSpeed = -1;

        /// <summary>
        /// Puts back the speed <see cref="VMLotSwitch"/> took, for a lot switch request that was
        /// consumed without a lot switch happening.
        ///
        /// <remarks>
        /// The whole of the frozen-lot guard. The pause is taken so the tick that asked for a
        /// switch can finish before the lot is torn down, so the only thing that may end it is a
        /// switch. Three things can consume a request without one:
        /// <list type="bullet">
        /// <item>a travel destination the service refuses - out of the lot range, or a lot with
        /// no house file, which <see cref="LotTravelService.TryTransition"/> answers by leaving
        /// the player exactly where they were;</item>
        /// <item>a walk that cannot start because one is already under way;</item>
        /// <item>a walk whose route could not be found.</item>
        /// </list>
        /// Each of them clears <c>SwitchLot</c> at the end of the block in <c>Update</c>, so
        /// without this the lot runs at <c>SpeedMultiplier == 0</c> until the session ends. Only
        /// from a speed that was running, so a deliberately paused lot stays paused, and only for
        /// the same <c>VM</c> the pause was taken on, so a switch that has already happened is
        /// never disturbed.
        /// </remarks>
        /// </summary>
        private void RestoreSpeedAfterFailedLotSwitch()
        {
            if (vm == null || !ReferenceEquals(_switchPausedVm, vm)) return;
            if (vm.SpeedMultiplier != GameplaySpeed.ReverseRemap[GameplaySpeed.Pause]) return;
            var resuming = _switchResumeSpeed;
            _switchPausedVm = null;
            _switchResumeSpeed = -1;
            if (resuming <= 0) return;
            SimitoneLog.Info("The lot switch that paused this lot did not happen, so it is running"
                + " at the speed it had (" + resuming + ") rather than staying paused.");
            vm.SpeedMultiplier = resuming;
            vm.ResetTickAlign();
        }

        private void VMRefreshed()
        {
            if (vm == null) return;
            LotControl.ActiveEntity = null;
            LotControl.RefreshCut();
        }

        public override bool CloseAttempt()
        {
            if (Dialogs.HasOpenDialog) return true;

            GameThread.NextUpdate(x =>
            {
                if (!Dialogs.TryBeginConfirm()) return;

                var canSave = vm != null;
                // This one dialog is deliberately not built through DialogService.ShowConfirm: its
                // yes and no answers call Game.Exit() and leave the dialog up and the latch held,
                // because the process is on its way out. Only cancel closes and releases.
                Dialogs.ShowModal(new UIMobileAlert(new FSO.Client.UI.Controls.UIAlertOptions
                {
                    Title = GameFacade.Strings.GetString("153", "1"), //quit?
                    Message = GameFacade.Strings.GetString("153", canSave?"6":"2"), //are you sure (2), save before quitting (3)
                    Buttons =
                    canSave?
                    UIAlertButton.YesNoCancel(
                        (b) => { Save(); GameFacade.Game.Exit(); },
                        (b) => { GameFacade.Game.Exit(); },
                        (b) => Dialogs.CloseAndRelease()
                        )
                    :
                    UIAlertButton.YesNo(
                        (b) => { GameFacade.Game.Exit(); },
                        (b) => Dialogs.CloseAndRelease()
                        )
                }));
            });
            return false;
        }

        public void ReturnToNeighbourhood()
        {
            if (!Dialogs.TryBeginConfirm()) return;

            Dialogs.ShowConfirm(
                GameFacade.Strings.GetString("153", "3"), //save
                GameFacade.Strings.GetString("153", "4"), //Do you want to save the game?
                () => { Save(); ExitLot(); },
                () => { ExitLot(); },
                null);
        }

        /// <summary>
        /// Saves the current lot.
        ///
        /// The house IFF is written atomically: the bytes are staged into "&lt;path&gt;.tmp",
        /// the previous good save is copied to "&lt;path&gt;.bak", and only then is the staged
        /// data copied over the real one. A crash (or an IO error) part way through therefore
        /// leaves the previous save intact at either the original path or the .bak, instead
        /// of truncating it.
        ///
        /// The neighbourhood file is only rewritten once the lot data has landed on disk.
        /// Any failure is logged and surfaced to the player as a dialog - Save never throws
        /// into the UI event handler that called it.
        ///
        /// Which file the lot data goes to depends on whether the lot is the family's own home
        /// (<see cref="LotPersistence.IsPersistableLot"/>):
        /// <list type="bullet">
        /// <item><b>home lot</b> - the canonical <c>House{NN}.iff</c>, as before: FSOV + SIMI +
        /// both thumbnails, committed atomically. The lot has a file of its own, so this is the
        /// whole save.</item>
        /// <item><b>travel lot</b> (downtown, vacation, studio, magic, or any lot that is not
        /// the family's home) - the shipped <c>House{NN}.iff</c> is a copy of a template that
        /// every family and every future visit starts from, so it is <em>never</em> written.
        /// The <c>VMMarshal</c> goes to the family's own slot at
        /// <c>&lt;UserDir&gt;/TravelLots/House{NN}.fsov</c> instead
        /// (<see cref="TravelSave.SlotPathFor"/>), committed with the same atomic contract, and
        /// <see cref="InitializeLot(string, bool)"/> resumes from it on the next visit. The
        /// slot's header records the owning family, so one family's vacation can never be loaded
        /// into another's game. The neighbourhood is saved on this path too - money, family and
        /// lot ownership do not live in the slot - and the save is reported as a success, because
        /// the player's lot was saved; it is only a real failure (a slot that could not be
        /// written) that raises the error dialog.</item>
        /// </list>
        ///
        /// <see cref="TravelSave.IsTravelLot"/> is the exact negation of
        /// <see cref="LotPersistence.IsPersistableLot"/>, so these two branches are exhaustive.
        /// The implementation is <see cref="SaveService"/>; the live VM, world and family are
        /// passed in per call so the screen stays their owner.
        /// </summary>
        public void Save()
        {
            //§5.2's handshake, on the way out of a lot that is being written to disk. Ahead of
            //SaveService rather than inside it: the service owns the bytes and this screen owns
            //the neighbourhood, and the time has to be handed over before the lot's own clock is
            //read into a marshal. A no-op with the capability off.
            HandshakeClockOnSave();

            SaveService.Save(vm, World, ActiveFamily);

            //B7: the neighbourhood's session goes to its sidecar once the lot has landed on disk,
            //so a lot that failed to save is not followed by a session claiming it did. The clock
            //was handed over above, and the world is saved with the player still in the lot.
            SaveNeighbourhoodWorld(WorldSession.World, WorldSession.Slot);

            //the cosmetic weather is a sidecar rather than part of the lot save: it is not lot
            //state (every lot, on every machine, opens in the same weather), so it does not belong
            //in the IFF or the travel slot. Written after the lot save so a failed lot save is
            //not reported as a success by a sidecar that did land.
            PersistWeather();
        }

        /// <summary>
        /// Encodes a lot thumbnail texture into its <c>PNG_</c> chunk. See
        /// <see cref="SaveService.GeneratePNG"/>.
        /// </summary>
        public PNG GeneratePNG(Texture2D data)
        {
            return SaveService.GeneratePNG(data);
        }

        public void ExitLot()
        {
            //the world first, while the lot's clock can still be read: its time is handed over, its
            //session is written (B7) and it is dropped. A world is a session object and the session
            //ends here.
            EndNeighbourhoodWorld();
            CleanupLastWorld();
            //the neighbourhood the lot was picked on, not always the residential one: a family
            //that bought a magic-town lot through a move-in, or a lot picked off the vacation or
            //studio map, comes back to the map that lot is on. CurrentNeighborhoodMode keeps its
            //value deliberately - it describes where the lot lives, not where the player is going.
            NeighSelection(CurrentNeighborhoodMode);
            Downtown = false;
            //every preserved lot belongs to the world that was just torn down; the marshals
            //are not disposable, so dropping the references is the whole cleanup.
            SessionLots.Clear();
        }
    }

    public enum NeighSelectionMode
    {
        Normal,
        MoveIn,
        MoveInMagic
    }
}
