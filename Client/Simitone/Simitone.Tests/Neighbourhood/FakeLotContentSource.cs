/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// A stand-in for a TS1 neighbourhood: a lot-location table and a table of lot facts, with no
    /// provider, no files and no content manager behind it.
    ///
    /// <para>
    /// This is the whole reason <see cref="ILotContentSource"/> exists. The builder's reads are
    /// captured as data, so a test can hand it exactly the neighbourhood it wants to reason
    /// about - a lot with no house, a community lot, two lots deliberately on the same spot -
    /// and every test in this folder runs with no game data, no graphics device and no
    /// <c>UserData</c> folder anywhere.
    /// </para>
    /// </summary>
    internal sealed class FakeLotContentSource : ILotContentSource
    {
        /// <summary>The raw <c>"number,x,y"</c> lines this neighbourhood's mode reports.</summary>
        public List<string> Lines = new List<string>();

        /// <summary>What each lot is, keyed by lot number.</summary>
        public Dictionary<int, NeighbourhoodLotRead> Lots = new Dictionary<int, NeighbourhoodLotRead>();

        /// <summary>
        /// Lots the source refuses to describe at all - the one case
        /// <see cref="ILotContentSource.TryReadLot"/> answers <c>false</c> for, standing in for a
        /// lot number the neighbourhood does not address.
        /// </summary>
        public HashSet<int> Unreadable = new HashSet<int>();

        /// <summary>How many times <see cref="LotLocationLines"/> was asked for a table.</summary>
        public int TableReads { get; private set; }

        public IReadOnlyList<string> LotLocationLines(int mode)
        {
            TableReads++;
            return Lines;
        }

        public bool TryReadLot(int number, out NeighbourhoodLotRead lot)
        {
            if (Unreadable.Contains(number))
            {
                lot = null;
                return false;
            }

            NeighbourhoodLotRead found;
            if (!Lots.TryGetValue(number, out found))
            {
                //a lot the table names but the source has no facts for is still a lot: an empty
                //plot, with a size the builder has to clamp
                found = new NeighbourhoodLotRead { Number = (short)number };
            }

            //hand back a copy, so a test's fixture cannot be mutated by the code under test
            lot = Copy(found);
            lot.Number = (short)number;
            return true;
        }

        private static NeighbourhoodLotRead Copy(NeighbourhoodLotRead from)
        {
            return new NeighbourhoodLotRead
            {
                Number = from.Number,
                MapX = from.MapX,
                MapY = from.MapY,
                HasHouse = from.HasHouse,
                Size = from.Size,
                Rotation = from.Rotation,
                TerrainType = from.TerrainType,
                BaseHeight = from.BaseHeight,
                HouseHash = from.HouseHash,
                HasGrassVariation = from.HasGrassVariation,
                Zone = from.Zone,
                Occupied = from.Occupied
            };
        }

        /// <summary>
        /// Describe one lot and add it to this neighbourhood's table. A convenience so a test can
        /// build a neighbourhood in four lines instead of twenty.
        /// </summary>
        /// <param name="number">The lot number.</param>
        /// <param name="mapX">Bitmap x, which is also what the packer seeds its arrangement from.</param>
        /// <param name="mapY">Bitmap y.</param>
        /// <param name="size">The lot side in tiles, from <c>SIMI.GlobalData[23]</c>.</param>
        /// <param name="zone">The lot's zone; residential unless the test says otherwise.</param>
        /// <param name="terrain">The lot's ground type; grass unless the test says otherwise.</param>
        /// <param name="hasHouse">Whether a house file exists. A bulldozed lot is still in the table.</param>
        public FakeLotContentSource Add(int number, int mapX, int mapY, int size = 24,
            short zone = LotZoning.ZoneResidential, TerrainType terrain = TerrainType.GRASS, bool hasHouse = true)
        {
            Lines.Add(number + "," + mapX + "," + mapY);
            Lots[number] = new NeighbourhoodLotRead
            {
                Number = (short)number,
                MapX = mapX,
                MapY = mapY,
                HasHouse = hasHouse,
                Size = hasHouse ? size : 0,
                TerrainType = terrain,
                HouseHash = hasHouse ? NeighbourhoodLayoutBuilder.HouseHashFor(4096, System.DateTime.UnixEpoch) : 0u,
                Zone = zone
            };
            return this;
        }
    }
}
