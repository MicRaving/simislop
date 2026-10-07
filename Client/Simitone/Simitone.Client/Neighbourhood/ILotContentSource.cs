/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Everything the layout builder needs to know about one lot, read once out of the
    /// neighbourhood and then never touched again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "input record" the pure part of <see cref="NeighbourhoodLayoutBuilder"/>
    /// consumes. It exists because the build is two very different activities: reading every
    /// <c>HouseNN.iff</c> in a neighbourhood, which touches the disk and the game's IFF
    /// machinery, and deciding where each lot goes, which is arithmetic. Only the second one is
    /// worth testing, and it can only be tested if the first one's results are handed to it as
    /// data.
    /// </para>
    ///
    /// <para>
    /// Note what is <em>not</em> here. Occupancy (<see cref="Occupied"/>) and zone
    /// (<see cref="Zone"/>) are read because §5.2 says to read them and because a later scope
    /// needs them to decide what a lot is for, but Scope A's layout sidecar does not store them:
    /// nothing in M1-M5 reads them back, and the provider is the authority at runtime. The same
    /// goes for <see cref="HasGrassVariation"/>. Keeping them on the read record rather than on
    /// the persisted lot is what keeps the sidecar exactly the §4.1 model.
    /// </para>
    /// </remarks>
    public sealed class NeighbourhoodLotRead
    {
        /// <summary>The lot/house number. Never 99 - the lot-location parser drops that sentinel.</summary>
        public short Number { get; set; }

        /// <summary>x position on the neighbourhood bitmap, from <c>LotLocations.iff</c>.</summary>
        public int MapX { get; set; }

        /// <summary>y position on the neighbourhood bitmap, from <c>LotLocations.iff</c>.</summary>
        public int MapY { get; set; }

        /// <summary>
        /// Is there a readable house file? <c>false</c> for an empty plot, a bulldozed lot, and a
        /// house file that exists but will not parse - all three draw nothing and contribute no
        /// facade, but all three are still placed, because the roads still have to reach them.
        /// </summary>
        public bool HasHouse { get; set; }

        /// <summary>Raw lot side in tiles, from <c>SIMI.GlobalData[23]</c>. Zero when there is no house.</summary>
        public int Size { get; set; }

        /// <summary>Raw <c>HOUS.CameraDir</c>, already masked to 0..3. Zero when there is no house.</summary>
        public int Rotation { get; set; }

        /// <summary>
        /// The lot's ground type, from <c>VMTS1Activator.HouseNumToType</c>. Always resolved,
        /// house or not, because an empty plot still has to be surrounded by the right ground.
        /// </summary>
        public TerrainType TerrainType { get; set; }

        /// <summary>Median decoded <c>ARRY(0)</c> altitude, in the lot's own units. Zero when there is no house.</summary>
        public short BaseHeight { get; set; }

        /// <summary>Size+mtime fingerprint of <c>HouseNN.iff</c>. Zero when there is no house.</summary>
        public uint HouseHash { get; set; }

        /// <summary>
        /// Does the lot's <c>ARRY(6)</c> grass array carry any non-default value? Read because
        /// §5.2 lists it; not stored, because nothing in Scope A acts on it.
        /// </summary>
        public bool HasGrassVariation { get; set; }

        /// <summary>
        /// The lot's zone: <see cref="LotZoning.ZoneResidential"/>,
        /// <see cref="LotZoning.ZoneCommunity"/> or <see cref="LotZoning.ZoneStudio"/>.
        /// </summary>
        public short Zone { get; set; }

        /// <summary>Does a family live here? From the provider's <c>FamilyForHouse</c>.</summary>
        public bool Occupied { get; set; }

        public NeighbourhoodLotRead()
        {
        }

        public override string ToString()
        {
            return "read " + Number + " @" + MapX + "," + MapY + " size " + Size
                + " " + TerrainType + (HasHouse ? string.Empty : " [no house]")
                + (Occupied ? " occupied" : string.Empty);
        }
    }

    /// <summary>
    /// The read model <see cref="NeighbourhoodLayoutBuilder"/> builds a layout from: where the
    /// lots are, and what each one is.
    ///
    /// <para>
    /// §15's preparation item. Scope A's builder needs exactly this and nothing more, and
    /// routing the reads through an interface is what lets the packing be tested with canned lot
    /// records instead of a real neighbourhood: a test implements this in twenty lines, a
    /// half-written house file in one of them.
    /// </para>
    ///
    /// <para>
    /// It is deliberately read-only and has no notion of mutation. A later scope replaces it
    /// wholesale with a mutable implementation rather than growing setters here.
    /// </para>
    /// </summary>
    public interface ILotContentSource
    {
        /// <summary>
        /// The raw <c>LotLocations.iff</c> lines for a neighbourhood mode, in file order, one
        /// <c>"number,x,y"</c> string per selectable lot. <c>null</c> or empty when the mode has
        /// no table.
        /// </summary>
        /// <remarks>
        /// Raw, not parsed: <see cref="LotLocationParser"/> is what drops lot 99 and malformed
        /// entries, and the builder applies it, so the skipping rule lives in one place and is
        /// the same rule the neighbourhood panel uses.
        /// </remarks>
        /// <param name="mode">The neighbourhood mode, 1-based, as <c>LotLocations.iff</c> numbers its STR chunks.</param>
        IReadOnlyList<string> LotLocationLines(int mode);

        /// <summary>
        /// Read one lot's facts.
        /// </summary>
        /// <param name="number">The lot number.</param>
        /// <param name="lot">The facts, when the source has something to say.</param>
        /// <returns>
        /// <c>false</c> only when the source does not address that lot at all - an out-of-range
        /// number, or a stub with nothing behind it. A lot whose house file is missing, empty or
        /// unreadable is <em>not</em> a failure: it comes back with
        /// <see cref="NeighbourhoodLotRead.HasHouse"/> <c>false</c> and is still placed, because
        /// a bulldozed lot still has a street address.
        /// </returns>
        bool TryReadLot(int number, out NeighbourhoodLotRead lot);
    }
}
