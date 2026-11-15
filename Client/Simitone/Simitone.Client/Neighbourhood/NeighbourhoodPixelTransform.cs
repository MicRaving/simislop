/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using Microsoft.Xna.Framework;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The one transform between a TS1 neighbourhood's 800x600 backdrop bitmap and the generated
    /// map's tile grid. Frozen in <c>docs/NEIGHBOURHOOD_3D_AUDIT_AND_FIX_PLAN.md</c> §8.4, and
    /// everything that has to agree about "where is lot 12" agrees about it through here.
    ///
    /// <para><b>What it is.</b> The bitmap's own isometric projection -
    /// <see cref="NeighbourhoodLayoutBuilder.ProjectMapPosition"/>'s
    /// <c>(x + 2y, 2y - x)</c>, the shape the OBJ exporter and the old packer both used - composed
    /// with a <em>fit</em> that puts the projection inside the isometric diamond every draw and
    /// cull path clips to (<see cref="NeighbourhoodCityMeshBuilder.IsInsideDiamond"/>). It is a
    /// single similarity: one divisor, one offset per diagonal, no floor, so
    /// <see cref="PixelForTile"/> undoes <see cref="TileForPixel"/> and a value can go round as
    /// many times as a test likes.</para>
    ///
    /// <para><b>Why the projection came back (§9.3).</b> It used to be an axis-aligned square
    /// mapping onto the whole <c>MapSize</c> x <c>MapSize</c> map, and that was wrong in the worst
    /// possible way: the map is a square but only its <em>diamond</em> is ever drawn, so a lot at a
    /// bitmap position near a corner landed in one of the four cut corners, where no ground quad and
    /// no road quad are emitted - "disconnected houses floating in a void, without lighting". The
    /// packer that preceded it was diamond-aligned because it used this very projection, and
    /// throwing it away is what broke the placement. The rule the rework inherits and this type
    /// obeys: <b>a bitmap position becomes a diamond-interior tile position through the bitmap's
    /// own isometric projection, not an axis-aligned square mapping</b>.</para>
    ///
    /// <para><b>The arithmetic, in full.</b> The projection
    /// <c>(x + 2y, 2y - x)</c> is <em>rotated 45 degrees</em> by the fit, and the two rotated
    /// coordinates are the map's own 45-degree diagonals - which is why the fit is separable and
    /// why every edge of the drawn island is a constant of one of them. Write a tile position as
    /// <c>sum = tileX + tileRow</c> and <c>difference = tileX - tileRow</c>; the projection sends
    /// <c>bitmapX</c> straight to <c>difference</c> and <c>bitmapY</c> straight to <c>sum</c>:
    ///
    /// <code>
    ///   sum        = sumMid        + sumInside        * (bitmapY / 600 - 0.5)
    ///   difference = differenceMid + differenceInside * (bitmapX / 800 - 0.5)
    ///   tileX      = (sum + difference) / 2
    ///   tileRow    = (sum - difference) / 2
    /// </code>
    ///
    /// Composed with <see cref="NeighbourhoodLayoutBuilder.ProjectMapPosition"/>'s own arithmetic
    /// that is <c>tile = differenceInside/4 * (px + 2py, -(px + 2py)) + sumInside/4 * (2py - px,
    /// 2py - px)</c>: the projection, then a quarter turn into the diamond's frame, then the fit.</para>
    ///
    /// <para><b>The fit.</b> The diamond's interior, in these coordinates, is
    /// <c>DiamondCentre &lt; sum &lt; MapSize + DiamondRightPivot</c> and
    /// <c>-DiamondCentre &lt; difference &lt; DiamondRightBase</c>: a 411-wide by 613-wide
    /// rectangle at <c>MapSize</c> 512. The fit shrinks each by one
    /// <see cref="NeighbourhoodCityMeshBuilder.FadeRange"/> - so a lot's centre is a whole ground
    /// fade inside the drawn edge - centres it, and scales the whole 800x600 backdrop onto it, each
    /// diagonal onto its own. That is what makes the guarantee unconditional: <b>every</b>
    /// position on the bitmap, and therefore <em>every</em> lot a <c>LotLocations.iff</c> can name
    /// and <em>every</em> lot a modded neighbourhood can add, lands on drawn ground inside the
    /// diamond. It is also why this type knows nothing about lots: a fit keyed on where a
    /// neighbourhood's lots happen to sit would be a second answer, and the second answer is the
    /// bug.</para>
    ///
    /// <para><b>Why the fit is separable, and what it costs.</b> The diamond is 48% of the map's
    /// area, so anything fitted into it is smaller than the square mapping put it, and the lots
    /// therefore sit closer together than the old square mapping put them - two real lots overlap
    /// by more than TSO's own <c>Size - 2</c> pitch, which is a known and documented consequence
    /// of putting real positions on a half-size island (§8.7), not a new defect. What is *not*
    /// negotiable is wasting the diamond's width: a single uniform scale can only bind on the
    /// narrower diagonal, which here is the 411-wide <c>sum</c>, and would then put the bitmap's
    /// 800-pixel width through only 261 of the 613 tiles the <c>difference</c> diagonal has - a
    /// neighbourhood a further factor of 2.3 smaller in each axis, three houses deep inside one
    /// another. Separable fitting is what "a fit" means in this tree - the superseded mapping had a
    /// <c>PadX</c> and a <c>PadY</c> - and it is why both diagonals are filled.</para>
    ///
    /// <para><b>What the anisotropy is.</b> 1.14, and that is the right answer rather than an
    /// accident: the projection reads a 2:1 pixel-art grid, and this backdrop is 4:3, not 2:1. A
    /// step right on the bitmap is 0.741 tiles of <c>difference</c> (0.524 tiles of world distance
    /// at 45 degrees) and a step down is 0.652 tiles of <c>sum</c> (0.461) - so the backdrop's own
    /// pixels come out a shade taller than they are wide, which is exactly the correction a 4:3
    /// picture needs to fill a 1.49:1 window.</para>
    ///
    /// <para><b>Pure.</b> No graphics device, no content, no clock, no filesystem - two divisions
    /// and an add. That is deliberate: the lot positions, the road mask and the camera all have to
    /// be derivable in a test with nothing but arithmetic, and a transform that could consult the
    /// game would eventually be consulted by only one of them.</para>
    /// </summary>
    public static class NeighbourhoodPixelTransform
    {
        /// <summary>Width of every TS1 neighbourhood backdrop bitmap, in pixels.</summary>
        public const int BitmapWidth = 800;

        /// <summary>Height of every TS1 neighbourhood backdrop bitmap, in pixels.</summary>
        public const int BitmapHeight = 600;

        /// <summary>
        /// The map size the diamond's own numbers are written for, and the size this type's fit is
        /// expressed in. Every other map size scales the fit by
        /// <c>mapSize / <see cref="ReferenceMapSize"/></c>, so a test's 96-tile map is the same
        /// neighbourhood at a different scale.
        /// </summary>
        public const int ReferenceMapSize = NeighbourhoodCityMeshBuilder.MapSize;

        /// <summary>
        /// The diamond's lower edge in <c>tileX + tileRow</c>. A tile inside the diamond has a
        /// sum above it, because that is the line the north-west edge of the diamond runs along.
        /// </summary>
        public const float ReferenceSumMin = NeighbourhoodCityMeshBuilder.DiamondCentre;

        /// <summary>
        /// The diamond's upper edge in <c>tileX + tileRow</c>: the south-east edge, whose row turns
        /// around at <see cref="NeighbourhoodCityMeshBuilder.DiamondRightPivot"/>.
        /// </summary>
        public const float ReferenceSumMax =
            NeighbourhoodCityMeshBuilder.MapSize + NeighbourhoodCityMeshBuilder.DiamondRightPivot;

        /// <summary>
        /// The diamond's lower edge in <c>tileX - tileRow</c>: the south-west edge, whose row turns
        /// around at <see cref="NeighbourhoodCityMeshBuilder.DiamondCentre"/>.
        /// </summary>
        public const float ReferenceDifferenceMin = -NeighbourhoodCityMeshBuilder.DiamondCentre;

        /// <summary>
        /// The diamond's upper edge in <c>tileX - tileRow</c>: the north-east edge.
        /// </summary>
        public const float ReferenceDifferenceMax = NeighbourhoodCityMeshBuilder.DiamondRightBase;

        /// <summary>
        /// <b>How far inside the diamond's edge the fit keeps the bitmap, in map tiles at
        /// <see cref="ReferenceMapSize"/>.</b> One
        /// <see cref="NeighbourhoodCityMeshBuilder.FadeRange"/>, which is the ground skirt the mesh
        /// builder still emits outside the edge: a lot whose centre is a whole fade inside is on
        /// ground that is fully opaque, and one at the edge itself would be half faded and partly on
        /// nothing.
        /// </summary>
        public const float EdgeMargin = NeighbourhoodCityMeshBuilder.FadeRange;

        /// <summary>
        /// A bitmap position as a continuous tile position. The pixel is treated as the lot's
        /// <em>centre</em> on the bitmap - which is what <c>LotLocations.iff</c> means
        /// (<c>SimitoneNeighOBJExporter</c> treats it as a centre too) - so the tile this answers
        /// is the lot's centre tile, not its origin. See <see cref="OriginForCentre"/>.
        /// </summary>
        /// <param name="pixel">A position on the 800x600 bitmap.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static Vector2 TileForPixel(Vector2 pixel, int mapSize)
        {
            var sum = SumForPixelY(pixel.Y, mapSize);
            var difference = DifferenceForPixelX(pixel.X, mapSize);
            return new Vector2((sum + difference) * 0.5f, (sum - difference) * 0.5f);
        }

        /// <summary>
        /// A continuous tile position as a continuous bitmap position. The exact inverse of
        /// <see cref="TileForPixel"/>; there is no tolerance anywhere in the round trip.
        /// </summary>
        /// <param name="tile">A position in map-tile space.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static Vector2 PixelForTile(Vector2 tile, int mapSize)
        {
            var size = Positive(mapSize);
            var factor = size / (float)ReferenceMapSize;
            return new Vector2(
                BitmapWidth * Fraction(tile.X - tile.Y, factor, DifferenceMid(), DifferenceInside()),
                BitmapHeight * Fraction(tile.X + tile.Y, factor, SumMid(), SumInside()));
        }

        /// <summary>
        /// The top-left tile of the <paramref name="size"/>-tile rectangle whose centre is
        /// <paramref name="centre"/>. The two steps the layout builder needs and must not spell out
        /// separately: <c>LotLocations</c> gives a centre, the map wants an origin.
        /// </summary>
        /// <remarks>
        /// Half the side is subtracted from the centre's tile, floored. Rounding down on the way
        /// means a lot's centre never drifts above the transform's own answer by more than half a
        /// tile, which is the precision §8.6 asks the placement to keep.
        /// </remarks>
        /// <param name="centre">The lot's centre on the bitmap.</param>
        /// <param name="size">The lot's own side, in tiles.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static Point OriginForCentre(Vector2 centre, int size, int mapSize)
        {
            var tile = TileForPixel(centre, mapSize);
            var half = Math.Max(0, size) / 2;
            return new Point((int)Math.Floor(tile.X) - half, (int)Math.Floor(tile.Y) - half);
        }

        /// <summary>
        /// Clamps a rectangle's origin so the whole rectangle is inside <c>0..mapSize</c>.
        /// </summary>
        /// <remarks>
        /// The only adjustment the layout builder makes to a map-derived position, and it is an
        /// adjustment rather than an invention: a lot whose bitmap position is nonsense really does
        /// have to be put somewhere legal, and a lot bigger than the map has to land at the origin
        /// rather than hang off both edges.
        ///
        /// <para><b>It is a safety net, not the placement rule.</b> The fit inside this type already
        /// puts every bitmap position inside the drawn diamond, so on any real
        /// <c>LotLocations.iff</c> this changes nothing; it is here for a corrupt or absent table,
        /// whose lots are all at bitmap (0, 0) and would otherwise be twenty rectangles on one
        /// tile.</para>
        /// </remarks>
        /// <param name="origin">The unclamped origin.</param>
        /// <param name="size">The rectangle's side, in tiles.</param>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        public static Point ClampOrigin(Point origin, int size, int mapSize)
        {
            var max = Math.Max(0, mapSize - Math.Max(0, size));
            return new Point(
                origin.X < 0 ? 0 : (origin.X > max ? max : origin.X),
                origin.Y < 0 ? 0 : (origin.Y > max ? max : origin.Y));
        }

        // ---- the fit, in the map's diagonal coordinates ------------------------------------------

        /// <summary>
        /// The diamond's interior along <c>tileX + tileRow</c> at
        /// <see cref="ReferenceMapSize"/>, minus one <see cref="EdgeMargin"/> at each end: the span
        /// the backdrop's 600 pixels of height are scaled onto.
        /// </summary>
        public static float SumInside()
        {
            return (ReferenceSumMax - ReferenceSumMin) - 2f * EdgeMargin;
        }

        /// <summary>
        /// The diamond's interior along <c>tileX - tileRow</c> at
        /// <see cref="ReferenceMapSize"/>, minus one <see cref="EdgeMargin"/> at each end: the span
        /// the backdrop's 800 pixels of width are scaled onto.
        ///
        /// <para><b>Where the shape of the bitmap meets the shape of the island.</b> The projection
        /// puts the width on this diagonal and the height on <see cref="SumInside"/>, so the fit
        /// gives each of them the room the diamond actually has instead of one common divisor.
        /// The 411-to-613 ratio is the whole of the anisotropy the composed transform carries, and
        /// it is 1.14 - see the type remarks.</para>
        /// </summary>
        public static float DifferenceInside()
        {
            return (ReferenceDifferenceMax - ReferenceDifferenceMin) - 2f * EdgeMargin;
        }

        /// <summary>The middle of the diamond along <c>tileX + tileRow</c>, at <see cref="ReferenceMapSize"/>.</summary>
        public static float SumMid()
        {
            return (ReferenceSumMin + ReferenceSumMax) * 0.5f;
        }

        /// <summary>The middle of the diamond along <c>tileX - tileRow</c>, at <see cref="ReferenceMapSize"/>.</summary>
        public static float DifferenceMid()
        {
            return (ReferenceDifferenceMin + ReferenceDifferenceMax) * 0.5f;
        }

        private static float SumForPixelY(float pixelY, int mapSize)
        {
            //the whole diagonal scales with the map, offset included: a 96-tile map is the same
            //neighbourhood at a different scale, not a 96-tile map with a 512-tile neighbourhood
            //pinned in the middle of it
            return (SumMid() + SumInside() * (pixelY / BitmapHeight - 0.5f)) * SizeFactor(mapSize);
        }

        private static float DifferenceForPixelX(float pixelX, int mapSize)
        {
            return (DifferenceMid() + DifferenceInside() * (pixelX / BitmapWidth - 0.5f))
                * SizeFactor(mapSize);
        }

        /// <summary>
        /// A diagonal coordinate back to its fraction of the bitmap's own extent: where 0 is the
        /// first row of pixels and 1 the last.
        /// </summary>
        private static float Fraction(float diagonal, float sizeFactor, float mid, float inside)
        {
            return (diagonal / sizeFactor - mid) / inside + 0.5f;
        }
        private static float SizeFactor(int mapSize)
        {
            return Positive(mapSize) / (float)ReferenceMapSize;
        }

        /// <summary>
        /// A non-positive map size is not a map, and every caller here divides by it. Folded to one
        /// rather than thrown: a camera that has just lost its device asks this question with a
        /// default-constructed layout behind it, and a crash over a zero is a worse answer than a
        /// one-tile map.
        /// </summary>
        private static int Positive(int mapSize)
        {
            return mapSize > 0 ? mapSize : 1;
        }
    }
}
