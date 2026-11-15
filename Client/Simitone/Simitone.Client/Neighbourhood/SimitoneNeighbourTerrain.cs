/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FSO.Content.Model;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// One lot's own <c>ARRY(0)</c> height plane - the graded, per-tile field a TS1 lot's ground
    /// actually is, as opposed to the single number the generated map pins it to.
    ///
    /// <para><b>Why this type exists.</b> <see cref="TerrainSeamSolver"/> stages 2 and 3 pin every
    /// tile of a lot's footprint <em>and</em> its one-tile border ring to that lot's
    /// <c>BaseHeight</c>, which is the <em>median</em> of its <c>ARRY(0)</c> plane. So the
    /// generated map's answer for a lot's own ground is, by construction, flat at the median -
    /// correct as a boundary condition, and the wrong answer to "what shape is this lot's ground",
    /// which is what a surrounding lot is drawn from. The real plane is in the lot's own
    /// <c>HouseNN.iff</c> (as <c>ARRY 0</c>, four bytes per tile with the height in the second) and
    /// in nothing else, so a caller that has read the house file hands it here and every reader of
    /// a lot's terrain stops being told the lot is flat.</para>
    ///
    /// <para><b>Its units are <c>ARRY(0)</c> units, not elevation bytes.</b> A TS1 lot's own
    /// altitude is three sixteenths of a tile of height
    /// (<see cref="NeighbourhoodCityMeshBuilder.TilesPerArryUnit"/>), and the generated map's
    /// elevation byte is a twelfth of a tile; the two are converted at
    /// <see cref="SimitoneNeighbourTerrain.HeightByteFor"/> and
    /// <see cref="NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit"/> respectively, and
    /// neither conversion is written here.</para>
    ///
    /// <para><b>Unavailable is a value, not a null.</b> The default instance reports
    /// <see cref="IsValid"/> false and answers 0 everywhere, which is what a caller that has no
    /// house file - a lot with no house, a layout built from a bitmap, a test - gets, and it is
    /// the reason the median path is still the default rather than a special case.</para>
    /// </summary>
    public readonly struct SimitoneArryAltitude
    {
        private readonly byte[] _plane;
        private readonly int _side;

        /// <summary>
        /// Wraps a decoded height plane. The plane is row-major and <paramref name="side"/> square;
        /// it is not copied, so the caller keeps ownership of it.
        /// </summary>
        /// <param name="plane">
        /// The lot's decoded <c>ARRY(0)</c> plane - one byte per tile, row-major. A retail TS1 house
        /// file's is 64x64, whatever the lot's own <c>SIMI.GlobalData[23]</c> says.
        /// </param>
        /// <param name="side">The plane's side, in tiles. Zero or less, or a plane that is not this
        /// many square, is a plane that is not there.</param>
        public SimitoneArryAltitude(byte[] plane, int side)
        {
            _plane = plane;
            _side = side;
        }

        /// <summary>A plane that is not there. Every read answers 0 and <see cref="IsValid"/> is false.</summary>
        public static SimitoneArryAltitude None
        {
            get { return default(SimitoneArryAltitude); }
        }

        /// <summary>The plane's own side, in tiles, or 0 when there is no plane.</summary>
        public int Side
        {
            get { return _side; }
        }

        /// <summary>Whether a real field is behind this value at all.</summary>
        public bool IsValid
        {
            get
            {
                return _plane != null && _side > 0
                    && (long)_plane.LongLength >= (long)_side * _side;
            }
        }

        /// <summary>
        /// The height at a lot-local tile, clamped to the plane's own bounds - 0 when there is no
        /// plane, and 0 for a lot-local tile outside a plane that is smaller than the lot.
        /// </summary>
        /// <remarks>
        /// Clamped rather than refused, for the same reason every other lot-terrain read clamps: a
        /// loop over a generated neighbourhood must not be able to fail on a lot whose
        /// <c>SIMI</c> claims a size its house file does not carry. A house plane is 64x64 and
        /// <c>NeighbourhoodLayout.MaxLotSize</c> is 64, so retail never reaches the clamp.
        /// </remarks>
        /// <param name="x">Lot-local tile x, zero at the lot's west edge.</param>
        /// <param name="z">Lot-local tile z, zero at the lot's north edge.</param>
        public byte At(int x, int z)
        {
            if (!IsValid) return 0;
            if (x < 0) x = 0;
            if (z < 0) z = 0;
            if (x >= _side) x = _side - 1;
            if (z >= _side) z = _side - 1;
            return _plane[z * _side + x];
        }

        /// <summary>
        /// The lower median of a rectangle of this plane, in <c>ARRY(0)</c> units - the same
        /// statistic, over the same data, as <see cref="NeighbourhoodLayoutBuilder.MedianBaseHeight"/>,
        /// and the level a lot's own ground is measured from once it is anchored on its own field.
        /// </summary>
        /// <param name="size">The rectangle's side, in tiles.</param>
        /// <returns>The median, or 0 when there is no plane or no tile in it.</returns>
        public byte Median(int size)
        {
            if (!IsValid || size <= 0) return 0;
            if (size == 1) return At(0, 0);

            //A copy of the rectangle, sorted, rather than a 256-bucket histogram over the whole
            //plane: a lot is at most NeighbourhoodLayout.MaxLotSize square, so this is at most a
            //few thousand entries, and the plane itself is not touched.
            var side = Math.Min(size, _side);
            var count = side * side;
            var values = new byte[count];
            var at = 0;
            for (var z = 0; z < side; z++)
            {
                for (var x = 0; x < side; x++) values[at++] = At(x, z);
            }
            Array.Sort(values);
            return values[(count - 1) / 2];
        }

        public override string ToString()
        {
            return IsValid
                ? "the lot's own ARRY(0) plane, " + _side + "x" + _side
                : "no ARRY(0) plane (the generated map's flat median is used instead)";
        }
    }

    /// <summary>
    /// One neighbouring lot's terrain, in the shape <c>VMLotTerrainRestoreTools</c> consumes: a
    /// <see cref="TerrainBlend"/>, a road byte, and a 4x4 grid of corner heights.
    ///
    /// <para><b>What this is for.</b> B2 replaces the facade-only surroundings with real lots
    /// (§6, B2 task 2), and the FreeSO helpers that build a lot's terrain out of its surroundings
    /// - <c>RestoreTerrain</c>, <c>RestoreHeight</c>, <c>GetBaseLevel</c>,
    /// <c>PopulateBlankTerrain</c>, <c>EnsureCoreObjects</c> - all take exactly these three things.
    /// <c>VMLotTerrainRestoreTools.RestoreSurroundings</c> gets them out of
    /// <c>vm.TSOState.Terrain</c>, which on a TS1 lot does not exist (Part B §11, landmine 2), and
    /// it fills them from a 3x3 grid keyed on a packed <c>vm.TSOState.LotID</c>, which a generated
    /// layout is not (landmine 1). So this type is the layout-aware replacement for that source:
    /// it asks the generated map about one lot's own rectangle and answers in the helpers' terms.
    /// Nothing here touches a <c>VM</c>, a graphics device, a file or a clock.</para>
    ///
    /// <para><b>The 4x4 grid, and why it is not a 3x3.</b> The helpers read
    /// <c>VMTSOSurroundingTerrain.Height</c> as a 4x4 array of bytes: a 3x3 grid of <em>lot-sized
    /// cells</em> has four corners per axis, and the centre lot's own rectangle is the centre 2x2
    /// of that grid. <c>GetBaseLevel</c> averages exactly those four, and <c>RestoreHeight</c>
    /// bilinearly interpolates the whole 4x4 into the lot's terrain. There is no 3x3 in the
    /// helpers' vocabulary and no room for one, so this type fills all sixteen samples from the
    /// lot's own footprint, subdivided three times: sample <c>i</c> of four along an axis is tile
    /// <c>origin + i * (size - 1) / 3</c>, so every sample lands on a tile of the lot itself and
    /// the grid is a real bilinear patch of the generated ground under the property rather than the
    /// single flat cell TSO's grid gives. Integer arithmetic throughout, so two calls with the same
    /// map answer with the same sixteen bytes.</para>
    ///
    /// <para><b>Where the heights come from, and the arithmetic that is not obvious.</b>
    /// <see cref="NeighbourhoodMap.ElevationData"/> is a byte the mesh builder divides by twelve
    /// (<see cref="NeighbourhoodMapAdapter.ElevationUnitsPerTile"/>), so one of its units is a
    /// twelfth of a tile of height. The helpers' units are nothing like that, and the chain is
    /// worth writing down because it is four hops through three projects:</para>
    /// <list type="number">
    ///   <item><description>
    ///     A TS1 lot's own ground is <c>ARRY(0) * 10</c>: <c>VMTS1Activator</c> assigns
    ///     <c>arch.Terrain.Heights</c> and then <c>Blueprint.Altitude</c> from it (absolute;
    ///     <c>BaseAlt</c> stays zero and the surround is lifted by the median instead, see
    ///     <c>SimitoneSurroundingsPlacement.LotGroundAltitudeFor</c>). So one <c>ARRY(0)</c> unit is
    ///     <see cref="LotAltitudeUnitsPerArryUnit"/> blueprint altitude units.
    ///   </description></item>
    ///   <item><description>
    ///     One altitude unit is <c>Blueprint.TerrainFactor * 3</c> lot-view world units - the lot
    ///     view multiplies by three everywhere - and <c>TerrainFactor</c> is
    ///     <see cref="LotTerrainFactor"/>. One tile is
    ///     <see cref="SimitoneSurroundingsPlacement.LotViewUnitsPerTile"/> world units, so a tile
    ///     of height is <see cref="ArryUnitsPerTile"/> <c>ARRY(0)</c> units.
    ///   </description></item>
    ///   <item><description>
    ///     The helpers multiply every height byte by <see cref="HeightByteScale"/>'s reciprocal:
    ///     <c>GetBaseLevel</c> is <c>(int)((sum of the centre four) / 4 * 100)</c> and
    ///     <c>RestoreHeight</c> writes <c>Heights[i] = (short)(h * 100 - baseLevel)</c>. So
    ///     <b>one byte here is one hundredth of one <c>ARRY(0)</c> unit</b>, and that is the
    ///     whole convention.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Composing the three gives <see cref="HeightBytesPerElevationByte"/>, and
    /// <see cref="HeightByteFor"/> is the one place that conversion is written. The consequence
    /// worth stating plainly: the answer is chosen so that a neighbour standing at the same
    /// generated elevation as the lot the player is in ends up at <em>the same world height as that
    /// lot's own ground</em>, because <c>Blueprint.BaseAlt</c> for the sub-world is computed as
    /// <c>activeBase - neighbourBase</c> and both sides are on this scale. The alternative -
    /// matching the surround <em>mesh</em> instead, which is in whole tiles rather than in
    /// <c>ARRY(0)</c> units - would use eight times the byte per elevation and would leave every
    /// neighbour lot's own walls, floors and mailbox standing about five times too high above its
    /// own ground. The price of the choice is quantisation: one byte is
    /// <see cref="ElevationBytesPerHeightByte"/> elevation bytes, so a lot's height field moves in
    /// steps of about two tiles of map height. Neighbourhoods that are flat - which is nearly all
    /// of them, and all of them below the seam threshold - come out exactly flat, which is the
    /// right answer; a steeply graded one gets a staircase inside the property. See the remarks
    /// on <see cref="BaseLevel"/> for what that does and does not affect.
    /// </para>
    ///
    /// <para><b>The road byte is a different thing from the road mask.</b>
    /// <see cref="NeighbourhoodMap.RoadMaskAt"/> gives the generated map's four-bit
    /// <see cref="RoadGraph"/>-shaped mask (<c>East | South | West | North</c>), which answers
    /// "which directions does this <em>tile</em> carry road in". <c>RestoreRoad</c> wants something
    /// else: a byte naming which of the <em>lot's four edges</em> have a street running along their
    /// outside, in TSO's clockwise-from-the-top-left order, plus a high nibble naming the corners
    /// where two of those edges meet. So the lot's border is probed outward by
    /// <see cref="RoadProbeDepth"/> tiles on each side and the edges that find road become bits.
    /// The depth is a constant because "how far from a lot is the street" is a property of the
    /// packer (one tile of sidewalk, then the street band) and not of any one lot, and a value that
    /// reached further would start finding the street behind the next row of houses.</para>
    ///
    /// <para><b>AdjFlags, and why one bit of it is used.</b> The eight bits of
    /// <c>TerrainBlend.AdjFlags</c> are a fan of "does a different terrain meet me here" around the
    /// lot's border, and <c>ApplyTerrainBlend</c> uses all of them. A generated map is
    /// single-terrain per lot by construction - every tile of a lot's footprint is stamped with that
    /// lot's own ground type - so there is no neighbouring terrain type to blend into and seven of
    /// the eight bits are honestly zero. The eighth, <see cref="AdjFlagMyEdge"/>, means "this
    /// lot's own edge is the odd one out", which is exactly the answer for a lot a street runs past,
    /// and it is what a road on the border sets.</para>
    /// </summary>
    public readonly struct SimitoneNeighbourTerrain
    {
        /// <summary>
        /// Samples per axis in the height grid, matching
        /// <c>VMTSOSurroundingTerrain.Height</c>. Three cells, four corners.
        /// </summary>
        public const int GridSize = 4;

        /// <summary>How many samples the whole grid holds. Sixteen.</summary>
        public const int GridSamples = GridSize * GridSize;

        /// <summary>
        /// <c>FSO.LotView.Model.Blueprint.TerrainFactor</c>, written out: lot-view world units per
        /// blueprint altitude unit is this times three.
        /// </summary>
        /// <remarks>
        /// It lives on a type in <c>FSO.LotView</c> that this file otherwise has no reason to
        /// reference, and it is load-bearing - every height this type produces is scaled by it - so
        /// it is named here where the derivation that uses it can be read. A change to FSO's
        /// default would be a rendering change everywhere and this would then be quietly wrong;
        /// that is the risk the constant's doc-comment is for.
        /// </remarks>
        public const float LotTerrainFactor = 3f / 160f;

        /// <summary>
        /// Blueprint altitude units per <c>ARRY(0)</c> unit - ten, from
        /// <c>VMTS1Activator</c>'s <c>Heights = ARRY(0) * 10</c>.
        /// </summary>
        public const float LotAltitudeUnitsPerArryUnit = 10f;

        /// <summary>
        /// The factor the helpers apply to a height byte, and therefore the size of one byte: the
        /// helpers write <c>byte * 100</c>, so a byte is a hundredth of an <c>ARRY(0)</c> unit.
        /// </summary>
        public const float HeightByteScale = 1f / 100f;

        /// <summary>
        /// Elevation bytes per tile of height, from
        /// <see cref="NeighbourhoodMapAdapter.ElevationUnitsPerTile"/>. The generated map's own
        /// scale, restated because the arithmetic below needs it and because it is the one number
        /// in the pipeline a reader is most likely to have backwards.
        /// </summary>
        public const int ElevationUnitsPerTile = NeighbourhoodMapAdapter.ElevationUnitsPerTile;

        /// <summary>
        /// How many <c>ARRY(0)</c> units of lot altitude one tile of height is: a tile is
        /// <see cref="SimitoneSurroundingsPlacement.LotViewUnitsPerTile"/> world units, an
        /// altitude unit is <c><see cref="LotTerrainFactor"/> * 3</c> of those, and an
        /// <c>ARRY(0)</c> unit is <see cref="LotAltitudeUnitsPerArryUnit"/> altitude units.
        /// </summary>
        public const float ArryUnitsPerTile =
            SimitoneSurroundingsPlacement.LotViewUnitsPerTile
            / (LotTerrainFactor * 3f * LotAltitudeUnitsPerArryUnit);

        /// <summary>
        /// Height-grid bytes per generated-map elevation byte: the composition of the three above.
        /// Two tiles of map height to the byte, which is the quantisation this type's remarks
        /// are about.
        /// </summary>
        public const float HeightBytesPerElevationByte =
            ArryUnitsPerTile * LotAltitudeUnitsPerArryUnit / ElevationUnitsPerTile / 100f;

        /// <summary>
        /// Generated-map elevation bytes per height-grid byte - the same number the other way
        /// round, named because it is the one a reader wants when they are asking "how coarse is
        /// this?".
        /// </summary>
        public const float ElevationBytesPerHeightByte = 1f / HeightBytesPerElevationByte;

        /// <summary>
        /// How far outside a lot's rectangle each edge is probed for a street, in map tiles.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three covers the near half of a street laid in a reserved corridor, from both ends
        /// of it.</b> The corridor is <see cref="NeighbourhoodMapAdapter.StreetCorridorMapTiles"/>
        /// tiles wide - <see cref="RoadGraph.MainStreetWidth"/> of tarmac with a footway either
        /// side - and the tarmac starts one tile in from the low lot, so the low lot finds it at
        /// depths one to three and the facing lot finds it at depths two to seven. Three therefore
        /// reads enough of the street from either lot for <c>RestoreRoad</c> to stamp the half that
        /// is inside that lot, which is the only half it stamps.
        /// </para>
        /// <para>
        /// <b>Not larger.</b> Real lots stand at their <c>LotLocations</c> positions, which is
        /// TSO's own <c>Size - 2</c> pitch, so the next row of plots is over fifty tiles away and a
        /// deeper probe would not reach it either - but it <em>would</em> reach a driveway or a
        /// back path on the far side of a block, and an edge bit set by something that is not a
        /// street gets a half-street stamped along it. Three is the width of the corridor's own
        /// near half, not a guess.
        /// </para>
        /// </remarks>
        public const int RoadProbeDepth = 3;

        /// <summary>
        /// <c>TerrainBlend.AdjFlags</c>'s "my own edge is the odd one out" bit, which
        /// <c>RestoreTerrain</c> reads as a hard blend down that whole edge. Set here where a road
        /// meets the lot.
        /// </summary>
        public const byte AdjFlagMyEdge = 4;

        /// <summary>Road byte bit for a street along the lot's north (low y) edge.</summary>
        public const byte RoadNorth = 4;

        /// <summary>Road byte bit for a street along the lot's east (high x) edge.</summary>
        public const byte RoadEast = 2;

        /// <summary>Road byte bit for a street along the lot's south (high y) edge.</summary>
        public const byte RoadSouth = 1;

        /// <summary>Road byte bit for a street along the lot's west (low x) edge.</summary>
        public const byte RoadWest = 8;

        /// <summary>
        /// Road byte corner bit for the top-left corner: a road north <em>and</em> west.
        /// </summary>
        /// <remarks>
        /// The four corner bits live in the high nibble, which is where <c>RestoreRoad</c> reads
        /// them from (<c>var corners = roads &gt;&gt; 4</c>), so they are 0x10..0x80 rather than
        /// 1..8. Naming them separately from the edge bits is what stops a corner being set by the
        /// same value as the edge it belongs to.
        /// </remarks>
        public const byte RoadCornerTopLeft = 0x80;

        /// <summary>Road byte corner bit for the top-right corner: north and east.</summary>
        public const byte RoadCornerTopRight = 0x40;

        /// <summary>Road byte corner bit for the bottom-right corner: east and south.</summary>
        public const byte RoadCornerBottomRight = 0x20;

        /// <summary>Road byte corner bit for the bottom-left corner: south and west.</summary>
        public const byte RoadCornerBottomLeft = 0x10;

        private readonly byte[] _heights;
        private readonly bool _graded;

        private SimitoneNeighbourTerrain(TerrainBlend blend, byte roads, byte[] heights, bool graded)
        {
            Blend = blend;
            Roads = roads;
            _heights = heights;
            _graded = graded;
        }

        /// <summary>
        /// The lot's ground type, as the two terrain types of a <see cref="TerrainBlend"/>. Both are
        /// the lot's own: a generated map has nothing else for it to blend into.
        /// </summary>
        public TerrainBlend Blend { get; }

        /// <summary>
        /// Which of the lot's edges have a street along them, in the byte
        /// <c>RestoreRoad</c> reads. See the type remarks for the edge order and the corner
        /// nibble.
        /// </summary>
        public byte Roads { get; }

        /// <summary>
        /// Whether the sixteen samples came from the lot's own <c>ARRY(0)</c> plane rather than from
        /// the generated map's flat pin - i.e. whether this lot's ground is graded or a plane.
        /// </summary>
        /// <remarks>
        /// The log line says it, because "the surrounding lots are flat" and "the surrounding lots
        /// are flat because nothing read their house files" are different defects and only one of
        /// them is a bug in this file. A false value is the pre-Part-8 behaviour and is correct
        /// whenever the caller had no plane to offer.
        /// </remarks>
        public bool IsGraded
        {
            get { return _graded; }
        }

        /// <summary>
        /// The sixteen height samples, row-major with <c>y</c> second - the same order
        /// <c>VMTSOSurroundingTerrain.Height[x, y]</c> is serialised in. Prefer
        /// <see cref="HeightAt"/>; this is here so a caller can copy the whole grid into a
        /// one-off <c>VMTSOSurroundingTerrain</c> without walking it.
        /// </summary>
        public IReadOnlyList<byte> Heights
        {
            get { return (IReadOnlyList<byte>)_heights; }
        }

        /// <summary>Whether this holds a grid at all. The default value does not.</summary>
        public bool IsValid
        {
            get { return _heights != null && _heights.Length == GridSamples; }
        }

        /// <summary>
        /// The height sample at a grid position, or 0 for a position off the grid. Off-grid reads
        /// answer 0 rather than throwing because the only caller that can be wrong is a loop over
        /// a generated neighbourhood, and a crash there costs the whole surround rather than one
        /// lot.
        /// </summary>
        /// <param name="x">Grid x, 0..3.</param>
        /// <param name="y">Grid y, 0..3.</param>
        public byte HeightAt(int x, int y)
        {
            if (_heights == null) return 0;
            if (x < 0 || x >= GridSize || y < 0 || y >= GridSize) return 0;
            return _heights[y * GridSize + x];
        }

        /// <summary>
        /// The lot's own ground height in the helpers' units: the mean of the centre four samples
        /// scaled by a hundred, which is <c>VMLotTerrainRestoreTools.GetBaseLevel</c>'s arithmetic
        /// written out over a one-lot grid.
        ///
        /// <para>
        /// This is the number a sub-world's <c>Blueprint.BaseAlt</c> is measured from -
        /// <c>activeBase - neighbourBase</c> - and therefore the number that decides whether a
        /// neighbour lot stands at the right height next to the one the player is in. It is
        /// exposed rather than left inside the bridge so the decision is a pure function a test can
        /// state: <c>GetBaseLevel</c> needs a <c>VM</c> to run against, this does not.
        /// </para>
        /// <para>
        /// The expression is <c>GetBaseLevel</c>'s own, including its truncation and its float
        /// division, because a difference of a truncated hundredth is a difference a millimetre of
        /// ground across four lots.
        /// </para>
        /// </summary>
        public int BaseLevel
        {
            get
            {
                if (!IsValid) return 0;
                var centre = (HeightAt(1, 1) + HeightAt(2, 1) + HeightAt(1, 2) + HeightAt(2, 2));
                return (int)(((centre / 4f) * 100f));
            }
        }

        /// <summary>
        /// One generated-map elevation byte as a height-grid byte, rounded and clamped to 0..255.
        ///
        /// <para>
        /// Rounded rather than truncated because the two ends of it are the difference between a
        /// flat neighbourhood and one that is a whole byte off flat, and clamped because the byte
        /// cannot hold the result for the top of the elevation range and a wrap would put a lot
        /// that is high on the map at the bottom of it.
        /// </para>
        /// </summary>
        /// <param name="elevation">A <see cref="NeighbourhoodMap.ElevationData"/> byte.</param>
        public static byte HeightByteFor(byte elevation)
        {
            var value = (int)Math.Round(elevation * HeightBytesPerElevationByte,
                MidpointRounding.AwayFromZero);
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        /// <summary>
        /// The terrain for one lot, read out of the generated map, or <c>false</c> with the reason.
        ///
        /// <para>
        /// A refusal is a lot the map cannot answer about - no map, no lot, a lot with no
        /// footprint - and never a wrong answer. There is no fallback grid: a neighbour built from
        /// a guessed height field is a neighbour standing in the wrong place, and a caller that
        /// cannot build one should skip that lot (which is what
        /// <see cref="SimitoneSurroundingsPlan"/> does) rather than draw it.
        /// </para>
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="terrain">The terrain, or the default value when there is none.</param>
        /// <param name="reason">Why there is no terrain, when there is not one.</param>
        public static bool TryBuild(
            NeighbourhoodMap map, NeighbourhoodLot lot,
            out SimitoneNeighbourTerrain terrain, out string reason)
        {
            return TryBuild(map, lot, SimitoneArryAltitude.None, out terrain, out reason);
        }

        /// <summary>
        /// The terrain for one lot, from its own <c>ARRY(0)</c> plane where one is available and
        /// from the generated map's flat pin where one is not, or <c>false</c> with the reason.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What the real field changes, and what it does not.</b> The sixteen height samples
        /// become a graded bilinear patch of the lot's own ground rather than sixteen copies of its
        /// median, and <see cref="BaseLevel"/> - the mean of the centre four - moves with them, so
        /// a sub-world is hung from the level the middle of that ground is at rather than from the
        /// level a boundary condition was written at. The road byte, the terrain blend and every
        /// refusal are identical on both paths: those are answers about the lot's <em>edges</em>
        /// and the street beside it, and the map is where both live.
        /// </para>
        /// <para>
        /// <b>The grid is still sampled at the lot's own corners.</b> Sample <c>i</c> of four along
        /// an axis is lot-local tile <c>i * (size - 1) / 3</c>, exactly as the map path does, so
        /// both grids have the same four-by-four meaning and <see cref="BaseLevel"/>'s centre four
        /// are the same four tiles either way. A field and a plane answer the same question about
        /// the same four tiles; only the heights differ.
        /// </para>
        /// <para>
        /// <b>A field that is not a field takes the map path rather than failing.</b>
        /// <see cref="SimitoneArryAltitude.None"/>, a plane smaller than one tile, or a lot with
        /// no footprint: "this caller could not read the house file" is not a reason to refuse a
        /// lot the map describes perfectly well.
        /// </para>
        /// <para>
        /// <b>The caller that has to change.</b> Nothing in the neighbourhood pipeline reads a
        /// house file's <c>ARRY(0)</c> plane, so every production caller is still on the flat path
        /// and the real one is opt-in. The callers are <c>SimitoneSurroundingsPlan</c>, which sizes
        /// each neighbour, and <c>SimitoneSurroundingsRestore</c>, which stands it up; each has to
        /// hand this overload the plane its own lot-content source already read. Until one does,
        /// the generated map's pinned median stands and a surrounding lot is flat - see
        /// <see cref="SimitoneArryAltitude"/> for what that costs.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="arry">
        /// The lot's own decoded <c>ARRY(0)</c> plane, or <see cref="SimitoneArryAltitude.None"/>.
        /// </param>
        /// <param name="terrain">The terrain, or the default value when there is none.</param>
        /// <param name="reason">Why there is no terrain, when there is not one.</param>
        public static bool TryBuild(
            NeighbourhoodMap map, NeighbourhoodLot lot, SimitoneArryAltitude arry,
            out SimitoneNeighbourTerrain terrain, out string reason)
        {
            terrain = default(SimitoneNeighbourTerrain);
            if (map == null)
            {
                reason = "there is no generated map";
                return false;
            }
            if (lot == null)
            {
                reason = "there is no lot";
                return false;
            }
            if (map.Width <= 0 || map.Height <= 0)
            {
                reason = "the generated map has no tiles";
                return false;
            }
            if (lot.Size < NeighbourhoodLayout.MinLotSize)
            {
                reason = "lot " + lot.Number + " is " + lot.Size + " tiles square, so it has no"
                    + " footprint to read a height field from";
                return false;
            }

            var minX = lot.WorldX;
            var minY = lot.WorldY;
            var maxX = lot.WorldX + lot.Size - 1;
            var maxY = lot.WorldY + lot.Size - 1;

            // Sixteen samples over the lot's own rectangle, subdivided three times. Integer
            // division so the two outer samples land on the lot's first and last tile rather than
            // one past them - a corner grid whose far edge is the sidewalk beside the lot would
            // tilt every lot towards its neighbour.
            var graded = arry.IsValid;
            var heights = new byte[GridSamples];
            for (int y = 0; y < GridSize; y++)
            {
                var localY = (y * (lot.Size - 1)) / (GridSize - 1);
                for (int x = 0; x < GridSize; x++)
                {
                    var localX = (x * (lot.Size - 1)) / (GridSize - 1);
                    heights[y * GridSize + x] = graded
                        //the lot's own plane, in ARRY(0) units, through the one conversion this
                        //type owns - which is the same number the map path gets for a flat lot
                        ? HeightByteFor(NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(
                            arry.At(localX, localY)))
                        : HeightByteFor(map.ElevationData[map.Index(
                            Clamp(minX + localX, 0, map.Width - 1),
                            Clamp(minY + localY, 0, map.Height - 1))]);
                }
            }

            var roads = RoadByteFor(map, minX, minY, maxX, maxY);

            // The lot's own ground type, and nothing else: TerrainFill stamps every tile of a
            // lot's footprint with it, so there is no second terrain type for the blend half of
            // the blend to name. Through NeighbourhoodMapAdapter rather than a cast, because
            // TerrainType also holds TS1's own grass variants and PopulateBlankTerrain indexes a
            // five-entry table with the result.
            var type = (TerrainType)NeighbourhoodMapAdapter.TerrainIndexFor(lot.TerrainType);
            var blend = new TerrainBlend
            {
                Base = type,
                Blend = type,
                AdjFlags = roads != 0 ? AdjFlagMyEdge : (byte)0,
                WaterFlags = 0
            };

            terrain = new SimitoneNeighbourTerrain(blend, roads, heights, graded);
            reason = null;
            return true;
        }

        /// <summary>
        /// The road byte for a lot rectangle: the four edge bits and the four corner bits, from
        /// the generated map's own road bytes just outside the property line.
        ///
        /// <para>
        /// Exposed as its own method because it is the only part of the answer that depends on
        /// anything outside the lot, and therefore the only part whose behaviour a test can argue
        /// about by moving a road.
        /// </para>
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="minX">The lot's west tile, inclusive.</param>
        /// <param name="minY">The lot's north tile, inclusive.</param>
        /// <param name="maxX">The lot's east tile, inclusive.</param>
        /// <param name="maxY">The lot's south tile, inclusive.</param>
        public static byte RoadByteFor(
            NeighbourhoodMap map, int minX, int minY, int maxX, int maxY)
        {
            if (map == null) return 0;
            var roads = (byte)0;
            if (EdgeHasRoad(map, minX, minY, maxX, maxY, 0, -1)) roads |= RoadNorth;
            if (EdgeHasRoad(map, minX, minY, maxX, maxY, 1, 0)) roads |= RoadEast;
            if (EdgeHasRoad(map, minX, minY, maxX, maxY, 0, 1)) roads |= RoadSouth;
            if (EdgeHasRoad(map, minX, minY, maxX, maxY, -1, 0)) roads |= RoadWest;

            // A corner where two of the lot's edges both have a street is a corner the road turns
            // at, which is what RestoreRoad's corner tilemaps draw. One edge alone is a straight
            // and gets no corner, which is the same rule RestoreRoad applies to the bits.
            if ((roads & RoadNorth) != 0 && (roads & RoadWest) != 0) roads |= RoadCornerTopLeft;
            if ((roads & RoadNorth) != 0 && (roads & RoadEast) != 0) roads |= RoadCornerTopRight;
            if ((roads & RoadSouth) != 0 && (roads & RoadEast) != 0) roads |= RoadCornerBottomRight;
            if ((roads & RoadSouth) != 0 && (roads & RoadWest) != 0) roads |= RoadCornerBottomLeft;
            return roads;
        }

        private static bool EdgeHasRoad(
            NeighbourhoodMap map, int minX, int minY, int maxX, int maxY,
            int stepX, int stepY)
        {
            // A north or south edge is a row and an east or west edge is a column, and the probe
            // walks the strip one tile at a time out from the property line, running the whole
            // length of the edge including both corners - so a street that meets the lot at its
            // corner counts for the edge as well as the corner.
            var horizontal = stepX == 0;
            var from = horizontal ? minX : minY;
            var to = horizontal ? maxX : maxY;
            for (int depth = 1; depth <= RoadProbeDepth; depth++)
            {
                for (int t = from; t <= to; t++)
                {
                    int x, y;
                    if (horizontal)
                    {
                        x = t;
                        y = stepY < 0 ? minY - depth : maxY + depth;
                    }
                    else
                    {
                        x = stepX < 0 ? minX - depth : maxX + depth;
                        y = t;
                    }
                    if (IsRoadAt(map, x, y)) return true;
                }
            }
            return false;
        }

        private static bool IsRoadAt(NeighbourhoodMap map, int x, int y)
        {
            // Outside the map is not road, and is not a crash either: a lot on the edge of the
            // generated world has streets on three sides and no fourth, which is the honest
            // answer and the one the packer's own layout produces.
            if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) return false;
            return map.RoadMaskAt(map.Index(x, y)) != RoadMask.None;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// One line for a log or a failing assertion: the ground type, the road byte decoded into
        /// words, and the four-by-four grid. The grid is in it because a height field is the one
        /// thing here a player would have to look at a screenshot to check.
        /// </summary>
        public string Describe()
        {
            if (!IsValid) return "no neighbour terrain";
            var sb = new StringBuilder();
            sb.Append("terrain ").Append(Blend.Base)
              .Append(", roads 0x").Append(Roads.ToString("X2", CultureInfo.InvariantCulture))
              .Append(" (").Append(RoadsInWords(Roads)).Append(')')
              .Append(", base level ").Append(BaseLevel)
              .Append(", ").Append(_graded
                  ? "graded from the lot's own ARRY(0) plane"
                  : "flat at the generated map's pinned median");
            for (int y = 0; y < GridSize; y++)
            {
                sb.Append('\n');
                for (int x = 0; x < GridSize; x++)
                {
                    if (x > 0) sb.Append(' ');
                    sb.Append(HeightAt(x, y).ToString(CultureInfo.InvariantCulture).PadLeft(3));
                }
            }
            return sb.ToString();
        }

        /// <summary>The road byte as the edge names it, for a log line.</summary>
        /// <param name="roads">A road byte.</param>
        public static string RoadsInWords(byte roads)
        {
            if (roads == 0) return "no road on any edge";
            var sb = new StringBuilder();
            AppendWord(sb, (roads & RoadNorth) != 0, "north");
            AppendWord(sb, (roads & RoadEast) != 0, "east");
            AppendWord(sb, (roads & RoadSouth) != 0, "south");
            AppendWord(sb, (roads & RoadWest) != 0, "west");
            return sb.ToString();
        }

        private static void AppendWord(StringBuilder sb, bool present, string word)
        {
            if (!present) return;
            if (sb.Length > 0) sb.Append('+');
            sb.Append(word);
        }

        public override string ToString()
        {
            return Describe();
        }
    }
}
