/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// B7 for the live world: read a neighbourhood's stored session into a world when the world is
    /// built, and write the world's session back when the player leaves a lot or saves the game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are gated on <see cref="NeighbourhoodWorldFlag.Persist"/> and do nothing when it
    /// is off, so a session with the flag off reads and writes no session at all.
    /// </para>
    /// <para>
    /// The write goes through <see cref="NeighbourhoodLifecycle.SaveWorldState"/>, which stamps the
    /// session onto the sidecar already on disk and commits it atomically through the layout
    /// store, leaving the lots in it alone. Because the lots are the sidecar's own, each lot's
    /// carried session fields (state, last-simulated tick, entrance) are exactly what the refresh
    /// left there; this class never writes a layout it built itself.
    /// </para>
    /// </remarks>
    public static class NeighbourhoodWorldPersistence
    {
        /// <summary>
        /// Adopts the session stored with <paramref name="stored"/> into <paramref name="world"/>:
        /// its clock, its active lot and its hops in progress.
        /// </summary>
        /// <param name="world">The world just built.</param>
        /// <param name="stored">The session the layout carried, or null for none.</param>
        /// <returns>Whether a session was applied.</returns>
        public static bool Restore(SimitoneNeighbourhoodWorld world, NeighbourhoodWorldState stored)
        {
            if (world == null || stored == null) return false;
            if (!NeighbourhoodLifecycle.PersistsWorldState) return false;
            world.ApplyWorldState(stored);
            return true;
        }

        /// <summary>
        /// Writes <paramref name="world"/>'s session into the sidecar of <paramref name="slot"/>.
        /// </summary>
        /// <param name="userDir">The user directory the sidecar lives in.</param>
        /// <param name="slot">The slot the world belongs to.</param>
        /// <param name="world">The world to save.</param>
        /// <returns>
        /// Whether the sidecar now carries the session. False with the flag off, with no world,
        /// with no sidecar to stamp it onto, and on any write failure.
        /// </returns>
        public static bool Save(string userDir, int slot, SimitoneNeighbourhoodWorld world)
        {
            if (world == null) return false;
            if (!NeighbourhoodLifecycle.PersistsWorldState) return false;
            //SaveWorldState() applies the off-screen stamps to the world's layout; handing that
            //layout over lets the per-lot fields reach the file, which re-reads the sidecar itself.
            var session = world.SaveWorldState();
            return NeighbourhoodLifecycle.SaveWorldState(userDir, slot, session, world.Layout);
        }
    }
}
