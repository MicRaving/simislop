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
    /// The whole-map terrain mesh the in-lot surround is cropped from, kept per neighbourhood slot
    /// so a lot entry does not rebuild its ~138k quads. CPU memory only: each surround still owns
    /// its own GPU buffers and its own crop (the bank and the band depend on the lot).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The key is what the mesh is a function of.</b> The slot, the map object the mesh was
    /// built from (compared by reference, held weakly), and the slot's
    /// <see cref="NeighbourhoodWorldCache.EpochOf"/> - which the lifecycle bumps on every
    /// <c>Invalidate</c>/<c>InvalidateLot</c>, the existing signal for "the layout or the terrain
    /// changed". A regenerated map is a new object and a bumped epoch, so either one alone misses.
    /// </para>
    /// <para>
    /// <b>Bounded.</b> A mesh is about 30 MB, so only the <see cref="Capacity"/> most recently used
    /// slots are kept. The built mesh is never written to after it is built (the crop only reads
    /// it), which is what makes sharing it between workers safe.
    /// </para>
    /// </remarks>
    public static class SimitoneSurroundingsMeshCache
    {
        /// <summary>How many slots' meshes are kept.</summary>
        public const int Capacity = 2;

        private sealed class Entry
        {
            public int Slot;
            public int Epoch;
            public WeakReference<NeighbourhoodMap> Map;
            public NeighbourhoodCityMesh Mesh;
        }

        private static readonly object Gate = new object();
        private static readonly List<Entry> Entries = new List<Entry>();

        /// <summary>How many slots are cached.</summary>
        public static int Count
        {
            get { lock (Gate) { return Entries.Count; } }
        }

        /// <summary>Forgets everything.</summary>
        public static void Clear()
        {
            lock (Gate) { Entries.Clear(); }
        }

        /// <summary>
        /// The mesh for this map, built by <paramref name="build"/> only when no entry matches.
        /// A negative slot or a null map is never cached.
        /// </summary>
        /// <param name="slot">The neighbourhood slot.</param>
        /// <param name="map">The generated map the mesh is built from.</param>
        /// <param name="epoch">The slot's invalidation epoch when the caller read it.</param>
        /// <param name="build">Builds the mesh; runs outside the lock.</param>
        /// <param name="hit">Whether the answer came from the cache.</param>
        public static NeighbourhoodCityMesh GetOrBuild(
            int slot, NeighbourhoodMap map, int epoch,
            Func<NeighbourhoodMap, NeighbourhoodCityMesh> build, out bool hit)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            hit = false;
            if (slot < 0 || map == null) return build(map);

            lock (Gate)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    var entry = Entries[i];
                    if (entry.Slot != slot || entry.Epoch != epoch) continue;
                    NeighbourhoodMap cached;
                    if (!entry.Map.TryGetTarget(out cached) || !ReferenceEquals(cached, map)) continue;
                    //most recently used goes to the back
                    Entries.RemoveAt(i);
                    Entries.Add(entry);
                    hit = true;
                    return entry.Mesh;
                }
            }

            var mesh = build(map);
            if (mesh == null) return null;
            lock (Gate)
            {
                //the slot's older entries (another epoch, another map object) are stale for good
                Entries.RemoveAll(e => e.Slot == slot);
                Entries.Add(new Entry
                {
                    Slot = slot,
                    Epoch = epoch,
                    Map = new WeakReference<NeighbourhoodMap>(map),
                    Mesh = mesh
                });
                while (Entries.Count > Capacity) Entries.RemoveAt(0);
            }
            return mesh;
        }
    }
}
