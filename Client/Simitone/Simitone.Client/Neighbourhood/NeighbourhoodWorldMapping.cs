/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The correspondence between a lot's position on the 800x600 neighbourhood bitmap and its place
    /// in the generated world: the one transform, in both directions.
    ///
    /// <para><b>What it is for.</b> §7.3 requires the 3D view to be driven by
    /// <c>NeighCameraMachine</c>, which works entirely in the bitmap's own 800x600 space - it
    /// answers "zoom out to the whole map" and "centre here, at this zoom, on this lot" in bitmap
    /// pixels. Something has to say what those numbers mean in world tiles, and this is it. The
    /// house buttons are the same story: they are placed at <c>HousePositions</c>, in bitmap space,
    /// and the 3D camera has to be aimed so the two spaces mean the same neighbourhood.</para>
    ///
    /// <para><b>It is <see cref="NeighbourhoodPixelTransform"/>, and nothing else.</b> Since Part 8
    /// the layout builder places a lot at <c>TileForPixel(MapX, MapY)</c> and nothing moves it, so
    /// the position this type reports <em>is</em> the position the rectangle is at - which is the
    /// property the whole thing exists for. Before Part 8 this type reproduced the OBJ exporter's
    /// isometric projection and its fit into the map, and it documented at length why it could not
    /// be the inverse of the packer: the packer relaxed overlapping rectangles afterwards and
    /// clamped them into the map, so a lot's final <c>WorldX</c> was not derivable from its bitmap
    /// position at all, and the camera had to prefer the packed rectangle over the projection and
    /// fall back to the projection only for a lot the layout did not contain. That is gone. There is
    /// a bijection between bitmap positions and tile positions, the layout uses it, and the camera
    /// uses it.</para>
    ///
    /// <para><b>What is kept, and why.</b> <see cref="MapSize"/> and <see cref="LotCount"/> are the
    /// facts a caller asks about the world rather than about the arithmetic.
    /// <see cref="BitmapToWorld"/>, <see cref="BitmapToWorldTile"/>,
    /// <see cref="WorldToBitmap"/> and <see cref="PackedCentre"/> are the four things the camera
    /// placement, the renderer and the tests call, unchanged in signature and in meaning.
    /// <see cref="Scale"/>, <see cref="Fit"/>, <see cref="PadX"/>, <see cref="PadY"/>,
    /// <see cref="MinProjectedX"/> and <see cref="MinProjectedY"/> are gone: they were the
    /// projection and the fit, and there is no projection and no fit left to describe. So is the
    /// <see cref="Fitted"/> factory, which existed only to build them.</para>
    ///
    /// <para><b>Exact invertibility.</b> The transform is a per-axis scale, so
    /// <see cref="WorldToBitmap"/> undoes <see cref="BitmapToWorld"/> to the last bit, and a round
    /// trip through both is meaningful. The floored integer form is
    /// <see cref="BitmapToWorldTile"/>, which is the layout builder's own answer for a rectangle's
    /// origin plus half its side - see <see cref="LotCentre"/> for why the two differ by half a
    /// lot.</para>
    /// </summary>
    public readonly struct NeighbourhoodWorldMapping
    {
        /// <summary>Map side in tiles. The scale the transform is built against.</summary>
        public int MapSize { get; }

        /// <summary>How many lots this world has. Informational: the transform does not depend on it.</summary>
        public int LotCount { get; }

        /// <summary>
        /// The map's scale, read from <see cref="NeighbourhoodPixelTransform"/> rather than restated.
        /// </summary>
        /// <remarks>
        /// <b>It used to be the OBJ exporter's isometric divisor.</b> Kept as a member because a
        /// caller logging a mapping wants one number for "how big is this world relative to the
        /// bitmap", and because it costs nothing to answer from the one definition rather than
        /// keeping a second.
        /// </remarks>
        public float Scale
        {
            get { return MapSize / (float)NeighbourhoodPixelTransform.BitmapWidth; }
        }

        private NeighbourhoodWorldMapping(int mapSize, int lotCount)
        {
            MapSize = mapSize;
            LotCount = lotCount;
        }

        /// <summary>
        /// A mapping for a layout's map size and lot count, with no per-layout detail - the whole of
        /// what this type now has to know.
        /// </summary>
        /// <param name="layout">The layout, or <c>null</c> for a mapping of the default map.</param>
        public static NeighbourhoodWorldMapping For(NeighbourhoodLayout layout)
        {
            var mapSize = layout != null && layout.MapSize > 0
                ? layout.MapSize
                : NeighbourhoodLayout.DefaultMapSize;
            var count = layout != null ? layout.LotCount : 0;
            return new NeighbourhoodWorldMapping(mapSize, count);
        }

        /// <summary>A mapping for a bare map size, for a caller that has no layout.</summary>
        /// <param name="mapSize">The generated map's side length, in tiles.</param>
        /// <param name="lotCount">How many lots it has.</param>
        public static NeighbourhoodWorldMapping ForMap(int mapSize, int lotCount)
        {
            return new NeighbourhoodWorldMapping(
                mapSize > 0 ? mapSize : NeighbourhoodLayout.DefaultMapSize,
                Math.Max(0, lotCount));
        }

        /// <summary>
        /// A bitmap position as a continuous tile position: the lot's <em>centre</em> tile.
        /// </summary>
        /// <param name="mapX">Bitmap x, from <c>LotLocations</c> or a house button.</param>
        /// <param name="mapY">Bitmap y, from <c>LotLocations</c> or a house button.</param>
        public Vector2 BitmapToWorld(int mapX, int mapY)
        {
            return NeighbourhoodPixelTransform.TileForPixel(new Vector2(mapX, mapY), MapSize);
        }

        /// <summary>
        /// A bitmap position as a whole tile, floored: the centre tile of the lot's rectangle.
        /// </summary>
        /// <remarks>
        /// The layout's <see cref="NeighbourhoodLot.WorldX"/> is the rectangle's <em>origin</em>,
        /// which is this minus half the lot's side, so this and <see cref="PackedCentre"/> agree
        /// within half a tile rather than exactly. That is the transform's precision and §8.6 asks
        /// for nothing finer; <see cref="LotCentre"/> is the exact answer for a lot the layout has.
        /// </remarks>
        /// <param name="mapX">Bitmap x.</param>
        /// <param name="mapY">Bitmap y.</param>
        public Point BitmapToWorldTile(int mapX, int mapY)
        {
            var world = BitmapToWorld(mapX, mapY);
            return new Point((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        }

        /// <summary>
        /// A tile position as a continuous bitmap position. The exact inverse of
        /// <see cref="BitmapToWorld"/>.
        /// </summary>
        /// <param name="worldX">Tile x.</param>
        /// <param name="worldY">Tile y.</param>
        public Vector2 WorldToBitmap(float worldX, float worldY)
        {
            return NeighbourhoodPixelTransform.PixelForTile(new Vector2(worldX, worldY), MapSize);
        }

        /// <summary>
        /// The centre of a lot's rectangle in world tiles, exactly: the origin plus half its side.
        /// </summary>
        /// <remarks>
        /// <b>This is the answer the camera should use for a lot the layout contains</b>, and it is
        /// now equal to the lot's bitmap position through the transform rather than approximately
        /// equal to it, because the layout builder placed the rectangle from that same transform.
        /// <see cref="BitmapToWorldTile"/> is the fallback for a lot the layout does not have, and
        /// the two are kept distinct rather than merged because they are two different questions: one
        /// is "where is the rectangle", the other is "where does the map say this lot is".
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static Vector2 PackedCentre(NeighbourhoodLot lot)
        {
            if (lot == null) return Vector2.Zero;
            var size = Math.Max(0, lot.Size);
            return new Vector2(lot.WorldX + size / 2f, lot.WorldY + size / 2f);
        }

        /// <summary>
        /// Which lot covers a world tile, preferring the one whose bitmap centre is nearest to that
        /// tile's bitmap position.
        /// </summary>
        /// <remarks>
        /// <b>The tie-break is the new part, and it is why this exists.</b> Real lots overlap by two
        /// tiles on TSO's own <c>Size - 2</c> pitch, so a tile in the overlap belongs to two lots and
        /// <see cref="NeighbourhoodLayout.LotAt"/>'s first-match answer is whichever lot number is
        /// lower. That is a stable but arbitrary answer, and it is the wrong one for a click: the
        /// player clicked the house drawn at a particular button, and the lot whose <em>map
        /// position</em> is that button is the one they meant.
        /// </remarks>
        /// <param name="layout">The layout, or <c>null</c>.</param>
        /// <param name="worldX">Tile x.</param>
        /// <param name="worldY">Tile y.</param>
        public static NeighbourhoodLot LotAt(NeighbourhoodLayout layout, int worldX, int worldY)
        {
            if (layout == null) return null;

            var covering = layout.LotAt(worldX, worldY);
            if (covering == null) return covering;

            //a tile in no lot's overlap needs no tie-break, and asking anyway would cost a scan of
            //every lot on every click for an answer the first match already gave
            var rivals = 0;
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var other = layout.Lots[i];
                if (ReferenceEquals(other, covering)) continue;
                if (worldX >= other.WorldX && worldX < other.WorldX + Math.Max(0, other.Size)
                    && worldY >= other.WorldY && worldY < other.WorldY + Math.Max(0, other.Size))
                {
                    rivals++;
                }
            }
            if (rivals == 0) return covering;

            var pixel = NeighbourhoodPixelTransform.PixelForTile(
                new Vector2(worldX + 0.5f, worldY + 0.5f), layout.MapSize);
            var best = covering;
            var bestDistance = DistanceTo(covering, pixel);
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var other = layout.Lots[i];
                if (ReferenceEquals(other, covering)) continue;
                if (worldX < other.WorldX || worldX >= other.WorldX + Math.Max(0, other.Size)
                    || worldY < other.WorldY || worldY >= other.WorldY + Math.Max(0, other.Size))
                {
                    continue;
                }
                var distance = DistanceTo(other, pixel);
                //strictly less, so a tie goes to the lower lot index and the answer does not depend
                //on the order the scan reached the two lots in
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = other;
                }
            }
            return best;
        }

        private static float DistanceTo(NeighbourhoodLot lot, Vector2 pixel)
        {
            var dx = pixel.X - lot.MapX;
            var dy = pixel.Y - lot.MapY;
            return dx * dx + dy * dy;
        }

        public override string ToString()
        {
            return "map " + MapSize + ", " + LotCount + " lots, bitmap scale "
                + Scale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + " tiles per pixel";
        }
    }
}
