/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using FSO.Client;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Marshals.Hollow;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework.Graphics;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Gives a neighbour sub-world its real contents when the <see cref="LotStore"/> holds no
    /// <see cref="VMHollowMarshal"/> for it: stands a throwaway <c>World</c> and <c>VM</c> up, runs
    /// the lot's own <c>HouseNN.iff</c> through
    /// <see cref="VMTS1Activator.LoadFromIff"/>, takes a <c>VM.HollowSave()</c> of the result and
    /// throws the world away again.
    ///
    /// <para><b>Why the log says "0 hollow, 4 blank" without this.</b> A neighbour's contents are
    /// hollow when <see cref="SimitoneSurroundingsPlan"/> found a marshal in the
    /// <see cref="LotStore"/>, and the store only ever holds a lot somebody has visited (§5.3). A
    /// generated neighbourhood's lots have all been visited by nobody, so every neighbour was a
    /// blank lot of the right shape at the right height - the "weird stand-in rendering". The
    /// contents are not missing: they are in each lot's own <c>HouseNN.iff</c>, and this is the
    /// pass that reads them out of it.</para>
    ///
    /// <para><b>This is the facade pipeline's sequence, unchanged.</b>
    /// <see cref="NeighbourhoodFacadeBuilder.TryBuild(GraphicsDevice, NeighbourhoodLot, NeighbourhoodMap, out string, out NeighbourhoodFacadeTimings)"/>
    /// is the only proven code in the tree that turns a TS1 house file into something the lot view
    /// can draw, and the sequence is load-bearing in ways that are easy to get subtly wrong: the
    /// world has to be added to the 3D scene layer before it has a <c>State</c>, the VM has to be
    /// initialised over it before the activator runs, the light map is built inside
    /// <c>World.InitBlueprint</c> at the tail of the load, and the throwaway VM has to be
    /// unsubscribed from the static <c>VM.OnBHAVChange</c> event before the world goes or it leaks
    /// for the life of the process. So the four of them are done here in the order that is known
    /// to work rather than in the order that reads shortest.</para>
    ///
    /// <para><b>One difference, and it is deliberate.</b> The three
    /// <c>LotFacadeGenerator</c> passes are not run: they are what rasterises walls, floor and
    /// roof, and the sub-world does its own drawing from the marshal. Everything else - the
    /// lighting budget over the load, the global 11, the activator, the teardown - is the same.</para>
    ///
    /// <para><b>Cached per lot, keyed on the house file's own hash.</b> The same rule the facade
    /// mesh cache uses (<see cref="NeighbourhoodFacadePlan.NeedsBuild"/>), for the same reason: a
    /// lot's furniture changing must not cost a re-read of its contents, and a lot whose file has
    /// changed must not be handed the contents of the file it had before. A hash of zero is not a
    /// cache key - it is what a lot with no house reports, and it is refused before any world is
    /// stood up.</para>
    ///
    /// <para><b>Budgeted, in the facade pass's shape.</b> One build is a whole <c>World</c>, a
    /// whole <c>VM</c> and a whole house-file load on the game thread, and the plan is a handful
    /// of lots at a time, so the count is bounded per pass and so is the wall clock - and the first
    /// build of a pass is unconditional, because a queue that can never advance is
    /// indistinguishable from a queue that is broken. What a lot over the budget gets is the blank
    /// lot it has today, which is why the budget degrades rather than fails.</para>
    ///
    /// <para><b>Game thread only, and it never throws.</b> Same rule as
    /// <see cref="LotStore"/> and <see cref="SimitoneSurroundingsRestore"/>: a <c>VM</c> is not
    /// thread-safe and this stands several up in a row on the thread that is bringing a lot up. No
    /// lock is added, because a lock would hide the one caller that must not be here. And every
    /// failure - a missing house file, a house file the lot view cannot be sized for, a throw
    /// inside the load - is a sentence naming the lot, and the lot is built blank. "A missing or
    /// broken neighbour IFF is skipped, not fatal" is B2's acceptance criterion, and here "skipped"
    /// means "the neighbour has no contents", which is what a blank lot is.</para>
    ///
    /// <para><b>What is deliberately not here.</b> Nothing in this type reads the generated map or
    /// the layout's terrain, and nothing here knows about sub-world placement: a neighbour's ground,
    /// height and facing are the restore's business and stay there. This type answers one
    /// question - "what is in this lot" - and returns a marshal.</para>
    ///
    /// <para><b>The second thing it answers, which is not a marshal: the lot's own <c>ARRY(0)</c>
    /// height plane.</b> A TS1 lot's ground is a graded, per-tile field and it lives in that lot's
    /// own <c>HouseNN.iff</c> and nowhere else - the generated neighbourhood throws it away, because
    /// <c>TerrainSeamSolver</c> pins every tile of a lot's footprint to that lot's <em>median</em>
    /// altitude as a boundary condition. That is a correct boundary condition and the wrong answer
    /// to "what shape is this lot's ground", which is the question a surrounding lot is drawn from,
    /// and it is why the owner reports "height maps of surrounding lots are incorrect".
    /// <see cref="TryReadArryAltitude"/> is the read, and it is deliberately the cheap half of this
    /// type: one file opened, one chunk decoded, no <c>World</c>, no <c>VM</c>, no device and no
    /// budget. <see cref="SimitoneSurroundingsPlan"/> asks it for every neighbour it plans and
    /// threads what it gets back into the sub-world's own ground.</para>
    /// </summary>
    public sealed class NeighbourHollowLoader
    {
        /// <summary>
        /// How many throwaway worlds one pass may stand up by default.
        ///
        /// <para>Four, because the plan that produces them selects a radius of
        /// <see cref="SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles"/> tiles - this street and
        /// the row behind it - and four is what that radius costs in the ordinary case. It is a
        /// budget rather than a promise: a lot that has already been built this pass is free (it
        /// comes out of the cache), and a lot that cannot be built costs nothing.</para>
        /// </summary>
        public const int DefaultMaxBuilds = 4;

        /// <summary>
        /// How long one pass may spend standing worlds up by default, in milliseconds. Zero or less
        /// means no wall-clock limit, which is the facade pass's own convention.
        ///
        /// <para>Checked before each build and not after, so no build is ever abandoned half way
        /// through: a marshal is either taken from a whole loaded lot or it was never taken.</para>
        /// </summary>
        public const int DefaultMaxMilliseconds = 2500;

        //The stages, in the order they run, so that a throw can say which one it came out of.
        //The first three are the facade builder's own names; the fourth is this pass's.
        private const string StageHouseFile = "reading the lot's house file";
        private const string StageWorld = "standing the throwaway lot world up";
        private const string StageLoad = "loading the lot's house file into the throwaway lot";
        private const string StageMarshal = "taking the lot's contents out of the throwaway lot";

        //Two dictionaries rather than one dictionary of entries, because that is the shape the
        //facade mesh cache already uses (`NeighbourhoodFacadeMeshCache` keeps its hashes beside
        //its meshes) and because it is what makes the decision half of the cache - which lot needs
        //a build, and does this hash match - a pure function over two `IReadOnlyDictionary`s that
        //a test can call with dictionaries it built itself.
        private readonly Dictionary<short, uint> _hashes = new Dictionary<short, uint>();
        private readonly Dictionary<short, VMHollowMarshal> _marshals =
            new Dictionary<short, VMHollowMarshal>();
        private readonly HashSet<short> _failedLots = new HashSet<short>();

        //Runs across a pass rather than across the loader's life: the budget is "what this lot
        //load may spend", and a loader a caller keeps for the whole session would otherwise stop
        //answering True after one pass's worth of milliseconds and silently blank every lot after
        //the first. BeginPass restarts both counters and this clock.
        private readonly Stopwatch _clock = new Stopwatch();
        private int _attempts;
        private int _built;
        private int _cached;
        private int _blank;

        /// <summary>
        /// A loader that builds at most <paramref name="maxBuilds"/> lots per pass, with no
        /// wall-clock limit.
        /// </summary>
        /// <param name="device">
        /// The lot view's graphics device - the sub-world's own, not a separate one. A loader with
        /// no device refuses every build with a sentence saying so, and refuses them without
        /// touching anything else.
        /// </param>
        /// <param name="maxBuilds">How many lots a pass may stand a world up for.</param>
        public NeighbourHollowLoader(GraphicsDevice device, int maxBuilds = DefaultMaxBuilds)
            : this(device, maxBuilds, 0d)
        {
        }

        /// <summary>A loader with both budgets stated.</summary>
        /// <param name="device">The lot view's graphics device, or <c>null</c>.</param>
        /// <param name="maxBuilds">How many lots a pass may stand a world up for.</param>
        /// <param name="maxMilliseconds">
        /// How long a pass may spend building, in milliseconds; zero or less for no limit.
        /// </param>
        public NeighbourHollowLoader(GraphicsDevice device, int maxBuilds, double maxMilliseconds)
        {
            Device = device;
            MaxBuilds = Math.Max(0, maxBuilds);
            MaxMilliseconds = maxMilliseconds;
            BeginPass();
        }

        /// <summary>The device a build runs on. Never asked again after construction.</summary>
        public GraphicsDevice Device { get; }

        /// <summary>How many lots a pass may stand a throwaway world up for.</summary>
        public int MaxBuilds { get; set; }

        /// <summary>How long a pass may spend building, in milliseconds; zero or less for none.</summary>
        public double MaxMilliseconds { get; set; }

        /// <summary>Builds this pass has started, successful or not. The budget is counted in these.</summary>
        public int Attempts
        {
            get { return _attempts; }
        }

        /// <summary>Builds this pass that produced a marshal.</summary>
        public int BuiltCount
        {
            get { return _built; }
        }

        /// <summary>Lots this pass answered out of the cache rather than by building.</summary>
        public int CachedCount
        {
            get { return _cached; }
        }

        /// <summary>Lots this pass refused to build contents for, and that are therefore blank.</summary>
        public int BlankCount
        {
            get { return _blank; }
        }

        /// <summary>The hash each cached marshal was built from, for the cache's own key.</summary>
        public IReadOnlyDictionary<short, uint> CachedHashes
        {
            get { return _hashes; }
        }

        /// <summary>How many lots are cached. Cleared by <see cref="Clear"/>.</summary>
        public int CachedLots
        {
            get { return _marshals.Count; }
        }

        /// <summary>
        /// Restarts the pass: the build counters and the wall clock. The cache is not touched,
        /// because a pass is about what this load may spend rather than about what it knows.
        /// </summary>
        public void BeginPass()
        {
            _attempts = 0;
            _built = 0;
            _cached = 0;
            _blank = 0;
            _clock.Restart();
        }

        /// <summary>Forgets every cached marshal. What leaving a neighbourhood calls.</summary>
        public void Clear()
        {
            _hashes.Clear();
            _marshals.Clear();
            _failedLots.Clear();
        }

        /// <summary>
        /// Whether a build for this lot was started and produced nothing. Unlike a lot refused
        /// on the budget, such a lot would fail the same way again, so nothing should ask twice.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        public bool HasFailed(short lot)
        {
            return _failedLots.Contains(lot);
        }

        // ==================================================================== the decisions

        /// <summary>
        /// Why this lot cannot have its contents read out of a house file at all, or
        /// <c>null</c> when it is worth standing a world up for.
        /// </summary>
        /// <remarks>
        /// <para>Two refusals, and both of them are free - a field read on a layout record rather
        /// than anything that costs a second - so both run before the device, the content manager
        /// and the house file are touched at all.</para>
        /// <para><b>A hash of zero is a refusal rather than a build.</b> It is what a lot with no
        /// house file reports, and it is what a hand-written layout leaves behind for a lot it
        /// cannot fingerprint. Either way there is nothing to stand a world up for, and worse,
        /// nothing to key a cache entry on: caching the marshal of a file we cannot fingerprint
        /// would serve it for the rest of the session even after the file appeared.</para>
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static string BuildRefusalFor(NeighbourhoodLot lot)
        {
            if (lot == null)
            {
                return "there is no lot to read contents out of";
            }
            if (!lot.HasHouse)
            {
                return "lot " + lot.Number + ": the lot has no house file, so it has no contents to"
                    + " read - it is an empty plot";
            }
            if (lot.HouseHash == 0u)
            {
                return "lot " + lot.Number + ": the layout has no house-file fingerprint for the lot"
                    + " (a hash of zero), so its contents cannot be read or cached - it is an empty"
                    + " plot";
            }
            return null;
        }

        /// <summary>
        /// Whether this lot's contents have to be read out of its house file, given what is already
        /// cached. The cache's whole policy, as one comparison.
        /// </summary>
        /// <remarks>
        /// The facade mesh cache's rule
        /// (<see cref="NeighbourhoodFacadePlan.NeedsBuild"/>), with this type's own two refusals
        /// in front of it. The key is the house file's fingerprint and not the lot number: a lot's
        /// furniture changing must cost a re-read of its contents, and a file that changed must not
        /// be answered with the contents of the file it had before.
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="cachedHashes">The hash each cached marshal was built from.</param>
        public static bool NeedsBuild(
            NeighbourhoodLot lot, IReadOnlyDictionary<short, uint> cachedHashes)
        {
            if (BuildRefusalFor(lot) != null) return false;
            if (cachedHashes == null) return true;
            uint built;
            if (!cachedHashes.TryGetValue(lot.Number, out built)) return true;
            return built != lot.HouseHash;
        }

        /// <summary>
        /// This lot's cached marshal, but only if it was built from the house file the lot has now.
        /// </summary>
        /// <remarks>
        /// A hash that matches with no marshal behind it is a miss, not a hit: the two are written
        /// together by <see cref="Remember"/> and read together here, and a hit that handed back
        /// <c>null</c> would be a lot that "succeeded" and then built blank.
        /// </remarks>
        /// <param name="lot">The lot number.</param>
        /// <param name="houseHash">The lot's current <see cref="NeighbourhoodLot.HouseHash"/>.</param>
        /// <param name="cachedHashes">The hash each cached marshal was built from.</param>
        /// <param name="cachedMarshals">The marshals themselves.</param>
        /// <param name="marshal">The cached marshal, when there is a hit.</param>
        public static bool TryGetCached(short lot, uint houseHash,
            IReadOnlyDictionary<short, uint> cachedHashes,
            IReadOnlyDictionary<short, VMHollowMarshal> cachedMarshals,
            out VMHollowMarshal marshal)
        {
            marshal = null;
            if (cachedHashes == null || cachedMarshals == null) return false;
            uint built;
            if (!cachedHashes.TryGetValue(lot, out built)) return false;
            if (built != houseHash) return false;
            return cachedMarshals.TryGetValue(lot, out marshal) && marshal != null;
        }

        /// <summary>
        /// Whether a pass may start one more build, given its count budget, its wall-clock budget,
        /// and what it has already spent.
        /// </summary>
        /// <remarks>
        /// <para>Both budgets, and the facade pass's
        /// <see cref="NeighbourhoodFacadePlan.FrameAllowance"/> rule underneath them: the count
        /// alone is not enough because a build's cost is the lot's, not ours, and the clock is
        /// checked <em>before</em> the build so that no build is abandoned half way.</para>
        /// <para><b>The first build of a pass is unconditional</b>, even if the clock says the
        /// budget is already spent. That is the rule the facade pass learned from a queue that
        /// could never advance, and a pass whose first lot is refused on a clock that started
        /// before the pass did would have exactly that shape.</para>
        /// </remarks>
        /// <param name="maxBuilds">How many builds a pass may start.</param>
        /// <param name="buildsSoFar">How many it has started.</param>
        /// <param name="msBudget">How long a pass may spend; zero or less for no limit.</param>
        /// <param name="elapsedMs">How long the pass has already spent.</param>
        public static bool AllowsBuild(int maxBuilds, int buildsSoFar, double msBudget, double elapsedMs)
        {
            if (Math.Max(0, maxBuilds) - Math.Max(0, buildsSoFar) <= 0) return false;
            if (msBudget <= 0d) return true;
            if (buildsSoFar > 0 && elapsedMs >= msBudget) return false;
            return true;
        }

        /// <summary>
        /// Whether a freshly built marshal may be written into the lot store, which is the only
        /// reason the next lot load over the same lots is free.
        /// </summary>
        /// <remarks>
        /// <para>Only a lot the store holds nothing for. <see cref="LotStore"/> is a three-way
        /// union per lot and every writer clears the other two payloads, so writing over an
        /// <see cref="NeighbourhoodLotState.Active"/> entry would throw away the resident marshal
        /// of the lot the player is standing in. That lot is never a neighbour - the plan excludes
        /// it - but this is asked of a store rather than of a plan, so it asks the store.</para>
        /// <para>A <c>null</c> store is not something to publish into, and answers false rather
        /// than throwing: a caller with no world attached has nothing to publish to and no reason to
        /// find out here.</para>
        /// </remarks>
        /// <param name="store">The store, or <c>null</c>.</param>
        /// <param name="lot">The lot number.</param>
        public static bool ShouldPublish(LotStore store, short lot)
        {
            if (store == null) return false;
            return store.StateOf(lot) == NeighbourhoodLotState.Unloaded;
        }

        // ==================================================== the lot's own ARRY(0) height plane

        /// <summary>
        /// Why this lot's own <c>ARRY(0)</c> height plane cannot be read - with the answer the
        /// neighbourhood falls back to named in the sentence - or <c>null</c> when it can.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same two refusals as <see cref="BuildRefusalFor"/>, because they are the same two
        /// facts about the same file: a lot with no house file has no plane, and a lot whose file
        /// cannot be fingerprinted has a plane nobody may cache. What differs is the sentence and
        /// the consequence. <see cref="BuildRefusalFor"/> says the lot is an empty plot; this one
        /// says its <em>ground</em> is the generated map's flat median over its whole rectangle,
        /// which is what a lot with a house file the reader could not open looks like too - and
        /// naming the fallback here is what lets the plan hand the reason straight to a log line
        /// instead of dropping it.
        /// </para>
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static string ArryRefusalFor(NeighbourhoodLot lot)
        {
            if (lot == null)
            {
                return "there is no lot to read an ARRY(0) plane out of, so its ground is the"
                    + " generated map's flat median";
            }
            if (!lot.HasHouse)
            {
                return "lot " + lot.Number + ": the lot has no house file, so it has no ARRY(0) plane"
                    + " and its ground is the generated map's flat median";
            }
            if (lot.HouseHash == 0u)
            {
                return "lot " + lot.Number + ": the layout has no house-file fingerprint for the lot"
                    + " (a hash of zero), so its ARRY(0) plane can be neither read nor cached and its"
                    + " ground is the generated map's flat median";
            }
            return null;
        }

        /// <summary>
        /// A lot's own <c>ARRY(0)</c> plane out of the transposed chunk bytes, or
        /// <see cref="SimitoneArryAltitude.None"/> when those bytes are not a square height field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Four bytes per tile with the altitude in the second - the layout of the chunk, decoded by
        /// <c>VMTS1Activator.DecodeHeights</c> so that the plane this hands out and the plane the
        /// lot view loads are the same numbers read by the same function. The side is the chunk's
        /// own <c>Width</c>, which is the stride the transposed bytes are laid out at.
        /// </para>
        /// <para>
        /// <b>Square or nothing.</b> <see cref="SimitoneArryAltitude"/> indexes
        /// <c>z * side + x</c>, so a plane that is not square cannot be a height field over a lot -
        /// and a lot's rectangle is square by construction - and answering <c>None</c> puts the
        /// caller on the map's flat median rather than on a mis-indexed guess.
        /// </para>
        /// </remarks>
        /// <param name="transposed">The transposed <c>ARRY(0)</c> chunk bytes.</param>
        /// <param name="width">The chunk's own width, in tiles.</param>
        /// <param name="height">The chunk's own height, in tiles.</param>
        public static SimitoneArryAltitude ArryAltitudeFor(
            byte[] transposed, int width, int height)
        {
            if (transposed == null || width <= 0 || width != height) return SimitoneArryAltitude.None;
            return new SimitoneArryAltitude(VMTS1Activator.DecodeHeights(transposed), width);
        }

        /// <summary>
        /// A lot's own <c>ARRY(0)</c> plane out of its own house file, or <c>false</c> with the
        /// reason and <see cref="SimitoneArryAltitude.None"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The read the neighbourhood's surroundings have been waiting for, and the reason it is
        /// here rather than in the plan: the plan is a value, and a value that read files would stop
        /// being assertable with no install present. The plane travels as a value - see
        /// <see cref="SimitoneArryAltitude"/> - and this is the one place that makes one.
        /// </para>
        /// <para>
        /// <b>Nothing here stands a world up.</b> It is <c>provider.Neighborhood.GetHouse</c> - one
        /// file opened and parsed - and one chunk read out of it, with no <c>World</c>, no
        /// <c>VM</c>, no graphics device and no lighting budget. That is why it is affordable for
        /// every neighbour on every lot load and why the build budget that governs
        /// <see cref="TryLoad"/> does not govern it: the budget exists for the things that cost a
        /// lot view, and this is not one of them.
        /// </para>
        /// <para>
        /// <b>It never throws.</b> A missing content manager, a missing file, a file that will not
        /// open and a file whose <c>ARRY(0)</c> is not a square height field are four sentences,
        /// and each of them names the lot and the fallback.
        /// </para>
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="arry">The lot's own plane, or <c>None</c>.</param>
        /// <param name="reason">Why there is no plane, when there is not one.</param>
        public static bool TryReadArryAltitude(
            NeighbourhoodLot lot, out SimitoneArryAltitude arry, out string reason)
        {
            arry = SimitoneArryAltitude.None;
            var refusal = ArryRefusalFor(lot);
            if (refusal != null)
            {
                reason = refusal;
                return false;
            }

            var provider = FSO.Content.Content.Get();
            if (provider == null || provider.Neighborhood == null)
            {
                reason = "lot " + lot.Number + ": the game has no neighbourhood content loaded, so"
                    + " its house file cannot be opened and its ground is the generated map's flat"
                    + " median";
                return false;
            }

            IffFile house = null;
            try
            {
                house = provider.Neighborhood.GetHouse(lot.Number);
            }
            catch (Exception ex)
            {
                reason = "lot " + lot.Number + ": its house file could not be read (" + ex.Message
                    + "), so its ground is the generated map's flat median";
                return false;
            }
            if (house == null)
            {
                reason = "lot " + lot.Number + ": the neighbourhood has no house file for it, so its"
                    + " ground is the generated map's flat median";
                return false;
            }
            return TryDecodeArryAltitude(house, lot.Number, out arry, out reason);
        }

        /// <summary>
        /// A lot's own <c>ARRY(0)</c> plane out of an already-open house file, or <c>false</c> with
        /// the reason and <see cref="SimitoneArryAltitude.None"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Split from <see cref="TryReadArryAltitude"/> so that a caller holding an
        /// <see cref="IffFile"/> - the facade pass, the lot view's own load - can take the plane out
        /// of a file it has already paid for, and so that the decode is reachable with no content
        /// manager present at all.
        /// </para>
        /// <para>
        /// <b>The chunk's own type is checked before its data is transposed.</b>
        /// <c>ARRY.TransposeData</c> walks its array <c>i += (int)Type</c>, so a chunk that
        /// declares any stride but <c>RLEAlt</c>'s four - or none at all - is either mis-indexed or
        /// is an infinite loop. A house file is untrusted input (see
        /// <c>VMTS1LotLoadGuard</c>) and a hang on the lot-load thread is a far worse answer than a
        /// refused plane, so the type is a gate rather than an assumption. Everything after it is
        /// still wrapped, because <c>Data</c> itself is whatever the file said it was.
        /// </para>
        /// </remarks>
        /// <param name="house">The house file, or <c>null</c>.</param>
        /// <param name="lotNumber">The lot number, for the refusal sentences.</param>
        /// <param name="arry">The lot's own plane, or <c>None</c>.</param>
        /// <param name="reason">Why there is no plane, when there is not one.</param>
        public static bool TryDecodeArryAltitude(
            IffFile house, short lotNumber, out SimitoneArryAltitude arry, out string reason)
        {
            arry = SimitoneArryAltitude.None;
            if (house == null)
            {
                reason = "lot " + lotNumber + ": there is no house file to read an ARRY(0) plane out"
                    + " of, so its ground is the generated map's flat median";
                return false;
            }
            try
            {
                var plane = house.Get<ARRY>(0);
                if (plane == null)
                {
                    reason = "lot " + lotNumber + ": its house file carries no ARRY(0) chunk, so its"
                        + " ground is the generated map's flat median";
                    return false;
                }
                if (plane.Type != ARRYType.RLEAlt)
                {
                    reason = "lot " + lotNumber + ": its ARRY(0) chunk declares stride "
                        + (int)plane.Type + " rather than the four-byte altitude records a height"
                        + " plane is, so its ground is the generated map's flat median";
                    return false;
                }
                arry = ArryAltitudeFor(plane.TransposeData, plane.Width, plane.Height);
            }
            catch (Exception ex)
            {
                //an IFF chunk written by a third-party editor is not a shape this file may assume,
                //and a plane is not worth taking a whole lot load down for
                arry = SimitoneArryAltitude.None;
                reason = "lot " + lotNumber + ": its ARRY(0) plane could not be decoded ("
                    + ex.Message + "), so its ground is the generated map's flat median";
                return false;
            }

            if (!arry.IsValid)
            {
                reason = "lot " + lotNumber + ": its ARRY(0) plane is not a square height field, so"
                    + " its ground is the generated map's flat median";
                return false;
            }
            reason = null;
            return true;
        }

        // ==================================================================== the cache

        /// <summary>This loader's own view of the cache.</summary>
        /// <param name="lot">The lot number.</param>
        /// <param name="houseHash">The lot's current <see cref="NeighbourhoodLot.HouseHash"/>.</param>
        /// <param name="marshal">The cached marshal, when it was built from that file.</param>
        public bool TryGetCached(short lot, uint houseHash, out VMHollowMarshal marshal)
        {
            return TryGetCached(lot, houseHash, _hashes, _marshals, out marshal);
        }

        /// <summary>
        /// Records a marshal against the house file it came from.
        /// </summary>
        /// <remarks>
        /// Public because it is the one half of the cache that is not the loader, and both halves
        /// have to be exercisable without a graphics device: a test that seeds a cache can then
        /// prove a hit answers without touching the device, and a caller that reads its contents
        /// from somewhere else can hand them over rather than building them again.
        /// </remarks>
        /// <param name="lot">The lot number.</param>
        /// <param name="houseHash">The hash the marshal was built from.</param>
        /// <param name="marshal">The marshal, or <c>null</c> to forget the lot.</param>
        public void Remember(short lot, uint houseHash, VMHollowMarshal marshal)
        {
            if (marshal == null)
            {
                _hashes.Remove(lot);
                _marshals.Remove(lot);
                return;
            }
            _hashes[lot] = houseHash;
            _marshals[lot] = marshal;
        }

        /// <summary>
        /// Hands a marshal to the lot store, if the store is holding nothing for that lot. See
        /// <see cref="ShouldPublish"/> for why "holding nothing" is the condition.
        /// </summary>
        /// <param name="store">The store, or <c>null</c>.</param>
        /// <param name="lot">The lot number.</param>
        /// <param name="marshal">The marshal, or <c>null</c>.</param>
        /// <returns>Whether it was written.</returns>
        public bool Publish(LotStore store, short lot, VMHollowMarshal marshal)
        {
            if (marshal == null) return false;
            if (!ShouldPublish(store, lot)) return false;
            store.SetHollow(lot, marshal);
            return true;
        }

        // ==================================================================== the build

        /// <summary>Whether this pass may start one more build right now.</summary>
        public bool AllowsBuild()
        {
            return AllowsBuild(MaxBuilds, _attempts, MaxMilliseconds, _clock.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// This lot's contents as a <see cref="VMHollowMarshal"/>, out of the cache where it can be
        /// and out of the lot's own house file where it cannot.
        /// </summary>
        /// <remarks>
        /// <para><b>The order of these four checks is the order of what they cost.</b> The lot's own
        /// refusals are two field reads and run first; the cache is a dictionary lookup and runs
        /// next, which is why a lot built earlier in the same session costs nothing and is
        /// answered even after the budget is spent; the budget is asked third; and only then is a
        /// world stood up. A lot that cannot be built therefore costs a sentence and no device
        /// time, which is the difference between a neighbourhood with one unreadable house file and
        /// a neighbourhood that stalls on it.</para>
        /// <para><b>Every refusal is a blank lot and a sentence.</b> Nothing here throws and nothing
        /// here returns an empty marshal: the caller builds the lot blank, exactly as it did before
        /// this type existed, and the log says which lot and why.</para>
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="marshal">The lot's contents, when there are any.</param>
        /// <param name="reason">Why there are none, when there are none.</param>
        public bool TryLoad(
            NeighbourhoodLot lot, out VMHollowMarshal marshal, out string reason)
        {
            marshal = null;
            reason = null;

            var refusal = BuildRefusalFor(lot);
            if (refusal != null)
            {
                _blank++;
                reason = refusal;
                return false;
            }

            //The cache before the budget, deliberately: a lot whose contents are already in
            //memory costs nothing, and refusing it because this pass has spent its budget would
            //blank a lot we can answer for free.
            if (TryGetCached(lot.Number, lot.HouseHash, out marshal))
            {
                _cached++;
                return true;
            }

            if (!AllowsBuild())
            {
                _blank++;
                //The clause that matters is "built blank": a neighbour with no contents is an
                //empty lot, and the log is the only place that says so. The elapsed time is in
                //here as well because a budget spent on time and a budget spent on count are two
                //different things to fix.
                reason = "lot " + lot.Number + ": the budget of " + MaxBuilds
                    + " hollow build" + (MaxBuilds == 1 ? "" : "s") + " per lot load is spent"
                    + (MaxMilliseconds > 0d
                        ? " after " + ElapsedText(_clock.Elapsed.TotalMilliseconds)
                            + " of a " + ElapsedText(MaxMilliseconds) + " allowance"
                        : string.Empty)
                    + ", so this lot is built blank - its real contents are read the next time a lot"
                    + " load has budget for them";
                return false;
            }

            _attempts++;
            string failure;
            marshal = Build(lot, out failure);
            if (marshal == null)
            {
                _blank++;
                //remembered, so a later-frame top-up can tell "this lot cannot be read" from "this
                //lot was only past the budget" and does not stand a world up for it again
                _failedLots.Add(lot.Number);
                reason = failure;
                return false;
            }
            _failedLots.Remove(lot.Number);
            _built++;
            Remember(lot.Number, lot.HouseHash, marshal);
            return true;
        }

        /// <summary>
        /// The facade pipeline's sequence, with the three generator passes left out, plus the
        /// <c>HollowSave</c> at the end and a teardown that runs whatever happened.
        /// </summary>
        private VMHollowMarshal Build(NeighbourhoodLot lot, out string failure)
        {
            failure = null;
            var stage = StageHouseFile;
            World world = null;
            VM vm = null;
            try
            {
                var provider = FSO.Content.Content.Get();
                IffFile house = null;
                string houseReadFailure = null;
                if (provider != null && provider.Neighborhood != null)
                {
                    try
                    {
                        house = provider.Neighborhood.GetHouse(lot.Number);
                    }
                    catch (Exception ex)
                    {
                        //a named failure rather than a bare one: which file, and why it would not
                        //open. GetHouse is a `new IffFile(path)` over the lot's own file, so this is
                        //the "no such file" and the truncated-file case both.
                        houseReadFailure = "the house file could not be read: " + ex.Message;
                    }
                }

                //Free, and before the world exists: the same pre-flight the facade pass runs, in
                //the same order. A house file whose SIMI names a size the lot view cannot be sized
                //for is refused here rather than dying as a D3D error seconds later, after a world
                //and a VM have been paid for.
                var preflight = NeighbourhoodFacadeBuilder.PreFlight(
                    Device, GameFacade.Scenes, provider, house, lot.Number);
                if (preflight != null)
                {
                    failure = preflight;
                    return null;
                }
                if (houseReadFailure != null)
                {
                    //everything above passed, so the provider is there and the file is a house
                    //file; the file itself would not open
                    failure = "lot " + lot.Number + ": " + houseReadFailure;
                    return null;
                }

                //The light map is built inside World.InitBlueprint, which runs at the tail of the
                //load, so the setting has to be lowered across this window and nowhere else - and
                //put back before HollowSave, which reads no lighting at all.
                using (new LightingBudget(Device, NeighbourhoodFacadeBuilder.LotSizeFor(house)))
                {
                    stage = StageWorld;
                    world = new World(Device);
                    world.Opacity = 1f;
                    //Add initialises the world, and only initialises it when the scene layer has a
                    //device. A world with no State is a world whose LoadFromIff dies on its third
                    //line, so the state is checked here rather than discovered there.
                    GameFacade.Scenes.Add(world);
                    if (world.State == null)
                    {
                        failure = "lot " + lot.Number + ": the throwaway lot world has no state, so"
                            + " there is no lot view to load its house file into";
                        return null;
                    }

                    var vmts1link = new VMTS1GlobalLinkStub();
                    var driver = new VMServerDriver(vmts1link);
                    vm = new VM(new VMContext(world), driver, new VMNullHeadlineProvider());
                    vm.ListenBHAVChanges();
                    vm.Init();
                    vm.SetGlobalValue(11, (short)lot.Number);

                    stage = StageLoad;
                    var activator = new VMTS1Activator(vm, world, (short)lot.Number);
                    var blueprint = activator.LoadFromIff(house);
                    if (blueprint == null)
                    {
                        failure = "lot " + lot.Number + ": the lot's blueprint could not be built"
                            + " from its house file";
                        return null;
                    }
                }

                //The whole of what this pass exists for. The VM is still TS1 - nothing here touches
                //`TS1`, unlike the restore's temporary VM, which sets it false so that the TSO
                // helpers can read `vm.TSOState` - so the entities were marshalled as the TS1
                // objects they are, which is what `HollowLoad` on the other side expects.
                stage = StageMarshal;
                return vm.HollowSave();
            }
            catch (Exception ex)
            {
                failure = NeighbourhoodFacadeDiagnostics.StageFailure(lot.Number, stage, ex);
                return null;
            }
            finally
            {
                //Before the world goes, and not with it: the throwaway VM subscribed itself to the
                //static VM.OnBHAVChange event, and unsubscribing is the only thing that lets go of
                //it. A disposed world does not get it back, so every lot this pass failed on would
                //leave a whole throwaway lot alive for the life of the process. See
                //VM.ListenBHAVChanges, which is explicit that the pair is meant to be balanced.
                try
                {
                    if (vm != null) vm.SuppressBHAVChanges();
                }
                catch (Exception)
                {
                    //an event this VM is not subscribed to is not a failure worth reporting
                }
                //and whatever happened, the throwaway world must not stay in the scene graph, or
                //the game starts simulating an empty lot behind the UI
                if (world != null)
                {
                    try
                    {
                        if (GameFacade.Scenes != null) GameFacade.Scenes.Remove(world);
                        world.Dispose();
                    }
                    catch (Exception)
                    {
                        //a world that will not dispose is a leak, not a crash
                    }
                }
            }
        }

        /// <summary>
        /// The lot view's light map, sized to what this device can hold, for the length of the
        /// throwaway world and its house-file load - and the player's own setting afterwards.
        /// </summary>
        /// <remarks>
        /// The facade pass's own budget, for the same reason and with the same arithmetic:
        /// <c>LMapBatch.Init</c> sizes its render targets as <c>3 * shadow-detail * (size - 1)</c>
        /// pixels across without asking the device, and both factors are outside the house file -
        /// so on a machine that caps render targets at 4096 a 60 tile lot is refused with
        /// <c>E_INVALIDARG</c> unless the setting is brought down first. A using block rather than a
        /// pair of assignments, because a throw from inside the load has to put it back.
        /// </remarks>
        private sealed class LightingBudget : IDisposable
        {
            private readonly int _previous;
            private readonly bool _lowered;

            public LightingBudget(GraphicsDevice device, int lotSize)
            {
                var config = WorldConfig.Current;
                _previous = config.ShadowDetail;
                _lowered = false;
                //no device is the pre-flight's business, and a size outside the range was already
                //refused before this was constructed
                if (device == null || !NeighbourhoodFacadeBuilder.LotSizeIsUsable(lotSize)) return;
                var detail = NeighbourhoodFacadeBuilder.ShadowDetailFor(lotSize, config.ShadowDetail,
                    config.UltraLighting, config.Directional,
                    NeighbourhoodFacadeBuilder.DeviceMaxTarget(device));
                //zero means the pre-flight should have refused the lot, and a value the setting
                //already holds is not ours to write
                if (detail <= 0 || detail == config.ShadowDetail) return;
                config.ShadowDetail = detail;
                _lowered = true;
            }

            public void Dispose()
            {
                if (_lowered) WorldConfig.Current.ShadowDetail = _previous;
            }
        }

        /// <summary>
        /// One line for the log: what this pass spent and what it answered, so that a
        /// neighbourhood of blank neighbours is distinguishable from one whose neighbours were
        /// built and whose facades were withheld.
        /// </summary>
        public string Describe()
        {
            return BuiltCount + " neighbour lot" + (BuiltCount == 1 ? "" : "s")
                + " loaded from " + CachedLots + " cached house file"
                + (CachedLots == 1 ? "" : "s") + " (" + CachedCount + " cache hit"
                + (CachedCount == 1 ? "" : "s") + ", " + BlankCount + " blank), "
                + (AllowsBuild() ? "budget remaining" : "build budget spent");
        }

        /// <summary>Milliseconds as a whole number of milliseconds, for a log line.</summary>
        private static string ElapsedText(double milliseconds)
        {
            return ((long)Math.Round(milliseconds, MidpointRounding.AwayFromZero))
                .ToString(CultureInfo.InvariantCulture) + "ms";
        }

        public override string ToString()
        {
            return Describe();
        }
    }
}