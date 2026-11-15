/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using FSO.Content.TS1;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Builds a <see cref="NeighbourhoodLayout"/> out of a neighbourhood's own files: reads the
    /// lot-location table and each lot's house file (<see cref="ReadLots"/>), then arranges the
    /// lots on the generated map (<see cref="PackOnGrid"/>).
    ///
    /// <para>
    /// <b>The split.</b> Reading is IO: twenty-odd <c>HouseNN.iff</c> files, an STR chunk, a
    /// zoning dictionary and a family dictionary. Placing is arithmetic. Only the second is
    /// deterministic, so the reads are captured as data (<see cref="NeighbourhoodLotRead"/>,
    /// produced by an <see cref="ILotContentSource"/>) and <see cref="PackOnGrid(IReadOnlyList{NeighbourhoodLotRead}, int, int, int)"/>
    /// consumes nothing else - a test can hand it canned lot records and get a layout back
    /// without a game, a VM or a graphics device.
    /// </para>
    ///
    /// <para>
    /// <b>The arrangement is a synthetic shelf packing</b>: lots in rows, each advancing by its
    /// <see cref="FootprintFor"/> (its own side plus <see cref="StreetCorridorTiles"/>), so every
    /// pair of lots has a full street corridor between them. A lot's bitmap position
    /// (<see cref="NeighbourhoodLotRead.MapX"/>/<see cref="NeighbourhoodLotRead.MapY"/>) is
    /// recorded for the 2D map, the camera and the hit test and only orders the shelf. An
    /// earlier attempt placed lots at their bitmap positions (the "connected mechanics" work);
    /// it was removed together with the road-mask driveway probe it needed.
    /// </para>
    ///
    /// <para>
    /// <b>The street-width contract</b> is what the other members here are for: <see cref="ClampSize"/>,
    /// <see cref="FootprintFor"/> and <see cref="StreetCorridorTiles"/> are read by the road graph,
    /// the map adapter and the lot view, and <see cref="DoorSideFor"/> picks a lot's driveway
    /// edge. <see cref="ProjectMapPosition"/> and <see cref="IsometricScaleFor"/> are the OBJ
    /// exporter's transform, kept so <see cref="NeighbourhoodPixelTransform"/> can be checked
    /// against it; nothing in the shipped path calls them.
    /// </para>
    /// </summary>
    public static class NeighbourhoodLayoutBuilder
    {
        /// <summary>
        /// The layout algorithm's version. Stamped onto every layout and checked on load, so a
        /// layout packed by a different arrangement rule is rebuilt rather than trusted.
        /// </summary>
        /// <remarks>
        /// Compared for equality only, never as a sequence: a sidecar stamped with any other value
        /// is rebuilt, so an old arrangement is never trusted.
        /// </remarks>
        public const int CurrentGeneratorVersion = 73;

        /// <summary>Side of the generated map in tiles.</summary>
        public const int DefaultMapSize = NeighbourhoodLayout.DefaultMapSize;

        /// <summary>
        /// Tiles of street corridor, in map tiles: the corridor a generated street occupies
        /// (<see cref="NeighbourhoodMapAdapter.StreetCorridorMapTiles"/>) and therefore the
        /// smallest gap in which the road graph can lay a full-width tarmac street with a footway
        /// on each side, which is what <see cref="SimitoneLotViewRoad.StreetCorridorTiles"/> is
        /// for and why it is the same number.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It is not a sidewalk, and it is not restated anywhere.</b> It used to be
        /// <c>SidewalkMargin = 2</c> - "one tile of sidewalk on each side" - which is two tiles of
        /// ground between two lots and not enough to be a street in any of the three grammars that
        /// have an opinion: the lot view's own <c>RestoreRoad</c> bands reach six tiles into each
        /// lot and the road graph asks for <see cref="RoadGraph.MainStreetWidth"/> of tarmac.
        /// </para>
        /// <para>
        /// It used also to be a promise the packer kept - that every two lots left this much
        /// between them, and that no two of them overlapped at all. Part 8 removed both: lots are at
        /// their real <c>LotLocations</c> positions, TSO's own pitch is <c>Size - 2</c>, and two real
        /// lots therefore overlap by two tiles on purpose. The number survives because it is still
        /// the corridor a street is measured against and still the pitch the shelf arrangement
        /// advances by - see <see cref="FootprintFor"/>.
        /// </para>
        /// </remarks>
        public const int StreetCorridorTiles = SimitoneLotViewRoad.StreetCorridorTiles;

        /// <summary>
        /// The old name for <see cref="StreetCorridorTiles"/>, which is the number the road graph's
        /// remarks, the map adapter's <c>PackedLotPitchTiles</c> and a dozen call sites already
        /// read. Kept, and equal, so there is one number in the tree rather than one here and a
        /// second one spelled out three files away.
        /// </summary>
        /// <remarks>
        /// <b>The name is now the misleading half of it.</b> It was the margin the packer left
        /// between two footprints; since Part 8 nothing is packed, so it is the corridor a street
        /// needs - and <see cref="StreetCorridorTiles"/> says that. Renamed rather than removed
        /// because a dozen call sites read it.
        /// </remarks>
        public const int SidewalkMargin = StreetCorridorTiles;

        /// <summary>
        /// A lot's footprint for street-width purposes - its own side plus the whole street
        /// corridor. Pure, and the one place a footprint is computed: the shelf arrangement, the map
        /// adapter's <c>PackedLotPitchTiles</c> and the road graph's corridor all have to agree on it
        /// or a street is drawn wider than the ground it is drawn in.
        /// </summary>
        /// <remarks>
        /// It is <em>not</em> how far apart two lots stand. Since Part 8 they stand where
        /// <c>LotLocations.iff</c> says, at TSO's own <c>Size - 2</c> pitch, which is a different
        /// number with a different meaning - see
        /// <see cref="NeighbourhoodMapAdapter.LotViewLotPitchTiles"/> for that one.
        /// </remarks>
        /// <param name="lotSize">The lot's raw <c>SIMI.GlobalData[23]</c>, clamped.</param>
        public static int FootprintFor(int lotSize)
        {
            return ClampSize(lotSize) + StreetCorridorTiles;
        }

        /// <summary>
        /// The exporter's threshold: neighbourhoods with more lots than this are scaled down
        /// twice as far, because the bitmaps get denser, not wider.
        /// </summary>
        public const int LargeNeighbourhoodLotCount = 30;

        /// <summary>
        /// <c>sqrt(2)</c>, spelled out because the exporter's transform is part of the
        /// compatibility contract with the mesh people already made from the OBJ export.
        /// </summary>
        public const float IsometricScaleFactor = 1.4142135623730950488016887242097f;

        // ---- the isometric projection, shared with the OBJ exporter -----------------------

        /// <summary>
        /// The divisor <c>SimitoneNeighOBJExporter</c> applies to a lot's bitmap position.
        /// </summary>
        /// <remarks>
        /// <c>(count &gt; 30 ? 1f : 2f) * sqrt(2)</c>, verbatim. A neighbourhood with more than
        /// <see cref="LargeNeighbourhoodLotCount"/> lots is scaled half as far, because what
        /// actually happens in a big neighbourhood is that the lots crowd together, not that the
        /// map grows.
        /// </remarks>
        public static float IsometricScaleFor(int lotCount)
        {
            return (lotCount > LargeNeighbourhoodLotCount ? 1f : 2f) * IsometricScaleFactor;
        }

        /// <summary>
        /// Project a lot's neighbourhood-bitmap position through the exporter's isometric
        /// transform, <c>(x + 2y, 0, 2y - x) / scale</c>.
        /// </summary>
        /// <param name="mapX">The lot's bitmap x.</param>
        /// <param name="mapY">The lot's bitmap y.</param>
        /// <param name="lotCount">How many lots are being placed; see <see cref="IsometricScaleFor"/>.</param>
        /// <param name="x">The projected x, before fitting into the map.</param>
        /// <param name="z">The projected y - the transform's second component - before fitting.</param>
        public static void ProjectMapPosition(int mapX, int mapY, int lotCount, out float x, out float z)
        {
            var scale = IsometricScaleFor(lotCount);
            //written as the exporter writes it: the integer combination first, the float divide
            //second, so the two agree to the last bit rather than merely to the last tile
            x = (mapX + mapY * 2) / scale;
            z = (mapY * 2 - mapX) / scale;
        }

        // ---- per-lot derivations -------------------------------------------------------------

        /// <summary>
        /// Coerce a raw <c>SIMI.GlobalData[23]</c> into a legal lot side.
        /// </summary>
        /// <remarks>
        /// TS1 lots are 64x64 and the activator turns this value straight into a
        /// <c>Size x Size</c> blueprint, so 0 and 65 are corrupt values rather than extreme
        /// ones. Zero (no <c>SIMI</c>, or a house that would not parse) becomes
        /// <see cref="NeighbourhoodLayout.MinLotSize"/>, not an invented "typical" size: a lot
        /// with no house draws nothing anyway, and the alternative would be putting a number in
        /// the sidecar that no file in the neighbourhood supports.
        /// </remarks>
        public static int ClampSize(int rawSize)
        {
            if (rawSize < NeighbourhoodLayout.MinLotSize) return NeighbourhoodLayout.MinLotSize;
            if (rawSize > NeighbourhoodLayout.MaxLotSize) return NeighbourhoodLayout.MaxLotSize;
            return rawSize;
        }

        /// <summary>
        /// The lot's base height: the median of its decoded <c>ARRY(0)</c> altitudes.
        /// </summary>
        /// <remarks>
        /// <paramref name="altitudeArray"/> is the transposed ARRY chunk, four bytes per tile
        /// with the altitude in the second - the same layout <c>VMTS1Activator.DecodeHeights</c>
        /// reads, and the test asserts the two agree. A median rather than a minimum because a
        /// single bad tile would otherwise drag a whole lot (and every lot below it, since §6.3
        /// takes the minimum across lots) down with it; the minimum is taken across lots
        /// instead, where a genuine low outlier actually matters.
        /// </remarks>
        /// <returns>The altitude, or 0 when the array is missing or too short to hold a tile.</returns>
        public static short MedianBaseHeight(byte[] altitudeArray)
        {
            if (altitudeArray == null || altitudeArray.Length < 4) return 0;

            var count = altitudeArray.Length / 4;
            //256-bucket histogram: no sort, no allocation beyond the counters, and stable for
            //any lot size. The lower median is taken for even counts.
            var histogram = new int[256];
            for (int i = 0; i < count; i++)
            {
                histogram[altitudeArray[i * 4 + 1]]++;
            }

            var target = (count + 1) / 2;
            var seen = 0;
            for (int value = 0; value < 256; value++)
            {
                seen += histogram[value];
                if (seen >= target) return (short)value;
            }
            return 0;
        }

        /// <summary>
        /// Does a lot's <c>ARRY(6)</c> grass array hold anything other than the uniform default?
        /// </summary>
        /// <remarks>
        /// <c>VMTS1Activator</c> turns the chunk into grass state with <c>127 - x</c>, so a
        /// uniform lot is all <c>0x7f</c> and reads as zero. Read because §5.2 lists it; not
        /// stored anywhere, because nothing in Scope A acts on it.
        /// </remarks>
        public static bool HasGrassVariation(byte[] grassArray)
        {
            if (grassArray == null || grassArray.Length == 0) return false;
            for (int i = 0; i < grassArray.Length; i++)
            {
                if (grassArray[i] != 0x7f) return true;
            }
            return false;
        }

        /// <summary>
        /// The fingerprint a layout uses to notice that a lot's <c>HouseNN.iff</c> changed: the
        /// file's length mixed with its last write time.
        /// </summary>
        /// <remarks>
        /// FNV-1a over the two 64-bit values, so it is stable across processes and runs on any
        /// platform - a hash seeded from a string hash or a <c>DateTime.GetHashCode</c> would
        /// differ between the build that wrote a sidecar and the build that reads it back, which
        /// would silently invalidate every cached layout on every launch. Zero is reserved for
        /// "no house file", so the (one in four billion) chance of a real hash being zero is
        /// reported as <c>0xFFFFFFFF</c> rather than as an empty plot.
        /// </remarks>
        public static uint HouseHashFor(long fileLength, DateTime lastWriteUtc)
        {
            unchecked
            {
                const uint offset = 2166136261u;
                const uint prime = 16777619u;
                var ticks = lastWriteUtc.Ticks;
                var hash = offset;
                for (int i = 0; i < 8; i++)
                {
                    hash ^= (byte)(fileLength >> (i * 8));
                    hash *= prime;
                }
                for (int i = 0; i < 8; i++)
                {
                    hash ^= (byte)(ticks >> (i * 8));
                    hash *= prime;
                }
                return hash == 0u ? 0xFFFFFFFFu : hash;
            }
        }

        /// <summary>
        /// The ground type of a lot, from the same table <c>VMTS1Activator.HouseNumToType</c>
        /// applies to the lot's own terrain, so the generated ground around a lot matches the
        /// ground the player walks on inside it. Falls back to <see cref="TerrainType.GRASS"/>,
        /// which is what the activator falls back to.
        /// </summary>
        public static TerrainType TerrainTypeFor(int houseNumber)
        {
            TerrainType type;
            if (VMTS1Activator.HouseNumToType != null
                && VMTS1Activator.HouseNumToType.TryGetValue(houseNumber, out type))
            {
                return type;
            }
            return TerrainType.GRASS;
        }

        /// <summary>
        /// Which edge of a packed lot faces outwards, from its distance to the four map borders.
        /// </summary>
        /// <remarks>
        /// §5.5's pre-road-graph answer: until the road skeleton exists (M2), the edge nearest
        /// the map border is the edge a street would plausibly run past. Ties resolve in the
        /// fixed order north, east, south, west, so the same layout always produces the same
        /// driveway edge - a coin flip here would be a different map from the same seed.
        /// </remarks>
        /// <param name="worldX">The lot's origin.</param>
        /// <param name="worldY">The lot's origin.</param>
        /// <param name="footprint">The lot's packed footprint, margin included.</param>
        /// <param name="mapSize">The map's side length.</param>
        public static NeighbourhoodLotSide DoorSideFor(int worldX, int worldY, int footprint, int mapSize)
        {
            var north = worldY;
            var south = mapSize - (worldY + footprint);
            var west = worldX;
            var east = mapSize - (worldX + footprint);

            if (north <= south && north <= east && north <= west) return NeighbourhoodLotSide.North;
            if (east <= south && east <= west) return NeighbourhoodLotSide.East;
            if (south <= west) return NeighbourhoodLotSide.South;
            return NeighbourhoodLotSide.West;
        }

        // ---- the build ------------------------------------------------------------------------

        /// <summary>
        /// Read a neighbourhood's lots through a content source. Pure with respect to the layout:
        /// the only IO is whatever the source itself does.
        /// </summary>
        /// <remarks>
        /// The skip rules are <see cref="LotLocationParser"/>'s, not new ones - lot 99, a
        /// malformed entry, or a number outside <see cref="short"/> never reaches the packer. A
        /// lot named twice in the table is read once, first entry winning, so the same lot cannot
        /// be placed in two places.
        ///
        /// <para>
        /// The line's x/y are stamped onto the record (<see cref="NeighbourhoodLotRead.MapX"/>/
        /// <see cref="NeighbourhoodLotRead.MapY"/>), for any source, so the value is the one the
        /// <em>line</em> says. The shelf packer only uses them to order the lots.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<NeighbourhoodLotRead> ReadLots(ILotContentSource source, int mode)
        {
            var result = new List<NeighbourhoodLotRead>();
            if (source == null) return result;

            var lines = source.LotLocationLines(mode);
            if (lines == null) return result;

            var seen = new HashSet<short>();
            for (int i = 0; i < lines.Count; i++)
            {
                int number, x, y;
                if (!LotLocationParser.TryParseLotLocation(lines[i], out number, out x, out y)) continue;
                if (number < short.MinValue || number > short.MaxValue) continue;

                var id = (short)number;
                if (!seen.Add(id)) continue;

                NeighbourhoodLotRead lot;
                if (!source.TryReadLot(id, out lot) || lot == null) continue;

                //the lot's real position on the 2D map, in bitmap pixels, from the line this entry
                //came from. The layout records it for the camera, the house buttons and the hit
                //test; the synthetic packer places the rectangle and never reads these back.
                lot.MapX = x;
                lot.MapY = y;
                result.Add(lot);
            }

            return result;
        }

        private static NeighbourhoodLayout Finish(
            int slot, int seed, int mapSize, Placement[] placements)
        {
            var lots = new NeighbourhoodLot[placements.Length];
            for (int i = 0; i < placements.Length; i++)
            {
                lots[i] = placements[i].ToLot(mapSize);
            }

            return new NeighbourhoodLayout(slot, seed, CurrentGeneratorVersion,
                NeighbourhoodLayout.CurrentSchemaVersion, mapSize, lots);
        }

        /// <summary>
        /// The shelf arrangement: lots laid out in rows, in <c>MapY</c> then <c>MapX</c> then
        /// lot-number order, wrapping when a row runs out of map. Pure.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the arrangement the 3D neighbourhood ships with</b> (the only caller is
        /// <c>NeighbourhoodLifecycle</c>). A lot's bitmap position (<c>MapX</c>/<c>MapY</c>) is
        /// recorded for the camera and the 2D hit test but never decides where the rectangle goes:
        /// it only orders the shelf. Every lot gets a distinct rectangle, and the gap between
        /// neighbours is exactly <see cref="StreetCorridorTiles"/> because a shelf advances by
        /// whole footprints - which is what leaves room for the road graph's measured streets.
        /// </para>
        /// </remarks>
        public static NeighbourhoodLayout PackOnGrid(IReadOnlyList<NeighbourhoodLotRead> reads, int slot, int seed)
        {
            return PackOnGrid(reads, slot, seed, DefaultMapSize);
        }

        /// <summary>The shelf arrangement, into a map of the given size. Pure.</summary>
        public static NeighbourhoodLayout PackOnGrid(IReadOnlyList<NeighbourhoodLotRead> reads, int slot, int seed, int mapSize)
        {
            if (mapSize <= 0) throw new ArgumentOutOfRangeException(nameof(mapSize), mapSize, "A generated map has a positive side length.");

            var placements = ShelfPlacements(reads, mapSize);
            return Finish(slot, seed, mapSize, placements);
        }

        /// <summary>
        /// One shelf placement per read record: rows of whole footprints, in
        /// <c>MapY</c>/<c>MapX</c>/number order.
        /// </summary>
        private static Placement[] ShelfPlacements(IReadOnlyList<NeighbourhoodLotRead> reads, int mapSize)
        {
            if (reads == null || reads.Count == 0) return new Placement[0];

            var ordered = new List<NeighbourhoodLotRead>(reads);
            ordered.Sort(CompareForGrid);

            var placements = new Placement[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                placements[i] = new Placement
                {
                    Read = ordered[i],
                    Footprint = FootprintFor(ordered[i].Size),
                    X = 0,
                    Y = 0,
                };
            }

            var x = 0;
            var y = 0;
            var rowHeight = 0;
            for (int i = 0; i < placements.Length; i++)
            {
                var lot = placements[i];
                if (x > 0 && x + lot.Footprint > mapSize)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                }
                lot.X = x;
                lot.Y = y;
                x += lot.Footprint;
                if (lot.Footprint > rowHeight) rowHeight = lot.Footprint;
            }

            Clamp(placements, mapSize);
            return placements;
        }

        // ---- queries --------------------------------------------------------------------------

        /// <summary>How many pairs of lots in a layout share a tile. Zero is what a shelf
        /// arrangement guarantees. Only the tests ask.</summary>
        public static int CountOverlaps(NeighbourhoodLayout layout)
        {
            if (layout == null) return 0;
            return CountOverlaps(layout.Lots, 0);
        }

        /// <summary>
        /// How many pairs of these lots are closer than <paramref name="margin"/> tiles, ignoring
        /// anything else about them. Zero for a shelf layout at
        /// <see cref="StreetCorridorTiles"/>, because a shelf reserves the whole street corridor
        /// between every pair.
        /// </summary>
        /// <param name="lots">The lots to measure.</param>
        /// <param name="margin">The gap two lots have to leave between their rectangles.</param>
        public static int CountOverlaps(IReadOnlyList<NeighbourhoodLot> lots, int margin)
        {
            if (lots == null) return 0;
            var count = 0;
            for (int i = 0; i < lots.Count; i++)
            {
                for (int j = i + 1; j < lots.Count; j++)
                {
                    if (NeighbourhoodLot.Overlaps(lots[i], lots[j], margin)) count++;
                }
            }
            return count;
        }

        // ---- the placement ------------------------------------------------------------------------

        /// <summary>One lot's rectangle. Mutable on purpose - this is the working state.</summary>
        private sealed class Placement
        {
            public NeighbourhoodLotRead Read;
            public int Footprint;
            public int X;
            public int Y;

            /// <summary>
            /// The finished lot: its recorded bitmap position, the rectangle's origin, and the
            /// driveway edge nearest the map border.
            /// </summary>
            public NeighbourhoodLot ToLot(int mapSize)
            {
                var size = ClampSize(Read.Size);
                var door = DoorSideFor(X, Y, Footprint, mapSize);

                return new NeighbourhoodLot
                {
                    Number = Read.Number,
                    Zone = Read.Zone,
                    Size = size,
                    MapX = Read.MapX,
                    MapY = Read.MapY,
                    WorldX = X,
                    WorldY = Y,
                    Rotation = Read.Rotation,
                    TerrainType = Read.TerrainType,
                    BaseHeight = Read.BaseHeight,
                    DoorSide = door,
                    HouseHash = Read.HouseHash,
                    HasHouse = Read.HasHouse
                };
            }
        }

        /// <summary>
        /// The order a shelf is laid out in: <c>MapY</c>, then <c>MapX</c>, then lot number. The
        /// same order the old grid fallback used, kept because a shelf that read the location table
        /// left to right is a shelf a person can look at and recognise.
        /// </summary>
        private static int CompareForGrid(NeighbourhoodLotRead a, NeighbourhoodLotRead b)
        {
            var byY = a.MapY.CompareTo(b.MapY);
            if (byY != 0) return byY;
            var byX = a.MapX.CompareTo(b.MapX);
            if (byX != 0) return byX;
            return a.Number.CompareTo(b.Number);
        }

        private static void Clamp(Placement[] placements, int mapSize)
        {
            for (int i = 0; i < placements.Length; i++)
            {
                Clamp(placements[i], mapSize);
            }
        }

        private static void Clamp(Placement lot, int mapSize)
        {
            var max = Math.Max(0, mapSize - lot.Footprint);
            if (lot.X < 0) lot.X = 0;
            if (lot.X > max) lot.X = max;
            if (lot.Y < 0) lot.Y = 0;
            if (lot.Y > max) lot.Y = max;
        }
    }
}
