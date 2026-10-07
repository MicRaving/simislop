/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// What the generated world offers a view, so the view reads no game state of its own.
    ///
    /// <para>
    /// §15 lists <c>INeighbourhoodSceneSource</c> as a prep item to introduce in M3, and this is
    /// it: the seam between "what exists in the neighbourhood" (a layout, a generated map, which
    /// lot the player is standing in) and "what the renderer draws". Scope A's implementation is
    /// completely static - a layout is packed once, a map is generated from it, nothing changes
    /// while the view is up - and that is the whole point of the seam: the renderer reads four
    /// properties and never asks the game whether a lot is loaded, so a later milestone that makes
    /// those answers dynamic changes this interface's implementation and nothing else.
    /// </para>
    ///
    /// <para>
    /// It is deliberately read-only and it is deliberately small. Every member here is a question
    /// the renderer already asks itself; the interface only says the questions in one place.
    /// </para>
    /// </summary>
    public interface INeighbourhoodSceneSource
    {
        /// <summary>The packed layout, or <c>null</c> if none was built.</summary>
        NeighbourhoodLayout Layout { get; }

        /// <summary>The generated map, or <c>null</c> if generation failed.</summary>
        NeighbourhoodMap Map { get; }

        /// <summary>
        /// The lot the player is standing inside, or <c>null</c>. Always <c>null</c> on the
        /// neighbourhood screen; M4's in-lot surroundings is what sets it, and the facade cache
        /// skips this lot because the lot view is already drawing it in full.
        /// </summary>
        int? ActiveLot { get; }

        /// <summary>The bitmap/world correspondence, valid whenever <see cref="Layout"/> is.</summary>
        NeighbourhoodWorldMapping Mapping { get; }
    }

    /// <summary>
    /// The Scope A <see cref="INeighbourhoodSceneSource"/>: a static snapshot of one generated
    /// neighbourhood, held for as long as the view is up.
    /// </summary>
    public sealed class NeighbourhoodSceneSource : INeighbourhoodSceneSource
    {
        private readonly NeighbourhoodLayout _layout;
        private readonly NeighbourhoodMap _map;
        private readonly NeighbourhoodWorldMapping _mapping;

        /// <summary>The lot the player is standing inside, or <c>null</c> on the view screen.</summary>
        public int? ActiveLot { get; set; }

        public NeighbourhoodLayout Layout
        {
            get { return _layout; }
        }

        public NeighbourhoodMap Map
        {
            get { return _map; }
        }

        public NeighbourhoodWorldMapping Mapping
        {
            get { return _mapping; }
        }

        /// <summary>Why generation failed, or <c>null</c> if it did not.</summary>
        public string Failure { get; private set; }

        /// <summary>
        /// A source over a built world.
        /// </summary>
        /// <remarks>
        /// The map is cloned rather than shared. The renderer keeps one of these for as long as
        /// the neighbourhood view is up, across neighbourhood switches, while the object that
        /// generated the map is not; and the clone is the cheap half of §15's "generation stays
        /// pure, mutation happens on the game thread" boundary - the renderer gets an immutable
        /// world it can hold, and the worker that produced it can go away.
        /// </remarks>
        /// <param name="layout">The packed layout.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        public NeighbourhoodSceneSource(NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            _layout = layout;
            _map = map == null ? null : map.Clone();
            _mapping = NeighbourhoodWorldMapping.For(layout);
        }

        /// <summary>A source for a world that could not be built.</summary>
        /// <param name="layout">The layout, if one was packed before generation failed.</param>
        /// <param name="failure">Why it failed.</param>
        public NeighbourhoodSceneSource(NeighbourhoodLayout layout, string failure)
        {
            _layout = layout;
            _map = null;
            _mapping = NeighbourhoodWorldMapping.For(layout);
            Failure = failure;
        }

        /// <summary>Whether there is a map worth drawing.</summary>
        public bool HasWorld
        {
            get { return Failure == null && _map != null; }
        }

        /// <summary>
        /// The seed a mode's world is generated from, and the same one the layout sidecar is
        /// stamped with so a regenerated map always matches the layout that produced it.
        /// </summary>
        /// <remarks>
        /// Derived from the mode rather than chosen by a player or stored anywhere else, and
        /// derived by a fixed integer hash rather than <c>string.GetHashCode</c> or a random
        /// number, for the reason <c>NeighbourhoodRng</c> gives: the same mode must produce the
        /// same world on every machine and every launch, or a sidecar written by one build would
        /// be regenerated differently by the next. Zero is reserved - it is what an unseeded
        /// generator would give, and a zero seed is a "not configured" answer everywhere else.
        /// </remarks>
        /// <param name="mode">The neighbourhood view, 1..8.</param>
        public static int SeedForMode(int mode)
        {
            unchecked
            {
                const uint offset = 2166136261u;
                const uint prime = 16777619u;
                var hash = offset;
                var key = (uint)mode;
                for (int i = 0; i < 4; i++)
                {
                    hash ^= (byte)(key >> (i * 8));
                    hash *= prime;
                }
                return hash == 0u ? 0x5F3759DF : (int)hash;
            }
        }
    }
}
