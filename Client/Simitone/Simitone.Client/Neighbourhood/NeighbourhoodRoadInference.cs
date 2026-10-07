/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using FSO.Content;
using FSO.Content.Model;
using FSO.Files.FAR1;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Infers the connector mask - which tiles of the generated map are street - from the 2D
    /// neighbourhood map, in the same tile space as <see cref="NeighbourhoodMap.RoadData"/>.
    ///
    /// <para><b>Why this exists.</b> §8.1's third requirement: the streets, driveways and paths
    /// between lots are <em>derived from the 2D map</em>, because lots are often not directly
    /// adjacent and a street drawn by a gap-measuring rule is not the street the player saw. The
    /// investigated facts (§8.2) are that there is no road art and no road mask anywhere in the
    /// tree - the only road textures are TSO's generated-world atlas - and that the roads are
    /// therefore just pixels in the per-mode backdrop bitmap, which every neighbourhood has, which
    /// is 800x600 8-bit indexed, and which is readable headlessly through
    /// <c>ITextureRef.GetImage()</c>.</para>
    ///
    /// <para><b>How the pixels are classified.</b> The decoded bitmap is BGRA, and the two editions
    /// paint their roads differently - a US map is grey tarmac, an international one is black - so
    /// the test is not "is it this exact colour" but the two properties both have: <b>no chroma</b>
    /// (<c>max(R,G,B) - min(R,G,B)</c> under <see cref="MaxRoadChroma"/>) and a <b>dark-to-medium
    /// luminance</b> (<see cref="MinRoadLuminance"/>..<see cref="MaxRoadLuminance"/>). Grass is
    /// green, the sea is blue, the sand is yellow and the trees are green, so all of them fail the
    /// chroma test; a road - grey or black - cannot. The remaining false positives are the roofs
    /// and the kerbs, and a roof is inside a lot, so the road graph clips it out of the mask
    /// exactly as it clips its own edges.</para>
    ///
    /// <para><b>The tile test.</b> Each map tile reads the pixel rectangle
    /// <see cref="NeighbourhoodPixelTransform.PixelBoundsForTile"/> gives it and is road when at
    /// least <see cref="TileRoadFraction"/> of those pixels classify. The fraction rather than any
    /// single pixel, because at 512 tiles across a tile covers 1.56 pixels and a one-pixel test is
    /// a coin flip; at a small map size a tile covers hundreds of pixels and "any" would make a
    /// kerb a road.</para>
    ///
    /// <para><b>Where the file actually is.</b> The 2D panel's own table gives an install-relative
    /// name - <c>Community\NScreen_unleashed.bmp</c> - and that name is both the loose file an
    /// unpacked install has and the entry name inside <c>UIGraphics\UIGraphics.far</c>. The caller
    /// that decodes the backdrop has only ever looked for the loose file under
    /// <c>UserData</c>, where TS1 keeps a neighbourhood's <em>own</em> picture, and on a stock
    /// install the file is not there, so the mask was the lattice fallback on every machine that
    /// had not been hand-unpacked. <see cref="FromInstall"/> is the route that answers the question
    /// the caller could not: it tries the loose file and then every <c>.far</c> in the install, and
    /// only then the lattice. <see cref="ForBitmap"/> is where that happens, because
    /// <see cref="For"/> and <see cref="BuildFromBitmap"/> are the pure seams.</para>
    ///
    /// <para><b>When it gives up.</b> Four ways, all of them answered rather than thrown, and all
    /// of them ending at the same deterministic fallback: no backdrop at all (an install without
    /// the file, a provider that cannot name it), a bitmap that will not decode, a classification
    /// that is degenerate - nothing at all, or nearly everything, which is what a solid fill or a
    /// mis-decoded palette looks like - and, for <see cref="ForBitmap"/>, a caller that had no
    /// pixels to offer and no archive to find them in. The fallback is
    /// <see cref="BuildFromLattice"/>: cluster the lot centres into rows and columns by
    /// proximity, and lay a street down each gap between neighbours. It needs no pixels, so it is
    /// the same answer on every machine and every launch, and it is the reason §8.7's "pixel road
    /// inference is new and heuristic" is a risk rather than a blocker.</para>
    ///
    /// <para><b>Where the result goes.</b> <see cref="Build"/> returns a
    /// <c>MapSize * MapSize</c> <c>bool[]</c> in exactly the space <see cref="NeighbourhoodMap"/>
    /// indexes by <c>y * width + x</c>, which is what
    /// <see cref="NeighbourhoodMapAdapter.InputFor"/> carries on
    /// <see cref="NeighbourhoodMapInput.RoadMask"/> and what <see cref="RoadGraph"/> rasterises.
    /// Nothing here writes into a map, so the whole classification is testable with a synthetic
    /// bitmap and no game.</para>
    ///
    /// <para><b>The cache.</b> §8.7 asks for "a per-mode cache keyed like the raster cache". The
    /// decode and the classification are the expensive part and depend on the mode alone, so
    /// <see cref="For"/> memoises them per (mode, map size). The slot binding below is the second
    /// half of the seam and exists because the two callers that need the mask do not both know
    /// everything: the pipeline that reads a neighbourhood's lots knows the <em>mode</em>, and the
    /// pipeline that generates a map knows the <em>slot</em>, so something has to carry the mode to
    /// the slot. <see cref="Bind"/> is that something, and it is explicit rather than inferred -
    /// a guess would be wrong the moment two neighbourhoods are generated at once.</para>
    /// </summary>
    public static class NeighbourhoodRoadInference
    {
        // ---- the classification ---------------------------------------------------------------

        /// <summary>
        /// The largest <c>max(R,G,B) - min(R,G,B)</c> a road pixel may have. 48 is roughly a fifth
        /// of the way to a saturated colour, which separates every tarmac in the six maps from
        /// every grass, sea, sand and tree in them without being tight enough to reject a road with
        /// a JPEG-ish smudge or a compression ring in it.
        /// </summary>
        public const int MaxRoadChroma = 48;

        /// <summary>
        /// The largest <c>max(R,G,B) - min(R,G,B)</c> a road pixel may have <em>relative to its own
        /// brightness</em>: the test rejects a pixel when <c>(hi - lo) * 255</c> is more than this
        /// many times <c>hi</c>, so a saturated hue is rejected at any luminance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why <see cref="MaxRoadChroma"/> alone is not enough, and this is the bug it was
        /// added for.</b> The absolute chroma test is scale-free, and a dark pixel has a small
        /// absolute spread no matter what hue it is: the real <c>NScreen_unleashed.bmp</c> paints
        /// its tree canopies and shadowed ground around <c>(12,28,12)</c> and <c>(21,36,12)</c>,
        /// whose spread is 16 and 15 - comfortably under 48 - so every tree was classified as
        /// tarmac. On that map the mask came out at 31% of the bitmap and 81,586 tiles, most of it
        /// forest, which drew as a dense lattice of road tiles across the whole neighbourhood.
        /// A road is grey: its channels are close to its own brightness. A dark green is not, and
        /// the ratio is what tells them apart.
        /// </para>
        /// <para>
        /// 40, so the chroma may be at most about a sixth of the brightest channel. The real grey
        /// tarmac and kerbs sit at 0-8% of their brightness (the same pixels the absolute test
        /// already admitted), and the tree and shadow pixels sit at 50% and up, so the gap is wide
        /// and the exact number is not delicate. See <c>IsRoadPixel</c>.
        /// </para>
        /// </remarks>
        public const int MaxRoadSaturation = 40;

        /// <summary>
        /// The darkest a road pixel may be. 8 rather than 0 because the international maps' black
        /// asphalt is very dark but never literally 0, and a pixel of pure black in an 8-bit indexed
        /// palette is far more often an unset palette entry than a road.
        /// </summary>
        public const int MinRoadLuminance = 8;

        /// <summary>
        /// The lightest a road pixel may be. 200 rather than 255 because a US map's kerbside
        /// pavement is a light grey and a lot's own lawn in a bright neighbourhood is a pale
        /// yellow-green - the chroma test is what rejects the second, and this bound is only there
        /// to stop a white highlight reading as tarmac.
        /// </summary>
        public const int MaxRoadLuminance = 200;

        /// <summary>
        /// How much of a tile's pixel rectangle has to classify as road before the tile is road.
        /// A third, which is the point at which a tile that is half road and half pavement is
        /// street and a tile that is a road's shadow or a painted lane marking is not.
        /// </summary>
        public const double TileRoadFraction = 1.0 / 3.0;

        /// <summary>
        /// Below this share of the whole bitmap, a classification is treated as having found
        /// nothing and the lattice fallback is used instead. A neighbourhood map always has visible
        /// streets; one twentieth of its area being road is already a very sparse one.
        /// </summary>
        public const double MinRoadShare = 0.05;

        /// <summary>
        /// Above this share, a classification is treated as degenerate - a solid fill, or a palette
        /// that decoded to one flat colour - and the lattice fallback is used instead. Half the map
        /// being "road" is a picture of a tarmac quarry, not a neighbourhood.
        /// </summary>
        public const double MaxRoadShare = 0.5;

        // ---- where a backdrop was found -----------------------------------------------------------

        /// <summary>
        /// One install-relative file, decoded: the pixels, where they were read from, and why they
        /// are <c>null</c> when there are none.
        /// </summary>
        public sealed class InstalledBackdrop
        {
            /// <summary>The decoded pixels, or <c>null</c>.</summary>
            public TexBitmap Bitmap { get; }

            /// <summary>
            /// Where they came from: a full path for a loose file, or the archive's
            /// install-relative path for an entry.
            /// </summary>
            public string Source { get; }

            /// <summary>Why there is no bitmap, or <c>null</c> when there is one.</summary>
            public string Failure { get; }

            internal InstalledBackdrop(TexBitmap bitmap, string source, string failure)
            {
                Bitmap = bitmap;
                Source = source;
                Failure = failure;
            }
        }

        // ---- the entry points -----------------------------------------------------------------

        /// <summary>
        /// The connector mask for a neighbourhood mode: the mode's backdrop bitmap read through
        /// <paramref name="backdrop"/> and classified into tile space, or the deterministic
        /// lot-lattice fallback if that is not possible.
        /// </summary>
        /// <param name="mode">The neighbourhood mode, 1..8. Names the backdrop and the reason text.</param>
        /// <param name="backdrop">
        /// The mode's backdrop as a texture reference, or <c>null</c> - which is one of the three
        /// documented ways to end up on the fallback rather than a special case.
        /// </param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="lots">The lots, which only the fallback consults.</param>
        /// <param name="reason">
        /// What happened, in one sentence: which of the two sources answered, and for a fallback why
        /// the pixels could not. Never null, so it can be logged unconditionally.
        /// </param>
        /// <returns>
        /// A <c>mapSize * mapSize</c> row-major mask in <see cref="NeighbourhoodMap.RoadData"/>'s
        /// own space. Never null and never short.
        /// </returns>
        public static bool[] Build(
            ushort mode, ITextureRef backdrop, int mapSize,
            IReadOnlyList<NeighbourhoodLot> lots, out string reason)
        {
            if (mapSize < 1) throw new ArgumentOutOfRangeException(nameof(mapSize), mapSize,
                "an inferred road mask needs a map of at least one tile across.");

            var name = Describe(mode);
            if (backdrop == null)
            {
                return BuildFromLattice(lots, mapSize, out reason,
                    "the backdrop " + (name ?? "for mode " + mode) + " could not be named, so the"
                    + " connectors were inferred from the lot lattice instead");
            }

            TexBitmap bitmap;
            try
            {
                bitmap = backdrop.GetImage();
            }
            catch (Exception e)
            {
                //A bitmap that will not decode is not a failure the neighbourhood can act on; it is
                //the documented second way to reach the fallback, and the exception is named in
                //the reason rather than thrown.
                return BuildFromLattice(lots, mapSize, out reason,
                    "the backdrop '" + name + "' could not be decoded (" + e.Message
                    + "), so the connectors were inferred from the lot lattice instead");
            }

            if (bitmap == null || bitmap.Data == null
                || bitmap.Width < 1 || bitmap.Height < 1 || bitmap.PixelSize < 3)
            {
                return BuildFromLattice(lots, mapSize, out reason,
                    "the backdrop '" + name + "' decoded to nothing, so the connectors were inferred"
                    + " from the lot lattice instead");
            }

            return BuildFromBitmap(mode, bitmap, mapSize, lots, out reason);
        }

        /// <summary>
        /// The connector mask for a backdrop that has already been decoded. The seam a test uses:
        /// a synthetic <see cref="TexBitmap"/> is a complete input, so the classification can be
        /// argued about with no game content and no provider.
        /// </summary>
        /// <param name="mode">The neighbourhood mode, for the reason text.</param>
        /// <param name="bitmap">The decoded backdrop. BGRA if <c>PixelSize</c> is 4.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="lots">The lots, which only the fallback consults.</param>
        /// <param name="reason">What happened. Never null.</param>
        public static bool[] BuildFromBitmap(
            ushort mode, TexBitmap bitmap, int mapSize,
            IReadOnlyList<NeighbourhoodLot> lots, out string reason)
        {
            return BuildFromBitmap(mode, bitmap, mapSize, lots, out reason, null);
        }

        /// <summary>
        /// <see cref="BuildFromBitmap"/> with a note appended to the success reason, so a mask read
        /// out of an archive says which archive it came from.
        /// </summary>
        /// <param name="mode">The neighbourhood mode.</param>
        /// <param name="bitmap">The decoded backdrop.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="lots">The lots, for the fallback.</param>
        /// <param name="reason">What happened. Never null.</param>
        /// <param name="readNote">Where the pixels came from, or <c>null</c> when the caller knows.</param>
        private static bool[] BuildFromBitmap(
            ushort mode, TexBitmap bitmap, int mapSize,
            IReadOnlyList<NeighbourhoodLot> lots, out string reason, string readNote)
        {
            if (mapSize < 1) throw new ArgumentOutOfRangeException(nameof(mapSize), mapSize,
                "an inferred road mask needs a map of at least one tile across.");
            var name = Describe(mode);

            if (!Usable(bitmap))
            {
                return BuildFromLattice(lots, mapSize, out reason,
                    "the backdrop '" + name + "' decoded to nothing, so the connectors were inferred"
                    + " from the lot lattice instead");
            }

            var mask = new bool[mapSize * mapSize];
            var total = 0L;
            var road = 0L;
            var stride = bitmap.PixelSize;

            for (int ty = 0; ty < mapSize; ty++)
            {
                for (int tx = 0; tx < mapSize; tx++)
                {
                    NeighbourhoodPixelTransform.PixelBoundsForTile(
                        tx, ty, mapSize, out var left, out var top, out var right, out var bottom);
                    if (right > bitmap.Width) right = bitmap.Width;
                    if (bottom > bitmap.Height) bottom = bitmap.Height;

                    var sampled = 0;
                    var hits = 0;
                    for (int py = top; py < bottom; py++)
                    {
                        var row = py * bitmap.Width;
                        for (int px = left; px < right; px++)
                        {
                            var at = (row + px) * stride;
                            if (at + 2 >= bitmap.Data.Length) continue;
                            sampled++;
                            if (IsRoadPixel(bitmap.Data[at], bitmap.Data[at + 1], bitmap.Data[at + 2]))
                            {
                                hits++;
                            }
                        }
                    }

                    total += sampled;
                    road += hits;
                    //a tile with no pixels at all - a bitmap smaller than the map, at a corner -
                    //is not road, and saying so by counting zero samples keeps the share honest
                    if (sampled > 0 && (double)hits / sampled >= TileRoadFraction)
                    {
                        mask[ty * mapSize + tx] = true;
                    }
                }
            }

            var share = total > 0 ? (double)road / total : 0.0;
            if (share < MinRoadShare)
            {
                return BuildFromLattice(lots, mapSize, out reason,
                    "only " + Percent(share) + " of '" + name + "' reads as tarmac, which is not a"
                    + " road network, so the connectors were inferred from the lot lattice instead");
            }
            if (share > MaxRoadShare)
            {
                return BuildFromLattice(lots, mapSize, out reason,
                    Percent(share) + " of '" + name + "' reads as tarmac, which is a flat fill rather"
                    + " than a road network, so the connectors were inferred from the lot lattice"
                    + " instead");
            }

            //"read from" is not decoration: CameFromBitmap is a substring test on this line and it is how a
            //caller tells the two sources apart without keeping the mask's provenance separately
            reason = "the connectors were " + (readNote ?? "read from") + " '" + name + "': "
                + Percent(share) + " of its pixels are low-chroma tarmac, giving "
                + Count(mask) + " road tile(s)";
            return mask;
        }

        /// <summary>
        /// Whether one decoded pixel is tarmac: no chroma, and dark-to-medium brightness.
        /// </summary>
        /// <param name="r">Red, or blue in a BGRA buffer - the test is symmetric in the two.</param>
        /// <param name="g">Green.</param>
        /// <param name="b">Blue, or red in a BGRA buffer.</param>
        public static bool IsRoadPixel(int r, int g, int b)
        {
            var hi = r > g ? (r > b ? r : b) : (g > b ? g : b);
            var lo = r < g ? (r < b ? r : b) : (g < b ? g : b);
            if (hi - lo > MaxRoadChroma) return false;

            //A road is grey at every brightness, and the absolute spread above cannot see that in
            //the dark: a dark green canopy has a small spread and a large hue. This is the same
            //"no chroma" question asked relative to the pixel's own brightness, so it rejects the
            //dark saturated pixels the absolute test lets through and keeps every grey the test
            //above admits. See MaxRoadSaturation.
            if (hi > 0 && (hi - lo) * 255 > MaxRoadSaturation * hi) return false;

            //(77, 150, 29) is the BT.601 luma weights, kept integer so the classification is
            //bit-identical on every machine - which matters, because the raster cache stamp is
            //computed from the mask this produces.
            var luma = (77 * r + 150 * g + 29 * b) >> 8;
            return luma >= MinRoadLuminance && luma <= MaxRoadLuminance;
        }

        // ---- the fallback ---------------------------------------------------------------------

        /// <summary>
        /// The connector mask when there are no pixels to read: cluster the lots into rows and
        /// columns by proximity, and lay a street down every gap between two neighbours.
        /// </summary>
        /// <param name="lots">The lots, in any order.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="reason">What happened. Never null.</param>
        /// <remarks>
        /// <para>
        /// <b>Deterministic and pixel-free, which is the whole of its virtue.</b> The lot
        /// rectangles are already in tile space - they came through
        /// <see cref="NeighbourhoodPixelTransform"/> - so a "row" is a run of lots whose y
        /// intervals overlap by more than half, and a "column" is the same in x. The gap between
        /// two lots in the same row, and the gap between two in the same column, is where a street
        /// goes, and it is as wide as the gap allows up to
        /// <see cref="RoadGraph.MainStreetWidth"/>.
        /// </para>
        /// <para>
        /// <b>Why proximity and not a grid.</b> A grid would need a pitch, and the pitch is
        /// precisely what is not known any more: the lots are where the map put them, so two
        /// neighbours can be four tiles apart in one row and eighty in the next. Proximity finds
        /// both without a pitch.
        /// </para>
        /// <para>
        /// <b>What it is not.</b> It is not a reconstruction of the map's streets - it is the
        /// skeleton §6.1 already described, computed from the real positions instead of from a
        /// fit. It is the answer when the pixels are unavailable, not the answer when they are.
        /// </para>
        /// </remarks>
        public static bool[] BuildFromLattice(
            IReadOnlyList<NeighbourhoodLot> lots, int mapSize, out string reason)
        {
            return BuildFromLattice(lots, mapSize, out reason, null);
        }

        private static bool[] BuildFromLattice(
            IReadOnlyList<NeighbourhoodLot> lots, int mapSize, out string reason, string because)
        {
            if (mapSize < 1) throw new ArgumentOutOfRangeException(nameof(mapSize), mapSize,
                "an inferred road mask needs a map of at least one tile across.");

            var mask = new bool[mapSize * mapSize];
            var placed = new List<Rect>();
            if (lots != null)
            {
                for (int i = 0; i < lots.Count; i++)
                {
                    var lot = lots[i];
                    if (lot == null) continue;
                    var size = Math.Max(1, lot.Size);
                    var rect = new Rect(lot.WorldX, lot.WorldY, size);
                    if (rect.Right <= 0 || rect.Bottom <= 0 || rect.Left >= mapSize || rect.Top >= mapSize)
                    {
                        continue;
                    }
                    placed.Add(rect);
                }
            }

            //as written in the type remarks: rows and columns are found by overlap, not by a pitch,
            //and the street is whatever free ground two neighbours leave between them
            for (int i = 0; i < placed.Count; i++)
            {
                for (int j = i + 1; j < placed.Count; j++)
                {
                    var a = placed[i];
                    var b = placed[j];

                    if (Overlap(a.Top, a.Bottom, b.Top, b.Bottom) > a.Size / 2
                        && b.Right > a.Right)
                    {
                        LayCorridor(mask, mapSize, a.Right, b.Left, a.Top, a.Bottom, gapOnX: true);
                    }
                    if (Overlap(a.Left, a.Right, b.Left, b.Right) > a.Size / 2
                        && b.Bottom > a.Bottom)
                    {
                        LayCorridor(mask, mapSize, a.Bottom, b.Top, a.Left, a.Right, gapOnX: false);
                    }
                }
            }

            var tally = placed.Count + " lot(s) clustered into rows and columns, "
                + Count(mask) + " road tile(s)";
            reason = because != null
                ? because + " (" + tally + ")"
                : "the connectors were inferred from the lot lattice: " + tally;
            return mask;
        }

        /// <summary>
        /// Whether two lots are the same row or the same column: their intervals on the shared axis
        /// overlap by more than half of the smaller lot's side.
        /// </summary>
        private static int Overlap(int a0, int a1, int b0, int b1)
        {
            var lo = a0 > b0 ? a0 : b0;
            var hi = a1 < b1 ? a1 : b1;
            return hi > lo ? hi - lo : 0;
        }

        /// <summary>
        /// Lay the street between two lots: <c>[lo, hi)</c> across the gap, up to
        /// <see cref="RoadGraph.MainStreetWidth"/> tiles, running the whole of
        /// <paramref name="across0"/>..<paramref name="across1"/> on the other axis and kept against
        /// the low side of the gap so a lot gets a footway in front of its own door.
        /// </summary>
        /// <remarks>
        /// <paramref name="gapOnX"/> says which axis the gap is on, because a gap between two lots
        /// that are side by side is a column of tiles and a street running up it, while a gap
        /// between two lots that are stacked is a row of tiles and a street running across - and
        /// swapping the two silently draws the street in the wrong place, which is a bug no test of
        /// one orientation would catch.
        /// </remarks>
        private static void LayCorridor(
            bool[] mask, int mapSize, int lo, int hi, int across0, int across1, bool gapOnX)
        {
            if (hi <= lo) return;
            var run = hi - lo;
            var width = run < RoadGraph.MainStreetWidth ? run : RoadGraph.MainStreetWidth;
            var spare = run - width;
            var pad = (spare + 1) / 2;
            if (pad > RoadGraph.SidewalkWidth) pad = RoadGraph.SidewalkWidth;

            var from = lo + pad;
            var to = from + width;
            for (int t = Math.Max(0, across0); t < Math.Min(mapSize, across1); t++)
            {
                for (int s = Math.Max(0, from); s < Math.Min(mapSize, to); s++)
                {
                    var x = gapOnX ? s : t;
                    var y = gapOnX ? t : s;
                    mask[y * mapSize + x] = true;
                }
            }
        }

        // ---- names -----------------------------------------------------------------------------

        /// <summary>
        /// The backdrop file a mode draws from, or <c>null</c> for a mode that has none - the
        /// 2D panel's own table, read rather than restated, so the map the connectors come from is
        /// the map the player is looking at.
        /// </summary>
        /// <param name="mode">The 1-based neighbourhood view.</param>
        public static string Describe(ushort mode)
        {
            try
            {
                return UINeighborhoodSelectionPanel.BitmapOf(mode);
            }
            catch (Exception)
            {
                //the table is a static array of literals; this cannot fail in practice, and a
                //reason string that says "no name" is a better answer than an exception out of a
                //diagnostic
                return null;
            }
        }

        /// <summary>Which of the two sources a mask came from, for a log line.</summary>
        /// <param name="reason">A reason string from one of the <c>Build</c> overloads.</param>
        public static bool CameFromBitmap(string reason)
        {
            return reason != null && reason.IndexOf("read from", StringComparison.Ordinal) >= 0;
        }

        // ---- the per-mode cache ------------------------------------------------------------------

        private sealed class CacheEntry
        {
            public bool[] Mask;
            public string Reason;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, CacheEntry> ByMode = new Dictionary<long, CacheEntry>();
        private static readonly Dictionary<int, long> SlotToMode = new Dictionary<int, long>();

        private static long KeyFor(int mode, int mapSize)
        {
            unchecked
            {
                return ((long)(mode & 0xFFFF) << 32) | (uint)mapSize;
            }
        }

        /// <summary>
        /// The mask for a mode, computed once per (mode, map size) and remembered. §8.7's mitigation
        /// for pixel inference being new: the decode and the classification are the expensive part
        /// and depend on nothing but the mode, so a neighbourhood that is revisited costs a
        /// dictionary lookup.
        /// </summary>
        /// <param name="mode">The neighbourhood mode.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="backdrop">The mode's backdrop, or <c>null</c>.</param>
        /// <param name="lots">The lots, for the fallback.</param>
        /// <param name="reason">What happened, from the <c>Build</c> that answered.</param>
        public static bool[] For(
            int mode, int mapSize, ITextureRef backdrop,
            IReadOnlyList<NeighbourhoodLot> lots, out string reason)
        {
            var hit = Cached(mode, mapSize, out reason);
            if (hit != null) return hit;

            var mask = Build((ushort)mode, backdrop, mapSize, lots, out reason);
            Remember(mode, mapSize, mask, reason);
            return mask;
        }

        /// <summary>
        /// <see cref="For"/> from a backdrop that has already been decoded, which is the route the
        /// builder takes: generation runs off the game thread and reading an
        /// <see cref="ITextureRef"/> needs a graphics device in this build, so
        /// <c>TS1LotContentSource</c> decodes the BMP itself and hands the pixels over.
        /// </summary>
        /// <param name="mode">The neighbourhood mode.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="bitmap">The decoded backdrop, or <c>null</c>.</param>
        /// <param name="lots">The lots, for the fallback.</param>
        /// <param name="reason">What happened, from the <c>Build</c> that answered.</param>
        /// <remarks>
        /// A null or degenerate <paramref name="bitmap"/> is read as "the caller could not find the
        /// file", not as "there are no pixels to read", and <see cref="FromInstall"/> is tried
        /// before the lattice. That is the whole fix for the connectors having been inferred from
        /// the lot lattice on a complete TS1 install: the caller looks for the backdrop under
        /// <c>UserData</c>, where TS1 keeps a neighbourhood's <em>own</em> picture, and the six
        /// canonical maps are not there - they are inside <c>UIGraphics\UIGraphics.far</c>. The
        /// caller had no way to know that, and answering "decoded to nothing" hid which of the two
        /// it was. <see cref="Build"/> and <see cref="BuildFromBitmap"/> deliberately do
        /// <em>not</em> read the install: they are the pure seams a test drives with a synthetic
        /// bitmap, and a test that asks about classification should not depend on what is installed.
        /// </remarks>
        public static bool[] ForBitmap(
            int mode, int mapSize, TexBitmap bitmap,
            IReadOnlyList<NeighbourhoodLot> lots, out string reason)
        {
            var hit = Cached(mode, mapSize, out reason);
            if (hit != null) return hit;

            bool[] mask;
            if (Usable(bitmap))
            {
                mask = BuildFromBitmap((ushort)mode, bitmap, mapSize, lots, out reason);
            }
            else
            {
                mask = FromInstall(mode, mapSize, lots, out reason);
            }

            Remember(mode, mapSize, mask, reason);
            return mask;
        }

        /// <summary>
        /// Whether a decoded bitmap can be classified at all. One predicate, because the two callers
        /// that hold a <see cref="TexBitmap"/> disagreed about what "nothing" means: one asked for
        /// <c>Data != null</c> and the other for the dimensions as well, so a 0x0 bitmap was a
        /// divide-by-zero waiting to happen on one path and a clean fallback on the other.
        /// </summary>
        private static bool Usable(TexBitmap bitmap)
        {
            return bitmap != null && bitmap.Data != null && bitmap.Data.Length > 0
                && bitmap.Width > 0 && bitmap.Height > 0 && bitmap.PixelSize >= 3;
        }

        // ---- reading the backdrop out of the install ----------------------------------------------

        /// <summary>
        /// The connector mask for a mode, reading the backdrop out of the TS1 install rather than
        /// being handed it: a loose file if the install has one, otherwise the entry of the same
        /// name inside whichever <c>.far</c> holds it. The lattice fallback if neither works.
        /// </summary>
        /// <param name="mode">The neighbourhood mode.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="lots">The lots, for the fallback.</param>
        /// <param name="reason">
        /// What happened, naming where the pixels came from when they did and what was looked in
        /// when they did not. Never null.
        /// </param>
        public static bool[] FromInstall(
            int mode, int mapSize, IReadOnlyList<NeighbourhoodLot> lots, out string reason)
        {
            if (mapSize < 1) throw new ArgumentOutOfRangeException(nameof(mapSize), mapSize,
                "an inferred road mask needs a map of at least one tile across.");

            var name = Describe((ushort)mode);
            var where = name == null ? null : FromInstall(name);
            if (where == null || !Usable(where.Bitmap))
            {
                return BuildFromLattice(lots, mapSize, out reason,
                    "the backdrop " + (name == null
                        ? "for mode " + mode + " could not be named"
                        : "'" + name + "' is not a file in this install"
                          + (where != null ? " (" + where.Failure + ")" : string.Empty)
                          + " and not an entry of any of its " + ArchiveCount + " .far archive(s)")
                    + ", so the connectors were inferred from the lot lattice instead");
            }

            return BuildFromBitmap((ushort)mode, where.Bitmap, mapSize, lots, out reason,
                "read from '" + name + "' out of " + where.Source);
        }

        /// <summary>
        /// Find one install-relative file, as a decoded bitmap, wherever this install keeps it.
        /// Returns <c>null</c> with the reason in <c>Failure</c> when it is nowhere to be found.
        /// </summary>
        /// <param name="name">
        /// The 2D panel's install-relative name, backslashes included - the same string the panel's
        /// table holds and the same one a <c>.far</c> entry uses, which is what makes the lookup a
        /// comparison rather than a guess.
        /// </param>
        public static InstalledBackdrop FromInstall(string name)
        {
            //A null name is the caller's mistake and gets null, which is what "there is nothing to
            //look for" means. Everything else gets an answer with a reason, because a caller that
            //prints the reason cannot print a null.
            if (string.IsNullOrEmpty(name)) return null;

            var content = Content.Get();
            var root = content?.TS1BasePath;
            if (string.IsNullOrEmpty(root))
            {
                return new InstalledBackdrop(null, null,
                    "no TS1 install path is configured, so nothing could be searched");
            }

            //A loose file first: an install that has been unpacked has it on disk, and that is both
            //the cheapest answer and the one an unpacker expects to be honoured.
            string loose = null;
            try
            {
                var candidate = Path.Combine(root, name.Replace('\\', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) loose = candidate;
            }
            catch (Exception e)
            {
                return new InstalledBackdrop(null, null, "'" + root + "' could not be searched: "
                    + e.Message);
            }

            if (loose != null)
            {
                var decoded = DecodeFile(loose);
                if (decoded != null) return new InstalledBackdrop(decoded, loose, null);
                return new InstalledBackdrop(null, null, "'" + loose + "' is there but did not decode");
            }

            var index = ArchiveIndex();
            KeyValuePair<string, FarEntry> hit;
            if (!index.TryGetValue(name.ToLowerInvariant(), out hit))
            {
                return new InstalledBackdrop(null, null, "no archive holds an entry named '"
                    + name + "'");
            }

            byte[] bytes;
            try
            {
                using (var archive = new FAR1Archive(Path.Combine(root, hit.Key), false))
                {
                    bytes = archive.GetEntry(hit.Value);
                }
            }
            catch (Exception e)
            {
                return new InstalledBackdrop(null, null, "'" + hit.Key + "' could not be read: "
                    + e.Message);
            }

            var fromArchive = DecodeBytes(bytes);
            if (fromArchive == null)
            {
                return new InstalledBackdrop(null, null, "the entry in '" + hit.Key
                    + "' did not decode");
            }

            return new InstalledBackdrop(fromArchive, hit.Key, null);
        }

        private static TexBitmap DecodeFile(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    return DecodeBytes(BitmapDecoder.Read(stream));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Bytes straight out of an archive entry, decoded. A thin wrapper over the stream form,
        /// because an entry arrives as an array and a loose file as a stream and the decode is one
        /// call to <see cref="BitmapDecoder"/> either way.
        /// </summary>
        private static TexBitmap DecodeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                {
                    return DecodeBytes(BitmapDecoder.Read(stream));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// <see cref="BitmapDecoder"/>'s RGBA buffer as a <see cref="TexBitmap"/>, and
        /// <c>null</c> for a decode that produced nothing.
        /// </summary>
        /// <remarks>
        /// The channels are not swizzled. <see cref="TexBitmap"/> is documented as BGRA and this is
        /// RGBA, and the classifier asks only for the max, the min and a luma - all three
        /// symmetric in the two ends - so the buffer is handed over as it comes. See
        /// <c>TS1LotContentSource.BackdropBitmap</c>, which makes the same call for the same reason.
        /// </remarks>
        private static TexBitmap DecodeBytes(Tuple<byte[], int, int> decoded)
        {
            if (decoded == null || decoded.Item1 == null || decoded.Item1.Length == 0
                || decoded.Item2 < 1 || decoded.Item3 < 1)
            {
                return null;
            }

            return new TexBitmap
            {
                Data = decoded.Item1,
                Width = decoded.Item2,
                Height = decoded.Item3,
                PixelSize = 4,
            };
        }

        /// <summary>How many <c>.far</c> archives the install was found to have.</summary>
        public static int ArchiveCount
        {
            get { return ArchiveIndex().Count; }
        }

        private static readonly object IndexGate = new object();
        private static Dictionary<string, KeyValuePair<string, FarEntry>> Archives;

        /// <summary>
        /// Every <c>.far</c> in the install, by lowercased entry name: the value names the archive
        /// (install-relative) and carries the entry's own offset and length.
        /// </summary>
        /// <remarks>
        /// <b>Built once, and paid for by every mode.</b> Reading a <c>.far</c> means parsing its whole
        /// manifest - every entry's name, offset and length - because that is the only index a FAR1
        /// has. On a complete install that is ninety archives and about forty-three thousand
        /// entries, which is ~150ms of manifest reads and is paid once per session behind a lock,
        /// for every mode, rather than once per mode. The alternative - opening archives one at a
        /// time until the name turns up - is a few milliseconds when the name is in the first
        /// archive and the whole walk when it is in the last, and it is the whole walk for every
        /// mode that is not mode one.
        /// <para>
        /// First-wins, matching <c>FAR1Provider</c>: a name that appears in two archives belongs to
        /// whichever is scanned first, and the scan order is sorted so that is the same archive on
        /// every machine. A duplicate is not an error in a TS1 install - it is normal.
        /// </para>
        /// <para>
        /// An archive that will not open is skipped rather than allowed to abort the walk: one
        /// corrupt <c>.far</c> is a miss for the entries in it, not a reason to lose the other
        /// eighty-nine.
        /// </para>
        /// </remarks>
        private static Dictionary<string, KeyValuePair<string, FarEntry>> ArchiveIndex()
        {
            EnsureArchiveIndex();
            lock (IndexGate)
            {
                return Archives ?? new Dictionary<string, KeyValuePair<string, FarEntry>>();
            }
        }

        private static void EnsureArchiveIndex()
        {
            lock (IndexGate)
            {
                if (Archives != null) return;
                Archives = new Dictionary<string, KeyValuePair<string, FarEntry>>();

                var content = Content.Get();
                var files = content?.TS1AllFiles;
                var root = content?.TS1BasePath;
                if (files == null || string.IsNullOrEmpty(root)) return;

                //sorted, so "first wins" is the same archive everywhere and the scan does not depend
                //on the order the filesystem happened to enumerate in
                var paths = new List<string>();
                for (int i = 0; i < files.Length; i++)
                {
                    var file = files[i];
                    if (!string.IsNullOrEmpty(file)
                        && file.EndsWith(".far", StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add(file);
                    }
                }
                paths.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (var relative in paths)
                {
                    try
                    {
                        using (var archive = new FAR1Archive(Path.Combine(root, relative), false))
                        {
                            var entries = archive.GetAllFarEntries();
                            for (int i = 0; i < entries.Count; i++)
                            {
                                var name = entries[i]?.Filename;
                                if (string.IsNullOrEmpty(name)) continue;
                                var key = name.ToLowerInvariant();
                                if (!Archives.ContainsKey(key))
                                {
                                    Archives[key] = new KeyValuePair<string, FarEntry>(relative, entries[i]);
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        //skipped: see the remarks above
                    }
                }
            }
        }

        /// <summary>Forgets the archive index, so the next read rescans. For tests.</summary>
        public static void ForgetArchives()
        {
            lock (IndexGate)
            {
                Archives = null;
            }
        }

        private static bool[] Cached(int mode, int mapSize, out string reason)
        {
            lock (Gate)
            {
                CacheEntry hit;
                if (!ByMode.TryGetValue(KeyFor(mode, mapSize), out hit) || hit == null)
                {
                    reason = null;
                    return null;
                }
                reason = hit.Reason;
                return hit.Mask;
            }
        }

        /// <summary>
        /// Put a mask into the cache under its mode, so the next ask is a dictionary lookup rather
        /// than a decode and a quarter of a million pixel tests.
        /// </summary>
        /// <remarks>
        /// Public because the two callers that build a mask already have one - a test driving
        /// <see cref="BuildFromBitmap"/> with a synthetic bitmap, or a workstream that decoded the
        /// file itself - and forcing them through <see cref="For"/> to get it cached would mean
        /// decoding it twice.
        /// </remarks>
        /// <param name="mode">The neighbourhood mode.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="mask">The mask, or <c>null</c> to record that there is none.</param>
        /// <param name="reason">Why it is what it is, for <see cref="CachedReason"/>.</param>
        public static void Remember(int mode, int mapSize, bool[] mask, string reason)
        {
            lock (Gate)
            {
                ByMode[KeyFor(mode, mapSize)] = new CacheEntry { Mask = mask, Reason = reason };
            }
        }

        /// <summary>The remembered mask for a mode, or <c>null</c> when nothing has built one.</summary>
        /// <param name="mode">The neighbourhood mode.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static bool[] Cached(int mode, int mapSize)
        {
            lock (Gate)
            {
                CacheEntry hit;
                return ByMode.TryGetValue(KeyFor(mode, mapSize), out hit) && hit != null
                    ? hit.Mask : null;
            }
        }

        /// <summary>The remembered reason for a mode's mask, or <c>null</c>.</summary>
        /// <param name="mode">The neighbourhood mode.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static string CachedReason(int mode, int mapSize)
        {
            lock (Gate)
            {
                CacheEntry hit;
                return ByMode.TryGetValue(KeyFor(mode, mapSize), out hit) && hit != null
                    ? hit.Reason : null;
            }
        }

        /// <summary>
        /// Point a neighbourhood slot at a mode's remembered mask. The half of the seam the cache
        /// cannot do by itself: the reader of a neighbourhood knows the mode and the generator of a
        /// map knows the slot, and neither knows the other.
        /// </summary>
        /// <remarks>
        /// Explicit rather than inferred. Guessing - "assume the slot is the mode", or "use the
        /// mode that was read last" - is wrong the moment two neighbourhoods are generated at once,
        /// and it is wrong in a way that looks like a neighbourhood with the wrong streets in it
        /// rather than like a bug.
        /// </remarks>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="mode">The mode whose mask this slot's map should use.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static void Bind(int slot, int mode, int mapSize)
        {
            lock (Gate)
            {
                SlotToMode[slot] = KeyFor(mode, mapSize);
            }
        }

        /// <summary>The mask a slot's map should use, or <c>null</c> when nothing is bound.</summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static bool[] CachedForSlot(int slot, int mapSize)
        {
            lock (Gate)
            {
                long key;
                if (!SlotToMode.TryGetValue(slot, out key)) return null;
                if (key != KeyFor(keyMode(key), mapSize)) return null;
                CacheEntry hit;
                return ByMode.TryGetValue(key, out hit) && hit != null ? hit.Mask : null;
            }
        }

        /// <summary>Why a slot's mask is what it is, or <c>null</c> when nothing is bound.</summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static string ReasonForSlot(int slot, int mapSize)
        {
            lock (Gate)
            {
                long key;
                if (!SlotToMode.TryGetValue(slot, out key)) return null;
                if (key != KeyFor(keyMode(key), mapSize)) return null;
                CacheEntry hit;
                return ByMode.TryGetValue(key, out hit) && hit != null ? hit.Reason : null;
            }
        }

        private static int keyMode(long key)
        {
            return (int)((key >> 32) & 0xFFFF);
        }

        /// <summary>Forget a mode's mask, so the next ask decodes and classifies again.</summary>
        /// <param name="mode">The neighbourhood mode.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static void Forget(int mode, int mapSize)
        {
            var key = KeyFor(mode, mapSize);
            lock (Gate)
            {
                ByMode.Remove(key);
                var slots = new List<int>();
                foreach (var pair in SlotToMode)
                {
                    if (pair.Value == key) slots.Add(pair.Key);
                }
                for (int i = 0; i < slots.Count; i++) SlotToMode.Remove(slots[i]);
            }
        }

        /// <summary>Forget every mode and every binding. For a content change and for tests.</summary>
        public static void ForgetAll()
        {
            lock (Gate)
            {
                ByMode.Clear();
                SlotToMode.Clear();
            }
        }

        /// <summary>Forget a slot's binding, leaving the modes themselves cached.</summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        public static void ForgetSlot(int slot)
        {
            lock (Gate)
            {
                SlotToMode.Remove(slot);
            }
        }

        // ---- small helpers ---------------------------------------------------------------------

        private readonly struct Rect
        {
            public readonly int Left;
            public readonly int Top;
            public readonly int Size;

            public int Right { get { return Left + Size; } }

            public int Bottom { get { return Top + Size; } }

            public Rect(int left, int top, int size)
            {
                Left = left;
                Top = top;
                Size = size;
            }
        }

        private static string Percent(double share)
        {
            return ((int)Math.Round(share * 100.0)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "%";
        }

        private static int Count(bool[] mask)
        {
            var n = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i]) n++;
            }
            return n;
        }
    }
}
