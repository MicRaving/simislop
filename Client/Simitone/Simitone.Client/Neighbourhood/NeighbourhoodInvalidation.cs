/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Why a refresh of a generated neighbourhood is doing what it is doing, in the order the
    /// questions are asked.
    ///
    /// <para>
    /// §4.2 states the sidecar's invalidation rule in one line - "slot folder missing, drop;
    /// <c>generatorVersion</c> or <c>seed</c> changed, regenerate all; a lot's <c>houseHash</c>
    /// changed, regenerate that lot's placement and the roads/terrain that touch it" - and the
    /// three cheap answers are all that is in it. This enum names them so the decision is a value
    /// that can be asserted on rather than a stack of booleans that only means something in
    /// combination.
    /// </para>
    ///
    /// <para>
    /// <b>Order matters and is part of the contract.</b> A player who has switched the 3D
    /// neighbourhood off gets <see cref="SettingIsNotWorld3D"/> whatever is or is not on disk,
    /// because §0 decision 1 and §10 are the same promise: a player who never opted in pays for
    /// none of this. <see cref="SidecarRejected"/> and <see cref="SidecarStale"/> are separate
    /// because they are different failures - a file this build will not read is a different
    /// problem from a file packed by a different arrangement rule - and a player reporting "the
    /// map looks wrong" and a player reporting "the map never loads" need different lines in the
    /// log.
    /// </para>
    /// </summary>
    public enum NeighbourhoodRefreshKind
    {
        /// <summary>
        /// The setting is not <see cref="NeighbourhoodView.World3D"/>, so no world is built,
        /// nothing is read and nothing is regenerated. The 2D bitmap path, exactly as before.
        /// </summary>
        SettingIsNotWorld3D = 0,

        /// <summary>
        /// There is no reusable sidecar: first run, the folder is gone, or the file would not
        /// parse. Everything is packed from the neighbourhood's own files.
        /// </summary>
        NoSidecar = 1,

        /// <summary>
        /// A sidecar exists but stamps a schema this build will not stand behind. Treated exactly
        /// like <see cref="NoSidecar"/>, because the only safe reading of a file it does not
        /// understand is "there is nothing here".
        /// </summary>
        SidecarRejected = 2,

        /// <summary>
        /// A sidecar exists and parses, but it was packed with a different seed or a different
        /// arrangement rule, so it describes a different world.
        /// </summary>
        SidecarStale = 3,

        /// <summary>
        /// The sidecar is current and every lot's <c>houseHash</c> still matches: nothing to do.
        /// The in-memory raster is reused when it is there, and rebuilt when it is not - and only
        /// the raster, never the layout or the facades.
        /// </summary>
        LayoutUnchanged = 4,

        /// <summary>
        /// The sidecar is current, but some lots' <c>houseHash</c> moved - a lot was built in,
        /// bulldozed or rezoned. This is §4.2's cheap case, and the only one that is not "pack
        /// everything again": only the named lots are re-placed, and the rest of the arrangement
        /// is carried over untouched.
        /// </summary>
        LotsChanged = 5,

        /// <summary>
        /// A refresh for this slot is already running on a worker, or was just started by this
        /// one, and the raster is not in memory yet.
        ///
        /// <para>
        /// <b>This is a state, not a failure.</b> Nothing is wrong, nothing is being given up on,
        /// and the neighbourhood is open: the painted map carries the picture until the worker
        /// lands, and the finished world is applied on the game thread when it does. It is a
        /// separate member rather than a <c>Failure</c> because "a quarter of a million tiles are
        /// being generated right now" and "the road graph would not connect" are answers that
        /// want opposite things from a reader - one says wait, the other says this build cannot
        /// do it - and folding them into one value would make a log that cannot say which.
        /// </para>
        /// </summary>
        Refreshing = 6
    }

    /// <summary>
    /// What has to happen to bring a neighbourhood's generated world up to date, decided without
    /// touching a disk, a provider or a graphics device.
    ///
    /// <para>
    /// <b>Why this is a value and not a method body.</b> Every question the refresh has to answer
    /// is a question about facts someone else owns - the setting, the sidecar, the lot hashes -
    /// and each of them is expensive or unavailable in a test. Handing this type the answers turns
    /// the whole of §4.2's rule into an assertion: a lot whose hash moved regenerates <em>that
    /// lot only</em>, a setting that is not 3D does no work at all, and a failure is the 2D bitmap
    /// with the reason attached.
    /// </para>
    ///
    /// <para>
    /// <b>There is no "failed" state.</b> <see cref="BuildWorld"/> going false is either "the
    /// setting says 2D" or "generation failed", and <see cref="Selection"/> says which with a
    /// sentence. That is the plan's non-negotiable - "the 2D <c>UINeighborhoodSelectionPanel</c>
    /// bitmap path is kept and must still work if generation fails, is disabled, or the GPU cannot
    /// build the scene" - expressed as the absence of a third answer.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodRefreshPlan
    {
        private static readonly short[] NoLots = new short[0];

        private NeighbourhoodRefreshPlan(
            NeighbourhoodRefreshKind kind,
            NeighbourhoodViewSelection selection,
            bool buildWorld,
            bool reuseSidecar,
            bool patchOnlyChangedLots,
            IReadOnlyList<short> lotsToRegenerate,
            bool regenerateMap,
            bool regenerateFacades,
            string reason)
        {
            Kind = kind;
            Selection = selection;
            BuildWorld = buildWorld;
            ReuseSidecarLayout = reuseSidecar;
            PatchOnlyChangedLots = patchOnlyChangedLots;
            LotsToRegenerate = lotsToRegenerate ?? NoLots;
            RegenerateMap = regenerateMap;
            RegenerateFacades = regenerateFacades;
            Reason = reason;
        }

        /// <summary>Why this plan, as one of §4.2's cases.</summary>
        public NeighbourhoodRefreshKind Kind { get; }

        /// <summary>
        /// Which view the neighbourhood ends up drawn in, and why. Already resolved against
        /// whatever generation had to say, so a caller can log
        /// <see cref="NeighbourhoodViewSelection.Reason"/> without re-deriving anything.
        /// </summary>
        public NeighbourhoodViewSelection Selection { get; }

        /// <summary>
        /// Whether a generated world is being built at all. False for a 2D/off setting and false
        /// after a failure - in both cases <see cref="Selection"/> is the bitmap.
        /// </summary>
        public bool BuildWorld { get; }

        /// <summary>
        /// Whether the cached sidecar layout may be used as the base for this refresh. False means
        /// the neighbourhood is packed from scratch.
        /// </summary>
        public bool ReuseSidecarLayout { get; }

        /// <summary>
        /// Whether the layout is repaired in place - the carried-over rectangles kept, only the
        /// named lots re-placed - rather than packed whole. Only ever true for
        /// <see cref="NeighbourhoodRefreshKind.LotsChanged"/>.
        /// </summary>
        public bool PatchOnlyChangedLots { get; }

        /// <summary>
        /// The lots whose contribution is out of date, in ascending order. This is the "only that
        /// lot" set of §4.2, and it is also exactly the set of facades that have to be rebuilt.
        /// Empty for a full pack, where every lot is new by definition - see
        /// <see cref="RegenerateFacades"/>.
        /// </summary>
        public IReadOnlyList<short> LotsToRegenerate { get; }

        /// <summary>
        /// Whether the 512x512 raster has to be generated again. This is deliberately tied to the
        /// <em>layout</em> and not to the cache: a raster is a pure function of (layout, seed), so
        /// an unchanged layout means the raster already in memory is still the right one, and
        /// regenerating it would be a quarter of a million tiles of work to produce the same bytes.
        /// </summary>
        public bool RegenerateMap { get; }

        /// <summary>
        /// Whether any facade has to be rebuilt. Facades come from each lot's own
        /// <c>HouseNN.iff</c>, so they track <see cref="LotsToRegenerate"/> and not the layout as a
        /// whole: a lot that did not change keeps the mesh it had.
        /// </summary>
        public bool RegenerateFacades { get; }

        /// <summary>
        /// One sentence saying what this refresh is doing, for the log. Never null and never
        /// empty: a refresh that cannot explain itself is the one worth chasing.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Whether this refresh could reuse what was already built rather than redoing it. A
        /// sidecar hit that still has its raster in memory is the only true cache hit there is.
        /// </summary>
        public bool IsCacheHit
        {
            get
            {
                return Kind == NeighbourhoodRefreshKind.LayoutUnchanged
                    && !RegenerateMap
                    && !RegenerateFacades;
            }
        }

        /// <summary>
        /// Decides what a refresh has to do.
        /// </summary>
        /// <param name="requested">The setting, resolved; only <see cref="NeighbourhoodView.World3D"/> builds anything.</param>
        /// <param name="cached">The sidecar layout, or <c>null</c> when there is nothing reusable.</param>
        /// <param name="seed">The seed this neighbourhood's world is generated from.</param>
        /// <param name="generatorVersion">The arrangement rule this build packs with.</param>
        /// <param name="changedLots">
        /// Lots whose <c>houseHash</c> has moved since the cached layout was packed - see
        /// <see cref="NeighbourhoodLayoutStore.ChangedLots"/>. Null and empty both mean "none".
        /// </param>
        /// <param name="rasterCached">
        /// Whether a generated 512x512 map for <paramref name="cached"/> is still in memory. A
        /// false here is a reason to regenerate the raster, not to distrust the layout.
        /// </param>
        public static NeighbourhoodRefreshPlan Decide(
            NeighbourhoodView requested,
            NeighbourhoodLayout cached,
            int seed,
            int generatorVersion,
            IReadOnlyList<short> changedLots,
            bool rasterCached)
        {
            var view = NeighbourhoodViewSetting.Resolve(requested);
            var changed = changedLots ?? NoLots;

            //§0 decision 1 and §10: the setting is the whole gate. A player who has not opted in
            //does not read a sidecar, does not stat twenty HouseNN.iff files and does not generate
            //a tile, because all three of those happen before this call in the real pipeline and
            //the gate has to be able to say "none of that".
            if (view != NeighbourhoodView.World3D)
            {
                return new NeighbourhoodRefreshPlan(
                    NeighbourhoodRefreshKind.SettingIsNotWorld3D,
                    NeighbourhoodViewSelection.ForSetting(view),
                    false, false, false, NoLots, false, false,
                    "the 3D neighbourhood is " + NeighbourhoodViewSetting.Describe(view)
                    + ", so no world was built");
            }

            if (cached == null)
            {
                return Full(NeighbourhoodRefreshKind.NoSidecar, view,
                    "there was no cached layout for this neighbourhood, so it was packed from its own lots");
            }

            if (!cached.HasCurrentSchema)
            {
                return Full(NeighbourhoodRefreshKind.SidecarRejected, view,
                    "the cached layout stamps schema " + cached.SchemaVersion
                    + ", which this build does not understand, so it was packed again");
            }

            if (!cached.IsCurrentFor(seed, generatorVersion))
            {
                return Full(NeighbourhoodRefreshKind.SidecarStale, view,
                    "the cached layout was packed with seed " + cached.Seed
                    + " and layout generator " + cached.GeneratorVersion
                    + ", and this world is seed " + seed + " with layout generator "
                    + generatorVersion + ", so it was packed again");
            }

            if (changed.Count > 0)
            {
                //The sidecar is still current for this seed and generator, but some lot's house
                //file changed. NeighbourhoodLifecycle does not patch the sidecar in place (the
                //in-place patcher was removed with the connected-mechanics work): it re-packs the
                //whole shelf from the fresh reads, which is deterministic, and regenerates the
                //raster. LotsToRegenerate and the Reuse/PatchOnly flags therefore only describe
                //which lots triggered the refresh; the lifecycle consumes RegenerateMap alone.
                return new NeighbourhoodRefreshPlan(
                    NeighbourhoodRefreshKind.LotsChanged,
                    NeighbourhoodViewSelection.ForSetting(view),
                    true, true, true, Copy(changed), true, true,
                    "lot " + Join(changed) + " changed since the layout was packed, so the layout"
                    + " was packed again and the ground and roads were regenerated");
            }

            var hit = "the cached layout is current";
            return new NeighbourhoodRefreshPlan(
                NeighbourhoodRefreshKind.LayoutUnchanged,
                NeighbourhoodViewSelection.ForSetting(view),
                true, true, false, NoLots, !rasterCached, false,
                hit + (rasterCached
                    ? " and its generated map was reused"
                    : ", so the map was generated again"));
        }

        private static NeighbourhoodRefreshPlan Full(NeighbourhoodRefreshKind kind, NeighbourhoodView view, string reason)
        {
            //A full pack replaces every lot, so every lot's facade is stale as well.
            //LotsToRegenerate stays empty rather than being filled with the whole neighbourhood:
            //it means "these specific lots are what changed", and on a full pack the honest answer
            //is "all of them, because none of them is the same lot".
            return new NeighbourhoodRefreshPlan(
                kind, NeighbourhoodViewSelection.ForSetting(view),
                true, false, false, NoLots, true, true, reason);
        }

        /// <summary>
        /// The same plan after generation has had its say and it worked: the 3D selection, with the
        /// generated map's size in the reason.
        /// </summary>
        /// <param name="plan">The plan that was attempted.</param>
        /// <param name="map">The map that was generated.</param>
        public static NeighbourhoodRefreshPlan ForGeneratedMap(NeighbourhoodRefreshPlan plan, NeighbourhoodMap map)
        {
            if (!plan.BuildWorld) return plan;
            if (map == null) return ForFailure(plan, "no map was produced");
            return new NeighbourhoodRefreshPlan(
                plan.Kind, NeighbourhoodViewSelection.AfterGeneration(
                    plan.Selection.Requested, map, null),
                true, plan.ReuseSidecarLayout, plan.PatchOnlyChangedLots, plan.LotsToRegenerate,
                plan.RegenerateMap, plan.RegenerateFacades, plan.Reason);
        }

        /// <summary>
        /// The same plan after generation has had its say and it did not work: 3D was asked for
        /// and did not happen, so nothing is built, nothing is regenerated, and the bitmap is
        /// drawn for the stated reason.
        /// </summary>
        /// <param name="plan">The plan that was attempted.</param>
        /// <param name="failure">Why it failed, in one sentence.</param>
        public static NeighbourhoodRefreshPlan ForFailure(NeighbourhoodRefreshPlan plan, string failure)
        {
            var reason = string.IsNullOrEmpty(failure) ? "no map was produced" : failure;
            return new NeighbourhoodRefreshPlan(
                plan.Kind,
                NeighbourhoodViewSelection.AfterGeneration(plan.Selection.Requested, null, reason),
                false, plan.ReuseSidecarLayout, false, NoLots, false, false,
                "the generated world could not be built: " + reason);
        }

        /// <summary>
        /// The plan a refresh reports while it is still running, which is what a caller gets back
        /// the moment it asks for a world that is not there.
        ///
        /// <para>
        /// <b>What it claims and what it does not.</b> 3D is still what this neighbourhood will be
        /// drawn as, and the world still has to be built - so <see cref="BuildWorld"/> is true and
        /// nothing is being abandoned. It says nothing about the layout or the raster beyond that,
        /// because at this point nobody has looked: the refresh is on a worker, and a caller on the
        /// game thread must not be the thing that finds out. That is the whole difference from
        /// <see cref="ForFailure"/>, which is the same "there is no map yet" shape with the
        /// opposite meaning.
        /// </para>
        /// </summary>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="reason">What is happening, in one sentence, for the log.</param>
        public static NeighbourhoodRefreshPlan Refreshing(NeighbourhoodView requested, string reason)
        {
            var view = NeighbourhoodViewSetting.Resolve(requested);
            return new NeighbourhoodRefreshPlan(
                NeighbourhoodRefreshKind.Refreshing,
                NeighbourhoodViewSelection.ThreeD(view,
                    string.IsNullOrWhiteSpace(reason)
                        ? "the generated world is being built; the painted map carries the picture until it is"
                        : reason),
                true, false, false, NoLots, true, true, reason);
        }

        private static IReadOnlyList<short> Copy(IReadOnlyList<short> lots)
        {
            var copy = new short[lots.Count];
            for (int i = 0; i < lots.Count; i++) copy[i] = lots[i];
            return copy;
        }

        internal static string Join(IReadOnlyList<short> lots)
        {
            if (lots == null || lots.Count == 0) return string.Empty;
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < lots.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(lots[i]);
            }
            return text.ToString();
        }

        public override string ToString()
        {
            return Kind + " -> " + (BuildWorld ? "3D" : "2D") + ": " + Reason;
        }
    }

    /// <summary>
    /// What a request for a neighbourhood's generated world should do about it, in one of four
    /// answers. The middle two are the ones this type exists to keep apart.
    /// </summary>
    public enum NeighbourhoodScheduleAction
    {
        /// <summary>
        /// Nothing. The setting does not ask for a generated world, so nothing is read, nothing is
        /// generated and nothing is waited for.
        /// </summary>
        Nothing = 0,

        /// <summary>
        /// The world is already in memory and still describes what is on disk, so it is handed
        /// straight back. The only answer that lets the caller attach a scene on this frame.
        /// </summary>
        UseCache = 1,

        /// <summary>
        /// A refresh for this slot is already running on a worker. The caller shows the painted map
        /// and is handed the world when it lands; it must not start a second generation of the same
        /// neighbourhood.
        /// </summary>
        JoinRefresh = 2,

        /// <summary>
        /// Nothing cached and nothing running: start one. It is started here so that a second
        /// request in the same frame joins it rather than starting its own.
        /// </summary>
        StartRefresh = 3
    }

    /// <summary>
    /// Whether a request for a generated world can be answered now, must wait for one being
    /// built, or needs one started - as a value, decided without a disk, a provider, a clock or a
    /// thread.
    ///
    /// <para>
    /// <b>Why it is a value.</b> The three questions - is the setting on, is there a world, is one
    /// on its way - are all facts something else owns, and each of them used to be answered by
    /// doing work in the answer: a settings read, a 512x512 generation, a second set of twenty IFF
    /// reads. Handing the rule the three booleans turns "the panel never waits for generation" and
    /// "a cold neighbourhood is never generated twice at once" from claims into assertions, which
    /// is the only way either of them can be checked at all without a game running.
    /// </para>
    ///
    /// <para>
    /// <b>The order is the contract.</b> The setting is asked first, so a player who has not opted
    /// in cannot be made to wait for a refresh that was never going to run. The cache is asked
    /// before the in-flight flag, so a world that landed a frame ago is handed back rather than
    /// shadowed by a refresh that is about to finish anyway. Only when there is neither is anything
    /// started.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodSchedule
    {
        private NeighbourhoodSchedule(NeighbourhoodScheduleAction action, bool worldReady, string reason)
        {
            Action = action;
            WorldReady = worldReady;
            Reason = reason;
        }

        /// <summary>What to do about this request.</summary>
        public NeighbourhoodScheduleAction Action { get; }

        /// <summary>
        /// Whether the caller has a world it can build a scene from on this frame. True only for
        /// <see cref="NeighbourhoodScheduleAction.UseCache"/>; a refresh that is being started or
        /// joined is a world that does not exist yet, and the caller's answer to that is the
        /// painted map.
        /// </summary>
        public bool WorldReady { get; }

        /// <summary>Why this answer, in one sentence, for the log. Never null or blank.</summary>
        public string Reason { get; }

        /// <summary>Whether this request has to go to a worker, one way or the other.</summary>
        public bool IsAsynchronous
        {
            get
            {
                return Action == NeighbourhoodScheduleAction.JoinRefresh
                    || Action == NeighbourhoodScheduleAction.StartRefresh;
            }
        }

        /// <summary>
        /// Decides what a request for a generated world has to do.
        /// </summary>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="worldCached">
        /// Whether a world for this slot is in memory and still describes the layout on disk.
        /// </param>
        /// <param name="refreshRunning">
        /// Whether a refresh for this slot is already running and has not stopped yet.
        /// </param>
        public static NeighbourhoodSchedule Decide(
            NeighbourhoodView requested, bool worldCached, bool refreshRunning)
        {
            var view = NeighbourhoodViewSetting.Resolve(requested);

            if (view != NeighbourhoodView.World3D)
            {
                return new NeighbourhoodSchedule(NeighbourhoodScheduleAction.Nothing, false,
                    "the 3D neighbourhood is " + NeighbourhoodViewSetting.Describe(view)
                    + ", so no world was read, generated or waited for");
            }

            if (worldCached)
            {
                return new NeighbourhoodSchedule(NeighbourhoodScheduleAction.UseCache, true,
                    "the generated world for this neighbourhood is already in memory");
            }

            if (refreshRunning)
            {
                return new NeighbourhoodSchedule(NeighbourhoodScheduleAction.JoinRefresh, false,
                    "this neighbourhood is already being generated, so the painted map stays up"
                    + " until the finished world is applied");
            }

            return new NeighbourhoodSchedule(NeighbourhoodScheduleAction.StartRefresh, false,
                "there is no generated world for this neighbourhood yet, so one is being built"
                + " off the game thread and the painted map stays up until it is");
        }

        public override string ToString()
        {
            return (WorldReady ? "now" : "asynchronously") + " (" + Action + "): " + Reason;
        }
    }
}
