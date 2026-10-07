/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using FSO.LotView;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The three pure decisions <c>UILotControl.UpdateCutaway</c> makes every frame about the
    /// dynamic wall cutaway, lifted out so they can be regression tested without a VM.
    ///
    /// The cutaway is rebuilt from three pieces of state - the floor, the cutaway rotation and
    /// the set of hovered rooms - and cached between frames. Rebuilding it is expensive
    /// (<c>VMArchitectureTools.GenerateRoomCut</c> walks the whole architecture), so the caller
    /// only rebuilds when one of those three actually changed, and otherwise just re-applies the
    /// small 5x5 rectangle cut under the mouse.
    ///
    /// These helpers are pure functions over their arguments: no graphics device, no content, no
    /// audio, no statics.
    /// </summary>
    public static class CutawayDecision
    {
        /// <summary>
        /// True when the room cutaway must be regenerated from scratch, i.e. the floor, the
        /// cutaway rotation or the hovered-room set differs from what the cache was built for.
        /// The caller uses this to escalate <c>recut</c> to 2 ("regenerate", not just
        /// "re-apply the mouse rectangle").
        /// </summary>
        /// <remarks>
        /// Note this is a set comparison, not a count comparison: two room sets of the same size
        /// but different contents must regenerate.
        /// </remarks>
        public static bool ShouldRegenerate(HashSet<uint> finalRooms, HashSet<uint> lastCutRooms,
            int level, int lastFloor, WorldRotation cutRotation, WorldRotation lastRotation)
        {
            return !(lastFloor == level
                && lastRotation == cutRotation
                && finalRooms.SetEquals(lastCutRooms));
        }

        /// <summary>
        /// The 5x5 cut rectangle centred on the mouse tile, shifted two tiles along the
        /// cutaway direction so that it peeks at the wall the player is facing rather than
        /// straddling it. <see cref="VMArchitectureTools.CutCheckDir"/> is indexed by
        /// <see cref="WorldRotation"/> and is subtracted (hence the "- 2" on the near side).
        /// </summary>
        public static Rectangle ComputeMouseCutRect(int mouseTileX, int mouseTileY, WorldRotation cutRotation)
        {
            var newCut = new Rectangle((int)(mouseTileX - 2.5), (int)(mouseTileY - 2.5), 5, 5);
            newCut.X -= VMArchitectureTools.CutCheckDir[(int)cutRotation][0] * 2;
            newCut.Y -= VMArchitectureTools.CutCheckDir[(int)cutRotation][1] * 2;
            return newCut;
        }

        /// <summary>
        /// True when the freshly computed cut array should actually be pushed onto the
        /// blueprint. A regenerated cut (recut &gt; 1) is always pushed; a mouse-rectangle-only
        /// cut is pushed if it changed anything visible, or if the previous rectangle cut did
        /// (so the revert is never left dangling).
        /// </summary>
        public static bool ShouldPushCut(int recut, bool notableChange, bool lastRectCutNotable)
        {
            return recut > 1 || notableChange || lastRectCutNotable;
        }
    }
}
