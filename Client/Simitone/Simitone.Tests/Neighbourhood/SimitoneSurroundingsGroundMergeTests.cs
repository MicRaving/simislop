/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The drawn region has one ground, and this is the arithmetic that says so.
    ///
    /// <para><b>The bug these pin.</b> A real neighbouring lot is a sub-world with its own
    /// <c>TerrainComponent</c>, and it drew a second surface over the surround's: a smoothstep
    /// bicubic of sixteen <c>round(elevation / 22.5)</c> samples spread over the lot's rectangle, with
    /// up to a fifth of a tile of perlin noise in the interpolation fraction. The surround draws the
    /// generated map's per-tile elevation raster over the same rectangle. The two were built to
    /// agree on the <em>plane</em> - <see cref="SimitoneSurroundingsPlacement.GroundOriginFor"/> and
    /// <see cref="SimitoneSurroundingsRestore.BaseAltitudeFor"/> exist for that - and on the
    /// <em>rectangle</em> - <see cref="SimitoneSurroundingsPlan.SubWorldPositionFor"/> is the delta
    /// <see cref="SimitoneSurroundingsPlacement.ToLotView"/> subtracts - and on nothing else, so
    /// they crossed: a player saw a step of up to a tile and a half at the edge of every plot, with
    /// the surround's tile grid showing through around it.</para>
    ///
    /// <para><b>What is asserted.</b> Four rectangles and one height, all agreeing: the active lot's
    /// packed rectangle, the neighbour's sub-world rectangle
    /// (<c>GlobalPosition * LotViewUnitsPerTile</c>), the surround's rectangle for that neighbour
    /// (<c>ToLotView</c> of its packed corner), and the two grounds over every corner of the
    /// neighbour's lot. The horizontal half is what the existing road-seam tests already assume and
    /// this states as a whole; the vertical half is new, because it is the half that was wrong.</para>
    ///
    /// <para><b>Why it is all through the real arithmetic.</b> Every number is read out of
    /// <see cref="SimitoneSurroundingsPlacement"/>, <see cref="SimitoneSurroundingsPlan"/> and
    /// <see cref="SimitoneSurroundingsRestore"/> as the lot view uses them - a test that re-derived
    /// the placement would pass against a broken placement. The one thing this does not do is run a
    /// <c>VM</c>, which is why the field is asserted as a value the plan carries and the bridge
    /// copies: that is the seam the merge went through.</para>
    /// </summary>
    public class SimitoneSurroundingsGroundMergeTests
    {
        private const int MapSize = NeighbourhoodCityMeshBuilder.MapSize;
        private const int LotSize = 64;
        private const float Scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;

        private static NeighbourhoodLot Lot(short number, int worldX, int worldY, int size = LotSize)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                WorldX = worldX,
                WorldY = worldY,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = 0xABCD,
                HasHouse = true
            };
        }

        private static NeighbourhoodMap FlatMap(int elevation)
        {
            var map = NeighbourhoodMap.Create(MapSize);
            for (int i = 0; i < map.ElevationData.Length; i++)
            {
                map.ElevationData[i] = (byte)elevation;
                map.TerrainType[i] = NeighbourhoodTerrain.Grass;
            }
            return map;
        }

        /// <summary>
        /// A map with a gradient in it, because a flat map is the case where two different readings
        /// of it agree to within a third of a unit and the bug is invisible. Six elevation bytes
        /// across the two lots is a gentle slope - about a fifth of a byte per tile - and is the
        /// shape of a real neighbourhood rather than the shape of a test. Terrain type is stamped
        /// grass everywhere because <c>PopulateBlankTerrain</c> and the road band both index it.
        /// </summary>
        private static NeighbourhoodMap SlopedMap(int baseElevation)
        {
            var map = FlatMap(baseElevation);
            for (int z = 0; z < MapSize; z++)
            {
                for (int x = 0; x < MapSize; x++)
                {
                    //two sixths of a byte a tile east and a third of one south, so both axes of the
                    //gradient are real and no lot in the fixture is perfectly level
                    var rise = (x - 200) / 3 + (z - 300) / 6;
                    var value = baseElevation + rise;
                    if (value < 0) value = 0;
                    if (value > 255) value = 255;
                    map.ElevationData[map.Index(x, z)] = (byte)value;
                }
            }
            return map;
        }

        /// <summary>
        /// A street along one row, which is what reachability is answered against: a lot's door tile
        /// has to be on a road. The fixture's three lots all front this one, so all three are
        /// mutually reachable and the plan has neighbours to talk about.
        /// </summary>
        private static void MarkStreet(NeighbourhoodMap map, int z)
        {
            for (int x = 0; x < map.Width; x++) map.RoadData[map.Index(x, z)] = RoadMask.All;
        }

        private static SimitoneNeighbourTerrain Terrain(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            SimitoneNeighbourTerrain terrain;
            string reason;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map, lot, out terrain, out reason), reason);
            return terrain;
        }

        /// <summary>
        /// The three-lot fixture: the player's lot at the packer's own pitch from its eastern
        /// neighbour, and a northern one at the same pitch, on a map with a gradient in it.
        ///
        /// <para>
        /// With <paramref name="pinToMedian"/> the lot footprints and their one-tile border rings
        /// are then pinned to each lot's own quantised median, which is what
        /// <see cref="TerrainSeamSolver"/>'s Dirichlet stage does to a real generated map. The
        /// difference matters to the real-height-field tests and to nothing else: a hand-built
        /// gradient runs straight through a lot's rectangle, so the map's answer for a lot's own
        /// ground is graded even without a pin, and "the map says the lot is flat" would not be
        /// saying anything.
        /// </para>
        /// </summary>
        private static NeighbourhoodMap Street(
            out NeighbourhoodLot here, out NeighbourhoodLot east, out NeighbourhoodLot north,
            bool pinToMedian = false)
        {
            var map = SlopedMap(48);
            here = Lot(68, 200, 300);
            east = Lot(69, 200 + LotSize + NeighbourhoodLayoutBuilder.SidewalkMargin, 300);
            north = Lot(70, 200, 300 - LotSize - NeighbourhoodLayoutBuilder.SidewalkMargin);
            //One street along the row the two southern lots front, and a column up the western edge
            //to the northern one, so all three doors are on tarmac and the three lots are one
            //component. Reachability is answered by a lot's door tile being on a road, so without
            //this the plan is valid and empty - which is the right answer for a neighbourhood with
            //no street and no answer for anything else.
            MarkStreet(map, here.WorldY + LotSize);
            for (var z = north.WorldY; z <= here.WorldY + LotSize; z++)
            {
                map.RoadData[map.Index(here.WorldX, z)] = RoadMask.North | RoadMask.South;
            }
            if (pinToMedian) PinToMedian(map, here, east, north);
            return map;
        }

        /// <summary>
        /// Each lot's own footprint and its one-tile border ring, held at that lot's quantised median
        /// - <see cref="TerrainSeamSolver"/> stages 2 and 3, reproduced so a test can say "the
        /// generated map's answer for this lot's own ground is a plane" and mean it.
        /// </summary>
        private static void PinToMedian(NeighbourhoodMap map, params NeighbourhoodLot[] lots)
        {
            foreach (var lot in lots)
            {
                var height = NeighbourhoodMapAdapter.QuantiseAltitude(lot.BaseHeight);
                for (var z = Math.Max(0, lot.WorldY - 1);
                     z < Math.Min(map.Height, lot.WorldY + lot.Size + 1); z++)
                {
                    for (var x = Math.Max(0, lot.WorldX - 1);
                         x < Math.Min(map.Width, lot.WorldX + lot.Size + 1); x++)
                    {
                        map.ElevationData[map.Index(x, z)] = height;
                    }
                }
            }
        }

        /// <summary>
        /// The fixture's plan. Reachability is stated through a query with the three lots' doors on
        /// one street, for the reason <c>SimitoneSurroundingsPlanTests</c> gives: what is under test
        /// is the plan's use of the answer, not how the road graph derives it - and a plan with no
        /// neighbours is a valid plan that would test nothing here.
        ///
        /// <para>
        /// <b>Every lot pinned to its median, by name.</b> This file is about the merge - the field
        /// being the surround's own ground corner for corner - and the merge holds for a lot whose
        /// ground is the generated map's pin. A lot the plan could grade from its own
        /// <c>ARRY(0)</c> plane deliberately does not merge, because its field is then the lot's own
        /// real grading; see the note on
        /// <see cref="ASubWorldsGroundIsTheSurroundsGroundOnEveryCornerOfItsLot"/> and
        /// <c>NeighbourArryGroundTests</c> for that half. Naming <see cref="SimitoneArryPlanes.Pinned"/>
        /// rather than relying on the plan's default also stops these tests from changing behaviour
        /// on a machine that happens to have a Sims install.
        /// </para>
        /// </summary>
        private static SimitoneSurroundingsPlan Plan(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map, NeighbourhoodLayout layout)
        {
            var query = new NeighbourhoodQuery(layout, map, new[]
            {
                //each lot's door on the street the fixture drew, on the edge facing it
                new NeighbourhoodEntrance(68, 232, 364, 0, 1),
                new NeighbourhoodEntrance(69, 298, 364, 0, 1),
                new NeighbourhoodEntrance(70, 200, 236, 1, 0)
            });
            return SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout, (LotStore)null,
                SimitoneSurroundingsPlacement.CullRadiusTiles,
                SimitoneSurroundingsPlan.MaxSubWorlds, SimitoneArryPlanes.Pinned());
        }

        private static int BaseAltFor(
            SimitoneSurroundingsPlan plan, SimitoneSurroundingsNeighbour neighbour)
        {
            return SimitoneSurroundingsRestore.BaseAltitudeFor(plan.ActiveBaseLevel, neighbour.BaseLevel);
        }

        // ---- the rectangles -----------------------------------------------------------------------

        [Fact]
        public void TheActiveLotTheSubWorldAndTheSurroundAreTheSameRectangle()
        {
            // The three rectangles that have to be one. The lot view draws its own lot as
            // x[0, size * 3) z[0, size * 3) from the blueprint's tile (0, 0); the surround draws the
            // active lot's packed rectangle through ToLotView; and a neighbour is a sub-world whose
            // own tile (0, 0) lands at GlobalPosition * 3 while the surround draws that lot's packed
            // rectangle through the same ToLotView. If any of the three is a different rectangle,
            // the surround's filler tiles are standing somewhere other than the lot they were drawn
            // for, and no height question is even worth asking.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);

            //the lot the player is standing in: the surround's copy of it starts at the origin
            var hereMin = placement.ToLotView(new Vector3(here.WorldX, 0f, here.WorldY));
            Assert.Equal(0f, hereMin.X, 3);
            Assert.Equal(0f, hereMin.Z, 3);
            Assert.Equal(LotSize * Scale, here.Size * Scale, 3);

            var plan = Plan(placement, map, Layout(here, east, north));
            Assert.True(plan.IsValid, plan.Failure);
            Assert.True(plan.Neighbours.Count >= 2,
                "the fixture needs both neighbours: " + plan.Describe());

            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                var neighbour = plan.Neighbours[i];
                var lot = neighbour.Lot;

                //where the engine will put the sub-world's own tile (0, 0)
                var engine = new Vector2(neighbour.SubWorldPosition.X * Scale,
                    neighbour.SubWorldPosition.Y * Scale);
                //and where the surround puts the same lot's packed corner
                var surround = placement.ToLotView(new Vector3(lot.WorldX, 0f, lot.WorldY));
                Assert.True(engine.X == surround.X && engine.Y == surround.Z,
                    "lot " + lot.Number + ": the sub-world lands at " + engine
                    + " and the surround draws its rectangle at " + new Vector2(surround.X, surround.Z)
                    + ", a delta of " + (engine.X - surround.X) + ","
                    + (engine.Y - surround.Z) + " units");

                //and the far corner, so a size disagreement cannot hide behind a matching origin
                var engineFar = new Vector2(
                    (neighbour.SubWorldPosition.X + neighbour.SubWorldSize) * Scale,
                    (neighbour.SubWorldPosition.Y + neighbour.SubWorldSize) * Scale);
                var surroundFar = placement.ToLotView(
                    new Vector3(lot.WorldX + lot.Size, 0f, lot.WorldY + lot.Size));
                Assert.True(engineFar.X == surroundFar.X && engineFar.Y == surroundFar.Z,
                    "lot " + lot.Number + ": the far corners are at " + engineFar
                    + " and " + new Vector2(surroundFar.X, surroundFar.Z) + ", so the two rectangles"
                    + " are " + (neighbour.SubWorldSize - lot.Size) + " tile(s) different a side");

                //the sign, which is the one thing a negation would break
                Assert.Equal(lot.WorldX - here.WorldX, neighbour.SubWorldPosition.X);
                Assert.Equal(lot.WorldY - here.WorldY, neighbour.SubWorldPosition.Y);
            }
        }

        // ---- the ground, which is the half that was wrong ------------------------------------------

        [Fact]
        public void ASubWorldsGroundIsTheSurroundsGroundOnEveryCornerOfItsLot()
        {
            // The invariant - and it is an invariant of the *pinned* path, which is why it is worth
            // stating in those words now.
            //
            // Every corner of every neighbour's lot, asked both ways: the surround's own ground
            // through ToLotView, and the sub-world's own ground through the lot view's real
            // conversion - (altitude - BaseAlt) * TerrainFactor * 3, which is
            // TerrainComponent.GetElevationPoint less the BaseAlt its world matrix subtracts.
            //
            // A gradient in the map, because this is the assertion that has teeth. On a flat map two
            // different readings of it agree to within a third of a unit and everything looks fine.
            //
            // <b>What changed, and what this test now pins.</b> When the plan can read a
            // neighbour's own ARRY(0) plane - `SimitoneArryPlanes`, which the shipped path does -
            // that neighbour's field is the lot's real per-tile ground measured from its own median,
            // so this equality is deliberately false inside that lot's property line and the
            // difference is exactly the lot's own grading. That is the owner's "height maps of
            // surrounding lots are incorrect", fixed; `NeighbourArryGroundTests` covers the graded
            // half and the median rule that keeps S1 off the neighbours. This test therefore holds
            // the pinned answer, and it is unchanged behaviour for every lot whose plane could not
            // be read - which is what makes the fix a fix rather than a re-shuffle.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var plan = Plan(placement, map, Layout(here, east, north));
            Assert.True(plan.IsValid, plan.Failure);

            var checkedCorners = 0;
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                var neighbour = plan.Neighbours[i];
                Assert.True(neighbour.HasMergedGround,
                    "lot " + neighbour.Lot.Number + " has no ground of its own to be built on");

                var baseAlt = BaseAltFor(plan, neighbour);
                var lot = neighbour.Lot;
                var size = neighbour.SubWorldSize;
                Assert.Equal(size, lot.Size);

                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var drawn = placement.ToLotView(new Vector3(lot.WorldX + x,
                            NeighbourhoodCityMeshBuilder.WorldHeightFor(
                                map.ElevationData[map.Index(lot.WorldX + x, lot.WorldY + z)]),
                            lot.WorldY + z)).Y;
                        var subWorld = (neighbour.AltitudeAt(x, z) - baseAlt)
                            * SimitoneNeighbourTerrain.LotTerrainFactor * Scale;
                        Assert.True(Math.Abs(subWorld - drawn)
                                <= SimitoneSurroundingsRestore.SubWorldGroundTolerance,
                            "lot " + lot.Number + " at its own tile (" + x + "," + z
                            + "): the sub-world's ground is at " + subWorld
                            + " units and the surround's at " + drawn + ", a step of "
                            + Math.Abs(subWorld - drawn) + " units");
                        checkedCorners++;
                    }
                }
            }
            Assert.True(checkedCorners >= 2 * LotSize * LotSize,
                "both neighbours' whole rectangles were measured, and it says otherwise: "
                + checkedCorners);
        }

        [Fact]
        public void TheMergeIsTheOneQuestionEveryPlacementAsksTheSameWay()
        {
            // The point of doing the merge in the placement rather than in the bridge: the field is
            // written by the same function the surround's own heights are written by, so "the
            // sub-world's ground is the surround's ground" is true by construction and not by two
            // pieces of arithmetic agreeing. Here it is asked for one corner of one lot, by both
            // routes, and the answers are the same number.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var plan = Plan(placement, map, Layout(here, east, north));
            Assert.True(plan.IsValid, plan.Failure);

            var neighbour = plan.Neighbours[0];
            var lot = neighbour.Lot;
            var baseAlt = BaseAltFor(plan, neighbour);

            for (int z = 0; z < neighbour.SubWorldSize; z += 7)
            {
                for (int x = 0; x < neighbour.SubWorldSize; x += 7)
                {
                    var direct = SimitoneSurroundingsPlacement.SubWorldAltitudeFor(
                        map, placement, baseAlt, lot.WorldX + x, lot.WorldY + z);
                    Assert.Equal(direct, neighbour.AltitudeAt(x, z));
                }
            }

            //and the field is a rectangle of the sub-world's own size, in its own array order
            Assert.Equal(neighbour.SubWorldSize * neighbour.SubWorldSize,
                neighbour.SubWorldAltitudes.Length);
        }

        [Fact]
        public void AMergedGroundIsNotTheHelpersOwnFourByFourInterpolation()
        {
            // The old field and the new one are different functions of the same map, and this is
            // what separates them: the helpers read sixteen samples of round(elevation / 22.5) and
            // interpolate between them, so a single tile's worth of elevation inside a lot moves
            // nothing in their field. The surround reads the per-tile raster, so it moves four
            // corners. A spike in the middle of a neighbour's lot is therefore the sharpest possible
            // witness that the ground under a real lot is the generated map's own and not a
            // resampling of it.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var plan = Plan(placement, map, Layout(here, east, north));
            Assert.True(plan.IsValid, plan.Failure);

            var neighbour = plan.Neighbours[0];
            var lot = neighbour.Lot;
            var baseAlt = BaseAltFor(plan, neighbour);
            var spikeX = lot.WorldX + lot.Size / 2;
            var spikeZ = lot.WorldY + lot.Size / 2;

            var before = neighbour.AltitudeAt(lot.Size / 2, lot.Size / 2);
            var besideBefore = neighbour.AltitudeAt(lot.Size / 2 + 1, lot.Size / 2);

            //one tile, raised by twenty-five elevation bytes - a quarter of a tile of world height,
            //and a whole helper height byte is 100 altitude units
            var original = map.ElevationData[map.Index(spikeX, spikeZ)];
            map.ElevationData[map.Index(spikeX, spikeZ)] =
                (byte)Math.Min(255, original + 25);
            var after = SimitoneSurroundingsPlacement.SubWorldAltitudeFor(
                map, placement, baseAlt, spikeX, spikeZ);
            var besideAfter = SimitoneSurroundingsPlacement.SubWorldAltitudeFor(
                map, placement, baseAlt, spikeX + 1, spikeZ);

            Assert.True(after - before > 50,
                "one raised tile has to move the merged ground by more than a helper height byte"
                + " (100 altitude units): it moved by " + (after - before));
            //And the tile beside it has to stay where it was, which is the sharper half. A field
            //interpolated from sixteen samples has no such tile: the helpers' bicubic smears a
            //one-tile feature across the whole lot and cannot represent a step at all, and the
            //surround's per-tile raster can represent nothing else. So the two disagree on the
            //neighbouring corner by however much they disagree on the raised one - which is the
            //step a player was looking at.
            Assert.True(Math.Abs(besideAfter - besideBefore) < 50,
                "the tile beside the raised one is not raised, so its corner must not move: it moved"
                + " by " + (besideAfter - besideBefore));
        }

        // ---- a neighbour's real height field ------------------------------------------------------

        /// <summary>
        /// A lot's own decoded <c>ARRY(0)</c> plane, sloping east across the lot: zero at its west
        /// edge and <paramref name="rise"/> units at its east edge, so it is the graded ground the
        /// generated map's Dirichlet pin throws away.
        /// </summary>
        private static SimitoneArryAltitude SlopedPlane(int size, int rise)
        {
            var plane = new byte[size * size];
            for (var z = 0; z < size; z++)
            {
                for (var x = 0; x < size; x++)
                {
                    plane[z * size + x] = (byte)Math.Min(255,
                        (x * rise) / Math.Max(1, size - 1));
                }
            }
            return new SimitoneArryAltitude(plane, size);
        }

        [Fact]
        public void ALotsOwnHeightFieldGradesItWhereTheGeneratedMapsPinIsFlat()
        {
            // The owner's second complaint: "height maps of surrounding lots are incorrect".
            // TerrainSeamSolver pins every tile of a lot's footprint to that lot's median ARRY(0),
            // so the map's answer for a lot's own ground is a plane - correct as a boundary
            // condition, and the wrong answer to "what shape is this lot". With the lot's own plane
            // in hand the 4x4 grid is graded and BaseLevel moves with it.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north, pinToMedian: true);
            var lot = east;
            var arry = SlopedPlane(lot.Size, 40);

            SimitoneNeighbourTerrain flat, graded;
            string reason;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map, lot, out flat, out reason), reason);
            Assert.True(SimitoneNeighbourTerrain.TryBuild(
                map, lot, arry, out graded, out reason), reason);

            Assert.False(flat.IsGraded);
            Assert.True(graded.IsGraded);

            //the flat path is sixteen copies of one sample, by construction
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    Assert.Equal(flat.HeightAt(0, 0), flat.HeightAt(x, y));
                }
            }

            //and the real one is not: a lot that rises forty ARRY(0) units across itself is seven
            //and a half tiles of map height, which is a slope no pin could have produced
            Assert.True(graded.HeightAt(3, 0) > graded.HeightAt(0, 0),
                "the east edge of the grid reads higher than the west edge: "
                + graded.HeightAt(0, 0) + " -> " + graded.HeightAt(3, 0));
            Assert.True(graded.BaseLevel > flat.BaseLevel,
                "and the middle of that ground is above the pinned median the map carries: "
                + flat.BaseLevel + " -> " + graded.BaseLevel);

            //A field is not a licence to guess: the road byte, the blend and the corner arithmetic
            //are the same answers on both paths, because both are about the lot's edges.
            Assert.Equal(flat.Roads, graded.Roads);
            Assert.Equal(flat.Blend.Base, graded.Blend.Base);
            Assert.Equal(flat.Blend.AdjFlags, graded.Blend.AdjFlags);

            //and "unavailable" is a value, not a failure: a caller with no plane gets the flat path
            //rather than a refusal
            SimitoneNeighbourTerrain none;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(
                map, lot, SimitoneArryAltitude.None, out none, out reason), reason);
            Assert.False(none.IsGraded);
            Assert.Equal(flat.BaseLevel, none.BaseLevel);
        }

        [Fact]
        public void AFlatHeightFieldIsTheSameFieldTheGeneratedMapPins()
        {
            // The degradation the real field has to have. A lot whose plane is one value is the
            // same lot the pin describes, so a caller that has read a house file and a caller that
            // has not must not disagree about a flat lot - or every neighbour would jump as soon
            // as its house file was read.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north, pinToMedian: true);
            var lot = east;
            var median = NeighbourhoodMapAdapter.QuantiseAltitude(lot.BaseHeight);
            var plane = new byte[lot.Size * lot.Size];
            Array.Fill(plane, (byte)lot.BaseHeight);

            //The map's own tiles over this lot are the pin, and the pin is the quantised median -
            //which is the whole reason a caller with a house file and a caller without one have to
            //agree on a flat lot rather than disagree about every lot as its file is read.
            Assert.Equal(median, map.ElevationData[map.Index(lot.WorldX, lot.WorldY)]);

            SimitoneNeighbourTerrain fromMap, fromPlane;
            string reason;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map, lot, out fromMap, out reason), reason);
            Assert.True(SimitoneNeighbourTerrain.TryBuild(
                map, lot, new SimitoneArryAltitude(plane, lot.Size),
                out fromPlane, out reason), reason);

            Assert.False(fromMap.IsGraded);
            Assert.True(fromPlane.IsGraded);
            Assert.Equal(fromMap.BaseLevel, fromPlane.BaseLevel);
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    Assert.Equal(fromMap.HeightAt(x, y), fromPlane.HeightAt(x, y));
                }
            }
        }

        [Fact]
        public void AGradedSubWorldFieldIsMeasuredFromThatLotsOwnMedian()
        {
            // The merge with a real field. SubWorldAltitudesFor is what the bridge copies over the
            // sub-world's own Terrain.Heights, so it IS the neighbour's drawn ground - and the
            // median is subtracted because an ARRY(0) altitude is absolute while the field it feeds
            // is relative. Without the subtraction a graded lot would be lifted by its whole own
            // altitude, which is the S1 defect moved from the player's lot to its neighbours.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var lot = east;
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var arry = SlopedPlane(lot.Size, 40);

            SimitoneNeighbourTerrain active, own;
            string reason;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map, here, out active, out reason), reason);
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map, lot, out own, out reason), reason);
            var baseAlt = SimitoneSurroundingsRestore.BaseAltitudeFor(active.BaseLevel, own.BaseLevel);

            var flat = SimitoneSurroundingsPlacement.SubWorldAltitudesFor(
                map, placement, lot, lot.Size, baseAlt);
            var graded = SimitoneSurroundingsPlacement.SubWorldAltitudesFor(
                map, placement, lot, lot.Size, baseAlt, arry);
            Assert.NotNull(flat);
            Assert.NotNull(graded);
            Assert.Equal(flat.Length, graded.Length);

            //the graded field moves: its own west and east edges are a real slope apart
            var west = graded[0];
            var eastEdge = graded[lot.Size - 1];
            Assert.True(eastEdge - west > 200,
                "forty ARRY(0) units across the lot is seven and a half tiles of map height, which"
                + " is eight times the helper height byte - it moved by " + (eastEdge - west));

            //And the plane it is measured from is the surround's, with that lot's own median taken
            //out: the sub-world draws at (altitude - BaseAlt) * TF * 3, and the field is written so
            //that a corner whose own ARRY(0) equals the lot's median lands exactly on the level the
            //surround is measured to. Computed from the arithmetic rather than asserted as a
            //number, so the test fails if either side of the conversion moves.
            const int centreLocal = LotSize / 2;
            var centre = graded[centreLocal * lot.Size + centreLocal];
            var centreArry = arry.At(centreLocal, centreLocal);
            var expectedWorld = (
                NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(centreArry - arry.Median(lot.Size))
                - placement.MapTileOrigin.Y) * Scale;
            var centreWorld = (centre - baseAlt) * SimitoneNeighbourTerrain.LotTerrainFactor * Scale;
            Assert.True(Math.Abs(centreWorld - expectedWorld) < 0.05f,
                "the field is the lot's own height measured from its own median, on the surround's"
                + " plane: " + centreWorld + " units against the arithmetic's "
                + expectedWorld + " (ARRY(0) " + centreArry + " against a median of "
                + arry.Median(lot.Size) + ")");

            //Without the median taken out the lot would be lifted by its own altitude, which is
            //precisely the S1 defect moved from the player's lot to its neighbours
            var unanchoredWorld = (centre - baseAlt
                    + arry.Median(lot.Size)
                        * NeighbourhoodCityMeshBuilder.LotAltitudeUnitsPerArryUnit)
                * SimitoneNeighbourTerrain.LotTerrainFactor * Scale;
            Assert.True(Math.Abs(unanchoredWorld - centreWorld) > 5f,
                "and the median really is what holds it down: taking it out would move the lot by "
                + Math.Abs(unanchoredWorld - centreWorld) + " units");

            //A field that is not a field is the map's field: the default overload and an explicit
            //"none" are the same array, which is what keeps every existing caller unchanged.
            Assert.Equal(flat, SimitoneSurroundingsPlacement.SubWorldAltitudesFor(
                map, placement, lot, lot.Size, baseAlt, SimitoneArryAltitude.None));
        }

        [Fact]
        public void AFlatNeighbourhoodIsFlatInBothGroundsAndNotOneStepApart()
        {
            // The case a player in a flat neighbourhood would have seen least of and cared most
            // about: two readings of one flat map must produce one surface, not two parallel ones a
            // third of a unit apart.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var plan = Plan(placement, map, Layout(here, east, north));
            Assert.True(plan.IsValid, plan.Failure);

            var neighbour = plan.NeighbourFor(69);
            Assert.True(neighbour.IsValid, "the eastern neighbour is in the plan");
            var baseAlt = BaseAltFor(plan, neighbour);
            Assert.Equal(0, baseAlt);

            //the sub-world's own local zero is the surround's ground everywhere on a flat map
            var first = neighbour.AltitudeAt(0, 0);
            for (int z = 0; z < neighbour.SubWorldSize; z++)
            {
                for (int x = 0; x < neighbour.SubWorldSize; x++)
                {
                    Assert.Equal(first, neighbour.AltitudeAt(x, z));
                }
            }
            Assert.True(Math.Abs(first - baseAlt)
                    <= SimitoneSurroundingsRestore.SubWorldGroundTolerance,
                "and it sits on the surround's ground, which is at "
                + placement.ToLotView(new Vector3(neighbour.Lot.WorldX, 48f / 12f,
                    neighbour.Lot.WorldY)).Y + " units; local zero is at "
                + (first - baseAlt) * SimitoneNeighbourTerrain.LotTerrainFactor * Scale);
        }

        // ---- what the merge must not disturb ------------------------------------------------------

        [Fact]
        public void TheMergeChangesNoPlacementTheRoadBandOrTheFlagOffPath()
        {
            // The merge is a height field. Everything horizontal - the sub-world's rectangle, the
            // band the sub-world stamps and the band the surround draws, the selection, the budget -
            // is the same arithmetic it was, and the flag-off path never reaches any of it.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var layout = Layout(here, east, north);

            var plan = Plan(placement, map, layout);
            Assert.True(plan.IsValid, plan.Failure);

            //the road band is still the lot view's own, on the lot's own tiles
            var neighbour = plan.NeighbourFor(69);
            var lot = neighbour.Lot;
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = SimitoneLotViewRoadBand.For(map, LotsOf(plan),
                new Rectangle(cull.MinX, cull.MinZ, cull.WidthTiles, cull.HeightTiles));
            var z = lot.WorldY + 30;
            for (var i = 1; i <= SimitoneLotViewRoad.TarmacTiles; i++)
            {
                var worldX = (neighbour.SubWorldPosition.X + i) * Scale;
                var drawn = placement.ToLotView(new Vector3(lot.WorldX + i, 0f, z)).X;
                Assert.Equal(worldX, drawn, 3);
            }
            //and the pavement the sub-world stamps on its own tile 5 is on the same world tile as
            //the band's, which is the other half of the horizontal invariant
            var pavement = SimitoneLotViewRoad.PavementOffsetFor(
                SimitoneLotViewRoad.RoadByteFor(map, lot), SimitoneNeighbourTerrain.RoadWest);
            if (pavement > 0)
            {
                Assert.True(band.IsPavement(lot.WorldX + pavement, z)
                    || band.IsTarmac(lot.WorldX + pavement, z),
                    "the band holds street on the neighbour's west edge at its own tile " + pavement);
                Assert.Equal((neighbour.SubWorldPosition.X + pavement) * Scale,
                    placement.ToLotView(new Vector3(lot.WorldX + pavement, 0f, z)).X, 3);
            }

            //the selection is unaffected: every neighbour is still inside the drawn region
            Assert.True(plan.SubWorldsInside(cull), plan.Describe());
            Assert.True(plan.RadiusSharedWith(cull));

            //and the flag-off path is a pure decision that never gets here
            var off = SimitoneSurroundingsSelection.For(NeighbourhoodView.Off, 1);
            Assert.False(off.Enabled);
            var noLots = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 0);
            Assert.False(noLots.Enabled);
        }

        [Fact]
        public void ASubWorldWithNoMapStillHasNoGroundRatherThanAGuessedOne()
        {
            // The refusal, which is the same one the rest of the file makes: a neighbour the map
            // cannot answer about is skipped, and the field the bridge merges is null rather than a
            // zero plane, which would put the lot a whole base level out.
            var placement = SimitoneSurroundingsPlacement.For(FlatMap(48), Lot(68, 200, 300));
            Assert.Null(SimitoneSurroundingsPlacement.SubWorldAltitudesFor(
                null, placement, Lot(69, 266, 300), LotSize, 0));
            Assert.Null(SimitoneSurroundingsPlacement.SubWorldAltitudesFor(
                FlatMap(48), placement, null, LotSize, 0));
            Assert.Null(SimitoneSurroundingsPlacement.SubWorldAltitudesFor(
                FlatMap(48), placement, Lot(69, 266, 300), 0, 0));

            //and the default neighbour has no ground, so AltitudeAt cannot walk off one
            var none = default(SimitoneSurroundingsNeighbour);
            Assert.False(none.HasMergedGround);
            Assert.Equal(0, none.AltitudeAt(3, 3));
        }

        // ---- the fixture ---------------------------------------------------------------------------

        private static NeighbourhoodLayout Layout(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(1, 12345,
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, 1, MapSize, lots);
        }

        private static NeighbourhoodLot[] LotsOf(SimitoneSurroundingsPlan plan)
        {
            var lots = new NeighbourhoodLot[plan.Neighbours.Count];
            for (int i = 0; i < lots.Length; i++) lots[i] = plan.Neighbours[i].Lot;
            return lots;
        }
    }
}

