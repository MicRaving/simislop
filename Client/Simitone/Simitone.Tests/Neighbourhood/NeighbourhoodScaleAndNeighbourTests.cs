/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The two numbers the connected neighbourhood rests on, and the one decision that hangs off
    /// them: how big a generated map tile is in a lot view, and which of the lots around the player
    /// are real sub-worlds and which are facades.
    ///
    /// <para>
    /// <b>Why a map tile and a lot tile are the same thing.</b> A TS1 lot is 64x64 tiles and a lot
    /// in the generated world is <see cref="NeighbourhoodLot.Size"/> tiles square, so one generated
    /// map tile <em>is</em> one lot tile. The only conversion left is the lot view's own
    /// <c>WorldUnitsPerTile</c>, and
    /// <see cref="SimitoneSurroundingsPlacement.LotViewUnitsPerTile"/> is that number written out
    /// because the constant lives in <c>FSO.LotView</c> on a type the placement does not otherwise
    /// need. The first test below pins the copy against the engine's own declaration, because a
    /// surround at the right shape and the wrong size is the failure nothing in a screenshot names.
    /// </para>
    ///
    /// <para>
    /// <b>Why the fixtures run the real generator.</b> Every number here is read out of a map that
    /// <see cref="NeighbourhoodMapGenerator"/> actually produced from a layout, and out of the
    /// <see cref="RoadGraph"/> that same layout's gaps really grew into - the street width in
    /// particular is a fact about those two and not something a hand-written array can be asked to
    /// confirm. The lot records themselves are
    /// <see cref="NeighbourhoodTestInput"/>'s canned ones, put back through
    /// <see cref="NeighbourhoodMapAdapter.DescribeInput"/> so the seam a real layout comes out of
    /// is exercised rather than bypassed.
    /// </para>
    ///
    /// <para>
    /// <b>Why this neighbourhood and not a packed one.</b> The near/far decision needs a layout
    /// with both halves of it, and <see cref="NeighbourhoodTestInput.PackedNeighbourhood"/> cannot
    /// supply one: the packer leaves exactly
    /// <see cref="NeighbourhoodLayoutBuilder.SidewalkMargin"/> tiles between lots, so a lot two
    /// lots away is 68 tiles off and a lot next door is 2 tiles off - there is nothing in
    /// between. Every lot the 40-tile cull region covers is therefore a near one and the facade
    /// set is empty, which makes the two radii indistinguishable rather than merely different.
    /// The fixture below is a three-by-three block where one column is twenty tiles from the middle
    /// column and the other two are two, which is a shape a hand-written or re-zoned neighbourhood
    /// really has and which is the only way both halves of the decision can be asserted.
    /// </para>
    ///
    /// <para>
    /// <b>What is asserted as an invariant rather than as a number.</b> The scale is written down
    /// in one place and referenced from everywhere else, so the load-bearing assertions here are
    /// the ratios - "a run of n map tiles is n times the lot view's tile whatever a tile costs",
    /// and "the terrain, the roads and the packed rectangles are all on the map's own integer tile
    /// grid" - with the absolute value checked exactly once, against
    /// <c>FSO.LotView.WorldSpace.WorldUnitsPerTile</c>. If that constant is ever mid-change by
    /// another agent the ratio tests keep passing and the single test that hard-codes it says in
    /// its own failure message which way it moved.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodScaleAndNeighbourTests
    {
        /// <summary>
        /// How far past the property line a neighbour may sit and still be built as a real lot
        /// rather than a facade, in tiles of gap. <c>TS1GameScreen.HollowNeighbourRadiusTiles</c>,
        /// which is private to the screen, restated here so the decision below is pinned against
        /// the number the screen actually passes rather than against whatever the plan defaults to.
        /// </summary>
        /// <remarks>
        /// It used to be a literal eight here, and the literal was the bug: the screen asked for
        /// eight while the surround drew forty, so the lot view stood up a ring of real houses in
        /// the middle of a drawn region whose outer ring was facades. It is the drawn region's own
        /// radius now - read from the constant rather than written down, because the assertion this
        /// file exists for is that the two are <em>the same number</em> and a copy of a number
        /// cannot fail to be equal to itself.
        /// </remarks>
        private const int SubWorldRadiusTiles = SimitoneSurroundingsPlacement.CullRadiusTiles;

        /// <summary>
        /// The sub-world budget the screen passes, for the same reason. Zero or less stands nothing
        /// up and the whole region becomes facades, which is a state worth being able to ask for.
        /// </summary>
        private const int SubWorldBudget = SimitoneSurroundingsPlan.MaxSubWorlds;

        private const int LotSize = 64;

        /// <summary>The side of the map the fixture is packed into.</summary>
        private const int MapSize = NeighbourhoodTestInput.RealMapSize;

        private const short ActiveLotNumber = 216;

        /// <summary>
        /// Everything the tests below read, built once: a layout with a near ring and a far row, the
        /// map the real generator makes from it, the street network its gaps really grew into, the
        /// active lot, and the three answers derived from those - where the lot sits in the lot
        /// view, how much of the map is drawn, and which neighbours become real lots.
        /// </summary>
        private sealed class Fixture
        {
            public NeighbourhoodLayout Layout;
            public NeighbourhoodMap Map;
            public RoadGraph Streets;
            public NeighbourhoodQuery Query;
            public NeighbourhoodLot Active;
            public SimitoneSurroundingsPlacement Placement;
            public SimitoneSurroundingsCull Cull;
            public SimitoneSurroundingsPlan Plan;
        }

        /// <summary>
        /// A three-by-three block of 64-tile lots. The middle lot is the one the player stands on.
        /// Its column's eastern and western neighbours are two tiles away - the packer's own margin,
        /// so those are near ones - and its northern and southern neighbours two tiles away, so
        /// those are too. The lot beyond the western column is twenty tiles away, which is inside
        /// the forty-tile drawn region and outside the eight-tile near radius, and the two lots
        /// above and below it as well: those are the far half of the decision.
        ///
        /// <para>
        /// Nothing overlaps and every gap is at least the two tiles
        /// <see cref="RoadGraph"/> needs to grow a street in, which is the condition the generator
        /// refuses a neighbourhood without.
        /// </para>
        /// </summary>
        private static NeighbourhoodMapInput MixedGapNeighbourhood()
        {
            return new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, MapSize, new[]
                {
                    //the western column, twenty tiles west of the active lot
                    NeighbourhoodTestInput.Lot(213, 132, 150, LotSize, 4, NeighbourhoodDoorSide.East),
                    NeighbourhoodTestInput.Lot(214, 132, 216, LotSize, 4, NeighbourhoodDoorSide.East),
                    NeighbourhoodTestInput.Lot(215, 132, 282, LotSize, 4, NeighbourhoodDoorSide.East),
                    //the active lot's own column
                    NeighbourhoodTestInput.Lot(216, 216, 216, LotSize, 4, NeighbourhoodDoorSide.West),
                    NeighbourhoodTestInput.Lot(217, 216, 150, LotSize, 4, NeighbourhoodDoorSide.West),
                    NeighbourhoodTestInput.Lot(218, 216, 282, LotSize, 4, NeighbourhoodDoorSide.West),
                    //the eastern column, two tiles east of it
                    NeighbourhoodTestInput.Lot(219, 282, 150, LotSize, 4, NeighbourhoodDoorSide.West),
                    NeighbourhoodTestInput.Lot(220, 282, 216, LotSize, 4, NeighbourhoodDoorSide.West),
                    NeighbourhoodTestInput.Lot(221, 282, 282, LotSize, 4, NeighbourhoodDoorSide.West)
                });
        }

        private static Fixture Build()
        {
            // The canned lot records, through the seam a real layout comes back out of.
            var layout = NeighbourhoodMapAdapter.DescribeInput(MixedGapNeighbourhood());
            Assert.NotNull(layout);
            Assert.Equal(9, layout.LotCount);

            var input = NeighbourhoodMapAdapter.InputFor(layout);
            var map = NeighbourhoodMapGenerator.Generate(input);
            Assert.NotNull(map);
            Assert.Equal(MapSize, map.Width);
            Assert.Equal(MapSize, map.Height);

            //the middle lot, which the fixture is arranged around
            var active = layout.FindLot(ActiveLotNumber);
            Assert.NotNull(active);
            Assert.Equal(LotSize, active.Size);
            Assert.Equal(4, active.BaseHeight);
            Assert.True(active.HasHouse);
            //and enough map on every side that the drawn region is not clipped, or the far half of
            //the decision would be missing for the wrong reason
            foreach (var edge in new[]
            {
                active.WorldX, active.WorldY,
                MapSize - active.WorldX - LotSize, MapSize - active.WorldY - LotSize
            })
            {
                Assert.True(edge >= SimitoneSurroundingsPlacement.CullRadiusTiles,
                    "the active lot has only " + edge + " tiles of map past it, which is less than"
                    + " the drawn radius");
            }

            var placement = SimitoneSurroundingsPlacement.For(map, active);
            Assert.True(placement.IsValid);

            var fixture = new Fixture
            {
                Layout = layout,
                Map = map,
                Streets = RoadGraph.BuildNetwork(input),
                Query = new NeighbourhoodQuery(layout, map),
                Active = active,
                Placement = placement,
                Cull = placement.Cull
            };
            fixture.Plan = SimitoneSurroundingsPlan.ForQuery(
                placement, map, fixture.Query, layout, null, SubWorldRadiusTiles, SubWorldBudget);
            Assert.True(fixture.Plan.IsValid, fixture.Plan.Describe());
            return fixture;
        }

        // ==================================================================== the scale

        [Fact]
        public void TheGeneratedTileIsTheLotViewsOwnTile()
        {
            // The copy is written out rather than referenced - see
            // SimitoneSurroundingsPlacement's own remarks - so the only thing keeping it honest is
            // this comparison. Three is the lot view's number and not ours: every wall, floor and
            // object inside the lot is already measured in it.
            Assert.Equal(FSO.LotView.WorldSpace.WorldUnitsPerTile,
                SimitoneSurroundingsPlacement.LotViewUnitsPerTile);
            Assert.Equal(3f, SimitoneSurroundingsPlacement.LotViewUnitsPerTile);

            //and the horizontal tile grid is one grid, not two: the builder's own tile-per-altitude
            //and altitude-per-tile pair has to round-trip, because both the placement's scale and
            //the surround's bank are expressed against it
            Assert.Equal(1f,
                NeighbourhoodCityMeshBuilder.ArryUnitsPerTile
                    * NeighbourhoodCityMeshBuilder.TilesPerArryUnit, 5);

            //and the adapter does not restate the tile-to-byte scale either; it reads it back
            Assert.Equal(NeighbourhoodCityMeshBuilder.ElevationScale,
                NeighbourhoodMapAdapter.ElevationUnitsPerTile);
        }

        [Fact]
        public void OneGeneratedMapTileIsOneLotTileAndThreeLotViewWorldUnits()
        {
            // The whole conversion, on a real map from a real layout. The lot's packed corner is
            // the lot view's origin and nothing else has to be agreed between the two spaces; a
            // tile of generated ground is three units of lot-view ground.
            var f = Build();
            var scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
            var ground = f.Placement.MapTileOrigin.Y;
            var near = new Vector3(f.Active.WorldX, ground, f.Active.WorldY);

            Assert.Equal(0f, f.Placement.ToLotView(near).X, 5);
            Assert.Equal(0f, f.Placement.ToLotView(near).Z, 5);

            //one map tile east of the property line, and the same again north of it
            var east = f.Placement.ToLotView(new Vector3(f.Active.WorldX + 1f, ground, f.Active.WorldY));
            var north = f.Placement.ToLotView(new Vector3(f.Active.WorldX, ground, f.Active.WorldY + 1f));
            Assert.Equal(scale, east.X, 5);
            Assert.Equal(0f, east.Z, 5);
            Assert.Equal(0f, east.Y, 5);
            Assert.Equal(scale, north.Z, 5);
            Assert.Equal(0f, north.X, 5);
            Assert.Equal(0f, north.Y, 5);

            //the rectangle's own far corner, so the lot the player is standing in is Size tiles of
            //map and therefore Size * scale units wide in the lot view
            var far = f.Placement.ToLotView(new Vector3(
                f.Active.WorldX + f.Active.Size, ground, f.Active.WorldY + f.Active.Size));
            Assert.Equal(f.Active.Size * scale, far.X, 4);
            Assert.Equal(f.Active.Size * scale, far.Z, 4);

            //and the drawn region counts in the same tiles, so its width is a tile count too
            Assert.True(f.Cull.WidthTiles == f.Active.Size
                + 2 * SimitoneSurroundingsPlacement.CullRadiusTiles);
        }

        [Fact]
        public void TheRoadIsAsWideInWorldUnitsAsTheStreetGraphSaysItIsInTiles()
        {
            // The street beside the property, measured off the raster the generator wrote rather
            // than read off the constant that describes it, and then put through the one
            // conversion.
            //
            // The measurement is the *narrowest* run of road on the row across the lot's middle
            // that is not inside any lot. Narrowest, not widest, and not simply longest, because a
            // street is grown along the whole of the gap it sits in: the twenty-tile gap west of
            // this lot is one street twenty tiles long and six tiles thick, and a run measured along
            // it reports the length. The thickness is the narrowest corridor the streets occupy,
            // and the driveway tiles that reach into the plots are excluded because they are the
            // lots' own ground rather than part of the street.
            //
            // <b>The width is the gap, and this fixture's tight gap is its own.</b> A street is
            // min(RoadGraph.MainStreetWidth, the free ground it is laid in), and this fixture is
            // hand-written rather than packed: three columns of lots two tiles apart, so the
            // narrowest street in it is two tiles while the nominal the graph asked for is six and
            // the packer itself would have reserved a whole corridor. The twenty-tile gap is
            // asserted separately below, and it is six wide: that is the widening, and it is the
            // case a player with room between the blocks sees.
            const int tightGap = 2;
            var f = Build();
            var scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
            var row = f.Active.WorldY + f.Active.Size / 2;

            var street = NarrowestFreeRoadRun(f, row);
            Assert.True(street > 0, "the generated map drew no street across lot " + f.Active.Number);
            Assert.Equal(tightGap, street);
            Assert.Equal(RoadGraph.MainStreetWidth, f.Streets.StreetWidth);
            Assert.InRange(street, RoadGraph.DrivewayWidth, RoadGraph.MainStreetWidth);

            //The invariant, stated on its own: a run of n map tiles is n * scale world units,
            //whatever a tile costs. So the world width of the street is exactly the tile width
            //times three, and the world width of the lot is exactly Size times three.
            var streetUnits = RoadWorldWidth(street, scale);
            Assert.Equal(street * scale, streetUnits, 4);

            var lotUnits = f.Active.Size * scale;
            Assert.Equal(LotSize * scale, lotUnits, 4);

            //and the comparison a screenshot is of: the street is the same fraction of the lot's
            //width here as it is in tiles, so nothing in the conversion can be quietly applied to
            //one of them and not the other
            Assert.Equal(street / (double)f.Active.Size, streetUnits / lotUnits, 6);
            Assert.True(streetUnits < lotUnits,
                "the street is " + streetUnits + " units wide and the lot it runs past is "
                    + lotUnits + " units wide");
            Assert.Equal(tightGap * scale, streetUnits, 3);

            //and the same band, read the other way round: the gap the streets run in has one of
            //them in it, which is what makes the ratio above a statement about the map rather than
            //about one row of it
            Assert.True(WidestFreeRoadRun(f, row) >= street,
                "a street cannot be thinner than the narrowest corridor it is laid in");
        }

        [Fact]
        public void AStreetInTheTwentyTileGapIsTheLotViewsOwnWidth()
        {
            // <b>The fix, measured on a real generated map.</b> The fixture's western column is
            // twenty tiles from the active lot's, so the gap between them has room for a full-width
            // street, and the street in it is RoadGraph.MainStreetWidth - six tarmac tiles, which
            // is what the lot view draws around the player and what this surround used to draw a
            // third of. It is asserted off the raster, not off the constant, and put through the
            // one conversion, so a mesh or a rasteriser that quietly scaled it would fail here.
            var f = Build();
            var scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;

            //the street in the wide gap, on the row across the lot's middle
            var row = f.Active.WorldY + f.Active.Size / 2;
            var west = f.Active.WorldX - RoadGraph.MainStreetWidth - 1;
            Assert.True(west > 0, "the fixture's wide gap is not where this test looks for it");

            var thick = 0;
            var run = 0;
            for (int x = 0; x < f.Map.Width; x++)
            {
                var road = (f.Map.RoadData[f.Map.Index(x, row)] & RoadMask.MaskBits) != 0
                    && !InsideAnyLot(f.Layout, x, row);
                if (road) { run++; if (run > thick) thick = run; continue; }
                if (run > 0 && x <= f.Active.WorldX) break;
                run = 0;
            }

            //six tarmac tiles, and the same six at three world units a tile as the lot view's road
            Assert.Equal(RoadGraph.MainStreetWidth, thick);
            Assert.Equal(18f, RoadWorldWidth(thick, scale), 3);
            Assert.Equal(NeighbourhoodMapAdapter.LotViewStreetTarmacUnits,
                RoadWorldWidth(thick, scale), 3);
            Assert.Equal(1f, NeighbourhoodMapAdapter.StreetTarmacDeficit, 3);

            //and it is genuinely a corridor, not a lane: no road tile on that row falls inside a
            //lot, which is the invariant that outranks connectivity
            for (int x = west; x < f.Active.WorldX; x++)
            {
                Assert.False(InsideAnyLot(f.Layout, x, row),
                    "the widened street runs over lot ground at " + x + "," + row);
            }
        }

        [Fact]
        public void TheVerticalScaleIsTheSameConversionAsTheHorizontalOne()
        {
            // Not a second constant: the mesh divides the elevation byte by
            // NeighbourhoodCityMeshBuilder.ElevationScale to get height in tiles, and the placement
            // multiplies tiles by LotViewUnitsPerTile. So one elevation byte is a quarter of a
            // world unit on every map, which is the resolution the lot view's own ground has.
            var f = Build();
            var scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
            var x = f.Active.WorldX;
            var z = f.Active.WorldY;

            var nearByte = f.Map.ElevationData[f.Map.Index(x, z)];
            var farByte = f.Map.ElevationData[f.Map.Index(x + f.Active.Size - 1, z)];
            var step = Math.Abs(farByte - nearByte);

            var nearUnits = f.Placement.ToLotView(new Vector3(x, nearByte, z)).Y;
            var farUnits = f.Placement.ToLotView(new Vector3(x, farByte, z)).Y;

            Assert.Equal(step / NeighbourhoodCityMeshBuilder.ElevationScale * scale,
                Math.Abs(farUnits - nearUnits), 4);

            //one elevation byte is three twelfths of a unit - a quarter of one, which is the
            //finest step the generated ground can express at all
            Assert.Equal(0.25f, scale / NeighbourhoodCityMeshBuilder.ElevationScale, 4);

            //and the surround is measured down to the lowest ground on the property's border ring,
            //so the ring is at or above the lot view's own ground at zero on both sampled corners
            Assert.True(nearUnits >= -1e-3f,
                "the near ring corner sits " + nearUnits + " units below the lot view's own ground");
            Assert.True(farUnits >= -1e-3f,
                "the far ring corner sits " + farUnits + " units below the lot view's own ground");
        }

        [Fact]
        public void TheDrawnRegionIsQuotedInTheSameUnitsAsEverythingElseInIt()
        {
            // The cull's two derived distances - how far the region's corner is, and where its fog
            // finishes - are in lot-view units, so they are two more places the one conversion has
            // to be applied and the place a stale copy of it would show up as a fog that stops in
            // the middle of the map.
            var f = Build();
            var scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
            var halfWidth = f.Cull.WidthTiles * 0.5f;
            var halfDepth = f.Cull.HeightTiles * 0.5f;

            var expectedCorner = (float)Math.Sqrt(
                halfWidth * halfWidth + halfDepth * halfDepth) * scale;

            Assert.Equal(expectedCorner, f.Cull.CornerDistanceUnits, 3);
            Assert.Equal(expectedCorner * SimitoneSurroundingsCull.FogMargin, f.Cull.FogDistance, 3);
            Assert.True(f.Cull.CornerDistanceUnits > f.Active.Size * scale,
                "the drawn region has to reach past the property or the fog finishes on the lot");
        }

        [Fact]
        public void TheTerrainTheRoadsAndTheLotRectangleAreAllOnOneTileGrid()
        {
            // The real mesh, built by the real builder from the real map and cropped by the real
            // cull - asked the only question that matters: is it all in the same tile grid as the
            // packed rectangles?
            //
            // Every pass emits whole tiles, so a vertex lands on an integer map tile and the
            // placement's single multiplication is all that stands between it and the lot view. If
            // any of the three were on a grid of its own - a road drawn at its own scale, a lot
            // rectangle in packed coordinates rather than map ones - the vertices below would stop
            // being whole tiles and this test would name which of them moved.
            var f = Build();
            var mesh = NeighbourhoodCityMeshBuilder.TryBuild(f.Map, out var reason);
            Assert.True(mesh != null, reason);
            Assert.True(mesh.TotalPrimitives > 0, "the whole-map mesh held no triangles at all");

            //the drawn region, not the neighbour cut: this is the mesh the surround actually
            //uploads, and since Part 8 it is the whole shared map rather than a 144-tile crop of it
            Assert.True(f.Placement.DrawnRegion.IsConnectedMap, f.Placement.DrawnRegion.Describe());
            var crop = SimitoneSurroundingsCrop.Crop(mesh, f.Placement.DrawnRegion);
            Assert.False(crop.IsEmpty);
            Assert.True(crop.RoadPrimitives > 0,
                "the drawn region held no road, so there is nothing to compare the ground against");

            var scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;

            var road = crop.RoadVertices;
            Assert.True(road != null && road.Length > 0);
            for (int i = 0; i < road.Length; i++)
            {
                AssertWholeTile(f, road[i].Position, scale, "road vertex " + i);
            }

            //every ground triangle the surround actually submits, not the whole-map mesh
            var groundVertices = 0;
            for (int layer = 0; layer < crop.LayerVertices.Length; layer++)
            {
                var vertices = crop.LayerVertices[layer];
                if (vertices == null) continue;
                groundVertices += vertices.Length;
                for (int i = 0; i < vertices.Length; i++)
                {
                    AssertWholeTile(f, vertices[i].Position, scale,
                        "ground layer " + layer + " vertex " + i);
                }
            }
            Assert.True(groundVertices > 0, "the cropped region held no ground");

            //And the property is on that grid too: its far edge is exactly Size map tiles from its
            //near one, which is Size * scale units apart once placed - the same width the camera,
            //the roads and the bank all measured above.
            var propertyEdge = new Vector3(f.Active.WorldX, 0f, f.Active.WorldY + f.Active.Size);
            AssertWholeTile(f, propertyEdge, scale, "the property's own far edge");
            Assert.Equal(f.Active.Size * scale,
                f.Placement.ToLotView(propertyEdge).Z, 4);

            //and the far lot's plot edge lands on the same grid as the active one, which is the
            //whole claim: two plots twenty tiles apart are twenty tiles and sixty units apart,
            //with no second scale in between.
            var far = f.Layout.FindLot(214);
            Assert.NotNull(far);
            Assert.Equal(20, f.Active.WorldX - (far.WorldX + far.Size));
            //Twenty tiles of gap is inside the neighbour radius of forty, so this lot is a real
            //lot rather than a facade. It used to be asserted as outside a radius of eight, which
            //was the assertion that made the facade ring. Stated as the gap itself rather than as
            //a radius comparison, so it survives whichever radius the two questions answer with -
            //they are different numbers now that the ground is drawn for the whole map.
            Assert.Equal(20, SimitoneSurroundingsPlan.SubWorldGapFor(f.Active, far));
            Assert.True(SimitoneSurroundingsPlan.WithinRadius(f.Active, far, SubWorldRadiusTiles),
                "the far lot is inside the neighbour radius and must therefore be a real lot, not"
                + " a facade: this is the ring of cardboard the shared radius exists to remove");
            Assert.True(f.Plan.Contains(far.Number),
                "lot 214 is within the radius, so the plan stands it up as a real lot");
            //the far lot's east edge to the active lot's west edge, which is the twenty-tile gap
            //again in lot-view units - the same number the radius is measured in tiles, so the
            //scale cancels out of the comparison
            Assert.Equal(20f * scale,
                f.Placement.ToLotView(new Vector3(f.Active.WorldX, 0f, f.Active.WorldY)).X
                    - f.Placement.ToLotView(
                        new Vector3(far.WorldX + far.Size, 0f, far.WorldY)).X, 3);
        }

        // ==================================================================== near and far

        /// <summary>
        /// How one surrounding lot is answered by the renderer: a real sub-world, a facade, or
        /// nothing at all.
        /// </summary>
        private enum Answer
        {
            /// <summary>Drawn by the lot view itself; neither of the other two applies.</summary>
            Active,

            /// <summary>Built as a real hollow lot standing in the world beside this one.</summary>
            SubWorld,

            /// <summary>
            /// Drawn as a facade of its own walls. A real outcome rather than a failure, but only
            /// inside the drawn region and only because the sub-world budget ran out - which at the
            /// shipped budget and the packer's own lot pitch does not happen at all.
            /// </summary>
            Facade,

            /// <summary>Too far to be drawn at all, or an empty plot with no house to draw.</summary>
            None
        }

        /// <summary>
        /// The one decision per lot, written down in one place because it is the rule rather than
        /// either call site: a lot inside the sub-world radius is a real lot; a lot inside the
        /// drawn region but outside that radius is a facade; the lot the player is standing in is
        /// the lot's own; and a plot with no house has nothing to stand there.
        ///
        /// <para>
        /// <b>Why it is asked of the production rule and not restated here.</b> The facade half is
        /// <see cref="NeighbourhoodFacadePlan.ShouldDraw(NeighbourhoodLot, int?, IReadOnlyCollection{short})"/>
        /// with the plan's lot numbers handed to it, and that overload is where "a lot the plan
        /// will stand up as a real sub-world is not also a facade" lives. Restating it in a test
        /// would pin a copy; asking it here pins the rule, and the null-set calls below pin the
        /// capability-off behaviour the same method has to keep - the map view has no sub-worlds
        /// and passes none, so its two-argument answer cannot have moved.
        /// </para>
        ///
        /// <para>
        /// <b>Why the sub-world half is asked of the plan.</b>
        /// <c>TS1GameScreen</c> builds the plan at the drawn region's own radius - the screen's
        /// <c>HollowNeighbourRadiusTiles</c> is <c>SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles</c>,
        /// which <em>is</em> <c>SimitoneSurroundingsPlacement.CullRadiusTiles</c> - and the surround
        /// builds its facade queue from that same region. So the two sets are not "near" and "far"
        /// any more: the sub-world set is the facade-eligible region minus whatever the budget could
        /// not take, and the only way to get a facade is to run out of budget. This is where that
        /// has to be visible, because "the two sets are disjoint" is a much weaker statement than
        /// "one of them is empty" and only the second one is what a player means by a real
        /// neighbourhood.
        /// </para>
        /// </summary>
        private static Answer AnswerFor(
            Fixture f, NeighbourhoodLot lot, IReadOnlyCollection<short> subWorldLots)
        {
            if (lot.Number == f.Active.Number) return Answer.Active;
            if (!lot.HasHouse) return Answer.None;
            if (f.Plan.Contains(lot.Number)) return Answer.SubWorld;
            if (f.Cull.CoversLot(lot)
                && NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number, subWorldLots))
            {
                return Answer.Facade;
            }
            return Answer.None;
        }

        [Fact]
        public void EveryLotInsideTheDrawnRegionIsARealLotAndNoFacadeIsLeftInIt()
        {
            // The decision, on the real generator's map, and the claim this feature exists for: the
            // drawn region is all real. Every lot the region covers is a sub-world, so there is no
            // ring of cardboard between the real houses and the edge of the drawn ground, and
            // nothing outside the region is a sub-world either.
            var f = Build();
            var near = f.Plan.NeighbourNumbers;
            var subWorlds = 0;
            var facades = 0;
            var inRegion = 0;

            for (int i = 0; i < f.Layout.LotCount; i++)
            {
                var lot = f.Layout.Lots[i];
                if (f.Cull.CoversLot(lot) && lot.Number != f.Active.Number && lot.HasHouse) inRegion++;
                var answer = AnswerFor(f, lot, near);
                if (answer == Answer.SubWorld) subWorlds++;
                if (answer == Answer.Facade) facades++;
            }

            Assert.True(subWorlds > 0, "no lot around " + f.Active.Number + " became a real lot, so"
                + " the decision is untested. " + f.Plan.Describe());
            Assert.True(inRegion > 1,
                "the drawn region covers " + inRegion + " buildable lot(s) other than the player's"
                + " own, so there is nothing for the coverage claim to be about");
            Assert.Equal(f.Plan.Neighbours.Count, subWorlds);

            //Not "some facades and some real lots" any more. A flat lot inside the region is the
            //bug, and asserting a count of zero is what makes it a bug rather than a shape.
            Assert.Equal(0, facades);
            Assert.Equal(inRegion, subWorlds);
            Assert.True(f.Plan.IsFullyCovered,
                "the plan deferred " + f.Plan.DeferredCount + " lot(s) inside the drawn region, so"
                + " the region is not fully real at the shipped budget of " + SubWorldBudget);
            Assert.Equal(0, f.Plan.DeferredCount);
            Assert.Equal(SubWorldRadiusTiles, f.Plan.RadiusTiles);
            Assert.Equal(SubWorldBudget, f.Plan.SubWorldBudget);

            //and it holds because the radius is the drawn region's own: every lot in the region is
            //within it, so every lot in the region is a candidate, and the budget is the only thing
            //that could have said otherwise. With one more reason a lot can be no candidate: a lot
            //with no drawn ground under it is drawn by nobody rather than drawn wrong. This fixture
            //has none since audit O1; see EveryLotInTheFixtureStandsOnDrawnGround.
            for (int i = 0; i < f.Layout.LotCount; i++)
            {
                var lot = f.Layout.Lots[i];
                if (lot.Number == f.Active.Number) continue;
                var expected = SimitoneSurroundingsPlan.WithinRadius(
                        f.Active, lot, SubWorldRadiusTiles)
                    && SimitoneSurroundingsPlacement.GroundIsDrawnUnder(f.Map, lot);
                Assert.True(expected == f.Plan.Contains(lot.Number),
                    "lot " + lot.Number + " at (" + lot.WorldX + "," + lot.WorldY + ") is "
                    + (expected ? "a candidate" : "not a candidate")
                    + " and is "
                    + (f.Plan.Contains(lot.Number) ? "in" : "not in")
                    + " the plan. Plan warnings: "
                    + string.Join(" | ", f.Plan.Warnings));
            }
        }

        [Fact]
        public void EveryLotInTheFixtureStandsOnDrawnGround()
        {
            // The ground rule. A lot whose base tile is outside the mesh's clip stands on nothing -
            // §9.1's "disconnected houses floating in a void". This fixture's western column at
            // x=132 used to be in the TSO diamond's cut corner; the clip is now the map inset by
            // ClipInset (audit O1), so every lot here has ground, and every one is a real lot. The
            // refusal itself is pinned by SimitoneSurroundingsCullTests.ALotOnGroundTheMeshNeverDrawsIsNotInRange.
            var f = Build();

            var offGround = new List<short>();
            for (int i = 0; i < f.Layout.LotCount; i++)
            {
                var lot = f.Layout.Lots[i];
                if (!SimitoneSurroundingsPlacement.GroundIsDrawnUnder(f.Map, lot)) offGround.Add(lot.Number);
            }

            Assert.Empty(offGround);
            foreach (var number in offGround)
            {
                var lot = f.Layout.FindLot(number);
                Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(lot.WorldX, lot.WorldY),
                    "lot " + number + " is refused but its base tile is inside the clip, so the"
                    + " refusal is coming from somewhere else");
                //and it is drawn by nobody: not a facade, not a sub-world, and not in the queue
                Assert.False(f.Cull.CoversLot(lot));
                Assert.False(NeighbourhoodRenderDecision.InsideCut(lot, f.Cull));
                Assert.False(f.Plan.Contains(number));
                Assert.DoesNotContain(number, f.Plan.NeighbourNumbers);
                //and the overhang is measured rather than hidden
                Assert.True(SimitoneSurroundingsPlacement.GroundlessTilesOf(f.Map, lot) > 0,
                    "a refused lot with no groundless footprint tile is refused for another reason");
            }

            //Every lot that does have ground is untouched by the rule: it is a candidate, in the
            //plan, and a real lot. The count is the fixture's, not a constant, so this holds if the
            //fixture is ever moved.
            var onGround = f.Layout.LotCount - offGround.Count;
            Assert.True(onGround > 1, "the fixture has almost nothing on drawn ground");
            foreach (var lot in f.Layout.Lots)
            {
                if (lot.Number == f.Active.Number) continue;
                if (!SimitoneSurroundingsPlacement.GroundIsDrawnUnder(f.Map, lot)) continue;
                Assert.True(f.Plan.Contains(lot.Number),
                    "lot " + lot.Number + " stands on drawn ground and should be a real lot");
            }
        }

        [Fact]
        public void TheSubWorldRadiusIsTheDrawnRegionsOwnRadius()
        {
            // The coverage claim as a fact about the two numbers rather than as a loop over a
            // layout. Forty tiles is how much *ground* is worth drawing, it is nearly free, and the
            // fog finishes before its edge; eight used to be how many *lots* were worth standing up,
            // which left a ring of facades between the real houses and the drawn ground. The two
            // questions turned out to have the same answer, so there is one number now - and the
            // count that bounds the whole thing is the sub-world budget, not the radius.
            var f = Build();

            Assert.Equal(SimitoneSurroundingsPlacement.CullRadiusTiles, f.Cull.RadiusTiles);
            Assert.Equal(SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles, f.Plan.RadiusTiles);
            Assert.True(SimitoneSurroundingsPlan.RadiusIsShared(
                    f.Plan.RadiusTiles, f.Cull.RadiusTiles),
                "the plan and the drawn region are at different radii, so the lots between the two"
                + " are facades rather than real lots");
            Assert.True(f.Plan.RadiusSharedWith(f.Cull));

            //and the weaker one-sided rule still holds, which is what makes it safe for a caller to
            //ask for a narrower radius deliberately: no real lot ever stands outside the drawn
            //region, because a lot that did would be standing on ground that was never drawn
            Assert.True(SimitoneSurroundingsPlan.RadiusFitsCut(f.Plan.RadiusTiles, f.Cull.RadiusTiles));
            for (int i = 0; i < f.Plan.Neighbours.Count; i++)
            {
                var lot = f.Plan.Neighbours[i].Lot;
                Assert.True(f.Cull.CoversLot(lot),
                    "lot " + lot.Number + " is a real sub-world but is outside the drawn region");
            }

            //and there is no room for a facade inside the region at all, which is the whole claim
            Assert.Equal(0, NeighbourhoodRenderDecision.FacadeFallbackCount(
                f.Layout, f.Active.Number, f.Plan, f.Cull));
        }

        [Fact]
        public void ALotIsNeverBothARealLotAndAFacade()
        {
            // The rule the decision exists to hold. Two answers for one lot means the same house
            // drawn twice on the same plot - a facade standing inside a real lot - which is the one
            // thing a player cannot be given two explanations for. Every lot in the packed layout
            // gets exactly one answer, and the sub-world and facade sets are disjoint.
            var f = Build();
            var near = f.Plan.NeighbourNumbers;
            var subWorlds = new List<short>();
            var facades = new List<short>();
            var seen = new HashSet<short>();

            for (int i = 0; i < f.Layout.LotCount; i++)
            {
                var lot = f.Layout.Lots[i];
                Assert.True(seen.Add(lot.Number),
                    "lot " + lot.Number + " appears twice in the layout, so the decision cannot be"
                    + " counted per lot");
                switch (AnswerFor(f, lot, near))
                {
                    case Answer.SubWorld:
                        //The production rule, asked directly rather than through the classification
                        //above: a lot the plan stands up is not a facade candidate at all, whatever
                        //region it is in and whether or not anyone passed the set.
                        Assert.False(NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number, near),
                            "lot " + lot.Number + " is a real sub-world and is still a facade"
                            + " candidate");
                        Assert.DoesNotContain(lot.Number, facades);
                        subWorlds.Add(lot.Number);
                        break;
                    case Answer.Facade:
                        //and the other direction: a lot the budget deferred is a facade, and handing
                        //the near set over did not take it away
                        Assert.True(NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number, near),
                            "lot " + lot.Number + " is a facade but ShouldDraw says otherwise");
                        Assert.DoesNotContain(lot.Number, subWorlds);
                        facades.Add(lot.Number);
                        break;
                    case Answer.Active:
                        //the lot view draws this one in full from its own house file. A facade for
                        //it is a whole lot view stood up to build the house the player is standing
                        //in; a sub-world for it is the same lot twice.
                        Assert.False(f.Plan.Contains(lot.Number),
                            "the active lot is its own neighbour");
                        Assert.False(NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number, near),
                            "the active lot is still a facade candidate");
                        Assert.DoesNotContain(lot.Number, subWorlds);
                        Assert.DoesNotContain(lot.Number, facades);
                        break;
                }
            }

            Assert.Equal(near, subWorlds);
            Assert.Empty(subWorlds.FindAll(lot => facades.Contains(lot)));

            //The capability-off behaviour, pinned: with no sub-world set the two-argument call is
            //the whole rule it has always been, so Part A's map view - which has no sub-worlds and
            //passes none - cannot have moved because B2 landed.
            for (int i = 0; i < f.Layout.LotCount; i++)
            {
                var lot = f.Layout.Lots[i];
                if (lot.Number == f.Active.Number || !lot.HasHouse) continue;
                Assert.Equal(NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number),
                    NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number, null));
                Assert.Equal(NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number),
                    NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number, new short[0]));
                //and the only thing the set changes is the near lots, which stop being facades
                if (subWorlds.Contains(lot.Number))
                {
                    Assert.False(NeighbourhoodFacadePlan.ShouldDraw(lot, f.Active.Number, near));
                }
            }

            //and the same answer twice, so a decision that depends on iteration order or on the
            //reachability list's order shows up here rather than as a different street on a
            //different machine
            var again = Build();
            Assert.Equal(near, again.Plan.NeighbourNumbers);
            //including the budget's own order, which is a separate ordering from Neighbours' and
            //the one that decides which lot survives when the budget binds
            Assert.Equal(f.Plan.SelectionOrder, again.Plan.SelectionOrder);

            //and the drawn region's lots are partitioned between the two and nothing else, so the
            //radius is what takes a lot out of the facade set rather than the facade set being
            //whatever happened to be left over
            var inRegion = 0;
            for (int i = 0; i < f.Layout.LotCount; i++)
            {
                var lot = f.Layout.Lots[i];
                if (lot.Number == f.Active.Number || !lot.HasHouse) continue;
                if (f.Cull.CoversLot(lot)) inRegion++;
            }
            Assert.True(inRegion > 0, "the drawn region covers no other lot, so the decision is"
                + " being made over nothing");
            Assert.Equal(inRegion, subWorlds.Count + facades.Count);
            Assert.True(subWorlds.Count > 0, "no lot in the drawn region became a real lot");
            //and no lot in the drawn region is left as a facade at the shipped budget, which is the
            //coverage claim. A facade here means the budget bound, and it says so in the plan.
            Assert.Empty(facades);
            Assert.True(f.Plan.IsFullyCovered, f.Plan.Describe());
        }

        // ================================================================ the budget

        /// <summary>
        /// The plan for the fixture at a named budget, which is how the over-budget behaviour is
        /// reached: the layout is unchanged and only the number moves, so whatever differs between
        /// two of these plans is the budget and nothing else.
        /// </summary>
        private static SimitoneSurroundingsPlan PlanWith(Fixture f, int maxSubWorlds)
        {
            return SimitoneSurroundingsPlan.ForQuery(f.Placement, f.Map, f.Query, f.Layout, null,
                SubWorldRadiusTiles, maxSubWorlds);
        }

        [Fact]
        public void AnOverBudgetPlanTakesTheNearestLotsAndDefersTheRestToFacades()
        {
            // The budget, asked as behaviour rather than as prose. A lot the budget could not take
            // is not dropped - it is drawn as a facade, inside the drawn region, which is the only
            // way this can cost flatness rather than a hole in the street. And which lots are taken
            // is nearest-first, deterministically, so the same lot is deferred on every visit.
            var f = Build();
            var full = f.Plan;
            Assert.True(full.Neighbours.Count > 2,
                "the fixture needs more neighbours than a budget of two to be interesting");

            //nearest-first is what the order says, and the lot number is what breaks a tie
            var order = full.SelectionOrder;
            Assert.Equal(full.Neighbours.Count, order.Count);
            for (int i = 1; i < order.Count; i++)
            {
                var previous = f.Layout.FindLot(order[i - 1]);
                var current = f.Layout.FindLot(order[i]);
                var previousGap = SimitoneSurroundingsPlan.SubWorldGapFor(f.Active, previous);
                var currentGap = SimitoneSurroundingsPlan.SubWorldGapFor(f.Active, current);
                Assert.True(previousGap < currentGap
                        || (previousGap == currentGap && order[i - 1] < order[i]),
                    "lot " + order[i] + " is nearer than lot " + order[i - 1]
                    + " but comes after it in the selection order");
            }

            var budget = 2;
            var trimmed = PlanWith(f, budget);
            Assert.True(trimmed.IsValid);
            Assert.Equal(budget, trimmed.SubWorldBudget);
            Assert.Equal(budget, trimmed.Neighbours.Count);
            Assert.Equal(full.Neighbours.Count - budget, trimmed.DeferredCount);
            Assert.False(trimmed.IsFullyCovered);

            //the nearest-first prefix survives, and the deferred tail is the rest of that same
            //order - never a different order, and never dependent on which candidates came first
            for (int i = 0; i < budget; i++)
            {
                Assert.Equal(order[i], trimmed.NeighbourNumbers[i]);
                Assert.DoesNotContain(order[i], trimmed.DeferredNumbers);
            }
            for (int i = budget; i < order.Count; i++)
            {
                Assert.DoesNotContain(order[i], trimmed.NeighbourNumbers);
                Assert.Contains(order[i], trimmed.DeferredNumbers);
            }

            //and the deferred lots are inside the drawn region - that is what makes them facades
            //rather than invisible, and it is what makes the budget safe rather than destructive
            var facades = 0;
            for (int i = 0; i < f.Layout.LotCount; i++)
            {
                var lot = f.Layout.Lots[i];
                //The player's own lot is drawn by the lot view in full, so it is neither and is not
                //part of what the budget has to cover; a lot outside the drawn region is not drawn
                //at all, and is not a facade this change has anything to say about.
                if (lot.Number == f.Active.Number || !f.Cull.CoversLot(lot)) continue;
                var render = NeighbourhoodRenderDecision.Decide(
                    lot, f.Active.Number, trimmed, f.Cull);
                Assert.True(render != NeighbourhoodLotRender.None,
                    "lot " + lot.Number + " is neither real nor drawn");
                if (render == NeighbourhoodLotRender.Facade)
                {
                    facades++;
                    Assert.False(trimmed.Contains(lot.Number));
                    Assert.Contains(lot.Number, trimmed.DeferredNumbers);
                }
            }
            Assert.Equal(trimmed.DeferredCount, facades);
            Assert.Equal(trimmed.DeferredCount,
                NeighbourhoodRenderDecision.FacadeFallbackCount(
                    f.Layout, f.Active.Number, trimmed, f.Cull));

            //and the same budget twice gives the same street, and a budget of everything gives the
            //untrimmed plan back
            var again = PlanWith(f, budget);
            Assert.Equal(trimmed.NeighbourNumbers, again.NeighbourNumbers);
            Assert.Equal(trimmed.DeferredNumbers, again.DeferredNumbers);
            Assert.Equal(full.NeighbourNumbers, PlanWith(f, full.Neighbours.Count).NeighbourNumbers);

            //a budget of nothing stands up no lots and defers all of them, which is a state worth
            //being able to reach: the facade pass over the whole region is the capability-off shape
            var none = PlanWith(f, 0);
            Assert.True(none.IsValid);
            Assert.Empty(none.Neighbours);
            Assert.Equal(full.Neighbours.Count, none.DeferredCount);
            Assert.False(none.IsFullyCovered);

            //and a negative budget is zero, not an error or an unlimited pass
            var negative = PlanWith(f, -4);
            Assert.Equal(0, negative.SubWorldBudget);
            Assert.Empty(negative.Neighbours);
        }

        [Fact]
        public void TheShippedBudgetIsNeverReachedByAPackedNeighbourhood()
        {
            // The feasibility measurement, asked of the layout the generator really produces: forty
            // 64-tile lots with the packer's own two-tile margin, which is what a base residential
            // neighbourhood looks like once fitted into a 512 map. Every lot the drawn region covers
            // is inside the budget, so no real layout pays a facade for being fully real - and the
            // count is asserted rather than reasoned about, because it is the number
            // MaxSubWorlds was chosen against.
            var input = NeighbourhoodTestInput.PackedNeighbourhood(41, LotSize, 2, MapSize);
            var layout = NeighbourhoodMapAdapter.DescribeInput(input);
            Assert.NotNull(layout);
            Assert.Equal(41, layout.LotCount);

            var map = NeighbourhoodMapGenerator.Generate(input);
            var covered = 0;
            var widest = 0;
            for (int i = 0; i < layout.LotCount; i++)
            {
                var lot = layout.Lots[i];
                SimitoneSurroundingsPlacement placement;
                string reason;
                Assert.True(SimitoneSurroundingsPlacement.TryFor(
                    layout, map, lot.Number, out placement, out reason), reason);
                var cull = placement.Cull;
                Assert.True(cull.IsValid, cull.Describe());
                Assert.True(SimitoneSurroundingsPlan.RadiusIsShared(
                        SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles, cull.RadiusTiles));

                //the region plus the budget, for this lot, over the layout's own reachability
                var plan = SimitoneSurroundingsPlan.ForQuery(placement, map,
                    new NeighbourhoodQuery(layout, map), layout, null,
                    SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles,
                    SimitoneSurroundingsPlan.MaxSubWorlds);
                Assert.True(plan.IsValid, plan.Describe());
                Assert.True(plan.IsFullyCovered,
                    "lot " + lot.Number + " deferred " + plan.DeferredCount
                    + " lot(s) at the shipped budget: " + plan.Describe());

                //and the count of lots the region holds, which is the measurement
                var inRegion = 0;
                for (int j = 0; j < layout.LotCount; j++)
                {
                    var other = layout.Lots[j];
                    if (other.Number == lot.Number || !other.HasHouse) continue;
                    if (!cull.CoversLot(other)) continue;
                    inRegion++;
                    //every one of them is inside the plan, so none of them is a facade
                    Assert.False(NeighbourhoodRenderDecision.IsFacade(
                        NeighbourhoodRenderDecision.Decide(other, lot.Number, plan, cull)),
                        "lot " + other.Number + " is a facade beside lot " + lot.Number
                        + ", so the region is not fully real");
                }
                covered += inRegion;
                if (inRegion > widest) widest = inRegion;
                //the region is never more than the budget can take
                Assert.True(inRegion <= SimitoneSurroundingsPlan.MaxSubWorlds,
                    "the drawn region around lot " + lot.Number + " holds " + inRegion
                    + " lots, which is more than the sub-world budget of "
                    + SimitoneSurroundingsPlan.MaxSubWorlds);
            }

            Assert.True(covered > 0, "no lot in the packed neighbourhood had a neighbour in view");
            //the number MaxSubWorlds is sized against: at a 66-tile pitch a 64-tile lot's 144-tile
            //region is three plots across and three deep, so a lot has eight neighbours and not
            //thirty. Asserted as a range rather than as eight, because the packs the packer actually
            //produces are relaxed rather than gridded and the corner lots differ from the middles.
            //
            //Measured over the lots that stand on drawn ground, because that is what "covered" means:
            //this fixture's packer puts its grid from the map's top-left corner, and the mesh is
            //clipped to the isometric diamond, so a share of the 41 lots is in a cut corner where no
            //ground is emitted at all. Those lots are drawn by nobody and contribute no neighbours to
            //anybody. The shipped layout does not have this property - NeighbourhoodPixelTransform
            //fits every LotLocations position a street corridor inside the clip - and
            //TheOnlyLotsWithoutDrawnGroundAreOutsideTheDiamond says so directly.
            var onGround = 0;
            for (int i = 0; i < layout.LotCount; i++)
            {
                if (SimitoneSurroundingsPlacement.GroundIsDrawnUnder(map, layout.Lots[i])) onGround++;
            }
            Assert.True(onGround > 0, "no lot in the packed neighbourhood stands on drawn ground");
            Assert.InRange(covered / onGround, 4, 9);
            Assert.True(layout.LotCount - onGround > 0,
                "this fixture is supposed to put some of its lots outside the drawn ground; if that"
                + " stops being true the mean is being taken over a different population than the"
                + " one the range was measured on");
        }

        // ==================================================================== the helpers

        //Every free run of road on a row, narrowest first. The generator grows a street along
        /// the whole of the gap it sits in, so a run measured along a street is its <em>length</em>;
        /// its width is the thinnest corridor the streets occupy on that row. Driveway tiles that
        /// reach into a plot are excluded, because they are the lot's own ground and not the street.
        /// </summary>
        private static int NarrowestFreeRoadRun(Fixture f, int row)
        {
            var narrowest = int.MaxValue;
            var run = 0;
            for (int x = 0; x <= f.Map.Width; x++)
            {
                var road = x < f.Map.Width
                    && (f.Map.RoadData[f.Map.Index(x, row)] & RoadMask.MaskBits) != 0
                    && !InsideAnyLot(f.Layout, x, row);
                if (road) { run++; continue; }
                if (run > 0 && run < narrowest) narrowest = run;
                run = 0;
            }
            return narrowest == int.MaxValue ? 0 : narrowest;
        }

        /// <summary>The longest such run, which is a street's length rather than its width.</summary>
        private static int WidestFreeRoadRun(Fixture f, int row)
        {
            var widest = 0;
            var run = 0;
            for (int x = 0; x <= f.Map.Width; x++)
            {
                var road = x < f.Map.Width
                    && (f.Map.RoadData[f.Map.Index(x, row)] & RoadMask.MaskBits) != 0
                    && !InsideAnyLot(f.Layout, x, row);
                run = road ? run + 1 : 0;
                if (run > widest) widest = run;
            }
            return widest;
        }

        private static bool InsideAnyLot(NeighbourhoodLayout layout, int x, int z)
        {
            for (int i = 0; i < layout.LotCount; i++)
            {
                var lot = layout.Lots[i];
                if (x < lot.WorldX || x >= lot.WorldX + lot.Size) continue;
                if (z < lot.WorldY || z >= lot.WorldY + lot.Size) continue;
                return true;
            }
            return false;
        }

        /// <summary>A run of map tiles as lot-view world units, through the one conversion.</summary>
        private static float RoadWorldWidth(int tiles, float scale)
        {
            return tiles * scale;
        }

        /// <summary>
        /// That a mesh vertex is a whole generated map tile and is placed at exactly
        /// <paramref name="scale"/> units per tile - the shared-scale claim, spelled out per vertex
        /// so a failure names the vertex rather than the mesh.
        /// </summary>
        private static void AssertWholeTile(Fixture f, Vector3 position, float scale, string what)
        {
            //the builder writes integer tile corners
            Assert.True(Math.Abs(position.X - (float)Math.Round(position.X)) < 1e-3f,
                what + " sits at map x " + position.X + ", which is not a tile boundary");
            Assert.True(Math.Abs(position.Z - (float)Math.Round(position.Z)) < 1e-3f,
                what + " sits at map z " + position.Z + ", which is not a tile boundary");

            //and after placement it is a whole number of lot-view tiles from the property's origin
            var placed = f.Placement.ToLotView(position);
            Assert.True(Math.Abs(placed.X / scale - (float)Math.Round(placed.X / scale)) < 1e-3f,
                what + " is " + placed.X + " units from the origin, which is " + (placed.X / scale)
                + " tiles rather than a whole one");
            Assert.True(Math.Abs(placed.Z / scale - (float)Math.Round(placed.Z / scale)) < 1e-3f,
                what + " is " + placed.Z + " units from the origin, which is " + (placed.Z / scale)
                + " tiles rather than a whole one");
        }
    }
}