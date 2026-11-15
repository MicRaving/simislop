/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using FSO.SimAntics.Marshals;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Why a lot is being switched to. The callers named in the Part B handoff (§B3 task 1, and
    /// §B4 task 4) all end up on the one switch path, and the reason is carried through so the
    /// world can record which of them it was - and so a log line can say so without a second
    /// source.
    /// </summary>
    public enum TransitionReason
    {
        /// <summary>
        /// The lot's own <c>VM</c> asked for the switch: the controlled sim walked onto a
        /// neighbour's door, took a bus, entered downtown. The in-VM request path
        /// (<c>VMLotSwitch</c>, §11 item 5) which only records the request and lets the frame
        /// <c>Update</c> act on it.
        /// </summary>
        VmRequest = 0,

        /// <summary>
        /// The lot picker: the player chose a house from the neighbourhood view and the world
        /// loaded it, with no sim having asked to travel there.
        /// </summary>
        Picker = 1,

        /// <summary>
        /// A family is moving house - the lot being left is a home lot and the one being entered
        /// is the family's new one. Separate from <see cref="Picker"/> because it is the only one
        /// of the three that changes family authority (§5.5) rather than only which lot is live.
        /// </summary>
        MoveIn = 2,

    }

    /// <summary>
    /// One lot switch, as data: which lot and why.
    /// </summary>
    /// <remarks>
    /// A struct rather than two method arguments because the switch is asked for from two call
    /// sites that both want to log it. It is readonly because a request is a statement of intent
    /// made once, and a half-edited one that disagreed with the world would be indistinguishable
    /// from a bug in the world.
    /// </remarks>
    public readonly struct LotTransitionRequest
    {
        /// <summary>
        /// The lot number to load, in the same numbering as
        /// <see cref="ILotEntrance.LotNumber"/> - never 0, never 99. See
        /// <see cref="LotTravelService.IsLotNumber"/> for the range and why it is checked before
        /// anything is torn down.
        /// </summary>
        public int HouseId { get; }

        /// <summary>Which of the three callers is asking.</summary>
        public TransitionReason Reason { get; }

        /// <summary>A request to load a lot.</summary>
        /// <param name="houseId">The lot to load.</param>
        /// <param name="reason">Which caller is asking.</param>
        public LotTransitionRequest(int houseId, TransitionReason reason)
        {
            HouseId = houseId;
            Reason = reason;
        }

        /// <summary>One line, in the wording the reason strings below use.</summary>
        public override string ToString()
        {
            return HouseId + " via " + LotTravelService.ReasonName(Reason);
        }
    }

    /// <summary>
    /// Everything <see cref="LotTravelService"/> needs from the screen that owns the live
    /// <c>VM</c>, and nothing it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a seam at all.</b> The switch this type owns is today a block inside
    /// <c>TS1GameScreen.Update</c>, sitting between <c>vm.Save()</c> and
    /// <c>InitializeLot</c> and therefore unrunnable without a <c>VM</c>, a content manager and a
    /// graphics device. B3 needs the ordering in that block to be testable, because the two
    /// properties that make travel reversible - stash exactly once, resume memory before disk -
    /// are only observable in the order the calls happen in. Pulling the decision out behind
    /// this interface leaves the screen holding what it always held (the VM, the store, the
    /// content) and gives the ordering somewhere it can be asserted without any of it.
    /// </para>
    /// <para>
    /// <b>It is deliberately thin, and deliberately has no <c>VM</c> on it.</b> Every member is
    /// a decision the screen has to make anyway; nothing here can be answered by a service that
    /// is not the screen. In particular there is no "teardown" member: the screen's own teardown
    /// happens inside <see cref="LoadLot(VMMarshal, int)"/>, which is why the stash below has to
    /// come first and why the service cannot be reordered to suit itself.
    /// </para>
    /// <para>
    /// <b>Thread affinity: the game thread, only.</b> Same rule as <see cref="LotStore"/> and the
    /// rest of the world (§7): the VM is not thread-safe and a lot is switched once per frame from
    /// the thread that owns it.
    /// </para>
    /// </remarks>
    public interface ILotTravelHost
    {
        /// <summary>
        /// The lot the player is standing on, as the screen reads it out of the VM's global 10.
        /// 0 when no lot is loaded, which is not a lot number and is why the service refuses to
        /// stash it.
        /// </summary>
        int CurrentHouseId { get; }

        /// <summary>
        /// The active family's own house number, or null when there is no family (a simless lot
        /// load). Needed to compute whether the destination is "downtown" - see
        /// <see cref="LotTravelService.IsDowntownLot"/> for why that is a decision and not a
        /// lookup.
        /// </summary>
        int? ActiveFamilyHouseNumber { get; }

        /// <summary>
        /// The path to a lot's own house file, i.e. the screen's
        /// <c>Content.Get().Neighborhood.GetHousePath</c>. On the seam because the root of a
        /// neighbourhood is content, and a service that built the path itself would need the
        /// content manager - which is exactly the dependency that makes a switch untestable.
        /// </summary>
        /// <param name="houseId">The lot number.</param>
        /// <returns>The path, or null/empty when the lot has no house file on disk.</returns>
        string GetHousePath(int houseId);

        /// <summary>
        /// Marshal the live lot out and keep it in the per-lot store, keyed by
        /// <see cref="CurrentHouseId"/>. Returns the marshal for the host's own bookkeeping; the
        /// service does not use it, because the lot being loaded is a different lot's state (and
        /// the same lot is never stashed at all).
        /// </summary>
        VMMarshal SaveCurrentLot();

        /// <summary>
        /// Whether the per-lot store holds state for a lot, and that state. This is the in-memory
        /// session-stash lookup - the source that outranks the on-disk travel slot, because
        /// travel save persistence (§5.6) and the screen's own
        /// <c>InitializeLot(path, false)</c> both fall back to it rather than overriding it.
        /// </summary>
        /// <param name="lot">The lot number, as a <c>short</c> because the store is keyed by one.</param>
        /// <param name="marshal">The held state, when there is one. Must not be null when it
        /// returns true; a null with true is treated as "nothing held" by the service, because a
        /// null marshal handed to a VM load fails deep inside the lot with no useful cause.</param>
        bool TryRestoreSaved(short lot, out VMMarshal marshal);

        /// <summary>Drop a lot's entry from the store - consume it, exactly once.</summary>
        /// <param name="lot">The lot number.</param>
        void RemoveSaved(short lot);

        /// <summary>
        /// Record whether the lot now loading is a travel lot rather than a home lot. The screen
        /// reads this to decide family selection and downtown time, so it must be set before the
        /// load and not after.
        /// </summary>
        /// <param name="downtown">Whether the destination is downtown.</param>
        void SetDowntown(bool downtown);

        /// <summary>Load a lot from its house file, or from a travel slot, by path.</summary>
        /// <param name="path">The lot's own path, as <see cref="GetHousePath"/> returned it.</param>
        /// <param name="houseId">The lot number, which the generated surroundings need and which
        /// a path alone does not reliably carry.</param>
        void LoadLot(string path, int houseId);

        /// <summary>Resume a lot from preserved state instead of reading it from disk.</summary>
        /// <param name="marshal">The state handed back by <see cref="TryRestoreSaved"/>.</param>
        /// <param name="houseId">The lot number that state belongs to.</param>
        void LoadLot(VMMarshal marshal, int houseId);

        /// <summary>
        /// Where a completed hop is reported (§B3 task 3, §5.4). A property rather than a method
        /// so the screen can wire it straight to the world's <c>Transit</c> list plus the mirror
        /// into <c>TS1GameState.LotTransitInfo</c> (§11 item 6), which is two writes in one place
        /// rather than two callers remembering them.
        /// </summary>
        Action<NeighbourhoodTransitRecord> RecordTransit { get; }
    }

    /// <summary>
    /// The lot switch B3 asked for, as a type: one path for
    /// <c>NeighborhoodService.PlayHouse</c> (the picker) and <c>VMLotSwitch</c> (the in-VM
    /// request) to share, so the two cannot drift into subtly different lot-preservation
    /// behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ordering is the whole point.</b> There is one live VM, so leaving a lot means
    /// marshalling it out, and a switch that loaded the destination before stashing the origin
    /// would overwrite the state it was supposed to keep. The sequence is therefore fixed and
    /// asserted, in this order and no other:
    /// </para>
    /// <list type="number">
    /// <item>reject an impossible request (no host, or a house number that is not a lot) before
    /// touching anything;</item>
    /// <item><b>stash the current lot</b>, if and only if
    /// <see cref="LotTravel.ShouldStashCurrentLot"/> says the destination is a different lot -
    /// before any teardown, because the teardown is inside the load;</item>
    /// <item>set downtown, before the load, because the load reads it to choose the family and
    /// the downtown time;</item>
    /// <item>resume preserved state if the store holds it, <b>consuming the entry first</b> so a
    /// later visit reloads from disk rather than replaying a stale snapshot, and only then
    /// falling back to the lot's own <c>House{NN}.iff</c>;</item>
    /// <item>record the hop <b>last</b>, and only for a hop that is actually a hop - see
    /// <see cref="TransitionReason"/> and the remarks on <see cref="TryTransition"/>.</item>
    /// </list>
    /// <para>
    /// That is the block that was inline in <c>TS1GameScreen.Update</c>, in the same order
    /// (§11 item 5, "keep the stash-before-load order"), with two things stated explicitly that
    /// the inline version left to its own context: what a failure does, and when the hop is
    /// written.
    /// </para>
    /// <para>
    /// <b>Pure.</b> No VM, no content, no filesystem, no clock and no world - the service asks
    /// the host for everything it does not know and makes no decisions of its own beyond the two
    /// comparisons it exists to make. It is stateless and holds nothing, so one instance serves a
    /// whole session and two tests cannot see each other.
    /// </para>
    /// </remarks>
    public sealed class LotTravelService
    {
        /// <summary>Lowest lot number the service will accept. 0 means "no lot".</summary>
        public const int MinLotNumber = 1;

        /// <summary>
        /// Highest lot number the service will accept. 99 is excluded because the lot-location
        /// parser uses it as its sentinel and <see cref="INeighbourhoodWorld.ActiveLotNumber"/>
        /// uses 0 for the same purpose - a lot numbered either could be a lie, and the failure
        /// would be loading the wrong lot's contents.
        /// </summary>
        public const int MaxLotNumber = 98;

        /// <summary>
        /// The crossing's mode code for a discrete switch (§5.4). The record deliberately does
        /// not define the vocabulary - it stores an int and a reader asks the type that owns the
        /// meaning - so this is the one place that says which mode B3's travel is.
        /// </summary>
        public const int DiscreteTravelMode = 0;

        /// <summary>Is this number a lot a switch may target?</summary>
        /// <param name="houseId">The number to test.</param>
        /// <returns>
        /// True for <see cref="MinLotNumber"/>..<see cref="MaxLotNumber"/>, which is also the
        /// range that cannot overflow the <c>short</c> the per-lot store is keyed by.
        /// </returns>
        public static bool IsLotNumber(int houseId)
        {
            return houseId >= MinLotNumber && houseId <= MaxLotNumber;
        }

        /// <summary>
        /// Whether loading a lot means loading a travel lot rather than a home lot.
        /// </summary>
        /// <remarks>
        /// The screen computes this as
        /// <c>target != (ActiveFamily?.HouseNumber ?? target)</c>, i.e. "this is not the active
        /// family's own house", and that is reproduced here exactly - including the no-family
        /// case, where the original expression's <c>?? target</c> makes the answer false for
        /// <em>every</em> lot. That last half is not a detail: with no family there is no home to
        /// be downtown about, and it is also what keeps a simless load off the travel-slot path
        /// (<see cref="TravelSave.ShouldRestoreSlot"/> refuses a slot without a family too).
        /// </remarks>
        /// <param name="houseId">The lot being loaded.</param>
        /// <param name="activeFamilyHouseNumber">The active family's house number, or null.</param>
        /// <returns>True when the lot is not the active family's own house.</returns>
        public static bool IsDowntownLot(int houseId, int? activeFamilyHouseNumber)
        {
            return activeFamilyHouseNumber.HasValue && houseId != activeFamilyHouseNumber.Value;
        }

        /// <summary>One switch, performed on the host.</summary>
        /// <param name="host">The screen that owns the live lot and the per-lot store.</param>
        /// <param name="request">Which lot and why.</param>
        /// <param name="reason">
        /// A one-line account of what was decided and done, suitable for the switch log. Always
        /// set, including on refusal, because "the switch did nothing" is the case most worth
        /// reading in a log.
        /// </param>
        /// <returns>
        /// <c>true</c> when the destination lot was loaded; <c>false</c> when the request was
        /// refused or the destination had no house file to read.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>What a refusal guarantees.</b> A request for a number that is not a lot, or a null
        /// host, is refused before the first call on the host: nothing is stashed, torn down or
        /// recorded, and the player stays where they were. The one failure that can happen after
        /// the stash - the destination having no house file - leaves the player on the current
        /// lot with that lot's state safely in the store, so a later attempt resumes it rather
        /// than losing it.
        /// </para>
        /// <para>
        /// <b>Why the hop is written last, and only when it is a hop.</b> A record is a claim
        /// about where a sim is, and at <see cref="NeighbourhoodTransitRecord.MaxProgress"/>
        /// percent it claims they arrived - which B7's resume path is entitled to act on. Writing
        /// it before the load would leave a hop that says "arrived" for a lot that never came up.
        /// And a switch whose origin and destination are the same lot is not a hop by the
        /// record's own <see cref="NeighbourhoodTransitRecord.IsHop"/> test, so no record is
        /// written for it; a same-lot switch is a reload, and the world already has a lot active.
        /// </para>
        /// </remarks>
        public bool TryTransition(ILotTravelHost host, LotTransitionRequest request, out string reason)
        {
            reason = null;

            if (host == null)
            {
                reason = "refused: no lot travel host, so the current lot cannot be put anywhere";
                return false;
            }

            if (!IsLotNumber(request.HouseId))
            {
                reason = "refused: " + request.HouseId + " is not a lot number ("
                    + MinLotNumber + ".." + MaxLotNumber + ")";
                return false;
            }

            var target = request.HouseId;
            var current = host.CurrentHouseId;
            //the store is keyed by short, and the range check above is what makes this cast safe
            var lot = (short)target;

            //(1)stash. Before everything, because the teardown that destroys the live VM is inside
            //the load below. Only for a real current lot: 0 means "no lot is loaded", and there
            //is no state to keep and no lot number to key it under.
            var stashed = false;
            if (IsLotNumber(current) && LotTravel.ShouldStashCurrentLot(current, target))
            {
                host.SaveCurrentLot();
                stashed = true;
            }

            //(2)downtown. Before the load, which reads it to pick the family and the downtown time.
            var downtown = IsDowntownLot(target, host.ActiveFamilyHouseNumber);
            host.SetDowntown(downtown);

            //(3)source of lot state. Memory first: an in-session snapshot outranks the on-disk
            //travel slot, and both outrank the shipped template the fallback path reads.
            VMMarshal preserved;
            string path = null;
            var resumed = host.TryRestoreSaved(lot, out preserved) && preserved != null;
            if (resumed)
            {
                //consume the entry before loading, so the snapshot is played exactly once and a
                //later visit reloads this lot from disk. A store that returned true with a null
                //marshal has nothing to play, so it is not consumed and falls through to the
                //file - a null would otherwise reach the load and fail inside the lot.
                host.RemoveSaved(lot);
                host.LoadLot(preserved, target);
            }
            else
            {
                path = host.GetHousePath(target);
                if (string.IsNullOrEmpty(path))
                {
                    reason = (stashed ? "stashed " + current : "no stash")
                        + ", then stopped: lot " + target + " has no house file to load";
                    return false;
                }
                host.LoadLot(path, target);
            }

            //(4)the hop, last and only if it is one.
            var hop = current != target;
            if (hop && host.RecordTransit != null)
            {
                //sim 0 is the record's own "belongs to nobody", and the mode is this class's
                //discrete crossing. origin 0 is the same sentinel, for a move in from no lot.
                host.RecordTransit(new NeighbourhoodTransitRecord(
                    /*sim*/ 0, current, target,
                    DiscreteTravelMode,
                    NeighbourhoodTransitRecord.MaxProgress));
            }

            reason = current + " -> " + target + " (" + ReasonName(request.Reason) + "): "
                + (stashed ? "stashed " + current : "nothing to stash")
                + ", downtown=" + (downtown ? "on" : "off")
                + ", " + (resumed ? "resumed preserved state" : "loaded " + path)
                + (hop ? ", hop recorded" : ", no hop (same lot)");
            return true;
        }

        /// <summary>
        /// The three reasons in the words a log line uses, rather than
        /// <see cref="TransitionReason.ToString"/> - which would follow the enum's member names
        /// and change with them, and which renders an unrecognised cast as a number.
        /// </summary>
        /// <param name="reason">The reason to name.</param>
        /// <returns>A short stable label, or the number for a reason this build does not have.</returns>
        public static string ReasonName(TransitionReason reason)
        {
            switch (reason)
            {
                case TransitionReason.VmRequest: return "vm request";
                case TransitionReason.Picker: return "picker";
                case TransitionReason.MoveIn: return "move in";
                default: return "unknown (" + (int)reason + ")";
            }
        }
    }
}
