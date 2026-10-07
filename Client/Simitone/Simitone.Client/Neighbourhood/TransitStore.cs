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
    /// Who is between two lots right now - the neighbourhood's half of §5.4's transit table.
    ///
    /// <para>
    /// A sim travelling is a fact about the <em>neighbourhood</em>, not about either of the lots it
    /// is leaving and arriving at: neither lot can answer "is that Sim on their way to me", and a
    /// lot switch is exactly the window in which neither lot is running. So the world keeps the
    /// hop, keyed by Sim, and the lot the player is standing in is just the other end of one. The
    /// shape of a hop - <c>{simGUID, origin, destination, path?, progress?, mode}</c> - is
    /// <see cref="NeighbourhoodTransitRecord"/>'s, because that is the type the sidecar already
    /// persists (B7, schema 2); this store is the in-memory half of the same five facts, and the
    /// same reason the path is absent: the mode owns it, not the record.
    /// </para>
    ///
    /// <para>
    /// <b>One hop per Sim.</b> The key is the Sim, and a Sim is either in one hop or in none: a
    /// second <see cref="Add"/> for a Sim that is already travelling replaces the first rather than
    /// queueing behind it, because "where is this Sim" cannot have two answers and a store that
    /// answered with a list would push that choice onto every reader. Replacing is a real operation
    /// here - B3's discrete travel restarts a hop when the player changes destination - and the
    /// caller's <see cref="TryGet"/> result is the one that changed.
    /// </para>
    ///
    /// <para>
    /// <b>Start order is the enumeration order.</b> <see cref="Active"/> is in the order hops were
    /// started, and an update or a completion never reorders anything. That is the same order
    /// <see cref="NeighbourhoodWorldState.Transit"/> is documented to hold ("in the order they
    /// were started"), which is what makes the round-trip through a sidecar an order-preserving
    /// equality rather than a set comparison. <see cref="Sims"/> is the exception and is ascending,
    /// for the "which Sims are in transit" question that does not care about order.
    /// </para>
    ///
    /// <para>
    /// <b>An arrived hop is still here.</b> <see cref="Complete"/> sets the progress to
    /// <see cref="NeighbourhoodTransitRecord.MaxProgress"/> and leaves the record alone; removing it
    /// is <see cref="Remove"/>, because "finish this hop" and "start the next one" are different
    /// work and a caller that is only resuming (§B7) wants to see a 100% record rather than a
    /// missing one. "Is anyone in transit right now" is a different question, answered by
    /// <see cref="InTransit"/> and, identically, by
    /// <see cref="NeighbourhoodWorldState.InTransit"/> on the saved copy.
    /// </para>
    ///
    /// <para>
    /// <b>Pure.</b> No IO and no VM: the store holds the same five integers the sidecar holds, so it
    /// can be built, driven and asserted on with no content manager, no graphics device and no
    /// running simulation - which is the reason it is a type of its own rather than three fields
    /// on the world. <see cref="ToRecords"/> and <see cref="WriteTo"/> hand out <em>copies</em>, so
    /// nothing a caller does to a persisted record can reach back into live transit state.
    /// </para>
    ///
    /// <para>
    /// <b>Thread affinity: the game thread, only.</b> Same rule as <see cref="LotStore"/> and
    /// <see cref="WorldClock"/> (§7 of the Part B handoff): one world, one owner, one writer, and
    /// no locking. A store shared across threads would be a design error, not a race to paper over.
    /// </para>
    /// </summary>
    public sealed class TransitStore
    {
        /// <summary>The hops in the order they were started.</summary>
        private readonly List<NeighbourhoodTransitRecord> _hops = new List<NeighbourhoodTransitRecord>();

        /// <summary>Sim number to its position in <see cref="_hops"/>. Rebuilt by the writers
        /// that shift positions, which is every removal - removals are rare and n is tiny.</summary>
        private readonly Dictionary<int, int> _positionOf = new Dictionary<int, int>();

        /// <summary>How many hops are recorded, arrived ones included. See the type remarks.</summary>
        public int Count => _hops.Count;

        /// <summary>Whether any recorded hop has not yet arrived.</summary>
        /// <remarks>
        /// The "is the player part-way through a hop" question §B7's resume path asks before it
        /// chooses between finishing a hop and loading a lot. Stated once here for the live store,
        /// exactly as <see cref="NeighbourhoodWorldState.InTransit"/> states it for the saved one,
        /// so the two cannot answer differently about the same set of hops.
        /// </remarks>
        public bool InTransit
        {
            get
            {
                for (int i = 0; i < _hops.Count; i++)
                {
                    if (!_hops[i].IsComplete) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Every recorded hop, in the order they were started. A snapshot, so a caller iterating it
        /// while a Sim arrives cannot be moved under itself; a hop at 100% is included, which is why
        /// <see cref="Count"/> and <see cref="InTransit"/> are separate questions.
        /// </summary>
        public IReadOnlyList<NeighbourhoodTransitRecord> Active => _hops.ToArray();

        /// <summary>
        /// The Sim numbers travelling, ascending. Ordered rather than in start order because this
        /// is the "who is in transit" answer, which is a set; the order-carrying list is
        /// <see cref="Active"/>.
        /// </summary>
        public IReadOnlyList<int> Sims
        {
            get
            {
                var sims = new List<int>(_hops.Count);
                for (int i = 0; i < _hops.Count; i++) sims.Add(_hops[i].Sim);
                sims.Sort();
                return sims;
            }
        }

        /// <summary>Is this Sim in a hop? Whether it has arrived is
        /// <see cref="NeighbourhoodTransitRecord.IsComplete"/>'s question.</summary>
        /// <param name="sim">The Sim number.</param>
        public bool Has(int sim) => _positionOf.ContainsKey(sim);

        /// <summary>
        /// This Sim's hop, or <c>null</c>. The very instance that was added, not a copy - the same
        /// answer <see cref="LotStore.TryGetActive"/> gives for a marshal - so a caller that wants
        /// to record progress does it through <see cref="UpdateProgress"/>, which is where the
        /// bookkeeping lives.
        /// </summary>
        /// <param name="sim">The Sim number.</param>
        /// <param name="hop">The hop, when there is one.</param>
        /// <returns>Whether the Sim is travelling.</returns>
        public bool TryGet(int sim, out NeighbourhoodTransitRecord hop)
        {
            int position;
            if (_positionOf.TryGetValue(sim, out position))
            {
                hop = _hops[position];
                return true;
            }
            hop = null;
            return false;
        }

        /// <summary>
        /// Start a hop, or replace the one this Sim already has. See the type remarks for why the
        /// second case is a replacement.
        /// </summary>
        /// <param name="sim">The Sim that is travelling.</param>
        /// <param name="origin">The lot it left.</param>
        /// <param name="destination">The lot it is going to.</param>
        /// <param name="mode">The crossing's mode code, not interpreted here.</param>
        /// <param name="progress">How far along, in percent. Defaults to "not started", which is
        /// what a discrete hop (B3) wants; a continuous one (B4) may start part-way in.</param>
        /// <returns>The record now held for this Sim - the caller's, if it passed one.</returns>
        public NeighbourhoodTransitRecord Add(int sim, int origin, int destination, int mode,
            int progress = NeighbourhoodTransitRecord.MinProgress)
        {
            return Add(new NeighbourhoodTransitRecord(sim, origin, destination, mode, progress));
        }

        /// <summary>
        /// Record a hop as the caller's own record, replacing any hop this Sim already had. The
        /// instance is stored, not copied, so <see cref="TryGet"/> hands back exactly this object -
        /// which is also why a caller that keeps a reference to it is holding live transit state.
        /// </summary>
        /// <param name="hop">The hop. Required.</param>
        /// <returns>The record now held for this Sim, which is <paramref name="hop"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="hop"/> is null.</exception>
        public NeighbourhoodTransitRecord Add(NeighbourhoodTransitRecord hop)
        {
            if (hop == null) throw new ArgumentNullException(nameof(hop));

            int position;
            if (_positionOf.TryGetValue(hop.Sim, out position))
            {
                //in place, so the start order is the order the hops were first started in and not
                //the order they were last touched
                _hops[position] = hop;
                return hop;
            }

            _positionOf.Add(hop.Sim, _hops.Count);
            _hops.Add(hop);
            return hop;
        }

        /// <summary>
        /// Replace all five fields of a Sim's hop, keeping its position in the start order. A Sim
        /// that is not travelling is not started here: an update that had to create the thing it
        /// updates would make "which Sim moved" unanswerable after the fact.
        /// </summary>
        /// <param name="sim">The Sim whose hop is being rewritten.</param>
        /// <param name="origin">The lot it left.</param>
        /// <param name="destination">The lot it is going to.</param>
        /// <param name="mode">The crossing's mode code.</param>
        /// <param name="progress">How far along, in percent.</param>
        /// <returns>Whether the Sim had a hop to update.</returns>
        public bool Update(int sim, int origin, int destination, int mode, int progress)
        {
            int position;
            if (!_positionOf.TryGetValue(sim, out position)) return false;

            var hop = _hops[position];
            hop.Origin = origin;
            hop.Destination = destination;
            hop.Mode = mode;
            hop.Progress = progress;
            return true;
        }

        /// <summary>
        /// Move a Sim's hop along without touching where it is going - the operation a travelling
        /// Sim does every frame, and the one that must be cheap.
        /// </summary>
        /// <param name="sim">The Sim whose hop is advancing.</param>
        /// <param name="progress">How far along, in percent.</param>
        /// <returns>Whether the Sim had a hop to advance.</returns>
        public bool UpdateProgress(int sim, int progress)
        {
            int position;
            if (!_positionOf.TryGetValue(sim, out position)) return false;

            _hops[position].Progress = progress;
            return true;
        }

        /// <summary>
        /// Mark a Sim's hop as arrived. The record stays, at
        /// <see cref="NeighbourhoodTransitRecord.MaxProgress"/>; retiring it is
        /// <see cref="Remove"/>. See the type remarks for why "arrived" is not "forgotten".
        /// </summary>
        /// <param name="sim">The Sim that has arrived.</param>
        /// <returns>Whether the Sim had a hop to complete.</returns>
        public bool Complete(int sim)
        {
            return UpdateProgress(sim, NeighbourhoodTransitRecord.MaxProgress);
        }

        /// <summary>
        /// Forget a Sim's hop, arrived or not. What B3's arrival path calls once the destination
        /// lot is loaded, and what a player who quit mid-travel calls when the hop is abandoned.
        /// </summary>
        /// <remarks>
        /// Removing a Sim that is not travelling is not an error: the caller that wanted an empty
        /// store and the caller that already had one are in the same situation, and neither is a
        /// bug worth a log line.
        /// </remarks>
        /// <param name="sim">The Sim.</param>
        /// <returns>Whether there was a hop to remove.</returns>
        public bool Remove(int sim)
        {
            int position;
            if (!_positionOf.TryGetValue(sim, out position)) return false;

            _hops.RemoveAt(position);
            Reindex();
            return true;
        }

        /// <summary>Forget every hop. What leaving a neighbourhood does to the one it leaves.</summary>
        public void Clear()
        {
            _hops.Clear();
            _positionOf.Clear();
        }

        /// <summary>
        /// Every hop as <em>new</em> records, in start order. Copies, so the returned list is a
        /// snapshot that no later write to this store can change and that a caller may keep.
        /// </summary>
        public IList<NeighbourhoodTransitRecord> ToRecords()
        {
            var records = new List<NeighbourhoodTransitRecord>(_hops.Count);
            for (int i = 0; i < _hops.Count; i++) records.Add(Copy(_hops[i]));
            return records;
        }

        /// <summary>
        /// Write this store's hops into a storable world state, replacing whatever transit it held
        /// - the state owns its list, and a caller saving a session must not end up with a stale hop
        /// that this store has already forgotten.
        /// </summary>
        /// <remarks>
        /// Only the transit half is written. The clock, the active lot, the flags and the version
        /// stamp belong to whoever is saving - <see cref="NeighbourhoodWorldState.FromClock"/> -
        /// and a store that wrote them would be a second, disagreeing source of the same numbers.
        /// </remarks>
        /// <param name="state">The state to write into. Required.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        public void WriteTo(NeighbourhoodWorldState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            state.Transit.Clear();
            for (int i = 0; i < _hops.Count; i++) state.Transit.Add(Copy(_hops[i]));
        }

        /// <summary>
        /// Replace every hop with the ones a saved world state carries, in its order. The resume
        /// half of the round-trip: a world loaded from a sidecar holds exactly the hops the session
        /// was saved with, started when the session started them.
        /// </summary>
        /// <remarks>
        /// A state with no transit, or with a null list, empties the store rather than throwing: a
        /// session saved on the neighbourhood screen has no hops to resume and that is a normal
        /// thing to load.
        /// </remarks>
        /// <param name="state">The state to load from. A null state empties the store.</param>
        public void LoadFrom(NeighbourhoodWorldState state)
        {
            LoadFrom(state == null ? null : state.Transit);
        }

        /// <summary>
        /// Replace every hop with <paramref name="records"/>, in the order given, taking a copy of
        /// each so the saved state and the live store do not share mutable records. Null entries are
        /// skipped and a null list empties the store.
        /// </summary>
        /// <param name="records">The hops to load, or <c>null</c> for none.</param>
        public void LoadFrom(IEnumerable<NeighbourhoodTransitRecord> records)
        {
            _hops.Clear();
            _positionOf.Clear();
            if (records == null) return;

            foreach (var record in records)
            {
                //a null hop in a list is a corrupt file rather than a caller bug, and dropping it
                //keeps one bad entry from making the whole session unresumable
                if (record == null) continue;
                Add(Copy(record));
            }
        }

        public override string ToString()
        {
            return "transit " + _hops.Count + " hop(s)" + (InTransit ? string.Empty : ", none in progress");
        }

        /// <summary>
        /// Rebuild the index after a removal has shifted everything above the removed position.
        /// Deliberately the slow way: removals happen once per arrival, and a hop list is bounded
        /// by how many Sims a neighbourhood can have in transit at once.
        /// </summary>
        private void Reindex()
        {
            _positionOf.Clear();
            for (int i = 0; i < _hops.Count; i++) _positionOf[_hops[i].Sim] = i;
        }

        private static NeighbourhoodTransitRecord Copy(NeighbourhoodTransitRecord hop)
        {
            return new NeighbourhoodTransitRecord(hop.Sim, hop.Origin, hop.Destination, hop.Mode, hop.Progress);
        }
    }
}
