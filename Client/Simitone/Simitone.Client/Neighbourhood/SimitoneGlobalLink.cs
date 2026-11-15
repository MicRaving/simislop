/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Engine.TSOGlobalLink.Model;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.NetPlay.EODs.Handlers;
using FSO.SimAntics.NetPlay.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Primitives;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// What leaving a lot means, decided from one number.
    ///
    /// <para>
    /// <b>Why this is a value and not a branch inside a method.</b> The whole of B5's
    /// leave-lot rule is a comparison - <c>avatar.PersistID == vm.MyUID</c> - and the rule the
    /// stub gets wrong for single player is the <em>other</em> side of it. So the decision is
    /// lifted out as a pure function over two persist ids, which is the only part of
    /// <see cref="SimitoneGlobalLink.LeaveLot"/> that can be asserted without a live
    /// <c>VM</c>, a graphics device and a content manager. The method that does the work reads
    /// its branch from here; the test reads the same branch from here.
    /// </para>
    /// </summary>
    public enum SimitoneLotLeaveAction
    {
        /// <summary>
        /// Somebody else's avatar is leaving: delete it and say so in chat. No net close, no
        /// transition, nothing to do about the lot. This is exactly the stub's behaviour.
        /// </summary>
        DepartLot = 0,

        /// <summary>
        /// The local player is leaving: the lot must <em>transition</em>. The VM raises its
        /// existing lot-switch signal and the screen performs the stash-then-load.
        /// <c>VMTS1GlobalLinkStub</c> calls <c>CloseNet(VMCloseNetReason.LeaveLot)</c> here,
        /// which in single player tears the lot down instead of moving the player anywhere -
        /// and there is no network to be disconnected from, so the shutdown is the end of the
        /// lot rather than the end of a session.
        /// </summary>
        TransitionToLot = 1
    }

    /// <summary>
    /// Part B5's global link: a single-player <see cref="IVMTSOGlobalLink"/> that keeps the
    /// behaviour <see cref="VMTS1GlobalLinkStub"/> already had, changes the one thing that was
    /// wrong for an offline game, and takes its outside world as injected handles rather than
    /// reaching for a screen or a static.
    ///
    /// <para>
    /// <b>It is not the stub, and it is not a subclass of the stub.</b> Inheriting would have
    /// been the smallest diff, and it would also have made every behaviour this milestone
    /// changes unreachable to a test that has no <c>VM</c>: the interesting half of the type is
    /// precisely the half that is not the stub. So this is a separate sealed class that
    /// implements the interface directly, and the two agree member for member except where this
    /// document says otherwise.
    /// </para>
    ///
    /// <para>
    /// <b>Everything it needs is a constructor argument.</b> The family it charges is a
    /// <c>Func&lt;short, FAMI&gt;</c>, the lot it sends a leaving player to is an
    /// <c>Action&lt;int&gt;</c> and the milestone's capability is a <c>bool</c>. It is
    /// "leave-lot plus local architecture" and no more: it holds no world, and nothing here is
    /// a neighbourhood-wide family or occupancy authority. A test can therefore build one with a
    /// dictionary of families and a list of destinations, and a screen can build one with its
    /// own <c>VMLotSwitch</c> and its own family, without either of them being visible to this
    /// file. What is left that needs a real lot - <c>Tick</c>'s architecture simulation, the
    /// transaction path's <c>GetObjectByPersist</c> - is the part that genuinely cannot be
    /// faked, and the tests say so rather than pretending.
    /// </para>
    ///
    /// <para>
    /// <b>The game thread, and only the game thread.</b> The TS1 stub answers a transaction
    /// callback from a <c>new System.Threading.Thread()</c>, which means the caller's callback
    /// and the command that releases the waiting SimAntics thread both run off the thread that
    /// owns the <c>VM</c>. Every callback here is invoked inline, on the thread that called in -
    /// which for the engine is the game thread (§8 of the handoff) and for a test is the test's
    /// own thread, so "inline" is an assertion rather than a promise.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws.</b> Every member is reachable from a lot that is being torn down, from
    /// a VM whose driver has already gone, and from a screen that is mid-switch. A failure here
    /// is logged through <see cref="SimitoneLog"/> and turned into the answer the caller can
    /// live with; an exception thrown out of a global link is an exception thrown from inside
    /// somebody else's tick.
    /// </para>
    /// </summary>
    public sealed class SimitoneGlobalLink : IVMTSOGlobalLink
    {
        /// <summary>
        /// The lot number that means "wherever this player's own house is", which is how
        /// <c>TS1GameScreen.VMLotSwitch</c> already reads a request: it maps a lot id of
        /// <c>-1</c> onto <c>ActiveFamily.HouseNumber</c> before it sets up the switch.
        ///
        /// <para>
        /// Used as the destination when this link cannot name a home lot of its own - no family,
        /// no world, or the milestone's flag is off. Signalling <c>(uint)-1</c> therefore reaches
        /// the screen as a request it already knows how to resolve, rather than as a number it
        /// would have to guess at.
        /// </para>
        /// </summary>
        public const int GoHomeLot = -1;

        /// <summary>
        /// The budget of a party that is not a family: "Maxis has infinite money", which is the
        /// stub's answer for anything that is not an avatar, and this type's for a family it
        /// cannot find.
        /// </summary>
        public const uint InfiniteBudget = uint.MaxValue;

        /// <summary>
        /// The first synthetic persist id handed out by <see cref="AllocatePersistId"/>. The
        /// stub starts its counter at the same place, so ids do not collide with the engine's.
        /// </summary>
        public const uint FirstSyntheticPersist = 0x1000000;

        private readonly Func<short, FAMI> _families;
        private readonly Action<int> _lotTransition;
        private readonly bool _worldServices;

        private readonly Queue<VMNetArchitectureCmd> _archBuffer = new Queue<VMNetArchitectureCmd>();
        private bool _waitingOnArch;
        private uint _nextPersist = FirstSyntheticPersist;

        /// <summary>
        /// A global link over an injected family authority, an injected lot-transition hook, and
        /// (optionally) an injected world.
        /// </summary>
        /// <param name="familyResolver">
        /// Resolves a <c>FAMI</c> chunk id - an avatar's
        /// <see cref="VMPersonDataVariable.TS1FamilyNumber"/> - to the family that owns the
        /// money. <c>null</c> means "ask the lot", i.e. fall back to
        /// <c>vm.TS1State.CurrentFamily</c>, which is what the stub does. A resolver that
        /// returns <c>null</c> for a family it does not know also falls back, so a world that
        /// has not loaded its families yet behaves like a world that is not there.
        /// </param>
        /// <param name="onLotTransitionRequested">
        /// Told the lot number a leaving player is being sent to, before the VM signal is
        /// raised. The world's own use is to record the hop; the screen's own use is
        /// <c>TS1GameScreen.VMLotSwitch</c>, which the VM signal already reaches, so passing
        /// that one here as well is redundant rather than harmful (it only assigns
        /// <c>SwitchLot</c>). <c>null</c> for a link that only signals the VM.
        /// </param>
        /// <param name="worldServicesEnabled">
        /// The <c>NeighbourhoodWorldServices</c> capability (§7 of the handoff). With the
        /// milestone off, the screen installs <see cref="VMTS1GlobalLinkStub"/> and this class
        /// is never asked to do anything, so the flag is carried rather than consulted for
        /// behaviour that cannot be reached. The one place it is consulted is
        /// <see cref="ResolveLeaveDestination"/>: with the flag off this link does not claim to
        /// know where home is and asks the screen to resolve
        /// <see cref="GoHomeLot"/>, which is the pre-B5 answer and needs no world.
        /// </param>
        public SimitoneGlobalLink(
            Func<short, FAMI> familyResolver = null,
            Action<int> onLotTransitionRequested = null,
            bool worldServicesEnabled = true)
        {
            _families = familyResolver;
            _lotTransition = onLotTransitionRequested;
            _worldServices = worldServicesEnabled;
        }

        /// <summary>
        /// The <c>NeighbourhoodWorldServices</c> flag this link was built with. Exposed so a
        /// screen (or a log line) can say which of the two implementations is driving the lot.
        /// </summary>
        public bool WorldServicesEnabled
        {
            get { return _worldServices; }
        }

        /// <summary>
        /// The next persist id <see cref="RegisterNewObject"/> will hand out. The counter is not
        /// settable: a caller that wanted an id out of sequence would be editing the very state
        /// the offline game relies on to be monotonic.
        /// </summary>
        public uint NextPersist
        {
            get { return _nextPersist; }
        }

        /// <summary>
        /// How many architecture commands are waiting for <see cref="Tick"/>. Named rather than
        /// exposed as the queue itself, so a caller can see the back-pressure without being able
        /// to dequeue from under <see cref="Tick"/>.
        /// </summary>
        public int PendingArchitectureCommands
        {
            get { lock (_archBuffer) { return _archBuffer.Count; } }
        }

        /// <summary>
        /// Whether <see cref="Tick"/> is holding a command back until a transaction it is
        /// waiting on comes back. The stub's back-pressure: while this is true the queue does
        /// not drain, which is what stops a lot from spending its family into the negative one
        /// build at a time.
        /// </summary>
        public bool WaitingOnArchitecture
        {
            get { lock (_archBuffer) { return _waitingOnArch; } }
        }

        // ---- family budgets -------------------------------------------------------------------------

        /// <summary>
        /// The family that owns this entity's money, resolved through the injected resolver and
        /// falling back to the lot's own current family. <c>null</c> when the entity is not an
        /// avatar, when no resolver answered, and when the lot has no current family - all three
        /// of which the stub treats as "Maxis has infinite money".
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="ent">The entity paying or being paid, or <c>null</c>.</param>
        public FAMI ResolveFamily(VM vm, VMEntity ent)
        {
            var avatar = ent as VMAvatar;
            if (avatar == null) return null; //not a person: Maxis pays
            short familyNumber;
            try
            {
                familyNumber = avatar.GetPersonData(VMPersonDataVariable.TS1FamilyNumber);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not read an avatar's family number,"
                    + " falling back to the lot's current family.", e);
                return CurrentFamily(vm);
            }
            return ResolveFamily(vm, familyNumber);
        }

        /// <summary>
        /// The family with this chunk id, through the injected resolver, falling back to the
        /// lot's own current family. Never throws: a resolver that throws is logged and treated
        /// as one that knows nothing, because a lot cannot ask its player to retry because a
        /// family lookup misbehaved. A resolver that answers with a family this chunk id is not is
        /// treated the same way - see <see cref="IsTheFamilyAskedFor"/>, which is where the
        /// chunk-id-versus-house-number trap is written down.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="familyId">The <c>FAMI</c> chunk id.</param>
        public FAMI ResolveFamily(VM vm, short familyId)
        {
            if (_families != null)
            {
                try
                {
                    var family = _families(familyId);
                    if (IsTheFamilyAskedFor(familyId, family)) return family;
                }
                catch (Exception e)
                {
                    SimitoneLog.Warn("SimitoneGlobalLink: the family resolver threw for family "
                        + familyId + "; falling back to the lot's current family.", e);
                }
            }
            return CurrentFamily(vm);
        }

        /// <summary>
        /// Whether <paramref name="family"/> is the family <paramref name="familyNumber"/> names.
        /// The whole of what this link will accept from an injected family authority.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The number a resolver is given is a <c>FAMI</c> chunk id, not a house number.</b>
        /// <c>VMTS1LotState.ActivateFamily</c> writes <c>CurrentFamily.ChunkID</c> into an avatar's
        /// <c>VMPersonDataVariable.TS1FamilyNumber</c>, and that is the value handed to the
        /// resolver. The neighbourhood's own family table, by contrast, is keyed on
        /// <c>FAMI.HouseNumber</c> - and in a shipped neighbourhood the two ranges overlap heavily,
        /// because families and lots are both numbered from one. A resolver keyed on the wrong one
        /// therefore answers with a real family every time rather than with <c>null</c>, and every
        /// budget decision below is then made against a household the player has never heard of: a
        /// build whose charge is refused, a purchase that will not go through, and an interaction
        /// that spends money failing for a player who is not broke.
        /// </para>
        /// <para>
        /// <b>So the answer is verified rather than trusted, and a mismatch is treated as knowing
        /// nothing.</b> <c>null</c> is what "knows nothing" looks like, and the code after the
        /// resolver already handles it: the lot answers for itself through
        /// <see cref="CurrentFamily"/>, which is the stub's answer. A mis-keyed authority is
        /// therefore not a wrong family - it is no authority, and the lot behaves exactly as it
        /// does under <see cref="VMTS1GlobalLinkStub"/>.
        /// </para>
        /// <para>
        /// Public and static so the rule can be asserted with no <c>VM</c>, a content manager or a
        /// graphics device, which is the only place the whole of B5's family rule can be tested at
        /// all. A family that cannot be inspected at all is accepted, because a refusal the link
        /// cannot justify is worse than the answer it was given.
        /// </para>
        /// </remarks>
        /// <param name="familyNumber">The avatar's <c>TS1FamilyNumber</c>.</param>
        /// <param name="family">The family the resolver answered with, or <c>null</c>.</param>
        public static bool IsTheFamilyAskedFor(short familyNumber, FAMI family)
        {
            if (family == null) return false;
            if (familyNumber < 0) return false; //no family is numbered below zero
            try
            {
                return family.ChunkID == (ushort)familyNumber;
            }
            catch (Exception)
            {
                //a family that will not answer the question is taken at its word rather than
                //silently becoming Maxis, who pays for everything
                return true;
            }
        }

        /// <summary>
        /// The lot's own current family, or <c>null</c> for a lot with no TS1 platform state.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        public FAMI CurrentFamily(VM vm)
        {
            if (vm == null) return null;
            try
            {
                return vm.TS1State?.CurrentFamily;
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not read the lot's current family.", e);
                return null;
            }
        }

        /// <summary>
        /// This entity's budget, or <see cref="InfiniteBudget"/> for Maxis.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="ent">The entity, or <c>null</c>.</param>
        public uint GetBudgetForFamily(VM vm, VMEntity ent)
        {
            //todo: make this get the appropriate family
            //if multiple families are playable in the same lot.
            //Partly done, and the seam is now named: the family is resolved through the
            //constructor's Func<short, FAMI> keyed on the avatar's own family number, rather
            //than from whichever family the lot happens to have activated. What is still open is
            //the second half - a lot with two playable families has to decide which of them pays
            //for a shared object, and TS1 has no answer to offer, so that question is still a
            //question and not a rule.
            var avatar = ent as VMAvatar;
            if (avatar == null) return InfiniteBudget; //maxis has infinite money
            var family = ResolveFamily(vm, ent);
            return GetBudgetForFamily(family);
        }

        /// <summary>
        /// This family's budget, or <see cref="InfiniteBudget"/> for a family this link cannot
        /// find.
        /// </summary>
        /// <param name="family">The family, or <c>null</c>.</param>
        public uint GetBudgetForFamily(FAMI family)
        {
            //the stub's cast, kept deliberately: a FAMI budget that somehow reads negative
            //becomes a very large number rather than an exception, and a transaction against it
            //is refused instead of the lot crashing.
            return (uint?)family?.Budget ?? InfiniteBudget;
        }

        /// <summary>
        /// Move <paramref name="delta"/> into this entity's family budget, or report that it
        /// cannot be done. Always <c>true</c> for a party that is not a family.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="ent">The entity, or <c>null</c>.</param>
        /// <param name="delta">Simoleons to add; negative to spend.</param>
        public bool TransactBudgetForFamily(VM vm, VMEntity ent, int delta)
        {
            //todo: make this get the appropriate family
            //if multiple families are playable in the same lot.
            //Same seam and the same remaining question as GetBudgetForFamily: the resolver
            //names which family, the rule for a shared object is still unwritten.
            var avatar = ent as VMAvatar;
            if (avatar == null) return true; //maxis has infinite money
            return TransactBudgetForFamily(ResolveFamily(vm, ent), delta);
        }

        /// <summary>
        /// Move <paramref name="delta"/> into this family's budget. A family that would go below
        /// zero refuses, and refusing is what every unpaid-for build, purchase and architecture
        /// command is built on. <c>true</c> for a family this link cannot find, which is
        /// Maxis's answer.
        /// </summary>
        /// <param name="family">The family, or <c>null</c>.</param>
        /// <param name="delta">Simoleons to add; negative to spend.</param>
        public bool TransactBudgetForFamily(FAMI family, int delta)
        {
            if (family == null) return true; //maxis has infinite money
            if (family.Budget + delta < 0) return false;
            family.Budget += delta;
            return true;
        }

        /// <summary>
        /// Whether this entity's family could afford <paramref name="delta"/>, without moving
        /// anything. The check the transaction path runs first, and the check a build/buy gate
        /// asks before it has done any work.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="ent">The entity, or <c>null</c>.</param>
        /// <param name="delta">Simoleons that would be added; negative to spend.</param>
        public bool CanTransactBudgetForFamily(VM vm, VMEntity ent, int delta)
        {
            //todo: make this get the appropriate family
            //if multiple families are playable in the same lot.
            //Third copy of the same seam, and the same open question: naming the family is
            //solved, deciding between two of them is not.
            var avatar = ent as VMAvatar;
            if (avatar == null) return true; //maxis has infinite money
            return CanTransactBudgetForFamily(ResolveFamily(vm, ent), delta);
        }

        /// <summary>
        /// Whether this family could afford <paramref name="delta"/>, without moving anything.
        /// </summary>
        /// <param name="family">The family, or <c>null</c>.</param>
        /// <param name="delta">Simoleons that would be added; negative to spend.</param>
        public bool CanTransactBudgetForFamily(FAMI family, int delta)
        {
            if (family == null) return true; //maxis has infinite money
            return family.Budget + delta >= 0;
        }

        // ---- transactions ---------------------------------------------------------------------------

        /// <summary>
        /// The stub's local transaction: can both parties afford it, and if this is not a test,
        /// move the money. No server, no queue, no waiting - the offline answer.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="testOnly">Whether to answer without moving any money.</param>
        /// <param name="uid1">The payer.</param>
        /// <param name="uid2">The payee.</param>
        /// <param name="amount">Simoleons to move.</param>
        /// <returns>Whether the transaction happened (or would have).</returns>
        public bool PerformTransaction(VM vm, bool testOnly, uint uid1, uint uid2, int amount)
        {
            // max value ID is "maxis"

            VMEntity obj1 = null;
            VMEntity obj2 = null;
            try
            {
                obj1 = vm?.GetObjectByPersist(uid1);
                obj2 = vm?.GetObjectByPersist(uid2);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not look up the parties to a"
                    + " transaction; treating both as Maxis.", e);
            }

            // fail if the target is an object and they're not on lot
            if (obj1 != null && !CanTransactBudgetForFamily(vm, obj1, -amount)) return false;
            if (obj2 != null && !CanTransactBudgetForFamily(vm, obj2, amount)) return false;

            if (!testOnly)
            {
                if (obj1 != null) TransactBudgetForFamily(vm, obj1, -amount);
                if (obj2 != null) TransactBudgetForFamily(vm, obj2, amount);
            }
            return true;
        }

        /// <summary>
        /// A transaction, answered to the VM thread and to the caller's callback.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Inline, on the calling thread.</b> The TS1 stub does this work on a
        /// <c>new System.Threading.Thread()</c>, so both the command that unblocks a waiting
        /// SimAntics thread and the caller's callback run off the thread that owns the
        /// <c>VM</c>. Everything here happens before this method returns, on the thread that
        /// called it.
        /// </para>
        ///
        /// <para>
        /// <b>The response names the caller's own thread.</b> The interface declares
        /// <c>(..., short type, short thread, ...)</c> and <c>VMTransferFunds</c> passes
        /// <c>((short)type, caller.ObjectID)</c>; the TS1 stub's implementation of this same
        /// signature names its sixth parameter <c>thread</c>, so the response it sends names the
        /// expense type where the object id was meant. The parameter this type uses is the one
        /// the interface calls <c>thread</c>, which is the one that releases the thread that is
        /// actually waiting.
        /// </para>
        ///
        /// <para>
        /// <b>The response is sent whether or not a callback was supplied.</b> The stub nests
        /// both inside <c>if (callback != null)</c>, so a caller with no callback leaves its
        /// waiting thread blocked for ever. Every caller in the engine passes one, so this has
        /// never been visible; sending the response unconditionally is the same command in the
        /// case that matters and the only correct one in the case that does not.
        /// </para>
        /// </remarks>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="testOnly">Whether to answer without moving any money.</param>
        /// <param name="uid1">The payer.</param>
        /// <param name="uid2">The payee.</param>
        /// <param name="amount">Simoleons to move.</param>
        /// <param name="type">The expense type, for the caller that wants it.</param>
        /// <param name="thread">The calling thread's object id, to release when it is waiting.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void PerformTransaction(VM vm, bool testOnly, uint uid1, uint uid2, int amount,
            short type, short thread, VMAsyncTransactionCallback callback)
        {
            var result = PerformTransaction(vm, testOnly, uid1, uid2, amount);

            VMEntity obj1 = null;
            VMEntity obj2 = null;
            try
            {
                obj1 = vm?.GetObjectByPersist(uid1);
                obj2 = vm?.GetObjectByPersist(uid2);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not read the budgets to report after"
                    + " a transaction; reporting zero for both parties.", e);
            }

            var finalAmount = amount;
            var budget1 = obj1 == null ? 0 : GetBudgetForFamily(vm, obj1);
            var budget2 = obj2 == null ? 0 : GetBudgetForFamily(vm, obj2);

            //update client side budgets for avatars involved.
            if (vm != null)
            {
                try
                {
                    vm.SendCommand(new VMNetAsyncResponseCmd(thread, new VMTransferFundsState
                    {
                        Responded = true,
                        Success = result,
                        TransferAmount = finalAmount,
                        UID1 = uid1,
                        Budget1 = budget1,
                        UID2 = uid2,
                        Budget2 = budget2
                    }));
                }
                catch (Exception e)
                {
                    SimitoneLog.Warn("SimitoneGlobalLink: could not send a transaction response"
                        + " for thread " + thread + ".", e);
                }
            }

            if (callback == null) return;
            try
            {
                callback(result, finalAmount, uid1, budget1, uid2, budget2);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: a transaction callback threw; the transaction"
                    + " itself is already done.", e);
            }
        }

        /// <summary>A transaction with an expense type and no thread to release.</summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="testOnly">Whether to answer without moving any money.</param>
        /// <param name="uid1">The payer.</param>
        /// <param name="uid2">The payee.</param>
        /// <param name="amount">Simoleons to move.</param>
        /// <param name="type">The expense type, for the caller that wants it.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void PerformTransaction(VM vm, bool testOnly, uint uid1, uint uid2, int amount,
            short type, VMAsyncTransactionCallback callback)
        {
            PerformTransaction(vm, testOnly, uid1, uid2, amount, type, 0, callback);
        }

        /// <summary>A transaction with neither an expense type nor a thread to release.</summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="testOnly">Whether to answer without moving any money.</param>
        /// <param name="uid1">The payer.</param>
        /// <param name="uid2">The payee.</param>
        /// <param name="amount">Simoleons to move.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void PerformTransaction(VM vm, bool testOnly, uint uid1, uint uid2, int amount,
            VMAsyncTransactionCallback callback)
        {
            PerformTransaction(vm, testOnly, uid1, uid2, amount, 0, 0, callback);
        }

        // ---- architecture ---------------------------------------------------------------------------

        /// <summary>
        /// Hold an architecture command until <see cref="Tick"/> has a moment for it. A
        /// <c>null</c> command is dropped rather than queued: the queue drains on the game
        /// thread and a null in it could only be a dequeue that then dereferences nothing.
        /// </summary>
        /// <param name="cmd">The command.</param>
        public void QueueArchitecture(VMNetArchitectureCmd cmd)
        {
            if (cmd == null) return;
            lock (_archBuffer)
            {
                _archBuffer.Enqueue(cmd);
            }
        }

        /// <summary>
        /// Drain the architecture queue, verifying each command locally and forwarding it only
        /// if the lot can pay for it.
        ///
        /// <para>
        /// The stub's semantics, unchanged: simulate, and if the cost is zero forward it as
        /// verified immediately; if it is not, hold the whole queue
        /// (<see cref="WaitingOnArchitecture"/>) until a transaction for the cost comes back,
        /// and forward only on success. One command's charge therefore blocks the rest of the
        /// queue for the one tick the transaction takes, which is the back-pressure that stops a
        /// lot from committing builds it cannot pay for.
        /// </para>
        ///
        /// <para>
        /// Two deliberate narrowings of the stub, both about the callback having moved onto this
        /// thread. A charge that <em>fails</em> releases the queue and drops the command, as it
        /// always did - a build the family cannot pay for is not retried, and the object the
        /// player placed is charged for by the next thing they try. And if the charge somehow
        /// never answers, the hold is released here rather than left set for ever, because
        /// nothing else in the game can clear it.
        /// </para>
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        public void Tick(VM vm)
        {
            if (vm == null) return;
            lock (_archBuffer)
            {
                while (!_waitingOnArch && _archBuffer.Count > 0)
                {
                    var cmd = _archBuffer.Dequeue();
                    if (cmd == null) continue;

                    int cost;
                    try
                    {
                        cost = vm.Context.Architecture.SimulateCommands(cmd.Commands, false);
                    }
                    catch (Exception e)
                    {
                        //The command is dropped rather than verified, so a placement the lot cannot
                        //answer costs the player that placement and nothing else.
                        //
                        //<b>Dropping it is only safe because the lot is put back.</b>
                        //SimulateCommands swaps the lot's real wall/floor/height planes for the
                        //preview ones and leaves RealMode false while it simulates, so a throw out of
                        //it used to leave the live lot permanently in preview state - the build grid
                        //drawing a stale copy, placements committing into buffers nothing reads, and
                        //objects left occupied - and this catch is exactly what turned that from a
                        //crash into a silent wedge. VMArchitecture.SimulateCommands now unwinds in a
                        //finally. The assertion below is the tripwire for that: it cannot fix the
                        //wedge, but it says in the log that the lot is in it rather than leaving the
                        //player to work it out from a grid that stopped accepting clicks.
                        SimitoneLog.Warn("SimitoneGlobalLink: could not simulate a queued"
                            + " architecture command; it is dropped rather than verified."
                            + DescribeSimulationState(vm), e);
                        continue;
                    }

                    if (cost == 0)
                    {
                        //just send it
                        cmd.Verified = true;
                        ForwardArchitecture(vm, cmd);
                        continue;
                    }

                    uint source, target;
                    if (cost > 0) { source = cmd.ActorUID; target = uint.MaxValue; }
                    else { source = uint.MaxValue; target = cmd.ActorUID; }

                    //Math.Abs(int.MinValue) throws, and a command that costs exactly that much
                    //is not more reason to crash the lot than one that costs a lot.
                    var charge = cost == int.MinValue ? int.MaxValue : Math.Abs(cost);

                    var answered = false;
                    _waitingOnArch = true;
                    PerformTransaction(vm, false, source, target, charge,
                        (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) =>
                        {
                            //inline, on this thread, inside this lock - which is reentrant, so
                            //the stub's cross-thread hand-off is not needed to avoid a deadlock.
                            answered = true;
                            _waitingOnArch = false;
                            if (!success) return;
                            cmd.Verified = true;
                            ForwardArchitecture(vm, cmd);
                        });
                    if (!answered) _waitingOnArch = false;
                }
            }
        }

        private static void ForwardArchitecture(VM vm, VMNetArchitectureCmd cmd)
        {
            try
            {
                vm.ForwardCommand(cmd);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not forward a verified architecture"
                    + " command; it is dropped rather than requeued.", e);
            }
        }

        /// <summary>
        /// Whether this lot's architecture came back out of the simulation preview it is put into
        /// for every placement, and the clause a log line carries when it did not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The whole rule, with no lot in it.</b> <c>RealMode</c> is the flag the engine itself
        /// keeps: <c>VMArchitecture.SimulateCommands</c> clears it while it runs a command list and
        /// sets it on the way out, and <c>RunCommands</c> reads it to decide whether a command is a
        /// preview or a commit. A lot whose simulation threw before its restore is the one shape of
        /// dead build/buy that no amount of player input recovers - the build grid draws the preview
        /// copy, every placement the player tries commits into arrays the blueprint never reads, and
        /// objects mid-interaction keep their <c>Occupied</c> bit, which is what makes the pie menu
        /// refuse them. So the question "did the lot come back?" is a question about one bool, and it
        /// is asked here rather than left to a log line about a grid that stopped working.
        /// </para>
        /// <para>
        /// True is the healthy answer and the one every passing test sees; the clause is what a
        /// failing one says, because "and the lot's architecture is still in preview" is the sentence
        /// that turns an hour of bug-hunting into one line.
        /// </para>
        /// </remarks>
        /// <param name="realMode">Whether the architecture is out of its preview state.</param>
        public static bool LotIsOutOfSimulationPreview(bool realMode)
        {
            return realMode;
        }

        /// <summary>The clause a log line carries, and nothing else, so it can be concatenated.</summary>
        /// <param name="realMode">Whether the architecture is out of its preview state.</param>
        public static string DescribeSimulationState(bool realMode)
        {
            return LotIsOutOfSimulationPreview(realMode)
                ? string.Empty
                : ", and the lot's architecture is still in its simulation preview, so build/buy"
                    + " and placement will not work until the lot is reloaded";
        }

        private static string DescribeSimulationState(VM vm)
        {
            try
            {
                var architecture = vm?.Context?.Architecture;
                if (architecture == null) return string.Empty;
                return DescribeSimulationState(architecture.RealMode);
            }
            catch (Exception)
            {
                //A diagnostic must never be the thing that breaks the frame it is describing.
                return string.Empty;
            }
        }

        // ---- leaving a lot --------------------------------------------------------------------------

        /// <summary>
        /// Which of the two leave-lot behaviours this avatar gets. The whole of the B5 rule, over
        /// two numbers.
        /// </summary>
        /// <remarks>
        /// A persist id of zero means "this avatar is not a networked sim", and a
        /// <c>MyUID</c> of zero means the lot has no client at all. Comparing those two for
        /// equality would call the local player a visitor and strand them, so the transition
        /// branch requires a real persist id. Nothing in the engine can reach that case, and the
        /// narrowing is in the safe direction: a sim nobody can identify is not a sim the
        /// transition path should move.
        /// </remarks>
        /// <param name="avatarPersistId">The leaving avatar's persist id.</param>
        /// <param name="localPersistId">The lot's own <c>MyUID</c>.</param>
        public static SimitoneLotLeaveAction PlanLeaveLot(uint avatarPersistId, uint localPersistId)
        {
            if (avatarPersistId == 0 || avatarPersistId != localPersistId) return SimitoneLotLeaveAction.DepartLot;
            return SimitoneLotLeaveAction.TransitionToLot;
        }

        /// <summary>
        /// The lot number to send a leaving player to, given the family authority's idea of
        /// where home is. Anything that is not a lot number a switch can act on - no family, a
        /// family with no house yet, a house number outside the lot range - is
        /// <see cref="GoHomeLot"/>, which the screen resolves itself.
        ///
        /// <para>
        /// <b>The range test is load-bearing, not tidiness.</b> "Positive" was the whole test
        /// here, and a family's <c>FAMI.HouseNumber</c> is not constrained to being a loadable
        /// lot: 99 is the lot-location parser's own "no lot" sentinel, and a family whose data
        /// has been hand-edited or migrated can carry anything. Naming such a number here used
        /// to hand the screen a destination it could not act on, and with
        /// <see cref="NeighbourhoodWorldFlag.Travel"/> on the refusal came back from
        /// <see cref="LotTravelService.TryTransition"/> <em>after</em>
        /// <c>TS1GameScreen.VMLotSwitch</c> had already paused the lot - so a lot that was never
        /// switched stayed at <c>SpeedMultiplier == 0</c> for the rest of the session: frozen
        /// clock, no autonomous sims, and every script that was mid-way through serving a meal
        /// stranded with the object's <c>Occupied</c> bit still set, which is what makes the pie
        /// menu then refuse every interaction on it. Asking for "go home" instead reaches
        /// <c>VMLotSwitch</c>'s own <c>SwitchLot &lt;= 0</c> guard, which restores the speed.
        /// </para>
        /// </summary>
        /// <param name="familyHomeLot">The family's house number, if it has one.</param>
        public static int SelectLeaveDestination(int? familyHomeLot)
        {
            if (familyHomeLot.HasValue && LotTravelService.IsLotNumber(familyHomeLot.Value))
                return familyHomeLot.Value;
            return GoHomeLot;
        }

        /// <summary>
        /// This link's answer to "where does the player go when they leave": the house of the
        /// family this lot has activated, or <see cref="GoHomeLot"/>.
        ///
        /// <para>
        /// The <c>NeighbourhoodWorldServices</c> flag gates the lookup. With the milestone off
        /// the link declines to name a destination and asks the screen to resolve
        /// <see cref="GoHomeLot"/> - which is what the pre-B5 behaviour was and what a screen
        /// with no world behind it can already do. With it on, the family it resolves is the
        /// lot's own current one, i.e. the one whose money the lot is charging.
        /// </para>
        ///
        /// <para>
        /// The lot the player is <em>on</em> is deliberately not consulted. It is the wrong
        /// answer to the question - it is where they are going <em>from</em> - and
        /// <c>TS1GameScreen.Update</c> would read it as "switch to the lot I am in", stash the
        /// lot it is already in, and load it again.
        /// </para>
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        public int ResolveLeaveDestination(VM vm)
        {
            if (!_worldServices) return GoHomeLot;
            var family = CurrentFamily(vm);
            if (family == null) return GoHomeLot;
            int houseNumber;
            try
            {
                houseNumber = family.HouseNumber;
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not read the family's house number"
                    + " for a leave-lot destination; asking the screen to go home.", e);
                return GoHomeLot;
            }
            return SelectLeaveDestination(houseNumber);
        }

        /// <summary>
        /// Ask for a lot transition: tell the injected hook, then raise the VM's existing
        /// lot-switch signal.
        ///
        /// <para>
        /// The hook goes first so the world has the hop recorded before the screen starts the
        /// stash-then-load; a crash in the middle of a switch then leaves a world that knows a
        /// hop was asked for. The signal is what actually moves the player -
        /// <c>TS1GameScreen</c> subscribes <c>VMLotSwitch</c> to it, which sets
        /// <c>SwitchLot</c>, which <c>Update</c> acts on after the tick that requested it. A
        /// <c>GoHomeLot</c> destination arrives there as <c>0xFFFFFFFF</c> and the screen's own
        /// <c>(short)lotId == -1</c> test turns it into the player's own house.
        /// </para>
        /// </summary>
        /// <param name="vm">The lot to signal, or <c>null</c> when there is not one.</param>
        /// <param name="destination">The lot number, or <see cref="GoHomeLot"/>.</param>
        public void RequestLotTransition(VM vm, int destination)
        {
            if (_lotTransition != null)
            {
                try
                {
                    _lotTransition(destination);
                }
                catch (Exception e)
                {
                    SimitoneLog.Warn("SimitoneGlobalLink: the lot-transition hook threw for lot "
                        + destination + "; the VM signal is still raised.", e);
                }
            }
            if (vm == null) return;
            try
            {
                vm.SignalLotSwitch((uint)destination);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not raise the lot-switch signal for"
                    + " lot " + destination + ".", e);
            }
        }

        /// <summary>
        /// An avatar is leaving this lot.
        ///
        /// <para>
        /// For anybody but the local player this is the stub's behaviour, unchanged: delete the
        /// avatar and announce the departure. For the local player it is the one thing B5
        /// changes - the stub calls <c>CloseNet(VMCloseNetReason.LeaveLot)</c>, which in single
        /// player shuts the lot's driver down and leaves the player nowhere, whereas here the
        /// existing travel signal is raised so the screen's stash-then-load path runs. The
        /// avatar is deleted either way, after the request, so the lot is left in the state the
        /// switch expects to find it in.
        /// </para>
        ///
        /// <para>
        /// <b>Why the delete stays (decided 2026-11-15, from code, not from a runtime trace).</b>
        /// The deferred switch does <c>vm.Save()</c> after this returns, so the leaving sim is
        /// absent from the stashed lot. That is what "leave" means in the engine's own primitive
        /// (<c>VMGenericTSOCall</c> calls <c>UserLeaveLot</c> and then this: the sim's state is
        /// meant to go to the server, which here is the lot's own save plus the family rebuild
        /// the arrival scripts do), and the stub does the same. Keeping the sim would put it back
        /// in a lot whose arrival path also spawns a controlled sim, which is the two-sims
        /// failure. TS1 scripts do not reach this member at all (they travel through
        /// <c>VMGenericTS1Call</c>'s lot-switch mode, which writes the sim to the inventory
        /// motive first), so the question only matters for a TSO-style leave interaction.
        /// </para>
        ///
        /// <para>
        /// Never throws. A leave is asked for by an interaction running inside a tick, on a lot
        /// that is about to be torn down, and the one thing worse than a player not going home
        /// is an exception escaping into somebody else's scheduler.
        /// </para>
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="avatar">The avatar leaving, or <c>null</c>.</param>
        public void LeaveLot(VM vm, VMAvatar avatar)
        {
            if (avatar == null) return;

            //TODO: in the global server, this will save the avatar (and possibly lot) states
            //and send back to server. Here the lot's own save is the database, and
            //TS1GameScreen.Update writes it as part of the switch the signal below starts.
            uint localUid;
            try
            {
                localUid = vm?.MyUID ?? 0;
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not read MyUID for a leave-lot"
                    + " request; treating the avatar as a visitor.", e);
                localUid = 0;
            }

            if (PlanLeaveLot(avatar.PersistID, localUid) == SimitoneLotLeaveAction.TransitionToLot)
            {
                //never CloseNet: in single player the lot is not a session, it is a place.
                RequestLotTransition(vm, ResolveLeaveDestination(vm));
            }

            //read the name before the delete, because afterwards there may be no group to
            //read it from - and the chat line is the one thing that must still say who left.
            string name = null;
            try
            {
                name = avatar.Name;
            }
            catch (Exception)
            {
                //an avatar with no readable name leaves without one
            }

            try
            {
                avatar.Delete(true, vm?.Context);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not delete a leaving avatar"
                    + (name == null ? "" : " (" + name + ")") + ".", e);
            }

            try
            {
                vm?.SignalChatEvent(new VMChatEvent(avatar, VMChatEventType.Leave, name));
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not announce a leave-lot chat event.", e);
            }
        }

        // ---- neighbours -----------------------------------------------------------------------------

        /// <summary>
        /// Make a sim a roommate, now, by forwarding the permission change as verified. The
        /// stub's behaviour, unchanged: in the final game this signals persistent roommate
        /// state to a city server, and there is no city server, so it does the thing immediately
        /// rather than doing nothing.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="pid">The sim to add.</param>
        /// <param name="mode">The engine's request mode, which the stub ignores.</param>
        /// <param name="permissions">The permissions asked for, which the stub overrides.</param>
        public void RequestRoommate(VM vm, uint pid, int mode, byte permissions)
        {
            if (vm == null) return;
            try
            {
                vm.ForwardCommand(new VMChangePermissionsCmd()
                {
                    TargetUID = pid,
                    Level = VMTSOAvatarPermissions.Roommate,
                    Verified = true
                });
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not make sim " + pid
                    + " a roommate.", e);
            }
        }

        /// <summary>
        /// Drop a roommate, by forwarding the permission change back to visitor. The stub's
        /// behaviour, unchanged, and for the same reason.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="avatar">The roommate leaving, or <c>null</c>.</param>
        public void RemoveRoommate(VM vm, VMAvatar avatar)
        {
            if (vm == null || avatar == null) return;
            try
            {
                vm.ForwardCommand(new VMChangePermissionsCmd()
                {
                    TargetUID = avatar.PersistID,
                    Level = VMTSOAvatarPermissions.Visitor,
                    Verified = true
                });
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not remove sim " + avatar.PersistID
                    + " as a roommate.", e);
            }
        }

        // ---- objects --------------------------------------------------------------------------------

        /// <summary>
        /// The next synthetic persist id, and advance the counter.
        /// </summary>
        /// <remarks>
        /// The offline game's ids come from the engine's own persist space, so a new object
        /// needs one that cannot collide with anything already in the lot. The stub hands out
        /// <c>0x1000000</c> and up from a field anybody can also write; here the counter is
        /// private and readable, so the sequence cannot be edited from outside and a test can
        /// pin the values without a VM.
        /// </remarks>
        public uint AllocatePersistId()
        {
            return _nextPersist++;
        }

        /// <summary>
        /// Give a newly created object a persist id, tell the lot about it, and inform the
        /// caller.
        /// </summary>
        /// <remarks>
        /// The stub sends the update command and never calls the callback, which leaves a caller
        /// that is waiting for the id (every engine caller is) waiting for a lot that is not
        /// going to answer. Here both happen: the command is what makes the id real in the
        /// object's query tables, and the callback is what releases the caller. The two are
        /// idempotent - a caller that resends the same command for the same object id and
        /// persist id changes nothing.
        /// </remarks>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="obj">The object, or <c>null</c>.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void RegisterNewObject(VM vm, VMEntity obj, VMAsyncPersistIDCallback callback)
        {
            if (vm == null || obj == null)
            {
                if (callback != null) SafeCallback(() => callback((short)0, 0), "RegisterNewObject");
                return;
            }

            //todo: sandbox servers should give things an "id"
            var persistId = AllocatePersistId();
            try
            {
                vm.SendCommand(new VMNetUpdatePersistStateCmd()
                {
                    ObjectID = obj.ObjectID,
                    PersistID = persistId
                });
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: could not register persist id "
                    + persistId + " for object " + obj.ObjectID + "; the caller is told anyway.",
                    e);
            }
            if (callback != null)
            {
                var objectId = obj.ObjectID;
                SafeCallback(() => callback(objectId, persistId), "RegisterNewObject");
            }
        }

        /// <summary>
        /// Move a group to its owner's inventory. The stub's answer, unchanged and now inline:
        /// success, and the persist id the object already has, because in single player the
        /// inventory and the lot are the same file and there is nowhere else for it to be.
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="obj">The group, or <c>null</c>.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void MoveToInventory(VM vm, VMMultitileGroup obj, VMAsyncInventorySaveCallback callback)
        {
            //todo: nice stub for this using database?
            var baseObject = obj?.BaseObject;
            var persistId = baseObject == null ? 0u : baseObject.PersistID;
            if (callback != null)
            {
                var success = baseObject != null;
                SafeCallback(() => callback(success, persistId), "MoveToInventory");
            }
        }

        /// <summary>
        /// Delete an object.
        /// </summary>
        /// <remarks>
        /// The stub's body is a comment - <c>//todo: delete local data</c> - and does not even
        /// answer, so a caller waiting on the answer waits for ever. There is no local data
        /// beyond the lot's own <c>House{NN}.iff</c>, and that is written after the VM has
        /// dropped the object, which is why the honest answer here is "it is deleted" either
        /// way: an object this lot no longer holds is an object its save cannot resurrect. The
        /// lookup is done anyway so the log can name what was asked about.
        /// </remarks>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="objectPID">The object's persist id.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void DeleteObject(VM vm, uint objectPID, VMAsyncDeleteObjectCallback callback)
        {
            //todo: delete local data
            VMEntity found = null;
            if (vm != null)
            {
                try
                {
                    found = vm.GetObjectByPersist(objectPID);
                }
                catch (Exception e)
                {
                    SimitoneLog.Warn("SimitoneGlobalLink: could not look up object " + objectPID
                        + " to delete it.", e);
                }
            }
            if (found != null)
            {
                //the command that asked for the deletion removes it; there is no second copy
                //of it anywhere for this link to remove.
                SimitoneLog.Info("SimitoneGlobalLink: object " + objectPID
                    + " is being deleted by the lot; the save drops it with the lot.");
            }
            if (callback != null) SafeCallback(() => callback(true), "DeleteObject");
        }

        // ---- tokens ---------------------------------------------------------------------------------

        /// <summary>
        /// An FSO token request. The stub echoes the request back as the result, unchanged, and
        /// inline rather than on a thread of its own.
        /// </summary>
        /// <remarks>
        /// The request is the answer because TS1 has no token table: a request carries the
        /// defaults it wants, and a response carries the values that were actually stored, and
        /// with nothing to store them in those are the same numbers. A caller that is
        /// <c>GetOrCreate</c> gets its own defaults back, which is what every TS1 primitive
        /// that asks can already cope with.
        /// </remarks>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        /// <param name="avatarID">The requesting sim, unused.</param>
        /// <param name="guid">The token's object, unused.</param>
        /// <param name="mode">The request mode, unused.</param>
        /// <param name="attributeData">The attributes, echoed back.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void TokenRequest(VM vm, uint avatarID, uint guid, VMTokenRequestMode mode,
            List<int> attributeData, VMAsyncTokenCallback callback)
        {
            if (callback == null) return;
            var attributes = attributeData;
            SafeCallback(() => callback(true, attributes), "TokenRequest");
        }

        // ---- the TSO-online surface, unchanged from the stub -----------------------------------------
        //
        // Every member below is TSO-online only: it is about a shard, an account, a shared
        // avatar database or a second player, none of which exists in a single-player
        // neighbourhood. Their bodies are the stub's bodies, so that switching this link in
        // cannot change what any of them does - which is the point of the list. They are named
        // here as a group because a reader who finds one of them wondering "why is this empty"
        // deserves the answer without having to go and look for it.
        //
        // The one thing that is not identical is the guards: each of these used to dereference
        // its arguments unconditionally, so calling one with a null callback threw out of the
        // middle of a tick. The bodies are the same; the arguments are now checked first. Every
        // caller in the engine passes a callback, so nothing observable changed - a test that
        // calls them all with nulls does not get a stack trace instead of silence.
        //
        //   ObtainAvatarFromTicket     - a shard ticket, not a TS1 concept
        //   LoadPluginPersist          - no plugin database; the stub answers null, inline
        //   SavePluginPersist          - nothing to write it to
        //   ForceInInventory           - TS1 has one inventory and the lot owns it
        //   UpdateObjectPersist        - a persist id is a database id, not a TS1 one
        //   PurchaseFromOwner          - a second player with their own database
        //   RetrieveFromInventoryByType- a shared database, answered with an empty restore
        //   RetrieveFromInventory      - as above
        //   ConsumeInventory           - nothing consumes an inventory item by database id
        //   SetSpotlightStatus         - TSO's lot-wide spotlight
        //   StockOutfit / GetOutfits / DeleteOutfit / UpdateOutfitSalePrice / PurchaseOutfit
        //                              - TSO's outfit database
        //   SecureTrade (both)         - a second player, and both would get silence anyway
        //   GetDynPayouts              - the newspaper's data
        //   GetBulletinState           - the bulletin board
        //   GetObjectGlobalCooldown    - a global (shard-wide) cooldown
        //   GetAccountIDFromAvatar     - accounts
        //   FindLotAndValue (both)     - a lot database to search

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="ticket">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void ObtainAvatarFromTicket(VM vm, string ticket, VMAsyncAvatarCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. Answers <c>null</c> inline.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="objectPID">Unused.</param>
        /// <param name="pluginID">Unused.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void LoadPluginPersist(VM vm, uint objectPID, uint pluginID, VMAsyncPluginLoadCallback callback)
        {
            //the stub answers this from a thread of its own; a plugin state is a VM object and
            //the answer belongs on the thread that asked.
            if (callback == null) return;
            SafeCallback(() => callback(null), "LoadPluginPersist");
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="objectPID">Unused.</param>
        /// <param name="pluginID">Unused.</param>
        /// <param name="data">Unused.</param>
        public void SavePluginPersist(VM vm, uint objectPID, uint pluginID, byte[] data)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="objectPID">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void ForceInInventory(VM vm, uint objectPID, VMAsyncInventorySaveCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="obj">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void UpdateObjectPersist(VM vm, VMMultitileGroup obj, VMAsyncInventorySaveCallback callback)
        {
        }

        /// <summary>
        /// TSO-online only; see the list above. The stub answers success with the object's own
        /// persist id, and does so here - except for a group it does not have, which is
        /// reported as a failure rather than as the crash the stub would have produced.
        /// </summary>
        /// <param name="vm">Unused.</param>
        /// <param name="obj">The group, or <c>null</c>.</param>
        /// <param name="purchaserPID">Unused.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        /// <param name="tcallback">Unused - a purchase's funds move through the transaction path.</param>
        public void PurchaseFromOwner(VM vm, VMMultitileGroup obj, uint purchaserPID,
            VMAsyncInventorySaveCallback callback, VMAsyncTransactionCallback tcallback)
        {
            //todo: nice stub for this using database?
            if (callback == null) return;
            var baseObject = obj?.BaseObject;
            var success = baseObject != null;
            var persistId = baseObject == null ? 0u : baseObject.PersistID;
            SafeCallback(() => callback(success, persistId), "PurchaseFromOwner");
        }

        /// <summary>TSO-online only; see the list above. Answers an empty restore, inline.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="ownerPID">Unused.</param>
        /// <param name="guid">Unused.</param>
        /// <param name="index">Unused.</param>
        /// <param name="setOnLot">Unused.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void RetrieveFromInventoryByType(VM vm, uint ownerPID, uint guid, int index, bool setOnLot,
            VMAsyncInventoryRetrieveCallback callback)
        {
            //todo: nice stub for this using database?
            if (callback == null) return;
            SafeCallback(() => callback(new VMInventoryRestoreObject(0, 0, 0)), "RetrieveFromInventoryByType");
        }

        /// <summary>TSO-online only; see the list above. Answers an empty restore, inline.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="objectPID">Unused.</param>
        /// <param name="ownerPID">Unused.</param>
        /// <param name="setOnLot">Unused.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void RetrieveFromInventory(VM vm, uint objectPID, uint ownerPID, bool setOnLot,
            VMAsyncInventoryRetrieveCallback callback)
        {
            //todo: nice stub for this using database?
            if (callback == null) return;
            SafeCallback(() => callback(new VMInventoryRestoreObject(0, 0, 0)), "RetrieveFromInventory");
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="ownerPID">Unused.</param>
        /// <param name="guid">Unused.</param>
        /// <param name="mode">Unused.</param>
        /// <param name="num">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void ConsumeInventory(VM vm, uint ownerPID, uint guid, int mode, short num,
            VMAsyncInventoryConsumeCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="on">Unused.</param>
        public void SetSpotlightStatus(VM vm, bool on)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="outfit">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void StockOutfit(VM vm, VMGLOutfit outfit, VMAsyncStockOutfitCallback callback)
        {
            //todo: local stub?
        }

        /// <summary>TSO-online only; see the list above. Answers an empty set, inline.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="owner">Unused.</param>
        /// <param name="ownerPID">Unused.</param>
        /// <param name="callback">Informed inline, or <c>null</c>.</param>
        public void GetOutfits(VM vm, VMGLOutfitOwner owner, uint ownerPID, VMAsyncGetOutfitsCallback callback)
        {
            if (callback == null) return;
            SafeCallback(() => callback(new VMGLOutfit[0]), "GetOutfits");
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="outfitPID">Unused.</param>
        /// <param name="owner">Unused.</param>
        /// <param name="ownerPID">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void DeleteOutfit(VM vm, uint outfitPID, VMGLOutfitOwner owner, uint ownerPID,
            VMAsyncDeleteOutfitCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="outfitPID">Unused.</param>
        /// <param name="objectPID">Unused.</param>
        /// <param name="newSalePrice">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void UpdateOutfitSalePrice(VM vm, uint outfitPID, uint objectPID, int newSalePrice,
            VMAsyncUpdateOutfitSalePriceCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="outfitPID">Unused.</param>
        /// <param name="objectPID">Unused.</param>
        /// <param name="avatarPID">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void PurchaseOutfit(VM vm, uint outfitPID, uint objectPID, uint avatarPID,
            VMAsyncPurchaseOutfitCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="callback">Unused.</param>
        public void GetDynPayouts(VMAsyncNewspaperCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="p1">Unused.</param>
        /// <param name="p2">Unused.</param>
        /// <param name="untradableGUIDs">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void SecureTrade(VM vm, VMEODSecureTradePlayer p1, VMEODSecureTradePlayer p2,
            List<uint> untradableGUIDs, VMAsyncSecureTradeCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="p1">Unused.</param>
        /// <param name="p2">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void SecureTrade(VM vm, VMEODSecureTradePlayer p1, VMEODSecureTradePlayer p2,
            VMAsyncSecureTradeCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="persistID">Unused.</param>
        /// <param name="untradableGUIDs">Unused.</param>
        /// <param name="p">Unused.</param>
        public void FindLotAndValue(VM vm, uint persistID, List<uint> untradableGUIDs, VMAsyncFindLotCallback p)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="persistID">Unused.</param>
        /// <param name="p">Unused.</param>
        public void FindLotAndValue(VM vm, uint persistID, VMAsyncFindLotCallback p)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void GetBulletinState(VM vm, VMAsyncBulletinCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="vm">Unused.</param>
        /// <param name="objectGUID">Unused.</param>
        /// <param name="avatarID">Unused.</param>
        /// <param name="userID">Unused.</param>
        /// <param name="cooldownLength">Unused.</param>
        /// <param name="considerAccount">Unused.</param>
        /// <param name="considerCategory">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void GetObjectGlobalCooldown(VM vm, uint objectGUID, uint avatarID, uint userID,
            TimeSpan cooldownLength, bool considerAccount, bool considerCategory,
            VMAsyncGetObjectCooldownCallback callback)
        {
        }

        /// <summary>TSO-online only; see the list above. A no-op.</summary>
        /// <param name="avatarID">Unused.</param>
        /// <param name="callback">Unused.</param>
        public void GetAccountIDFromAvatar(uint avatarID, VMAsyncAccountUserIDFromAvatarCallback callback)
        {
        }

        // ---- helpers --------------------------------------------------------------------------------

        /// <summary>
        /// Run a caller-supplied callback, and log rather than propagate anything it throws.
        /// </summary>
        /// <remarks>
        /// A callback is somebody else's code, called from inside a lot's tick because the
        /// engine asked us a question. Letting its exception escape would take the tick with it,
        /// and the game would report a crash in a primitive that did nothing wrong. The work
        /// itself has already happened by the time a callback runs - a budget is moved, an
        /// object is registered - so swallowing this loses the reporting and nothing else.
        /// </remarks>
        /// <param name="body">The callback to run.</param>
        /// <param name="what">The member that is running it, for the log.</param>
        private static void SafeCallback(Action body, string what)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneGlobalLink: a " + what + " callback threw. The operation"
                    + " it was told about is already done.", e);
            }
        }

        public override string ToString()
        {
            return "simitone global link (" + (_worldServices ? "world services" : "no world services")
                + ", " + PendingArchitectureCommands + " architecture command(s) queued"
                + (_waitingOnArch ? ", waiting on a charge" : "")
                + ")";
        }
    }
}
