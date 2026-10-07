/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The connected-mechanics capabilities, all off by default.
    ///
    /// <para>
    /// The connected-mechanics rework made the generated 3D neighbourhood place its lots at their
    /// bitmap positions and draw its streets from connectors inferred off the 2D map. Both are
    /// changes to the mode that worked, and the revert gates them off so the synthetic packer and
    /// the synthetic road graph are what the shipped path runs - which is what the previous 3D mode
    /// did. The code is kept rather than deleted, so the pure utilities that implement it stay
    /// compiled and tested, and so a future opt-in connected mode has something to switch on.
    /// </para>
    ///
    /// <para>
    /// These are process-wide switches rather than per-neighbourhood state on purpose: the shipped
    /// path has exactly one answer, and a default that is off in every build is the property the
    /// revert is asserting. A test that wants to drive the connected path sets them directly.
    /// </para>
    /// </summary>
    public static class NeighbourhoodConnected
    {
        /// <summary>
        /// Whether lots are placed at their <c>LotLocations.iff</c> bitmap positions (map-derived)
        /// rather than by the synthetic shelf packer. Off by default: the shipped path packs.
        /// </summary>
        public static bool MapDerivedPlacements;

        /// <summary>
        /// Whether the connector mask is inferred off the 2D map's pixels and handed to the road
        /// graph and the door-side rule. Off by default: the shipped path measures its own streets.
        /// </summary>
        public static bool InferredConnectors;
    }
}
