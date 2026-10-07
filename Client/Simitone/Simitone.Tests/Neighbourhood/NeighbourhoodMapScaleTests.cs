/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The horizontal scale contract between a generated map and a live lot: what one map tile is,
    /// what a lot tile is worth in the lot view, and how wide a street ends up in each. Pure
    /// arithmetic over constants - no map, no lot file, no graphics device.
    ///
    /// <para>
    /// This exists because the one scale failure a player can see is also the one nothing can catch
    /// on its own: a surround at the wrong scale still looks like a neighbourhood, just a small or
    /// a large one. So the relationship is pinned here rather than left to a screenshot, and - more
    /// to the point - it is pinned as a <em>measured</em> relationship, with each of the three
    /// numbers traced to the thing in FreeSO or the packer that fixes it.
    /// </para>
    ///
    /// <para>
    /// The two facts that matter, and the reason the conversion is three and not something larger:
    /// a generated map tile is one lot tile (a lot's <c>SIMI.GlobalData[23]</c> is both the lot
    /// view's blueprint size and the number of tiles the packer stamps for it), and a lot tile is
    /// <c>FSO.LotView.WorldState.WorldUnitsPerTile</c> - three - world units. The neighbouring
    /// sub-worlds are placed by the engine at <c>GlobalPosition * 3</c> from a raw map-tile delta,
    /// so the surround mesh and the real lots standing in it are on one scale already.
    /// </para>
    ///
    /// <para>
    /// <b>What was <em>not</em> on that scale was the street, and it now is.</b> The generated
    /// surround used to draw two tarmac tiles where <c>RestoreRoad</c> draws six, so its roads were
    /// a third of the width of the road the player's own lot view already had around it - the "the
    /// roads are a twentieth of the size they should be" report. That was a street-width
    /// disagreement, not a conversion error, and widening the conversion to compensate would put
    /// every neighbouring lot on the wrong plot. It was fixed where the width is chosen,
    /// <see cref="RoadGraph.MainStreetWidth"/>, and the tests below are the evidence: a generated
    /// street is now the same six tarmac tiles, and so the same eighteen world units, as the lot
    /// view's own.
    /// </para>
    /// </summary>
    public class NeighbourhoodMapScaleTests
    {
        private const float Tolerance = 0.0001f;

        // ---- map tile -> lot tile -------------------------------------------------------------

        [Fact]
        public void AGeneratedMapTileIsOneLotTile()
        {
            // The packer's own arithmetic says so, and the log line agrees: a lot's packed footprint
            // is Size + SidewalkMargin, so "the player's own x[92,139) z[114,161)" is a 47-tile
            // lot because 139 - 92 == 47 is that lot's Size, which is its SIMI.GlobalData[23], which
            // VMTS1Activator turns into the Size x Size blueprint the lot view draws.
            Assert.Equal(1f, NeighbourhoodMapAdapter.MapTilesPerLotTile);
            //The margin is the whole street corridor, not a sidewalk: a gap narrower than
            //MainStreetCorridorWidth cannot hold six tiles of tarmac and a footway on each side,
            //which is the arrangement the road graph lays a street into.
            Assert.Equal(RoadGraph.MainStreetCorridorWidth,
                NeighbourhoodLayoutBuilder.SidewalkMargin);
            // 47 is the lot's own size, and the packer's footprint is 47 + 8 = 55 - which is why
            // the log's rectangle is 47 tiles wide and not 55.
            Assert.Equal(55, NeighbourhoodMapAdapter.PackedLotPitchTiles(47));
        }

        [Fact]
        public void AGeneratedMapTileIsThreeLotViewWorldUnits()
        {
            // FSO.LotView.WorldState.WorldUnitsPerTile, and the factor the engine itself applies to
            // a sub-world's tile delta in Cameras.ModelTranslation and in UpdateBounds' box.
            Assert.Equal(3f, NeighbourhoodMapAdapter.LotViewUnitsPerLotTile);
            Assert.Equal(3f, NeighbourhoodMapAdapter.LotViewUnitsPerMapTile);
            Assert.Equal(3f, SimitoneSurroundingsPlacement.LotViewUnitsPerTile);
        }

        [Fact]
        public void TheSurroundsScaleIsTheSeamsScaleAndNotItsOwnCopy()
        {
            // One spelling of the load-bearing number. A second one is how the surround and the real
            // neighbouring lots end up on different scales while both look plausible.
            Assert.Equal(NeighbourhoodMapAdapter.LotViewUnitsPerMapTile,
                SimitoneSurroundingsPlacement.LotViewUnitsPerTile);
        }

        [Fact]
        public void WidthsConvertThroughTheOneHelper()
        {
            Assert.Equal(3f, NeighbourhoodMapAdapter.LotViewWidthFor(1f), 4);
            Assert.Equal(47f * 3f, NeighbourhoodMapAdapter.LotViewWidthFor(47f), 4);
            Assert.Equal(0f, NeighbourhoodMapAdapter.LotViewWidthFor(0f), 4);
        }

        // ---- a lot's own rectangle -------------------------------------------------------------

        [Fact]
        public void TheLotViewDrawsALotInLotViewUnitsOfItsOwnSize()
        {
            // The 47 from the log line. Its own architecture is 47 lot tiles across, the lot view
            // draws that at 47 * WorldUnitsPerTile, and the generated map draws its 47 map tiles at
            // 47 * LotViewUnitsPerMapTile - the same number, because a map tile is a lot tile.
            const int lotSize = 47;
            var lotViewWidth = lotSize * NeighbourhoodMapAdapter.LotViewUnitsPerLotTile;
            var surroundWidth = lotSize * NeighbourhoodMapAdapter.MapTilesPerLotTile
                * NeighbourhoodMapAdapter.LotViewUnitsPerMapTile;
            Assert.Equal(lotViewWidth, surroundWidth, 4);
            Assert.Equal(141f, lotViewWidth, 3);
        }

        // ---- the street ------------------------------------------------------------------------

        [Fact]
        public void AStreetIsTheLotViewsOwnWidthInTheGeneratedMap()
        {
            // Taken from the road graph rather than restated, because the road graph is what marks
            // the tiles and the mesh draws exactly the tiles that are marked. Six, and the same six
            // <c>RestoreRoad</c> draws - see TheLotViewsOwnStreetIsEighteenLotViewUnitsOfTarmac,
            // which is the derivation and not a coincidence.
            Assert.Equal(6, RoadGraph.MainStreetWidth);
            Assert.Equal(1, RoadGraph.SidewalkWidth);
            Assert.Equal(6, NeighbourhoodMapAdapter.StreetTarmacMapTiles);
            Assert.Equal(8, NeighbourhoodMapAdapter.StreetCorridorMapTiles);

            //and the road graph's own name for the ground one street occupies agrees with the
            //adapter's sum, so the two cannot drift into different corridors
            Assert.Equal(NeighbourhoodMapAdapter.StreetCorridorMapTiles,
                RoadGraph.MainStreetCorridorWidth);
        }

        [Fact]
        public void TheMeshDrawsOneRoadQuadPerRoadTile()
        {
            // AddRoadQuad is handed the four corners of a single tile and spends a whole road atlas
            // cell on them, so the drawn width is the marked width with nothing scaled in between.
            Assert.Equal(1, NeighbourhoodCityMeshBuilder.RoadQuadWidthMapTiles);
        }

        [Fact]
        public void AGeneratedStreetIsEighteenLotViewUnitsOfTarmac()
        {
            // Six map tiles at three world units each - the number the surround now draws, and the
            // same eighteen the lot view draws. This was 6f, from a two-tile street, which is a
            // third of the lot view's own road and the whole of the reported bug.
            Assert.Equal(18f, NeighbourhoodMapAdapter.StreetTarmacLotViewUnits, 4);
            Assert.Equal(24f, NeighbourhoodMapAdapter.StreetCorridorLotViewUnits, 4);
        }

        [Fact]
        public void TheLotViewsOwnStreetIsEighteenLotViewUnitsOfTarmac()
        {
            // RestoreRoad stamps each lot's own half of the street: TopRightRoadTile and
            // TopLeftRoadTile are three rows/columns of pattern 9 ("black tarmac") deep, with a
            // pattern 12 pavement beyond, and RestoreSurroundings puts the next lot size - 2 tiles
            // away, so both halves are drawn. Three tiles a side, six in all, at three units a tile.
            Assert.Equal(3, NeighbourhoodMapAdapter.LotViewStreetTarmacTilesPerSide);
            Assert.Equal(6, NeighbourhoodMapAdapter.LotViewStreetTarmacTiles);
            Assert.Equal(18f, NeighbourhoodMapAdapter.LotViewStreetTarmacUnits, 4);
            Assert.Equal(33f, NeighbourhoodMapAdapter.LotViewStreetCorridorUnits, 4);
        }

        [Fact]
        public void TheGeneratedStreetIsTheSameWidthAsTheLotViewsOwn()
        {
            // <b>The fix, stated as one equality.</b> The generated street and the road the lot
            // view draws around the player are the same width in tiles and the same width in world
            // units, because a generated map tile is a lot tile and the tarmac is six of them on
            // both sides. This is what was three, and it is the "the roads are a twentieth of the
            // size they should be" a player reported: two tiles against six.
            Assert.Equal(NeighbourhoodMapAdapter.LotViewStreetTarmacTiles,
                RoadGraph.MainStreetWidth);
            Assert.Equal(NeighbourhoodMapAdapter.LotViewStreetTarmacUnits,
                NeighbourhoodMapAdapter.StreetTarmacLotViewUnits, 4);
            Assert.Equal(18f, NeighbourhoodMapAdapter.StreetTarmacLotViewUnits, 4);
        }

        [Fact]
        public void TheSurroundsStreetIsNoLongerNarrowerThanTheLotViews()
        {
            // The measured factor, by tarmac, and now one: the deficit is gone. The corridor still
            // differs by eleven eighths, because the lot view spends five tiles on its corridor and
            // the generated map spends two - a gap has to be RoadGraph.MainStreetCorridorWidth wide
            // before a footway and a full-width street both fit, and that is the packer's margin.
            Assert.Equal(1f, NeighbourhoodMapAdapter.StreetTarmacDeficit, 4);
            Assert.Equal(1.375f, NeighbourhoodMapAdapter.StreetCorridorDeficit, 4);
            Assert.Equal(NeighbourhoodMapAdapter.LotViewStreetTarmacUnits / 18f,
                NeighbourhoodMapAdapter.StreetTarmacDeficit, 4);

            //and the corridor arithmetic, from the two halves rather than from the product
            Assert.Equal(33f / 24f, NeighbourhoodMapAdapter.StreetCorridorDeficit, 4);
        }

        [Fact]
        public void TheGeneratedNeighbourhoodIsSpreadWiderThanTheLotViewDrawsIt()
        {
            // TSO's RestoreSurroundings spaces two lots size - 2 tiles apart, so the two halves of
            // the street share the two tiles the packer keeps separate, and the packer now keeps a
            // whole corridor rather than those two: 10 / (47 - 2) of the lot view's pitch. This is
            // why the streets read as wider before their width even counts.
            Assert.Equal(45, NeighbourhoodMapAdapter.LotViewLotPitchTiles(47));
            Assert.Equal(55, NeighbourhoodMapAdapter.PackedLotPitchTiles(47));
            Assert.Equal(55f / 45f,
                (float)NeighbourhoodMapAdapter.PackedLotPitchTiles(47)
                / NeighbourhoodMapAdapter.LotViewLotPitchTiles(47), 4);
        }

        [Fact]
        public void ThePackersMarginAndTheLotViewsOverlapDifferByTenTiles()
        {
            // The packer keeps a whole street corridor between two lots; the lot view overlaps them
            // by two. Ten tiles of the pitch, which is the whole of the spread difference - and a
            // one-tile lot is a degenerate pitch that must still answer something usable rather
            // than going negative, because the helpers ask about cells, not about houses.
            Assert.Equal(10, NeighbourhoodMapAdapter.PackedLotPitchTiles(47)
                - NeighbourhoodMapAdapter.LotViewLotPitchTiles(47));
            Assert.Equal(1, NeighbourhoodMapAdapter.LotViewLotPitchTiles(1));
            Assert.Equal(9, NeighbourhoodMapAdapter.PackedLotPitchTiles(1));
        }
    }
}
