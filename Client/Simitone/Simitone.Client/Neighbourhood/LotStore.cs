/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Hollow;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Which lots of a neighbourhood have contents in memory, and which kind of contents.
    ///
    /// <para>
    /// The store behind Part B's second lot state (§5.3). It is the neighbourhood's memory of the
    /// lots it is not currently showing: a lot the player left holds the marshal of its live
    /// <c>VM</c>, a neighbour rendered as a subworld holds a hollow marshal, and a lot that has
    /// never been entered holds nothing at all and is rebuilt from its own <c>HouseNN.iff</c> on
    /// demand. That is exactly <see cref="NeighbourhoodLotState"/>, the enum Part A declared in
    /// §15 for this, used here as the store's own answer rather than as a field on a layout record
    /// - the layout's copy is a persisted snapshot of what the world last did, and this is what
    /// is true right now.
    /// </para>
    ///
    /// <para>
    /// <b>One payload per lot.</b> An entry is a three-way union and never holds two things at
    /// once: a resident lot and a hollow one are different states of the same lot number, so the
    /// second write replaces the first rather than being merged with it. The reason that is
    /// lossless in practice is the order §5.6 already imposes - a lot's contents are committed to
    /// its own <c>HouseNN.iff</c> before the store is asked to remember a different shape for it,
    /// so the marshal being replaced has somewhere else it lives. A store that silently kept both
    /// would instead have to decide, at every read, which of the two a caller meant.
    /// </para>
    ///
    /// <para>
    /// <b>It holds marshals, not VMs.</b> A <see cref="VMMarshal"/> or
    /// <see cref="VMHollowMarshal"/> is data: it can be built, compared and kept without a
    /// graphics device, game content, or a running simulation. That is the whole reason this type
    /// is worth testing on its own - a world that stored live <c>VM</c> objects instead would be
    /// untestable without a content manager, and the states are the part that has to be right.
    /// </para>
    ///
    /// <para>
    /// <b>Pure memory.</b> No IO: <see cref="VMMarshal"/> and <see cref="VMHollowMarshal"/> know how
    /// to serialise themselves to a <c>BinaryWriter</c>, and a store that called them would be a
    /// second persistence path beside the per-lot house save, which §5.6 reserves for that alone.
    /// </para>
    ///
    /// <para>
    /// <b>Thread affinity: the game thread, only.</b> Same rule as the rest of the world (§7 of the
    /// Part B handoff): <c>VM</c> is not thread-safe and the game thread is where a lot is
    /// switched, so this store is read and written there and nowhere else. No locking is
    /// therefore deliberate - one world, one thread, one owner. A store shared across threads
    /// would be a design error this type declines to accommodate rather than a race to be
    /// papered over with a lock that hid the error.
    /// </para>
    /// </summary>
    public sealed class LotStore
    {
        /// <summary>
        /// One lot's stored contents. The two payload fields are mutually exclusive by
        /// construction: every writer below clears the other, so <see cref="StateOf"/> can read the
        /// state off the payload instead of storing a third field that could disagree with it.
        /// </summary>
        private sealed class Entry
        {
            /// <summary>The resident lot's marshalled <c>VM</c>, or <c>null</c>.</summary>
            public VMMarshal Active;

            /// <summary>The hollow lot's marshalled contents, or <c>null</c>.</summary>
            public VMHollowMarshal Hollow;

            public NeighbourhoodLotState State
            {
                get
                {
                    if (Active != null) return NeighbourhoodLotState.Active;
                    if (Hollow != null) return NeighbourhoodLotState.Hollow;
                    return NeighbourhoodLotState.Unloaded;
                }
            }
        }

        private readonly Dictionary<short, Entry> _entries = new Dictionary<short, Entry>();

        /// <summary>
        /// How many lots have contents in memory - hollow ones included, unloaded ones excluded.
        /// </summary>
        public int Count
        {
            get { return _entries.Count; }
        }

        /// <summary>
        /// Every lot that has contents in memory, in ascending lot-number order.
        /// </summary>
        /// <remarks>
        /// A snapshot, taken when the property is read, so a caller iterating it cannot be moved
        /// under itself by a lot switching mid-loop - the same ascending order
        /// <see cref="NeighbourhoodLayout.Lots"/> uses, so the two agree on what "next lot" means.
        /// Unloaded lots are absent rather than listed with a state of their own: there is nothing
        /// for a caller to do about one, and listing them would make the count and the list
        /// disagree.
        /// </remarks>
        public IReadOnlyList<short> LoadedLots
        {
            get
            {
                var lots = new List<short>(_entries.Count);
                foreach (var pair in _entries)
                {
                    //an entry is only in the dictionary while it has a payload, so this cannot
                    //surface an Unloaded lot; the check is kept because that invariant is about
                    //the writers, and this is the read
                    if (pair.Value.State != NeighbourhoodLotState.Unloaded) lots.Add(pair.Key);
                }
                lots.Sort();
                return lots;
            }
        }

        /// <summary>
        /// This lot's contents, if they are resident.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        /// <param name="marshal">
        /// The lot's <see cref="VMMarshal"/> - the same instance that was handed to
        /// <see cref="SetActive"/>, not a copy - or <c>null</c> when the lot is not active.
        /// </param>
        /// <returns>Whether the lot is <see cref="NeighbourhoodLotState.Active"/>.</returns>
        public bool TryGetActive(short lot, out VMMarshal marshal)
        {
            Entry entry;
            if (_entries.TryGetValue(lot, out entry) && entry.Active != null)
            {
                marshal = entry.Active;
                return true;
            }
            marshal = null;
            return false;
        }

        /// <summary>
        /// This lot's contents, if they are present but not resident.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        /// <param name="marshal">
        /// The lot's <see cref="VMHollowMarshal"/> - the same instance that was handed to
        /// <see cref="SetHollow"/> - or <c>null</c> when the lot is not hollow.
        /// </param>
        /// <returns>Whether the lot is <see cref="NeighbourhoodLotState.Hollow"/>.</returns>
        public bool TryGetHollow(short lot, out VMHollowMarshal marshal)
        {
            Entry entry;
            if (_entries.TryGetValue(lot, out entry) && entry.Hollow != null)
            {
                marshal = entry.Hollow;
                return true;
            }
            marshal = null;
            return false;
        }

        /// <summary>
        /// How much of this lot exists in memory: <see cref="NeighbourhoodLotState.Active"/>,
        /// <see cref="NeighbourhoodLotState.Hollow"/>, or
        /// <see cref="NeighbourhoodLotState.Unloaded"/> for a lot that was never entered - which is
        /// every lot, in every neighbourhood, until something is entered.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        public NeighbourhoodLotState StateOf(short lot)
        {
            Entry entry;
            if (_entries.TryGetValue(lot, out entry)) return entry.State;
            return NeighbourhoodLotState.Unloaded;
        }

        /// <summary>
        /// Record a lot as resident, holding <paramref name="marshal"/> - the lot the player has
        /// just left, or is on.
        /// </summary>
        /// <remarks>
        /// Replaces whatever this lot held, including a hollow marshal: a lot cannot be both, and
        /// the marshal being displaced has already been committed to the lot's own file. A
        /// <c>null</c> marshal is a request to forget the lot, and is the same as
        /// <see cref="Remove"/> - a caller that has nothing to hand over should not need a second
        /// way of saying so.
        /// </remarks>
        /// <param name="lot">The lot number.</param>
        /// <param name="marshal">The lot's marshalled <c>VM</c>, or <c>null</c> to remove it.</param>
        public void SetActive(short lot, VMMarshal marshal)
        {
            if (marshal == null)
            {
                Remove(lot);
                return;
            }
            Entry entry;
            if (!_entries.TryGetValue(lot, out entry))
            {
                entry = new Entry();
                _entries.Add(lot, entry);
            }
            entry.Active = marshal;
            entry.Hollow = null;
        }

        /// <summary>
        /// Record a lot as present but not resident, holding <paramref name="marshal"/> - a
        /// neighbour drawn as a subworld.
        /// </summary>
        /// <remarks>
        /// Replaces whatever this lot held, for the reasons given on <see cref="SetActive"/>, and
        /// treats a <c>null</c> marshal as <see cref="Remove"/>.
        /// </remarks>
        /// <param name="lot">The lot number.</param>
        /// <param name="marshal">The lot's hollow contents, or <c>null</c> to remove it.</param>
        public void SetHollow(short lot, VMHollowMarshal marshal)
        {
            if (marshal == null)
            {
                Remove(lot);
                return;
            }
            Entry entry;
            if (!_entries.TryGetValue(lot, out entry))
            {
                entry = new Entry();
                _entries.Add(lot, entry);
            }
            entry.Hollow = marshal;
            entry.Active = null;
        }

        /// <summary>
        /// Forget this lot: back to <see cref="NeighbourhoodLotState.Unloaded"/>, contents and all.
        /// This is what a lot switch calls once the house file is saved, and what leaving a
        /// neighbourhood calls on every lot it holds.
        /// </summary>
        /// <remarks>
        /// Removing a lot that is not there is not an error - the caller asking for an empty store
        /// is the same situation as the caller that already has one, and neither is a bug worth a
        /// log line.
        /// </remarks>
        /// <param name="lot">The lot number.</param>
        public void Remove(short lot)
        {
            _entries.Remove(lot);
        }

        /// <summary>Forget every lot. What leaving a neighbourhood does to the world it leaves.</summary>
        public void Clear()
        {
            _entries.Clear();
        }

        public override string ToString()
        {
            var counts = new int[3];
            foreach (var pair in _entries)
            {
                counts[(int)pair.Value.State]++;
            }
            return "lots " + counts[(int)NeighbourhoodLotState.Active] + " active, "
                + counts[(int)NeighbourhoodLotState.Hollow] + " hollow";
        }
    }
}
