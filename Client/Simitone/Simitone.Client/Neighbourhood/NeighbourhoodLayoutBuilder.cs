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
    /// lot-location table, reads each lot's house file, and places every lot at the position the
    /// table gives it in a 512x512 generated map.
    ///
    /// <para>
    /// <b>The split.</b> The build is two activities with nothing in common. Reading is IO:
    /// twenty-odd <c>HouseNN.iff</c> files, an STR chunk, a zoning dictionary, a family
    /// dictionary, and the provider's own idea of where things are. Placing is arithmetic: one
    /// transform, one clamp, done. Only the second is worth testing and only the second is
    /// deterministic, so the reads are captured as data
    /// (<see cref="NeighbourhoodLotRead"/>, produced by an <see cref="ILotContentSource"/>) and
    /// <see cref="Pack(IReadOnlyList{NeighbourhoodLotRead}, int, int, int)"/> consumes nothing
    /// else. A test can hand it four canned lot records - one with no house, one community lot,
    /// two deliberately overlapping - and get a layout back without a game, a VM or a graphics
    /// device anywhere in sight.
    /// </para>
    ///
    /// <para>
    /// <b>The arrangement, and what it used to be.</b> This used to be a packer: a lot's
    /// <c>LotLocations</c> x/y was pushed through the OBJ exporter's isometric projection, the
    /// cloud was fitted into the map, and the overlaps that left were resolved by a bounded
    /// relaxation with a grid as the guaranteed fallback. That was a reconstruction of the map,
    /// and it was wrong in the way §8.1 names: a fit has one scale and one origin for the whole
    /// cloud, so the neighbourhood it drew was the bitmap's arrangement compressed into whatever
    /// shape fitted - a second view of the neighbourhood rather than the neighbourhood.
    /// </para>
    ///
    /// <para>
    /// <b>Now a lot is placed where <c>LotLocations.iff</c> says it is.</b>
    /// <see cref="NeighbourhoodLotRead.MapX"/>/<see cref="NeighbourhoodLotRead.MapY"/> through
    /// <see cref="NeighbourhoodPixelTransform"/>, half a lot back to reach the rectangle's origin,
    /// and clamped into the map. That is the whole of it. The
    /// <c>Seed -&gt; Relax -&gt; Separate -&gt; GridFallback -&gt; Clamp</c> pipeline is gone, and so is
    /// the no-overlap invariant it existed to keep - which is correct, because TSO's own
    /// <c>RestoreSurroundings</c> puts two neighbouring lots <c>Size - 2</c> tiles apart, so two
    /// <em>real</em> lots overlap by two tiles on purpose and any invariant that forbade it was
    /// forbidding the truth. What replaced it is the one that shows on screen: a road is never drawn
    /// inside a lot, which <see cref="RoadGraph"/>'s own <c>Mark</c> refuses and which holds however
    /// the rectangles are arranged.
    /// </para>
    ///
    /// <para>
    /// <b>The pitch is still here, because it is the street-width contract and not an
    /// arrangement.</b> A lot's footprint is <see cref="FootprintFor"/>: its own side plus
    /// <see cref="StreetCorridorTiles"/>, and that number was two, which is a sidewalk and not a
    /// street - it is the reason the surround used to draw a street band of its own that no lot
    /// agreed with, since the lot view draws
    /// <see cref="SimitoneLotViewRoad.StreetCorridorTiles"/> of corridor and each lot's own
    /// <c>RestoreRoad</c> band reaches six tiles <em>into</em> the lot. It is still what the road
    /// graph's <see cref="RoadGraph.MainStreetCorridorWidth"/> is measured against, what
    /// <see cref="NeighbourhoodMapAdapter.StreetCorridorMapTiles"/> restates, and what the lot
    /// view's own band needs to fit - but it no longer decides where anything stands.
    /// </para>
    ///
    /// <para>
    /// <b>What is still here from the packing era, and why.</b> <see cref="ClampSize"/>,
    /// <see cref="FootprintFor"/> and <see cref="StreetCorridorTiles"/> because they are the
    /// street-width contract and are read by the road graph, the adapter and the lot view.
    /// <see cref="DoorSideFor"/> because it is still the answer for a neighbourhood with no
    /// inferred road mask, and because <c>NeighbourhoodLayoutPatch</c> calls it.
    /// <see cref="ProjectMapPosition"/> because it is the OBJ exporter's transform and the meshes
    /// already exported through it must not move. <see cref="PackOnGrid"/> because a shelf packing
    /// is the only arrangement available for a layout whose bitmap positions are all zero, which is
    /// what a corrupt or absent location table produces. <see cref="MaxRelaxationPasses"/> because
    /// <c>NeighbourhoodLayoutPatch</c> bounds its relocation sweep by it. None of them chooses a
    /// position any more: they are queries, one documented fallback, and two compatibility
    /// transforms.
    /// </para>
    /// </summary>
    public static class NeighbourhoodLayoutBuilder
    {
        /// <summary>
        /// The layout algorithm's version. Stamped onto every layout and checked on load, so a
        /// layout packed by a different arrangement rule is rebuilt rather than trusted.
        /// </summary>
        /// <remarks>
        /// <b>Seventy-one, and the jump is the point.</b> Two, because the pitch changed: version
        /// one packed at <c>Size + 2</c> and every cached sidecar in the world is a two-tile gap,
        /// which is the layout that cannot draw a street. Seventy-one, because Part 8 replaced the
        /// packer outright: a lot is now at the tile <see cref="NeighbourhoodPixelTransform"/> puts
        /// it at, so every sidecar written by any earlier version is a synthetic arrangement and
        /// every raster built from one has its lots in the wrong places relative to each other. The
        /// number is not a sequence - §8.4 froze it here because "which world is this" must not be
        /// answered by a comparison against a number someone might have reused.
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
        /// How many relaxation sweeps to attempt before declaring an arrangement a failure and
        /// falling back. Bounded on purpose: the relaxation is a heuristic on a heuristic, and a
        /// layout must not depend on it converging.
        /// </summary>
        /// <remarks>
        /// No longer this file's own bound - <see cref="Pack"/> does not relax anything - but it is
        /// still the constant <c>NeighbourhoodLayoutPatch</c> bounds its relocation sweep by, and
        /// that call site is not this file's to change. Kept as the one spelling of the number
        /// rather than restated there.
        /// </remarks>
        public const int MaxRelaxationPasses = 64;

        /// <summary>
        /// How far <see cref="TryDoorSideFromRoad"/> probes outward from a lot's edge looking for
        /// the street its driveway should reach. Six, because that is how far the lot view's own
        /// <c>RestoreRoad</c> bands reach into a lot: a road six tiles outside the property line is
        /// one the lot already draws its own half of, so anything further out is a different street.
        /// </summary>
        public const int DoorProbeDepth = 6;

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

        /// <summary>
        /// Which edge of a lot faces the road, asked of the inferred connector mask: the edge with
        /// the most road within <see cref="DoorProbeDepth"/> tiles of it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is Part 8's replacement for <see cref="DoorSideFor"/>, and it is a question rather
        /// than a guess. "The edge nearest a map border" was always a proxy for "the edge a street
        /// runs past", chosen because no street skeleton existed when it was written; now one does,
        /// in the pixels of the 2D map, and the driveway should leave by the edge the player's own
        /// map shows the road on.
        /// </para>
        /// <para>
        /// <b>Ties resolve north, east, south, west</b>, the same fixed order
        /// <see cref="DoorSideFor"/> uses, so the same layout always produces the same driveways.
        /// A corner lot with a street on two sides is genuinely two-sided and the order is the whole
        /// of the decision; the lot view's own <c>RoadByteFor</c> will then report both edges as
        /// having road, so a corner house draws road on both of the sides its corner touches.
        /// </para>
        /// <para>
        /// <b>A <c>false</c> here means "ask the old way".</b> An empty mask, a mask of the wrong
        /// size, or a lot walled in on all four sides all answer <c>false</c>, and the caller falls
        /// back to <see cref="DoorSideFor"/> - which is the right answer for a neighbourhood with no
        /// backdrop at all rather than a worse one.
        /// </para>
        /// </remarks>
        /// <param name="worldX">The lot rectangle's origin x, in tiles.</param>
        /// <param name="worldY">The lot rectangle's origin y, in tiles.</param>
        /// <param name="size">The lot's own side, in tiles.</param>
        /// <param name="roadMask">
        /// The connector mask, row-major in a <paramref name="mapSize"/> square. Not clipped of
        /// lot interiors: a caller may pass either the raw inferred mask or the clipped one, and the
        /// probe is outside the rectangle either way.
        /// </param>
        /// <param name="mapSize">The map's side length, in tiles.</param>
        /// <param name="side">The edge the road is on, when the answer is <c>true</c>.</param>
        public static bool TryDoorSideFromRoad(
            int worldX, int worldY, int size, bool[] roadMask, int mapSize, out NeighbourhoodLotSide side)
        {
            side = NeighbourhoodLotSide.North;
            if (roadMask == null || mapSize < 1) return false;
            if ((long)roadMask.LongLength != (long)mapSize * mapSize) return false;

            var lot = Math.Max(1, size);
            var right = worldX + lot;
            var bottom = worldY + lot;

            var bestCount = 0;
            var bestSide = NeighbourhoodLotSide.North;

            //north, east, south, west - the same order DoorSideFor resolves its ties in, and the
            //reason a tie is not a coin flip
            for (int candidate = 0; candidate < 4; candidate++)
            {
                var count = 0;
                for (int depth = 1; depth <= DoorProbeDepth; depth++)
                {
                    for (int t = 0; t < lot; t++)
                    {
                        int x, y;
                        switch (candidate)
                        {
                            case 0: x = worldX + t; y = worldY - depth; break;
                            case 1: x = right + depth; y = worldY + t; break;
                            case 2: x = worldX + t; y = bottom + depth; break;
                            default: x = worldX - depth; y = worldY + t; break;
                        }
                        if (x < 0 || y < 0 || x >= mapSize || y >= mapSize) continue;
                        if (roadMask[y * mapSize + x]) count++;
                    }
                }

                if (count > bestCount)
                {
                    bestCount = count;
                    bestSide = (NeighbourhoodLotSide)candidate;
                }
            }

            if (bestCount <= 0) return false;
            side = bestSide;
            return true;
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
        /// <b>The line's x/y are stamped onto the record, and that is the whole of Part 8 §8.2's
        /// load-bearing bug.</b> They were parsed and thrown away, so every real lot arrived at the
        /// packer at (0, 0), the seed was degenerate, and no amount of arithmetic downstream could
        /// recover the position the neighbourhood's own file states. They are written here rather
        /// than only in <c>TS1LotContentSource</c> so that the value is the one the <em>line</em>
        /// says, for any source, and so a source that does not know about the table still gets the
        /// right answer.
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

        /// <summary>
        /// Place captured lot records into a layout, at their real positions. Pure: no disk, no
        /// provider, no graphics, no time, and no dependence on the order the records arrived in.
        /// </summary>
        public static NeighbourhoodLayout Pack(IReadOnlyList<NeighbourhoodLotRead> reads, int slot, int seed)
        {
            return Pack(reads, slot, seed, DefaultMapSize);
        }

        /// <summary>
        /// Place captured lot records into a layout of the given map size, at their real positions.
        /// Pure: no disk, no provider, no graphics, no time, and no dependence on the order the
        /// records arrived in.
        /// </summary>
        /// <remarks>
        /// <b>Which connector mask this uses is the one bound to <paramref name="slot"/>, if
        /// any.</b> The mask decides which edge each lot's driveway leaves from and nothing else -
        /// the generator is handed it separately, through
        /// <see cref="NeighbourhoodMapAdapter.InputFor"/>, which looks it up by the same slot. A
        /// slot with nothing bound gets <see cref="DoorSideFor"/>, which is the pre-Part-8 answer and
        /// the right one for a neighbourhood whose backdrop could not be read.
        /// </remarks>
        /// <param name="reads">The lot records, whose <c>MapX</c>/<c>MapY</c> are the real positions.</param>
        /// <param name="slot">The neighbourhood slot id to stamp on the result.</param>
        /// <param name="seed">The world seed. Recorded, and not used to place anything.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static NeighbourhoodLayout Pack(IReadOnlyList<NeighbourhoodLotRead> reads, int slot, int seed, int mapSize)
        {
            if (mapSize <= 0) throw new ArgumentOutOfRangeException(nameof(mapSize), mapSize, "A generated map has a positive side length.");

            var placements = MapPlacements(reads, mapSize);
            return Finish(slot, seed, mapSize, placements, null);
        }

        /// <summary>
        /// One placement per read record, at the tile <see cref="NeighbourhoodPixelTransform"/> puts
        /// it at, clamped into the map.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Ordered by lot number first, so the layout cannot depend on the order the location table
        /// happened to be in - which was true of the packer and is just as true of this.
        /// </para>
        /// <para>
        /// <b>Nothing is pushed apart and nothing is nudged.</b> Two real lots whose rectangles
        /// overlap are two real lots the map put there; the generator's job is to draw them where
        /// they are and to keep the streets out of them, which is a job the road graph and the lot
        /// mask already do. A packer that separated them would be inventing a neighbourhood that
        /// does not exist, and would do it differently at every map size - which is exactly the
        /// failure §8.1's "one connected map, not two views" is about.
        /// </para>
        /// </remarks>
        private static Placement[] MapPlacements(IReadOnlyList<NeighbourhoodLotRead> reads, int mapSize)
        {
            if (reads == null || reads.Count == 0) return new Placement[0];

            var ordered = new List<NeighbourhoodLotRead>(reads);
            ordered.Sort(CompareByNumber);

            var placements = new Placement[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                var read = ordered[i];
                var size = ClampSize(read.Size);
                var origin = NeighbourhoodPixelTransform.ClampOrigin(
                    NeighbourhoodPixelTransform.OriginForCentre(
                        new Vector2(read.MapX, read.MapY), size, mapSize),
                    size, mapSize);

                placements[i] = new Placement
                {
                    Read = read,
                    Footprint = FootprintFor(read.Size),
                    X = origin.X,
                    Y = origin.Y,
                };
            }

            return placements;
        }

        private static NeighbourhoodLayout Finish(
            int slot, int seed, int mapSize, Placement[] placements, bool[] roadMask)
        {
            var lots = new NeighbourhoodLot[placements.Length];
            for (int i = 0; i < placements.Length; i++)
            {
                lots[i] = placements[i].ToLot(mapSize, roadMask);
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
        /// <b>This is not the arrangement any more, and nothing in the game calls it.</b> It is kept
        /// for the case <see cref="Pack"/> cannot answer: a neighbourhood whose location table is
        /// missing, empty or corrupt has every lot at bitmap (0, 0), every lot maps to the map's
        /// top-left corner, and twenty-odd lots stacked on one tile is not a neighbourhood anybody
        /// can play. A shelf packing is the honest answer to "these lots have no positions": it
        /// invents nothing, it only gives each lot a distinct rectangle, and the gap between
        /// neighbours is exactly <see cref="StreetCorridorTiles"/> because a shelf advances by whole
        /// footprints.
        /// </para>
        /// <para>
        /// A test can also call it to assert that guarantee - no overlaps, a corridor between every
        /// pair - without having to construct a neighbourhood with no positions at all, which is
        /// what the packer's own fallback used to be for.
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
            return Finish(slot, seed, mapSize, placements, null);
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

        /// <summary>Read a neighbourhood through a content source and place it, in one call.</summary>
        public static NeighbourhoodLayout Build(ILotContentSource source, int mode, int slot, int seed)
        {
            return Build(source, mode, slot, seed, DefaultMapSize);
        }

        /// <summary>
        /// Read a neighbourhood through a content source and place it, in one call - and bind the
        /// mode's connector mask to the slot so the generator and the door sides can both find it.
        /// </summary>
        /// <remarks>
        /// The binding is the one piece of Part 8's seam that cannot be derived: the reader of a
        /// neighbourhood knows the mode, the generator of a map knows the slot, and
        /// <see cref="NeighbourhoodLayout"/> carries neither. A caller that reads and places in two
        /// separate steps - <see cref="ReadLots"/> then <see cref="Pack"/>, which is what
        /// <c>NeighbourhoodLifecycle</c> does - has to make the same call itself.
        /// </remarks>
        public static NeighbourhoodLayout Build(ILotContentSource source, int mode, int slot, int seed, int mapSize)
        {
            var reads = ReadLots(source, mode);
            return Pack(reads, slot, seed, mapSize);
        }

        /// <summary>
        /// Build a neighbourhood's layout straight from the game's provider. The impure entry
        /// point; everything after the reads is the same pure <c>Pack</c> the tests drive.
        /// </summary>
        public static NeighbourhoodLayout Build(TS1NeighborhoodProvider provider, int mode, int slot, int seed)
        {
            return Build(provider, mode, slot, seed, DefaultMapSize);
        }

        /// <summary>
        /// Build a neighbourhood's layout straight from the game's provider. The impure entry
        /// point; everything after the reads is the same pure <c>Pack</c> the tests drive.
        /// </summary>
        public static NeighbourhoodLayout Build(TS1NeighborhoodProvider provider, int mode, int slot, int seed, int mapSize)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            return Build(new TS1LotContentSource(provider, mode), mode, slot, seed, mapSize);
        }

        // ---- queries --------------------------------------------------------------------------

        /// <summary>How many pairs of lots in a layout share a tile. Zero is what a shelf
        /// arrangement guarantees; a map-derived arrangement does not promise it, because TSO's own
        /// lot pitch is <c>Size - 2</c> and two real lots therefore overlap. Exposed because
        /// "no overlaps" is otherwise an assertion about somebody else's arithmetic.</summary>
        public static int CountOverlaps(NeighbourhoodLayout layout)
        {
            if (layout == null) return 0;
            return CountOverlaps(layout.Lots, 0);
        }

        /// <summary>
        /// How many pairs of these lots are closer than <paramref name="margin"/> tiles, ignoring
        /// anything else about them. Zero for a shelf layout at
        /// <see cref="StreetCorridorTiles"/>, because a shelf reserves the whole street corridor
        /// between every pair; non-zero is the normal, correct answer for a map-derived layout,
        /// whose lots sit at TSO's own <c>Size - 2</c> pitch.
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
            /// The finished lot: its real bitmap position, the rectangle's origin, and a driveway
            /// edge chosen from the connector mask when there is one.
            /// </summary>
            public NeighbourhoodLot ToLot(int mapSize, bool[] roadMask)
            {
                var size = ClampSize(Read.Size);
                NeighbourhoodLotSide door;
                if (!TryDoorSideFromRoad(X, Y, size, roadMask, mapSize, out door))
                {
                    door = DoorSideFor(X, Y, Footprint, mapSize);
                }

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

        private static int CompareByNumber(NeighbourhoodLotRead a, NeighbourhoodLotRead b)
        {
            return a.Number.CompareTo(b.Number);
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
