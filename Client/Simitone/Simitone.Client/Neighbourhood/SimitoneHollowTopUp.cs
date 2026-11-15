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
    /// The later-frame half of B2's neighbour loading: which neighbours were built blank on the
    /// first pass because the per-load build budget ran out, which of them is due a real build on
    /// this frame, and what becomes of one whose build fails. Pure bookkeeping - no device, no VM -
    /// so the attempt rules are testable without either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this replaced the one-attempt-per-lot latch.</b> The screen used to make exactly one
    /// restore per lot entry, with a budget of four builds, and every neighbour past the fourth
    /// stayed an empty plot under a facade for the rest of the visit. The restore itself is still
    /// one attempt; what is deferred is now tracked per neighbour, and each one is topped up on a
    /// later frame.
    /// </para>
    /// <para>
    /// <b>The rules.</b> Neighbours are served in the order given (nearest first). At most one is
    /// handed out per <see cref="MinFramesBetweenBuilds"/> frames, because each build stands a
    /// <c>World</c> and a <c>VM</c> up on the game thread. A neighbour that is built is never
    /// offered again. One whose build threw is retried after <see cref="RetryDelayFrames"/> frames
    /// and given up on after <see cref="MaxAttempts"/> attempts, so a broken lot costs two builds
    /// and never one per frame. One that cannot be read at all is given up on at once.
    /// </para>
    /// </remarks>
    public sealed class SimitoneHollowTopUp
    {
        /// <summary>Frames between two builds, so the hitches do not run back to back.</summary>
        public const int MinFramesBetweenBuilds = 3;

        /// <summary>Frames a failed neighbour waits before its next attempt.</summary>
        public const int RetryDelayFrames = 120;

        /// <summary>Attempts a neighbour gets in total before it is left blank for the visit.</summary>
        public const int MaxAttempts = 2;

        private enum Phase
        {
            Pending,
            Built,
            GaveUp
        }

        private sealed class Entry
        {
            public short Lot;
            public Phase Phase;
            public int Attempts;
            public long NotBefore;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private long _nextBuildFrame;

        /// <summary>Neighbours still waiting for a build.</summary>
        public int PendingCount
        {
            get
            {
                var count = 0;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Phase == Phase.Pending) count++;
                }
                return count;
            }
        }

        /// <summary>Neighbours that were topped up.</summary>
        public int BuiltCount
        {
            get { return CountOf(Phase.Built); }
        }

        /// <summary>Neighbours given up on for this visit.</summary>
        public int GaveUpCount
        {
            get { return CountOf(Phase.GaveUp); }
        }

        /// <summary>Whether nothing is left to build.</summary>
        public bool IsComplete
        {
            get { return PendingCount == 0; }
        }

        /// <summary>
        /// Starts a pass over these neighbours, in order. Replaces whatever was tracked, and lists
        /// a lot number once however often it is named.
        /// </summary>
        /// <param name="lots">The blank neighbours worth a later build, nearest first.</param>
        public void Begin(IEnumerable<short> lots)
        {
            _entries.Clear();
            _nextBuildFrame = 0;
            if (lots == null) return;
            var seen = new HashSet<short>();
            foreach (var lot in lots)
            {
                if (!seen.Add(lot)) continue;
                _entries.Add(new Entry { Lot = lot, Phase = Phase.Pending });
            }
        }

        /// <summary>
        /// The neighbour due a build on this frame, if there is one. Does not count as an attempt:
        /// the caller reports what happened with <see cref="MarkBuilt"/>, <see cref="MarkFailed"/>
        /// or <see cref="MarkUnreadable"/>.
        /// </summary>
        /// <param name="frame">A monotonically increasing frame number.</param>
        /// <param name="lot">The lot to build.</param>
        public bool TryNext(long frame, out short lot)
        {
            lot = 0;
            if (frame < _nextBuildFrame) return false;
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Phase != Phase.Pending) continue;
                if (frame < entry.NotBefore) continue;
                lot = entry.Lot;
                return true;
            }
            return false;
        }

        /// <summary>This neighbour now has a real sub-world. It is not offered again.</summary>
        /// <param name="lot">The lot number.</param>
        /// <param name="frame">The frame the build ran on.</param>
        public void MarkBuilt(short lot, long frame)
        {
            var entry = Find(lot);
            if (entry == null) return;
            entry.Phase = Phase.Built;
            entry.Attempts++;
            _nextBuildFrame = frame + MinFramesBetweenBuilds;
        }

        /// <summary>
        /// This neighbour's build threw. It is retried after <see cref="RetryDelayFrames"/> frames
        /// unless that was its last attempt.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        /// <param name="frame">The frame the build ran on.</param>
        /// <returns>Whether the neighbour will be tried again.</returns>
        public bool MarkFailed(short lot, long frame)
        {
            _nextBuildFrame = frame + MinFramesBetweenBuilds;
            var entry = Find(lot);
            if (entry == null || entry.Phase != Phase.Pending) return false;
            entry.Attempts++;
            if (entry.Attempts >= MaxAttempts)
            {
                entry.Phase = Phase.GaveUp;
                return false;
            }
            entry.NotBefore = frame + RetryDelayFrames;
            return true;
        }

        /// <summary>
        /// This neighbour's contents cannot be read, so asking again would fail the same way. It is
        /// left blank for the visit.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        /// <param name="frame">The frame the attempt ran on.</param>
        public void MarkUnreadable(short lot, long frame)
        {
            _nextBuildFrame = frame + MinFramesBetweenBuilds;
            var entry = Find(lot);
            if (entry == null || entry.Phase != Phase.Pending) return;
            entry.Attempts++;
            entry.Phase = Phase.GaveUp;
        }

        /// <summary>Forgets everything. What a lot detach calls.</summary>
        public void Clear()
        {
            _entries.Clear();
            _nextBuildFrame = 0;
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            return BuiltCount + " topped up, " + PendingCount + " waiting, " + GaveUpCount + " given up on";
        }

        private int CountOf(Phase phase)
        {
            var count = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Phase == phase) count++;
            }
            return count;
        }

        private Entry Find(short lot)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Lot == lot) return _entries[i];
            }
            return null;
        }
    }
}
