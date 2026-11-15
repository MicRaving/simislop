/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Which lots of a neighbourhood the in-lot surround hands its facade cache: every lot inside
    /// the neighbour region, plus the nearest <see cref="FarFacadeCap"/> lots beyond it. Pure - a
    /// layout and a cull in, a list out - so the cap and the order are testable without a device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why far lots are fed at all.</b> The surround's ground covers the whole island, so a
    /// lot past the neighbour region used to stand on drawn ground with no house on it. The facade
    /// cache already builds nearest-first (<see cref="NeighbourhoodFacadePlan.ToBuild"/> orders by
    /// distance from the focus), so feeding it the far lots costs nothing for the near ones: they
    /// are built first and the far ones fill in behind them.
    /// </para>
    /// <para>
    /// <b>Why it is capped.</b> A facade is a <c>World</c> and a <c>VM</c> stood up on the game
    /// thread, one per few frames (<see cref="NeighbourhoodFacades.DefaultBuildsPerFrame"/>), and the
    /// pass's own log records builds of a second or more each. An uncapped feed of a 40-odd lot
    /// island is a minute of intermittent hitching. The cap bounds how much of that a lot entry can
    /// cost; the cross-visit store (<see cref="NeighbourhoodFacadeMeshCache"/>, 128 meshes) makes
    /// a revisit free for whatever was built.
    /// </para>
    /// </remarks>
    public static class SimitoneSurroundingsFacadeFeed
    {
        /// <summary>How many lots beyond the neighbour region are given a facade, nearest first.</summary>
        public const int FarFacadeCap = 16;

        /// <summary>
        /// The lots to feed the facade cache for the lot the player is standing in.
        /// </summary>
        /// <param name="layout">The whole layout, or <c>null</c>.</param>
        /// <param name="cull">The neighbour region: its lots are always fed.</param>
        /// <param name="activeLot">The player's own lot, never fed (the lot view draws it).</param>
        /// <param name="farCap">How many lots beyond the region to add.</param>
        public static IReadOnlyList<NeighbourhoodLot> LotsToFeed(
            NeighbourhoodLayout layout, SimitoneSurroundingsCull cull, short activeLot,
            int farCap = FarFacadeCap)
        {
            var fed = new List<NeighbourhoodLot>();
            if (layout == null) return fed;

            var inRegion = new HashSet<short>();
            var near = cull.IsValid ? cull.LotsInRange(layout, activeLot) : new List<NeighbourhoodLot>();
            for (int i = 0; i < near.Count; i++)
            {
                fed.Add(near[i]);
                inRegion.Add(near[i].Number);
            }
            if (farCap <= 0) return fed;

            var home = layout.FindLot(activeLot);
            var origin = home == null ? Vector2.Zero : NeighbourhoodWorldMapping.PackedCentre(home);
            var far = new List<KeyValuePair<float, NeighbourhoodLot>>();
            for (int i = 0; i < layout.LotCount; i++)
            {
                var lot = layout.Lots[i];
                if (lot == null || lot.Number == activeLot || inRegion.Contains(lot.Number)) continue;
                far.Add(new KeyValuePair<float, NeighbourhoodLot>(
                    Vector2.DistanceSquared(origin, NeighbourhoodWorldMapping.PackedCentre(lot)), lot));
            }
            far.Sort((a, b) =>
            {
                var byDistance = a.Key.CompareTo(b.Key);
                return byDistance != 0 ? byDistance : a.Value.Number.CompareTo(b.Value.Number);
            });
            for (int i = 0; i < far.Count && i < farCap; i++) fed.Add(far[i].Value);
            return fed;
        }
    }
}
