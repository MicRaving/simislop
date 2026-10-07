using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The two directions a sim can be sent in by TS1's "auto-follow", and the set of them that
    /// is currently available.
    /// </summary>
    /// <remarks>
    /// These are not Simitone's invention: they are the two engine calls the Downtown expansion
    /// ships, <see cref="VMGenericTS1CallMode.SpawnDowntownDateOfPersonInTemp0"/> (19) and
    /// <see cref="VMGenericTS1CallMode.SpawnTakeBackHomeDataOfPersonInTemp0"/> (20). They are a
    /// flags enum because the two are independent: a sim carries a downtown request and a
    /// take-home request in two separate slots, so a sim that is eligible for neither is
    /// <see cref="None"/> while an ordinary sim is eligible for <em>both</em>, and which one the
    /// player most likely wants is a separate question answered by <see cref="AutoFollow.Preferred"/>.
    /// <para>
    /// The engine reads the two slots independently, but a sim is only ever <em>booked</em> one
    /// way at a time: they cannot be on their way to a Downtown date and on their way home at
    /// once. The set is still what the control works in, because what is on offer is a set - a
    /// sim with a request standing is offered the direction that is not standing.
    /// </para>
    /// </remarks>
    [Flags]
    public enum AutoFollowAction
    {
        /// <summary>Nothing can be sent for this sim.</summary>
        None = 0,

        /// <summary>"Invite to Downtown" - <c>VMGenericTS1Call</c> mode 19.</summary>
        InviteToDowntown = 1,

        /// <summary>"Take Home" - <c>VMGenericTS1Call</c> mode 20.</summary>
        TakeHome = 2,

        /// <summary>Both directions at once; the set returned for an ordinary, eligible sim.</summary>
        Both = InviteToDowntown | TakeHome,
    }

    /// <summary>
    /// Why a sim is or is not eligible to be auto-followed.
    /// </summary>
    /// <remarks>
    /// Reported alongside the decision rather than folded into it, so the control can tell the
    /// player <em>why</em> the two buttons are greyed out - or, for the last two, what is
    /// already queued for them. The order the reasons are tested in is the order they are
    /// declared here: the most fundamental objection to a request ("that is you") is reported
    /// ahead of a bookkeeping one ("a request is already outstanding").
    /// </remarks>
    public enum AutoFollowReason
    {
        /// <summary>The sim may be sent either way.</summary>
        Allowed,

        /// <summary>The target is the player's own sim. You are already where you are.</summary>
        IsSelf,

        /// <summary>
        /// The target is a pet. TS1 excludes pets from the phone book, which is the only
        /// interaction that feeds these two calls, and they carry no neighbour record for the
        /// engine to read a request out of.
        /// </summary>
        IsPet,

        /// <summary>The target is a ghost (TS1 person data 68). A ghost is not travelling.</summary>
        IsGhost,

        /// <summary>
        /// The target is not on this lot. Both engine calls read their target from
        /// <c>Thread.TempRegisters[0]</c> as an in-world object ID, so a sim with no presence on
        /// the lot cannot be named, let alone moved.
        /// </summary>
        NotOnLot,

        /// <summary>
        /// The target is already travelling with the family. <c>VMGenericTS1Call</c>
        /// <see cref="VMGenericTS1CallMode.ChangeToLotInTemp0"/> adds every member of the active
        /// family to the travelling party on its own, so inviting one is a no-op.
        /// </summary>
        AlreadyTravelling,

        /// <summary>
        /// A request is standing for the <em>other</em> direction, so that is what is still on
        /// offer. A sim booked one way is never stuck: pressing the direction that is not queued
        /// withdraws the standing request and queues this one instead, because
        /// <see cref="TryQueueRequest"/> drops the other direction before it writes. The panel
        /// names the direction that is actually queued instead of refusing the sim outright.
        /// </summary>
        PendingInOtherDirection,

        /// <summary>
        /// Requests are standing in <em>both</em> slots. The queue path keeps the two mutually
        /// exclusive, so this can only come from data this control did not write - a request
        /// parked by an older build, or hand-edited neighbourhood data. Both directions are
        /// still offered precisely so the player can collapse it back to one: pressing either
        /// writes that direction and drops the other.
        /// </summary>
        AlreadyPending,
    }

    /// <summary>
    /// What the auto-follow control has to say about one sim, and whether it has anything to
    /// say at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this type exists.</b> The control used to be raised for <em>every</em> avatar the
    /// cursor passed over, whether or not anything could be done about it. Almost nothing a player
    /// points at is auto-followable - themselves, a pet, a ghost, the postman, a skeleton on a
    /// lot with no neighbourhood loaded - and for all of those the old code put up a heading, a
    /// line of prose and two greyed-out buttons: a panel that said nothing the player could act
    /// on, in the middle of the lot, while saying it. The rest of the time the status line was
    /// the only thing on screen: one of seven strings, most of them a refusal, none of which the
    /// player could do anything about.
    /// </para>
    /// <para>
    /// The fix is not a smaller panel or a dimmer one. It is that a control with no button to
    /// offer shows nothing, and that is a decision about the sim rather than about the screen -
    /// so it is a property here, next to <see cref="AutoFollow.Available"/>, where it can be
    /// tested headlessly, instead of a <c>Visible = ...</c> buried in the panel.
    /// </para>
    /// <para>
    /// A struct rather than a class for the same reason <c>PlumbbobPresentation</c> is: the lot
    /// control asks this question about the hovered sim <em>every frame</em>, reads the answer,
    /// and throws it away.
    /// </para>
    /// </remarks>
    public readonly struct AutoFollowOffer
    {
        /// <summary>
        /// The directions the player may send this sim in, or
        /// <see cref="AutoFollowAction.None"/> when there are none.
        /// </summary>
        public AutoFollowAction Available { get; }

        /// <summary>
        /// Why those directions are (or are not) on offer - including, when they are not, the
        /// reason that the panel no longer shows.
        /// </summary>
        public AutoFollowReason Reason { get; }

        /// <summary>
        /// Whether the control should be on screen for this sim.
        /// </summary>
        /// <remarks>
        /// Exactly "is there a direction to press". A panel with no live button has nothing to
        /// say: every string the status line can produce for a refused sim is a restatement of
        /// the refusal, and the refusal is already the cursor's own - a sim that cannot be
        /// auto-followed is a sim the player was not invited to do anything with in the first
        /// place.
        /// <para>
        /// Derived rather than stored so it cannot disagree with
        /// <see cref="Available"/>, which is the only thing it is a statement about.
        /// </para>
        /// </remarks>
        public bool IsShown
        {
            get { return Available != AutoFollowAction.None; }
        }

        internal AutoFollowOffer(AutoFollowAction available, AutoFollowReason reason)
        {
            Available = available;
            Reason = reason;
        }

        /// <summary>
        /// The direction to lead with on the given lot, or
        /// <see cref="AutoFollowAction.None"/> for an offer that is not shown.
        /// </summary>
        /// <param name="lotIsDowntown">Whether the current lot is the Downtown neighbourhood.</param>
        public AutoFollowAction Preferred(bool lotIsDowntown)
        {
            return AutoFollow.Preferred(Available, lotIsDowntown);
        }
    }

    /// <summary>
    /// Everything about a sim that the auto-follow decision turns on, with no reference to the
    /// object graph it came from.
    /// </summary>
    /// <remarks>
    /// The control reads these off a <c>VMAvatar</c> and hands them over; keeping the decision
    /// this side of the boundary is what lets it be tested without a VM, a lot, or a graphics
    /// device. See <see cref="Ordinary"/> for the "nothing special about this sim" case that most
    /// of the rules are stated relative to.
    /// </remarks>
    public sealed class AutoFollowSubject
    {
        /// <summary>The target is the sim the player is controlling.</summary>
        public bool IsSelf { get; set; }

        /// <summary>The target is a dog or a cat.</summary>
        public bool IsPet { get; set; }

        /// <summary>The target is dead.</summary>
        public bool IsGhost { get; set; }

        /// <summary>
        /// The target has a body on the current lot, and so has an object ID the engine's
        /// <c>Spawn...InTemp0</c> calls can be pointed at.
        /// </summary>
        public bool IsOnLot { get; set; }

        /// <summary>The target is a member of the family that is already travelling with the lot.</summary>
        public bool InTravellingFamily { get; set; }

        /// <summary>
        /// The directions this sim already has a standing request in, read out of its
        /// neighbourhood inventory. <see cref="AutoFollowAction.None"/> for a sim with nothing
        /// pending; a single direction for an ordinary queued request; and
        /// <see cref="AutoFollowAction.Both"/> only for a sim carrying a request in each slot,
        /// which this control's own writes cannot produce.
        /// </summary>
        /// <remarks>
        /// A set rather than a flag because the direction matters: it is what decides which
        /// direction is still on offer, and what the panel names in its status line. Collapsing
        /// it to <c>true</c> would make "invite to Downtown" and "take home" indistinguishable
        /// to a control that has to report which of the two is standing.
        /// </remarks>
        public AutoFollowAction Pending { get; set; }

        /// <summary>
        /// A sim that trips none of the exclusions: on the lot, alive, human, and not already
        /// travelling with the family. A fresh instance is returned per call, so callers are free
        /// to mutate the result.
        /// </summary>
        public static AutoFollowSubject Ordinary
        {
            get { return new AutoFollowSubject { IsOnLot = true }; }
        }
    }

    /// <summary>
    /// The decisions behind Simitone's "auto-follow" control, and the engine slots it feeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where the two directions come from.</b> Downtown's auto-follow is two
    /// <see cref="VMGenericTS1CallMode"/> calls. Both do the same thing and differ only in which
    /// slot of the target sim's neighbourhood inventory they read:
    /// <c>SpawnDowntownDateOfPersonInTemp0</c> reads
    /// <see cref="DowntownDateSlot"/> and <c>SpawnTakeBackHomeDataOfPersonInTemp0</c> reads
    /// <see cref="TakeBackHomeSlot"/>. Both take their target from the calling thread's
    /// <c>TempRegisters[0]</c> as an in-world object ID, read the target's neighbour inventory,
    /// spawn the sim whose neighbour ID is stored in the slot, consume the slot, and leave the
    /// new sim's object ID back in <c>TempRegisters[0]</c> for the lot script to carry to the
    /// destination.
    /// </para>
    /// <para>
    /// <b>What that means for a UI.</b> A sim can only be auto-followed if it is on the lot,
    /// because that is the only way to produce the object ID the calls need. A request has to be
    /// <em>standing in the slot</em> before the call runs, which is the half a client can do and
    /// the half it does here; running the call itself is a primitive, so it belongs to a TS1 lot
    /// script thread and not to the UI.
    /// </para>
    /// <para>
    /// <b>One direction at a time.</b> A sim carries a request per slot, so nothing stops the
    /// engine from finding two of them. A player does not mean it: a standing request is a live
    /// instruction, and two of them mean the lot scripts get to spawn this sim twice, in two
    /// directions the player did not ask for. So the write path treats the pair as one decision -
    /// <see cref="TryQueueRequest"/> drops the direction it is not writing - and the read path
    /// reports which direction is standing, so that <see cref="Available"/> can offer the other
    /// one. That is also what makes a queued request withdrawable: the control never shows a
    /// sim as having nothing left to do, because the direction that is not queued is always one
    /// press away.
    /// </para>
    /// <para>
    /// Every decision here is pure - plain data in, plain data out, no VM, no lot, no graphics
    /// device - so the whole of the eligibility rule is testable headlessly.
    /// </para>
    /// <para>
    /// <b>Whether to be visible at all.</b> <see cref="Available"/> says what a sim can be sent
    /// in; <see cref="Offer"/> additionally says whether the control should be on screen for it.
    /// That is a separate question on purpose: a control whose buttons are all dead is noise, and
    /// on a lot the cursor rests on a lot of things that cannot be auto-followed. So the rule is
    /// "visible iff at least one direction is on offer", and it is pinned in
    /// <c>AutoFollowTests</c> rather than left to the panel.
    /// </para>
    /// </remarks>
    public static class AutoFollow
    {
        /// <summary>
        /// Neighbourhood-inventory entry type the two calls read. "Sim data" entries: the same
        /// type the travel code writes motive and clock values into, under GUIDs 0-9.
        /// </summary>
        public const int SimDataInventoryType = 2;

        /// <summary>
        /// Inventory GUID the downtown call reads. This is the engine's own
        /// <c>inventoryInd</c> local, which is initialised to 10 and set to 11 immediately before
        /// the take-home case jumps into the shared body.
        /// </summary>
        public const uint DowntownDateSlot = 10;

        /// <summary>Inventory GUID the take-home call reads.</summary>
        public const uint TakeBackHomeSlot = 11;

        /// <summary>
        /// Person data 77, the "who is following me" field TS1 wrote alongside the slot. The
        /// engine declares it and nothing reads it, so this control is its only writer.
        /// </summary>
        public const VMPersonDataVariable FollowPersonData = VMPersonDataVariable.TS1AutoFollow;

        /// <summary>The engine call behind <see cref="AutoFollowAction.InviteToDowntown"/>.</summary>
        public const VMGenericTS1CallMode DowntownCall =
            VMGenericTS1CallMode.SpawnDowntownDateOfPersonInTemp0;

        /// <summary>The engine call behind <see cref="AutoFollowAction.TakeHome"/>.</summary>
        public const VMGenericTS1CallMode TakeHomeCall =
            VMGenericTS1CallMode.SpawnTakeBackHomeDataOfPersonInTemp0;

        /// <summary>
        /// The lowest neighbour ID a request may name, and therefore the first one that can
        /// actually be spawned.
        /// </summary>
        /// <remarks>
        /// TS1 neighbour IDs are 1-based, and the engine takes the request's neighbour ID straight
        /// out of the entry's <c>Count</c>, as <c>GetNeighborByID((short)Count)</c>. A
        /// non-positive ID is in no dictionary, so that is <c>null</c> and the engine refuses it
        /// rather than spawning. Refusing the ID where the request is written keeps a request that
        /// could never spawn from standing in the first place.
        /// </remarks>
        public const short MinSpawnNeighbourId = 1;

        /// <summary>
        /// Whether <paramref name="spawnNid"/> names a neighbour the engine could spawn.
        /// </summary>
        /// <remarks>
        /// Only the lower bound needs checking: the ID travels in a <c>ushort</c> but is cast
        /// back to a <c>short</c> by the engine, so anything above <see cref="short.MaxValue"/>
        /// comes back negative - and a parameter typed <c>short</c> cannot express it in the
        /// first place. Zero and the negatives are the whole of the problem.
        /// </remarks>
        public static bool IsSpawnableNeighbourId(short spawnNid)
        {
            return spawnNid >= MinSpawnNeighbourId;
        }

        /// <summary>
        /// The one direction that is not <paramref name="action"/>.
        /// </summary>
        /// <returns>
        /// <see cref="AutoFollowAction.TakeHome"/> for
        /// <see cref="AutoFollowAction.InviteToDowntown"/> and vice versa;
        /// <see cref="AutoFollowAction.None"/> for <paramref name="action"/> being
        /// <see cref="AutoFollowAction.None"/> or <see cref="AutoFollowAction.Both"/>, neither
        /// of which has a single opposite.
        /// </returns>
        /// <remarks>
        /// The two directions are not two halves of one trip; they are two opposite statements
        /// about the same sim at the same time, so a sim that is booked one way is by
        /// definition not booked the other. That is why this is a helper rather than a flag
        /// test: the write path (<see cref="TryQueueRequest"/>) and the read path
        /// (<see cref="Available"/>) have to agree about it, and "the other direction" is the
        /// only thing either of them needs to name.
        /// </remarks>
        public static AutoFollowAction Opposite(AutoFollowAction action)
        {
            switch (action)
            {
                case AutoFollowAction.InviteToDowntown:
                    return AutoFollowAction.TakeHome;
                case AutoFollowAction.TakeHome:
                    return AutoFollowAction.InviteToDowntown;
                default:
                    return AutoFollowAction.None;
            }
        }

        /// <summary>True when <paramref name="action"/> is a single, real direction.</summary>
        public static bool IsSingle(AutoFollowAction action)
        {
            return action == AutoFollowAction.InviteToDowntown || action == AutoFollowAction.TakeHome;
        }

        /// <summary>
        /// The neighbourhood-inventory slot a direction writes.
        /// </summary>
        /// <param name="action">The direction.</param>
        /// <param name="slot">The slot on success.</param>
        /// <returns>
        /// <c>true</c> for <see cref="AutoFollowAction.InviteToDowntown"/> and
        /// <see cref="AutoFollowAction.TakeHome"/>; <c>false</c> for <see cref="AutoFollowAction.None"/>
        /// and for <see cref="AutoFollowAction.Both"/>.
        /// </returns>
        /// <remarks>
        /// A <c>Try</c> rather than a plain lookup on purpose. Slot 0 is a real slot - it holds
        /// bladder - so a caller that resolved "no direction" to 0 would write a motive as a
        /// neighbour ID. There is no sensible slot for "either", so the set has to be narrowed
        /// to a single direction before it can be turned into one.
        /// </remarks>
        public static bool TrySlotFor(AutoFollowAction action, out uint slot)
        {
            switch (action)
            {
                case AutoFollowAction.InviteToDowntown:
                    slot = DowntownDateSlot;
                    return true;
                case AutoFollowAction.TakeHome:
                    slot = TakeBackHomeSlot;
                    return true;
                default:
                    slot = 0;
                    return false;
            }
        }

        /// <summary>
        /// The direction a neighbourhood-inventory slot belongs to, or
        /// <see cref="AutoFollowAction.None"/> if it is not one of the two auto-follow slots.
        /// </summary>
        public static AutoFollowAction ActionForSlot(uint slot)
        {
            if (slot == DowntownDateSlot) return AutoFollowAction.InviteToDowntown;
            if (slot == TakeBackHomeSlot) return AutoFollowAction.TakeHome;
            return AutoFollowAction.None;
        }

        /// <summary>
        /// Decides which directions a sim may be sent in.
        /// </summary>
        /// <param name="subject">The sim, described without any reference to the lot.</param>
        /// <param name="reason">Why the sim was allowed or refused.</param>
        /// <returns>
        /// <see cref="AutoFollowAction.Both"/> when nothing excludes the sim, the one direction
        /// that is <em>not</em> already queued when a request is standing, and
        /// <see cref="AutoFollowAction.None"/> when the sim cannot be sent anywhere at all.
        /// Both directions are structurally available on either lot - the two slots are
        /// independent, so a Downtown lot does not stop a sim from being invited to Downtown.
        /// Which of the two the player most likely wants is <see cref="Preferred"/>, a separate
        /// question.
        /// </returns>
        public static AutoFollowAction Available(AutoFollowSubject subject, out AutoFollowReason reason)
        {
            // a null subject is treated as "no sim", which is the one thing that is always refused
            if (subject == null)
            {
                reason = AutoFollowReason.NotOnLot;
                return AutoFollowAction.None;
            }

            // you cannot be invited somewhere you are already standing
            if (subject.IsSelf)
            {
                reason = AutoFollowReason.IsSelf;
                return AutoFollowAction.None;
            }

            // the phone book - the only interaction the engine routes into these two calls -
            // filters pets out, and a pet has no neighbour record holding a request
            if (subject.IsPet)
            {
                reason = AutoFollowReason.IsPet;
                return AutoFollowAction.None;
            }

            if (subject.IsGhost)
            {
                reason = AutoFollowReason.IsGhost;
                return AutoFollowAction.None;
            }

            // both calls read TempRegisters[0] as an in-world object ID; no body, no request
            if (!subject.IsOnLot)
            {
                reason = AutoFollowReason.NotOnLot;
                return AutoFollowAction.None;
            }

            // ChangeToLotInTemp0 already takes the whole active family along
            if (subject.InTravellingFamily)
            {
                reason = AutoFollowReason.AlreadyTravelling;
                return AutoFollowAction.None;
            }

            // a request already standing in one direction does not close the control: the other
            // direction is still on offer, and queueing it drops the standing one. Offering the
            // queued direction again would only rewrite the request the player already has, and
            // refusing both would leave the request unable to be withdrawn or re-directed
            if (subject.Pending == AutoFollowAction.None)
            {
                reason = AutoFollowReason.Allowed;
                return AutoFollowAction.Both;
            }

            if (subject.Pending == AutoFollowAction.Both)
            {
                // both slots occupied, which only data this control did not write can do. Either
                // button collapses it back to a single request, so neither is closed
                reason = AutoFollowReason.AlreadyPending;
                return AutoFollowAction.Both;
            }

            reason = AutoFollowReason.PendingInOtherDirection;
            return Opposite(subject.Pending);
        }

        /// <summary>
        /// Picks the direction to lead with, given the lot the player is standing on.
        /// </summary>
        /// <param name="available">Whatever <see cref="Available"/> returned.</param>
        /// <param name="lotIsDowntown">Whether the current lot is the Downtown neighbourhood.</param>
        /// <returns>
        /// <see cref="AutoFollowAction.TakeHome"/> on Downtown - a sim standing there is away from
        /// home, so bringing them back is the meaningful move - and
        /// <see cref="AutoFollowAction.InviteToDowntown"/> anywhere else, where the lot the
        /// player is on is the one they are inviting somebody <em>away</em> from. If only one
        /// direction is available that one is used; if none are, so is
        /// <see cref="AutoFollowAction.None"/>.
        /// </returns>
        public static AutoFollowAction Preferred(AutoFollowAction available, bool lotIsDowntown)
        {
            if (!IsSingle(available) && available != AutoFollowAction.Both) return AutoFollowAction.None;

            var wanted = lotIsDowntown ? AutoFollowAction.TakeHome : AutoFollowAction.InviteToDowntown;
            if ((available & wanted) != AutoFollowAction.None) return wanted;

            // a single-direction set that is not the preferred one: it is the only option left
            return (available & AutoFollowAction.InviteToDowntown) != AutoFollowAction.None
                ? AutoFollowAction.InviteToDowntown
                : AutoFollowAction.TakeHome;
        }

        /// <summary>
        /// The whole answer for one sim: what it may be sent in, why, and - through
        /// <see cref="AutoFollowOffer.IsShown"/> - whether the control has anything to put on
        /// screen for it at all.
        /// </summary>
        /// <param name="subject">The sim, described without any reference to the lot.</param>
        /// <remarks>
        /// The one call a control needs per frame. <see cref="Available"/> answers the same
        /// question and is still the rule itself; what this adds is the decision to be visible,
        /// gathered with the decision about what to offer so a caller cannot apply one and forget
        /// the other - which is exactly the bug that put an inert panel on screen for every sim
        /// the cursor touched.
        /// <para>
        /// A <c>null</c> subject, and a subject with no offer at all, both come back as
        /// <see cref="AutoFollowAction.None"/>, so the caller does not have to special-case a
        /// frame where the sim resolved to nothing.
        /// </para>
        /// </remarks>
        public static AutoFollowOffer Offer(AutoFollowSubject subject)
        {
            AutoFollowReason reason;
            var available = Available(subject, out reason);
            return new AutoFollowOffer(available, reason);
        }

        /// <summary>
        /// Looks for a standing auto-follow request in a sim's neighbourhood inventory.
        /// </summary>
        /// <param name="inventory">The sim's inventory list, as the engine's
        /// <c>GetInventoryByNID</c> returns it. May be <c>null</c>.</param>
        /// <param name="action">The direction the request is for.</param>
        /// <param name="spawnNid">The neighbour ID the request asks the engine to spawn.</param>
        /// <returns>
        /// <c>true</c> when either slot is occupied. A downtown request is reported ahead of a
        /// take-home one, matching the order the engine's own lookup would find them in.
        /// </returns>
        /// <remarks>
        /// The exact inverse of what <c>SpawnDowntownDateOfPersonInTemp0</c> does when it runs,
        /// down to which of the two requests it finds first, so a request this class wrote can be
        /// read back exactly as the primitive will read it. The neighbour ID is carried in the
        /// entry's <c>Count</c>, which is the field the engine casts back to a neighbour ID - so
        /// that cast is done here the same way rather than a second, stricter one that would
        /// disagree with the engine about the same bytes.
        /// <para>
        /// The control itself reads its pending state with <see cref="FindPendingActions"/>
        /// rather than this: it has to know which direction is standing, and this deliberately
        /// reports just one of them.
        /// </para>
        /// </remarks>
        public static bool TryFindRequest(IEnumerable<InventoryItem> inventory, out AutoFollowAction action, out short spawnNid)
        {
            action = AutoFollowAction.None;
            spawnNid = 0;

            InventoryItem found = null;
            foreach (var item in RequestEntries(inventory))
            {
                // downtown first, so a sim carrying both reports the same one the engine's
                // first branch would have picked
                if (found == null || found.GUID == TakeBackHomeSlot) found = item;
            }

            if (found == null) return false;

            action = ActionForSlot(found.GUID);
            spawnNid = (short)found.Count;
            return true;
        }

        /// <summary>
        /// Reports every direction a sim already has a standing request in.
        /// </summary>
        /// <param name="inventory">The sim's inventory list, as the engine's
        /// <c>GetInventoryByNID</c> returns it. May be <c>null</c>.</param>
        /// <returns>
        /// The union of the directions whose slot is occupied: <see cref="AutoFollowAction.None"/>
        /// for a sim with nothing pending, a single direction for an ordinary queued request,
        /// and <see cref="AutoFollowAction.Both"/> for a sim carrying one in each slot.
        /// </returns>
        /// <remarks>
        /// This is what the control reads to describe a sim, and unlike
        /// <see cref="TryFindRequest"/> it does not have to pick one request to report: the
        /// caller needs to know <em>which</em> direction is standing, so that the direction it
        /// offers and the direction it names in the panel are the ones that are not already
        /// queued. Reading a single request and remembering it as a bare "something is pending"
        /// is what let a fresh take-home request be reported as an old downtown one.
        /// </remarks>
        public static AutoFollowAction FindPendingActions(IEnumerable<InventoryItem> inventory)
        {
            var pending = AutoFollowAction.None;
            foreach (var item in RequestEntries(inventory)) pending |= ActionForSlot(item.GUID);
            return pending;
        }

        /// <summary>
        /// The entries of a sim's neighbourhood inventory that the two calls would read: type 2,
        /// in slot 10 or slot 11. The single definition of "this is an auto-follow request", so
        /// the read and write paths cannot drift apart on it.
        /// </summary>
        private static IEnumerable<InventoryItem> RequestEntries(IEnumerable<InventoryItem> inventory)
        {
            if (inventory == null) yield break;

            foreach (var item in inventory)
            {
                if (item == null || item.Type != SimDataInventoryType) continue;
                if (item.GUID != DowntownDateSlot && item.GUID != TakeBackHomeSlot) continue;
                yield return item;
            }
        }

        /// <summary>
        /// Writes a standing auto-follow request into a sim's neighbourhood inventory, replacing
        /// whatever was in that slot and withdrawing the request for the other direction.
        /// </summary>
        /// <param name="inventory">The sim's inventory list. May be <c>null</c>.</param>
        /// <param name="action">Which direction to request. Must be a single direction.</param>
        /// <param name="spawnNid">The neighbour ID the engine should spawn when it runs the call.</param>
        /// <returns>
        /// <c>true</c> when the request was written.
        /// </returns>
        /// <remarks>
        /// The write is deliberately shaped like the engine's own cleanup: every entry in the
        /// slot is dropped and a single new one put in its place, so the slot can never end up
        /// holding two requests.
        /// <para>
        /// The <em>other</em> direction's slot is cleared first, and that is the whole point of
        /// going through here. The two slots are independent as far as the engine is concerned -
        /// each call reads only its own - but a sim cannot be booked to Downtown and taken home
        /// at the same time, and a request left standing in the other slot is not merely stale
        /// bookkeeping: it is a live instruction that the next lot script to run one of these
        /// calls will carry out, spawning the sim in a direction the player has already changed
        /// their mind about. Dropping it is also how a request is withdrawn, and how a player
        /// changes their mind: the control offers whichever direction is not already queued, and
        /// queueing it replaces what was there.
        /// </para>
        /// <para>
        /// Everything is checked before anything is written, so a refused call leaves the
        /// inventory exactly as it found it - including the other direction's request, which
        /// must not be dropped on the way to refusing.
        /// </para>
        /// <para>
        /// A <c>null</c> inventory is refused rather than created. The engine reads
        /// <c>GetInventoryByNID</c> and gives up when it is <c>null</c>, so a request parked
        /// somewhere the engine will not look is worse than no request at all. So is one naming
        /// a neighbour that does not exist: see <see cref="IsSpawnableNeighbourId"/>.
        /// </para>
        /// </remarks>
        public static bool TryQueueRequest(List<InventoryItem> inventory, AutoFollowAction action, short spawnNid)
        {
            uint slot;
            if (inventory == null || !TrySlotFor(action, out slot)) return false;

            // an ID that names no neighbour is a request the engine refuses, so nothing would ever
            // spawn from it. Refuse it here, where the ID comes from
            if (!IsSpawnableNeighbourId(spawnNid)) return false;

            // a sim is booked one way or the other, never both
            ClearRequest(inventory, Opposite(action));

            inventory.RemoveAll(x => x != null && x.Type == SimDataInventoryType && x.GUID == slot);
            inventory.Add(new InventoryItem()
            {
                Type = SimDataInventoryType,
                GUID = slot,
                Count = (ushort)spawnNid
            });
            return true;
        }

        /// <summary>
        /// Drops the standing request for a direction, as the engine does when its call runs.
        /// </summary>
        /// <returns><c>true</c> when something was removed.</returns>
        /// <remarks>
        /// Also how one direction is withdrawn in favour of the other:
        /// <see cref="TryQueueRequest"/> calls this for the direction it is <em>not</em> writing,
        /// which is what keeps a sim from carrying both requests at once.
        /// </remarks>
        public static bool ClearRequest(List<InventoryItem> inventory, AutoFollowAction action)
        {
            uint slot;
            if (inventory == null || !TrySlotFor(action, out slot)) return false;
            return inventory.RemoveAll(x => x != null && x.Type == SimDataInventoryType && x.GUID == slot) > 0;
        }
    }
}
