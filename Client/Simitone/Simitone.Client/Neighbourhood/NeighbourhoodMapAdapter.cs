/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The seam between M1's <see cref="NeighbourhoodLayout"/> and M2's
    /// <see cref="NeighbourhoodMapInput"/>, and the unit conversion that has to happen on the way.
    ///
    /// <para><b>Why the seam is here and not in either milestone.</b> Both ends are deliberately
    /// independent: M1 owns the sidecar and the packing, M2 owns the arrays and must be testable
    /// from a canned lot list with no I/O, and neither holds a reference to the other's type. That
    /// leaves somebody to do the projection, and the projection is not a field copy - it is where
    /// two different units meet, which is exactly the kind of thing that gets forgotten.
    /// </para>
    ///
    /// <para><b>The one conversion that matters.</b> A lot's <see cref="NeighbourhoodLot.BaseHeight"/>
    /// is the median of its <c>ARRY(0)</c> altitudes, and <em>one <c>ARRY(0)</c> unit is not one
    /// tile</em> - it is three sixteenths of one, because <c>Blueprint.TerrainFactor</c> is
    /// <c>3/160</c> of a tile per altitude unit and <c>VMTS1Activator</c> hands
    /// <c>Blueprint.Altitude</c> ten altitude units per <c>ARRY(0)</c> unit. The generated map's
    /// <see cref="NeighbourhoodMap.ElevationData"/> is a byte, and the mesh builder divides that
    /// by twelve, so a tile is twelve bytes. Putting those together gives the multiplier this seam
    /// needs: <c>12 * 3/16 = 9/4</c> elevation bytes per <c>ARRY(0)</c> unit.
    /// </para>
    ///
    /// <para><b>It used to be twelve, and that was the bug.</b> Multiplying by twelve says one
    /// <c>ARRY(0)</c> unit is a whole tile, which is <c>12 / (9/4) = 16/3 = 5.33</c> too large:
    /// every lot's stamped ground landed five and a third times too high, so a map built from real
    /// altitudes had hillsides steeper than anything the game ships, and a neighbour's own walls -
    /// which are three sixteenths of a tile per unit - ended up far below the surround that was
    /// drawn for them. The multiplier is no longer written down here at all:
    /// <see cref="QuantiseAltitude"/> is
    /// <see cref="NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit"/>, so the seam and the
    /// mesh cannot drift apart, and there is no second spelling of the number to be wrong.
    /// </para>
    ///
    /// <para><b>The horizontal conversion, and what one map tile actually is.</b> A generated map
    /// tile is <em>one lot tile</em>, exactly, and the lot view is three world units to a lot tile,
    /// so a map tile is three lot-view world units. Both halves are measured, not assumed:</para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>One map tile is one lot tile.</b> <see cref="NeighbourhoodLot.Size"/> is a lot's
    ///     <c>SIMI.GlobalData[23]</c>, and <c>VMTS1Activator.LoadFromIff</c> turns that value
    ///     "straight into a <c>Size x Size</c> blueprint" - the lot view's own architecture, in lot
    ///     tiles. The layout builder then stamps that same number of tiles into the map
    ///     (<see cref="NeighbourhoodLayoutBuilder"/>, and the log's
    ///     <c>the player's own x[92,139) z[114,161)</c> is a 47-tile lot, so
    ///     <c>139 - 92 == 47 == Size</c>). A generated tile is therefore the same thing the lot view
    ///     calls a tile, and nothing in the pipeline rescales it. <b>Part 8 §8.3 changed where a
    ///     lot's tiles <em>are</em>, not how big they are</b>:
    ///     <see cref="NeighbourhoodLot.WorldX"/> and <see cref="NeighbourhoodLot.WorldY"/> now come
    ///     from <see cref="NeighbourhoodPixelTransform"/> rather than from an isometric projection
    ///     and a fit, and a projection moves lots <em>around</em> - it never changes a lot's own
    ///     size, so it could not change this.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A lot tile is three world units.</b> <c>FSO.LotView.WorldState.WorldUnitsPerTile</c>
    ///     is <c>3f</c> and the engine uses it everywhere: <c>GetWorldCoords</c> is
    ///     <c>tile * WorldUnitsPerTile</c>, <c>SubWorldComponent</c>'s bounds are
    ///     <c>GlobalPosition * 3</c> to <c>+ Blueprint.Width * 3</c>, and its
    ///     <c>Cameras.ModelTranslation</c> is <c>GlobalPosition * 3</c>. So
    ///     <see cref="SimitoneSurroundingsPlacement.LotViewUnitsPerTile"/> is that number, and it
    ///     is the number the <em>neighbouring sub-worlds</em> are already placed by
    ///     (<c>SubWorldPosition</c> is a raw map-tile delta, unswapped). The surround mesh and the
    ///     real lots standing in it are therefore on one scale by construction, and a change to
    ///     either one alone would put every neighbour on the wrong plot.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>The street is the one thing that is not the same size in both spaces, and the deficit is
    /// three, not twenty.</b> The generated map draws its streets at TSO's <em>city map</em>
    /// convention - <see cref="RoadGraph.MainStreetWidth"/> tarmac tiles with a
    /// <see cref="RoadGraph.SidewalkWidth"/>-tile pavement either side - so a street is
    /// <see cref="StreetCorridorMapTiles"/> map tiles, <see cref="StreetTarmacLotViewUnits"/> of
    /// which are tarmac. The lot view draws a different street: <c>RestoreRoad</c> stamps each lot's
    /// own half of it, and the tilemaps are three rows of pattern 9 ("black tarmac") deep with a
    /// pattern 12 pavement beyond, on a lot pitch of <c>Size - 2</c> - six tarmac tiles in an
    /// eleven-tile corridor, <see cref="LotViewStreetTarmacUnits"/> units of tarmac. Tarmac is
    /// therefore <see cref="StreetTarmacDeficit"/> times wider in the lot view than in the
    /// generated surround, and the whole corridor <see cref="StreetCorridorDeficit"/> times. That
    /// is a real and visible discrepancy and it is the reason a surround reads as a shrunken model
    /// of the city rather than as the city, but it is a <em>street-width</em> disagreement between
    /// the corridor the lot view stamps and the one a measured street is laid in, not a scale error
    /// in the conversion above: the lot rectangles drawn under the player are already the right
    /// size, and widening the conversion to fix the road would make the lots wrong. These constants
    /// exist so that the two numbers can be compared without a screenshot, and so the fix lands
    /// where the two widths are actually chosen.
    /// </para>
    ///
    /// <para><b>The terrain type needs no conversion, and that is not a coincidence.</b>
    /// <c>FSO.Content.Model.TerrainType</c> - the enum <c>VMTS1Activator.HouseNumToType</c> hands
    /// back for a lot - is declared <c>GRASS = 0, SAND = 1, ROCK = 2, SNOW = 3, WATER = 4</c>,
    /// which is exactly <see cref="NeighbourhoodTerrain"/>'s five drawable indices in the same
    /// order. The three extra members the enum has (<c>NULL</c> and the two TS1-only grass
    /// variants) are not drawable terrains and fold to grass, which is what the lot view does with
    /// them.
    /// </para>
    /// </summary>
    public static class NeighbourhoodMapAdapter
    {
        // ---- the horizontal scale, and the whole of it -----------------------------------------------
        //
        // Three numbers, and they are the entire horizontal contract between the generated map and a
        // live lot. Every x and z that crosses the seam - the surround's world matrix, a facade's
        // vertex, a sub-world's GlobalPosition - is one of these three composed with a position, and
        // a second spelling of any of them is the bug where the surround is the right shape at the
        // wrong size, which is the one scale failure a screenshot cannot catch.

        /// <summary>
        /// How many generated map tiles there are in one lot tile. One, and it is one by measurement
        /// rather than by wish: <see cref="NeighbourhoodLot.Size"/> is the lot's own
        /// <c>SIMI.GlobalData[23]</c> and is what the lot view sizes its architecture from, and the
        /// packer stamps that many tiles into the map. See the class remarks.
        /// </summary>
        public const float MapTilesPerLotTile = 1f;

        /// <summary>
        /// Lot-view world units in one lot tile, verbatim from
        /// <c>FSO.LotView.WorldState.WorldUnitsPerTile</c>.
        /// </summary>
        /// <remarks>
        /// The lot view multiplies by this in every conversion it has -
        /// <c>GetWorldCoords</c>, <c>SubWorldComponent</c>'s <c>ModelTranslation</c> and its
        /// bounding box, <c>InterpAltitude</c>'s callers - so it is not a Simitone choice at all. It
        /// is written down here rather than referenced because the constant lives in
        /// <c>FSO.LotView</c> and because this is the one number whose being wrong would be
        /// invisible: a surround at the wrong scale still looks like a neighbourhood.
        /// </remarks>
        public const float LotViewUnitsPerLotTile = 3f;

        /// <summary>
        /// Lot-view world units in one generated map tile - the conversion
        /// <see cref="SimitoneSurroundingsPlacement"/> applies to the whole surround.
        /// </summary>
        /// <remarks>
        /// <b>This is the number that must not be tuned.</b> The neighbouring sub-worlds are placed
        /// at <c>SubWorldComponent.GlobalPosition * 3</c>, where <c>GlobalPosition</c> is the raw
        /// map-tile delta between two packed rectangles, so the real lots standing in the surround
        /// are already at this scale. Changing this constant moves the ground and the facades and
        /// leaves every real neighbour lot where it was.
        /// </remarks>
        public const float LotViewUnitsPerMapTile = MapTilesPerLotTile * LotViewUnitsPerLotTile;

        /// <summary>
        /// Tiles of tarmac in a generated map's street, from the road graph rather than restated.
        /// </summary>
        public const int StreetTarmacMapTiles = RoadGraph.MainStreetWidth;

        /// <summary>Pavement tiles along each side of a generated map's street.</summary>
        public const int StreetPavementMapTiles = RoadGraph.SidewalkWidth;

        /// <summary>
        /// The whole corridor a generated map's street occupies: tarmac plus the pavement on each
        /// side. Four tiles, against the eleven the lot view draws for the same street.
        /// </summary>
        public const int StreetCorridorMapTiles = StreetTarmacMapTiles + 2 * StreetPavementMapTiles;

        /// <summary>Tiles of tarmac one lot stamps along each of its own edges, in the lot view.</summary>
        public const int LotViewStreetTarmacTilesPerSide = 3;

        /// <summary>
        /// The tarmac a lot view's street is made of, in lot tiles: the three each lot stamps along
        /// its own edge, from <c>VMLotTerrainRestoreTools</c>'s road tilemaps.
        /// </summary>
        public const int LotViewStreetTarmacTiles = 2 * LotViewStreetTarmacTilesPerSide;

        /// <summary>
        /// The width of a lot view's street corridor in lot tiles, at a lot pitch of
        /// <see cref="LotViewLotPitchTiles"/>: two five-tile bands with one tile between them.
        /// </summary>
        public const int LotViewStreetCorridorTiles = 11;

        /// <summary>
        /// How far apart two neighbouring lots' origins are in the generated world, in lot tiles:
        /// TSO's own <c>RestoreSurroundings</c> spacing, <c>(size - 2)</c>. Written as
        /// <c>Size - 2</c> rather than as a constant because it is a function of the lot's own size.
        /// </summary>
        /// <remarks>
        /// <b>This is the real pitch, and since Part 8 it is also the real one.</b> Lots are placed
        /// at their <c>LotLocations.iff</c> positions through
        /// <see cref="NeighbourhoodPixelTransform"/>, and the 2D map's own lot spacing is this
        /// number - so two real neighbours stand <c>Size - 2</c> apart and their <c>Size</c>-wide
        /// rectangles overlap by two tiles. That is TSO's geometry rather than an artefact, and it
        /// is why the packer's old <c>Size + 8</c> pitch
        /// (<see cref="PackedLotPitchTiles"/>) was wrong rather than merely tight.
        /// </remarks>
        /// <param name="lotSize">The lot's <see cref="NeighbourhoodLot.Size"/>, in lot tiles.</param>
        public static int LotViewLotPitchTiles(int lotSize)
        {
            //floored at one rather than left negative: TSO's own (size - 2) is a spacing between two
            //plots, and a one-tile lot has no such thing. A negative pitch would mirror the next lot
            //across the origin, which is the one answer a caller could not detect.
            return Math.Max(1, Math.Max(1, lotSize) - 2);
        }

        /// <summary>
        /// The pitch the <em>shelf</em> arrangement puts two neighbouring lots' origins at, in map
        /// tiles: the lot's own size plus the sidewalk margin it leaves between footprints.
        /// </summary>
        /// <remarks>
        /// Note the sign - TSO <em>subtracts</em> two and the shelf <em>adds</em> eight - and note
        /// what this is no longer for. It used to describe every generated neighbourhood, which is
        /// why its streets read as thinner than the lot view's even before their width is
        /// considered; since Part 8 the lots are at their real <c>LotLocations</c> positions, so the
        /// pitch between two of them is whatever the 2D map says (and is <c>Size - 2</c>, TSO's own).
        /// What is left is the number the surround's band arithmetic and the sub-world budget were
        /// written against, which is why it kept its name.
        /// </remarks>
        /// <param name="lotSize">The lot's <see cref="NeighbourhoodLot.Size"/>, in lot tiles.</param>
        public static int PackedLotPitchTiles(int lotSize)
        {
            return Math.Max(1, lotSize) + NeighbourhoodLayoutBuilder.SidewalkMargin;
        }

        /// <summary>Lot-view world units in one generated map tile's worth of length.</summary>
        /// <param name="mapTiles">A length in generated map tiles.</param>
        public static float LotViewWidthFor(float mapTiles)
        {
            return mapTiles * LotViewUnitsPerMapTile;
        }

        /// <summary>How wide the generated map's tarmac is in the lot view, in world units.</summary>
        public static float StreetTarmacLotViewUnits
        {
            get { return LotViewWidthFor(StreetTarmacMapTiles); }
        }

        /// <summary>How wide the generated map's whole street corridor is in the lot view.</summary>
        public static float StreetCorridorLotViewUnits
        {
            get { return LotViewWidthFor(StreetCorridorMapTiles); }
        }

        /// <summary>How wide the lot view's own tarmac is, in world units.</summary>
        public static float LotViewStreetTarmacUnits
        {
            get { return LotViewWidthFor(LotViewStreetTarmacTiles); }
        }

        /// <summary>How wide the lot view's own street corridor is, in world units.</summary>
        public static float LotViewStreetCorridorUnits
        {
            get { return LotViewWidthFor(LotViewStreetCorridorTiles); }
        }

        /// <summary>
        /// How many times wider a street's tarmac is in the lot view than in the generated surround
        /// drawn into it. Three, and it is the number a screenshot of "the roads are far too thin"
        /// is actually reporting.
        /// </summary>
        public static float StreetTarmacDeficit
        {
            get { return LotViewStreetTarmacUnits / StreetTarmacLotViewUnits; }
        }

        /// <summary>
        /// The same question asked of the whole corridor, pavements included: 33 units where the
        /// generated surround draws 12.
        /// </summary>
        public static float StreetCorridorDeficit
        {
            get { return LotViewStreetCorridorUnits / StreetCorridorLotViewUnits; }
        }

        /// <summary>
        /// How many elevation bytes there are in one tile of height. The mesh builder divides
        /// <see cref="NeighbourhoodMap.ElevationData"/> by twelve, so this is the inverse of the
        /// only scale in the pipeline.
        /// </summary>
        /// <remarks>
        /// <b>This is the tile-to-byte scale, not the unit that
        /// <see cref="QuantiseAltitude"/> multiplies by.</b> A lot's altitude is in
        /// <c>ARRY(0)</c> units, and one of those is
        /// <see cref="NeighbourhoodCityMeshBuilder.TilesPerArryUnit"/> of a tile - so the
        /// multiplier is <see cref="NeighbourhoodCityMeshBuilder.ElevationUnitsPerArryUnit"/>, nine
        /// quarters of a byte, and using this constant instead is the <c>16/3</c> error described on
        /// the class. The constant stays because the surround's own arithmetic is expressed in it.
        /// </remarks>
        public const int ElevationUnitsPerTile = (int)NeighbourhoodCityMeshBuilder.ElevationScale;

        /// <summary>
        /// A lot's own <c>ARRY(0)</c> altitude as an elevation byte.
        /// </summary>
        /// <remarks>
        /// <b>Nothing is written down here.</b> The whole method is
        /// <see cref="NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit"/>, i.e.
        /// <c>altitude * 9/4</c> rounded away from zero and clamped to a byte, and that is the point:
        /// the seam and the mesh now derive the same byte from the same arithmetic, so a lot's
        /// <c>BaseHeight</c> byte and the terrain height the mesh puts under it cannot disagree.
        /// Clamped rather than wrapped, because the byte cannot hold a 40-tile cliff and a wrapped
        /// value would be a cliff on the other side of the world.
        /// </remarks>
        /// <param name="altitude">The raw <c>ARRY(0)</c> altitude.</param>
        public static byte QuantiseAltitude(short altitude)
        {
            return NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(altitude);
        }

        /// <summary>
        /// A stored elevation byte as world height in tiles - the byte-to-world direction, derived
        /// from <see cref="NeighbourhoodCityMeshBuilder.WorldHeightFor"/> rather than restating the
        /// divisor.
        /// </summary>
        /// <remarks>
        /// Note the asymmetry with <see cref="QuantiseAltitude"/>: this answers in <em>tiles</em>,
        /// so composing the two does not return the <c>ARRY(0)</c> altitude that went in. It returns
        /// where the lot's own ground sits in the world, which is the question the two ends share -
        /// and it is lossy to within half an elevation byte, <c>1/24</c> of a tile, because that is
        /// the finest height the byte can express. <see cref="NeighbourhoodCityMeshBuilder"/>'s
        /// <c>WorldHeightForArryUnit</c> is the same quantity for the lot's own altitude without
        /// going through the byte.
        /// </remarks>
        /// <param name="elevation">The elevation byte.</param>
        public static float DequantiseAltitude(byte elevation)
        {
            return NeighbourhoodCityMeshBuilder.WorldHeightFor(elevation);
        }

        /// <summary>
        /// A <c>FSO.Content.Model.TerrainType</c> as a <see cref="NeighbourhoodTerrain"/> index.
        /// </summary>
        /// <remarks>
        /// A straight cast for the five drawable members, which share their values with the five
        /// drawable terrains by declaration, and grass for everything else - including
        /// <c>NULL</c> and the TS1-only grass variants, neither of which names a texture a
        /// generated map has.
        /// </remarks>
        /// <param name="type">The lot's own ground type.</param>
        public static byte TerrainIndexFor(TerrainType type)
        {
            switch (type)
            {
                case TerrainType.GRASS: return NeighbourhoodTerrain.Grass;
                case TerrainType.SAND: return NeighbourhoodTerrain.Sand;
                case TerrainType.ROCK: return NeighbourhoodTerrain.Rock;
                case TerrainType.SNOW: return NeighbourhoodTerrain.Snow;
                case TerrainType.WATER: return NeighbourhoodTerrain.Water;
                default: return NeighbourhoodTerrain.Grass;
            }
        }

        /// <summary>
        /// The generation input for a packed layout, with whichever connector mask has been bound to
        /// the layout's slot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One-way and lossless for the fields the generator uses. <c>MapX</c>/<c>MapY</c> now
        /// cross as well - Part 8 §8.2 calls dropping them here "the load-bearing bug", because a
        /// generator that cannot see where a lot was on the 2D map cannot be checked against it,
        /// cannot re-derive a mask from it, and cannot put a camera on it. <c>Rotation</c> and
        /// <c>HouseHash</c> stay behind: the layout keeps them for the facade and its cache, and the
        /// generator has no use for either.
        /// </para>
        /// <para>
        /// The mask is looked up rather than passed, and that is a deliberate seam rather than an
        /// oversight: the caller of this method has a <em>layout</em> and a layout does not know
        /// which neighbourhood mode it came from. <see cref="NeighbourhoodRoadInference"/> holds the
        /// mode-keyed cache and the explicit slot binding, and this is the one place that reads it -
        /// so a mask reaches the generator from the 2D map without a mode parameter appearing on any
        /// signature the renderer or the lifecycle already calls. A slot with nothing bound gets
        /// <c>null</c> and <see cref="RoadGraph"/> measures its own streets, which is the pre-Part-8
        /// behaviour and still the answer for a mode with no backdrop.
        /// </para>
        /// </remarks>
        /// <param name="layout">The packed layout, or <c>null</c> for an empty neighbourhood.</param>
        public static NeighbourhoodMapInput InputFor(NeighbourhoodLayout layout)
        {
            if (layout == null)
            {
                return new NeighbourhoodMapInput(0, NeighbourhoodLayout.DefaultMapSize,
                    new NeighbourhoodLotInput[0]);
            }

            var lots = new List<NeighbourhoodLotInput>(layout.Lots.Count);
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var lot = layout.Lots[i];
                lots.Add(new NeighbourhoodLotInput(
                    lot.Number,
                    lot.MapX,
                    lot.MapY,
                    lot.WorldX,
                    lot.WorldY,
                    lot.Size,
                    lot.Zone,
                    TerrainIndexFor(lot.TerrainType),
                    QuantiseAltitude(lot.BaseHeight),
                    (byte)lot.DoorSide,
                    lot.HasHouse));
            }

            return new NeighbourhoodMapInput(layout.Seed, layout.MapSize, lots);
        }

        /// <summary>
        /// <see cref="InputFor(NeighbourhoodLayout)"/> with the connector mask named rather than
        /// looked up. For a caller that has a mask in hand and no binding to point at - a test, and
        /// the path a workstream takes while wiring the lifecycle.
        /// </summary>
        /// <param name="layout">The packed layout, or <c>null</c> for an empty neighbourhood.</param>
        /// <param name="roadMask">The connector mask, or <c>null</c>.</param>
        public static NeighbourhoodMapInput InputFor(NeighbourhoodLayout layout, bool[] roadMask)
        {
            if (layout == null)
            {
                return new NeighbourhoodMapInput(0, NeighbourhoodLayout.DefaultMapSize,
                    new NeighbourhoodLotInput[0], roadMask);
            }

            var plain = InputFor(layout);
            if (roadMask == null) return plain;
            return new NeighbourhoodMapInput(plain.Seed, plain.MapSize, plain.Lots, roadMask);
        }

        /// <summary>
        /// The layout a generated map was made from, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Named for symmetry with <see cref="InputFor"/> and for the diagnostic log line - the
        /// generation report names its own input's lots, so a failure message can say which lot
        /// caused it. It does not reconstruct a layout: the packing is M1's and re-running it here
        /// would be a second, subtly different packer.
        /// </remarks>
        /// <param name="input">The generation input.</param>
        public static NeighbourhoodLayout DescribeInput(NeighbourhoodMapInput input)
        {
            if (input == null) return null;
            var lots = new List<NeighbourhoodLot>();
            for (int i = 0; i < input.Lots.Count; i++)
            {
                var lot = input.Lots[i];
                lots.Add(new NeighbourhoodLot
                {
                    Number = lot.Number,
                    MapX = lot.MapX,
                    MapY = lot.MapY,
                    WorldX = lot.WorldX,
                    WorldY = lot.WorldY,
                    Size = lot.EffectiveSize,
                    Zone = lot.Zone,
                    TerrainType = (TerrainType)lot.TerrainType,
                    BaseHeight = lot.BaseHeight,
                    DoorSide = (NeighbourhoodLotSide)lot.DoorSide,
                    HasHouse = lot.HasHouse
                });
            }
            return new NeighbourhoodLayout(0, (int)input.Seed, 0,
                NeighbourhoodLayout.CurrentSchemaVersion, input.MapSize, lots);
        }
    }
}
