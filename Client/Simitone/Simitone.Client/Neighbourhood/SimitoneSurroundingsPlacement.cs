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
    /// Where the generated neighbourhood sits in a live lot's coordinate space: the one tile of
    /// the generated map that is drawn at the lot's own origin, the matrix that takes the map
    /// there, and the lot's own light turned into the two numbers the city shaders want.
    ///
    /// <para><b>Why this is arithmetic and not a decision.</b> §7.4 says the terrain is drawn
    /// "translated so the active lot's rectangle aligns with the lot's own origin", and that is
    /// literally the whole of it: a lot in the generated world is
    /// <see cref="NeighbourhoodLot.Size"/> tiles square, and <see cref="NeighbourhoodLot.Size"/>
    /// is the lot's own <c>SIMI.GlobalData[23]</c>, which <c>VMTS1Activator</c> turns "straight
    /// into a <c>Size x Size</c> blueprint" - so a generated tile <em>is</em> a lot tile, and the
    /// log's <c>the player's own x[92,139) z[114,161)</c> is a 47-tile lot because
    /// <c>139 - 92 == 47</c> is that lot's own size. One generated tile becomes
    /// <see cref="LotViewUnitsPerTile"/> lot-view world units - the lot view's own
    /// <c>WorldState.WorldUnitsPerTile</c>, which is the number the lot camera's target and
    /// every wall, floor and object in the lot are already measured in, and the number the engine
    /// itself multiplies a neighbouring sub-world's tile delta by - and the lot's packed
    /// rectangle's minimum corner becomes <c>(0, 0, 0)</c>. Nothing else has to be agreed
    /// between the two spaces for the surround to line up, which is why it can be a struct and
    /// a test rather than something read back out of a draw call.
    /// </para>
    ///
    /// <para><b>What is <em>not</em> at this scale: nothing.</b> The ground, the facades, the lot
    /// rectangles under the player and the street between them are all at the lot view's own scale,
    /// because the map tile and the lot tile are the same thing. A lot's own <c>RestoreRoad</c> band
    /// reaches six tiles into the lot it stands on, the packer reserves
    /// <see cref="SimitoneLotViewRoad.StreetCorridorTiles"/> of corridor between two lots, and the
    /// surround's band draws exactly the tiles neither of them draws - see
    /// <see cref="SimitoneLotViewRoad"/> for the arithmetic and for why the corridor is the map's
    /// eight rather than the lot view's eleven.</para>
    ///
    /// <para><b>Where the height comes from.</b> The generated map, through
    /// <see cref="NeighbourhoodFacadeTransform.GroundHeightFor"/> - the same call that placed
    /// the neighbouring facades. That is deliberate: the surround and the houses standing in it
    /// have to be standing on the same ground, and the only way to guarantee that is for both to
    /// ask one question of one place. The lot view's own ground is at
    /// <c>elevation * TerrainFactor * 3 - BaseAlt * TerrainFactor * 3</c>
    /// (<c>TerrainComponent</c>), which is zero at the lot's base altitude, so putting the
    /// generated ground under the active lot at zero is what makes the two grounds meet.
    /// </para>
    ///
    /// <para><b>One ground, and this type owns where it comes from.</b> The map has one surface and
    /// the neighbourhood view draws all of it; this type decides where that surface sits relative
    /// to the lot the player is standing in, and <see cref="SubWorldAltitudesFor"/> hands the same
    /// heights to every real neighbouring lot in the lot view's own altitude units - so a
    /// neighbour's ground is not a second reading of the same map but the first one, corner for
    /// corner, and the step a player reported at the edge of every plot is arithmetic that no longer
    /// has anywhere to come from. The facade side is the same claim read the other way round:
    /// <see cref="FacadeGroundInLotView"/> is the height a neighbour's mesh is hung at, and it is
    /// the same number the mesh's own vertex over that tile carries.</para>
    ///
    /// <para><b>What "the lot's lighting" is.</b> The lot view's outdoor colour
    /// (<c>WorldState.OutsideColor</c>, which is what the lot clears its background to and what
    /// <c>VMArchitecture.SetTimeOfDay</c> writes) and the lot's sun vector
    /// (<c>WorldState.Light.LightVec</c>, the same vector the lot's own terrain component
    /// shades with). Both are read by the screen and handed in here, so the translation of a
    /// pair of engine values into the city shaders' two parameters is a pure function of
    /// something a test can write down.
    /// </para>
    /// </summary>
    public readonly struct SimitoneSurroundingsPlacement
    {
        /// <summary>
        /// Lot-view world units per generated map tile, and therefore per lot tile: the surround's
        /// whole scale, taken from <see cref="NeighbourhoodMapAdapter.LotViewUnitsPerMapTile"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two facts behind it are that a generated map tile <em>is</em> a lot tile
        /// (<c>NeighbourhoodLot.Size</c> is the lot's own <c>SIMI.GlobalData[23]</c>, which the
        /// lot view sizes its architecture from, and the packer stamps that many tiles into the map)
        /// and that the lot view is <c>FSO.LotView.WorldState.WorldUnitsPerTile</c> - three - world
        /// units to a tile. Both are written down and derived at
        /// <see cref="NeighbourhoodMapAdapter"/>, which is the seam where the two unit systems meet,
        /// so that there is one spelling of this number in the tree rather than one here and one
        /// there.
        /// </para>
        /// <para>
        /// <b>It is not a tuning constant, and changing it does not fix a street.</b> The
        /// neighbouring sub-worlds are placed by the engine at
        /// <c>SubWorldComponent.GlobalPosition * 3</c>, where <c>GlobalPosition</c> is the raw
        /// map-tile delta between two packed rectangles - so every real neighbouring lot is already
        /// standing at this scale, and rescaling the mesh alone would slide the ground out from
        /// under them. How wide a street is between two lots is <em>not</em> a scale question at
        /// all: it is how much ground the packer leaves between their rectangles, which is
        /// <see cref="NeighbourhoodLayoutBuilder.StreetCorridorTiles"/>.
        /// </para>
        /// </remarks>
        public const float LotViewUnitsPerTile = NeighbourhoodMapAdapter.LotViewUnitsPerMapTile;

        /// <summary>
        /// The light's strength multiplier. Above 1 because the generated ground textures are
        /// Simitone's own flat terrain colours rather than TSO's pre-lit city tiles, and a
        /// <c>LightCol</c> of exactly the outdoor colour leaves a noon lot looking like dusk.
        /// </summary>
        public const float LightStrength = 1.15f;

        /// <summary>
        /// The floor on each light channel, so midnight is dim rather than black.
        /// </summary>
        /// <remarks>
        /// The lot's outdoor colour really does go to near-black at night, and a light of zero
        /// would leave the neighbourhood invisible - a worse answer than a dim one, and a much
        /// worse one than the sky the lot itself is drawing, which is never fully black.
        /// </remarks>
        public const float MinLightChannel = 0.35f;

        /// <summary>
        /// How many tiles of <em>gap</em> a neighbouring lot may sit away from the property and
        /// still be given a real lot, and how far past the property its ground and its street are
        /// drawn.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The drawn region is not built at this radius: <see cref="For(NeighbourhoodMap,NeighbourhoodLot)"/>
        /// builds the whole connected map, which is <see cref="SimitoneSurroundingsCull.ConnectedMap"/>,
        /// the same rectangle the
        /// neighbourhood view draws and the same <see cref="NeighbourhoodMap"/> the map view's
        /// terrain renderer read. What is left at this radius is the <em>neighbour</em> budget: how
        /// close a lot has to be to be stood up as a real <c>SubWorldComponent</c> with its own
        /// contents, or built as a facade mesh, instead of being left to the far side of the fog
        /// where the neighbourhood view draws it as a facade too.
        /// </para>
        /// <para>
        /// Two questions, one number, and the reason they agree is that a neighbour is only worth a
        /// whole lot while its plot is close enough to be seen clearly: the packer leaves one street
        /// corridor between two lots
        /// (<see cref="NeighbourhoodLayoutBuilder.StreetCorridorTiles"/>), so a neighbour's plot
        /// starts about half a corridor past this lot's edge and forty tiles of gap reaches the
        /// fronts of the next two rows of houses. <see cref="RadiusIsShared"/> is the assertion
        /// that the two do not drift apart.
        /// </para>
        /// </remarks>
        public const int CullRadiusTiles = 40;

        /// <summary>
        /// How much of a generated map tile's height counts as a visible step at the property line,
        /// in map tiles.
        /// </summary>
        /// <remarks>
        /// The generated ground is quantised to <see cref="NeighbourhoodCityMeshBuilder.ElevationScale"/>
        /// (= 12) elevation units to a tile, so a bank round the property that is thinner than one
        /// step is not worth a triangle - and a bank that is thinner than one step is not a step at
        /// all. See <see cref="SimitoneSurroundingsCrop"/>.
        /// </remarks>
        public const float SeamEpsilon = 1f / 64f;

        private SimitoneSurroundingsPlacement(
            NeighbourhoodLot lot, Vector3 mapTileOrigin,
            SimitoneSurroundingsCull cull, SimitoneSurroundingsCull drawn)
        {
            Lot = lot;
            MapTileOrigin = mapTileOrigin;
            Cull = cull;
            DrawnRegion = drawn;
        }

        /// <summary>The lot the player is standing in, or <c>null</c> when there is no placement.</summary>
        public NeighbourhoodLot Lot { get; }

        /// <summary>
        /// The point in generated-map tile space that is drawn at the lot view's origin. Its x
        /// and z are the lot's packed rectangle's minimum corner; its y is the ground height the
        /// generated map is measured down to - see <see cref="GroundOriginFor"/>.
        /// </summary>
        public Vector3 MapTileOrigin { get; }

        /// <summary>
        /// The square of the shared map that neighbouring lots are drawn from: the lot's own
        /// rectangle plus <see cref="CullRadiusTiles"/> on every side, clipped to the map. This is
        /// the <em>neighbour</em> budget - which lots get a real sub-world or a facade - and not
        /// the extent of the ground.
        /// </summary>
        public SimitoneSurroundingsCull Cull { get; }

        /// <summary>
        /// The square of the shared map the surround draws ground and streets for: the whole
        /// connected neighbourhood, which is the same raster the neighbourhood view draws.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is §8.3's "the lot view renders the same map" and it is a rectangle, not a second
        /// world. It is the same generated <see cref="NeighbourhoodMap"/> raster the map view
        /// draws (each view builds its own mesh from it - the removed <c>NeighbourhoodSharedWorld</c>
        /// no longer hands one instance to both), so the terrain, the neighbouring lots and their
        /// facades are a pure function of one raster. Before this it was a 144x144 crop of
        /// that raster centred on the active lot, which is what made the lot view a second,
        /// truncated view of the same world rather than a closer look at it.
        /// </para>
        /// <para>
        /// <b>Why it is still a rectangle and not "no cut".</b> <see cref="SimitoneSurroundingsCull"/>
        /// is the type the crop, the road band and the fog all take, and a null cut would mean three
        /// separate answers to "which tiles". At this radius the rectangle is the whole map, so the
        /// crop is a pass-through, the band covers every lot's frontage, and the fog's floor is the
        /// far corner of the island - which is the same floor the neighbourhood view uses, and is
        /// why there is no seam to hide.
        /// </para>
        /// <para>
        /// <b>And what it costs.</b> The whole island is about 138,000 quads, which is why the
        /// region is not what limits the per-frame cost: the crop keeps each source chunk's quads
        /// together (<see cref="SimitoneSurroundingsCropResult.LayerChunkStarts"/>) and the draw
        /// submits only the chunks inside the camera's frustum. The whole-map mesh itself is
        /// cached per slot (<see cref="SimitoneSurroundingsMeshCache"/>), so a lot entry pays for
        /// the crop and the upload but not for the mesh build.
        /// </para>
        /// </remarks>
        public SimitoneSurroundingsCull DrawnRegion { get; }

        /// <summary>Whether this placement points at a real lot.</summary>
        public bool IsValid
        {
            get { return Lot != null; }
        }

        /// <summary>
        /// Generated-map tile space to lot-view world space. The single place the arithmetic is
        /// written; the matrix below is the same thing in the form the shaders take.
        /// </summary>
        /// <param name="mapTile">A point in generated-map tiles.</param>
        public Vector3 ToLotView(Vector3 mapTile)
        {
            return (mapTile - MapTileOrigin) * LotViewUnitsPerTile;
        }

        /// <summary>
        /// The generated world placed into the lot view: moved so <see cref="MapTileOrigin"/>
        /// lands on the lot's own origin, then scaled by <see cref="LotViewUnitsPerTile"/> on
        /// every axis.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Translate before scale, and the order is not cosmetic. XNA multiplies row vectors -
        /// <c>Vector3.Transform(v, A * B)</c> is <c>Vector3.Transform(Vector3.Transform(v, A), B)</c>
        /// - so the leftmost factor is the one applied first. Scaling first would scale the
        /// translation too and put the surround three times further out than the lot, which is
        /// exactly the bug this type's own test caught: TSO's city terrain writes the same pair
        /// in this order, in <c>Terrain.DrawSurrounding</c>.
        /// </para>
        /// <para>
        /// The translation is in map tiles and the scale is applied to the result, so the packed
        /// rectangle's integers are used in the only space they are meaningful in.
        /// </para>
        /// </remarks>
        public Matrix WorldMatrix
        {
            get
            {
                return Matrix.CreateTranslation(-MapTileOrigin)
                    * Matrix.CreateScale(LotViewUnitsPerTile);
            }
        }

        /// <summary>
        /// Where the player's own lot ground sits in the lot view, in blueprint altitude units, as
        /// the surround has to be raised to meet it: the median of the lot's altitude field less
        /// the <c>BaseAlt</c> its terrain is already drawn with. Zero for a blueprint with no
        /// field, so the surround is then left where it always was.
        /// </summary>
        /// <remarks>
        /// The played TS1 lot keeps <c>BaseAlt</c> at zero (walls, roofs and avatars bake the
        /// altitude into their geometry when built), so its ground is at an absolute
        /// <c>median * TerrainFactor * 3</c> and the surround - measured down to the lot's own
        /// ground, <see cref="GroundOriginFor"/> - is lifted by exactly that instead.
        /// </remarks>
        /// <param name="blueprint">The live lot's blueprint, or <c>null</c>.</param>
        public static int LotGroundAltitudeFor(FSO.LotView.Model.Blueprint blueprint)
        {
            if (blueprint == null || blueprint.Altitude == null || blueprint.Altitude.Length == 0)
            {
                return 0;
            }
            return FSO.LotView.Model.Blueprint.MedianAltitude(blueprint.Altitude) - blueprint.BaseAlt;
        }

        /// <summary>
        /// <see cref="WorldMatrix"/> lifted by a lot-ground altitude, in blueprint altitude units
        /// (<see cref="LotGroundAltitudeFor"/>). Exactly <see cref="WorldMatrix"/> when it is zero.
        /// </summary>
        /// <param name="lotGroundAltitude">The lot's ground, in blueprint altitude units.</param>
        public Matrix WorldMatrixAt(int lotGroundAltitude)
        {
            if (lotGroundAltitude == 0) return WorldMatrix;
            return WorldMatrix * Matrix.CreateTranslation(0f,
                lotGroundAltitude * SimitoneNeighbourTerrain.LotTerrainFactor * LotViewUnitsPerTile, 0f);
        }

        /// <summary>
        /// A placement for a lot in a generated map, whether or not the map can be asked for its
        /// ground height. A null map leaves the height at zero, which puts the surround at the
        /// map's own zero elevation rather than refusing to place it at all.
        /// </summary>
        /// <remarks>
        /// <b>Two regions, and this is where the connected map arrives.</b> The neighbour cut
        /// (<see cref="Cull"/>) is still built at <see cref="CullRadiusTiles"/>, because a real
        /// sub-world costs a whole <c>World</c> and a <c>VM</c> and a facade costs seconds of
        /// rasterising a house, and neither should be spent on a plot across the island. The drawn
        /// region (<see cref="DrawnRegion"/>) is <b>the whole map</b>, because the ground and the
        /// streets are free to draw and the map view already draws them: they are the same
        /// <see cref="NeighbourhoodMap"/>, so a player who walks out of their lot is standing in
        /// the neighbourhood they were just looking at rather than in a 144-tile crop of it.
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static SimitoneSurroundingsPlacement For(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            if (lot == null) return default(SimitoneSurroundingsPlacement);
            //the cull carries the same ground height, computed once: the placement's translation and
            //the seam the crop cuts both have to be measured from the same number, and two calls
            //into GroundOriginFor would be two chances for them to disagree
            var cull = SimitoneSurroundingsCull.For(map, lot, CullRadiusTiles);
            return new SimitoneSurroundingsPlacement(
                lot, new Vector3(lot.WorldX, cull.GroundHeight, lot.WorldY), cull,
                SimitoneSurroundingsCull.ConnectedMap(map, lot));
        }

        /// <summary>
        /// Where a neighbouring house's facade vertex ends up, in lot-view world units.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The facade cache and the surround terrain work in the same generated-map tile space and
        /// are drawn through the same <see cref="WorldMatrix"/>, so a neighbour's house lands on
        /// its own lot's rectangle by construction rather than by agreement. This is the one place
        /// that says so out loud, and it is here because it is the arithmetic a screenshot cannot
        /// check: a facade that is one lot width out, or turned ninety degrees, looks like a house
        /// on the wrong plot and nothing in the log says so.
        /// </para>
        /// <para>
        /// The height argument is zero because this answers the horizontal question - which plot
        /// and which way round - and the ground is
        /// <see cref="NeighbourhoodFacadeTransform.GroundHeightFor"/>'s business, applied once to
        /// the whole mesh rather than per vertex. <see cref="FacadeGroundInLotView"/> is the
        /// vertical half of the same claim.
        /// </para>
        /// </remarks>
        /// <param name="placement">This lot's placement.</param>
        /// <param name="neighbour">The lot a neighbouring house belongs to.</param>
        /// <param name="local">A point in that lot's own tile space, in tiles.</param>
        public static Vector3 FacadePointInLotView(
            SimitoneSurroundingsPlacement placement, NeighbourhoodLot neighbour, Vector3 local)
        {
            if (neighbour == null) return local;
            return placement.ToLotView(NeighbourhoodFacadeTransform.ToWorld(neighbour, local, 0f));
        }

        /// <summary>
        /// The height, in lot-view world units, a neighbouring house's base sits at - which has to
        /// be the same height the surround's own ground is at under it, or the house is standing on
        /// a plinth or in a hole.
        /// </summary>
        /// <remarks>
        /// Both numbers come from the same question asked of the same map
        /// (<see cref="NeighbourhoodFacadeTransform.GroundHeightFor"/>, the elevation byte at the
        /// lot's minimum corner) and the same translation, so the answer is that the two agree; the
        /// method exists so the claim is a test rather than a comment. The surround's terrain is
        /// measured down to <see cref="SimitoneSurroundingsCull.GroundHeight"/>, which is the
        /// <em>minimum</em> on the active lot's border ring, so a neighbour whose own ground is
        /// higher stands that much above the lot - which is the generated neighbourhood's own
        /// shape, not a misplacement.
        /// </remarks>
        /// <param name="placement">This lot's placement.</param>
        /// <param name="map">The generated map.</param>
        /// <param name="neighbour">The lot a neighbouring house belongs to.</param>
        public static float FacadeGroundInLotView(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map, NeighbourhoodLot neighbour)
        {
            if (neighbour == null) return 0f;
            var ground = NeighbourhoodFacadeTransform.GroundHeightFor(map, neighbour);
            return placement.ToLotView(
                new Vector3(neighbour.WorldX, ground, neighbour.WorldY)).Y;
        }

        /// <summary>
        /// The base level of the lot this placement is for, in the helpers' own units - the number
        /// every neighbouring sub-world's <c>BaseAlt</c> is measured against.
        /// </summary>
        /// <remarks>
        /// Read from the lot's own <see cref="SimitoneNeighbourTerrain"/>, exactly as
        /// <see cref="SimitoneSurroundingsNeighbour.BaseLevel"/> is, so the plan and the bridge
        /// cannot end up with two answers to one question. Null map or lot, or a lot whose rectangle
        /// the map cannot answer for, is zero - a lot with no ground has no base level, and a
        /// sub-world hung from zero is at worst the height it would have been at anyway.
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        public int ActiveBaseLevel
        {
            get
            {
                SimitoneNeighbourTerrain terrain;
                string reason;
                if (!SimitoneNeighbourTerrain.TryBuild(Cull.Map, Lot, out terrain, out reason)) return 0;
                return terrain.BaseLevel;
            }
        }

        /// <summary>
        /// This lot's own base level in <em>blueprint altitude units</em> - the number
        /// <c>Blueprint.BaseAlt</c> wants, and therefore the datum the player's own ground has to be
        /// measured from for the surround to meet it. Zero when there is no map or no lot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It is <see cref="GroundOriginFor"/> restated, and that is the whole claim.</b> The
        /// surround is measured down to <see cref="GroundOriginFor"/> in map tiles; the lot view
        /// multiplies an altitude by <see cref="SimitoneNeighbourTerrain.LotTerrainFactor"/> times
        /// <see cref="LotViewUnitsPerTile"/> to get world units, and
        /// <see cref="GroundOriginFor"/> is already in the same world units' worth of tiles, so
        /// dividing it by <see cref="SimitoneNeighbourTerrain.LotTerrainFactor"/> is the altitude
        /// whose drawn height is exactly the surround's. One conversion, written once.
        /// </para>
        /// <para>
        /// <b>Why the two ends are not bit-identical, and why it does not matter.</b> The two
        /// answers are a map elevation byte and a lot's own <c>ARRY(0)</c> median:
        /// <c>QuantiseAltitude</c> rounds the median onto whole elevation bytes at nine quarters of
        /// a byte to the unit, so they can differ by up to half a byte - half an elevation unit
        /// quantised, which is at most <c>1 / 24</c> of a tile of world height, a fortieth of a
        /// wall. The altitude is rounded rather than truncated for the same reason, so the usual
        /// disagreement is under a hundredth of a tile. Anything larger than that would be a real
        /// disagreement about the lot's height rather than a quantisation, and
        /// <c>TerrainSeamSolver</c>'s own pin is what pins the map to the median in the first
        /// place.
        /// </para>
        /// <para>
        /// <b>Not the same number as <see cref="ActiveBaseLevel"/>.</b> That one is the
        /// <em>helpers'</em> hundredths of a height byte, and it is what a neighbouring sub-world's
        /// own <c>BaseAlt</c> is a difference of. This one is altitude units, for the player's own
        /// blueprint. Two scales, two names, and <c>Blueprint.BaseAlt</c> is this one.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static int BaseAltitudeFor(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            if (map == null || lot == null) return 0;
            return (int)Math.Round(
                GroundOriginFor(map, lot) / SimitoneNeighbourTerrain.LotTerrainFactor,
                MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// The same number as <see cref="BaseAltitudeFor"/>, for the lot this placement is for.
        /// </summary>
        public int ActiveBaseAltitude
        {
            get { return BaseAltitudeFor(Cull.Map, Lot); }
        }

        /// <summary>
        /// The generated-map height the whole surround is measured down to: the <em>mean of the four
        /// centre samples</em> of this lot's own rectangle, which is the number every neighbouring
        /// sub-world is hung from.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the sub-worlds' own base level, in the map's own units.</b>
        /// <c>SimitoneSurroundingsRestore.BaseAltitudeFor</c> hangs each neighbour at
        /// <c>activeBase - neighbourBase</c>, and both terms are
        /// <c>VMLotTerrainRestoreTools.GetBaseLevel</c>'s: the mean of the centre four of a 4x4
        /// height grid. So the plane the surround's ground has to lie on is <em>this lot's</em>
        /// centre-four mean, and a surround measured to anything else is a step at the border of
        /// every lot standing in it. A neighbourhood with the player's lot on a slope and its
        /// neighbours on the flat is the case that shows it: measured to the old ring minimum, the
        /// surround sat a fraction of a tile above a sub-world whose own ground was at zero, all the
        /// way round the property, and nothing in the log said so.
        /// </para>
        /// <para>
        /// The one tile at the rectangle's minimum corner was the answer before that, and the lowest
        /// elevation on its border ring was the answer before this. Both made the offset independent
        /// of which tile happens to sit at the origin, which was the point of them, and neither was
        /// a height that anything else in the pipeline is measured from.
        /// </para>
        /// <para>
        /// The four samples are the four <c>SimitoneNeighbourTerrain</c> averages, and the answer is
        /// the mean of their elevation bytes over
        /// <see cref="NeighbourhoodCityMeshBuilder.ElevationScale"/> - the same conversion the
        /// mesh's own vertices go through. A lot whose rectangle is smaller than that grid, or a map
        /// that cannot answer, falls back to the corner tile, which is
        /// <see cref="NeighbourhoodFacadeTransform.GroundHeightFor"/>'s answer and the number the
        /// neighbouring facades stand on.
        /// </para>
        /// <para>
        /// <b>What this does not fix, and cannot.</b> A sub-world's own height field moves in steps
        /// of one helpers' height byte - 22.5 elevation bytes, one and seven eighths of a tile of
        /// world height - so a neighbourhood graded more finely than that has a sub-world whose
        /// ground is up to half a step from the surround's whatever is subtracted. That is the
        /// sub-world's own quantisation and it is described on
        /// <see cref="SimitoneNeighbourTerrain"/>; what this plane removes is the
        /// <em>systematic</em> part of the join, so that on a flat neighbourhood - which is nearly
        /// all of them - the two grounds meet at exactly the same height.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static float GroundOriginFor(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            var corner = NeighbourhoodFacadeTransform.GroundHeightFor(map, lot);
            if (map == null || lot == null) return corner;

            var size = Math.Max(0, lot.Size);
            if (size < SimitoneNeighbourTerrain.GridSize) return corner;
            var minX = Math.Max(0, Math.Min(map.Width - 1, lot.WorldX));
            var minZ = Math.Max(0, Math.Min(map.Height - 1, lot.WorldY));

            //integer arithmetic throughout, so two calls with the same map answer with the same
            //height, and so the samples land on exactly the tiles SimitoneNeighbourTerrain reads
            //for GetBaseLevel: sample i of three along an axis is min + i * (size - 1) / 3
            var span = size - 1;
            long total = 0;
            for (int j = 1; j <= 2; j++)
            {
                var z = Math.Max(0, Math.Min(map.Height - 1,
                    minZ + (j * span) / (SimitoneNeighbourTerrain.GridSize - 1)));
                for (int i = 1; i <= 2; i++)
                {
                    var x = Math.Max(0, Math.Min(map.Width - 1,
                        minX + (i * span) / (SimitoneNeighbourTerrain.GridSize - 1)));
                    total += map.ElevationData[map.Index(x, z)];
                }
            }
            return total / 4f / NeighbourhoodCityMeshBuilder.ElevationScale;
        }

        /// <summary>
        /// Whether a lot has drawn ground under the tile its whole placement hangs from - its
        /// minimum corner, which is where <see cref="NeighbourhoodFacadeTransform.GroundHeightFor"/>
        /// reads the facade's base, where <see cref="SimitoneSurroundingsPlan.SubWorldPositionFor"/>
        /// anchors the sub-world's own origin, and which is therefore the one tile a lot that has no
        /// ground under it is visibly standing in the air over.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why the mesh's own diamond and nothing else.</b>
        /// <see cref="NeighbourhoodCityMeshBuilder.IsInsideDiamond"/> is the rule every draw path
        /// uses - the ground walk, the road walk and this one - so a lot cannot be placed on a tile
        /// the builder would have left empty. Part 9 §9.1 is the bug this exists for: real
        /// <c>LotLocations</c> positions landing in the cut corners, where the mesh emits neither
        /// ground quads nor road quads, which reads as "disconnected houses floating in a void".
        /// The diamond-aligned <c>NeighbourhoodPixelTransform</c> fixed the centres; this is the
        /// guard that keeps a placement from ignoring it.
        /// </para>
        /// <para>
        /// <b>Why the base tile and not the whole rectangle.</b> The diamond's edge moves one tile
        /// in x per tile in row, so a 64-tile plot whose centre is a street corridor inside the
        /// clip (§9.5's guarantee) overhangs it at its far corner - by design, because the fit is a
        /// fit and the alternative is a neighbourhood a third again smaller. A lot like that still
        /// has real ground under the house it stands on and real ground under its street; refusing
        /// it would empty the edge of the island of houses, which is a worse answer than a plot
        /// whose far verge fades out with the island's own skirt. <see cref="GroundlessTilesOf"/>
        /// measures the overhang so it can be reported rather than hidden.
        /// </para>
        /// <para>
        /// <b>A map the builder does not mesh is not asked.</b>
        /// <see cref="NeighbourhoodCityMeshBuilder.CanMesh"/> refuses anything that is not a
        /// <see cref="NeighbourhoodCityMeshBuilder.MapSize"/> square, so the surround refuses it too
        /// and there is no mesh for a lot to stand on; the question does not arise, and the answer is
        /// "yes" rather than a refusal built on a diamond that does not describe the map. Every
        /// production map is 512 wide; the smaller ones are fixtures.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static bool GroundIsDrawnUnder(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            if (map == null || lot == null) return false;
            if (!IsMeshed(map)) return true;
            return NeighbourhoodCityMeshBuilder.IsInsideDiamond(
                lot.WorldX, lot.WorldY);
        }

        /// <summary>
        /// How many of a lot's footprint tiles carry no drawn ground, so a plot overhanging the
        /// island's edge can be reported instead of silently drawn.
        /// </summary>
        /// <remarks>
        /// Zero for a map the builder does not mesh, for the reason
        /// <see cref="GroundIsDrawnUnder"/> gives, and for a lot with no size: there is nothing to
        /// overhang. Pure arithmetic over the diamond's own edges, so it is assertable without a
        /// mesh.
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static int GroundlessTilesOf(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            if (map == null || lot == null || !IsMeshed(map)) return 0;
            var size = Math.Max(0, lot.Size);
            var missing = 0;
            for (int dz = 0; dz < size; dz++)
            {
                var row = lot.WorldY + dz;
                for (int dx = 0; dx < size; dx++)
                {
                    if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(lot.WorldX + dx, row)) missing++;
                }
            }
            return missing;
        }

        /// <summary>
        /// Whether this is the map the mesh builder meshes at all, which is the precondition for
        /// asking any question about where its ground is.
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        public static bool IsMeshed(NeighbourhoodMap map)
        {
            return map != null
                && map.Width == NeighbourhoodCityMeshBuilder.MapSize
                && map.Height == NeighbourhoodCityMeshBuilder.MapSize;
        }

        /// <summary>
        /// The blueprint altitude a sub-world's own ground has to carry at one of its corner tiles
        /// for that ground to <em>be</em> the ground the surround draws - the altitude of the merged
        /// single surface, corner for corner.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why the altitude at all, and not a height.</b> A sub-world's ground is drawn by the lot
        /// view's own <c>TerrainComponent</c>, which is told how high the ground is in
        /// <c>Blueprint.Altitude</c>: a world of its own, with no access to the surround's mesh, its
        /// origin or its elevation raster. So the way to make two surfaces one surface is to write
        /// the surround's heights into the sub-world's own altitude units, and this is that
        /// conversion - the one place it is written down.
        /// </para>
        /// <para>
        /// <b>The arithmetic, and why the two threes cancel.</b> The surround puts a map tile's
        /// ground at <c>(WorldHeightFor(elevation) - MapTileOrigin.Y) * LotViewUnitsPerTile</c> world
        /// units of height, and the lot view puts an altitude <c>A</c> at
        /// <c>(A - BaseAlt) * LotTerrainFactor * LotViewUnitsPerTile</c>
        /// (<c>TerrainComponent.GetElevationPoint</c> less the <c>BaseAlt</c> its world matrix
        /// subtracts). <c>LotViewUnitsPerTile</c> is three on both sides - the surround's scale and
        /// the lot view's <c>WorldUnitsPerTile</c> are the same number, which is the whole reason the
        /// surround is measured in lot tiles at all - so it divides out and what is left is
        /// <c>A = BaseAlt + (WorldHeightFor(elevation) - MapTileOrigin.Y) / LotTerrainFactor</c>.
        /// One elevation byte is therefore <c>1 / 12 / 0.01875 = 4.444</c> altitude units, which is
        /// the same <c>100 / LotAltitudeUnitsPerArryUnit</c> the base-level arithmetic is made of
        /// seen from the other end.
        /// </para>
        /// <para>
        /// <b>What is left over, and what it is worth.</b> The elevation byte is a whole number and
        /// the mean of four of them need not be, so the answer is rounded to the nearest altitude
        /// unit: at most half a unit, and one unit is
        /// <c>0.01875 * 3 = 0.056</c> world units - about six hundredths of a tile, and well under a
        /// pixel at any lot-view zoom. Every corner in a flat neighbourhood lands on the same value,
        /// so this is the only error in the join at all.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="placement">The active lot's placement, for the ground the surround is measured to.</param>
        /// <param name="baseAltitude">
        /// The sub-world's <c>Blueprint.BaseAlt</c> - <see cref="SimitoneSurroundingsRestore.BaseAltitudeFor"/>
        /// of the two lots' base levels.
        /// </param>
        /// <param name="mapX">The map tile whose ground this corner carries, in map tiles.</param>
        /// <param name="mapZ">The map tile whose ground this corner carries, in map tiles.</param>
        public static short SubWorldAltitudeFor(
            NeighbourhoodMap map, SimitoneSurroundingsPlacement placement,
            int baseAltitude, int mapX, int mapZ)
        {
            var height = NeighbourhoodCityMeshBuilder.WorldHeightFor(
                ElevationByteAt(map, mapX, mapZ)) - placement.MapTileOrigin.Y;
            return AltitudeFor(height, baseAltitude);
        }

        /// <summary>
        /// <see cref="SubWorldAltitudeFor(NeighbourhoodMap,SimitoneSurroundingsPlacement,int,int,int)"/>
        /// with the corner's height read off the lot's own <c>ARRY(0)</c> plane where one is
        /// available, and so a sub-world whose ground is the lot's real grading rather than the
        /// generated map's flat pin.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The map answer and the plane answer are the same shape, and the shape is the point: both
        /// are <c>baseAltitude + (worldHeight - MapTileOrigin.Y) / LotTerrainFactor</c>, and only
        /// <c>worldHeight</c> differs - <see cref="NeighbourhoodCityMeshBuilder.WorldHeightFor"/> of
        /// a map elevation byte against
        /// <see cref="NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit"/> of a
        /// <c>ARRY(0)</c> altitude measured from that lot's own median. So a caller with a plane
        /// gets a graded field in the very same altitude units, and a caller without one gets
        /// today's flat field.
        /// </para>
        /// <para>
        /// <b>The median is the lot's own, and it is subtracted.</b> A <c>ARRY(0)</c> altitude is
        /// absolute and the field this writes is relative, so the reference has to be the level the
        /// lot's own ground is measured from - the same median the player's lot is lifted to meet
        /// (<see cref="LotGroundAltitudeFor"/>). Without the subtraction a graded lot would be lifted by its whole
        /// own altitude, which is precisely the S1 defect moved from the player's lot to its
        /// neighbours.
        /// </para>
        /// <para>
        /// <b>What it costs at the property line.</b> The surround's own ground under a neighbour
        /// is still the map's, and the sub-world's own ground is drawn over it - the lot view clears
        /// the depth buffer after the surround - so a graded neighbour replaces the surround's flat
        /// copy inside its own rectangle rather than fighting it. What is left is the join at the
        /// property line, where the neighbour's real border height is whatever its real terrain
        /// says and the surround's is the pinned median. That is the shape of the lot's own ground
        /// rather than an error, and it is the same shape the player's own lot has against the
        /// surround; the bank along the property line
        /// (<see cref="SimitoneSurroundingsCrop"/>) is what closes such a step, and it is the
        /// active lot's border ring that the crop walks today.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="placement">The active lot's placement, for the ground the surround is measured to.</param>
        /// <param name="baseAltitude">The sub-world's <c>Blueprint.BaseAlt</c>.</param>
        /// <param name="mapX">The map tile whose ground this corner carries, in map tiles.</param>
        /// <param name="mapZ">The map tile whose ground this corner carries, in map tiles.</param>
        /// <param name="lot">
        /// The lot whose plane <paramref name="arry"/> is, for the lot-local index and the median -
        /// or <c>null</c>, which reads the map.
        /// </param>
        /// <param name="arry">
        /// The lot's own <c>ARRY(0)</c> plane, indexed by <em>lot-local</em> tile, or
        /// <see cref="SimitoneArryAltitude.None"/> to read the map.
        /// </param>
        public static short SubWorldAltitudeFor(
            NeighbourhoodMap map, SimitoneSurroundingsPlacement placement,
            int baseAltitude, int mapX, int mapZ, NeighbourhoodLot lot, SimitoneArryAltitude arry)
        {
            if (!arry.IsValid || lot == null)
            {
                return SubWorldAltitudeFor(map, placement, baseAltitude, mapX, mapZ);
            }
            var height = NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(
                arry.At(mapX - lot.WorldX, mapZ - lot.WorldY) - arry.Median(Math.Max(1, lot.Size)));
            return AltitudeFor(height - placement.MapTileOrigin.Y, baseAltitude);
        }

        /// <summary>
        /// The one conversion from a world height above the surround's plane into a blueprint
        /// altitude, rounding to the nearest unit and saturating rather than wrapping. Named apart
        /// from <see cref="BaseAltitudeFor(NeighbourhoodMap,NeighbourhoodLot)"/> because the two
        /// answer different questions: this is "an altitude for this height", that is "this lot's
        /// base level".
        /// </summary>
        private static short AltitudeFor(float relativeHeight, int baseAltitude)
        {
            var units = baseAltitude + (int)Math.Round(
                relativeHeight / SimitoneNeighbourTerrain.LotTerrainFactor,
                MidpointRounding.AwayFromZero);
            if (units > short.MaxValue) return short.MaxValue;
            if (units < short.MinValue) return short.MinValue;
            return (short)units;
        }

        /// <summary>
        /// The whole of a sub-world's ground, in the surround's own heights and in the array order
        /// the lot view draws it in - the single source of the merged surface.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the merge, and it is done here.</b> The drawn region has one ground. The
        /// surround draws it from the generated map's per-tile elevation raster
        /// (<see cref="NeighbourhoodCityMeshBuilder"/>'s quads, whose corner <c>(x, z)</c> carries
        /// <c>elevation[z][x]</c>), and every real neighbouring lot used to draw a second,
        /// <em>different</em> surface on top of it: <c>VMLotTerrainRestoreTools.RestoreHeight</c>'s
        /// smoothstep bicubic of a 4x4 grid of <c>round(elevation / 22.5)</c> samples taken at the
        /// lot's own corners, with up to a fifth of a tile of
        /// <c>TerrainXNoise</c>/<c>TerrainYNoise</c> perturbing the interpolation fraction. The two
        /// agreed on a plane - <see cref="GroundOriginFor"/> and
        /// <see cref="SimitoneSurroundingsRestore.BaseAltitudeFor"/> are both built so that they do -
        /// and on a rectangle - <see cref="SimitoneSurroundingsPlan.SubWorldPositionFor"/> is the same
        /// delta the surround's own <see cref="ToLotView"/> applies - and on nothing else. Measured
        /// over a 64x64 lot on a five-unit slope, that is a mean step of two world units and a
        /// worst step of five, a whole tile and two thirds of vertical displacement across a surface
        /// that is supposed to be one surface, and on a <em>flat</em> map it is still a third of a
        /// unit from the quantisation and the noise alone.
        /// </para>
        /// <para>
        /// So the sub-world is given the surround's own heights rather than a second reading of the
        /// same map. The result is not "close": both surfaces are quads over the same two-by-two
        /// lattice of corner heights in the same order, so they become the same triangles. The
        /// sub-world's grass, its own <c>RestoreRoad</c> pavement and its furniture stay exactly
        /// where they were - they are drawn against this field - and only the height field changes.
        /// </para>
        /// <para>
        /// The array is indexed <c>z * size + x</c> in the lot view's <em>drawn corner</em> order -
        /// the order <c>TerrainComponent.RegenTerrain</c> reads <c>GroundHeight</c> in and the order
        /// <c>VMArchitectureTerrain.RegenerateCenters</c> walks - so it can be copied over
        /// <c>Terrain.Heights</c> with no index arithmetic at all. Corner <c>(x, z)</c> is map tile
        /// <c>(lot.WorldX + x, lot.WorldY + z)</c>, which is where the surround puts the corner at
        /// the same world position, because the sub-world's own tile <c>(0, 0)</c> lands at the
        /// parent's tile <c>GlobalPosition</c> and <c>GlobalPosition</c> is
        /// <see cref="SimitoneSurroundingsPlan.SubWorldPositionFor"/>.
        /// </para>
        /// <para>
        /// A null map or a size that is not a rectangle gives a null field, which the bridge reads as
        /// "keep whatever the helpers made" rather than as a lot with no ground.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="placement">The active lot's placement.</param>
        /// <param name="lot">The neighbouring lot, for the rectangle the field covers.</param>
        /// <param name="subWorldSize">
        /// The side the sub-world's blueprint is built at -
        /// <see cref="SimitoneSurroundingsPlan.SubWorldSizeFor"/>, not necessarily
        /// <see cref="NeighbourhoodLot.Size"/>.
        /// </param>
        /// <param name="baseAltitude">The sub-world's <c>Blueprint.BaseAlt</c>.</param>
        public static short[] SubWorldAltitudesFor(
            NeighbourhoodMap map, SimitoneSurroundingsPlacement placement, NeighbourhoodLot lot,
            int subWorldSize, int baseAltitude)
        {
            return SubWorldAltitudesFor(map, placement, lot, subWorldSize, baseAltitude,
                SimitoneArryAltitude.None);
        }

        /// <summary>
        /// <see cref="SubWorldAltitudesFor(NeighbourhoodMap,SimitoneSurroundingsPlacement,NeighbourhoodLot,int,int)"/>
        /// with the whole field read off the lot's own <c>ARRY(0)</c> plane, so a neighbouring lot's
        /// ground is graded like the player's instead of pinned flat at its median.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the field <c>SimitoneSurroundingsRestore.ApplySurroundGround</c> copies over the
        /// sub-world's own <c>Terrain.Heights</c>, so it <em>is</em> the neighbour's drawn ground,
        /// and it used to be the generated map's flat pin over the lot's whole rectangle - the
        /// player's own lot, drawn by the lot view from its own <c>ARRY(0)</c>, was graded while
        /// every lot around it was a plane. Passing the plane here is what makes the two the same
        /// shape.
        /// </para>
        /// <para>
        /// <b>With no plane the merge is unchanged</b>, and that is deliberate rather than a
        /// fallback: the merge is what makes the sub-world's ground and the surround's ground the
        /// same triangles, and a field read from somewhere else is only the same surface where the
        /// surround's own ground is that field. See
        /// <see cref="SubWorldAltitudeFor(NeighbourhoodMap,SimitoneSurroundingsPlacement,int,int,int,NeighbourhoodLot,SimitoneArryAltitude)"/>
        /// for the join at the property line, and <see cref="SimitoneArryAltitude"/> for who has to
        /// read the plane.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="placement">The active lot's placement.</param>
        /// <param name="lot">The neighbouring lot, for the rectangle the field covers.</param>
        /// <param name="subWorldSize">
        /// The side the sub-world's blueprint is built at -
        /// <see cref="SimitoneSurroundingsPlan.SubWorldSizeFor"/>, not necessarily
        /// <see cref="NeighbourhoodLot.Size"/>.
        /// </param>
        /// <param name="baseAltitude">The sub-world's <c>Blueprint.BaseAlt</c>.</param>
        /// <param name="arry">
        /// The lot's own <c>ARRY(0)</c> plane, indexed by <em>lot-local</em> tile, or
        /// <see cref="SimitoneArryAltitude.None"/> to read the map.
        /// </param>
        public static short[] SubWorldAltitudesFor(
            NeighbourhoodMap map, SimitoneSurroundingsPlacement placement, NeighbourhoodLot lot,
            int subWorldSize, int baseAltitude, SimitoneArryAltitude arry)
        {
            if (map == null || lot == null || subWorldSize <= 0) return null;
            var field = new short[subWorldSize * subWorldSize];
            for (int z = 0; z < subWorldSize; z++)
            {
                var mapZ = lot.WorldY + z;
                for (int x = 0; x < subWorldSize; x++)
                {
                    field[z * subWorldSize + x] = SubWorldAltitudeFor(
                        map, placement, baseAltitude, lot.WorldX + x, mapZ, lot, arry);
                }
            }
            return field;
        }

        /// <summary>
        /// The elevation byte at a map tile, or zero outside the map - the same refusal the rest of
        /// this file makes, so a lot on the edge of the world is measured against the edge rather
        /// than against a wrap or a throw. Public because the surround's ground question
        /// (<c>GroundIsTheSurroundsGround</c>, now gone) was asked the way the mesh answers it
        /// rather than by a second reading of the map.
        /// </summary>
        public static byte ElevationByteAt(NeighbourhoodMap map, int mapX, int mapZ)
        {
            if (map == null || map.Width <= 0 || map.Height <= 0) return 0;
            var x = Math.Max(0, Math.Min(map.Width - 1, mapX));
            var z = Math.Max(0, Math.Min(map.Height - 1, mapZ));
            return map.ElevationData[map.Index(x, z)];
        }

        /// <summary>
        /// The placement for a lot number in a packed layout, or <c>false</c> with the reason.
        /// </summary>
        /// <remarks>
        /// A lot the layout does not contain is a refusal rather than a fallback position. It is
        /// the one thing that can make the surround wrong rather than merely absent - a guess at
        /// where an unknown lot went would put the whole neighbourhood somewhere else on the
        /// map - and the reason it says so is the difference between "the setting is off" and
        /// "this lot is not on the generated map" in the log.
        /// </remarks>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lotNumber">The lot the player is standing on.</param>
        /// <param name="placement">The placement, or the default when there is none.</param>
        /// <param name="reason">Why there is no placement, when there is not one.</param>
        public static bool TryFor(
            NeighbourhoodLayout layout, NeighbourhoodMap map, int lotNumber,
            out SimitoneSurroundingsPlacement placement, out string reason)
        {
            placement = default(SimitoneSurroundingsPlacement);
            if (layout == null)
            {
                reason = "there is no generated neighbourhood layout";
                return false;
            }
            if (map == null)
            {
                reason = "there is no generated map";
                return false;
            }
            var lot = layout.FindLot((short)lotNumber);
            if (lot == null)
            {
                reason = "lot " + lotNumber + " is not in this neighbourhood's generated map";
                return false;
            }
            placement = For(map, lot);
            reason = null;
            return true;
        }

        /// <summary>
        /// The lot view's sun vector as the city shaders' light direction: the unit vector
        /// <c>dot(normal, LightVec)</c> wants, which points <em>towards</em> the sun.
        /// </summary>
        /// <remarks>
        /// A zero-length vector means the light batch has not been built for this lot yet, which
        /// is the normal state for the frame or two after a load. Rather than a NaN reaching a
        /// shader it falls back to the same fixed afternoon sun the neighbourhood view uses, so
        /// a lot that is up before its light is briefly lit like the map rather than not at all.
        /// </remarks>
        /// <param name="lotLightVector">The lot's <c>LightVec</c>, or <c>null</c>.</param>
        public static Vector3 LightDirectionFor(Vector3? lotLightVector)
        {
            if (!lotLightVector.HasValue) return Neighbourhood3DView.LightDirection;
            var v = lotLightVector.Value;
            if (v.LengthSquared() < 1e-8f || float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z))
            {
                return Neighbourhood3DView.LightDirection;
            }
            v.Normalize();
            return v;
        }

        /// <summary>
        /// The lot's outdoor colour as the city shaders' light colour: each channel scaled to
        /// 0..1, floored at <see cref="MinLightChannel"/> and lifted by
        /// <see cref="LightStrength"/>.
        /// </summary>
        /// <param name="lotOutsideColor">The lot's <c>OutsideColor</c>, or <c>null</c>.</param>
        public static Vector4 LightColorFor(Color? lotOutsideColor)
        {
            if (!lotOutsideColor.HasValue) return Neighbourhood3DView.LightColor;
            return LightColorFor(lotOutsideColor.Value);
        }

        /// <summary>
        /// The lot's outdoor colour as the city shaders' light colour.
        /// </summary>
        /// <param name="lotOutsideColor">The lot's <c>OutsideColor</c>.</param>
        public static Vector4 LightColorFor(Color lotOutsideColor)
        {
            return new Vector4(
                ChannelFor(lotOutsideColor.R),
                ChannelFor(lotOutsideColor.G),
                ChannelFor(lotOutsideColor.B),
                1f);
        }

        private static float ChannelFor(byte channel)
        {
            var value = channel / 255f;
            if (float.IsNaN(value) || value < MinLightChannel) value = MinLightChannel;
            if (value > 1f) value = 1f;
            return value * LightStrength;
        }

        public override string ToString()
        {
            if (!IsValid) return "no in-lot surroundings placement";
            return "lot " + Lot.Number + " at map tile " + MapTileOrigin
                + ", scaled by " + LotViewUnitsPerTile + ", drawing " + DrawnRegion.Describe()
                + ", neighbours from " + Cull.Describe();
        }
    }

    /// <summary>
    /// The lot view's own street, as arithmetic over a lot rectangle: the three or four tiles of
    /// tarmac, the one tile of nothing and the one tile of pavement that
    /// <c>VMLotTerrainRestoreTools.RestoreRoad</c> stamps along each edge of a lot, measured in
    /// lot-local tiles from that edge, and the corridor the packer has to leave between two lots
    /// for the two bands to make one street.
    ///
    /// <para><b>Why this file owns it.</b> Every real neighbouring lot draws this band itself -
    /// <c>RestoreTerrain</c> calls <c>RestoreRoad</c> on the sub-world's own architecture, with
    /// offsets from that architecture's own edges - so the band is a property of the lot view, not
    /// of the generated map, and it lands at fixed tiles relative to the lot rectangle. The
    /// generated surround used to lay a <em>parallel</em> street instead: the road graph's own band,
    /// <see cref="RoadGraph.MainStreetWidth"/> of tarmac in the gap the packer left between two
    /// footprints. Those are different streets at different tiles, which is the whole of "the
    /// generated sidewalk sits somewhere else, and the neighbour's own pavement appears as a
    /// separate strip": the surround's pavement was one tile outside the lot, and the sub-world's
    /// was five tiles inside it.</para>
    ///
    /// <para><b>The grammar, read off the tilemaps.</b> <c>RestoreRoad</c> stamps
    /// <c>TopLeftRoadTile</c> at arch tile (1, 1) repeated down the height for the west edge - it
    /// is <c>9, 9, 9, 0, 12</c> per row, i.e. three columns of pattern 9 (black tarmac), a column
    /// of pattern 0 (nothing - the lot's own ground) and a column of pattern 12 (pavement).
    /// <c>TopRightRoadTile</c> is stamped at (1, 1) across the width for the north edge and is
    /// <b>four</b> rows of four 9s, then a row of zeros, then a row of twelves - so the north edge
    /// is four tiles of tarmac at 1 to 4, nothing at 5 and pavement at 6, one tile deeper than the
    /// west. The east and south edges are the mirror: <c>BottomRightRoadTile</c> is stamped at
    /// <c>Width - 7</c> and reads <c>12, 0, 9, 9, 9, 11</c>, so from the far edge inward it is
    /// tarmac at offsets 1 to 4 - the fourth being the centre-line tile - nothing at 5 and pavement
    /// at 6. The outermost tile of a lot (offset 0) is never road.</para>
    ///
    /// <para><b>One road byte for both ends of the seam.</b> The bit that says "this lot has a
    /// street along that edge" is <see cref="SimitoneNeighbourTerrain.RoadByteFor"/>, which is what
    /// the sub-world bridge already hands to <c>RestoreTerrain</c>. This class reads the same
    /// function rather than working the edge out again, so the surround's band and a sub-world's
    /// own band cannot be laid on different edges of the same lot - which would put the pavement
    /// back where it was.</para>
    ///
    /// <para><b>The one number the packer reads, and what it is for.</b> A lot's own band reaches
    /// <see cref="HalfStreetDepth"/> tiles into the lot, and the lot view's
    /// <c>RestoreSurroundings</c> packs two lots <c>Size - 2</c> apart - so their rectangles
    /// overlap and the street's centre-line is inside them, which is why
    /// <see cref="LotViewStreetTiles"/> cannot be the packer's pitch: the packer promises that no
    /// two lots share a tile. What the packer reserves instead is
    /// <see cref="StreetCorridorTiles"/>: the corridor a generated street occupies, which is the
    /// smallest gap in which the road graph can lay a full-width street with a footway on each
    /// side and both facing lots can still find it. The surround then draws exactly the tiles
    /// neither sub-world draws - the reserved corridor plus the two outermost rows, which the lot
    /// view's own bands never stamp - so a sub-world's half-street and the surround's band meet at
    /// local tile 1 of each lot with no overlap and no gap between them.</para>
    /// </summary>
    public static class SimitoneLotViewRoad
    {
        /// <summary>
        /// The first tarmac tile, in lot-local tiles from the edge. One, because
        /// <c>StampTilemap</c> starts at arch tile 1 and the lot's own outermost tile is never road.
        /// </summary>
        public const int TarmacFromEdge = 1;

        /// <summary>
        /// Tiles of tarmac along a near edge: the three columns of pattern 9 in
        /// <c>TopLeftRoadTile</c>, which is the west edge.
        /// </summary>
        public const int TarmacTiles = 3;

        /// <summary>
        /// Tiles of tarmac along the north edge: the four rows of four 9s in
        /// <c>TopRightRoadTile</c>. One more than the west edge's three, which is what the tilemaps
        /// say and what the surround's band used to get wrong by a tile on two of the four edges.
        /// </summary>
        public const int NorthTarmacTiles = 4;

        /// <summary>
        /// Tiles of tarmac along a far edge: four, the last of them the centre-line tile which is how
        /// the far side's band meets the near side's at the lot view's own <c>size - 2</c> pitch.
        /// </summary>
        public const int FarTarmacTiles = 4;

        /// <summary>Nothing at all, in lot-local tiles from the west edge. The lot's own ground.</summary>
        public const int GapFromEdge = 4;

        /// <summary>Nothing at all, in lot-local tiles from the north edge.</summary>
        public const int NorthGapFromEdge = 5;

        /// <summary>Nothing at all, in lot-local tiles from a far edge.</summary>
        public const int FarGapFromEdge = 5;

        /// <summary>
        /// The pavement, in lot-local tiles from the west edge: the pattern-12 column, and the only
        /// part of the lot view's street that is not tarmac.
        /// </summary>
        public const int PavementFromEdge = 5;

        /// <summary>The pavement, in lot-local tiles from the north edge.</summary>
        public const int NorthPavementFromEdge = 6;

        /// <summary>The pavement, in lot-local tiles from a far edge.</summary>
        public const int FarPavementFromEdge = 6;

        /// <summary>
        /// How far a lot's own half of a street reaches into the lot: local tile
        /// <see cref="FarPavementFromEdge"/>, which is the deepest thing <c>RestoreRoad</c> stamps on
        /// any edge. A lot's own buildable ground therefore starts at local tile
        /// <c>HalfStreetDepth + 1</c> from each edge that has a street on it.
        /// </summary>
        public const int HalfStreetDepth = FarPavementFromEdge;

        /// <summary>
        /// The width of the street the lot view draws between two lots, in lot tiles: two five-tile
        /// half-streets with the far edge's centre-line tile between them
        /// (<see cref="NeighbourhoodMapAdapter.LotViewStreetCorridorTiles"/>). It is the lot view's
        /// own number, and the packer does <em>not</em> reserve it - see
        /// <see cref="StreetCorridorTiles"/> for why it cannot.
        /// </summary>
        public const int LotViewStreetTiles = NeighbourhoodMapAdapter.LotViewStreetCorridorTiles;

        /// <summary>
        /// Tiles of street corridor the packer reserves between two lot rectangles, and therefore the
        /// corridor the surround's band draws: <see cref="NeighbourhoodMapAdapter.StreetCorridorMapTiles"/>,
        /// six tiles of tarmac and a footway either side.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this number and not the lot view's eleven.</b> Two reasons, and only the first
        /// is a choice.
        /// </para>
        /// <para>
        /// The second is arithmetic. A lot's own band is stamped <em>inside</em> its rectangle -
        /// six tiles deep on each edge that has a street - and TSO's own packing overlaps two lot
        /// rectangles by two tiles so that the two bands meet with nothing between them. The
        /// packer's invariant is the opposite: <see cref="NeighbourhoodLayoutBuilder"/> guarantees
        /// that no two lots share a tile, so the two bands cannot meet inside either lot, and every
        /// tile of the gap between them has to be drawn by the surround. A wider corridor therefore
        /// makes the street wider, not better: <see cref="StreetWidthFor"/> is what a player sees,
        /// twenty-one tiles west to east and twenty-two north to south, against the lot view's
        /// <see cref="LotViewStreetTiles"/>, and the difference is the price of the no-overlap
        /// invariant rather than a choice to make the streets wide.
        /// </para>
        /// <para>
        /// The first is what makes the lot view's own bands draw at all. A lot's road byte says an
        /// edge has a street on it only if the map carries road within
        /// <see cref="SimitoneNeighbourTerrain.RoadProbeDepth"/> tiles of that edge, and the road
        /// graph lays its street one tile in from the low end of the gap with the surplus as
        /// footway. A gap of six or seven leaves that street five or six tiles from the far lot's
        /// border - outside the probe - so half the lots in a packed neighbourhood would draw no
        /// half-street at all and the street would be a wall of tarmac on one side. A gap of nine or
        /// more puts the footway on the low lot's side only. This corridor is the one value that is
        /// both, which is exactly <see cref="NeighbourhoodMapAdapter.StreetCorridorMapTiles"/>.
        /// </para>
        /// </remarks>
        public const int StreetCorridorTiles = NeighbourhoodMapAdapter.StreetCorridorMapTiles;

        /// <summary>
        /// How many tiles of street the surround's band draws between two facing lots: the reserved
        /// corridor plus the two outermost rows or columns, which are the tiles the lot view's own
        /// bands never stamp and which are therefore the only tiles of the street that the surround
        /// is left holding. Bounded by the band so a lot on the edge of the world still gets a
        /// street rather than an unbounded one - see
        /// <see cref="SimitoneLotViewRoadBand.MaxCorridorFillTiles"/>.
        /// </summary>
        public const int CorridorFillTiles = StreetCorridorTiles + 2;

        /// <summary>
        /// The road byte for a lot: the four edge bits and the four corner bits
        /// <c>RestoreRoad</c> reads, from the generated map. The one function both ends of the seam
        /// ask.
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static byte RoadByteFor(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            if (map == null || lot == null) return 0;
            var size = Math.Max(0, lot.Size);
            return SimitoneNeighbourTerrain.RoadByteFor(map,
                lot.WorldX, lot.WorldY, lot.WorldX + size - 1, lot.WorldY + size - 1);
        }

        /// <summary>
        /// The lot-local tile the pavement lands on for one edge, or -1 when that edge has no
        /// street. The single question a test asks about the seam, and the one place the four edges'
        /// differing depths are written down.
        /// </summary>
        /// <param name="roads">A road byte.</param>
        /// <param name="edge">
        /// One of <see cref="SimitoneNeighbourTerrain.RoadWest"/>,
        /// <see cref="SimitoneNeighbourTerrain.RoadNorth"/>,
        /// <see cref="SimitoneNeighbourTerrain.RoadEast"/> or
        /// <see cref="SimitoneNeighbourTerrain.RoadSouth"/>.
        /// </param>
        public static int PavementOffsetFor(byte roads, byte edge)
        {
            if ((roads & edge) == 0) return -1;
            switch (edge)
            {
                case SimitoneNeighbourTerrain.RoadWest:
                    return PavementFromEdge;
                case SimitoneNeighbourTerrain.RoadNorth:
                    return NorthPavementFromEdge;
                default:
                    return FarPavementFromEdge;
            }
        }

        /// <summary>
        /// The lot-local tile the tarmac starts on for one edge, or -1 when that edge has no street.
        /// </summary>
        /// <param name="roads">A road byte.</param>
        /// <param name="edge">One of the four edge bits.</param>
        public static int TarmacOffsetFor(byte roads, byte edge)
        {
            if ((roads & edge) == 0) return -1;
            return TarmacFromEdge;
        }

        /// <summary>
        /// The lot-local tile the "nothing" column lands on for one edge, or -1 when that edge has no
        /// street. The lot's own ground, between its tarmac and its pavement.
        /// </summary>
        /// <param name="roads">A road byte.</param>
        /// <param name="edge">One of the four edge bits.</param>
        public static int GapOffsetFor(byte roads, byte edge)
        {
            if ((roads & edge) == 0) return -1;
            switch (edge)
            {
                case SimitoneNeighbourTerrain.RoadWest:
                    return GapFromEdge;
                case SimitoneNeighbourTerrain.RoadNorth:
                    return NorthGapFromEdge;
                default:
                    return FarGapFromEdge;
            }
        }

        /// <summary>
        /// How many tiles of tarmac an edge's band is, near or far. Three a side is the lot view's
        /// own count for the west edge, four for the north and the two far edges, and all of them
        /// are what <see cref="NeighbourhoodMapAdapter.LotViewStreetTarmacTiles"/> already names
        /// across the two lots that share a street.
        /// </summary>
        /// <param name="edge">One of the four edge bits.</param>
        public static int TarmacCountFor(byte edge)
        {
            switch (edge)
            {
                case SimitoneNeighbourTerrain.RoadWest:
                    return TarmacTiles;
                case SimitoneNeighbourTerrain.RoadNorth:
                    return NorthTarmacTiles;
                default:
                    return FarTarmacTiles;
            }
        }

        /// <summary>
        /// The map tiles of street between two lots' rectangles - the corridor the packer reserved -
        /// and the width of the run of tarmac across it once each lot's own band is counted in.
        /// Pure, and the arithmetic the seam tests assert rather than measure off a screenshot.
        /// </summary>
        /// <param name="a">One lot of the street.</param>
        /// <param name="b">The lot facing it, or <c>null</c>.</param>
        public static int CorridorBetween(NeighbourhoodLot a, NeighbourhoodLot b)
        {
            if (a == null || b == null) return 0;
            return Math.Max(0, Math.Max(
                b.WorldX - (a.WorldX + Math.Max(0, a.Size)),
                b.WorldY - (a.WorldY + Math.Max(0, a.Size))));
        }

        /// <summary>
        /// How far a lot's own half of a street reaches into the lot on one edge, in lot-local tiles:
        /// the deepest thing <c>RestoreRoad</c> stamps on it. The west edge is the shallow one -
        /// five tiles, because <c>TopLeftRoadTile</c> is three tarmac columns and a pavement column -
        /// and the other three are six.
        /// </summary>
        /// <param name="edge">One of the four edge bits.</param>
        public static int HalfStreetDepthFor(byte edge)
        {
            switch (edge)
            {
                case SimitoneNeighbourTerrain.RoadWest:
                    return PavementFromEdge;
                default:
                    return FarPavementFromEdge;
            }
        }

        /// <summary>
        /// The width of the street a player sees between two lots the packer placed, in map tiles,
        /// across one pair of facing edges: this lot's own half-street, the tiles no sub-world draws
        /// (<see cref="CorridorFillTiles"/>) and the facing lot's own half-street. The lot view draws
        /// <see cref="LotViewStreetTiles"/> of the same street because its own packing overlaps two
        /// lot rectangles by two tiles; the difference is the price of the no-overlap invariant.
        /// </summary>
        /// <param name="farEdge">The edge of the first lot, which is by definition a far one.</param>
        /// <param name="nearEdge">The facing edge of the second lot, west or north.</param>
        public static int StreetWidthFor(byte farEdge, byte nearEdge)
        {
            return CorridorFillTiles + HalfStreetDepthFor(farEdge) + HalfStreetDepthFor(nearEdge);
        }
    }

    /// <summary>
    /// The street the lot view is going to draw around a set of lots, as a per-tile occupancy of a
    /// map region: tarmac, pavement, or nothing. Pure - it is a function of the generated map and a
    /// list of lot rectangles, with no graphics device, no file and no clock in it, so the alignment
    /// between the surround's ground and a sub-world's own band is a thing a test can write down.
    ///
    /// <para><b>It is <see cref="SimitoneLotViewRoad"/>'s grammar applied to whole lots.</b> Each
    /// lot contributes the band <c>RestoreRoad</c> will stamp on it, at the tiles that method will
    /// stamp them on, and then the corridor between two facing bands is filled with tarmac - because
    /// in the lot view that corridor <em>is</em> tarmac: the two bands are <c>size - 2</c> apart and
    /// abut, so the middle of a street is covered by the far edge's own tiles. The packer leaves
    /// <see cref="SimitoneLotViewRoad.StreetCorridorTiles"/> instead, so the same tiles are the
    /// middle of the street here, and filling them is what makes the generated street one continuous
    /// road rather than two bands with a hole between them.</para>
    ///
    /// <para><b>What the band draws and what the sub-worlds draw, and why nothing is drawn
    /// twice.</b> A lot's own band is stamped <em>inside</em> its rectangle, on local tiles
    /// <see cref="SimitoneLotViewRoad.TarmacFromEdge"/> to
    /// <see cref="SimitoneLotViewRoad.HalfStreetDepth"/>, and a sub-world standing on that lot
    /// stamps the same tiles with the engine's own road templates. The band's copy is what a lot
    /// with no sub-world needs - a facade, a deferred lot, anything past the sub-world radius - and
    /// where a sub-world does stand it is hidden under the sub-world's own ground, so drawing it
    /// costs a few thousand vertices and puts the street on screen for the lots that have none.
    /// The fill is the other half of the same rule: it covers exactly the tiles <em>no</em>
    /// sub-world draws, which is the two outermost rows and the reserved corridor between them -
    /// <see cref="SimitoneLotViewRoad.CorridorFillTiles"/> - and it stops one tile short of
    /// anything the lot view's grammar has already stamped, so the two halves of the street meet at
    /// local tile 1 of each lot with no overlap and no gap.</para>
    ///
    /// <para><b>It replaces the generated map's own road band in the lot view, and only there.</b>
    /// The map's <see cref="NeighbourhoodMap.RoadData"/> is the road graph's grammar and belongs to
    /// the neighbourhood screen, which draws the whole map and is correct; the surround is the one
    /// place where a second, real lot is standing on the same ground and the two bands have to be
    /// the same tiles.</para>
    /// </summary>
    public sealed class SimitoneLotViewRoadBand
    {
        /// <summary>No street on this tile.</summary>
        public const byte None = 0;

        /// <summary>Tarmac: the pattern-9 tiles, and the middle of a street between two bands.</summary>
        public const byte Tarmac = 1;

        /// <summary>Pavement: the one pattern-12 tile each side of every band.</summary>
        public const byte Pavement = 2;

        /// <summary>
        /// How far the fill between two facing bands may run when there is no lot on the far side -
        /// a lot on the edge of the world, or a hand-written layout whose gap is wider than the one
        /// the packer reserves. Two reserved corridors' worth, which bridges a gap twice the one the
        /// packer leaves and still stops a lone lot's street from running to the edge of the world.
        /// </summary>
        /// <remarks>
        /// It used to be <see cref="RoadGraph.MainStreetWidth"/>, which is a tarmac width and not a
        /// distance: a six-tile budget spent from a lot's own outermost tile reaches only halfway
        /// across the eight-tile corridor, so every packed street had tiles of nothing in the middle
        /// of it - a hole, which is the one thing a road must not have, and the shape a player
        /// reports as a "mini street" beside the real one.
        /// </remarks>
        public const int MaxCorridorFillTiles = 2 * SimitoneLotViewRoad.CorridorFillTiles;

        private readonly byte[] _tiles;
        private readonly IReadOnlyList<NeighbourhoodLot> _lots;

        private SimitoneLotViewRoadBand(
            NeighbourhoodMap map, int minX, int minZ, int width, int height,
            IReadOnlyList<NeighbourhoodLot> lots)
        {
            Map = map;
            MinX = minX;
            MinZ = minZ;
            Width = Math.Max(0, width);
            Height = Math.Max(0, height);
            _tiles = new byte[Width * Height];
            _lots = lots ?? new NeighbourhoodLot[0];
        }

        /// <summary>
        /// The generated map the band was measured from, and the one its geometry is drawn from. Null
        /// only for a band built from nothing, which is empty.
        /// </summary>
        public NeighbourhoodMap Map { get; }

        /// <summary>The region's west edge, in map tiles. Inclusive.</summary>
        public int MinX { get; }

        /// <summary>The region's north edge, in map tiles. Inclusive.</summary>
        public int MinZ { get; }

        /// <summary>How wide the region is, in tiles.</summary>
        public int Width { get; }

        /// <summary>How deep the region is, in tiles.</summary>
        public int Height { get; }

        /// <summary>The region's east edge, in map tiles. Exclusive.</summary>
        public int MaxX
        {
            get { return MinX + Width; }
        }

        /// <summary>The region's south edge, in map tiles. Exclusive.</summary>
        public int MaxZ
        {
            get { return MinZ + Height; }
        }

        /// <summary>How many tiles of tarmac the band holds.</summary>
        public int TarmacTileCount { get; private set; }

        /// <summary>How many tiles of pavement the band holds.</summary>
        public int PavementTileCount { get; private set; }

        /// <summary>Every tile of street in the band, tarmac and pavement alike.</summary>
        public int RoadTileCount
        {
            get { return TarmacTileCount + PavementTileCount; }
        }

        /// <summary>Whether this band draws nothing at all.</summary>
        public bool IsEmpty
        {
            get { return RoadTileCount <= 0; }
        }

        /// <summary>
        /// The band for a set of lots over a map region, or an empty band for a map, a region or a
        /// lot list that is not there. The region is clipped to the map, so a band can never name a
        /// tile the ground does not exist for.
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lots">The lots whose frontage is in the region.</param>
        /// <param name="region">The tiles the surround draws, in map tiles.</param>
        public static SimitoneLotViewRoadBand For(
            NeighbourhoodMap map, IReadOnlyList<NeighbourhoodLot> lots, Rectangle region)
        {
            if (map == null || region.Width <= 0 || region.Height <= 0)
            {
                return new SimitoneLotViewRoadBand(null, region.X, region.Y,
                    Math.Max(0, region.Width), Math.Max(0, region.Height), lots);
            }

            var clipped = Rectangle.Intersect(region,
                new Rectangle(0, 0, map.Width, map.Height));
            var band = new SimitoneLotViewRoadBand(map, clipped.X, clipped.Y,
                Math.Max(0, clipped.Width), Math.Max(0, clipped.Height), lots);
            if (clipped.Width <= 0 || clipped.Height <= 0 || lots == null) return band;

            //every lot's own band first, then the fills, so a fill never stamps over the pavement
            //another lot has already put down - the only tile a fill may overwrite is its own lot's
            //outermost row or column, which belongs to the street (see FillEdge)
            for (int i = 0; i < lots.Count; i++) band.AddLot(map, lots[i]);
            for (int i = 0; i < lots.Count; i++) band.FillCorridor(map, lots[i]);
            band.Count();
            return band;
        }

        /// <summary>What the band says about a map tile, one of the three constants above.</summary>
        /// <param name="x">Map tile x.</param>
        /// <param name="z">Map tile z.</param>
        public byte TileAt(int x, int z)
        {
            var index = Index(x, z);
            return index < 0 ? None : _tiles[index];
        }

        /// <summary>Whether a tile is any part of the street.</summary>
        /// <param name="x">Map tile x.</param>
        /// <param name="z">Map tile z.</param>
        public bool IsRoad(int x, int z)
        {
            return TileAt(x, z) != None;
        }

        /// <summary>Whether a tile is tarmac.</summary>
        /// <param name="x">Map tile x.</param>
        /// <param name="z">Map tile z.</param>
        public bool IsTarmac(int x, int z)
        {
            return TileAt(x, z) == Tarmac;
        }

        /// <summary>Whether a tile is pavement.</summary>
        /// <param name="x">Map tile x.</param>
        /// <param name="z">Map tile z.</param>
        public bool IsPavement(int x, int z)
        {
            return TileAt(x, z) == Pavement;
        }

        /// <summary>
        /// The four-bit <see cref="RoadMask"/> the band's own occupancy gives a tile, so a corner
        /// or a T-junction is drawn with the same piece the map's own road pass would have chosen for
        /// it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Tarmac is asked of tarmac, so the one-tile pavement along a street's edge does not make
        /// the road a tile wider than it is - which is what it would do if the two were one question.
        /// Pavement is asked of pavement, because the gap column
        /// (<see cref="SimitoneLotViewRoad.GapFromEdge"/>) sits between the two and a pavement
        /// tile's nearest tarmac is a tile away: asked of tarmac it would answer zero, and the
        /// pavement would be geometry the band holds and never draws. Its own run answers instead,
        /// so a north-south pavement is a straight and a dead end where the street ends.
        /// </para>
        /// </remarks>
        /// <param name="x">Map tile x.</param>
        /// <param name="z">Map tile z.</param>
        public byte MaskAt(int x, int z)
        {
            var tile = TileAt(x, z);
            if (tile == Pavement) return RunMask(x, z, Pavement);
            if (tile != Tarmac) return RoadMask.None;
            var mask = RoadMask.None;
            if (IsTarmac(x - 1, z)) mask |= RoadMask.West;
            if (IsTarmac(x + 1, z)) mask |= RoadMask.East;
            if (IsTarmac(x, z - 1)) mask |= RoadMask.North;
            if (IsTarmac(x, z + 1)) mask |= RoadMask.South;
            return mask;
        }

        private byte RunMask(int x, int z, byte kind)
        {
            var mask = RoadMask.None;
            if (TileAt(x - 1, z) == kind) mask |= RoadMask.West;
            if (TileAt(x + 1, z) == kind) mask |= RoadMask.East;
            if (TileAt(x, z - 1) == kind) mask |= RoadMask.North;
            if (TileAt(x, z + 1) == kind) mask |= RoadMask.South;
            return mask;
        }

        private int Index(int x, int z)
        {
            var lx = x - MinX;
            var lz = z - MinZ;
            if (lx < 0 || lz < 0 || lx >= Width || lz >= Height) return -1;
            return lz * Width + lx;
        }

        private void Mark(int x, int z, int width, int depth, byte value)
        {
            for (int dz = 0; dz < depth; dz++)
            {
                for (int dx = 0; dx < width; dx++)
                {
                    var index = Index(x + dx, z + dz);
                    if (index >= 0) _tiles[index] = value;
                }
            }
        }

        private void AddLot(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            var size = Math.Max(0, lot.Size);
            var roads = SimitoneLotViewRoad.RoadByteFor(map, lot);
            if (roads == 0) return;

            var minX = lot.WorldX;
            var minZ = lot.WorldY;
            var maxX = minX + size - 1;
            var maxZ = minZ + size - 1;

            //Every run is the whole length of the edge it belongs to, which is what makes it a
            //street rather than a tile: RestoreRoad repeats TopLeftRoadTile down the architecture's
            //height and TopRightRoadTile across its width, and BottomRightRoadTile along the other
            //two, so the three tarmac columns and the one pavement column each run the full edge.
            //A one-tile run is a street with a hole in it everywhere else - and every one of those
            //holes is a tile the sub-world standing on this lot is drawing its own tarmac on, which
            //is the same seam as the one a band in the gap was, one hole closer to the lot.
            //
            //<para>The four tarmac runs go down first and the four pavement runs after them, which
            //is the one place this departs from <c>RestoreRoad</c>'s own west-north-east-south stamp
            //order: there, the second band's tarmac writes through the first band's pavement column
            //and leaves a notch in it at the corner. A one-tile pavement strip with a notch in it
            //is not a strip, and the tile it takes away is the one the alignment invariant names -
            //the pavement offset measured in from a near edge, on the lot's own north-west corner
            //tile - so the strips go down last and win the crossings. What that costs is three tiles
            //of tarmac per corner per crossing, in the one place where two streets meet anyway.</para>
            if ((roads & SimitoneNeighbourTerrain.RoadWest) != 0)
            {
                Mark(minX + SimitoneLotViewRoad.TarmacFromEdge, minZ,
                    SimitoneLotViewRoad.TarmacTiles, size, Tarmac);
            }
            if ((roads & SimitoneNeighbourTerrain.RoadNorth) != 0)
            {
                Mark(minX, minZ + SimitoneLotViewRoad.TarmacFromEdge,
                    size, SimitoneLotViewRoad.NorthTarmacTiles, Tarmac);
            }
            if ((roads & SimitoneNeighbourTerrain.RoadEast) != 0)
            {
                Mark(maxX - SimitoneLotViewRoad.FarTarmacTiles, minZ,
                    SimitoneLotViewRoad.FarTarmacTiles, size, Tarmac);
            }
            if ((roads & SimitoneNeighbourTerrain.RoadSouth) != 0)
            {
                Mark(minX, maxZ - SimitoneLotViewRoad.FarTarmacTiles,
                    size, SimitoneLotViewRoad.FarTarmacTiles, Tarmac);
            }
            if ((roads & SimitoneNeighbourTerrain.RoadWest) != 0)
            {
                Mark(minX + SimitoneLotViewRoad.PavementFromEdge, minZ, 1, size, Pavement);
            }
            if ((roads & SimitoneNeighbourTerrain.RoadNorth) != 0)
            {
                Mark(minX, minZ + SimitoneLotViewRoad.NorthPavementFromEdge, size, 1, Pavement);
            }
            if ((roads & SimitoneNeighbourTerrain.RoadEast) != 0)
            {
                Mark(maxX - SimitoneLotViewRoad.FarPavementFromEdge, minZ, 1, size, Pavement);
            }
            if ((roads & SimitoneNeighbourTerrain.RoadSouth) != 0)
            {
                Mark(minX, maxZ - SimitoneLotViewRoad.FarPavementFromEdge, size, 1, Pavement);
            }
        }

        private void FillCorridor(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            var size = Math.Max(0, lot.Size);
            var roads = SimitoneLotViewRoad.RoadByteFor(map, lot);
            if (roads == 0) return;

            var minX = lot.WorldX;
            var minZ = lot.WorldY;
            var maxX = minX + size - 1;
            var maxZ = minZ + size - 1;

            if ((roads & SimitoneNeighbourTerrain.RoadWest) != 0)
                FillEdge(minX, minZ, minX, maxZ, -1, 0, lot);
            if ((roads & SimitoneNeighbourTerrain.RoadNorth) != 0)
                FillEdge(minX, minZ, maxX, minZ, 0, -1, lot);
            if ((roads & SimitoneNeighbourTerrain.RoadEast) != 0)
                FillEdge(maxX, minZ, maxX, maxZ, 1, 0, lot);
            if ((roads & SimitoneNeighbourTerrain.RoadSouth) != 0)
                FillEdge(minX, maxZ, maxX, maxZ, 0, 1, lot);
        }

        /// <summary>
        /// The middle of a street: tarmac outwards from every tile of one lot edge, until something
        /// else owns the next tile - another lot's band, or another lot's footprint, which is where
        /// that lot's own band starts. Two bands that abut are the lot view's own street; two that
        /// do not are a street with a hole in it, and the hole is what the fill is for.
        ///
        /// <remarks>
        /// <para>
        /// It starts at the edge tile itself rather than one tile outside it, because the lot view's
        /// band stops short of that tile: <c>StampTilemap</c> starts at arch tile 1, so the lot's own
        /// outermost tile is lot ground - and in the lot view it is the <em>next</em> lot's tarmac,
        /// which is what puts it there here too. So between two lots the packer placed, the fill
        /// covers exactly <see cref="SimitoneLotViewRoad.CorridorFillTiles"/> tiles: the reserved
        /// corridor plus the two outermost rows, which are the only tiles of the street that no
        /// sub-world draws.
        /// </para>
        /// <para>
        /// The walk stops at the first tile something else has stamped, which is what keeps it from
        /// writing over another lot's pavement, and the cap
        /// (<see cref="MaxCorridorFillTiles"/>) is what keeps a lot with nothing facing it - the
        /// edge of the world, or a hand-written layout - from running a street to the horizon.
        /// </para>
        /// </remarks>
        /// </summary>
        private void FillEdge(
            int x0, int z0, int x1, int z1, int stepX, int stepZ, NeighbourhoodLot source)
        {
            var alongX = Math.Sign(x1 - x0);
            var alongZ = Math.Sign(z1 - z0);
            var z = z0;
            for (var x = x0; ; x += alongX, z += alongZ)
            {
                for (var step = 0; step < MaxCorridorFillTiles; step++)
                {
                    var ox = x + stepX * step;
                    var oz = z + stepZ * step;
                    if (IsRoad(ox, oz)) break;
                    Mark(ox, oz, 1, 1, Tarmac);
                    if (IsInsideAnotherLot(ox, oz, source)) break;
                }
                if (x == x1 && z == z1) break;
            }
        }

        private bool IsInsideAnotherLot(int x, int z, NeighbourhoodLot source)
        {
            for (int i = 0; i < _lots.Count; i++)
            {
                var other = _lots[i];
                if (other == null || ReferenceEquals(other, source)) continue;
                if (x < other.WorldX || x >= other.WorldX + Math.Max(0, other.Size)) continue;
                if (z < other.WorldY || z >= other.WorldY + Math.Max(0, other.Size)) continue;
                return true;
            }
            return false;
        }

        private void Count()
        {
            var tarmac = 0;
            var pavement = 0;
            for (int i = 0; i < _tiles.Length; i++)
            {
                if (_tiles[i] == Tarmac) tarmac++;
                else if (_tiles[i] == Pavement) pavement++;
            }
            TarmacTileCount = tarmac;
            PavementTileCount = pavement;
        }

        public override string ToString()
        {
            return TarmacTileCount + " tarmac and " + PavementTileCount
                + " pavement tile(s) over x[" + MinX + "," + MaxX + ") z[" + MinZ + "," + MaxZ + ")";
        }
    }

    /// <summary>
    /// A rectangle of the shared generated map, and the two questions that follow from it: which
    /// ground quads survive, and which neighbouring lots are close enough to be worth a real lot.
    ///
    /// <para><b>CHANGED: there are two of these now, and the pair is the whole of §8.3.</b>
    /// <see cref="ConnectedMap"/> is the region the lot view draws ground and streets for - the
    /// entire neighbourhood, which is the same raster and the same mesh the neighbourhood view
    /// draws, so the two views cannot disagree about what is between two houses. <see cref="For(NeighbourhoodMap,NeighbourhoodLot,int)"/>
    /// at <see cref="SimitoneSurroundingsPlacement.CullRadiusTiles"/> is the <em>neighbour</em>
    /// region, and it answers the expensive question instead of the cheap one: which lots are close
    /// enough to be stood up as real sub-worlds or built as facade meshes.</para>
    ///
    /// <para><b>Why the drawn region is not a crop any more.</b> The whole world is 512x512 tiles and
    /// <see cref="NeighbourhoodCityMeshBuilder"/> turns it into roughly 276,000 triangles, so
    /// cropping it was a defensible fill-rate decision on its own terms - and it is a crop the
    /// player can see: standing in a lot you saw forty tiles of neighbourhood in every direction and
    /// then fog, where the map you had just been looking at had streets all the way to its edge.
    /// That is §8.1's "not two views", and it is closed by drawing the same map. The fill rate the
    /// crop was protecting is now bought back by chunked submission and a camera frustum, which is
    /// the TSO path and not a rectangle.</para>
    ///
    /// <para><b>Why the lot's own rectangle is <em>not</em> cut out of it.</b> It used to be, and the
    /// reason given was that the lot view draws its own ground after the surround and clears the
    /// depth buffer in between, so the surround's copy of that ground is invisible at best. Which is
    /// true, and is exactly the problem: the generated ground stopped at the property line, so
    /// wherever the neighbourhood's own ground was not at exactly the lot's height the player was
    /// left looking at a cut edge with nothing behind it - a lip round the whole property, or a hole
    /// under it. The surround draws the property too, which puts the neighbourhood's ground
    /// continuously under the lot's own; the <see cref="GroundHeight"/> the region is measured down
    /// to and the bank <see cref="SimitoneSurroundingsCrop"/> cuts along the property line are what
    /// make the two meet instead of standing proud of each other. Note that the property line is
    /// still a question this type answers even though the region is now the whole map: it is where
    /// the two grounds have to be made to meet, and <see cref="IsLotBorderTile"/> and
    /// <see cref="GroundHeight"/> are about the property rather than about the extent of the
    /// drawing.
    /// </para>
    ///
    /// <para><b>All of it is arithmetic.</b> No graphics device, no camera, no file: the numbers a
    /// player would otherwise have to go and look at on screen are the numbers a test can write
    /// down.
    /// </para>
    /// </summary>
    public readonly struct SimitoneSurroundingsCull
    {
        /// <summary>
        /// How much further than the region's own half-diagonal the fog reaches, so that even the
        /// far corner of the drawn square is completely faded before the drawing stops.
        /// </summary>
        /// <remarks>
        /// Without it the cut at the region's edge is visible wherever the fog is not already
        /// finished, which is exactly the "graphical glitches beyond the property" this region
        /// exists to remove.
        /// </remarks>
        public const float FogMargin = 1.05f;

        private readonly int _lotTerrainIndex;

        private SimitoneSurroundingsCull(
            NeighbourhoodMap map,
            int lotMinX, int lotMinZ, int lotMaxX, int lotMaxZ,
            int minX, int minZ, int maxX, int maxZ, int radiusTiles,
            float groundHeight, int lotTerrainIndex)
        {
            Map = map;
            LotMinX = lotMinX;
            LotMinZ = lotMinZ;
            LotMaxX = lotMaxX;
            LotMaxZ = lotMaxZ;
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
            RadiusTiles = radiusTiles;
            GroundHeight = groundHeight;
            _lotTerrainIndex = lotTerrainIndex;
        }

        /// <summary>The active lot's rectangle, west edge, in map tiles.</summary>
        public int LotMinX { get; }

        /// <summary>The active lot's rectangle, north edge, in map tiles.</summary>
        public int LotMinZ { get; }

        /// <summary>The active lot's rectangle, east edge, in map tiles. Exclusive.</summary>
        public int LotMaxX { get; }

        /// <summary>The active lot's rectangle, south edge, in map tiles. Exclusive.</summary>
        public int LotMaxZ { get; }

        /// <summary>
        /// The map this region was measured from, or <c>null</c> for the invalid region. Carried
        /// because it is the only thing the placement's own
        /// <see cref="SimitoneSurroundingsPlacement.ActiveBaseLevel"/> can ask.
        /// </summary>
        public NeighbourhoodMap Map { get; }

        /// <summary>The drawn region's west edge, in map tiles. Inclusive.</summary>
        public int MinX { get; }

        /// <summary>The drawn region's north edge, in map tiles. Inclusive.</summary>
        public int MinZ { get; }

        /// <summary>The drawn region's east edge, in map tiles. Exclusive.</summary>
        public int MaxX { get; }

        /// <summary>The drawn region's south edge, in map tiles. Exclusive.</summary>
        public int MaxZ { get; }

        /// <summary>How many tiles of neighbourhood are kept past the property on every side.</summary>
        public int RadiusTiles { get; }

        /// <summary>
        /// The generated-map height the whole drawn region is measured down to, so that the
        /// player's own ground is at zero: this lot's centre-four mean elevation, from
        /// <see cref="SimitoneSurroundingsPlacement.GroundOriginFor"/>. See there for why that is
        /// the sub-worlds' base level and not the border ring's lowest tile.
        /// </summary>
        /// <remarks>
        /// Held here rather than only in the placement's translation because the crop needs it as
        /// well: the bank it cuts along the property line runs from this height up to the generated
        /// ground, so the two have to be the same number. It is computed once, in
        /// <see cref="For"/>, and the placement reads it back out of here rather than asking again.
        /// </remarks>
        public float GroundHeight { get; }

        /// <summary>
        /// The ground layer the player's own lot is drawn with, as a
        /// <see cref="NeighbourhoodTerrain"/> index, or -1 when this region names no lot.
        /// </summary>
        /// <remarks>
        /// The generated map already stamps every lot's own ground type across that lot's footprint
        /// (see <see cref="TerrainFill"/>), so the ground the player's lot edge meets is the lot's
        /// own terrain rather than whatever the neighbourhood happened to put there. It is recorded
        /// because that is the claim the log makes and a test pins. A region with no lot in it has
        /// none to name, so it answers -1 rather than a terrain that means nothing here.
        /// </remarks>
        public int LotTerrainIndex
        {
            get { return IsValid ? _lotTerrainIndex : -1; }
        }

        /// <summary>Whether there is a region at all.</summary>
        public bool IsValid
        {
            get { return MaxX > MinX && MaxZ > MinZ && LotMaxX > LotMinX && LotMaxZ > LotMinZ; }
        }

        /// <summary>How wide the drawn region is, in tiles.</summary>
        public int WidthTiles
        {
            get { return Math.Max(0, MaxX - MinX); }
        }

        /// <summary>How deep the drawn region is, in tiles.</summary>
        public int HeightTiles
        {
            get { return Math.Max(0, MaxZ - MinZ); }
        }

        /// <summary>How many tiles the region covers before the isometric diamond clips it.</summary>
        public long TileCount
        {
            get { return (long)WidthTiles * HeightTiles; }
        }

        /// <summary>How far the drawn region's corner is from the player's own origin, in units.</summary>
        public float CornerDistanceUnits
        {
            get
            {
                var halfWidth = WidthTiles * 0.5f;
                var halfDepth = HeightTiles * 0.5f;
                return (float)Math.Sqrt(halfWidth * halfWidth + halfDepth * halfDepth)
                    * SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
            }
        }

        /// <summary>
        /// The distance at which the surround's own fog has finished, in lot-view world units: the
        /// far corner of the drawn region, plus <see cref="FogMargin"/>.
        /// </summary>
        public float FogDistance
        {
            get { return CornerDistanceUnits * FogMargin; }
        }

        /// <summary>
        /// Whether a tile centre is inside the drawn region at all.
        /// </summary>
        /// <param name="x">Tile x.</param>
        /// <param name="z">Tile z (the map's second axis, which the mesh writes as y).</param>
        public bool ContainsTile(float x, float z)
        {
            return x >= MinX && x < MaxX && z >= MinZ && z < MaxZ;
        }

        /// <summary>
        /// Whether a tile centre is the player's own property. The surround does draw it - it is
        /// the ground the lot's own is drawn over - but it is asked about separately because the
        /// property's border ring is where the two grounds have to be made to meet.
        /// </summary>
        /// <param name="x">Tile x.</param>
        /// <param name="z">Tile z.</param>
        public bool InsideActiveLot(float x, float z)
        {
            return x >= LotMinX && x < LotMaxX && z >= LotMinZ && z < LotMaxZ;
        }

        /// <summary>
        /// Whether a tile centre is on the outer ring of the player's own property, which is where
        /// the two grounds have to be made to meet.
        /// </summary>
        /// <remarks>
        /// The ring, not the interior, for two reasons: the interior is covered by the lot's own
        /// ground and can never be seen from the surround, and a bank on the inside of the property
        /// line would be standing in the player's own lawn. A one-tile lot is all ring, which is the
        /// honest answer - there is nowhere else for the bank to go.
        /// </remarks>
        /// <param name="x">Tile x.</param>
        /// <param name="z">Tile z.</param>
        public bool IsLotBorderTile(float x, float z)
        {
            if (!InsideActiveLot(x, z)) return false;
            return x < LotMinX + 1f || x >= LotMaxX - 1f
                || z < LotMinZ + 1f || z >= LotMaxZ - 1f;
        }

        /// <summary>
        /// Whether a tile survives the crop: everything inside the drawn region, the player's own
        /// property included.
        /// </summary>
        /// <remarks>
        /// The property used to be cut out here, on the grounds that the lot view draws its own
        /// ground over it anyway. It does - and that is exactly why the cut was wrong: it left the
        /// generated ground stopping dead at the property line, which reads as a step all the way
        /// round the lot wherever the neighbourhood is not at exactly the lot's own height. See the
        /// type's remarks and <see cref="SimitoneSurroundingsCrop"/>.
        /// </remarks>
        /// <param name="x">Tile x.</param>
        /// <param name="z">Tile z.</param>
        public bool KeepsTile(float x, float z)
        {
            return ContainsTile(x, z);
        }

        /// <summary>
        /// Whether a lot is close enough to the player's own to have a facade built and drawn,
        /// and on ground that is drawn. The test is rectangle overlap with the drawn region plus
        /// "the mesh emits ground under the lot's base tile", so a neighbour that shares a
        /// stretch of street with the view is in and one across the map is not.
        /// </summary>
        /// <remarks>
        /// <b>CHANGED: the ground guard is part of this answer, not a separate one.</b>
        /// <see cref="SimitoneSurroundingsPlacement.GroundIsDrawnUnder"/> used to have no caller on
        /// the production path, which is how a lot could be built and drawn on a tile the mesh
        /// leaves empty - a house standing on the island's fade skirt, or past it, in the void
        /// (§9.1). Making it the second half of the one predicate that the facade queue, the render
        /// decision and the sub-world invariant all ask is what stops those three from disagreeing
        /// about it again, and it is deliberately here rather than in the neighbour plan: a lot
        /// outside the cut is refused for the cut's reason, and a lot inside it with no ground is
        /// refused for the ground's.
        /// </remarks>
        /// <param name="lot">The neighbouring lot, or <c>null</c>.</param>
        public bool CoversLot(NeighbourhoodLot lot)
        {
            if (lot == null) return false;
            var size = Math.Max(0, lot.Size);
            if (lot.WorldX >= MaxX || lot.WorldX + size <= MinX) return false;
            if (lot.WorldY >= MaxZ || lot.WorldY + size <= MinZ) return false;
            return SimitoneSurroundingsPlacement.GroundIsDrawnUnder(Map, lot);
        }

        /// <summary>
        /// Whether this region is the whole map - the shape
        /// <see cref="SimitoneSurroundingsPlacement.DrawnRegion"/> has, and the shape that makes
        /// the lot view and the neighbourhood view the same picture.
        /// </summary>
        /// <remarks>
        /// Asked as a fact about the rectangle rather than by remembering which factory built it,
        /// so a region that reached the map's edges by clipping - a lot in a corner, at a radius
        /// wide enough - answers true as well, which is what "the drawing stops here" wants to
        /// know.
        /// </remarks>
        public bool IsConnectedMap
        {
            get
            {
                return IsValid && Map != null
                    && MinX <= 0 && MinZ <= 0
                    && MaxX >= Map.Width && MaxZ >= Map.Height;
            }
        }

        /// <summary>
        /// The whole connected map as a region for a lot: the same raster the neighbourhood view
        /// draws, at every tile of it. §8.3's "the lot view renders the same map".
        /// </summary>
        /// <remarks>
        /// <para>
        /// The radius is the map's own side rather than a lot's distance from anything, so the
        /// region is <c>[0, Width) x [0, Height)</c> for a lot anywhere on the map including its
        /// corners - one answer for every lot, and the same answer the crop, the road band and the
        /// fog all get. A map or a lot that is not there gives the invalid region, which keeps
        /// nothing.
        /// </para>
        /// <para>
        /// The isometric diamond still clips the <em>geometry</em> - the builder emits ground and
        /// roads only strictly inside it - so this rectangle does not make the square corners of the
        /// raster drawable. It makes the region the whole map, which is what the two views have to
        /// agree on; the diamond is a property of the mesh, and
        /// <see cref="SimitoneSurroundingsPlacement.GroundIsDrawnUnder"/> is where a lot is held
        /// to it.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static SimitoneSurroundingsCull ConnectedMap(
            NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            return For(map, lot, ConnectedRadiusFor(map));
        }

        /// <summary>
        /// The radius that reaches every tile of a map from anywhere on it: the map's own side.
        /// </summary>
        /// <remarks>
        /// <see cref="For(NeighbourhoodMap,NeighbourhoodLot,int)"/> clips to the map, so any radius
        /// at least as large as the map's side gives the same whole-map rectangle; naming the
        /// number rather than writing <c>int.MaxValue</c> means the log and the fog floor are
        /// derived from the map rather than from a sentinel.
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        public static int ConnectedRadiusFor(NeighbourhoodMap map)
        {
            if (map == null) return 0;
            return Math.Max(0, Math.Max(map.Width, map.Height));
        }

        /// <summary>
        /// The region for a lot: its own rectangle plus <paramref name="radiusTiles"/> on every
        /// side, clipped to the map. The default when there is no map or no lot is the invalid
        /// region, which keeps nothing - a refusal that draws nothing is better than a guess.
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="radiusTiles">Tiles of neighbourhood to keep past the property.</param>
        public static SimitoneSurroundingsCull For(
            NeighbourhoodMap map, NeighbourhoodLot lot, int radiusTiles)
        {
            if (map == null || lot == null) return default(SimitoneSurroundingsCull);
            if (map.Width <= 0 || map.Height <= 0) return default(SimitoneSurroundingsCull);

            var radius = Math.Max(0, radiusTiles);
            var lotMinX = Math.Max(0, Math.Min(map.Width - 1, lot.WorldX));
            var lotMinZ = Math.Max(0, Math.Min(map.Height - 1, lot.WorldY));
            //the far corner is exclusive but always at least one tile past the near corner, so a
            //zero-sized or out-of-map lot still contributes a region rather than an empty one
            var lotMaxX = Math.Max(lotMinX + 1, Math.Min(map.Width, lot.WorldX + Math.Max(0, lot.Size)));
            var lotMaxZ = Math.Max(lotMinZ + 1, Math.Min(map.Height, lot.WorldY + Math.Max(0, lot.Size)));

            return new SimitoneSurroundingsCull(
                map,
                lotMinX, lotMinZ, lotMaxX, lotMaxZ,
                Math.Max(0, lotMinX - radius), Math.Max(0, lotMinZ - radius),
                Math.Min(map.Width, lotMaxX + radius), Math.Min(map.Height, lotMaxZ + radius),
                radius,
                //one computation of the ground height, shared with the placement's translation and
                //with the bank the crop cuts: three answers to the same question would be three
                //chances for the surround and the lot to disagree about where the ground is
                SimitoneSurroundingsPlacement.GroundOriginFor(map, lot),
                lot == null ? -1 : NeighbourhoodMapAdapter.TerrainIndexFor(lot.TerrainType));
        }

        /// <summary>
        /// The lots of a layout this region would give a facade, in ascending lot number, minus the
        /// lot the player is standing in.
        /// </summary>
        /// <remarks>
        /// The player's own lot is excluded because the lot view is already drawing it, in full, from
        /// the lot's own house file. A facade for it is a whole <c>World</c> and a <c>VM</c> stood
        /// up and torn down to build the house the player can see through the window - and, where
        /// the lot's own ground is translucent, a second copy of the house standing in the middle
        /// of it. <see cref="INeighbourhoodSceneSource.ActiveLot"/> says the same thing about the
        /// neighbourhood view, which is why this is a parameter rather than a second copy of the
        /// rule.
        /// </remarks>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="notThisLot">
        /// The lot number to leave out, or <c>null</c> for none. Passing the player's own number is
        /// the normal call; the overload below keeps the all-lots answer reachable.
        /// </param>
        public IReadOnlyList<NeighbourhoodLot> LotsInRange(
            NeighbourhoodLayout layout, short? notThisLot)
        {
            var lots = new List<NeighbourhoodLot>();
            if (layout == null) return lots;
            for (int i = 0; i < layout.LotCount; i++)
            {
                var lot = layout.Lots[i];
                if (notThisLot.HasValue && lot.Number == notThisLot.Value) continue;
                if (CoversLot(lot)) lots.Add(lot);
            }
            return lots;
        }

        /// <summary>The lots of a layout this region would give a facade, in ascending lot number.</summary>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        public IReadOnlyList<NeighbourhoodLot> LotsInRange(NeighbourhoodLayout layout)
        {
            return LotsInRange(layout, null);
        }

        public override string ToString()
        {
            return Describe();
        }

        /// <summary>
        /// One line naming what is being drawn and where, for the log. It is deliberately explicit
        /// about the rectangle, the property and the margin, because those are the numbers somebody
        /// has to check on screen to believe a screenshot.
        /// </summary>
        public string Describe()
        {
            if (!IsValid) return "nothing is drawn around the lot (there is no map region)";
            return "map tiles x[" + MinX + "," + MaxX + ") z[" + MinZ + "," + MaxZ + ")"
                + " (" + WidthTiles + "x" + HeightTiles
                + (IsConnectedMap
                    ? ", the whole connected map"
                    : ", " + RadiusTiles + " tiles past the property")
                + ", the player's own x[" + LotMinX + "," + LotMaxX + ") z[" + LotMinZ + "," + LotMaxZ + ") is drawn under the lot"
                + ", fog completes at " + FogDistance.ToString("0") + " units";
        }
    }

    /// <summary>
    /// What <see cref="SimitoneSurroundingsCrop.Crop"/> kept out of a built terrain mesh, and what
    /// it threw away: the numbers behind "how much of the neighbourhood is actually on screen".
    /// </summary>
    public sealed class SimitoneSurroundingsCropResult
    {
        /// <summary>One vertex array per ground layer, indexed by <see cref="NeighbourhoodTerrain"/> value.</summary>
        public NeighbourhoodTerrainVertex[][] LayerVertices { get; internal set; }

        /// <summary>One index array per ground layer.</summary>
        public int[][] LayerIndices { get; internal set; }

        /// <summary>Triangles kept per ground layer.</summary>
        public int[] LayerPrimitives { get; internal set; }

        /// <summary>
        /// Per ground layer, the index at which each map chunk's kept quads start in
        /// <see cref="LayerIndices"/>, plus a final entry holding the layer's index count - the
        /// source mesh's own <c>LayerChunkIndexStarts</c> after the crop. <c>null</c> for a layer
        /// whose source mesh carried no usable table, which is drawn whole.
        /// </summary>
        public int[][] LayerChunkStarts { get; internal set; }

        /// <summary>
        /// The same table for the map's own road quads, counted from the end of the band's block:
        /// add <see cref="RoadAlwaysIndices"/> to get a position in <see cref="RoadIndices"/>.
        /// <c>null</c> when the source carried no usable table.
        /// </summary>
        public int[] RoadChunkStarts { get; internal set; }

        /// <summary>
        /// How many road indices at the front of <see cref="RoadIndices"/> belong to the lot-view
        /// band, which is small and always drawn.
        /// </summary>
        public int RoadAlwaysIndices { get; internal set; }

        /// <summary>The road vertices that survived.</summary>
        public NeighbourhoodTerrainVertex[] RoadVertices { get; internal set; }

        /// <summary>The road indices that survived.</summary>
        public int[] RoadIndices { get; internal set; }

        /// <summary>Road triangles that survived.</summary>
        public int RoadPrimitives { get; internal set; }

        /// <summary>
        /// Which grammar laid the street that survived, for the log: the lot view's own
        /// <c>RestoreRoad</c> band, the generated map's own connectors, or both. A surround that
        /// says which one it drew is a surround whose seam can be argued about from a log line.
        /// </summary>
        public string RoadSource { get; internal set; }

        /// <summary>
        /// Triangles the map's own connector quads contributed, across the drawn region - which is
        /// the whole map. The streets and driveways read off the 2D neighbourhood map; zero means
        /// the map was built without a
        /// connector mask and the band is the whole street.
        /// </summary>
        /// <remarks>
        /// Zero here is the number that says the §8.6 failure is back: the space between two lots
        /// drawn as tarmac with no driveways and no inferred streets on it, which is the "green
        /// mini-street" of the bug report in its other form. It is a separate counter from
        /// <see cref="BandRoadPrimitives"/> precisely so the two grammars can be told apart in a log
        /// line rather than summed into one number that is non-zero whichever one survived.
        /// </remarks>
        public int MapRoadPrimitives { get; internal set; }

        /// <summary>
        /// Triangles the lot view's <c>RestoreRoad</c> band contributed. The corridor between two
        /// lots and the half-street each lot's own sub-world stamps underneath.
        /// </summary>
        public int BandRoadPrimitives { get; internal set; }

        /// <summary>
        /// How many map tiles of street the lot view's band holds, or 0 when there was no band.
        /// Tarmac and pavement together, so it is the number the alignment test counts on the other
        /// side.
        /// </summary>
        public int BandRoadTiles { get; internal set; }

        /// <summary>Every triangle the whole-map mesh held before the crop.</summary>
        public int SourcePrimitives { get; internal set; }

        /// <summary>Every triangle the crop kept, across the five layers and the roads.</summary>
        public int KeptPrimitives
        {
            get
            {
                if (LayerPrimitives == null) return RoadPrimitives;
                var total = RoadPrimitives;
                for (int i = 0; i < LayerPrimitives.Length; i++) total += LayerPrimitives[i];
                return total;
            }
        }

        /// <summary>Triangles dropped for being outside the drawn region.</summary>
        public int OutsideRegion { get; internal set; }

        /// <summary>
        /// Triangles that are inside the player's own property. These are <em>kept</em>, not cut -
        /// see <see cref="SimitoneSurroundingsCull.KeepsTile"/> - and the lot's own ground is drawn
        /// over them.
        /// </summary>
        public int UnderActiveLot { get; internal set; }

        /// <summary>
        /// Triangles the crop made itself: the bank along the property line that ties the
        /// neighbourhood's ground to the lot's own. Never part of the source mesh.
        /// </summary>
        public int BankPrimitives { get; internal set; }

        /// <summary>
        /// The triangles that came out of the source mesh, i.e. <see cref="KeptPrimitives"/> less
        /// the bank this crop added.
        /// </summary>
        public int KeptFromSource
        {
            get { return Math.Max(0, KeptPrimitives - BankPrimitives); }
        }

        /// <summary>
        /// Whether anything survived. A whole crop that keeps nothing is not a failure - a lot on
        /// the far edge of the map can have almost no neighbourhood on one side - but it is worth
        /// knowing, and it is the difference between "the surround drew nothing" and "the surround
        /// drew the wrong thing".
        /// </summary>
        public bool IsEmpty
        {
            get { return KeptPrimitives <= 0; }
        }

        public override string ToString()
        {
            return "kept " + KeptPrimitives + " of " + SourcePrimitives + " triangles"
                + " (outside the region " + OutsideRegion
                + ", " + UnderActiveLot + " of them under the lot, plus a " + BankPrimitives
                + "-triangle bank along the property line)";
        }
    }

    /// <summary>
    /// Reduces a whole-map terrain mesh to the square of it a player standing on one lot can
    /// actually see. Pure: it reads vertices and index arrays and produces smaller arrays, with no
    /// graphics device and no <c>GraphicsDevice</c> anywhere in it.
    ///
    /// <para><b>Why the mesh builder is not given a region instead.</b>
    /// <see cref="NeighbourhoodCityMeshBuilder"/> is shared with the neighbourhood view, which
    /// genuinely wants the whole map - it is a camera over a 512-tile world looking at one screen.
    /// Teaching it about a lot view's horizon would put a lot-view decision into the one mesh
    /// builder both views share, and the neighbourhood view would then inherit a cull it has no
    /// reason to have. Cropping the finished mesh keeps the shared builder honest and makes the
    /// cut reversible, which is what makes it testable: the numbers below are exactly the numbers
    /// a full-map build would have submitted, and a test can count them without a GPU.
    /// </para>
    ///
    /// <para><b>What it keeps.</b> In production the region is <see cref="SimitoneSurroundingsCull.ConnectedMap"/>,
    /// the whole map, so the crop keeps every quad (in chunk order) and adds the bank. A
    /// caller that wants a narrower region still gets one - the tests are built on it.</para>
    ///
    /// <para><b>Why it is not a vertex-range draw.</b> A quad's four vertices are re-emitted
    /// rather than referenced, so the kept triangles are contiguous and the buffers hold nothing
    /// that is not drawn. The whole-map mesh is a build artefact that is dropped as soon as the
    /// crop is uploaded; what lives on the GPU afterwards is a few tens of thousands of triangles.
    /// </para>
    ///
    /// <para><b>What the crop adds rather than only removes.</b> The player's own property, and a
    /// bank along its border ring. Both are the answer to "the surrounding lots are rendered in a
    /// very crude, incomplete way... no accurate line-up": the lot view draws its own ground
    /// <em>at its own height</em> and the surround draws the neighbourhood's, so wherever the two
    /// disagree the player is looking at a cut edge with nothing behind it. Drawing the property -
    /// the generated ground the lot's own is drawn over - puts the neighbourhood's ground
    /// continuously under the lot's, and the bank closes the remaining step. Both are pure
    /// arithmetic over the vertices already in the buffer, which is what makes them assertable
    /// without a graphics device.</para>
    ///
    /// <para><b>And the street is both grammars, not one of them.</b> The mesh's own road quads
    /// are the map's real connectors - the streets and driveways read off the 2D neighbourhood
    /// bitmap - and the band is the lot view's own
    /// <c>RestoreRoad</c> seam, the tiles no sub-world draws. Before this they were an either/or, and
    /// the either was the band, which has no driveways: the space between two lots was then a strip
    /// of band with the map's own connectors missing and the grass texture showing through where
    /// they were. Both are now in the same road buffer, band first, so the map's real connectors
    /// are the last thing written to a tile they share.</para>
    /// </summary>
    public static class SimitoneSurroundingsCrop
    {
        /// <summary>
        /// Crops a built mesh to a region, keeping the mesh's own road quads. Never throws for a mesh
        /// that has already been built; a mesh or a region that is not there produces an empty
        /// result. This is the neighbourhood screen's shape of the answer; the surround wants the
        /// three-argument overload below, because it is standing next to real lots.
        /// </summary>
        /// <param name="mesh">The whole-map mesh.</param>
        /// <param name="cull">The region to keep.</param>
        public static SimitoneSurroundingsCropResult Crop(
            NeighbourhoodCityMesh mesh, SimitoneSurroundingsCull cull)
        {
            return Crop(mesh, cull, null);
        }

        /// <summary>
        /// Crops a built mesh to a region, drawing the map's real connectors <em>and</em> the
        /// surround's lot-view band.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two-argument overload keeps the mesh's own road quads, which is right for a caller
        /// cropping the neighbourhood screen's whole-map mesh. The surround wants more than that,
        /// for two separate reasons, and this is where they are combined.
        /// </para>
        /// <para>
        /// <b>The band, because every real lot in the region is standing on the same ground drawing
        /// its own <c>RestoreRoad</c> band.</b> The surround's copy has to be on the tiles they will
        /// draw rather than a second street beside them, and it is the only thing that covers the
        /// reserved corridor - the tiles between two lots that no sub-world stamps, which is where a
        /// hole in the middle of a street would be.
        /// </para>
        /// <para>
        /// <b>The map's own quads, because they are the connectors.</b> They carry the streets the
        /// 2D neighbourhood bitmap drew and the driveways
        /// <see cref="RoadGraph.BuildDriveways"/> routed onto them, and the band has no driveway at
        /// all: it is a function of a lot's four edges and the corridor between them. With only the
        /// band drawn, the space between two non-adjacent lots was grass with a strip of tarmac
        /// across it - the "incorrect mini-street green texture" of the bug report.
        /// </para>
        /// <para>
        /// <b>Order: the band first, so the real connectors win a shared tile.</b> Both are opaque
        /// road quads over the same two-by-two lattice of corner heights - they are built from the
        /// same elevation raster with the same diamond clamps - so they are coplanar, and the depth
        /// test keeps whichever was written last. The map's own cells are the ones that came from
        /// the map, so they are written last.
        /// </para>
        /// <para>
        /// A <c>null</c> or empty band leaves the mesh's own roads alone, so a lot with no layout at
        /// all still gets the neighbourhood it had.
        /// </para>
        /// </remarks>
        /// <param name="mesh">The whole-map mesh.</param>
        /// <param name="cull">The region to keep.</param>
        /// <param name="band">The lot view's street over the region, or <c>null</c>.</param>
        public static SimitoneSurroundingsCropResult Crop(
            NeighbourhoodCityMesh mesh, SimitoneSurroundingsCull cull, SimitoneLotViewRoadBand band)
        {
            var result = new SimitoneSurroundingsCropResult
            {
                LayerVertices = new NeighbourhoodTerrainVertex[NeighbourhoodCityMeshBuilder.LayerCount][],
                LayerIndices = new int[NeighbourhoodCityMeshBuilder.LayerCount][],
                LayerPrimitives = new int[NeighbourhoodCityMeshBuilder.LayerCount],
                LayerChunkStarts = new int[NeighbourhoodCityMeshBuilder.LayerCount][],
                RoadVertices = new NeighbourhoodTerrainVertex[0],
                RoadIndices = new int[0],
                RoadSource = "nothing was drawn"
            };
            if (mesh == null || !cull.IsValid) return result;

            result.SourcePrimitives = mesh.TotalPrimitives;
            for (int i = 0; i < NeighbourhoodCityMeshBuilder.LayerCount; i++)
            {
                //the bank is ground, not road: it is the ground the lot's own meets, and a road
                //along the property line already has its own geometry laid over it
                int[] keptStarts;
                var kept = CropLayer(mesh.Layers[i], cull, result, bank: true,
                    sourceChunkStarts: mesh.LayerChunkIndexStarts?[i], keptChunkStarts: out keptStarts);
                result.LayerVertices[i] = kept.ToArray();
                result.LayerPrimitives[i] = kept.Count / 2;
                result.LayerIndices[i] = BuildIndices(result.LayerPrimitives[i]);
                result.LayerChunkStarts[i] = keptStarts;
            }

            //The lot view's band first and the map's real connectors after, so a tile both grammars
            //claim is written by the one that came from the map. They used to be an either/or - see
            //the remarks on the three-argument overload - and the either was the band, which left
            //every driveway and every inferred street missing with the ground texture showing
            //through where they were.
            var roads = new List<NeighbourhoodTerrainVertex>(band == null ? 256 : 1024);
            var bandRoads = band != null && !band.IsEmpty
                ? BandRoads(band)
                : new List<NeighbourhoodTerrainVertex>(0);
            int[] keptRoadStarts;
            var mapRoads = CropLayer(mesh.Roads, cull, result, bank: false,
                sourceChunkStarts: mesh.RoadChunkIndexStarts, keptChunkStarts: out keptRoadStarts);
            result.MapRoadPrimitives = mapRoads.Count / 2;
            //the band is a small block that is always drawn; the map's own road quads follow it,
            //so the chunk starts of those are shifted past it
            result.RoadAlwaysIndices = bandRoads.Count / 4 * 6;
            result.RoadChunkStarts = keptRoadStarts;
            roads.AddRange(bandRoads);
            roads.AddRange(mapRoads);
            if (band != null && !band.IsEmpty)
            {
                result.RoadSource = "the lot view's own RestoreRoad band ("
                    + band.TarmacTileCount + " tarmac, " + band.PavementTileCount
                    + " pavement) over the generated map's own connectors";
                result.BandRoadTiles = band.RoadTileCount;
                result.BandRoadPrimitives = bandRoads.Count / 2;
            }
            else
            {
                result.RoadSource = "the generated map's own connectors";
            }
            result.RoadVertices = roads.ToArray();
            result.RoadPrimitives = roads.Count / 2;
            result.RoadIndices = BuildIndices(result.RoadPrimitives);
            return result;
        }

        /// <summary>
        /// The band's geometry: one road quad per band tile, and a second for a tile the band's own
        /// occupancy says is a turn or a T-junction, exactly as the map's own road pass decides.
        ///
        /// <remarks>
        /// The four corners, the normals and the cell are
        /// <see cref="NeighbourhoodCityMeshBuilder"/>'s arithmetic read back out of the same map -
        /// the diamond clamp per row, the elevation at each corner, the central-difference normal -
        /// and the emitted quad is the four-vertex, six-index shape every other quad in these buffers
        /// is, so <see cref="BuildIndices"/> lays it out correctly. Only the <em>occupancy</em> is
        /// different, and that is the whole of the fix: the same road, on the same tiles, in the
        /// same atlas, at the place the sub-worlds are going to draw it.
        /// </remarks>
        /// </summary>
        /// <param name="band">The lot view's street over a map region.</param>
        private static List<NeighbourhoodTerrainVertex> BandRoads(SimitoneLotViewRoadBand band)
        {
            var kept = new List<NeighbourhoodTerrainVertex>(256);
            var map = band.Map;
            if (map == null || band.IsEmpty) return kept;
            var elevation = map.ElevationData;

            for (int z = band.MinZ; z < band.MaxZ; z++)
            {
                for (int x = band.MinX; x < band.MaxX; x++)
                {
                    if (!band.IsRoad(x, z)) continue;
                    //a road only exists strictly inside the diamond, the same rule the map's own
                    //road pass uses: the fade skirt is a cosmetic edge and a street over it is broken
                    if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, z)) continue;

                    var rowLeft = NeighbourhoodCityMeshBuilder.DiamondLeft(z);
                    var rowRight = NeighbourhoodCityMeshBuilder.DiamondRight(z);
                    var nextLeft = NeighbourhoodCityMeshBuilder.DiamondLeft(z + 1);
                    var nextRight = NeighbourhoodCityMeshBuilder.DiamondRight(z + 1);
                    var nextZ = Math.Min(z + 1, NeighbourhoodCityMeshBuilder.MapSize - 1);

                    var x0 = ClampToDiamond(rowLeft, rowRight, x);
                    var x1 = ClampToDiamond(rowLeft, rowRight, x + 1);
                    var x2 = ClampToDiamond(nextLeft, nextRight, x + 1);
                    var x3 = ClampToDiamond(nextLeft, nextRight, x);

                    var mask = band.MaskAt(x, z);
                    if (mask == RoadMask.None) continue;

                    var p0 = new Vector3(x, HeightAt(elevation, x0, z), z);
                    var p1 = new Vector3(x + 1, HeightAt(elevation, x1, z), z);
                    var p2 = new Vector3(x + 1, HeightAt(elevation, x2, nextZ), z + 1);
                    var p3 = new Vector3(x, HeightAt(elevation, x3, nextZ), z + 1);
                    var n0 = NeighbourhoodCityMeshBuilder.NormalAt(elevation, x0, z);
                    var n1 = NeighbourhoodCityMeshBuilder.NormalAt(elevation, x1, z);
                    var n2 = NeighbourhoodCityMeshBuilder.NormalAt(elevation, x2, nextZ);
                    var n3 = NeighbourhoodCityMeshBuilder.NormalAt(elevation, x3, nextZ);

                    AddRoadQuad(kept, NeighbourhoodRoadLayout.CellFor(mask, false),
                        p0, n0, p1, n1, p2, n2, p3, n3);
                    if (RoadMask.IsTurnOrJunction(mask))
                    {
                        AddRoadQuad(kept, NeighbourhoodRoadLayout.CellFor(mask, true),
                            p0, n0, p1, n1, p2, n2, p3, n3);
                    }
                }
            }
            return kept;
        }

        private static int ClampToDiamond(int rowLeft, int rowRight, int x)
        {
            return Math.Min(Math.Max(rowLeft, x), rowRight - 1);
        }

        private static float HeightAt(byte[] elevation, int x, int y)
        {
            var cx = x < 0 ? 0 : (x > NeighbourhoodCityMeshBuilder.MapSize - 1
                ? NeighbourhoodCityMeshBuilder.MapSize - 1 : x);
            var cy = y < 0 ? 0 : (y > NeighbourhoodCityMeshBuilder.MapSize - 1
                ? NeighbourhoodCityMeshBuilder.MapSize - 1 : y);
            return NeighbourhoodCityMeshBuilder.WorldHeightFor(elevation[cy * NeighbourhoodCityMeshBuilder.MapSize + cx]);
        }

        /// <summary>
        /// One road quad, in the shape <see cref="BuildIndices"/> assumes: four vertices, and the
        /// same atlas cell convention the mesh builder's own <c>AddRoadQuad</c> uses - the cell's
        /// top-right is the tile's far corner, so u is offset by one and v by none.
        /// </summary>
        private static void AddRoadQuad(
            List<NeighbourhoodTerrainVertex> verts, int cell,
            Vector3 p0, Vector3 n0, Vector3 p1, Vector3 n1,
            Vector3 p2, Vector3 n2, Vector3 p3, Vector3 n3)
        {
            var origin = NeighbourhoodRoadLayout.CellOrigin(cell);
            var delta = NeighbourhoodRoadLayout.CellSize;
            verts.Add(Road(p0, n0, origin.X + delta.X, origin.Y));
            verts.Add(Road(p1, n1, origin.X, origin.Y));
            verts.Add(Road(p2, n2, origin.X, origin.Y + delta.Y));
            verts.Add(Road(p3, n3, origin.X + delta.X, origin.Y + delta.Y));
        }

        private static NeighbourhoodTerrainVertex Road(
            Vector3 position, Vector3 normal, float u, float v)
        {
            return new NeighbourhoodTerrainVertex
            {
                Position = position,
                Normal = normal,
                //zero, i.e. opaque, for the same reason the mesh builder's road pass writes zero:
                //a road only ever exists strictly inside the diamond, and the shader reads this field
                //as one-minus-opacity
                Transparency = 0f,
                TextureCoord = new Vector2(u, v),
                MaskTextureCoord = new Vector2(-1, -1)
            };
        }

        private static List<NeighbourhoodTerrainVertex> CropLayer(
            NeighbourhoodMeshLayer layer, SimitoneSurroundingsCull cull,
            SimitoneSurroundingsCropResult result, bool bank,
            int[] sourceChunkStarts, out int[] keptChunkStarts)
        {
            var kept = new List<NeighbourhoodTerrainVertex>(256);
            keptChunkStarts = null;
            if (layer == null) return kept;
            var vertices = layer.Vertices;
            var indices = layer.Indices;
            if (vertices == null || indices == null) return kept;

            //Where each source chunk's quads start in the kept index run, so the draw can submit
            //only the chunks the camera sees. Only trusted when the source's own table is whole:
            //one entry per chunk plus the sentinel, ending at the layer's last index.
            var tracking = sourceChunkStarts != null
                && sourceChunkStarts.Length == NeighbourhoodCityMeshBuilder.ChunkCount + 1
                && sourceChunkStarts[NeighbourhoodCityMeshBuilder.ChunkCount] == indices.Length;
            if (tracking) keptChunkStarts = new int[NeighbourhoodCityMeshBuilder.ChunkCount + 1];
            var chunk = 0;

            //every quad the builder emits is two triangles over the same four vertices, so the
            //index array advances six at a time and a quad is kept or dropped whole
            for (int i = 0; i + 5 < indices.Length; i += 6)
            {
                if (tracking)
                {
                    //a bank quad is appended straight after its border tile's quad, so it stays in
                    //that tile's chunk and is drawn with it
                    while (chunk < NeighbourhoodCityMeshBuilder.ChunkCount - 1
                        && i >= sourceChunkStarts[chunk + 1])
                    {
                        chunk++;
                        keptChunkStarts[chunk] = kept.Count / 4 * 6;
                    }
                }
                //The four corners are indices i, i+1, i+2 and i+5. Not i+3: the builder's six
                //indices are (v0, v1, v2) and (v0, v2, v3), so the fourth of the run is v0 *again*
                //and the fifth is v2 - taking the fourth corner from there re-emits the quad as
                //(v0, v1, v2) plus a degenerate (v0, v2, v0), and BuildIndices faithfully draws
                //both. Half of every ground quad - and half of every road quad, which is written
                //the same way - is then never rasterised, and the holes show whatever the
                //framebuffer held: on screen, a hard-edged dark-green/black triangle checkerboard
                //over the whole surround. It was only ever visible here, because the
                //neighbourhood screen draws the whole-map mesh and never crops it.
                //
                //Reading i+5 is also what makes the centre below the tile's real centre, which is
                //the number both the keep/drop decision and the bank placement are measured in.
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                var d = vertices[indices[i + 5]];
                var centreX = (a.Position.X + b.Position.X + c.Position.X + d.Position.X) * 0.25f;
                var centreZ = (a.Position.Z + b.Position.Z + c.Position.Z + d.Position.Z) * 0.25f;

                if (!cull.KeepsTile(centreX, centreZ))
                {
                    result.OutsideRegion += 2;
                    continue;
                }
                if (cull.InsideActiveLot(centreX, centreZ)) result.UnderActiveLot += 2;
                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
                kept.Add(d);

                if (!bank) continue;
                if (!cull.IsLotBorderTile(centreX, centreZ)) continue;
                AddBank(kept, cull, a, b, c, d, centreX, centreZ, result);
            }
            if (tracking)
            {
                //chunks after the last quad (and the sentinel) all start where the run ends
                var total = kept.Count / 4 * 6;
                for (int c = chunk + 1; c <= NeighbourhoodCityMeshBuilder.ChunkCount; c++) keptChunkStarts[c] = total;
            }
            return kept;
        }

        /// <summary>
        /// The bank: a vertical strip of the border tile's own ground, from the lot's own ground
        /// plane up to the ground that is already there, on each side of the property line the tile
        /// touches.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without it the join reads as a cliff: the lot's own ground is at zero by construction
        /// (<see cref="SimitoneSurroundingsPlacement.GroundOriginFor"/> measures the whole surround
        /// down to the lowest ground on the property's border ring) and the neighbourhood's ground
        /// around the rest of the property line is above that, so the two never touch. A strip of
        /// the same texture closes the gap and costs two triangles per side of a tile on the ring.
        /// </para>
        /// <para>
        /// The top edge is the tile's <em>mean</em> corner height rather than its highest, so the
        /// bank is never left standing proud of the ground it is holding up by more than half a
        /// quantisation step; and a tile whose ground is already at the lot's own height gets no
        /// bank at all, which is the flat-neighbourhood case and the one that costs nothing. A
        /// corner tile gets two, which meet in an L.
        /// </para>
        /// </remarks>
        /// <param name="kept">The layer's vertex list, appended to.</param>
        /// <param name="cull">The region, for the property line and the lot's own ground height.</param>
        /// <param name="a">The tile's first corner, in index order.</param>
        /// <param name="b">The tile's second corner.</param>
        /// <param name="c">The tile's third corner.</param>
        /// <param name="d">The tile's fourth corner.</param>
        /// <param name="centreX">The tile's centre x.</param>
        /// <param name="centreZ">The tile's centre z.</param>
        /// <param name="result">The counters.</param>
        private static void AddBank(
            List<NeighbourhoodTerrainVertex> kept, SimitoneSurroundingsCull cull,
            NeighbourhoodTerrainVertex a, NeighbourhoodTerrainVertex b,
            NeighbourhoodTerrainVertex c, NeighbourhoodTerrainVertex d,
            float centreX, float centreZ, SimitoneSurroundingsCropResult result)
        {
            var top = (a.Position.Y + b.Position.Y + c.Position.Y + d.Position.Y) * 0.25f;
            var bottom = cull.GroundHeight;
            if (top - bottom <= SimitoneSurroundingsPlacement.SeamEpsilon) return;

            //the tile's own edges, and the property line: the wall goes on the side of the tile
            //that is the boundary, and the face looks away from the property
            var lo = centreX - 0.5f;
            var hi = centreX + 0.5f;
            var near = centreZ - 0.5f;
            var far = centreZ + 0.5f;
            var template = a;

            if (lo <= cull.LotMinX) AddWall(kept, template, top, bottom, lo, near, far, new Vector3(-1f, 0f, 0f), result);
            if (hi >= cull.LotMaxX) AddWall(kept, template, top, bottom, hi, near, far, new Vector3(1f, 0f, 0f), result);
            if (near <= cull.LotMinZ) AddWall(kept, template, top, bottom, near, lo, hi, new Vector3(0f, 0f, -1f), result);
            if (far >= cull.LotMaxZ) AddWall(kept, template, top, bottom, far, lo, hi, new Vector3(0f, 0f, 1f), result);
        }

        /// <summary>
        /// One side of the bank: a quad standing on the line at <paramref name="fixedEdge"/>, running
        /// from <paramref name="runningNear"/> to <paramref name="runningFar"/>, from the lot's own
        /// ground up to <paramref name="top"/>.
        /// </summary>
        /// <param name="kept">The layer's vertex list, appended to.</param>
        /// <param name="template">A source vertex, for the texture coordinates and transparency.</param>
        /// <param name="top">The height the bank reaches.</param>
        /// <param name="bottom">The height it starts at: the lot's own ground.</param>
        /// <param name="fixedEdge">The constant coordinate of the property line.</param>
        /// <param name="runningNear">The near end of the bank along the line.</param>
        /// <param name="runningFar">The far end of the bank along the line.</param>
        /// <param name="outward">Which way the face looks: away from the property.</param>
        /// <param name="result">The counters.</param>
        private static void AddWall(
            List<NeighbourhoodTerrainVertex> kept, NeighbourhoodTerrainVertex template,
            float top, float bottom, float fixedEdge, float runningNear, float runningFar,
            Vector3 outward, SimitoneSurroundingsCropResult result)
        {
            //four vertices and two triangles, in the same four-then-six shape every ground quad in
            //this buffer has - the index builder in BuildIndices assumes it, so a wall emitted any
            //other way would silently draw the wrong triangles
            kept.Add(Wall(template, fixedEdge, runningNear, top, outward));
            kept.Add(Wall(template, fixedEdge, runningNear, bottom, outward));
            kept.Add(Wall(template, fixedEdge, runningFar, bottom, outward));
            kept.Add(Wall(template, fixedEdge, runningFar, top, outward));
            result.BankPrimitives += 2;
        }

        private static NeighbourhoodTerrainVertex Wall(
            NeighbourhoodTerrainVertex template, float fixedEdge, float running, float height, Vector3 outward)
        {
            var position = Math.Abs(outward.X) > 0f
                ? new Vector3(fixedEdge, height, running)
                : new Vector3(running, height, fixedEdge);
            return new NeighbourhoodTerrainVertex
            {
                Position = position,
                //the same texel as the ground it hangs from: the texture repeat is four tiles, so a
                //strip that samples the tile it belongs to is a bank, not a smear
                TextureCoord = template.TextureCoord,
                MaskTextureCoord = template.MaskTextureCoord,
                Normal = outward,
                Transparency = template.Transparency
            };
        }

        /// <summary>
        /// The index run for a layer's kept quads: four vertices and two triangles each, in the
        /// order every ground quad in the mesh builder's buffers is in.
        /// </summary>
        /// <param name="primitives">
        /// Triangles, not quads - the same number the draw submits. Two per quad, so the loop is
        /// over half of it: looping over <paramref name="primitives"/> itself writes
        /// <paramref name="primitives"/> quads and indexes twice as far past the end of the vertex
        /// buffer as there is ground in it. Only the first half of what it wrote was ever
        /// submitted, so it drew correctly and read out of bounds if anything ever submitted the
        /// whole buffer.
        /// </param>
        private static int[] BuildIndices(int primitives)
        {
            var quads = Math.Max(0, primitives / 2);
            var indices = new int[quads * 6];
            for (int i = 0; i < quads; i++)
            {
                var first = i * 4;
                var triangle = i * 6;
                indices[triangle] = first;
                indices[triangle + 1] = first + 1;
                indices[triangle + 2] = first + 2;
                indices[triangle + 3] = first;
                indices[triangle + 4] = first + 2;
                indices[triangle + 5] = first + 3;
            }
            return indices;
        }
    }

    /// <summary>
    /// Whether the generated world is drawn around the live lot, and why - the pure decision
    /// behind <c>World.Surroundings</c>, with no graphics device, no <c>World</c> and no file in
    /// it.
    ///
    /// <para>
    /// §7.4's gate, in the same shape as <see cref="NeighbourhoodViewSelection"/> and for the
    /// same reason: <c>TS1GameScreen</c> is a screen with a graphics device, a content provider
    /// and a neighbourhood provider, none of which exist in a test, so "the setting is off so
    /// the lot is byte-for-byte today's" and "generation threw so the lot is byte-for-byte
    /// today's" are only assertions if the decision is somewhere else. That "the setting is off
    /// is byte-for-byte today's" is the acceptance criterion this whole milestone is judged on,
    /// which is why it is a value with a <see cref="Reason"/> and not a branch in the screen.
    /// </para>
    ///
    /// <para>
    /// <b>Two gates, both required.</b> The generated neighbourhood must be switched on - the
    /// same <see cref="NeighbourhoodView"/> the neighbourhood screen uses, which now defaults to
    /// 3D, so a player who has never touched the setting gets the connected world around the lot -
    /// and
    /// FreeSO's own surrounding-lot detail must not be off. That second gate is
    /// <c>GlobalSettings.SurroundingLotMode</c>, whose 0 step is TSO's "no surrounding lots"
    /// (the city terrain reads it the same way, as <c>houseRange = mode == 0 ? 0 : 1</c>), so
    /// turning terrain detail down to Low is a player's existing way of saying not, and this
    /// honours it rather than overriding it.
    /// </para>
    ///
    /// <para>
    /// <b>There is no failure state.</b> Every way this can end is a null
    /// <c>World.Surroundings</c> and the lot exactly as it is, which is the plan's
    /// non-negotiable in §1's goal and §11's guard case: "with the setting off,
    /// <c>World.Surroundings</c> is null and nothing changes".
    /// </para>
    /// </summary>
    public readonly struct SimitoneSurroundingsSelection
    {
        private SimitoneSurroundingsSelection(
            bool enabled, NeighbourhoodView requested, int surroundingLotMode, string reason)
        {
            Enabled = enabled;
            Requested = requested;
            SurroundingLotMode = surroundingLotMode;
            Reason = reason;
        }

        /// <summary>
        /// Whether the generated world is drawn around the lot. When false,
        /// <c>World.Surroundings</c> stays null and the lot is untouched.
        /// </summary>
        public bool Enabled { get; }

        /// <summary>The neighbourhood view setting that was in force.</summary>
        public NeighbourhoodView Requested { get; }

        /// <summary>The surrounding-lot detail that was in force, 0 meaning off.</summary>
        public int SurroundingLotMode { get; }

        /// <summary>Why the generated world is, or is not, drawn around the lot. Never null.</summary>
        public string Reason { get; }

        /// <summary>The generated world is drawn around the lot.</summary>
        /// <param name="requested">The setting that asked for it.</param>
        /// <param name="surroundingLotMode">The surrounding-lot detail.</param>
        /// <param name="reason">What was built, for the log.</param>
        public static SimitoneSurroundingsSelection Enabled3D(
            NeighbourhoodView requested, int surroundingLotMode, string reason)
        {
            return new SimitoneSurroundingsSelection(true, NeighbourhoodViewSetting.Resolve(requested),
                surroundingLotMode, string.IsNullOrEmpty(reason) ? "the generated world is drawn around the lot" : reason);
        }

        /// <summary>The generated world is not drawn around the lot, for this reason.</summary>
        /// <param name="requested">The setting that was in force.</param>
        /// <param name="surroundingLotMode">The surrounding-lot detail.</param>
        /// <param name="reason">Why not.</param>
        public static SimitoneSurroundingsSelection None(
            NeighbourhoodView requested, int surroundingLotMode, string reason)
        {
            return new SimitoneSurroundingsSelection(false, NeighbourhoodViewSetting.Resolve(requested),
                surroundingLotMode, string.IsNullOrEmpty(reason) ? "the generated world is not drawn around the lot" : reason);
        }

        /// <summary>
        /// The selection for a pair of settings, before anything has been generated or placed.
        /// </summary>
        /// <param name="view">The neighbourhood view setting.</param>
        /// <param name="surroundingLotMode">FreeSO's surrounding-lot detail, 0 meaning off.</param>
        public static SimitoneSurroundingsSelection For(NeighbourhoodView view, int surroundingLotMode)
        {
            var resolved = NeighbourhoodViewSetting.Resolve(view);
            if (resolved != NeighbourhoodView.World3D)
            {
                return None(resolved, surroundingLotMode,
                    resolved == NeighbourhoodView.Off
                        ? "the 3D neighbourhood is switched off"
                        : "the 2D neighbourhood map is selected");
            }
            if (surroundingLotMode <= 0)
            {
                return None(resolved, surroundingLotMode,
                    "surrounding lot detail is off, which is the engine's own way of saying not");
            }
            return Enabled3D(resolved, surroundingLotMode,
                "the 3D neighbourhood is switched on and surrounding lots are allowed");
        }

        /// <summary>
        /// The selection once generation has had its say. Not requested, or a failure, or no
        /// map: none of them, with the reason that stopped it.
        /// </summary>
        /// <param name="requested">The selection so far.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="failure">Why generation failed, when it did.</param>
        public static SimitoneSurroundingsSelection AfterGeneration(
            SimitoneSurroundingsSelection requested, NeighbourhoodMap map, string failure)
        {
            if (!requested.Enabled) return requested;
            if (!string.IsNullOrEmpty(failure))
            {
                return None(requested.Requested, requested.SurroundingLotMode,
                    "the generated world could not be built: " + failure);
            }
            if (map == null)
            {
                return None(requested.Requested, requested.SurroundingLotMode,
                    "the generated world could not be built: no map was produced");
            }
            return Enabled3D(requested.Requested, requested.SurroundingLotMode,
                "the generated world was built (" + map.Width + "x" + map.Height + ")");
        }

        /// <summary>
        /// The selection once the lot has been found in the generated map. A lot the layout does
        /// not contain is a refusal: the surround would be placed against a rectangle that is not
        /// there, and a neighbourhood drawn in the wrong place is worse than no neighbourhood.
        /// </summary>
        /// <param name="requested">The selection so far.</param>
        /// <param name="placement">The placement, or the default when there is not one.</param>
        /// <param name="reason">Why there is no placement, when there is not one.</param>
        public static SimitoneSurroundingsSelection AfterPlacement(
            SimitoneSurroundingsSelection requested, SimitoneSurroundingsPlacement placement, string reason)
        {
            if (!requested.Enabled) return requested;
            if (placement.IsValid)
            {
                return Enabled3D(requested.Requested, requested.SurroundingLotMode,
                    "the generated world is placed around lot " + placement.Lot.Number);
            }
            return None(requested.Requested, requested.SurroundingLotMode,
                string.IsNullOrEmpty(reason)
                    ? "the lot could not be found in the generated world"
                    : reason);
        }

        public override string ToString()
        {
            return (Enabled ? "3D around the lot" : "the lot as it is")
                + " (asked for " + NeighbourhoodViewSetting.Describe(Requested)
                + ", surrounding lot detail " + SurroundingLotMode + "): " + Reason;
        }
    }

    /// <summary>
    /// When the generated world may be attached to a lot that is being brought up - now, later, or
    /// never - as a value, decided without a graphics device, a content provider or a thread.
    /// </summary>
    /// <para>
    /// <b>Why this exists.</b> The attach used to happen inside
    /// <c>TS1GameScreen.InitializeLot</c>, and it generated the 512x512 map there: twenty IFF
    /// reads, a relaxation pass and a quarter of a million tiles, on the thread that is bringing up
    /// the lot, before the lot's first frame. That is the twenty seconds a player reports as "stuck
    /// in the final loading stage". The lot view reads <c>World.Surroundings</c> from its very
    /// first frame, so the attach cannot simply be skipped either - the answer is that the lot
    /// comes up first and the neighbourhood fills in, which is the same shape the neighbourhood
    /// screen has used since generation was moved onto a worker.
    /// </para>
    /// <para>
    /// <b>The rule is one line and it is the whole of the fix.</b> If a world is already in memory
    /// for this slot, attaching it costs one small placement and no generation, so it is attached on
    /// this frame. If there is not, the attach waits for a worker - it must never generate one,
    /// because that is the stall this type exists to rule out. And if the settings say no, there is
    /// nothing to wait for.
    /// </para>
    /// </summary>
    public readonly struct SimitoneSurroundingsAttachPlan
    {
        private SimitoneSurroundingsAttachPlan(SimitoneSurroundingsAttachTiming timing, string reason)
        {
            Timing = timing;
            Reason = reason;
        }

        /// <summary>When the surround is attached.</summary>
        public SimitoneSurroundingsAttachTiming Timing { get; }

        /// <summary>Why this timing, in one sentence, for the log. Never null or blank.</summary>
        public string Reason { get; }

        /// <summary>Whether the surround is attached on this frame, with a world already in hand.</summary>
        public bool AttachesNow
        {
            get { return Timing == SimitoneSurroundingsAttachTiming.Now; }
        }

        /// <summary>Whether something has to be generated before the surround can be attached.</summary>
        public bool IsAsynchronous
        {
            get { return Timing == SimitoneSurroundingsAttachTiming.WhenGenerated; }
        }

        /// <summary>
        /// The timing for a lot load, given the settings' answer and whether a world for this
        /// neighbourhood is already in memory.
        /// </summary>
        /// <param name="selection">The settings' answer - see <see cref="SimitoneSurroundingsSelection"/>.</param>
        /// <param name="worldReady">
        /// Whether a world for this lot's neighbourhood is already in memory, so that attaching it
        /// would cost a placement and a mesh and nothing else.
        /// </param>
        public static SimitoneSurroundingsAttachPlan Decide(
            SimitoneSurroundingsSelection selection, bool worldReady)
        {
            if (!selection.Enabled)
            {
                return new SimitoneSurroundingsAttachPlan(
                    SimitoneSurroundingsAttachTiming.Never, selection.Reason);
            }
            if (worldReady)
            {
                return new SimitoneSurroundingsAttachPlan(
                    SimitoneSurroundingsAttachTiming.Now,
                    "the generated world for this neighbourhood is already in memory, so the lot"
                    + " is surrounded on the frame it comes up");
            }
            return new SimitoneSurroundingsAttachPlan(
                SimitoneSurroundingsAttachTiming.WhenGenerated,
                "there is no generated world for this neighbourhood yet, so the lot comes up first"
                + " and the horizon is filled in when a worker has generated one");
        }

        public override string ToString()
        {
            return Timing + ": " + Reason;
        }
    }

    /// <summary>When the generated world is attached to a lot being brought up.</summary>
    public enum SimitoneSurroundingsAttachTiming
    {
        /// <summary>Never: the settings say the lot is played as it is.</summary>
        Never = 0,

        /// <summary>
        /// On the frame the lot comes up, from a world that is already in memory. No generation,
        /// so no stall.
        /// </summary>
        Now = 1,

        /// <summary>
        /// When a worker has generated one, and the game thread has applied it. The lot is up and
        /// playable in the meantime.
        /// </summary>
        WhenGenerated = 2
    }
}
