/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FSO.Client;
using FSO.Common.Utils;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// One neighbourhood's generated world at one point in time: the layout it was packed from, the
    /// raster that was generated from that layout, and the plan that says which of the two was
    /// rebuilt and why.
    ///
    /// <para>
    /// <b>Why the plan travels with the world.</b> The plan is the only record of whether the
    /// raster here is a cache hit or twenty lots' worth of fresh work, and it is worth exactly one
    /// log line. Carrying it in the same object means a caller that has a world always has the
    /// sentence that explains it, and cannot log "generated" over a cache hit or "reused" over a
    /// rebuild.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodGeneratedWorld
    {
        private NeighbourhoodGeneratedWorld(
            int slot, NeighbourhoodLayout layout, NeighbourhoodMap map,
            NeighbourhoodRefreshPlan plan, string failure, bool pending = false)
        {
            Slot = slot;
            Layout = layout;
            Map = map;
            Plan = plan;
            Failure = failure;
            Pending = pending;
        }

        /// <summary>The neighbourhood slot id this world belongs to, 0..8.</summary>
        public int Slot { get; }

        /// <summary>The packed layout, or <c>null</c> when the neighbourhood could not be read.</summary>
        public NeighbourhoodLayout Layout { get; }

        /// <summary>The generated 512x512 raster, or <c>null</c> when generation did not produce one.</summary>
        public NeighbourhoodMap Map { get; }

        /// <summary>What this refresh did, and why. Never default: a world always has a plan.</summary>
        public NeighbourhoodRefreshPlan Plan { get; }

        /// <summary>Why generation failed, or <c>null</c> if it did not.</summary>
        public string Failure { get; }

        /// <summary>
        /// Whether this world is still being generated on a worker, rather than absent.
        ///
        /// <para>
        /// The distinction a log reader needs and a test needs more: a pending world has no raster
        /// yet and never will have one from this object, and the reason it says so is "ask again
        /// in a moment", where a failure's reason means "this build cannot do it". Everything
        /// downstream treats both as "not a world to draw" - <see cref="HasWorld"/> is false for
        /// both, and the painted map is what the panel draws either way - so the flag is a
        /// statement about the reason, not about what to do.
        /// </para>
        /// </summary>
        public bool Pending { get; }

        /// <summary>Whether there is a raster worth drawing. The 2D fallback keys on this.</summary>
        public bool HasWorld
        {
            get { return Failure == null && Map != null && Layout != null; }
        }

        /// <summary>
        /// The session half of this world's layout - the clock, the lot the player is in, and the
        /// hops they are in the middle of - or <c>null</c>.
        ///
        /// <para>
        /// <b>Null for every world while B7 is off, and the flag is the reason.</b>
        /// <see cref="NeighbourhoodWorldFlag.Persist"/> defaults off, and while it is off the
        /// lifecycle reads no session out of a sidecar and writes none back, so there is nothing
        /// for this to hand over. A caller that wants a session asks
        /// <see cref="NeighbourhoodLifecycle.PersistsWorldState"/> once and then reads this
        /// without a further test, which is the arrangement the rest of Part B uses.
        /// </para>
        ///
        /// <para>
        /// <b>Why it travels on the world rather than being looked up.</b> A layout already
        /// carries it, so the only question is whether a caller holding a world can get at it
        /// without also holding a file - and on a cache hit, which reads no file at all, it cannot.
        /// Two answers to "what time is it in this neighbourhood" that are allowed to disagree
        /// are the bug; one property that is the layout's own session is the fix.
        /// </para>
        ///
        /// <para>
        /// <b>What this returns is the layout's, not the file's.</b> Whoever simulates the world
        /// fills the state in and then saves it through
        /// <see cref="NeighbourhoodLifecycle.SaveWorldState"/>, so when the two differ the copy
        /// in memory is the newer one and the file is what a later launch would read. A world's
        /// own session is therefore never replaced by a copy read off disk.
        /// </para>
        /// </summary>
        public NeighbourhoodWorldState WorldState
        {
            get
            {
                if (!NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist)) return null;
                return Layout == null ? null : Layout.WorldState;
            }
        }

        /// <summary>
        /// The same world, bound to a different layout of the same neighbourhood - same slot, same
        /// seed, same generator version, same lots, field for field.
        ///
        /// <remarks>
        /// Internal because it is only safe where that comparison has already been made. It exists
        /// for one case: a world that was built while B7 was off, meeting a sidecar that now holds
        /// a session, and the caller has to be handed a world that can read it. The raster is not
        /// touched, the plan and the failure are carried over untouched, and nothing about the
        /// world's identity as a <em>map</em> changes - only which layout object it reports.
        /// </remarks>
        /// </summary>
        /// <param name="layout">The layout to bind, after a field-for-field comparison.</param>
        internal NeighbourhoodGeneratedWorld WithLayout(NeighbourhoodLayout layout)
        {
            if (ReferenceEquals(layout, Layout)) return this;
            return new NeighbourhoodGeneratedWorld(Slot, layout, Map, Plan, Failure, Pending);
        }

        /// <summary>
        /// A world for a refresh that produced both halves.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="layout">The layout the raster was generated from.</param>
        /// <param name="map">The generated raster, which is required - a world with no map is
        /// <see cref="Failed"/>, and a null here would say "3D was built" over nothing.</param>
        /// <param name="plan">The plan that produced them.</param>
        public static NeighbourhoodGeneratedWorld Built(
            int slot, NeighbourhoodLayout layout, NeighbourhoodMap map, NeighbourhoodRefreshPlan plan)
        {
            if (map == null) return Failed(slot, layout, plan, "no map was produced");
            return new NeighbourhoodGeneratedWorld(slot, layout, map,
                NeighbourhoodRefreshPlan.ForGeneratedMap(plan, map), null);
        }

        /// <summary>
        /// A world for a refresh that is still running. It has no raster and no failure - it has a
        /// job - and the panel's answer to one is the painted map, exactly as it is for a failure.
        ///
        /// <remarks>
        /// This is what the non-blocking refresh hands back on a cold cache, and it is the reason
        /// the panel never has to wait for a quarter of a million tiles: the call returns a world
        /// immediately, the work happens on a worker, and the finished world is applied on the game
        /// thread when it lands. Deliberately not cached - a world with no raster is not worth
        /// storing, and an entry that only ever says "still building" would make the next refresh
        /// look like a hit.
        /// </remarks>
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="plan">The plan for the refresh that is running.</param>
        /// <param name="why">What is happening, in one sentence.</param>
        public static NeighbourhoodGeneratedWorld Refreshing(
            int slot, NeighbourhoodRefreshPlan plan, string why)
        {
            return new NeighbourhoodGeneratedWorld(slot, null, null, plan, why, true);
        }

        /// <summary>
        /// A world for a refresh that did not. The layout is kept when there was one - it is worth
        /// having for the camera and the log even when the raster is not - and the plan carries the
        /// 2D selection and the reason.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="layout">The layout, or <c>null</c>.</param>
        /// <param name="plan">The plan that was attempted.</param>
        /// <param name="failure">Why it failed, in one sentence.</param>
        public static NeighbourhoodGeneratedWorld Failed(
            int slot, NeighbourhoodLayout layout, NeighbourhoodRefreshPlan plan, string failure)
        {
            return new NeighbourhoodGeneratedWorld(slot, layout, null,
                NeighbourhoodRefreshPlan.ForFailure(plan, failure), failure);
        }

        /// <summary>
        /// The one line this world belongs in the log, and the answer to "why is my neighbourhood
        /// in 2D".
        /// </summary>
        public string Describe()
        {
            var map = Map;
            var prefix = Pending
                ? "neighbourhood " + Slot + ": still generating on a worker - " + Plan
                : "neighbourhood " + Slot + ": " + Plan;
            return prefix + (map == null
                ? string.Empty
                : "; " + map.Width + "x" + map.Height + " map, " + (Layout?.LotCount ?? 0) + " lots");
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// How many times the in-memory generated worlds have been reused, rebuilt and thrown away.
    /// For the log, and for a test that wants to assert the cheap path is being taken without
    /// reading a log file.
    /// </summary>
    public sealed class NeighbourhoodCacheCounters
    {
        /// <summary>Refreshes that reused the layout and the raster as they stood.</summary>
        public int Hits { get; internal set; }

        /// <summary>Refreshes that had to read, pack or generate something.</summary>
        public int Misses { get; internal set; }

        /// <summary>Times a cached world was dropped because something changed.</summary>
        public int Invalidations { get; internal set; }

        /// <summary>Refreshes that ended in the 2D bitmap, with a reason.</summary>
        public int Fallbacks { get; internal set; }

        /// <summary>
        /// Background refreshes this process actually started.
        ///
        /// <para>
        /// This is the number that says a cold generation is not happening twice. The neighbourhood
        /// panel asks for a world every frame while one is being built, the service asks for one
        /// before the panel even exists, and both would be entitled to start their own - which is
        /// two sets of twenty IFF reads, two layout writes and two 512x512 generations of the same
        /// neighbourhood, racing to put two different instances in the cache. A second request
        /// joins the first instead, so this counter counts one per real generation however many
        /// callers asked for it.
        /// </para>
        /// </summary>
        public int Refreshes { get; internal set; }

        /// <summary>
        /// Requests that found a refresh already running for their slot and waited on it rather
        /// than starting a second one.
        /// </summary>
        public int Joined { get; internal set; }

        /// <summary>
        /// Background refreshes that finished and were refused by the cache because the
        /// neighbourhood was invalidated while they were running.
        ///
        /// <para>
        /// Non-zero means a bulldoze landed mid-generation. It is the count of work that was thrown
        /// away rather than shown, which is the right answer: the alternative is a world built from
        /// the house files as they were before the bulldoze sitting in the cache being handed out
        /// as current.
        /// </para>
        /// </summary>
        public int Superseded { get; internal set; }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            return "generated neighbourhood cache: " + Hits + " hit(s), " + Misses + " miss(es), "
                + Invalidations + " invalidation(s), " + Fallbacks + " fallback(s), "
                + Refreshes + " refresh(es) started, " + Joined + " joined, "
                + Superseded + " superseded";
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// The process-wide cache of generated worlds, one entry per neighbourhood slot.
    ///
    /// <para>
    /// <b>Why there is a cache at all when the sidecar already exists.</b> The sidecar holds the
    /// <em>layout</em>: a few kilobytes of JSON describing where every lot goes. The raster it
    /// describes is 512x512 of terrain, road, elevation and foliage, and §4.2 is explicit that it
    /// is not stored ("a few hundred KB of JSON per map would be unnecessary") because it is a
    /// pure function of the layout and the seed. That means every fresh attach to a neighbourhood
    /// regenerates a quarter of a million tiles unless something holds the last one - and this is
    /// that something, for the length of a session.
    /// </para>
    ///
    /// <para>
    /// <b>For the length of a session, and no longer.</b> A cache in a dictionary dies with the
    /// process, so a player who quit and came back paid the whole generation again - which is the
    /// complaint the second half of the story is about. <see cref="NeighbourhoodRasterCache"/> is
    /// that half: the same map on disk, beside the layout it belongs to, stamped so a later launch
    /// can prove it is the map it would have generated. This cache stays exactly as it was - the
    /// in-memory one is the fast path, and the file is the slow one, and a refresh that finds the
    /// file still puts the result here so the next attach costs nothing at all.
    /// </para>
    ///
    /// <para>
    /// <b>Scope is one world per mode.</b> §0 decision 4: the nine slots and the six views are
    /// different things, downtown/vacation/studio/magic all live inside the one slot that is open,
    /// so the cache is keyed by the same slot id the sidecar is and holds one entry per slot. A
    /// neighbourhood switch is a different slot and therefore a different entry.
    /// </para>
    ///
    /// <para>
    /// <b>Entries are immutable once stored.</b> Nothing hands a caller a map it may then mutate;
    /// a refresh either replaces the entry or leaves it. That is §15's "generation stays pure,
    /// mutation only on the game thread", and it is why a refresh that is still in flight cannot
    /// corrupt what a view is drawing.
    /// </para>
    /// </summary>
    public static class NeighbourhoodWorldCache
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, NeighbourhoodGeneratedWorld> Worlds =
            new Dictionary<int, NeighbourhoodGeneratedWorld>();
        private static readonly Dictionary<int, int> Epochs = new Dictionary<int, int>();
        private static readonly NeighbourhoodCacheCounters Counters = new NeighbourhoodCacheCounters();

        /// <summary>The counters, for the log.</summary>
        public static NeighbourhoodCacheCounters Stats
        {
            get { lock (Gate) { return Counters; } }
        }

        /// <summary>How many slots currently hold a world.</summary>
        public static int Count
        {
            get { lock (Gate) { return Worlds.Count; } }
        }

        /// <summary>
        /// The cached world for a slot, or <c>null</c>. Does not count as a hit or a miss: asking
        /// whether something is there is not the same as deciding to use it, and the decision is
        /// <see cref="NeighbourhoodRefreshPlan.Decide"/>'s.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        public static NeighbourhoodGeneratedWorld Peek(int slot)
        {
            lock (Gate)
            {
                NeighbourhoodGeneratedWorld world;
                return Worlds.TryGetValue(slot, out world) ? world : null;
            }
        }

        /// <summary>
        /// Puts a world in the cache, replacing whatever was there. A world with no raster is not
        /// stored: there is nothing to reuse, and an entry that only ever says "failed" would make
        /// the next refresh look like a hit.
        /// </summary>
        /// <param name="world">The world to keep.</param>
        public static void Store(NeighbourhoodGeneratedWorld world)
        {
            if (world == null || world.Slot < 0) return;
            lock (Gate)
            {
                if (world.HasWorld) Worlds[world.Slot] = world;
                else Worlds.Remove(world.Slot);
            }
        }

        /// <summary>
        /// How many times this slot's cached world has been thrown away. The number a refresh
        /// captures before it starts, so it can tell whether anything happened to the
        /// neighbourhood while it was running.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        public static int EpochOf(int slot)
        {
            lock (Gate)
            {
                int epoch;
                return Epochs.TryGetValue(slot, out epoch) ? epoch : 0;
            }
        }

        /// <summary>
        /// Stores a world only if nothing has invalidated its slot since <paramref name="epoch"/>
        /// was read.
        ///
        /// <para>
        /// <b>Why a refresh needs this.</b> A background refresh reads the neighbourhood's lot
        /// files, packs, generates and then stores. That takes seconds, and a bulldoze can land in
        /// the middle of it - the player is standing on the neighbourhood screen with the painted
        /// map up, and the only way to bulldoze from there is to get there quickly. The world that
        /// comes out describes the house files as they were <em>before</em> that bulldoze, so
        /// storing it as current would put a world with a house on it back into the cache and hand
        /// it to the next attach as a hit. Dropping it is not a loss: the cache is already empty
        /// (that is what the invalidation did), so the next refresh starts from the house files as
        /// they are now and the layout is repaired again - on a worker, in about the time it takes
        /// to play a second visit.
        /// </para>
        /// </summary>
        /// <param name="world">The world to keep.</param>
        /// <param name="epoch">The <see cref="EpochOf"/> value from before the refresh started.</param>
        /// <returns>Whether it was stored. False means the slot moved on without it.</returns>
        public static bool TryStore(NeighbourhoodGeneratedWorld world, int epoch)
        {
            if (world == null || world.Slot < 0) return false;
            lock (Gate)
            {
                int current;
                if (Epochs.TryGetValue(world.Slot, out current) && current != epoch)
                {
                    Counters.Superseded++;
                    return false;
                }

                if (world.HasWorld) Worlds[world.Slot] = world;
                else Worlds.Remove(world.Slot);
                return true;
            }
        }

        /// <summary>
        /// Drops a slot's world, because the neighbourhood changed. Returns whether there was one.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        public static bool Invalidate(int slot)
        {
            lock (Gate)
            {
                Counters.Invalidations++;
                Bump(slot);
                return Worlds.Remove(slot);
            }
        }

        /// <summary>
        /// Drops a slot's world because one lot changed. Same effect as
        /// <see cref="Invalidate(int)"/> - the raster is a function of the whole layout, so there
        /// is nothing to salvage at tile granularity - but named for the case, and logging what
        /// is the difference between "the map was rebuilt" and "lot 31 was rebuilt".
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="lotNumber">The lot that changed.</param>
        public static bool InvalidateLot(int slot, short lotNumber)
        {
            lock (Gate)
            {
                Counters.Invalidations++;
                Bump(slot);
                return Worlds.Remove(slot);
            }
        }

        /// <summary>
        /// Marks the slot as having moved on, so a refresh that started before this is refused by
        /// <see cref="TryStore"/>. Must be called under <see cref="Gate"/>.
        /// </summary>
        private static void Bump(int slot)
        {
            int epoch;
            Epochs.TryGetValue(slot, out epoch);
            Epochs[slot] = epoch + 1;
        }

        /// <summary>Records that a refresh reused everything it could.</summary>
        internal static void CountHit()
        {
            lock (Gate) { Counters.Hits++; }
        }

        /// <summary>Records that a refresh had to do work.</summary>
        internal static void CountMiss()
        {
            lock (Gate) { Counters.Misses++; }
        }

        /// <summary>Records that a refresh ended in the bitmap.</summary>
        internal static void CountFallback()
        {
            lock (Gate) { Counters.Fallbacks++; }
        }

        /// <summary>Records that a background refresh was started for a slot.</summary>
        internal static void CountRefresh()
        {
            lock (Gate) { Counters.Refreshes++; }
        }

        /// <summary>Records that a request joined a refresh that was already running.</summary>
        internal static void CountJoined()
        {
            lock (Gate) { Counters.Joined++; }
        }

        /// <summary>
        /// Forgets everything. Only a test wants this: the cache is process-wide, and a test that
        /// asserted a hit would otherwise be asserting on whatever the previous test left behind.
        /// </summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Worlds.Clear();
                Epochs.Clear();
                Counters.Hits = 0;
                Counters.Misses = 0;
                Counters.Invalidations = 0;
                Counters.Fallbacks = 0;
                Counters.Refreshes = 0;
                Counters.Joined = 0;
                Counters.Superseded = 0;
            }
        }
    }

    /// <summary>
    /// One refresh in flight: the worker, the plan it decided on, and the world it produced.
    ///
    /// <para>
    /// <b>Threading, in the order the steps happen.</b> The reads, the packing, the patch and the
    /// 512x512 generation all run on a worker - they are IO and pure arithmetic, and §10 asks for
    /// exactly that ("off the game thread", and the plan's §15 row calls the boundary "immutable
    /// generated maps + a game-thread apply step"). The sidecar is written from the worker too: it
    /// is a cache file written through <c>AtomicFileCommit</c>, it is only read back by another
    /// refresh, and moving it onto the game thread would put a disk write in the frame for no
    /// gain. What lands on <c>GameThread.NextUpdate</c> is the swap into
    /// <see cref="NeighbourhoodWorldCache"/> and the caller's apply, because that is the first
    /// moment anything the renderer can see has changed.
    /// </para>
    ///
    /// <para>
    /// <b>Why a caller can wait for it, and how long for.</b> Two boundaries, because there are two
    /// things a caller could be waiting for. <see cref="WaitForLayout"/> is the one the
    /// neighbourhood service uses: a refresh that has to repair the layout has to have written the
    /// sidecar before the panel is built, and the wait is bounded so a pathological disk cannot
    /// hang the screen - a timeout means the panel is built from the layout that was already
    /// there, which is exactly what the game did before any of this existed.
    /// <see cref="WaitForFinish"/> is the other one, and it is the whole of the raster: nothing on
    /// the game thread should ever call it, which is why it exists for the tests and for a tool
    /// that wants a neighbourhood on disk rather than a frame that does not stop.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodRefreshJob
    {
        private readonly ManualResetEventSlim _layoutReady = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _finishedEvent = new ManualResetEventSlim(false);
        private readonly object _gate = new object();
        private readonly List<Action<NeighbourhoodGeneratedWorld>> _applies =
            new List<Action<NeighbourhoodGeneratedWorld>>();

        private NeighbourhoodGeneratedWorld _world;
        private volatile bool _finished;

        private NeighbourhoodRefreshJob(int slot)
        {
            Slot = slot;
            Epoch = NeighbourhoodWorldCache.EpochOf(slot);
        }

        /// <summary>The slot being refreshed.</summary>
        public int Slot { get; }

        /// <summary>
        /// The slot's invalidation epoch as of the moment this job started. A world this job built
        /// is only cached if the epoch still matches, and a job that produced no world is only
        /// remembered as the answer if it does - see
        /// <see cref="NeighbourhoodWorldCache.TryStore"/> and
        /// <see cref="NeighbourhoodLifecycle.RefreshNow"/>, which is why it is public rather than
        /// an implementation detail.
        /// </summary>
        public int Epoch { get; }

        /// <summary>The world this job produced, once it has.</summary>
        public NeighbourhoodGeneratedWorld World
        {
            get { lock (_gate) { return _world; } }
        }

        /// <summary>Whether the job has finished, applied or not.</summary>
        public bool IsComplete
        {
            get { return _layoutReady.IsSet; }
        }

        /// <summary>
        /// Whether the job has stopped running, successfully or not.
        ///
        /// <para>
        /// Not the same as <see cref="IsComplete"/>, and the difference is the whole reason this
        /// property exists. <see cref="IsComplete"/> means "the sidecar is current", which is true
        /// for the whole of the expensive half - the 512x512 generation runs <em>after</em> it.
        /// What a second caller needs to know is whether starting its own refresh would be a second
        /// generation of the same neighbourhood, and that is only false once the job has actually
        /// stopped.
        /// </para>
        /// </summary>
        public bool IsFinished
        {
            get { return _finished; }
        }

        /// <summary>
        /// Blocks until the sidecar for this slot is current, or the timeout expires.
        /// </summary>
        /// <param name="timeout">How long to wait. Zero polls.</param>
        /// <returns>Whether the layout landed in time.</returns>
        public bool WaitForLayout(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            try
            {
                return _layoutReady.Wait(timeout);
            }
            catch (Exception)
            {
                //an abandoned or disposed handle is not a reason to fail the neighbourhood screen
                return IsComplete;
            }
        }

        /// <summary>
        /// How many callers are currently waiting to be handed the finished world. Zero once the
        /// job has published, whatever it published: the list is taken and emptied, so a caller
        /// arriving after that is delivered late instead by <see cref="AddApply"/>.
        /// </summary>
        public int SubscriberCount
        {
            get { lock (_gate) { return _applies.Count; } }
        }

        /// <summary>
        /// Asks to be handed the finished world, on the game thread.
        ///
        /// <para>
        /// <b>Why a running job can be subscribed to.</b> The refresh for a neighbourhood is
        /// started by whoever got there first - in practice <c>NeighborhoodService</c>, a moment
        /// before the panel exists - and the panel is the thing that knows what to do with the
        /// answer. Without this, a panel arriving at a refresh somebody else started would have
        /// either to poll for the world on every frame or start a second one. A delegate rather
        /// than an event because the caller owns when it wants to stop listening: it holds the
        /// panel, and the panel is removed long before the job is.
        /// </para>
        /// <para>
        /// A job that has already finished schedules the callback on the game thread instead, so
        /// "I was late" and "I was early" cannot disagree about whether the caller ever hears
        /// about it. The callback may then be handed a world that is already in the cache, which is
        /// not a double delivery: a caller that has stopped listening simply never gets called
        /// again.
        /// </para>
        /// </summary>
        /// <param name="apply">Called on the game thread with the finished world.</param>
        public void AddApply(Action<NeighbourhoodGeneratedWorld> apply)
        {
            if (apply == null) return;
            NeighbourhoodGeneratedWorld published;
            lock (_gate)
            {
                //Subscribe while the job is still building. "_finished" alone is not enough: the
                //publish takes the apply list before it sets "_finished", so a caller that arrived
                //in between would add itself to a list nobody will take again. "_world" is set
                //before that take, so checking it closes the window.
                if (!_finished && _world == null)
                {
                    _applies.Add(apply);
                    return;
                }
                //Already published: hand over the world that was published, on the game thread.
                //Passing null here - as this used to - told a late caller the refresh had produced
                //nothing, so a late subscriber silently dropped a perfectly good world (its own
                //guard is "if (world == null) return").
                published = _world;
            }

            GameThread.NextUpdate(state => Run(published, apply));
        }

        /// <summary>
        /// Blocks until the job has stopped running, or the timeout expires.
        ///
        /// <para>
        /// The other half of <see cref="WaitForLayout"/>, and a thing no game thread should ever
        /// call: it waits for the 262 144 tiles. It exists for the callers that genuinely have to
        /// have the world before they go on - the tests, and any future tool that wants a
        /// neighbourhood on disk - and it is bounded for the same reason the layout wait is, so a
        /// worker that wedges costs a timeout rather than a hang.
        /// </para>
        /// </summary>
        /// <param name="timeout">How long to wait. Zero polls.</param>
        /// <returns>Whether the job finished in time.</returns>
        public bool WaitForFinish(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            try
            {
                return _finishedEvent.Wait(timeout);
            }
            catch (Exception)
            {
                return IsFinished;
            }
        }

        /// <summary>
        /// Runs a refresh on a worker and applies the result on the game thread.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="build">
        /// The work, which must be safe to run off the game thread: reads, packing, generation.
        /// It is handed a callback to invoke once the sidecar is current, which is what
        /// <see cref="WaitForLayout"/> waits on. Returns the world, or a world describing why
        /// there is not one. Called exactly once.
        /// </param>
        /// <param name="apply">
        /// Called on the game thread once the worker is done, with the world. May be
        /// <c>null</c> for a caller that only wants the cache filled.
        /// </param>
        public static NeighbourhoodRefreshJob Start(
            int slot, Func<Action, NeighbourhoodGeneratedWorld> build,
            Action<NeighbourhoodGeneratedWorld> apply)
        {
            var job = new NeighbourhoodRefreshJob(slot);
            if (apply != null) job.AddApply(apply);
            Task.Run(() =>
            {
                try
                {
                    var world = SafeBuild(job, build);
                    //the sidecar is current from here on whatever happened, which is what a caller
                    //that is about to build the neighbourhood panel is waiting for
                    job._layoutReady.Set();
                    job.Publish(world);
                }
                finally
                {
                    //after Publish, so a second caller that arrives the instant this job stops is
                    //never told the slot is free while it is still being written
                    job._finished = true;
                    job._finishedEvent.Set();
                }
            });
            return job;
        }

        /// <summary>
        /// Runs a refresh on this thread, for a caller that has no game thread to hand the result
        /// to. Used by the tests and by anything that is not a frame. The apply step runs here too,
        /// because there is nowhere else for it to run.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="build">The work.</param>
        /// <param name="apply">The apply step, or <c>null</c>.</param>
        public static NeighbourhoodRefreshJob RunNow(
            int slot, Func<Action, NeighbourhoodGeneratedWorld> build,
            Action<NeighbourhoodGeneratedWorld> apply)
        {
            var job = new NeighbourhoodRefreshJob(slot);
            if (apply != null) job.AddApply(apply);
            try
            {
                var world = SafeBuild(job, build);
                job._layoutReady.Set();
                job.PublishNow(world);
            }
            finally
            {
                job._finished = true;
                job._finishedEvent.Set();
            }
            return job;
        }

        /// <summary>
        /// Records the world and, if there is one, runs the apply step on the thread that is
        /// already the right one.
        /// </summary>
        private void PublishNow(NeighbourhoodGeneratedWorld world)
        {
            if (world == null) return;
            lock (_gate) { _world = world; }
            NeighbourhoodWorldCache.TryStore(world, Epoch);
            foreach (var apply in TakeApplies())
            {
                Run(world, apply);
            }
        }

        private static NeighbourhoodGeneratedWorld SafeBuild(
            NeighbourhoodRefreshJob job, Func<Action, NeighbourhoodGeneratedWorld> build)
        {
            if (build == null) return null;
            try
            {
                return build(job._layoutReady.Set);
            }
            catch (Exception e)
            {
                //A refresh that throws is a refresh that did not happen. The neighbourhood falls
                //back to the bitmap and the reason goes in the log; nothing above this can see
                //the exception, which is the point.
                SimitoneLog.Error("The generated neighbourhood for slot " + job.Slot
                    + " could not be refreshed. The neighbourhood is unaffected and will be drawn"
                    + " as the painted map.", e);
                return null;
            }
        }

        private void Publish(NeighbourhoodGeneratedWorld world)
        {
            if (world == null) return;
            lock (_gate) { _world = world; }
            //the cache itself is guarded and holds only immutable worlds, so it may be written
            //from the worker; what has to wait for the game thread is the apply, because that is
            //what a renderer would see
            NeighbourhoodWorldCache.TryStore(world, Epoch);

            var applies = TakeApplies();
            if (applies.Count == 0) return;
            GameThread.NextUpdate(state => Run(world, applies));
        }

        /// <summary>
        /// The apply callbacks, emptied under the lock. Taken once so a subscriber added while the
        /// world is on its way to the game thread is delivered by whichever path sees it first,
        /// rather than being run twice or never.
        /// </summary>
        private List<Action<NeighbourhoodGeneratedWorld>> TakeApplies()
        {
            lock (_gate)
            {
                var taken = new List<Action<NeighbourhoodGeneratedWorld>>(_applies);
                _applies.Clear();
                return taken;
            }
        }

        private static void Run(NeighbourhoodGeneratedWorld world,
            Action<NeighbourhoodGeneratedWorld> apply)
        {
            try
            {
                apply(world);
            }
            catch (Exception e)
            {
                SimitoneLog.Error("Applying a refreshed generated neighbourhood failed; the"
                    + " painted map stays up.", e);
            }
        }

        private static void Run(NeighbourhoodGeneratedWorld world,
            List<Action<NeighbourhoodGeneratedWorld>> applies)
        {
            for (int i = 0; i < applies.Count; i++) Run(world, applies[i]);
        }
    }

    /// <summary>
    /// The stages a neighbourhood's generated world is built in, in the order they run.
    ///
    /// <para>
    /// <b>Why they are named rather than left to the log.</b> "The neighbourhood took eleven
    /// seconds" is a measurement with no next step. Which of these eleven seconds went where is
    /// the only thing that says whether to look at the road graph, the terrain fill, the seam
    /// solver, twenty IFF reads or the mesh upload - and the four halves of that are owned by four
    /// different files, so the names have to be a shared vocabulary rather than each file's own
    /// wording.
    /// </para>
    /// </summary>
    public enum NeighbourhoodGenerationPhase
    {
        /// <summary>Reading the layout sidecar off disk.</summary>
        SidecarRead = 0,

        /// <summary>One <c>HouseNN.iff</c> per lot, which is what decides whether anything moved.</summary>
        LotReads = 1,

        /// <summary>Packing the whole neighbourhood, or patching the lots that changed.</summary>
        LayoutBuild = 2,

        /// <summary>Writing the layout sidecar, through the staged write.</summary>
        LayoutWrite = 3,

        /// <summary>Projecting the packed layout into the generator's own input.</summary>
        MapInput = 4,

        /// <summary>The street network, inside the generation pass. Reported by the generator.</summary>
        Roads = 5,

        /// <summary>Ground, water and foliage, inside the generation pass. Reported by the generator.</summary>
        TerrainFill = 6,

        /// <summary>Elevation and the lot seams, inside the generation pass. Reported by the generator.</summary>
        SeamSolve = 7,

        /// <summary>The whole 512x512 pass, as the lifecycle sees it. Contains the three above.</summary>
        MapGenerate = 8,

        /// <summary>Turning the raster into vertices. Owned by the 3D view, not by this file.</summary>
        MeshBuild = 9,

        /// <summary>Handing the vertices to the device. Owned by the 3D view, not by this file.</summary>
        MeshUpload = 10,

        /// <summary>The refresh as a whole, from the first read to the finished world.</summary>
        Total = 11
    }

    /// <summary>
    /// How long each stage of one refresh took, and the one log line that says it.
    ///
    /// <para>
    /// <b>What it is for.</b> The player-visible cost of entering a neighbourhood for the first
    /// time is several seconds, and until now the only number in the log was the total. This is the
    /// breakdown: which stage the seconds went to, in the order the work happens, so the next
    /// question is answerable from the log rather than from a profiler. It is deliberately the whole
    /// phase list and not just the phases this file can see - a stage nobody measured is reported
    /// as unmeasured rather than quietly left out, because a log line that silently omits the one
    /// phase that turned out to be slow is worse than no log line at all.
    /// </para>
    ///
    /// <para>
    /// <b>How a stage outside this file reports.</b> The generation pass measures its own three
    /// stages and hands them to <see cref="Current"/>, which the refresh publishes for the duration
    /// of the pass; the mesh stages are measured by the 3D view the same way. <see cref="Current"/>
    /// flows with the execution context rather than being a plain static, so two neighbourhoods
    /// generating at once cannot write into each other's numbers - which is the only reason a
    /// process-wide hook is safe here at all.
    /// </para>
    ///
    /// <para>
    /// <b>Not on the frame.</b> Everything in here happens on the worker that is building the
    /// world, and the log line is written there too. The only thing that reaches the game thread is
    /// the finished, immutable world.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodGenerationTimings
    {
        private static readonly AsyncLocal<NeighbourhoodGenerationTimings> Scope =
            new AsyncLocal<NeighbourhoodGenerationTimings>();

        private static readonly NeighbourhoodGenerationPhase[] Order =
        {
            NeighbourhoodGenerationPhase.SidecarRead,
            NeighbourhoodGenerationPhase.LotReads,
            NeighbourhoodGenerationPhase.LayoutBuild,
            NeighbourhoodGenerationPhase.LayoutWrite,
            NeighbourhoodGenerationPhase.MapInput,
            NeighbourhoodGenerationPhase.Roads,
            NeighbourhoodGenerationPhase.TerrainFill,
            NeighbourhoodGenerationPhase.SeamSolve,
            NeighbourhoodGenerationPhase.MapGenerate,
            NeighbourhoodGenerationPhase.MeshBuild,
            NeighbourhoodGenerationPhase.MeshUpload
        };

        private readonly double[] _milliseconds = new double[Order.Length + 1];
        private readonly bool[] _measured = new bool[Order.Length + 1];

        /// <summary>
        /// The timings the code on this execution context is reporting into, or <c>null</c> when
        /// nobody is listening. A stage that wants to be timed does
        /// <c>Current?.Record(phase, elapsed)</c> and pays nothing when it is not being asked for.
        ///
        /// <para>
        /// <b>How a stage outside this file reports.</b> The generation pass is inside
        /// <see cref="RefreshBlocking"/>, which calls <see cref="Begin"/> for the duration, so
        /// <c>NeighbourhoodMapGenerator.Generate</c> only has to wrap each of its three stages in a
        /// stopwatch and call <c>Current?.Record(NeighbourhoodGenerationPhase.Roads, elapsed)</c> -
        /// or, simpler, <c>Current?.Measure(phase, () =&gt; ...)</c> - and the log line the refresh
        /// writes a moment later picks it up. The mesh stages are on the other side of a worker
        /// boundary of their own, so whoever builds the mesh calls <see cref="Begin"/> itself and
        /// <see cref="Log"/> when it is done; the two lines then name their own refresh.
        /// </para>
        ///
        /// <para>
        /// <b>Why a scope and not a plain static.</b> Two neighbourhoods can be generating at once -
        /// the player travelled while the first was still running - and a process-wide
        /// <see cref="Action{T}"/> sink would have them writing into each other's numbers. It flows
        /// with the execution context instead, so a value published inside a worker is that
        /// worker's and reaches nobody else.
        /// </para>
        /// </summary>
        public static NeighbourhoodGenerationTimings Current
        {
            get { return Scope.Value; }
            set { Scope.Value = value; }
        }

        /// <summary>
        /// Starts a set of timings and makes it the one this thread and its children report into.
        /// </summary>
        public static NeighbourhoodGenerationTimings Begin()
        {
            var timings = new NeighbourhoodGenerationTimings();
            Current = timings;
            return timings;
        }

        /// <summary>Stops reporting into whatever is current.</summary>
        public static void End()
        {
            Current = null;
        }

        /// <summary>The stage's name as it appears in the log line.</summary>
        /// <param name="phase">The stage.</param>
        public static string Label(NeighbourhoodGenerationPhase phase)
        {
            switch (phase)
            {
                case NeighbourhoodGenerationPhase.SidecarRead: return "sidecar read";
                case NeighbourhoodGenerationPhase.LotReads: return "lot reads";
                case NeighbourhoodGenerationPhase.LayoutBuild: return "layout";
                case NeighbourhoodGenerationPhase.LayoutWrite: return "sidecar write";
                case NeighbourhoodGenerationPhase.MapInput: return "map input";
                case NeighbourhoodGenerationPhase.Roads: return "roads";
                case NeighbourhoodGenerationPhase.TerrainFill: return "terrain fill";
                case NeighbourhoodGenerationPhase.SeamSolve: return "seam solve";
                case NeighbourhoodGenerationPhase.MapGenerate: return "generation";
                case NeighbourhoodGenerationPhase.MeshBuild: return "mesh build";
                case NeighbourhoodGenerationPhase.MeshUpload: return "mesh upload";
                case NeighbourhoodGenerationPhase.Total: return "total";
                default: return phase.ToString();
            }
        }

        /// <summary>How long a stage took, or <see cref="TimeSpan.Zero"/> if it was not measured.</summary>
        /// <param name="phase">The stage.</param>
        public TimeSpan Elapsed(NeighbourhoodGenerationPhase phase)
        {
            return TimeSpan.FromMilliseconds(_milliseconds[Index(phase)]);
        }

        /// <summary>Whether anything reported a time for this stage.</summary>
        /// <param name="phase">The stage.</param>
        public bool WasMeasured(NeighbourhoodGenerationPhase phase)
        {
            return _measured[Index(phase)];
        }

        /// <summary>
        /// Adds a stage's time. A stage may be reported more than once - the mesh build, for one,
        /// is per lot - and the times add, because the question the log answers is "how long was
        /// this stage in total", not "how long was the longest one".
        /// </summary>
        /// <param name="phase">The stage.</param>
        /// <param name="elapsed">How long it took.</param>
        public void Record(NeighbourhoodGenerationPhase phase, TimeSpan elapsed)
        {
            var index = Index(phase);
            _milliseconds[index] += elapsed.TotalMilliseconds;
            _measured[index] = true;
        }

        /// <summary>
        /// Runs a stage, times it, and lets whatever it throws through. Timed in a
        /// <c>finally</c> on purpose: a generation that fails halfway is exactly the run whose log
        /// line is worth reading, and a stage that threw is a stage that took time.
        /// </summary>
        /// <param name="phase">The stage.</param>
        /// <param name="work">The work to run.</param>
        public void Measure(NeighbourhoodGenerationPhase phase, Action work)
        {
            if (work == null) return;
            var watch = Stopwatch.StartNew();
            try
            {
                work();
            }
            finally
            {
                watch.Stop();
                Record(phase, watch.Elapsed);
            }
        }

        /// <summary>
        /// The same, for a stage that produces a value - the total, which is the whole refresh and
        /// therefore the only stage that cannot be an <see cref="Action"/>.
        /// </summary>
        /// <typeparam name="T">What the stage produces.</typeparam>
        /// <param name="phase">The stage.</param>
        /// <param name="work">The work to run.</param>
        /// <returns>Whatever the stage produced.</returns>
        public T Measure<T>(NeighbourhoodGenerationPhase phase, Func<T> work)
        {
            if (work == null) return default(T);
            var watch = Stopwatch.StartNew();
            try
            {
                return work();
            }
            finally
            {
                watch.Stop();
                Record(phase, watch.Elapsed);
            }
        }

        /// <summary>
        /// The stages in this list that nothing reported a time for, in the order they run. A
        /// diagnostic: it is what stops a phase being left out of the log line by accident.
        /// </summary>
        public IReadOnlyList<string> UnmeasuredLabels()
        {
            var missing = new List<string>();
            for (int i = 0; i < Order.Length; i++)
            {
                if (!_measured[i]) missing.Add(Label(Order[i]));
            }
            return missing;
        }

        /// <summary>
        /// The log line's body: every stage that was measured, in order, then the total. Never
        /// empty, never null - a refresh that did nothing still has a total.
        /// </summary>
        public string Describe()
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < Order.Length; i++)
            {
                if (!_measured[i]) continue;
                if (text.Length > 0) text.Append(", ");
                text.Append(Label(Order[i])).Append(' ').Append(Millis(_milliseconds[i]));
            }

            if (WasMeasured(NeighbourhoodGenerationPhase.Total))
            {
                if (text.Length > 0) text.Append("; ");
                text.Append("total ").Append(Millis(_milliseconds[Index(NeighbourhoodGenerationPhase.Total)]));
            }

            return text.Length == 0 ? "nothing was measured" : text.ToString();
        }

        /// <summary>
        /// The most recent set of timings any refresh published, or <c>null</c> if none has.
        ///
        /// <para>
        /// A diagnostic, and the only way a caller of <see cref="RefreshBlocking"/> can see the
        /// breakdown at all - the timings live on the worker that did the work and are not returned
        /// from it, so this is where "the last generation took these" is read from. Written only by
        /// <see cref="Log"/>, so it holds a refresh that did something rather than a cache hit.
        /// </para>
        /// </summary>
        public static NeighbourhoodGenerationTimings LastPublished { get; private set; }

        /// <summary>
        /// Writes the breakdown to the log, naming the stages nothing measured so that a line with
        /// a gap in it says which gap.
        /// </summary>
        /// <param name="slot">The neighbourhood slot the refresh was for.</param>
        public void Log(int slot)
        {
            LastPublished = this;
            var missing = UnmeasuredLabels();
            SimitoneLog.Info("Neighbourhood " + slot + ": " + Describe()
                + (missing.Count == 0
                    ? "."
                    : " (not measured here: " + string.Join(", ", missing) + ")."));
        }

        private static string Millis(double value)
        {
            //milliseconds, to a tenth below a second and to a whole one above: "8420ms" is easier
            //to scan in a log than "8.4200ms", and "0.4ms" is worth the precision because the cheap
            //stages are the ones being compared against each other
            return (value >= 1000d
                ? Math.Round(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + "ms";
        }

        private static int Index(NeighbourhoodGenerationPhase phase)
        {
            if (phase == NeighbourhoodGenerationPhase.Total) return Order.Length;
            var index = (int)phase;
            return index >= 0 && index < Order.Length ? index : 0;
        }
    }

    /// <summary>
    /// The one place that decides what a neighbourhood's generated world needs, does it, keeps it
    /// and says what it did. M5's entry point, and the only writer of the generated-world log.
    ///
    /// <para>
    /// <b>What it is for.</b> §8 gives <c>NeighborhoodService</c> the job - "build/refresh layout
    /// on <c>NeighSelection</c>; invalidate on <c>PlayHouse</c>, <c>ExitLot</c>, <c>EvictLot</c>,
    /// <c>ResetHouse</c>, <c>RezoneLot</c>; keep 2D path on failure" - and every one of those five
    /// invalidation points is a different reason to throw a generated world away, and one of them
    /// (<c>ExitLot</c>) is not even a method on that class: <c>TS1GameScreen.ExitLot</c> calls
    /// back into <c>NeighSelection</c>. Putting the rule in one static type rather than in five
    /// call sites is what makes "editing a lot is reflected on return" a property of the design
    /// instead of a list of places somebody has to remember.
    /// </para>
    ///
    /// <para>
    /// <b>The gate is the setting.</b> Nothing here reads a file, stats a lot or generates a tile
    /// unless <see cref="NeighbourhoodViewSetting"/> says <see cref="NeighbourhoodView.World3D"/>.
    /// §0 decision 1 and §10 are the same promise twice: the 3D neighbourhood is opt-in, so a
    /// player who has not chosen it pays nothing for any of this. That is checked first, in
    /// <see cref="RefreshNow"/> and again in <see cref="RequestedView"/>, and it is why
    /// <see cref="InvalidateLot"/> does no more than count.
    /// </para>
    ///
    /// <para>
    /// <b>There is a second gate, and it is narrower.</b> B7 -
    /// <see cref="NeighbourhoodWorldFlag.Persist"/> - decides whether the sidecar carries a
    /// session as well as a layout, and it is off by default like every other Part B capability.
    /// With it off, a refresh reads no session out of a file and writes none into one: the layout
    /// it hands to the store has no <c>worldState</c> member at all, so the document on disk is the
    /// one Part A wrote, byte for byte. With it on, the session is carried through the read, merged
    /// onto whatever arrangement the refresh arrived at, written atomically, and handed back on
    /// <see cref="NeighbourhoodGeneratedWorld.WorldState"/> so a caller never has to open the file
    /// to find out what time it is. The two rules are the same shape on purpose - a capability
    /// that is off has to be invisible, not merely unused - and both live in this file rather than
    /// in the callers, so a caller cannot get one of them wrong by forgetting.
    /// </para>
    /// </summary>
    public static class NeighbourhoodLifecycle
    {
        /// <summary>
        /// Guards <see cref="Jobs"/>, the one entry per slot saying whether a refresh is running.
        ///
        /// <para>
        /// A separate lock from the cache's, and always taken first. A refresh's worker touches the
        /// cache but never this table, so the order is one-directional and cannot deadlock; and
        /// keeping them apart means a caller that wants to know "is one in flight" never has to
        /// queue behind somebody storing a world.
        /// </para>
        /// </summary>
        private static readonly object JobsGate = new object();

        /// <summary>
        /// The refresh in flight per slot, if any. A finished job is pruned on the next lookup, so
        /// this holds at most one entry per slot rather than one per visit.
        /// </summary>
        private static readonly Dictionary<int, NeighbourhoodRefreshJob> Jobs =
            new Dictionary<int, NeighbourhoodRefreshJob>();

        /// <summary>
        /// How long a caller may wait for a layout repair before building the neighbourhood screen
        /// anyway. Long enough for twenty IFF files, short enough that a pathological disk cannot
        /// hang the screen.
        /// </summary>
        public static readonly TimeSpan LayoutWait = TimeSpan.FromSeconds(3);

        /// <summary>
        /// The view the player has chosen, resolved, and defensive: a settings object that will not
        /// read is the same as never having chosen, which is 2D.
        /// </summary>
        public static NeighbourhoodView RequestedView
        {
            get
            {
                try
                {
                    return GraphicsPreferences.From(GlobalSettings.Default).NeighbourhoodView;
                }
                catch (Exception)
                {
                    return NeighbourhoodViewSetting.Default;
                }
            }
        }

        /// <summary>Whether a generated world is wanted at all.</summary>
        public static bool WantsWorld
        {
            get { return RequestedView == NeighbourhoodView.World3D; }
        }

        /// <summary>
        /// Records that a lot changed, and drops the generated world that was built before it did.
        ///
        /// <para>
        /// This is the cheap half of every invalidation and the only one that happens on the
        /// neighbourhood screen itself, where a bulldoze has just rewritten a lot's
        /// <c>HouseNN.iff</c>. It does not repair anything: the repair happens on the next
        /// <see cref="RefreshNow"/>, off the game thread, with the lot list this call recorded.
        /// </para>
        /// </summary>
        /// <param name="slot">The neighbourhood slot the lot belongs to.</param>
        /// <param name="lotNumber">The lot that changed.</param>
        /// <param name="why">A short clause for the log, e.g. "the lot was bulldozed".</param>
        public static void InvalidateLot(int slot, short lotNumber, string why)
        {
            if (!WantsWorld) return;

            var had = NeighbourhoodWorldCache.InvalidateLot(slot, lotNumber);
            SimitoneLog.Info("The generated neighbourhood for slot " + slot
                + " no longer matches lot " + lotNumber + " (" + (why ?? "the lot file changed")
                + "), so its generated map" + (had ? " was dropped" : " will be rebuilt next time the neighbourhood opens")
                + ".");
        }

        /// <summary>
        /// Records that a whole neighbourhood changed - a switch, or a change to the setting - and
        /// drops the generated world for it.
        /// </summary>
        /// <param name="slot">The neighbourhood slot.</param>
        /// <param name="why">A short clause for the log.</param>
        public static void InvalidateSlot(int slot, string why)
        {
            if (!WantsWorld) return;
            var had = NeighbourhoodWorldCache.Invalidate(slot);
            SimitoneLog.Info("The generated neighbourhood for slot " + slot + " was dropped ("
                + (why ?? "the neighbourhood changed") + ")" + (had ? "." : "; nothing was cached for it."));
        }

        /// <summary>
        /// Brings a neighbourhood's generated world up to date, without waiting for it to be built.
        ///
        /// <para>
        /// <b>This is the call the neighbourhood panel makes, and it never blocks.</b> The panel is
        /// built inside a screen transition on the game thread, and the expensive half of this
        /// pipeline - twenty <c>HouseNN.iff</c> reads, a relaxation pass, and 262 144 tiles of
        /// streets, ground, water and elevation - is seconds of arithmetic and disk. Doing it here,
        /// on the thread that draws the frame, is what made entering a neighbourhood freeze the
        /// game until the world existed. So this method answers with whatever it can answer
        /// immediately and puts the rest on a worker:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>
        ///     <b>The setting is not 3D.</b> Nothing is read, generated or waited for, and the
        ///     painted map is the answer - unchanged, because a player who has not opted in has
        ///     never paid for any of this.
        ///   </description></item>
        ///   <item><description>
        ///     <b>A world is in memory and still matches the layout on disk.</b> It is handed
        ///     straight back, which is what makes a second visit cost one small file read and no
        ///     arithmetic at all.
        ///   </description></item>
        ///   <item><description>
        ///     <b>Otherwise</b> a refresh is started if one is not already running for this slot,
        ///     and the answer is a world that says so. It has no raster, so the caller draws the
        ///     painted map, and the finished world is applied on the game thread through
        ///     <see cref="NeighbourhoodRefreshJob"/> when it lands.
        ///   </description></item>
        /// </list>
        ///
        /// <para>
        /// <b>What it costs on a cold cache: a few microseconds.</b> No disk, no lot read, no
        /// generation, and a rule over three booleans - see <see cref="NeighbourhoodSchedule"/>. The
        /// whole pipeline it used to run here is now
        /// <see cref="RefreshBlocking"/>, which runs on the worker this method starts.
        /// </para>
        /// </summary>
        /// <param name="userDir">The user directory the sidecar lives in; null means "no sidecar".</param>
        /// <param name="slot">The neighbourhood slot id, which is also the sidecar's name.</param>
        /// <param name="mode">The neighbourhood mode, 1..8.</param>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="source">
        /// The neighbourhood to read. Handed to the worker rather than read here, so it must not be
        /// used by the caller afterwards; the panel builds a fresh one per call for exactly that
        /// reason.
        /// </param>
        /// <param name="onLayoutWritten">
        /// Called once the layout on disk is known to be current for this refresh, which on a cache
        /// hit is before this method returns and otherwise is on the worker the moment the sidecar
        /// is written. May be <c>null</c>.
        /// </param>
        public static NeighbourhoodGeneratedWorld RefreshNow(
            string userDir, int slot, int mode, NeighbourhoodView requested,
            ILotContentSource source, Action onLayoutWritten = null)
        {
            var view = NeighbourhoodViewSetting.Resolve(requested);
            if (view != NeighbourhoodView.World3D)
            {
                var skipped = NeighbourhoodRefreshPlan.Decide(view, null,
                    NeighbourhoodSceneSource.SeedForMode(slot),
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false);
                onLayoutWritten?.Invoke();
                return NeighbourhoodGeneratedWorld.Failed(slot, null, skipped, null);
            }

            //A refresh in flight settles the answer whatever the cache holds, so the cache is not
            //even asked: it costs a sidecar read, and a caller that polls while a generation runs
            //would pay that on every frame for the several seconds the generation takes. The
            //ordering this guards is the same one NeighbourhoodSchedule.Decide states; doing the
            //check here is about the cost of asking, not about which answer comes back.
            var running = RefreshInFlight(slot);
            var cached = running == null ? CachedWorldFor(userDir, slot) : null;
            var schedule = NeighbourhoodSchedule.Decide(view, cached != null, running != null);

            switch (schedule.Action)
            {
                case NeighbourhoodScheduleAction.UseCache:
                    NeighbourhoodWorldCache.CountHit();
                    //the sidecar on disk is this world's own layout: the two were written together
                    //by the refresh that built it, and a repair that has since been written would
                    //have dropped the entry rather than left it here
                    onLayoutWritten?.Invoke();
                    return cached;

                case NeighbourhoodScheduleAction.JoinRefresh:
                    NeighbourhoodWorldCache.CountJoined();
                    return Refreshing(slot, view, schedule);

                case NeighbourhoodScheduleAction.StartRefresh:
                    //One attempt is one attempt. A refresh that finished without a world - a road
                    //graph that would not connect, a lot file that will not read - would otherwise
                    //be started again by the very next frame, and the frame after that, for as
                    //long as the player stays in this neighbourhood. A world with no raster cannot
                    //be cached, so the refusal is remembered here instead, against the epoch it
                    //happened in: until something invalidates the slot - which is the only thing
                    //that can change the answer - the same refusal comes back.
                    var refused = LastRefusal(slot, view);
                    if (refused != null) return refused;

                    Ensure(userDir, slot, mode, view, () => source, null);
                    return Refreshing(slot, view, schedule);
            }

            //NeighbourhoodScheduleAction.Nothing is unreachable - the only way to get it is a
            //setting that is not 3D, and that returned above - so the answer is the same world that
            //gate hands back rather than a second, differently worded one.
            var notWanted = NeighbourhoodRefreshPlan.Decide(view, null,
                NeighbourhoodSceneSource.SeedForMode(slot),
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false);
            return NeighbourhoodGeneratedWorld.Failed(slot, null, notWanted, null);
        }

        /// <summary>
        /// What the last finished refresh for a slot said, when it said no - and the answer is
        /// still no, because nothing has invalidated the slot since.
        ///
        /// <para>
        /// A world with no raster is deliberately not cached, so this is where "we already tried and
        /// it did not work" lives. The epoch is what makes it safe: a bulldoze bumps it, so the
        /// next request starts a fresh refresh instead of being handed the refusal from before it.
        /// </para>
        /// </summary>
        private static NeighbourhoodGeneratedWorld LastRefusal(int slot, NeighbourhoodView view)
        {
            NeighbourhoodRefreshJob finished;
            lock (JobsGate)
            {
                if (!Jobs.TryGetValue(slot, out finished)) return null;
                if (!finished.IsFinished) return null;
                if (finished.Epoch != NeighbourhoodWorldCache.EpochOf(slot)) return null;
            }

            var world = finished.World;
            if (world != null)
            {
                //the refresh's own refusal, with the reason it decided on: a road graph that would
                //not connect, a layout that would not pack
                return world.HasWorld ? null : world;
            }

            //the refresh itself threw, so there is no world and no reason of its own to quote: the
            //log has the exception, and the screen only has to say that it has already been tried
            const string why = "the last attempt to generate this neighbourhood did not produce a world";
            return NeighbourhoodGeneratedWorld.Failed(slot, null,
                NeighbourhoodRefreshPlan.ForFailure(
                    NeighbourhoodRefreshPlan.Refreshing(view, null), why),
                why);
        }

        /// <summary>
        /// The world a caller gets while a refresh for its slot is running: no raster, no failure,
        /// a plan that says 3D is what this is being built for, and the reason it is not ready.
        /// </summary>
        private static NeighbourhoodGeneratedWorld Refreshing(
            int slot, NeighbourhoodView view, NeighbourhoodSchedule schedule)
        {
            return NeighbourhoodGeneratedWorld.Refreshing(slot,
                NeighbourhoodRefreshPlan.Refreshing(view, schedule.Reason), schedule.Reason);
        }

        /// <summary>
        /// The cached world for a slot, but only if it still describes the layout on disk.
        ///
        /// <para>
        /// The field-for-field comparison is the guard that lets the cache be authoritative between
        /// invalidations. A refresh writes the sidecar and then puts its world in the cache, so the
        /// two normally agree by construction; they can only disagree if something wrote a layout
        /// in between, and a layout that is not this world's means the world in memory is a
        /// previous answer. Comparing them is cheaper than the alternative - re-reading every lot
        /// to find out whether anything moved - and it is the same comparison the plan makes.
        /// </para>
        ///
        /// <para>
        /// <b>And the sidecar's session comes back with the layout.</b> The comparison deliberately
        /// says nothing about <see cref="NeighbourhoodLayout.WorldState"/> - a session that has
        /// moved on is not a different neighbourhood, and treating it as one would turn every save
        /// into a cache miss - so a world cached before B7 was turned on would otherwise be handed
        /// out unable to answer "what time is it". When the flag is on and the world itself has no
        /// session, the one in the file is attached here. That is the only place the two can meet,
        /// because a cache hit reads no file of its own.
        /// </para>
        /// </summary>
        /// <param name="userDir">The user directory the sidecar lives in.</param>
        /// <param name="slot">The neighbourhood slot id.</param>
        public static NeighbourhoodGeneratedWorld CachedWorldFor(string userDir, int slot)
        {
            var world = NeighbourhoodWorldCache.Peek(slot);
            if (world == null || !world.HasWorld || world.Map == null) return null;
            var onDisk = NeighbourhoodLayoutStore.Load(userDir, slot);
            if (!SameLayout(world.Layout, onDisk)) return null;
            return WorldWithSession(world, onDisk);
        }

        /// <summary>
        /// Runs the whole pipeline, on this thread, and logs how long each stage took.
        ///
        /// <para>
        /// This is what <see cref="RefreshNow"/> used to do and what its worker does instead. It is
        /// the only place that reads lot files, packs, writes the sidecar and generates the raster,
        /// and it is deliberately a separate public method rather than a private one so that "the
        /// pipeline still works" is testable on its own: a test can drive it to completion in a
        /// couple of hundred milliseconds with a fake neighbourhood, which is the only way to assert
        /// anything about the phases at all.
        /// </para>
        /// <para>
        /// <b>Never call this from the game thread.</b> It is the several seconds the panel used to
        /// spend in a screen transition, and everything it does is IO and arithmetic that §10 asks
        /// for off that thread. Nothing it produces is mutable and nothing it returns is shared
        /// until the job that wraps it publishes it.
        /// </para>
        /// <para>
        /// <b>What it logs.</b> The stage breakdown, from
        /// <see cref="NeighbourhoodGenerationTimings"/>, on the same line as the world it produced.
        /// Eleven seconds of neighbourhood with no breakdown is a measurement with no next step;
        /// this is the answer to "which of the four halves is slow" without a profiler.
        /// </para>
        /// </summary>
        /// <param name="userDir">The user directory the sidecar lives in; null means "no sidecar".</param>
        /// <param name="slot">The neighbourhood slot id, which is also the sidecar's name.</param>
        /// <param name="mode">The neighbourhood mode, 1..8.</param>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="source">The neighbourhood to read.</param>
        /// <param name="onLayoutWritten">
        /// Called the moment the sidecar on disk is current, before the raster is generated. This is
        /// the boundary a caller that is about to build the neighbourhood screen waits on: the
        /// panel reads the sidecar synchronously, and the 512x512 generation is the expensive half
        /// it does not need to wait for. May be <c>null</c>.
        /// </param>
        public static NeighbourhoodGeneratedWorld RefreshBlocking(
            string userDir, int slot, int mode, NeighbourhoodView requested,
            ILotContentSource source, Action onLayoutWritten = null)
        {
            var timings = NeighbourhoodGenerationTimings.Begin();
            try
            {
                //the total is measured around the whole thing, so it is the number the panel's
                //eleven-second wait would have been, and it is measured whether the refresh built
                //anything or not
                return timings.Measure(NeighbourhoodGenerationPhase.Total,
                    () => Run(userDir, slot, mode, requested, source, onLayoutWritten, timings));
            }
            finally
            {
                NeighbourhoodGenerationTimings.End();
            }
        }

        /// <summary>
        /// The pipeline itself, with every expensive step wrapped in the timings it is reported
        /// under. Split out of <see cref="RefreshBlocking"/> so that the timings object can be
        /// created and disposed around it without a second try block.
        /// </summary>
        private static NeighbourhoodGeneratedWorld Run(
            string userDir, int slot, int mode, NeighbourhoodView requested,
            ILotContentSource source, Action onLayoutWritten,
            NeighbourhoodGenerationTimings timings)
        {
            var view = NeighbourhoodViewSetting.Resolve(requested);
            var seed = NeighbourhoodSceneSource.SeedForMode(slot);
            var generatorVersion = NeighbourhoodLayoutBuilder.CurrentGeneratorVersion;

            if (view != NeighbourhoodView.World3D)
            {
                var skipped = NeighbourhoodRefreshPlan.Decide(view, null, seed, generatorVersion, null, false);
                return NeighbourhoodGeneratedWorld.Failed(slot, null, skipped, null);
            }

            NeighbourhoodLayout cached = null;
            timings.Measure(NeighbourhoodGenerationPhase.SidecarRead,
                () => { cached = NeighbourhoodLayoutStore.Load(userDir, slot); });

            IReadOnlyList<short> changed = new short[0];
            IReadOnlyList<NeighbourhoodLotRead> reads = null;

            if (cached != null && cached.HasCurrentSchema && cached.IsCurrentFor(seed, generatorVersion))
            {
                //§4.2's cheap case, and the reason this method is worth having: a lot that did not
                //change is not re-read at all. ReadLots is only asked for when the sidecar exists
                //and is current, and its cost - one IFF per lot - is the cost being avoided.
                reads = ReadLots(source, mode, timings);
                changed = StaleByHouseHash(cached, reads);
            }

            var inMemory = NeighbourhoodWorldCache.Peek(slot);
            var rasterCached = inMemory != null && inMemory.Map != null
                && inMemory.Layout != null
                && SameLayout(inMemory.Layout, cached);
            var plan = NeighbourhoodRefreshPlan.Decide(
                view, cached, seed, generatorVersion, changed, rasterCached);

            if (!plan.BuildWorld)
            {
                //The gate above already returned, so this is unreachable today and is here so that
                //a future caller who reuses Decide cannot accidentally build a world it was told
                //not to.
                return NeighbourhoodGeneratedWorld.Failed(slot, cached, plan, null);
            }

            if (plan.IsCacheHit)
            {
                NeighbourhoodWorldCache.CountHit();
                return WorldWithSession(inMemory, cached);
            }
            NeighbourhoodWorldCache.CountMiss();

            if (reads == null) reads = ReadLots(source, mode, timings);

            //Part 8's hand-off. `ReadLots` inferred the connector mask for this mode (WS1's
            //`NeighbourhoodLayoutBuilder` does that as its last statement), but the mask is keyed by
            //slot and the reader of a neighbourhood only knows the mode - so it has to be pointed at
            //the slot before anything below can find it. Two things are waiting for it: `Pack` uses it
            //to decide which edge each lot's driveway leaves from, and `InputFor` hands it to the
            //generator so the streets in `RoadData` are the ones drawn on the 2D map rather than ones
            //measured off the lots. Without this call the whole inference is computed and thrown away,
            //which is what made "lots are at their real positions, streets are still synthetic" true.
            BindConnectorMask(slot, mode, NeighbourhoodLayout.DefaultMapSize);

            NeighbourhoodLayout layout = null;
            timings.Measure(NeighbourhoodGenerationPhase.LayoutBuild, () =>
            {
                if (plan.PatchOnlyChangedLots && NeighbourhoodConnected.MapDerivedPlacements)
                {
                    var patched = NeighbourhoodLayoutPatch.Apply(cached, reads, slot, seed);
                    layout = patched.Layout;
                    SimitoneLog.Info("Neighbourhood " + slot + ": " + patched + ".");
                    if (patched.RepackedEverything)
                    {
                        //The plan said "only these lots"; the repair could not honour it, so the
                        //facade cache has to be told that every lot is a different lot now. The panel
                        //drops and rebuilds its facades on a layout change anyway, so this is a log
                        //line rather than a second mechanism - but it is the difference between a
                        //player understanding the map and not.
                        SimitoneLog.Warn("Neighbourhood " + slot
                            + ": the layout had to be packed again in full, so every lot in it is"
                            + " placed differently from the last visit.");
                    }
                }
                else
                {
                    //The shipped revert packs synthetically. The map-derived placement is the
                    //connected-mechanics change the owner asked to gate off; see NeighbourhoodConnected.
                    layout = NeighbourhoodConnected.MapDerivedPlacements
                        ? NeighbourhoodLayoutBuilder.Pack(reads, slot, seed)
                        : NeighbourhoodLayoutBuilder.PackOnGrid(reads, slot, seed);
                }
            });

            if (layout == null)
            {
                return NeighbourhoodGeneratedWorld.Failed(slot, null, plan,
                    "the layout could not be packed");
            }

            //The bind above used the default map size, which is what `Pack(reads, slot, seed)` packs
            //at. A layout that came out at some other size - the patch path, or a caller that asked
            //for one - needs its own binding, because `InputFor` looks the mask up by the layout's own
            //map size and a mask classified on a different grid is not the mask for this one.
            if (layout.MapSize != NeighbourhoodLayout.DefaultMapSize)
            {
                BindConnectorMask(slot, mode, layout.MapSize);
            }

            //B7, and the only place the session is decided. Everything above rebuilt the layout -
            //possibly from scratch, possibly lot by lot - and the session belongs to the
            //neighbourhood rather than to any of that, so it is re-attached here and carried on
            //into the write and into the world. With the flag off this strips it again, which is
            //what makes a Part A sidecar the document Part A wrote: a layout with no world state
            //has no worldState member at all.
            layout = LayoutToWrite(layout, cached);

            //Best effort, and deliberately so: the sidecar is a cache, and a neighbourhood that
            //cannot be written to disk still generates identically every time. Save refuses an
            //unknown slot, never throws, and never writes inside UserData.
            timings.Measure(NeighbourhoodGenerationPhase.LayoutWrite,
                () => { NeighbourhoodLayoutStore.Save(userDir, layout); });

            //The layout is current on disk from here. Anything that is about to read the sidecar
            //may stop waiting; the quarter of a million tiles of generation below is not its
            //business.
            onLayoutWritten?.Invoke();

            if (!plan.RegenerateMap && inMemory?.Map != null)
            {
                //Nothing about the layout moved, so the raster in memory is still the right one.
                //The facade half of the plan is likewise a no-op, which is the point: returning to
                //an unedited neighbourhood costs one small file read.
                return WorldWithSession(inMemory, layout);
            }

            NeighbourhoodMap map = null;

            //The raster half of the sidecar's job, and the half §4.2 declined to store. The layout
            //above is a few kilobytes of JSON that says which world is wanted; this is the answer,
            //beside it, stamped with the seed, the generator version, the map size and a hash of
            //the arrangement. A file that checks out on all four is the map the generator would
            //produce, so it is used rather than produced - which is what makes a second *launch*
            //cost a three-megabyte read instead of several seconds of arithmetic. A file that does
            //not check out is not an error: it is a cache miss, and the generator below is the
            //answer to that. Silent when there is no file at all, the same as the layout sidecar.
            timings.Measure(NeighbourhoodGenerationPhase.MapGenerate,
                () => { map = NeighbourhoodRasterCache.Load(userDir, slot, layout); });

            if (map == null)
            {
                NeighbourhoodMapInput input = null;
                timings.Measure(NeighbourhoodGenerationPhase.MapInput,
                    () => { input = NeighbourhoodMapAdapter.InputFor(layout); });

                try
                {
                    timings.Measure(NeighbourhoodGenerationPhase.MapGenerate,
                        () => { map = NeighbourhoodMapGenerator.Generate(input); });
                }
                catch (Exception e)
                {
                    NeighbourhoodWorldCache.CountFallback();
                    SimitoneLog.Warn("Neighbourhood " + slot + " is drawn in 2D: the generated world could"
                        + " not be built (" + e.Message + ").");
                    timings.Log(slot);
                    return NeighbourhoodGeneratedWorld.Failed(slot, layout, plan, e.Message);
                }

                //Stored, best effort, and for the same reason the layout write above is: this is a
                //cache file, not the world. A neighbourhood that cannot be written to disk still
                //generates identically, so a failure is a log line and never a fallback to 2D.
                NeighbourhoodRasterCache.Save(userDir, layout, map);
            }
            else
            {
                SimitoneLog.Info("Neighbourhood " + slot + ": the generated map was read from '"
                    + NeighbourhoodRasterCache.PathFor(userDir, slot)
                    + "' rather than generated again.");
            }

            var world = NeighbourhoodGeneratedWorld.Built(slot, layout, map, plan);
            SimitoneLog.Info(world.Describe() + ".");
            //the breakdown, on the worker that did the work, and only on a refresh that built
            //something: a cache hit measures one small file read and the line above already says
            //the world was reused
            timings.Log(slot);
            return world;
        }

        /// <summary>
        /// Every lot in a neighbourhood, timed under the phase that reports it. One IFF per lot is
        /// the single most expensive thing here after the generation itself, and it is the phase
        /// that tells a slow disk from a slow relaxation pass.
        /// </summary>
        private static IReadOnlyList<NeighbourhoodLotRead> ReadLots(
            ILotContentSource source, int mode, NeighbourhoodGenerationTimings timings)
        {
            IReadOnlyList<NeighbourhoodLotRead> reads = null;
            timings.Measure(NeighbourhoodGenerationPhase.LotReads,
                () => { reads = NeighbourhoodLayoutBuilder.ReadLots(source, mode,
                    NeighbourhoodConnected.InferredConnectors); });
            return reads;
        }

        /// <summary>
        /// Points a neighbourhood slot at the connector mask that was inferred for its mode, so the
        /// packer and the generator can both find it.
        ///
        /// <para><b>The half of the seam that cannot be derived.</b> Part 8 §8.3 has the mask built
        /// where the mode is known - <see cref="NeighbourhoodLayoutBuilder.ReadLots"/>, which is the
        /// only place in the pipeline that reads a neighbourhood's own backdrop - and consumed where
        /// the slot is known: <see cref="NeighbourhoodLayoutBuilder.Pack"/> for the door sides, and
        /// <see cref="NeighbourhoodMapAdapter.InputFor"/> for the streets themselves. A
        /// <see cref="NeighbourhoodLayout"/> carries neither a mode nor a slot-to-mode table, so
        /// something has to make the connection and it has to be explicit: guessing - "the slot is
        /// the mode", which is true for four of the eight views and wrong for the other four - would
        /// produce a neighbourhood with the wrong streets in it rather than an error.</para>
        ///
        /// <para><b>Best effort, and never a failure.</b> A neighbourhood whose backdrop could not be
        /// read, could not be decoded, or classified as degenerate still has an answer:
        /// <see cref="NeighbourhoodRoadInference"/>'s deterministic lot-lattice fallback, already
        /// memoised under the mode. So this only reports; a caller that cannot reach the cache falls
        /// back to the pre-Part-8 answer of measured streets, which is what a neighbourhood with no
        /// backdrop deserves anyway.</para>
        /// </summary>
        /// <param name="slot">The neighbourhood slot the world is being generated for.</param>
        /// <param name="mode">The neighbourhood view the slot is showing.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        private static void BindConnectorMask(int slot, int mode, int mapSize)
        {
            if (mapSize < 1) return;
            //The shipped revert does not infer connectors off the 2D map's pixels; the road graph
            //measures its own streets. See NeighbourhoodConnected.InferredConnectors.
            if (!NeighbourhoodConnected.InferredConnectors) return;
            try
            {
                NeighbourhoodLayoutBuilder.BindRoadMask(slot, mode, mapSize);
                var reason = NeighbourhoodRoadInference.ReasonForSlot(slot, mapSize);
                if (reason != null)
                {
                    SimitoneLog.Info("Neighbourhood " + slot + " (view " + mode + ") at "
                        + mapSize + "x" + mapSize + " tiles: " + reason + ".");
                }
            }
            catch (Exception e)
            {
                //A diagnostic cache is never the reason a neighbourhood cannot be laid out, so this
                //is a log line and not a fallback to 2D.
                SimitoneLog.Warn("Neighbourhood " + slot + " could not bind its inferred connectors ("
                    + e.Message + "), so the road graph will measure its own streets.", e);
            }
        }

        /// <summary>
        /// Starts a refresh for a slot, or joins the one already running for it.
        ///
        /// <para>
        /// <b>One generation per slot, however many callers ask.</b> The neighbourhood service
        /// starts a refresh before the panel exists, the panel asks again on the frame it is built,
        /// and <see cref="RefreshBackdrop"/> - the panel's per-frame reconcile - would ask again on
        /// every frame after that. Each of those is entitled to a world and none of them is entitled
        /// to its own 262 144 tiles, so the second and later requests subscribe to the first job's
        /// apply step and wait on the same work. That is what
        /// <see cref="NeighbourhoodCacheCounters.Refreshes"/> counts: one per real generation.
        /// </para>
        /// <para>
        /// <b>The apply is per subscriber, not per job.</b> Whoever started the refresh usually does
        /// not know what to do with the answer - the service only wants the cache filled - and
        /// whoever does know may not have been the one to start it. So the job keeps a list and
        /// runs all of them on the game thread, and
        /// <see cref="NeighbourhoodRefreshJob.AddApply"/> lets a late arrival join one that is
        /// already running.
        /// </para>
        ///
        /// <para>
        /// <b>A call here always starts a refresh, even if the last one for this slot failed.</b>
        /// That is deliberate: this is the visit-level route, called once per time the player opens
        /// a neighbourhood, and "try again on the next visit" is the promise the sidecar makes. It
        /// is the per-frame route in <see cref="RefreshNow"/> that must not retry, and that is
        /// where <see cref="LastRefusal"/> stands.
        /// </para>
        /// </summary>
        /// <param name="userDir">The user directory the sidecar lives in.</param>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="mode">The neighbourhood mode, 1..8.</param>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="source">
        /// A factory for the content source, so the provider is touched on the worker rather than
        /// captured across the thread boundary. Invoked at most once, on the worker.
        /// </param>
        /// <param name="apply">Called on the game thread with the finished world. May be null.</param>
        /// <returns>The job for this slot: the one already running, or the one just started.</returns>
        public static NeighbourhoodRefreshJob Ensure(
            string userDir, int slot, int mode, NeighbourhoodView requested,
            Func<ILotContentSource> source, Action<NeighbourhoodGeneratedWorld> apply)
        {
            lock (JobsGate)
            {
                var running = RunningLocked(slot);
                if (running != null)
                {
                    NeighbourhoodWorldCache.CountJoined();
                    running.AddApply(apply);
                    return running;
                }

                var job = NeighbourhoodRefreshJob.Start(slot, onLayoutWritten =>
                {
                    ILotContentSource content = null;
                    try
                    {
                        content = source?.Invoke();
                    }
                    catch (Exception e)
                    {
                        SimitoneLog.Warn("The generated neighbourhood for slot " + slot
                            + " could not be read, so it will be drawn as the painted map.", e);
                    }
                    return RefreshBlocking(userDir, slot, mode, requested, content, onLayoutWritten);
                }, null);

                Jobs[slot] = job;
                NeighbourhoodWorldCache.CountRefresh();
                if (apply != null) job.AddApply(apply);
                return job;
            }
        }

        /// <summary>
        /// The refresh running for a slot, or <c>null</c>. A finished job is not one: the next
        /// request has to start a new one.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        public static NeighbourhoodRefreshJob RefreshInFlight(int slot)
        {
            lock (JobsGate) { return RunningLocked(slot); }
        }

        /// <summary>
        /// <see cref="RefreshInFlight"/>, minus the lock.
        ///
        /// <para>
        /// A finished entry is left in the table rather than pruned. The table is keyed by slot, so
        /// it cannot grow - nine entries at most for the length of a session - and the finished
        /// entry is worth keeping: it is what <see cref="LastRefusal"/> reads to answer "we already
        /// tried, and it did not work" without starting a second attempt on every frame. A new job
        /// overwrites the slot's entry, which is the only way one goes away.
        /// </para>
        /// </summary>
        private static NeighbourhoodRefreshJob RunningLocked(int slot)
        {
            NeighbourhoodRefreshJob job;
            if (!Jobs.TryGetValue(slot, out job)) return null;
            return job.IsFinished ? null : job;
        }

        /// <summary>
        /// Starts a refresh on a worker and applies the result on the game thread - the same thing
        /// <see cref="Ensure"/> does, under the name <c>NeighborhoodService</c> has been calling
        /// since the refresh was first moved off the game thread.
        /// </summary>
        /// <param name="userDir">The user directory the sidecar lives in.</param>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="mode">The neighbourhood mode, 1..8.</param>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="source">
        /// A factory for the content source, so the provider is touched on the worker rather than
        /// captured across the thread boundary.
        /// </param>
        /// <param name="apply">Called on the game thread with the finished world.</param>
        public static NeighbourhoodRefreshJob RefreshInBackground(
            string userDir, int slot, int mode, NeighbourhoodView requested,
            Func<ILotContentSource> source, Action<NeighbourhoodGeneratedWorld> apply)
        {
            return Ensure(userDir, slot, mode, requested, source, apply);
        }

        /// <summary>
        /// Forgets every refresh this process has started or finished.
        ///
        /// <para>
        /// Only a test wants this, for exactly the reason
        /// <see cref="NeighbourhoodWorldCache.Clear"/> does: the table is process-wide, so a test
        /// that asserted "this slot has nothing running" or "this slot was refused" would otherwise
        /// be asserting on whatever the previous test left behind - and unlike the cache, forgetting
        /// the world is not enough, because a finished refresh that produced nothing is remembered
        /// by <see cref="LastRefusal"/> until its slot is invalidated. Clears nothing else: the
        /// worlds themselves are the cache's to keep or drop.
        /// </para>
        /// </summary>
        public static void Forget()
        {
            lock (JobsGate) { Jobs.Clear(); }
        }

        /// <summary>
        /// Which lots no longer hash to what a cached layout recorded.
        ///
        /// <para>
        /// This is <see cref="NeighbourhoodLayoutStore.ChangedLots"/>'s rule over records that
        /// have already been read, which is the point: the caller pays the IFF reads once, and the
        /// comparison itself is arithmetic. A lot in the layout the records do not mention is
        /// reported too - the neighbourhood dropped it - and a lot in the records the layout does
        /// not have is reported for the same reason.
        /// </para>
        /// </summary>
        /// <param name="layout">The cached layout.</param>
        /// <param name="reads">The neighbourhood's lots as they are now.</param>
        public static IReadOnlyList<short> StaleByHouseHash(
            NeighbourhoodLayout layout, IReadOnlyList<NeighbourhoodLotRead> reads)
        {
            var stale = new List<short>();
            if (layout == null || reads == null) return stale;

            var current = new Dictionary<short, uint>(reads.Count);
            for (int i = 0; i < reads.Count; i++)
            {
                current[reads[i].Number] = reads[i].HouseHash;
            }

            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var lot = layout.Lots[i];
                uint hash;
                if (!current.TryGetValue(lot.Number, out hash) || hash != lot.HouseHash) stale.Add(lot.Number);
            }
            for (int i = 0; i < reads.Count; i++)
            {
                if (layout.FindLot(reads[i].Number) == null) stale.Add(reads[i].Number);
            }
            return stale;
        }

        /// <summary>
        /// The same comparison without reading a single IFF: a lot's fingerprint is its file's
        /// length and last write time, and both come from a <see cref="FileInfo"/>.
        ///
        /// <para>
        /// This is the check that makes returning to a neighbourhood free. Twenty
        /// <c>HouseNN.iff</c> parses is the expensive part of a layout build, and on the
        /// overwhelmingly common return - nothing was edited - the answer is that no file's length
        /// or timestamp moved. Only a lot that really did change costs a parse, and then only
        /// that one, because the rest are already known good.
        /// </para>
        /// </summary>
        /// <param name="layout">The cached layout.</param>
        /// <param name="currentHash">
        /// The lot's fingerprint right now, or 0 for a lot with no house file. A delegate rather
        /// than a path so this rule is testable with no neighbourhood folder anywhere.
        /// </param>
        public static IReadOnlyList<short> StaleByFileStat(
            NeighbourhoodLayout layout, Func<short, uint> currentHash)
        {
            var stale = new List<short>();
            if (layout == null || currentHash == null) return stale;
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var lot = layout.Lots[i];
                uint hash;
                try
                {
                    hash = currentHash(lot.Number);
                }
                catch (Exception)
                {
                    //a lot whose file cannot be stat'd is a lot that needs looking at, not a lot
                    //that can be waved through
                    hash = lot.HouseHash == 0u ? 0u : lot.HouseHash + 1u;
                }
                if (hash != lot.HouseHash) stale.Add(lot.Number);
            }
            return stale;
        }

        /// <summary>
        /// A lot's fingerprint from its file, or 0 when there is no file. The delegate
        /// <see cref="StaleByFileStat"/> is given by the caller so this stays the only piece that
        /// knows where a lot's file is.
        /// </summary>
        /// <param name="housePath">The lot's <c>HouseNN.iff</c> path, or <c>null</c>.</param>
        public static uint HouseHashOfFile(string housePath)
        {
            if (string.IsNullOrEmpty(housePath)) return 0u;
            try
            {
                var info = new FileInfo(housePath);
                if (!info.Exists) return 0u;
                return NeighbourhoodLayoutBuilder.HouseHashFor(info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception)
            {
                //no path, no permission, a locked file: a lot this build cannot see is a lot with
                //no house, which is what an absent file means too
                return 0u;
            }
        }

        // ---- the session half of the sidecar (B7) ----------------------------------------------

        /// <summary>
        /// Whether the session half of the sidecar is on at all: B7, and
        /// <see cref="NeighbourhoodWorldFlags.Default"/> until a build turns it on.
        ///
        /// <para>
        /// Public so that a caller asks once rather than per frame. The panel reconciles on every
        /// frame and <see cref="NeighbourhoodGeneratedWorld.WorldState"/> gates itself on the same
        /// flag, and a rule a caller can read once is a rule it cannot get subtly wrong by asking
        /// it in a different place.
        /// </para>
        /// </summary>
        public static bool PersistsWorldState
        {
            get { return NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist); }
        }

        /// <summary>
        /// The same layout with a session stamped onto it, or with none. Pure: it reads no flag and
        /// touches no disk, which is what lets the merge below be stated as a rule and tested
        /// without a capability switch in the way.
        ///
        /// <para>
        /// A new object rather than a mutated one, because <see cref="NeighbourhoodLayout"/> is a
        /// value and the lot records inside it are shared with the layout it was built from - the
        /// session is replaced, nothing else is. Returns the same instance when the session is
        /// already the one asked for, so the ordinary refresh allocates nothing here.
        /// </para>
        /// </summary>
        /// <param name="layout">The layout to stamp, or <c>null</c> for <c>null</c>.</param>
        /// <param name="world">
        /// The session, or <c>null</c> for a layout that carries none - which is exactly the shape
        /// a Part A layout has, and the one that serialises to the document Part A wrote.
        /// </param>
        public static NeighbourhoodLayout WithWorldState(
            NeighbourhoodLayout layout, NeighbourhoodWorldState world)
        {
            if (layout == null) return null;
            if (ReferenceEquals(layout.WorldState, world)) return layout;
            return new NeighbourhoodLayout(layout.Slot, layout.Seed, layout.GeneratorVersion,
                layout.SchemaVersion, layout.MapSize, layout.Lots, world);
        }

        /// <summary>
        /// Writes a session into a slot's sidecar, leaving the lots in it exactly as they are.
        ///
        /// <para>
        /// <b>The other half of a save, for the case a refresh cannot cover.</b> A refresh writes
        /// the layout <em>and</em> whatever session was on it when it started, which is right for
        /// entering a neighbourhood and wrong for leaving one: the clock has moved and the player
        /// is somewhere else by the time anything calls this. So the refresh preserves and this
        /// records.
        /// </para>
        ///
        /// <para>
        /// <b>It writes onto the layout that is on disk, not onto one it is handed.</b> Loading it
        /// first is what makes the call safe from any thread and at any moment in a visit: the lots
        /// in the file are whatever the last refresh left there, which is by construction the
        /// current arrangement, and a stale in-memory layout can never overwrite them. It also
        /// means there is no arrangement half to get wrong - a session is stamped, not packed.
        /// </para>
        ///
        /// <para>
        /// <b>What it does to the cache.</b> The world's session is updated too, so a caller that
        /// saves and then asks the panel for a world gets back the session it just saved rather
        /// than the one from before. The raster is not regenerated and the world is not
        /// invalidated: the lots are identical, which is the whole reason the write was safe.
        /// </para>
        ///
        /// <para>
        /// <b>With B7 off it refuses, and says so.</b> Not silently: a caller reaching for this
        /// while <see cref="NeighbourhoodWorldFlag.Persist"/> is off has a bug in it, and the one
        /// log line is what makes that visible instead of leaving a session that quietly never
        /// reached the disk.
        /// </para>
        ///
        /// <para>
        /// <b>Last writer wins, whole file.</b> The write is atomic, so a crash cannot tear it, but
        /// a refresh for the same slot that has already read the sidecar and not yet written its
        /// own layout back will overwrite this - it wrote a session it read before this one. The
        /// window is the cheap half of a refresh and the loss is one session, which the next save
        /// puts back; closing it properly means a conditional write, which belongs to
        /// <see cref="NeighbourhoodLayoutStore"/> rather than here.
        /// </para>
        /// </summary>
        /// <param name="userDir">The user directory the sidecar lives in.</param>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="world">
        /// The session to record, or <c>null</c> to forget the one in the file - which removes the
        /// member rather than writing an empty object, so the sidecar goes back to being a Part A
        /// document.
        /// </param>
        /// <returns>Whether the sidecar now says this. False for the flag being off, a slot with no
        /// sidecar yet, and any write failure - all of which are logged.</returns>
        public static bool SaveWorldState(string userDir, int slot, NeighbourhoodWorldState world)
        {
            if (!PersistsWorldState)
            {
                SimitoneLog.Warn("Neighbourhood " + slot + ": a session was not saved because "
                    + NeighbourhoodWorldFlags.Describe(NeighbourhoodWorldFlag.Persist)
                    + ". Nothing was written.");
                return false;
            }

            var cached = NeighbourhoodLayoutStore.Load(userDir, slot);
            if (cached == null)
            {
                //Nothing to stamp it onto. Writing one here would mean packing a layout, which is
                //the refresh's job and would put lots in a file on the strength of a clock.
                SimitoneLog.Warn("Neighbourhood " + slot + ": a session was not saved because the"
                    + " slot has no sidecar to save it into. Enter the neighbourhood and it will be"
                    + " saved with the layout.");
                return false;
            }

            if (!NeighbourhoodLayoutStore.Save(userDir, WithWorldState(cached, world))) return false;
            SimitoneLog.Info("Neighbourhood " + slot + ": the session was saved ("
                + (world == null ? "none - the sidecar no longer carries one" : world.ToString())
                + ").");

            //Keep the in-memory world telling the same story as the file. Same lots, so the raster
            //is still the right one and the map is not rebuilt; only the layout object the world
            //reports changes, and its lot records are the ones the world already had.
            var built = NeighbourhoodWorldCache.Peek(slot);
            if (built != null && built.HasWorld && SameLayout(built.Layout, cached))
            {
                NeighbourhoodWorldCache.Store(built.WithLayout(WithWorldState(built.Layout, world)));
            }
            return true;
        }

        /// <summary>
        /// The layout a refresh is about to write, with the session resolved.
        ///
        /// <remarks>
        /// <b>One branch, and it is the whole of B7's gate.</b> With the flag off the answer is
        /// "strip it": the patcher carries the cached session forward for its own reasons - a
        /// repaired layout should not lose the player's clock merely because it was re-placed - and
        /// that carry has to be undone here, or a Part A sidecar would grow a <c>worldState</c>
        /// member the moment a player bulldozed a house. With the flag on the answer is the first
        /// session that exists, in the order fresh layout, cached sidecar, empty default: the
        /// patcher's carry is already the cached one, a full repack has none of its own and takes
        /// the sidecar's, and a neighbourhood nobody has played in yet gets an empty session rather
        /// than a null one, so "B7 is on" always means "there is a session to read".
        ///
        /// <para>
        /// Internal so the rule can be asserted on without generating a raster, and because the
        /// other half of the invalidation story - the patch that fills <c>fresh</c> - lives in the
        /// same assembly and names this one in its own remarks.
        /// </para>
        /// </remarks>
        /// </summary>
        internal static NeighbourhoodLayout LayoutToWrite(
            NeighbourhoodLayout fresh, NeighbourhoodLayout cached)
        {
            if (!PersistsWorldState) return WithWorldState(fresh, null);
            var world = fresh?.WorldState ?? cached?.WorldState
                ?? NeighbourhoodWorldState.CreateDefault();
            return WithWorldState(fresh, world);
        }

        /// <summary>
        /// The world to hand back, carrying a session when one exists and the world does not.
        ///
        /// <remarks>
        /// The world's own session wins over the file's, deliberately: whoever simulates the world
        /// fills that state in and saves it afterwards, so between the two the in-memory copy is
        /// the newer one and replacing it with a file read would move the clock backwards. The
        /// file's session is only adopted when the world has none at all, which is the one state
        /// the two can be in without either being stale: B7 was turned on after this world was
        /// built.
        /// </remarks>
        /// </summary>
        internal static NeighbourhoodGeneratedWorld WorldWithSession(
            NeighbourhoodGeneratedWorld world, NeighbourhoodLayout from)
        {
            if (world == null || !PersistsWorldState) return world;
            if (world.Layout?.WorldState != null) return world;
            var carried = from?.WorldState;
            if (carried == null) return world;
            return world.WithLayout(WithWorldState(world.Layout, carried));
        }

        /// <summary>
        /// Whether a cached layout is the same one as a sidecar layout, field for field. Used to
        /// decide whether a raster in memory still belongs to the layout on disk.
        /// </summary>
        /// <remarks>
        /// <b>And the session is not part of the comparison.</b> Where every lot sits is what makes
        /// a raster valid; <see cref="NeighbourhoodLayout.WorldState"/> does not move a single
        /// tile, and including it would mean a save - which is the one thing a session does most -
        /// invalidated the world's raster. A layout that has just been given a session by
        /// <see cref="SaveWorldState"/> must still match the world in memory, or that call would
        /// throw away a quarter of a million tiles.
        /// </remarks>
        private static bool SameLayout(NeighbourhoodLayout a, NeighbourhoodLayout b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Slot != b.Slot || a.Seed != b.Seed || a.GeneratorVersion != b.GeneratorVersion) return false;
            if (a.MapSize != b.MapSize || a.LotCount != b.LotCount) return false;
            for (int i = 0; i < a.Lots.Count; i++)
            {
                var x = a.Lots[i];
                var y = b.Lots[i];
                if (x.Number != y.Number || x.WorldX != y.WorldX || x.WorldY != y.WorldY) return false;
                if (x.Size != y.Size || x.Zone != y.Zone || x.DoorSide != y.DoorSide) return false;
                if (x.HouseHash != y.HouseHash || x.HasHouse != y.HasHouse) return false;
                if (x.TerrainType != y.TerrainType || x.BaseHeight != y.BaseHeight) return false;
            }
            return true;
        }
    }
}
