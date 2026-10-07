/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using FSO.SimAntics.Marshals.Hollow;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// A surrounding lot stands on its own ground.
    ///
    /// <para><b>The defect.</b> "Height maps of surrounding lots are incorrect." A generated
    /// neighbourhood pins every tile of a lot's footprint <em>and its one-tile border ring</em> to
    /// that lot's median <c>ARRY(0)</c> altitude - <see cref="TerrainSeamSolver"/>'s Dirichlet
    /// stage, which is a correct boundary condition - so the map's answer for a lot's own ground is
    /// flat. The lot the player is standing in is drawn by the lot view from its own
    /// <c>ARRY(0)</c>, so before the plane was threaded every lot in the street was a plane and one
    /// lot in the middle of it was graded. The real plane is in the lot's own <c>HouseNN.iff</c> and
    /// nowhere else, so somebody has to read the file.</para>
    ///
    /// <para><b>What is asserted, and by which half.</b> <see cref="SimitoneNeighbourTerrain"/> and
    /// <see cref="SimitoneSurroundingsPlacement"/> already own "a real field grades the sixteen
    /// samples" and "a real field is measured from its own median", and their tests assert those.
    /// This file is the seam above them and below: <see cref="SimitoneArryPlanes"/> hands a plane to
    /// <see cref="SimitoneSurroundingsPlan.ForQuery"/>, the plan carries it on every
    /// <see cref="SimitoneSurroundingsNeighbour"/>, and
    /// <see cref="SimitoneSurroundingsRestore"/> - which has no generated map and no content manager
    /// - can still say which neighbours are graded and which are pinned.</para>
    ///
    /// <para><b>Nothing here opens a house file.</b> The plan's source is a parameter, so a test
    /// supplies a known plane and the assertion is about the plan rather than about an install. The
    /// file reading itself is exercised separately, on the decode and the refusals, because those
    /// are the only halves of it that can be reached without The Sims.</para>
    /// </summary>
    public sealed class NeighbourArryGroundTests
    {
        private const int MapSize = NeighbourhoodCityMeshBuilder.MapSize;
        private const int LotSize = 64;
        private const float Scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;

        /// <summary>
        /// The eastern neighbour's rise across its own footprint, in <c>ARRY(0)</c> units. Forty is
        /// seven and a half tiles of map height - a slope no median pin could have produced and one
        /// no quantised elevation byte can hide.
        /// </summary>
        private const int Rise = 40;

        // ==================================================================== the fixture

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

        /// <summary>
        /// A map with a gradient in it, because a flat map is the case where the pinned path and the
        /// graded one agree to within a rounding and the defect is invisible. Grass everywhere,
        /// because the road band indexes the terrain type.
        /// </summary>
        private static NeighbourhoodMap SlopedMap()
        {
            var map = NeighbourhoodMap.Create(MapSize);
            for (int z = 0; z < MapSize; z++)
            {
                for (int x = 0; x < MapSize; x++)
                {
                    var rise = (x - 200) / 3 + (z - 300) / 6;
                    var value = 48 + rise;
                    if (value < 0) value = 0;
                    if (value > 255) value = 255;
                    map.ElevationData[map.Index(x, z)] = (byte)value;
                    map.TerrainType[map.Index(x, z)] = NeighbourhoodTerrain.Grass;
                }
            }
            return map;
        }

        /// <summary>
        /// The player's lot, an eastern neighbour at the packer's pitch and a northern one at the
        /// same pitch, on one street - so all three doors are on tarmac, all three are mutually
        /// reachable and the plan has neighbours to talk about.
        /// </summary>
        private static NeighbourhoodMap Street(
            out NeighbourhoodLot here, out NeighbourhoodLot east, out NeighbourhoodLot north)
        {
            var map = SlopedMap();
            here = Lot(68, 200, 300);
            east = Lot(69, 200 + LotSize + NeighbourhoodLayoutBuilder.SidewalkMargin, 300);
            north = Lot(70, 200, 300 - LotSize - NeighbourhoodLayoutBuilder.SidewalkMargin);

            //the street along the row the two southern lots front...
            for (int x = 0; x < map.Width; x++)
            {
                map.RoadData[map.Index(x, here.WorldY + LotSize)] = RoadMask.All;
            }
            //...and a column up the western edge to the northern one
            for (int z = north.WorldY; z <= here.WorldY + LotSize; z++)
            {
                map.RoadData[map.Index(here.WorldX, z)] = RoadMask.All;
            }

            //And the pin, which is what makes this fixture a reproduction rather than a slope:
            //every lot's footprint and border ring held at its own quantised median, exactly as
            //TerrainSeamSolver leaves it on a real generated map.
            PinToMedian(map, here, east, north);
            return map;
        }

        /// <summary>
        /// <see cref="TerrainSeamSolver"/> stages 2 and 3, reproduced so a test can say "the
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

        private static NeighbourhoodLayout Layout(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(1, 12345,
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, 1, MapSize, lots);
        }

        private static SimitoneSurroundingsPlan Plan(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map,
            NeighbourhoodLayout layout, SimitoneArryPlanes planes)
        {
            var query = new NeighbourhoodQuery(layout, map, new[]
            {
                new NeighbourhoodEntrance(68, 232, 364, 0, 1),
                new NeighbourhoodEntrance(69, 298, 364, 0, 1),
                new NeighbourhoodEntrance(70, 200, 236, 1, 0)
            });
            return SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout,
                (LotStore)null, SimitoneSurroundingsPlacement.CullRadiusTiles,
                SimitoneSurroundingsPlan.MaxSubWorlds, planes);
        }

        /// <summary>
        /// A lot's own decoded <c>ARRY(0)</c> plane, sloping east across the lot: zero at its west
        /// edge and <paramref name="rise"/> units at its east edge.
        /// </summary>
        private static SimitoneArryAltitude SlopedPlane(int size, int rise)
        {
            var plane = new byte[size * size];
            for (var z = 0; z < size; z++)
            {
                for (var x = 0; x < size; x++)
                {
                    plane[z * size + x] = (byte)Math.Min(255, (x * rise) / Math.Max(1, size - 1));
                }
            }
            return new SimitoneArryAltitude(plane, size);
        }

        /// <summary>
        /// A source that hands <paramref name="forLots"/> a sloped plane and refuses every other lot,
        /// counting what it was asked. The refusal is the point: "one lot has no readable house file"
        /// has to be a state a test can reach, because it is a state a player reaches.
        /// </summary>
        private static SimitoneArryPlanes PlanesFor(params short[] forLots)
        {
            var wanted = new HashSet<short>(forLots);
            return new SimitoneArryPlanes((lot, out arry, out reason) =>
            {
                if (lot == null || !wanted.Contains(lot.Number))
                {
                    arry = SimitoneArryAltitude.None;
                    reason = "lot " + (lot?.Number ?? 0) + ": the fixture gave this lot no house file,"
                        + " so its ground is the generated map's flat median";
                    return false;
                }
                arry = SlopedPlane(lot.Size, Rise);
                reason = null;
                return true;
            });
        }

        private static int BaseAltFor(
            SimitoneSurroundingsPlan plan, SimitoneSurroundingsNeighbour neighbour)
        {
            return SimitoneSurroundingsRestore.BaseAltitudeFor(
                plan.ActiveBaseLevel, neighbour.BaseLevel);
        }

        // ============================================================ the defect, and its fix

        [Fact]
        public void ASurroundingLotWithAKnownNonFlatArryIsGradedAllTheWayToItsSubWorldGround()
        {
            // The owner's complaint, end to end. The map pins both neighbours' footprints to their
            // own medians, so the pinned plan is a street of planes; hand the plan the real planes
            // and the eastern neighbour's ground is that lot's own graded shape - in the sixteen
            // samples the helpers hang the lot at, and in the size-squared field the bridge copies
            // over the sub-world's Terrain.Heights, which is the ground a player is looking at.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var layout = Layout(here, east, north);

            var pinned = Plan(placement, map, layout, SimitoneArryPlanes.Pinned());
            Assert.True(pinned.IsValid, pinned.Failure);
            var graded = Plan(placement, map, layout, PlanesFor(69, 70));
            Assert.True(graded.IsValid, graded.Failure);

            Assert.Equal(0, pinned.GradedGroundCount);
            Assert.Equal(2, graded.GradedGroundCount);

            var before = pinned.NeighbourFor(69);
            var after = graded.NeighbourFor(69);
            Assert.True(after.IsValid, after.Describe());
            Assert.True(after.IsGradedGround, after.Describe());
            Assert.True(after.Terrain.IsGraded, after.Describe());
            Assert.False(before.IsGradedGround, before.Describe());

            //the sixteen samples are graded rather than sixteen copies of one number, and the east
            //edge is above the west edge - the shape the pin threw away
            Assert.True(after.Terrain.HeightAt(3, 0) > after.Terrain.HeightAt(0, 0),
                "the east edge of the grid reads higher than the west edge: "
                + after.Terrain.HeightAt(0, 0) + " -> " + after.Terrain.HeightAt(3, 0));
            Assert.True(after.BaseLevel > before.BaseLevel,
                "and the middle of that ground is above the pinned median: "
                + before.BaseLevel + " -> " + after.BaseLevel);

            //and so is the field the bridge actually copies over the sub-world's own heights
            var west = after.AltitudeAt(0, 0);
            var eastEdge = after.AltitudeAt(after.SubWorldSize - 1, 0);
            Assert.True(eastEdge - west > 200,
                "forty ARRY(0) units across a lot is seven and a half tiles of map height, which is"
                + " eight helper height bytes - the merged ground moved by " + (eastEdge - west));
            Assert.NotEqual(before.AltitudeAt(0, 0), after.AltitudeAt(0, 0));

            //A field is not a licence to guess: the road byte and the blend are answers about the
            //lot's edges, and both ends of the street seam are unaffected by anything above.
            Assert.Equal(before.Terrain.Roads, after.Terrain.Roads);
            Assert.Equal(before.Terrain.Blend.Base, after.Terrain.Blend.Base);
            Assert.Equal(before.Terrain.Blend.AdjFlags, after.Terrain.Blend.AdjFlags);
        }

        [Fact]
        public void AGradedNeighbourIsMeasuredFromItsOwnMedianSoTheS1DefectDoesNotMoveOntoIt()
        {
            // S1, restated for a neighbour. The player's lot is drawn by the lot view from its own
            // ARRY(0) with Blueprint.AnchorBaseAltitude anchoring it on its own median; a graded
            // neighbour has to land on the same plane relative to the surround, or the whole defect
            // is not fixed - it is relocated onto every lot around the player.
            //
            // Asked as arithmetic rather than as a number: the field is written so that a corner
            // whose own ARRY(0) equals the lot's median sits exactly on the level the surround is
            // measured to, and the surround measures itself from its own plane.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var plan = Plan(placement, map, Layout(here, east, north), PlanesFor(69, 70));

            var neighbour = plan.NeighbourFor(69);
            Assert.True(neighbour.IsGradedGround, neighbour.Describe());
            var arry = neighbour.ArryAltitude;
            Assert.True(arry.IsValid, "the plan carried no plane: " + neighbour.Describe());
            Assert.Equal(LotSize, arry.Side);

            var baseAlt = BaseAltFor(plan, neighbour);
            var centre = LotSize / 2;
            var centreArry = arry.At(centre, centre);
            var expectedWorld = (
                NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(centreArry - arry.Median(LotSize))
                - placement.MapTileOrigin.Y) * Scale;
            var centreWorld = (neighbour.AltitudeAt(centre, centre) - baseAlt)
                * SimitoneNeighbourTerrain.LotTerrainFactor * Scale;
            Assert.True(Math.Abs(centreWorld - expectedWorld) < 0.05f,
                "the field is the lot's own height measured from its own median, on the surround's"
                + " plane: " + centreWorld + " units against the arithmetic's " + expectedWorld
                + " (ARRY(0) " + centreArry + " against a median of " + arry.Median(LotSize) + ")");

            //And the median really is what holds it down: an ARRY(0) altitude is absolute, so taking
            //it out is the only thing standing between "graded" and "lifted by its own altitude".
            var unanchored = (neighbour.AltitudeAt(centre, centre) - baseAlt
                    + arry.Median(LotSize)
                        * NeighbourhoodCityMeshBuilder.LotAltitudeUnitsPerArryUnit)
                * SimitoneNeighbourTerrain.LotTerrainFactor * Scale;
            Assert.True(Math.Abs(unanchored - centreWorld) > 5f,
                "so taking the median out would move this lot by "
                + Math.Abs(unanchored - centreWorld) + " units, which is the S1 defect on a neighbour");
        }

        [Fact]
        public void OnlyTheLotsWithAReadablePlaneAreGradedAndTheActiveLotIsUntouched()
        {
            // Two properties in one, because they are one property. A source that answers one lot
            // and refuses another has to grade exactly the first: a source that graded everything
            // would be inventing ground, and a source that graded nothing would be the defect. And
            // the lot the player is standing in must come out of it byte for byte unchanged - it is
            // the surface everything else is measured against, and the plan reads it from the
            // generated map exactly as it did before planes existed.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var layout = Layout(here, east, north);

            var none = Plan(placement, map, layout, SimitoneArryPlanes.Pinned());
            var one = Plan(placement, map, layout, PlanesFor(69));

            Assert.Equal(1, one.GradedGroundCount);
            Assert.True(one.NeighbourFor(69).IsGradedGround);
            Assert.False(one.NeighbourFor(70).IsGradedGround);

            //the refused lot is the map's answer, unchanged, and it says so
            var refused = one.NeighbourFor(70);
            var wasPinned = none.NeighbourFor(70);
            Assert.Equal(wasPinned.BaseLevel, refused.BaseLevel);
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    Assert.Equal(wasPinned.Terrain.HeightAt(x, y), refused.Terrain.HeightAt(x, y));
                }
            }

            //and the active lot is the same on both plans, graded or not
            Assert.False(one.ActiveTerrain.IsGraded);
            Assert.Equal(none.ActiveTerrain.Describe(), one.ActiveTerrain.Describe());
            Assert.Equal(none.ActiveBaseLevel, one.ActiveBaseLevel);
            Assert.Equal(none.ActiveLot.Number, one.ActiveLot.Number);
        }

        // ============================================================ the pinned path, on purpose

        [Fact]
        public void APinnedNeighbourIsTheMapsFlatPinAndSaysSoThatItIsAPin()
        {
            // <b>This test pins the flat median, deliberately.</b> It is the degradation every lot
            // whose house file cannot be read gets, and it has to stay exactly the old behaviour -
            // flat at the generated map's quantised median over the whole rectangle, sixteen
            // copies of one sample - or reading one lot's file would make its neighbours disagree
            // with a caller that had not managed to. It is also the answer a lot with no house at
            // all gets forever, so it is a shape and not an error.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var plan = Plan(placement, map, Layout(here, east, north), SimitoneArryPlanes.Pinned());

            Assert.Equal(0, plan.GradedGroundCount);
            Assert.True(plan.Neighbours.Count >= 2, plan.Describe());
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                var neighbour = plan.Neighbours[i];
                Assert.False(neighbour.IsGradedGround, neighbour.Describe());
                var flat = neighbour.Terrain.HeightAt(0, 0);
                for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
                {
                    for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                    {
                        Assert.Equal(flat, neighbour.Terrain.HeightAt(x, y));
                    }
                }

                //and the merged field is that lot's own rectangle of one value
                var first = neighbour.AltitudeAt(0, 0);
                for (int z = 0; z < neighbour.SubWorldSize; z++)
                {
                    for (int x = 0; x < neighbour.SubWorldSize; x++)
                    {
                        Assert.Equal(first, neighbour.AltitudeAt(x, z));
                    }
                }
            }

            //the pin is the lot's own quantised median - the same number the layout recorded as its
            //BaseHeight, which is why a graded neighbour and a pinned one cannot disagree about a
            //flat lot
            var lot = east;
            Assert.Equal(NeighbourhoodMapAdapter.QuantiseAltitude(lot.BaseHeight),
                map.ElevationData[map.Index(lot.WorldX, lot.WorldY)]);
            Assert.Contains("pinned to the map's median", plan.NeighbourFor(69).Describe());
        }

        [Fact]
        public void AFlatPlaneAgreesWithThePinnedMedianRatherThanFightingIt()
        {
            // The other half of the degradation. A lot whose own ARRY(0) is one value is the same lot
            // the pin describes, so a caller that has read a house file and a caller that has not
            // must not disagree about it - otherwise every neighbour in the street would jump by its
            // own lot's grading as its file happened to be read.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var layout = Layout(here, east, north);

            var flat = new byte[LotSize * LotSize];
            Array.Fill(flat, (byte)east.BaseHeight);
            var planes = new SimitoneArryPlanes((lot, out arry, out reason) =>
            {
                arry = lot != null && lot.Number == east.Number
                    ? new SimitoneArryAltitude(flat, LotSize)
                    : SimitoneArryAltitude.None;
                reason = arry.IsValid
                    ? null
                    : "lot " + (lot?.Number ?? 0) + ": no plane, so its ground is the generated"
                        + " map's flat median";
                return arry.IsValid;
            });

            var pinned = Plan(placement, map, layout, SimitoneArryPlanes.Pinned());
            var graded = Plan(placement, map, layout, planes);

            var before = pinned.NeighbourFor(69);
            var after = graded.NeighbourFor(69);
            Assert.True(after.IsGradedGround);
            Assert.Equal(before.BaseLevel, after.BaseLevel);
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    Assert.Equal(before.Terrain.HeightAt(x, y), after.Terrain.HeightAt(x, y));
                }
            }
        }

        // ============================================================ where the plane comes from

        [Fact]
        public void TheShippedSourceIsEachLotsOwnHouseFileAndItSaysSoWhenThereIsNotOne()
        {
            // The production route, with no install present. The plan must not need a parameter to
            // ask for its neighbours' real ground - a caller that has never heard of height planes
            // still gets graded neighbours - and the refusal has to name the fallback, because
            // "the surrounding lots are flat" and "the surrounding lots are flat because nothing
            // could read their house files" are different defects and only one of them is a bug.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var plan = Plan(placement, map, Layout(here, east, north), null);

            Assert.NotNull(plan.ArryPlanes);
            Assert.Same(plan.ArryPlanes, plan.WithHollows(null).ArryPlanes);
            //a test host has no Sims install, so every lot is refused - and refused out loud
            Assert.Equal(0, plan.GradedGroundCount);
            Assert.NotEmpty(plan.ArryPlanes.PinReasons);
            for (int i = 0; i < plan.ArryPlanes.PinReasons.Count; i++)
            {
                Assert.Contains("median", plan.ArryPlanes.PinReasons[i]);
            }
            var described = plan.ArryPlanes.Describe();
            Assert.Contains("pinned", described);
            Assert.Contains("median", described);

            //and the bridge, which has neither a map nor a content manager, can still report it
            var heights = SimitoneSurroundingsRestore.DescribeHeights(plan);
            Assert.Contains("ARRY(0)", heights);
            Assert.Contains("pinned median", heights);
            Assert.Contains("median", SimitoneSurroundingsRestore.DescribeHeights(
                Plan(placement, map, Layout(here, east, north), PlanesFor(69, 70))));

            Assert.Contains("0 graded from their own ARRY(0)", plan.Describe());
            Assert.Contains("2 graded from their own ARRY(0)",
                Plan(placement, map, Layout(here, east, north), PlanesFor(69, 70)).Describe());
        }

        [Fact]
        public void AChangedHouseFileIsReReadAndAnUnchangedOneIsNot()
        {
            // The cache's whole policy, and it is the same one NeighbourHollowLoader applies to its
            // marshals: the key is the house file's own fingerprint, so a lot whose house changed
            // cannot be answered with the plane of the file it had, and a lot whose house did not
            // change costs nothing the second time.
            var lot = Lot(69, 200, 300);
            var calls = 0;
            var planes = new SimitoneArryPlanes((asked, out arry, out reason) =>
            {
                calls++;
                arry = SlopedPlane(asked.Size, Rise);
                reason = null;
                return true;
            });

            SimitoneArryAltitude first, second;
            string reason;
            Assert.True(planes.TryGet(lot, out first, out reason), reason);
            Assert.Equal(1, calls);

            //same fingerprint, same file: answered out of the cache
            Assert.True(planes.TryGet(lot, out second, out reason), reason);
            Assert.Equal(1, calls);
            Assert.Equal(2, planes.Asked);
            Assert.Equal(2, planes.GradedCount);
            Assert.Equal(0, planes.PinnedCount);
            Assert.Equal(1, planes.CacheHitCount);
            Assert.Equal(first.Side, second.Side);
            Assert.Equal(first.At(0, 0), second.At(0, 0));
            Assert.Equal(first.At(LotSize - 1, 0), second.At(LotSize - 1, 0));

            //a changed fingerprint: read again
            var moved = Lot(69, 200, 300);
            moved.HouseHash = 0x1234u;
            Assert.True(planes.TryGet(moved, out second, out reason), reason);
            Assert.Equal(2, calls);
            Assert.Equal(3, planes.GradedCount);

            //and a lot the layout cannot fingerprint is never cached, because there is nothing
            //here that could tell a changed file from an unchanged one
            var unfingerprinted = Lot(70, 200, 200);
            unfingerprinted.HouseHash = 0u;
            Assert.True(planes.TryGet(unfingerprinted, out second, out reason), reason);
            Assert.True(planes.TryGet(unfingerprinted, out second, out reason), reason);
            Assert.Equal(4, calls);

            //"no lot" is a sentence, not a throw: this runs once per candidate on the lot-load
            //thread and a lot the reader cannot answer for costs that lot and nothing else
            Assert.False(planes.TryGet(null, out second, out reason));
            Assert.Equal(SimitoneArryAltitude.None, second);
            Assert.Contains("median", reason);
        }

        [Fact]
        public void ARereadPlaneAfterTheContentsAreFilledInIsStillTheSameAnswer()
        {
            // The plan is a value and WithHollows is a copy, so filling in a neighbour's contents
            // must not re-read a height field or move a neighbour. If it did, the ground a lot was
            // hung at and the ground it is drawn on would come from two different files.
            NeighbourhoodLot here, east, north;
            var map = Street(out here, out east, out north);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var plan = Plan(placement, map, Layout(here, east, north), PlanesFor(69, 70));

            var marshals = new Dictionary<short, VMHollowMarshal>
            {
                { 69, new VMHollowMarshal() }
            };
            var filled = plan.WithHollows(marshals, new[] { "lot 70 was built blank" });

            Assert.Equal(1, filled.HollowCount);
            Assert.Equal(plan.GradedGroundCount, filled.GradedGroundCount);
            Assert.Same(plan.ArryPlanes, filled.ArryPlanes);
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                //the ground is the same field, corner for corner, and the same plane behind it. The
                //one field that did change is the one WithHollows exists to change.
                Assert.Equal(plan.Neighbours[i].Terrain.Describe(),
                    filled.Neighbours[i].Terrain.Describe());
                Assert.Equal(plan.Neighbours[i].BaseLevel, filled.Neighbours[i].BaseLevel);
                Assert.Equal(plan.Neighbours[i].SubWorldAltitudes,
                    filled.Neighbours[i].SubWorldAltitudes);
                Assert.Equal(plan.Neighbours[i].ArryAltitude.Side,
                    filled.Neighbours[i].ArryAltitude.Side);
                Assert.Equal(plan.Neighbours[i].IsGradedGround,
                    filled.Neighbours[i].IsGradedGround);
            }
            //the one thing that did change is the one it is for
            Assert.True(filled.NeighbourFor(69).IsHollow);
            Assert.False(plan.NeighbourFor(69).IsHollow);
        }

        [Fact]
        public void AnInvalidPlanHasNoPlanesToReportAndIsStillTheSameSentence()
        {
            // The refusals are decided before anything is asked of a height source, so a plan that
            // never got as far as choosing a neighbour has nothing to say about ground - and must
            // keep saying the same thing it always said.
            var map = NeighbourhoodMap.Create(MapSize);
            var placement = SimitoneSurroundingsPlacement.For(map, Lot(68, 200, 300));
            var plan = SimitoneSurroundingsPlan.For(placement, map, null, 8);
            Assert.False(plan.IsValid);
            Assert.Null(plan.ArryPlanes);
            Assert.Equal(0, plan.GradedGroundCount);
            Assert.Contains("no neighbour sub-worlds", plan.Describe());
            Assert.Equal("no sub-world ground to report",
                SimitoneSurroundingsRestore.DescribeHeights(plan));
            Assert.Equal("no sub-world ground to report",
                SimitoneSurroundingsRestore.DescribeHeights(null));
        }

        // ============================================================ reading the file

        [Fact]
        public void AnArryZeroChunkIsDecodedThroughTheRetailDecodersOwnFunction()
        {
            // Four bytes per record with the altitude in the second - the chunk's own layout - read
            // by VMTS1Activator.DecodeHeights, so the plane handed to the plan and the plane the lot
            // view loads are the same numbers out of the same function. A test that re-derived the
            // decode would pass against a broken decode.
            var records = new byte[8 * 8 * 4];
            for (int i = 0; i < 64; i++)
            {
                records[i * 4 + 0] = 0xAB;               //not the altitude
                records[i * 4 + 1] = (byte)(i % 251);    //the altitude
                records[i * 4 + 2] = 0xCD;               //not the altitude
                records[i * 4 + 3] = 0xEF;
            }

            var plane = NeighbourHollowLoader.ArryAltitudeFor(records, 8, 8);
            Assert.True(plane.IsValid);
            Assert.Equal(8, plane.Side);
            for (var z = 0; z < 8; z++)
            {
                for (var x = 0; x < 8; x++)
                {
                    Assert.Equal((byte)((z * 8 + x) % 251), plane.At(x, z));
                }
            }

            //and the median is the lower median of the rectangle, which is the statistic
            //NeighbourhoodLayoutBuilder.MedianBaseHeight takes over the same data
            Assert.Equal((int)NeighbourhoodLayoutBuilder.MedianBaseHeight(records),
                (int)plane.Median(8));

            //a chunk that is not a square height field is not one, whatever its bytes say
            Assert.False(NeighbourHollowLoader.ArryAltitudeFor(records, 8, 4).IsValid);
            Assert.False(NeighbourHollowLoader.ArryAltitudeFor(records, 0, 0).IsValid);
            Assert.False(NeighbourHollowLoader.ArryAltitudeFor(null, 8, 8).IsValid);
            Assert.False(NeighbourHollowLoader.ArryAltitudeFor(new byte[0], 1, 1).IsValid);
        }

        [Fact]
        public void EveryRefusalNamesTheLotAndTheFallbackItLeavesInPlace()
        {
            // The sentences are the whole of the contract on this path. There is no exception and no
            // wrong answer: a lot whose ARRY(0) cannot be read is built on the generated map's flat
            // median, and the reason says that in as many words - because the log is the only place
            // a player reporting "the next door's garden is a ramp" can be told which lots are real
            // and which are placeholders.
            var good = Lot(69, 200, 300);
            Assert.Null(NeighbourHollowLoader.ArryRefusalFor(good));

            var noHouse = Lot(69, 200, 300);
            noHouse.HasHouse = false;
            var unfingerprinted = Lot(69, 200, 300);
            unfingerprinted.HouseHash = 0u;

            foreach (var refused in new[] { null, noHouse, unfingerprinted })
            {
                var sentence = NeighbourHollowLoader.ArryRefusalFor(refused);
                Assert.NotNull(sentence);
                Assert.Contains("median", sentence);
                if (refused != null) Assert.Contains("lot " + refused.Number, sentence);
            }

            //and the read itself, with no install behind it, refuses the same way
            SimitoneArryAltitude arry;
            string reason;
            Assert.False(NeighbourHollowLoader.TryReadArryAltitude(noHouse, out arry, out reason));
            Assert.Equal(SimitoneArryAltitude.None, arry);
            Assert.Contains("median", reason);
            Assert.False(NeighbourHollowLoader.TryReadArryAltitude(good, out arry, out reason));
            Assert.Equal(SimitoneArryAltitude.None, arry);
            Assert.Contains("median", reason);
        }

        [Fact]
        public void AHouseFileWithNoArryZeroChunkIsARefusalRatherThanAGuess()
        {
            // A null file is the shape a caller that has no file has, and it is the only one a test
            // can build headlessly: everything after this point needs an IffFile the loader's own
            // parse produced. The sentence is the contract, and it is the same one every other
            // refusal makes.
            SimitoneArryAltitude arry;
            string reason;
            Assert.False(NeighbourHollowLoader.TryDecodeArryAltitude(null, 12, out arry, out reason));
            Assert.Equal(SimitoneArryAltitude.None, arry);
            Assert.Contains("lot 12", reason);
            Assert.Contains("median", reason);
            Assert.Contains("no house file", reason);
        }
    }
}
