using FSO.SimAntics.Marshals.Threads;
using System;
using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The money half of moving in and out of a lot.
    ///
    /// Moving in is not free: <c>TS1NeighbourProvider.SetFamilyForHouse(house, family, buy:
    /// true)</c> charges the lot's SIMI purchase value against the family's budget. Evicting
    /// only ever handed back <c>FAMI.ValueInArch</c>, the value of the furniture the family
    /// owned, and never the purchase price they had already paid for the lot. Every move in and
    /// out therefore destroyed a lot's worth of simoleons, and a family that moved house a few
    /// times went broke for no visible reason.
    ///
    /// Both halves of the corrected transaction live here so the arithmetic is testable without
    /// a neighbourhood, a FAMI chunk or a game session: <see cref="RefundOnEvict"/> returns the
    /// lot price alongside the furniture value, and <see cref="CanAffordMoveIn"/> is the matching
    /// "can this family pay the purchase price at all" check.
    ///
    /// <see cref="LotEviction"/> is the other half of an eviction, in the same file because it is
    /// the same decision seen from the other side: the money a family gets back, and the lot they
    /// are leaving behind.
    /// </summary>
    public static class LotAccounting
    {
        /// <summary>
        /// Computes a family's budget after being evicted from a lot: the furniture value they
        /// had banked plus the purchase price they were charged on move-in, added to their
        /// current budget.
        /// </summary>
        /// <param name="familyBudget">The family's current budget (FAMI.Budget).</param>
        /// <param name="valueInArch">The value the family had put into the lot (FAMI.ValueInArch).
        /// This is zeroed by the caller after the refund.</param>
        /// <param name="lotPurchaseValue">The lot's SIMI purchase value, i.e. what move-in
        /// charged. A lot with no SIMI chunk reports 0, in which case this is the old behaviour
        /// of refunding furniture value only.</param>
        /// <returns>
        /// The new budget, saturated at <see cref="int.MaxValue"/> / <see cref="int.MinValue"/>
        /// so an absurd purchase value or a corrupted chunk cannot silently wrap the budget
        /// negative.
        /// </returns>
        public static int RefundOnEvict(int familyBudget, int valueInArch, int lotPurchaseValue)
        {
            //summed as long so three overflowing ints cannot wrap before the clamp
            long total = (long)familyBudget + valueInArch + lotPurchaseValue;
            if (total > int.MaxValue) return int.MaxValue;
            if (total < int.MinValue) return int.MinValue;
            return (int)total;
        }

        /// <summary>
        /// Returns true when a family can pay a lot's purchase price out of its budget.
        /// </summary>
        /// <param name="familyBudget">The family's current budget (FAMI.Budget).</param>
        /// <param name="lotPurchaseValue">The lot's SIMI purchase value.</param>
        /// <returns>
        /// <c>true</c> when the budget covers the price exactly or with room to spare. A
        /// negative price (an unpriced or corrupt lot) is always affordable, and a zero budget
        /// always clears a zero price.
        /// </returns>
        public static bool CanAffordMoveIn(int familyBudget, int lotPurchaseValue)
        {
            return familyBudget >= lotPurchaseValue;
        }
    }

    /// <summary>
    /// What a lot keeps when a family is evicted from it.
    ///
    /// <para>
    /// Refunding the money was only half of an eviction. The lot's own file - the
    /// <c>House{NN}.iff</c> the game loads the next family from - was left exactly as the evicted
    /// family left it, so moving out of a lot did not actually move anyone out of the house: the
    /// next family to pick the lot got the previous family's furniture, the previous family's
    /// objects value in the lot price, and the previous family's leftovers in the lot thumbnail.
    /// This class is the decision half of the fix: for every object in the saved lot it says
    /// whether the object goes, stays, or stays-but-is-wiped.
    /// </para>
    ///
    /// <para>
    /// Three outcomes, and the third is the one that is easy to get wrong:
    /// <list type="bullet">
    /// <item><b>Remove</b> - the family is gone, so its Sims and everything it bought go with it.
    /// "Everything it bought" means the buy-mode catalog: <c>OBJD.FunctionFlags &gt; 0</c> marks an
    /// object as appearing in the buy catalog, and that is what separates a sofa from a door.</item>
    /// <item><b>Preserve</b> - the lot's own fabric. Walls and floors live in the architecture, not
    /// in the entity list, but doors, windows, stairs, portals and spawn points are ordinary
    /// entities, and so are the mailbox, the bin and the phone. All of those have
    /// <c>FunctionFlags == 0</c> or a build-mode type, and are kept by the filter without having to
    /// be listed. The exceptions are
    /// <see cref="IsEssentialObject"/>: a handful of catalog items that are lot infrastructure
    /// rather than furniture, and which have to be named because the catalog flag cannot tell them
    /// apart.</item>
    /// <item><b>PreserveAndReset</b> - the controller objects (the mailman, the paper deliverer,
    /// the sim who wanders past). They are kept because the lot cannot run without them, and wiped
    /// because their <c>ObjectData</c> carries per-visit state - "already visited" flags and the
    /// like - which must not bleed from the evicted family into the next one's session. Their
    /// threads are blanked for the same reason, which is exactly how a controller looks when it
    /// comes out of a vanilla <c>OBJM</c> load with no <c>EP0</c> ever run on it.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The globals that go with the reset are here too. <c>FSOv</c> lot data carries the lot's
    /// copy of the game globals, and a stale copy is not merely cosmetic: global 20 is the game
    /// edition (which expansion packs are running) and global 17 is the runtime code version, and
    /// both are read while controller trees run. Left as the evicted session left them, the next
    /// family can end up playing a lot that believes no expansion packs are installed.
    /// </para>
    ///
    /// <para>
    /// Everything here is pure: it takes the facts about an object as plain arguments and returns
    /// a decision, so the eviction filter can be pinned by tests with no TS1 install, no
    /// <c>Content</c> and no VM. <see cref="NeighborhoodService.ResetHouse"/> is the part that needs
    /// all three, and it reads its answers from here.
    /// </para>
    /// </summary>
    public static class LotEviction
    {
        /// <summary>
        /// What happens to one object in the lot when the family is evicted.
        /// </summary>
        public enum Disposition
        {
            /// <summary>Drop the object: a Sim, or something the family bought.</summary>
            Remove,

            /// <summary>Keep the object and its state.</summary>
            Preserve,

            /// <summary>
            /// Keep the object but clear the per-visit state it carries, so the next family does
            /// not inherit it.
            /// </summary>
            PreserveAndReset
        }

        /// <summary>
        /// Lot infrastructure that is <em>in</em> the buy catalog and therefore fails the
        /// "is it furniture" test, but is part of the lot rather than part of the family living
        /// there.
        ///
        /// <para>
        /// The mailbox is not a convenience. <c>VMTS1LotState.VerifyFamily</c> places a newly
        /// spawned Sim by looking the mailbox up by GUID - <c>0xEF121974</c> or <c>0x1D95C9B0</c> -
        /// and if it is not there the new family simply never appears: the Sims are created at
        /// <c>OUT_OF_WORLD</c>, which is invisible and unselectable. The 2-tile mailbox and both
        /// phones are here for the same reason, and the bin because a lot with no bin is a lot the
        /// next family has to pay to fix.
        /// </para>
        ///
        /// <para>
        /// <c>0x39CCF441</c> is the two-tile mailbox, which looks the same but is a different
        /// object, so it is listed separately rather than being assumed to share an ID with the
        /// one-tile version.
        /// </para>
        /// </summary>
        private static readonly HashSet<uint> EssentialObjects = new HashSet<uint>
        {
            0xEF121974u, // Mailbox (primary - the one VerifyFamily looks for)
            0x1D95C9B0u, // Mailbox (alternate)
            0x39CCF441u, // Mailbox (2-tile)
            0xA4258067u, // Trash bin
            0x313D2F9Au, // Phone
            0x303CD603u, // Phone (community lots)
        };

        /// <summary>
        /// Global 3 - the selected Sim. Cleared so the next family does not start with a selection
        /// pointing at a Sim that is no longer on the lot.
        /// </summary>
        private const int SelectedSimGlobal = 3;

        /// <summary>
        /// Global 17 - the runtime code version, read by controller trees as they start up.
        /// </summary>
        private const int RuntimeCodeVersionGlobal = 17;

        /// <summary>
        /// The value <c>VMTS1ActivatorNew.LoadFromIff</c> always writes here: "every expansion
        /// pack". A lot converted from a vanilla IFF has it, and a lot that has been through a
        /// session has whatever the session left behind.
        /// </summary>
        private const int AllExpansionsGameEdition = 255;

        /// <summary>
        /// Global 25 - also always written to 4 by the IFF load, and the value the idle-interaction
        /// code needs.
        /// </summary>
        private const int IdleInteractionsGlobal = 25;

        /// <summary>
        /// The value <c>VMTS1LotState.UpdateSIMI</c> stamps into a SIMI it has just recomputed.
        /// The chunk's writer hardcodes the same number, so a lot left at some other version has a
        /// SIMI whose declared layout and actual layout disagree - which corrupts the item count on
        /// the way back in for an original TS1 lot.
        /// </summary>
        private const uint NormalSimiVersion = 0x3E;

        /// <summary>
        /// Whether <paramref name="guid"/> is lot infrastructure that must survive an eviction even
        /// though it is a buy-catalog object. See <see cref="EssentialObjects"/> for why the
        /// mailbox is not optional.
        /// </summary>
        /// <param name="guid">The object's GUID.</param>
        public static bool IsEssentialObject(uint guid)
        {
            return EssentialObjects.Contains(guid);
        }

        /// <summary>
        /// Decides what happens to one object in the lot being evicted.
        /// </summary>
        /// <remarks>
        /// The order of the tests is the contract, and three of them cannot be swapped:
        /// <list type="number">
        /// <item>an avatar goes regardless of where it is standing, so that a Sim which was parked
        /// out of bounds (or inside a piece of furniture) does not survive on the strength of its
        /// position;</item>
        /// <item>a controller is kept-and-wiped before the furniture test, because a controller is
        /// a catalog object and the furniture test would remove it;</item>
        /// <item>an object content does not know about is removed, not kept. Keeping it would leave
        /// the previous family with objects the game cannot even price, and there is no way to tell
        /// a missing buy-mode object from a missing door.</item>
        /// </list>
        /// </remarks>
        /// <param name="guid">The object's GUID, for the essential-object check.</param>
        /// <param name="isAvatar">Whether the object is a Sim (a <c>VMAvatarMarshal</c>).</param>
        /// <param name="isController">Whether the object's GUID is one of the lot's controller
        /// objects.</param>
        /// <param name="isOutOfWorld">Whether the object is parked at <c>OUT_OF_WORLD</c> rather
        /// than standing on the lot.</param>
        /// <param name="objectIsKnown">Whether its <c>OBJD</c> resolved in content. An unknown
        /// object cannot be classified and is removed.</param>
        /// <param name="buildModeType">The object's <c>OBJD.BuildModeType</c>, or -1 when
        /// unknown.</param>
        /// <param name="functionFlags">The object's <c>OBJD.FunctionFlags</c>, or 0 when
        /// unknown.</param>
        public static Disposition Classify(uint guid, bool isAvatar, bool isController,
            bool isOutOfWorld, bool objectIsKnown, int buildModeType, int functionFlags)
        {
            if (isAvatar) return Disposition.Remove;
            if (isController) return Disposition.PreserveAndReset;

            if (isOutOfWorld) return Disposition.Preserve;
            if (IsEssentialObject(guid)) return Disposition.Preserve;

            //build mode covers doors, windows, stairs and anything else bought in build mode.
            //FunctionFlags == 0 covers the lot's system objects - portals, spawn points, the lot
            //tile marker - none of which are ever in the buy catalog.
            if (objectIsKnown && (buildModeType > 0 || functionFlags == 0)) return Disposition.Preserve;

            return Disposition.Remove;
        }

        /// <summary>
        /// Whether a multitile group survives an eviction. Only if <b>every</b> object in the group
        /// did: a group whose lead tile is gone but whose other tiles are not is a group the VM
        /// will try to restore into a lead object that no longer exists.
        /// </summary>
        /// <param name="groupObjects">The object ids the group is made of. A null or empty group is
        /// kept, because there is nothing in it that could dangle.</param>
        /// <param name="preservedObjectIds">The object ids that survived the filter.</param>
        public static bool KeepsGroup(IReadOnlyList<short> groupObjects, ICollection<short> preservedObjectIds)
        {
            if (groupObjects == null || groupObjects.Count == 0) return true;
            if (preservedObjectIds == null) return false;

            for (int i = 0; i < groupObjects.Count; i++)
            {
                if (!preservedObjectIds.Contains(groupObjects[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// The first object id the lot is allowed to hand out after an eviction.
        ///
        /// <para>
        /// The saved lot carries a running "next free object id" counter, and the ids the old family
        /// used are gone, so the counter is recomputed from what actually survived. It is clamped
        /// rather than allowed to wrap: an increment past <see cref="short.MaxValue"/> would come
        /// out negative and hand the next created object an id that collides with a live one.
        /// </para>
        /// </summary>
        /// <param name="preservedObjectIds">The object ids that survived the filter, or
        /// <c>null</c>.</param>
        /// <returns>A value in 1..<see cref="short.MaxValue"/>. An empty lot starts again at
        /// 1.</returns>
        public static short NextObjectId(IEnumerable<short> preservedObjectIds)
        {
            if (preservedObjectIds == null) return 1;

            short highest = 0;
            foreach (var id in preservedObjectIds)
            {
                if (id > highest) highest = id;
            }

            return highest == short.MaxValue ? short.MaxValue : (short)(highest + 1);
        }

        /// <summary>
        /// The lot's architecture value once the family's furniture is gone: the walls and floors,
        /// plus anything else that was counted into the architecture and is still standing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the shape of the sum <c>VMTS1LotState.UpdateSIMI</c> computes on a live VM, and
        /// it is what makes the bulldozed lot's purchase price - and therefore what the next family
        /// pays - reflect an empty house rather than the last one's furniture.
        /// </para>
        /// <para>
        /// The second term is a parameter rather than being folded in because it is not always zero
        /// in principle: it is the value of the build-mode objects (doors, windows, stairs) that
        /// have been bulldozed along with the furniture. As it happens the eviction filter keeps
        /// every build-mode object, so the caller passes zero - which is the point worth stating,
        /// because it is what lets the architecture value be read straight off the architecture
        /// that is staying instead of being reconstructed.
        /// </para>
        /// </remarks>
        /// <param name="wallsAndFloorsValue">The architecture's own value, from
        /// <c>VMArchitectureStats.GetArchValue</c>.</param>
        /// <param name="preservedBuildValue">The summed price of any build-mode multitile groups
        /// that went with the furniture.</param>
        /// <returns>The sum, saturated rather than wrapped.</returns>
        public static int ArchitectureValueAfterEvict(int wallsAndFloorsValue, int preservedBuildValue)
        {
            long total = (long)wallsAndFloorsValue + preservedBuildValue;
            if (total > int.MaxValue) return int.MaxValue;
            if (total < int.MinValue) return int.MinValue;
            return (int)total;
        }

        /// <summary>
        /// The SIMI version a rebuilt lot's values chunk is stamped with.
        /// </summary>
        /// <remarks>
        /// <c>SIMI.Write</c> hardcodes 0x3E into the file while taking its item count from the
        /// instance's own <c>Version</c>, so a lot left at any other version writes a chunk that
        /// disagrees with itself.
        /// </remarks>
        public static uint NormalizedSimiVersion
        {
            get { return NormalSimiVersion; }
        }

        /// <summary>
        /// The lot globals as a lot that has just been rebuilt should have them.
        /// </summary>
        /// <remarks>
        /// A new array is returned and the input is not touched, so the caller cannot accidentally
        /// keep using globals it believes are still the lot's. Indices past the end of the array are
        /// skipped rather than throwing: a short globals array is a corrupt save, and losing the
        /// eviction is a far worse outcome than leaving one global alone.
        /// </remarks>
        /// <param name="globalState">The lot's saved globals, or <c>null</c>.</param>
        /// <returns>The normalised copy, or <c>null</c> for a <c>null</c> input.</returns>
        public static short[] ResetLotGlobals(short[] globalState)
        {
            if (globalState == null) return null;

            var result = new short[globalState.Length];
            Array.Copy(globalState, result, globalState.Length);

            SetIfPresent(result, SelectedSimGlobal, 0);
            SetIfPresent(result, RuntimeCodeVersionGlobal, 4);
            SetIfPresent(result, 20, AllExpansionsGameEdition);
            SetIfPresent(result, IdleInteractionsGlobal, 4);

            return result;
        }

        /// <summary>
        /// A blank thread for a surviving object, matching what a controller looks like when a
        /// vanilla <c>OBJM</c> load builds it and no <c>EP0</c> has ever run.
        /// </summary>
        /// <remarks>
        /// Every surviving object gets one of these rather than keeping its old thread, so nothing
        /// the evicted family set in motion is still in progress when the next family arrives. The
        /// arrays are sized the way the VM expects (20 temp registers, 2 extended locals) because a
        /// zero-length one is not what a loaded thread looks like.
        /// </remarks>
        public static VMThreadMarshal CreateBlankThread()
        {
            return new VMThreadMarshal
            {
                Stack = new VMStackFrameMarshal[0],
                Queue = new VMQueuedActionMarshal[0],
                ActiveQueueBlock = -1,
                TempRegisters = new short[20],
                TempXL = new int[2],
            };
        }

        private static void SetIfPresent(short[] target, int index, short value)
        {
            if (index >= 0 && index < target.Length) target[index] = value;
        }
    }
}
