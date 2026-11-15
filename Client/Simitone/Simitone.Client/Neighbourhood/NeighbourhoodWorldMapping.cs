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
    /// <para><b>It is <see cref="NeighbourhoodPixelTransform"/>, and nothing else.</b> This type only
    /// converts bitmap coordinates to world tiles and back. It does <em>not</em> describe where the
    /// packer places lots: <see cref="NeighbourhoodLayoutBuilder.PackOnGrid(IReadOnlyList{NeighbourhoodLotRead}, int, int, int)"/>
    /// lays them out on a shelf grid, independently of their bitmap positions.</para>
    ///
    /// <para><b>Bitmap space is not the shelf's space.</b> A bitmap position lands where the
    /// transform says, but the shipped layout packs lots on a shelf grid, so a lot's own position is
    /// <see cref="PackedCentre"/> - never <see cref="BitmapToWorld"/> of its button. The camera,
    /// the click test and the facade plan all use the packed rectangle; <see cref="BitmapToWorld"/>
    /// remains only as the focus fallback for a lot the layout does not contain at all.</para>
    ///
    /// <para><b>Exact invertibility.</b> The transform is a per-axis scale, so
    /// <see cref="WorldToBitmap"/> undoes <see cref="BitmapToWorld"/> to the last bit.</para>
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
        /// <b>This is the answer for a lot the layout contains.</b> The shelf packer places the
        /// rectangle independently of the lot's bitmap position, so the two are different questions:
        /// this is where the rectangle is, <see cref="BitmapToWorld"/> is where the 2D map says the
        /// lot is.
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static Vector2 PackedCentre(NeighbourhoodLot lot)
        {
            if (lot == null) return Vector2.Zero;
            var size = Math.Max(0, lot.Size);
            return new Vector2(lot.WorldX + size / 2f, lot.WorldY + size / 2f);
        }

        public override string ToString()
        {
            return "map " + MapSize + ", " + LotCount + " lots, bitmap scale "
                + Scale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + " tiles per pixel";
        }
    }
}
