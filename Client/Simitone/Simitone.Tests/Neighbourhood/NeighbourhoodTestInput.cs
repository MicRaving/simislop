/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The canned lot lists the M2 tests all share.
    ///
    /// <para>
    /// §11 asks the generator tests to run on "canned lot records", and the reason is that the
    /// generator has to be provable without a TS1 install: a layout that can only be read out of
    /// <c>HouseNN.iff</c> can only be tested on a machine that has one, which is not a test, it is
    /// a manual check. These builders are that seam made concrete - each one is a small
    /// neighbourhood a person can picture.
    /// </para>
    ///
    /// <para>
    /// Two conventions make the results arguable rather than merely recorded. Lots sit on
    /// multiples of eight with a street-wide gap between them, so the doors face into the gap the
    /// way a real <c>DoorSide</c> does and the street has somewhere sensible to go. And heights
    /// are either all equal (the "flat neighbourhood" case §6.3 insists the seam solver must
    /// degrade to a no-op on) or deliberately stepped by a wide margin (the embankment case).
    /// </para>
    /// </summary>
    public static class NeighbourhoodTestInput
    {
        /// <summary>The seed used by every test that is not about seeds.</summary>
        public const long DefaultSeed = 123456789L;

        /// <summary>
        /// A small map. The generator is O(size^2) and a test that builds a dozen 512x512 maps is a
        /// test nobody waits for; every code path is size independent.
        /// </summary>
        public const int SmallMap = 96;

        /// <summary>
        /// The side of the map <see cref="StreetSplitByAOneTileChannel"/> is laid out on, which is
        /// larger than <see cref="SmallMap"/> because that fixture's plots reach x 47.
        /// </summary>
        public const int RepairMap = 48;

        /// <summary>
        /// Four lots in a 2x2 block, all flat at height 4, all with their doors facing into the
        /// eight-tile gap between the two columns. The gap is where the street ends up.
        /// </summary>
        public static NeighbourhoodMapInput FourFlatLots()
        {
            return new NeighbourhoodMapInput(
                DefaultSeed, SmallMap, new[]
                {
                    Lot(11, 8, 8, NeighbourhoodDoorSide.East),
                    Lot(12, 8, 32, NeighbourhoodDoorSide.East),
                    Lot(13, 40, 8, NeighbourhoodDoorSide.West),
                    Lot(14, 40, 32, NeighbourhoodDoorSide.West),
                });
        }

        /// <summary>
        /// Two lots with a gap between them, one low and one high. The default gap is wide enough
        /// for the embankment to grade the step within <see cref="TerrainSeamOptions.MaxSlope"/>.
        /// </summary>
        /// <param name="lowHeight">Height of the left-hand lot.</param>
        /// <param name="gap">Left edge of the right-hand lot.</param>
        /// <param name="highHeight">Height of the right-hand lot.</param>
        public static NeighbourhoodMapInput TwoLotsWithAHeightStep(byte lowHeight, int gap, byte highHeight)
        {
            return new NeighbourhoodMapInput(
                DefaultSeed, SmallMap, new[]
                {
                    Lot(21, 8, 16, 16, lowHeight, NeighbourhoodDoorSide.East),
                    Lot(22, gap, 16, 16, highHeight, NeighbourhoodDoorSide.West),
                });
        }

        /// <summary>
        /// A row of lots, all at the same height and all facing the same way - the "flat
        /// neighbourhood" the plan says the seam solver has to degrade to a no-op on.
        /// </summary>
        /// <param name="height">The height every lot sits at.</param>
        /// <param name="count">How many lots in the row.</param>
        public static NeighbourhoodMapInput FlatRow(byte height, int count)
        {
            var lots = new List<NeighbourhoodLotInput>();
            for (int i = 0; i < count; i++)
            {
                lots.Add(Lot((short)(30 + i), 8 + i * 24, 16, 16, height, NeighbourhoodDoorSide.East));
            }
            return new NeighbourhoodMapInput(DefaultSeed, SmallMap, lots);
        }

        /// <summary>
        /// Two houses whose street networks cannot reach each other, and cannot be reached by any
        /// street either - but can be joined by a one-tile path.
        ///
        /// <para>
        /// This is the case the connectivity repair exists for, and it takes a deliberate shape to
        /// build now that the streets come from the gaps between lots. <b>The shape changed when
        /// <see cref="RoadGraph.MainStreetWidth"/> went from two tiles to six.</b> The old fixture
        /// was two walls of eight-tile lots with a one-tile column between them, on the reasoning
        /// that "no two-tile street can ever fit down that column". A street is now
        /// <c>min(MainStreetWidth, the free gap)</c>, so a one-tile column is a perfectly good
        /// one-tile street, the bands grow straight down it, and the network arrives already
        /// connected - the fixture stopped being a split at all.
        /// </para>
        ///
        /// <para>
        /// What still splits a network is a pocket whose free ground is too short for a band to be
        /// seeded in it: the band needs a free run measured across a lot's <em>whole</em> extent on
        /// the other axis, and a pocket that only touches a lot's edge over part of that extent
        /// never gets one. This fixture is four bulldozed plots placed to make exactly that - a
        /// corner street that grows into a pocket and stops - with open ground beyond, so a
        /// four-connected search can still walk out of the pocket and around the plots.
        /// </para>
        /// </summary>
        public static NeighbourhoodMapInput StreetSplitByAOneTileChannel()
        {
            // The two plots that close the pocket in on the north and west, the two that close it on
            // the east and south, and the open ground beyond all four. The pocket's free tiles are
            // one column wide against the east plot and a few rows tall against the north one, so
            // neither edge sees a free run across its whole extent and no band is seeded there.
            return new NeighbourhoodMapInput(
                DefaultSeed, RepairMap, new[]
                {
                    Lot(100, 9, 15, 8, 4, NeighbourhoodDoorSide.North, false),
                    Lot(102, 37, 44, 4, 4, NeighbourhoodDoorSide.North, false),
                    Lot(103, 17, 35, 4, 4, NeighbourhoodDoorSide.North, false),
                    Lot(104, 46, 31, 2, 4, NeighbourhoodDoorSide.North, false),
                    Lot(105, 42, 39, 2, 4, NeighbourhoodDoorSide.North, false),
                });
        }

        /// <summary>
        /// A neighbourhood packed the way <c>NeighbourhoodLayoutBuilder</c> packs a real one: lots
        /// on a fixed pitch with the packer's own margin between them, door sides chosen by
        /// distance to the map border.
        ///
        /// <para>
        /// This is the fixture the street tests exist for. The small hand-placed neighbourhoods
        /// above have generous gaps that any layout rule happens to survive; this one does not, and
        /// it is the one whose streets were invisible. Forty-one 64-tile lots with a two-tile
        /// margin is what a base residential neighbourhood actually looks like once it has been
        /// fitted into a 512 map.
        /// </para>
        /// </summary>
        /// <param name="lotCount">How many lots.</param>
        /// <param name="lotSize">Side of each lot, in tiles.</param>
        /// <param name="margin">Gap between packed lots, in tiles.</param>
        /// <param name="mapSize">The map's side length.</param>
        public static NeighbourhoodMapInput PackedNeighbourhood(
            int lotCount, int lotSize, int margin, int mapSize)
        {
            var pitch = lotSize + margin;
            var perRow = Math.Max(1, mapSize / pitch);
            var lots = new List<NeighbourhoodLotInput>();
            for (int i = 0; i < lotCount; i++)
            {
                var x = (i % perRow) * pitch;
                var y = (i / perRow) * pitch;
                var side = (byte)(int)NeighbourhoodLayoutBuilder.DoorSideFor(x, y, pitch, mapSize);
                lots.Add(Lot((short)(200 + i), x, y, lotSize, 4, side));
            }
            return new NeighbourhoodMapInput(DefaultSeed, mapSize, lots);
        }

        /// <summary>The map size a generated neighbourhood actually uses.</summary>
        public const int RealMapSize = 512;

        /// <summary>
        /// A 2x2 block with a four-tile gap on every side, so a street has room to be two tiles
        /// wide and leave a pavement tile beside each lot. This is the shape the packer would
        /// produce with a wider sidewalk margin, and it is what makes the pavement rule in
        /// <c>RoadGraph</c> visible.
        /// </summary>
        public static NeighbourhoodMapInput WideGaps()
        {
            return new NeighbourhoodMapInput(
                DefaultSeed, SmallMap, new[]
                {
                    Lot(81, 8, 8, NeighbourhoodDoorSide.East),
                    Lot(82, 8, 40, NeighbourhoodDoorSide.East),
                    Lot(83, 48, 8, NeighbourhoodDoorSide.West),
                    Lot(84, 48, 40, NeighbourhoodDoorSide.West),
                });
        }

        /// <summary>One lot that has no house: a bulldozed plot on its own.</summary>
        public static NeighbourhoodMapInput OneBulldozedLot()
        {
            return new NeighbourhoodMapInput(
                DefaultSeed, SmallMap,
                new[] { Lot(51, 8, 8, NeighbourhoodDoorSide.East, false) });
        }

        /// <summary>
        /// Two houses with a solid wall of bulldozed lots between them, wall touching the top and
        /// bottom of the map so the free ground genuinely does not connect either side.
        ///
        /// <para>
        /// This is the one input the connectivity repair <em>cannot</em> fix, and it is here so
        /// that "the generator refuses rather than drawing half a network" is a tested fact
        /// instead of an assumption. Every other split network in these tests gets repaired, which
        /// is the point - a repair that silently does nothing would look exactly like a repair
        /// that succeeded if the throw were never exercised.
        /// </para>
        /// </summary>
        public static NeighbourhoodMapInput SolidWallBetweenTwoHouses()
        {
            var lots = new List<NeighbourhoodLotInput>
            {
                Lot(61, 0, 16, NeighbourhoodDoorSide.East),
                Lot(62, 64, 16, NeighbourhoodDoorSide.West),
            };

            // A bulldozed lot every 16 tiles down the wall column, from the top of the map to the
            // bottom, so there is no gap to squeeze a repair path through.
            for (short i = 0; i * 16 < SmallMap; i++)
            {
                lots.Add(Lot((short)(70 + i), 24, i * 16, 16, 4, NeighbourhoodDoorSide.North, false));
            }

            return new NeighbourhoodMapInput(DefaultSeed, SmallMap, lots);
        }

        /// <summary>No lots at all: the degenerate neighbourhood.</summary>
        public static NeighbourhoodMapInput NoLots()
        {
            return new NeighbourhoodMapInput(DefaultSeed, SmallMap, new NeighbourhoodLotInput[0]);
        }

        /// <summary>
        /// The same lots as <see cref="FourFlatLots"/> but under a different seed, for the tests
        /// that assert a different seed really does change something.
        /// </summary>
        public static NeighbourhoodMapInput FourFlatLotsWithSeed(long seed)
        {
            return new NeighbourhoodMapInput(
                seed, SmallMap, new[]
                {
                    Lot(11, 8, 8, NeighbourhoodDoorSide.East),
                    Lot(12, 8, 32, NeighbourhoodDoorSide.East),
                    Lot(13, 40, 8, NeighbourhoodDoorSide.West),
                    Lot(14, 40, 32, NeighbourhoodDoorSide.West),
                });
        }

        /// <summary>A lot record with everything but the interesting fields defaulted.</summary>
        /// <param name="number">The lot number.</param>
        /// <param name="x">Left edge.</param>
        /// <param name="y">Top edge.</param>
        /// <param name="doorSide">Which edge the driveway is on.</param>
        public static NeighbourhoodLotInput Lot(
            short number, int x, int y, byte doorSide,
            bool hasHouse = true,
            byte terrain = NeighbourhoodTerrain.Grass,
            short zone = LotZoning.ZoneResidential)
        {
            return Lot(number, x, y, 16, 4, doorSide, hasHouse, terrain, zone);
        }

        /// <summary>A lot record with every field given.</summary>
        /// <param name="number">The lot number.</param>
        /// <param name="x">Left edge.</param>
        /// <param name="y">Top edge.</param>
        /// <param name="size">Side of the square.</param>
        /// <param name="baseHeight">Height on the <c>ElevationData</c> scale.</param>
        /// <param name="doorSide">Which edge the driveway is on.</param>
        /// <param name="hasHouse">Whether the lot still has a house.</param>
        /// <param name="terrain">The lot's own ground type.</param>
        /// <param name="zone">The lot's zone.</param>
        public static NeighbourhoodLotInput Lot(
            short number, int x, int y, int size, byte baseHeight, byte doorSide,
            bool hasHouse = true,
            byte terrain = NeighbourhoodTerrain.Grass,
            short zone = LotZoning.ZoneResidential)
        {
            return new NeighbourhoodLotInput(
                number, x, y, size, zone, terrain, baseHeight, doorSide, hasHouse);
        }
    }
}
