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
                //§4.2's cheap case. The layout as a whole is still valid, and the arrangement is
                //still the one the player has been looking at: only the lots named here are
                //re-placed. The raster follows, because the lots it rasterises have moved - but
                //the facades follow the same list, and nothing else is touched.
                return new NeighbourhoodRefreshPlan(
                    NeighbourhoodRefreshKind.LotsChanged,
                    NeighbourhoodViewSelection.ForSetting(view),
                    true, true, true, Copy(changed), true, true,
                    "lot " + Join(changed) + " changed since the layout was packed, so "
                    + (changed.Count == 1 ? "that lot" : "those lots")
                    + " and the ground and roads around "
                    + (changed.Count == 1 ? "it" : "them") + " were regenerated");
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

    /// <summary>What a layout repair did, and why - the receipt a caller logs and a test asserts on.</summary>
    public sealed class NeighbourhoodLayoutPatchResult
    {
        private static readonly short[] NoLots = new short[0];

        internal NeighbourhoodLayoutPatchResult(
            NeighbourhoodLayout layout,
            IReadOnlyList<short> kept,
            IReadOnlyList<short> replaced,
            IReadOnlyList<short> added,
            IReadOnlyList<short> dropped,
            bool repackedEverything,
            string reason)
        {
            Layout = layout;
            KeptLots = kept ?? NoLots;
            ReplacedLots = replaced ?? NoLots;
            AddedLots = added ?? NoLots;
            DroppedLots = dropped ?? NoLots;
            RepackedEverything = repackedEverything;
            Reason = reason;
        }

        /// <summary>The repaired layout. Never null.</summary>
        public NeighbourhoodLayout Layout { get; }

        /// <summary>Lots whose placement was carried over from the cached layout, untouched.</summary>
        public IReadOnlyList<short> KeptLots { get; }

        /// <summary>Lots that existed before and were re-placed because their house file changed.</summary>
        public IReadOnlyList<short> ReplacedLots { get; }

        /// <summary>Lots the cached layout did not have, placed for the first time.</summary>
        public IReadOnlyList<short> AddedLots { get; }

        /// <summary>Lots the cached layout had that this neighbourhood no longer names.</summary>
        public IReadOnlyList<short> DroppedLots { get; }

        /// <summary>
        /// Whether the repair could not be completed in place and the whole neighbourhood was
        /// packed instead. Reported rather than hidden: "only that lot changed" is the promise, and
        /// a fallback that quietly rearranged every other lot would be a different - and much more
        /// surprising - world.
        /// </summary>
        public bool RepackedEverything { get; }

        /// <summary>One sentence, for the log.</summary>
        public string Reason { get; }

        /// <summary>The lots whose contribution was regenerated: replaced plus added.</summary>
        public IReadOnlyList<short> LotsToRegenerate
        {
            get
            {
                if (ReplacedLots.Count == 0) return AddedLots;
                if (AddedLots.Count == 0) return ReplacedLots;
                var both = new short[ReplacedLots.Count + AddedLots.Count];
                for (int i = 0; i < ReplacedLots.Count; i++) both[i] = ReplacedLots[i];
                for (int i = 0; i < AddedLots.Count; i++) both[ReplacedLots.Count + i] = AddedLots[i];
                return both;
            }
        }

        /// <summary>Whether the repair moved any lot at all.</summary>
        public bool ChangedAnything
        {
            get { return ReplacedLots.Count > 0 || AddedLots.Count > 0 || DroppedLots.Count > 0; }
        }

        public override string ToString()
        {
            return (RepackedEverything ? "repacked" : "patched") + ": " + Reason;
        }
    }

    /// <summary>
    /// Repairs a cached layout in place: keeps every lot whose <c>houseHash</c> is unchanged exactly
    /// where it was, and re-places only the ones that moved.
    ///
    /// <para>
    /// <b>Why this exists rather than just re-packing.</b> §4.2's invalidation rule says a lot
    /// whose <c>houseHash</c> changed has "that lot's placement and the roads/terrain that touch
    /// it" regenerated. Re-packing the whole neighbourhood would satisfy the letter of that and
    /// break the spirit of it: the packer is a relaxation over the whole cloud, so changing one
    /// lot's size moves its neighbours, and the player comes back from bulldozing one house to
    /// find the whole street rearranged. Keeping the untouched rectangles fixed and re-placing only
    /// the edited lot is what makes the cheap path actually cheap, and the visible change actually
    /// the one the player made.
    /// </para>
    ///
    /// <para>
    /// <b>Where a changed lot lands.</b> Its old position, when it still fits inside the map -
    /// which is the case for all three edits the plan names, because a bulldoze, a build and a
    /// rezone leave a lot's size alone. A lot that is new, or whose size no longer fits where it
    /// was, is seeded from the same isometric projection <see cref="NeighbourhoodLayoutBuilder.Pack"/>
    /// uses and then pushed clear of the rectangles that stayed put. Either way only the moving
    /// lots are ever moved.
    /// </para>
    ///
    /// <para>
    /// <b>The fallback is a full pack, and it is reported.</b> A neighbourhood is dense, and there
    /// are arrangements in which a grown lot genuinely has nowhere to go without disturbing its
    /// neighbours. Rather than ship an overlapping layout - the generator's connectivity repair
    /// would then have to draw roads through a house - the whole neighbourhood is packed and
    /// <see cref="NeighbourhoodLayoutPatchResult.RepackedEverything"/> says so. The no-overlap
    /// invariant is never traded away for the cheap path.
    /// </para>
    ///
    /// <para>
    /// <b>What a patch does not touch.</b> Everything the sidecar knows that
    /// <c>LotLocations.iff</c> does not. A kept lot keeps its §15 forward fields - hollow, active
    /// or unloaded, when it was last simulated, where its door is - and the patched layout keeps
    /// the previous one's <see cref="NeighbourhoodLayout.WorldState"/>, because re-placing three
    /// lots of forty is no reason to reset the player's clock or their half-finished journey. The
    /// only path that loses them is the full repack, which loses the lot records themselves and
    /// says so.
    /// </para>
    ///
    /// <para>
    /// <b>Carrying the session is this class's business; writing it is not.</b> Whether that
    /// session reaches the disk at all is the refresh's decision and B7's gate - see
    /// <see cref="NeighbourhoodWorldFlag.Persist"/> and
    /// <see cref="NeighbourhoodLifecycle.LayoutToWrite"/> - and the split is deliberate. This
    /// class is pure, so it neither reads a capability switch nor knows what a file is; a patcher
    /// that consulted a flag would make "does this lot still fit where it was" depend on a setting
    /// file, and the answer to that question has to be the same on every machine. The layout that
    /// comes out of here therefore always carries the session it was given, and the caller that
    /// writes it decides whether that becomes a <c>worldState</c> member or is stripped back off
    /// so a Part A sidecar stays a Part A document.
    /// </para>
    ///
    /// <para>Pure: reads in, layout out. No disk, no provider, no graphics, no clock.</para>
    /// </summary>
    public static class NeighbourhoodLayoutPatch
    {
        /// <summary>
        /// Repairs <paramref name="previous"/> against a fresh set of lot records.
        /// </summary>
        /// <param name="previous">The cached layout, or <c>null</c> for a first build.</param>
        /// <param name="reads">The neighbourhood's lots as they are now.</param>
        /// <param name="slot">The neighbourhood slot id to stamp on the result.</param>
        /// <param name="seed">The seed this neighbourhood's world is generated from.</param>
        public static NeighbourhoodLayoutPatchResult Apply(
            NeighbourhoodLayout previous,
            IReadOnlyList<NeighbourhoodLotRead> reads,
            int slot,
            int seed)
        {
            var records = reads ?? new NeighbourhoodLotRead[0];
            var mapSize = previous?.MapSize ?? NeighbourhoodLayout.DefaultMapSize;
            var nothing = new short[0];

            if (previous == null || records.Count == 0)
            {
                //Nothing to keep, so there is nothing to patch. Packing an empty neighbourhood is
                //well defined - it is just empty ground - and it is the 2D fallback's job to decide
                //whether that is worth drawing.
                var fromScratch = previous == null
                    ? "there was no layout to repair, so the neighbourhood was packed from its own lots"
                    : "the neighbourhood has no lots, so the layout is empty";
                return new NeighbourhoodLayoutPatchResult(
                    NeighbourhoodLayoutBuilder.Pack(records, slot, seed, mapSize),
                    nothing, nothing, nothing, Dropped(previous, null), true, fromScratch);
            }

            var ordered = new List<NeighbourhoodLotRead>(records);
            ordered.Sort(CompareByNumber);

            var kept = new List<short>();
            var replaced = new List<short>();
            var added = new List<short>();
            var placed = new List<NeighbourhoodLot>();
            var moving = new List<Moving>();

            for (int i = 0; i < ordered.Count; i++)
            {
                var read = ordered[i];
                var before = previous.FindLot(read.Number);
                if (before != null && CarriesOver(before, read))
                {
                    kept.Add(read.Number);
                    placed.Add(Keep(before, read));
                    continue;
                }

                if (before == null) added.Add(read.Number);
                else replaced.Add(read.Number);
                var lot = New(read);
                placed.Add(lot);
                moving.Add(new Moving { Lot = lot, Previous = before });
            }

            var dropped = Dropped(previous, ordered);
            if (moving.Count == 0)
            {
                //Nothing had to be re-placed. The dropped lots are still reported, because a lot
                //that quietly vanished from a layout is a change the player has to be able to see.
                var settled = "nothing in the " + kept.Count + " lot(s) of this neighbourhood had"
                    + " changed, so the layout was reused as it stands";
                if (dropped.Count > 0)
                {
                    settled += "; lot " + NeighbourhoodRefreshPlan.Join(dropped)
                        + " is no longer part of this neighbourhood";
                }
                return new NeighbourhoodLayoutPatchResult(
                    Finish(slot, seed, mapSize, placed, previous.WorldState), kept, replaced, added, dropped, false, settled);
            }

            PlaceMoving(moving, placed, mapSize);

            var regenerated = Join(replaced, added);
            if (Overlaps(placed))
            {
                //Say so rather than shipping it: the cheap path could not be completed, and the
                //log is the only place anybody would ever find that out.
                return new NeighbourhoodLayoutPatchResult(
                    NeighbourhoodLayoutBuilder.Pack(ordered, slot, seed, mapSize),
                    nothing, replaced, added, dropped, true,
                    "lot " + NeighbourhoodRefreshPlan.Join(regenerated)
                    + " had nowhere to go without disturbing the rest of the neighbourhood,"
                    + " so the whole layout was packed again");
            }

            var reason = "lot " + NeighbourhoodRefreshPlan.Join(regenerated)
                + " changed since the layout was packed, so "
                + (regenerated.Count == 1 ? "that lot was" : "those lots were")
                + " re-placed and the other " + kept.Count + " kept the positions they had";
            if (dropped.Count > 0)
            {
                reason += "; lot " + NeighbourhoodRefreshPlan.Join(dropped)
                    + " is no longer part of this neighbourhood";
            }
            return new NeighbourhoodLayoutPatchResult(
                Finish(slot, seed, mapSize, placed, previous.WorldState), kept, replaced, added, dropped, false, reason);
        }

        private static int CompareByNumber(NeighbourhoodLotRead a, NeighbourhoodLotRead b)
        {
            return a.Number.CompareTo(b.Number);
        }

        /// <summary>
        /// A lot with its own facts and no placement yet, for the ones that have to be placed.
        /// </summary>
        private static NeighbourhoodLot New(NeighbourhoodLotRead read)
        {
            return new NeighbourhoodLot
            {
                Number = read.Number,
                Zone = read.Zone,
                Size = NeighbourhoodLayoutBuilder.ClampSize(read.Size),
                MapX = read.MapX,
                MapY = read.MapY,
                Rotation = read.Rotation & 3,
                TerrainType = read.TerrainType,
                BaseHeight = read.BaseHeight,
                HouseHash = read.HouseHash,
                HasHouse = read.HasHouse
            };
        }

        /// <summary>
        /// The same lot with its old placement kept, so a lot that has not changed comes out
        /// identical to the one in the sidecar - which is the whole promise of the cheap path.
        /// </summary>
        /// <remarks>
        /// The §15 forward fields are carried across, and that is the reason the third group of
        /// members is copied from <paramref name="before"/> rather than recomputed: a lot that is
        /// <see cref="NeighbourhoodLotState.Hollow"/> has a VM's state behind it, and a patch that
        /// rebuilt the lot record from the lot-location table alone would silently demote it to
        /// <see cref="NeighbourhoodLotState.Unloaded"/> and make the next visit start the lot
        /// from nothing. Nothing here can invent those facts - they are only ever copied, and only
        /// for a lot this method is reached for, which is a lot <see cref="CarriesOver"/> already
        /// agreed to reuse.
        /// </remarks>
        private static NeighbourhoodLot Keep(NeighbourhoodLot before, NeighbourhoodLotRead read)
        {
            return new NeighbourhoodLot
            {
                Number = before.Number,
                Zone = read.Zone,
                Size = before.Size,
                MapX = read.MapX,
                MapY = read.MapY,
                WorldX = before.WorldX,
                WorldY = before.WorldY,
                Rotation = before.Rotation,
                TerrainType = read.TerrainType,
                BaseHeight = read.BaseHeight,
                DoorSide = before.DoorSide,
                HouseHash = read.HouseHash,
                HasHouse = read.HasHouse,
                //§15 forward fields: this lot's simulation state is not in LotLocations.iff and
                //has not changed, so it belongs to the cached record and moves with it
                LotState = before.LotState,
                LastSimulated = before.LastSimulated,
                Entrance = before.Entrance
            };
        }

        /// <summary>
        /// May this lot's cached placement be reused verbatim? The <c>houseHash</c> is the signal
        /// §4.2 names; the rest is a guard against a sidecar whose hash was not updated with the
        /// rest of the file - a hand-edited one, or one written by a build that stamped the hash
        /// and not the fields.
        /// </summary>
        /// <remarks>
        /// The §15 forward fields are deliberately <em>not</em> part of this test, and the reason
        /// matters: they are not in the lot-location table, so there is nothing to compare them
        /// against, and every field that <em>is</em> compared has just been shown to be unchanged.
        /// Comparing them would be impossible; treating their presence as staleness would be
        /// worse than useless, because a neighbourhood with any hollow lot in it would then be
        /// entirely re-placed every time anything at all changed. A cached lot is reusable
        /// whenever the source still agrees with it, whatever state its own simulation is in.
        /// </remarks>
        private static bool CarriesOver(NeighbourhoodLot before, NeighbourhoodLotRead read)
        {
            return before.HouseHash == read.HouseHash
                && before.HasHouse == read.HasHouse
                && before.Zone == read.Zone
                && before.Size == NeighbourhoodLayoutBuilder.ClampSize(read.Size)
                && before.MapX == read.MapX
                && before.MapY == read.MapY
                && before.Rotation == (read.Rotation & 3)
                && before.TerrainType == read.TerrainType
                && before.BaseHeight == read.BaseHeight;
        }

        private static IReadOnlyList<short> Dropped(NeighbourhoodLayout previous, IReadOnlyList<NeighbourhoodLotRead> reads)
        {
            var dropped = new List<short>();
            if (previous == null) return dropped;
            for (int i = 0; i < previous.Lots.Count; i++)
            {
                var number = previous.Lots[i].Number;
                if (reads != null)
                {
                    var stillThere = false;
                    for (int j = 0; j < reads.Count; j++)
                    {
                        if (reads[j].Number == number) { stillThere = true; break; }
                    }
                    if (stillThere) continue;
                }
                dropped.Add(number);
            }
            return dropped;
        }

        private static void PlaceMoving(List<Moving> moving, List<NeighbourhoodLot> placed, int mapSize)
        {
            //Every moving lot starts where it was if it still fits there, which is the common case,
            //and at the projection if it does not. A lot whose rectangle has grown is then given the
            //nearest place that keeps its street, and only a map with no such place is left to the
            //push. Either way only the moving lots are ever pushed.
            //The lots that are staying put, which are the only obstacles a re-placed lot has to
            //respect: every moving lot is still standing at the origin until this loop reaches it,
            //and the push below settles those against each other
            var obstacles = new List<NeighbourhoodLot>(placed.Count);
            for (int p = 0; p < placed.Count; p++)
            {
                var candidate = placed[p];
                if (candidate == null) continue;
                var isMoving = false;
                for (int m = 0; m < moving.Count; m++)
                {
                    if (!ReferenceEquals(moving[m].Lot, candidate)) continue;
                    isMoving = true;
                    break;
                }
                if (!isMoving) obstacles.Add(candidate);
            }

            for (int i = 0; i < moving.Count; i++)
            {
                var lot = moving[i].Lot;
                var footprint = lot.Footprint(NeighbourhoodLayoutBuilder.SidewalkMargin);
                var previous = moving[i].Previous;

                //the seed, so a patched neighbourhood and a re-packed one start from the same
                //place: the old position where it is still legal for a lot of this size, and the
                //packer's own isometric projection otherwise - so a lot that needs relocating
                //lands somewhere recognisable rather than at the origin
                int seedX;
                int seedY;
                if (previous != null
                    && Fits(previous.WorldX, previous.WorldY, footprint, mapSize))
                {
                    seedX = previous.WorldX;
                    seedY = previous.WorldY;
                }
                else
                {
                    NeighbourhoodLayoutBuilder.ProjectMapPosition(
                        lot.MapX, lot.MapY, moving.Count, out var projectedX, out var projectedY);
                    seedX = (int)Math.Floor(projectedX);
                    seedY = (int)Math.Floor(projectedY);
                }

                lot.WorldX = seedX;
                lot.WorldY = seedY;

                //The street, reserved where the lot lands. A grown lot's old position is inside
                //its neighbour, so it no longer has a corridor at all, and a push cannot invent
                //one: the relaxation below only ever moves a lot towards lower coordinates, so a
                //lot with room above it and none beside it is pushed back and forth between the
                //two lots it started between until the patch gives up and re-packs the whole
                //neighbourhood. The room is looked for first, and it is looked for the way the
                //packer reserves it: a full street corridor to every lot that is staying put, not
                //merely clear ground. A layout that leaves two-tile gaps is not a layout this
                //corridor fits in, so a patch that cannot find room under that rule is not allowed
                //to pretend it has.
                if (!Reserves(lot, obstacles, mapSize))
                {
                    //RoomFor moves the lot itself, and leaves it on the seed when there is no
                    //room anywhere - which is the case the push below and then the caller's
                    //re-pack are there for
                    RoomFor(lot, seedX, seedY, obstacles, mapSize);
                }

                Clamp(lot, footprint, mapSize);
                lot.DoorSide = NeighbourhoodLayoutBuilder.DoorSideFor(
                    lot.WorldX, lot.WorldY, footprint, mapSize);
            }

            for (int pass = 0; pass < NeighbourhoodLayoutBuilder.MaxRelaxationPasses; pass++)
            {
                var moved = false;
                for (int i = 0; i < moving.Count; i++)
                {
                    var lot = moving[i].Lot;
                    var footprint = lot.Footprint(NeighbourhoodLayoutBuilder.SidewalkMargin);
                    for (int j = 0; j < placed.Count; j++)
                    {
                        if (ReferenceEquals(placed[j], lot)) continue;
                        if (Separate(lot, placed[j], mapSize)) moved = true;
                    }
                    //the lot may have been clamped by a push, so the bound is restated
                    Clamp(lot, footprint, mapSize);
                }
                if (!moved) return;
            }
        }

        /// <summary>
        /// Whether this lot, standing where it currently stands, is inside the map and keeps a
        /// full street corridor to every lot that is staying put.
        /// </summary>
        /// <remarks>
        /// The corridor is <see cref="NeighbourhoodLayoutBuilder.StreetCorridorTiles"/> - the same
        /// number the packer reserves between two rectangles - asked through the same
        /// <see cref="NeighbourhoodLot.Overlaps"/> every other layout check in this file uses, so
        /// "the street is reserved" has one spelling here rather than a margin of its own. Only
        /// the kept lots are asked about: the lots this patch is re-placing are settled by the
        /// relaxation, and a lot never clashes with where it is.
        /// </remarks>
        private static bool Reserves(
            NeighbourhoodLot lot, List<NeighbourhoodLot> obstacles, int mapSize)
        {
            if (!Fits(lot.WorldX, lot.WorldY,
                lot.Footprint(NeighbourhoodLayoutBuilder.SidewalkMargin), mapSize))
            {
                return false;
            }
            for (int i = 0; i < obstacles.Count; i++)
            {
                if (NeighbourhoodLot.Overlaps(
                    lot, obstacles[i], NeighbourhoodLayoutBuilder.SidewalkMargin)) return false;
            }
            return true;
        }

        /// <summary>
        /// Move <paramref name="lot"/> to the nearest place in the map where it keeps a full street
        /// corridor to every lot that is staying put, and answer whether there was one. Nothing is
        /// moved when there is not, so a lot the caller already placed is left exactly where it was.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The corridor is the quantum, and that is not a rounding convenience: the packer's own
        /// pitch is <c>Size + corridor</c>, so the first place a lot can clear a neighbour is at
        /// most one corridor off that neighbour's edge, and a lot that has grown by less than a
        /// corridor cannot step over a place it would have fitted in. The scan is a row-major walk
        /// over the map - the generator is O(size^2) and this runs on an edit, not a frame - and the
        /// nearest candidate wins, ties going to the scan's own order, so the same edit always
        /// re-places the same lot in the same place.
        /// </para>
        /// <para>
        /// Nearest to <paramref name="fromX"/>, <paramref name="fromY"/> rather than to the origin:
        /// a house that grew slides one corridor to the clear and stays on its own street, which is
        /// the whole promise of the cheap path - the player comes back to the same corner with a
        /// bigger plot, not to a lot at the far side of the map.
        /// </para>
        /// </remarks>
        private static bool RoomFor(
            NeighbourhoodLot lot, int fromX, int fromY,
            List<NeighbourhoodLot> obstacles, int mapSize)
        {
            var footprint = lot.Footprint(NeighbourhoodLayoutBuilder.SidewalkMargin);
            if (footprint > mapSize) return false;

            var step = Math.Max(1, NeighbourhoodLayoutBuilder.StreetCorridorTiles);
            var originX = lot.WorldX;
            var originY = lot.WorldY;
            var best = long.MaxValue;
            var bestX = originX;
            var bestY = originY;
            var found = false;

            for (var y = 0; y + footprint <= mapSize; y += step)
            {
                for (var x = 0; x + footprint <= mapSize; x += step)
                {
                    lot.WorldX = x;
                    lot.WorldY = y;
                    if (!Reserves(lot, obstacles, mapSize)) continue;
                    var dx = (long)x - fromX;
                    var dy = (long)y - fromY;
                    var distance = dx * dx + dy * dy;
                    if (distance >= best) continue;
                    best = distance;
                    bestX = x;
                    bestY = y;
                    found = true;
                }
            }

            lot.WorldX = found ? bestX : originX;
            lot.WorldY = found ? bestY : originY;
            return found;
        }

        /// <summary>
        /// Push <paramref name="lot"/> clear of <paramref name="other"/> along whichever axis it
        /// overlaps least on. The whole move goes to the lot being placed, so the rectangle that
        /// stayed put is provably untouched - which is the invariant the cheap path rests on.
        /// </summary>
        private static bool Separate(NeighbourhoodLot lot, NeighbourhoodLot other, int mapSize)
        {
            var margin = NeighbourhoodLayoutBuilder.SidewalkMargin;
            var a = lot.Footprint(margin);
            var b = other.Footprint(margin);

            var overlapX = Math.Min(lot.WorldX + a, other.WorldX + b) - Math.Max(lot.WorldX, other.WorldX);
            if (overlapX <= 0) return false;

            var overlapY = Math.Min(lot.WorldY + a, other.WorldY + b) - Math.Max(lot.WorldY, other.WorldY);
            if (overlapY <= 0) return false;

            if (overlapX <= overlapY)
            {
                lot.WorldX -= overlapX;
            }
            else
            {
                lot.WorldY -= overlapY;
            }
            Clamp(lot, a, mapSize);
            lot.DoorSide = NeighbourhoodLayoutBuilder.DoorSideFor(lot.WorldX, lot.WorldY, a, mapSize);
            return true;
        }

        private static bool Fits(int x, int y, int footprint, int mapSize)
        {
            return x >= 0 && y >= 0 && x + footprint <= mapSize && y + footprint <= mapSize;
        }

        private static void Clamp(NeighbourhoodLot lot, int footprint, int mapSize)
        {
            var max = Math.Max(0, mapSize - footprint);
            if (lot.WorldX < 0) lot.WorldX = 0;
            if (lot.WorldX > max) lot.WorldX = max;
            if (lot.WorldY < 0) lot.WorldY = 0;
            if (lot.WorldY > max) lot.WorldY = max;
        }

        private static bool Overlaps(List<NeighbourhoodLot> placed)
        {
            //The margin is included: two lots a tile apart are fine, two lots whose sidewalks
            //share a tile are not, and that is the same test the packer guarantees.
            for (int i = 0; i < placed.Count; i++)
            {
                for (int j = i + 1; j < placed.Count; j++)
                {
                    if (NeighbourhoodLot.Overlaps(placed[i], placed[j], NeighbourhoodLayoutBuilder.SidewalkMargin))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// The patched layout, stamped with this build's generator and schema and carrying the
        /// previous layout's world state forward untouched.
        /// </summary>
        /// <remarks>
        /// The session - the clock, the active lot, the hop in progress - belongs to the
        /// neighbourhood and not to any one lot, so a patch that re-placed three lots of forty
        /// would otherwise throw away the player's time and their half-finished journey. The
        /// per-lot half of session state is carried by <see cref="Keep"/> for the same reason.
        ///
        /// <para>
        /// The full-repack paths do not do this, and deliberately: a repack rebuilds every lot
        /// record from the lot-location table, so everything that only the sidecar knew is gone
        /// by construction, and the result says so through
        /// <see cref="NeighbourhoodLayoutPatchResult.RepackedEverything"/>. The session is the one
        /// thing the caller puts back - not here, where the flag that decides whether it should be
        /// written at all does not live, but in
        /// <see cref="NeighbourhoodLifecycle.LayoutToWrite"/>, which re-attaches it to the repacked
        /// arrangement when B7 is on and leaves it off the layout when it is not.
        /// </para>
        /// </remarks>
        private static NeighbourhoodLayout Finish(int slot, int seed, int mapSize, List<NeighbourhoodLot> lots,
            NeighbourhoodWorldState worldState)
        {
            return new NeighbourhoodLayout(slot, seed,
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion,
                NeighbourhoodLayout.CurrentSchemaVersion, mapSize, lots, worldState);
        }

        private static IReadOnlyList<short> Join(IReadOnlyList<short> a, IReadOnlyList<short> b)
        {
            if (a.Count == 0) return b;
            if (b.Count == 0) return a;
            var both = new short[a.Count + b.Count];
            for (int i = 0; i < a.Count; i++) both[i] = a[i];
            for (int i = 0; i < b.Count; i++) both[a.Count + i] = b[i];
            return both;
        }

        private sealed class Moving
        {
            public NeighbourhoodLot Lot;
            public NeighbourhoodLot Previous;
        }
    }
}
