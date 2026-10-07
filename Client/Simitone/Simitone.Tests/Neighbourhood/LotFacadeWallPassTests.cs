/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FSO.LotView.Facade;
using FSO.LotView.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The rules that decide whether a wall pass does a piece of work once per lot or once per wall
    /// segment, and the line that says where the pass's time went.
    ///
    /// <para><b>The problem these pin.</b> One lot's facade cost 2.61 seconds and 1.73 of it was
    /// the wall pass, which is enough for the neighbourhood pass's 250ms ceiling to stop after one
    /// lot and never show a second house. The wall pass draws every wall of a house into one atlas
    /// and it draws them one at a time, each with its own little orthographic camera scissored to
    /// one cell, and inside that loop it did three things whose cost is per wall for no reason:
    /// it prepared the lot view's lighting, it redrew the same objects, and it rebuilt the lot's
    /// wall geometry on its way out.</para>
    ///
    /// <para><b>What can be tested here, and what cannot.</b> All three decisions are pure -
    /// a storey and a normal in, an answer out - so they are pinned here. The draw calls they
    /// remove need a graphics device and a house, and are not; what is asserted about them is the
    /// count the generator reports in its own timings, which is a number this file can read.</para>
    /// </summary>
    public class LotFacadeWallPassTests
    {
        // ---- the storey rule ------------------------------------------------------------------
        // A wall segment is drawn through a small orthographic camera aimed at the middle of that
        // wall's own storey. The rule under test is that this camera can only ever contain that
        // one storey's geometry, so drawing the storeys below and above it was wasted work rather
        // than a choice - and that is why trimming them is not a visual change.

        [Fact]
        public void AWallCameraCanOnlyContainItsOwnStorey()
        {
            // The whole argument, as an assertion. For every storey either side of the wall's own,
            // the geometry is entirely outside the slab the camera projects into.
            for (int wallFloor = 0; wallFloor <= 5; wallFloor++)
            {
                var visible = WallPassPlan.VisibleStoreys(wallFloor);
                Assert.Equal(new[] { wallFloor }, visible);
            }
        }

        [Fact]
        public void TheSlabIsAShadeUnderOneStoreyTallAndCentredOnItsOwn()
        {
            // The reason the storeys either side cannot reach in: the camera is a shade under one
            // storey tall and centred on its own, so it starts just above the floor of that storey
            // and stops just below its ceiling. The margin is a third of a world unit at the
            // bottom, which at eight pixels a tile is under a fifth of a pixel - but the margin is
            // what the argument needs, and it is in whole units rather than a rounding artefact.
            Assert.True(WallPassPlan.CameraHeight < WallPassPlan.StoreyHeight);
            Assert.Equal(0.075f, WallPassPlan.SlabBottom(0), 3);
            Assert.Equal(8.775f, WallPassPlan.SlabTop(0), 3);
            //and it moves by exactly one storey per storey, which is what makes the rule hold for
            //every wall of every storey rather than only for the ground floor
            Assert.Equal(WallPassPlan.SlabBottom(0) + WallPassPlan.StoreyHeight,
                WallPassPlan.SlabBottom(1), 3);
            Assert.Equal(WallPassPlan.SlabTop(0) + WallPassPlan.StoreyHeight,
                WallPassPlan.SlabTop(1), 3);
        }

        [Fact]
        public void TheStoreyBelowTheWallEndsUnderTheSlabsFloor()
        {
            // A storey's geometry tops out at the ceiling of that storey, which for the storey
            // below the wall's own is the wall's own floor - 0.075 units under the slab.
            Assert.False(WallPassPlan.StoreyIsVisibleFrom(1, 0));
            Assert.True(WallPassPlan.StoreyIsVisibleFrom(1, 1));
            //and the storey above starts 0.12 of a storey above the wall's own ceiling
            Assert.False(WallPassPlan.StoreyIsVisibleFrom(1, 2));
        }

        [Fact]
        public void AGroundWallOfAOneStoreyHouseIsDrawnAtTheLevelItAlwaysWas()
        {
            // The regression guard for "this changes nothing for an ordinary house". A TS1 house
            // file describes one occupied storey unless somebody built up, so a ground floor wall
            // of one is the case the rule must leave completely alone - and it does, because the
            // old code used the house's storey count, which is 1.
            var plan = new WallPassPlan(1);
            Assert.Equal(1, plan.LevelFor(0));
        }

        [Fact]
        public void AWallHigherUpAsksForItsOwnStoreyAndNotTheWholeHouse()
        {
            // Where the saving is. A five-storey house drew all five storeys of wall geometry for
            // every one of its wall segments; a ground floor wall only needs the ground floor.
            var plan = new WallPassPlan(5);
            Assert.Equal(1, plan.LevelFor(0));
            Assert.Equal(2, plan.LevelFor(1));
            Assert.Equal(5, plan.LevelFor(4));
        }

        [Fact]
        public void ALevelIsClampedToTheStoreysTheHouseActuallyHas()
        {
            // A room claiming a storey the house does not have must not ask the wall component for
            // groups past the end of its geometry, and a house that claims no storeys at all must
            // keep the zero that used to draw nothing - both are what the old constant did.
            var plan = new WallPassPlan(3);
            Assert.Equal(3, plan.LevelFor(9));
            Assert.Equal(0, new WallPassPlan(0).LevelFor(0));
            //and whatever the house claimed for a house that claims a negative number of storeys,
            //which is what the old constant gave the state and what draws nothing
            Assert.Equal(-1, new WallPassPlan(-1).LevelFor(4));
        }

        [Fact]
        public void AStoreyTheWallPassAsksForIsTheOnlyOneItDraws()
        {
            // CountWallDraws walks GroupsByTexture[0..level), so the level the plan asks for is
            // the level that bounds the draw calls. Null component and empty geometry are both
            // zero rather than a throw, because both are ordinary states of a lot.
            Assert.Equal(0, LotFacadeGenerator.CountWallDraws(null, 3));
        }

        // ---- the level is bounded by the geometry, at both ends ------------------------------------
        // This is the empty-wall bug. The wall pass asks WallComponentRC to draw "the storey this
        // wall belongs to", by level, and the component used to answer a level it had no storeys for
        // by returning before its own loop - so it drew nothing at all, for a lot whose walls were
        // in the storey below, and the pass reported a lot whose walls, roof and floor all came back
        // empty. The two facts below are the fix, and they are facts about numbers rather than about
        // a render: the plan clamps to the storeys the component has, and the component clamps to
        // the storeys it has.

        [Fact]
        public void ALevelTheGeometryDoesNotHaveStillDrawsTheStoreysItDoes()
        {
            // The component's half, as a pure function of two integers. One past the end is the
            // interesting value: it is what "the wall's storey is one higher than this lot has"
            // looks like, and it used to be a draw of nothing.
            Assert.Equal(5, WallComponentRC.LevelsToDraw(5, 5));
            Assert.Equal(5, WallComponentRC.LevelsToDraw(9, 5));
            //one past the end walks everything there is, rather than nothing
            Assert.Equal(5, WallComponentRC.LevelsToDraw(6, 5));
            //and below the first storey there is nothing to walk
            Assert.Equal(0, WallComponentRC.LevelsToDraw(0, 5));
            Assert.Equal(0, WallComponentRC.LevelsToDraw(1, 0));
            Assert.Equal(0, WallComponentRC.LevelsToDraw(-2, 5));
        }

        [Fact]
        public void ThePasssLevelIsBoundedByTheStoreysTheWallComponentHas()
        {
            // The plan's half. A stock TS1 lot claims five storeys because nothing in the house file
            // rewrites Blueprint.Stories, so the un-clamped answer for a ground floor wall is 1
            // against five - and that happens to agree with the geometry. These are the cases where
            // the two numbers do not: a lot with one storey of geometry, and a lot with none.
            var plan = new WallPassPlan(5);
            Assert.Equal(1, plan.LevelFor(0, 5));
            Assert.Equal(5, plan.LevelFor(4, 5));

            //one storey of geometry: every wall asks for its own storey, clamped to the one there is
            var oneStorey = new WallPassPlan(5);
            Assert.Equal(1, oneStorey.LevelFor(0, 1));
            Assert.Equal(1, oneStorey.LevelFor(1, 1));
            Assert.Equal(1, oneStorey.LevelFor(4, 1));

            //no geometry at all is a lot with no walls, and it has to answer zero rather than a
            //level the component cannot honour
            Assert.Equal(0, new WallPassPlan(5).LevelFor(0, 0));

            //and a house that claims no storeys keeps the zero the old constant gave it, so nothing
            //that used to draw nothing starts drawing something
            Assert.Equal(0, new WallPassPlan(0).LevelFor(0, 5));
            Assert.Equal(0, new WallPassPlan(0).LevelFor(0, 0));
        }

        [Fact]
        public void ALevelTheGeometryLacksCanNeverAskForAStoreyPastTheEnd()
        {
            // The invariant, over every storey of every claim. There is no input for which the
            //clamped answer is above the storeys the component has, which is the whole of what
            //"WCRC.Draw draws nothing" needed.
            for (int stories = 0; stories <= 5; stories++)
            {
                var plan = new WallPassPlan(stories);
                for (int storeys = 0; storeys <= 5; storeys++)
                {
                    for (int wallFloor = -1; wallFloor <= 6; wallFloor++)
                    {
                        var level = plan.LevelFor(wallFloor, storeys);
                        Assert.True(level <= storeys,
                            "storeys " + stories + "/" + storeys + " wall " + wallFloor
                                + " asked for level " + level);
                        Assert.True(level >= 0,
                            "storeys " + stories + "/" + storeys + " wall " + wallFloor
                                + " asked for level " + level);
                    }
                }
            }
        }

        [Fact]
        public void AGroundFloorWallOfAOneStoreyLotWithOneStoreyOfWallsIsStillDrawnAtLevelOne()
        {
            // The ordinary case, stated end to end: the level the pass asks for is 1, the component
            // has one storey, and the component's clamp therefore walks storey one - which is the
            // storey the walls are in. The three numbers together are the proof that the ordinary
            // house is not the bug, and that the fix costs it nothing.
            var plan = new WallPassPlan(1);
            var level = plan.LevelFor(0, 1);
            var walls = ComponentWithStoreys(16);

            Assert.Equal(1, level);
            Assert.Equal(1, walls.Storeys);
            Assert.Equal(1, WallComponentRC.LevelsToDraw(level, walls.Storeys));
            //and the walls on it are found by a draw at that level
            Assert.Equal(16, walls.GeometryThrough(level));
            Assert.Equal(1, walls.GroupsThrough(level));
        }

        [Fact]
        public void AComponentWithNoGroupsDrawsNothingRatherThanThrowing()
        {
            // A lot with no wall component, and one with an empty group table, are both ordinary
            // states of a lot - a bulldozed plot, and a house with no outside rooms.
            var empty = new WallComponentRC();

            Assert.Equal(0, empty.Storeys);
            Assert.Equal(0, WallComponentRC.LevelsToDraw(1, empty.Storeys));
            Assert.Equal(0, empty.GeometryThrough(1));
            Assert.Equal(0, empty.GroupsThrough(1));
        }

        [Fact]
        public void AComponentDrawsEveryStoreyBelowTheLevelAndNoStoreyAboveIt()
        {
            // The clamp, over a real group table: three storeys of which the middle one has no walls
            // at all, drawn at each level. This is the number the wall pass multiplies by its cell
            // count, and it is now read off the component rather than predicted beside it.
            var walls = ComponentWithStoreys(16, 0, 12);

            Assert.Equal(3, walls.Storeys);
            Assert.Equal(16, walls.GeometryThrough(1));
            //level two adds the storey with no walls, which adds nothing
            Assert.Equal(16, walls.GeometryThrough(2));
            Assert.Equal(28, walls.GeometryThrough(3));
            Assert.Equal(28, walls.GeometryThrough(5));
            //and a level past the end walks everything rather than nothing - the bug, in a number
            Assert.Equal(28, walls.GeometryThrough(9));
            Assert.Equal(1, walls.GroupsThrough(1));
            Assert.Equal(2, walls.GroupsThrough(9));
        }

        /// <summary>
        /// A wall component with a group table and no graphics device: one storey dictionary per
        /// entry, holding one group with that many primitives, or none at all for a storey with no
        /// walls on it.
        /// </summary>
        /// <param name="primCountsPerStorey">How many triangles each storey holds.</param>
        private static WallComponentRC ComponentWithStoreys(params int[] primCountsPerStorey)
        {
            var walls = new WallComponentRC();
            for (int s = 0; s < primCountsPerStorey.Length; s++)
            {
                var byTexture = new Dictionary<Tuple<Texture2D, Texture2D>, WallGroupRC>();
                if (primCountsPerStorey[s] > 0)
                {
                    //one group per storey, because a second would need a second Texture2D to key
                    //it on and a Texture2D cannot be made without a device
                    byTexture[Tuple.Create<Texture2D, Texture2D>(null, null)] =
                        new WallGroupRC { PrimCount = primCountsPerStorey[s] };
                }
                walls.GroupsByTexture.Add(byTexture);
            }
            return walls;
        }

        // ---- did the pass draw a wall at all ------------------------------------------------------

        [Fact]
        public void AWallPassIsNonEmptyWhenBothCountsAreAndEmptyWhenEitherIsNot()
        {
            // The rule the builder asks. Four vertices and six indices per wall, so a real pass
            // never lands on anything else - and the "both have to be there" is the point: a pass
            // whose arrays were never assigned and a pass that assigned an empty array are the same
            // two zeros, and neither of them is a lot with no house on it.
            Assert.True(LotFacadeGenerator.HasWallGeometry(4, 6));
            Assert.True(LotFacadeGenerator.HasWallGeometry(160, 240));
            Assert.False(LotFacadeGenerator.HasWallGeometry(0, 0));
            Assert.False(LotFacadeGenerator.HasWallGeometry(0, 6));
            Assert.False(LotFacadeGenerator.HasWallGeometry(4, 0));
        }

        [Fact]
        public void AGeneratorThatHasNotRunAWallPassReportsNoWallRatherThanThrowing()
        {
            // A caller that asks before anything has been built gets zero, which is the answer that
            // reads as "nothing yet" rather than as "I do not know".
            var generator = new LotFacadeGenerator();

            Assert.Equal(0, generator.LastWallVertexCount);
            Assert.Equal(0, generator.LastWallIndexCount);
            Assert.False(LotFacadeGenerator.HasWallGeometry(
                generator.LastWallVertexCount, generator.LastWallIndexCount));
        }

        // ---- the lighting rule ---------------------------------------------------------------

        [Fact]
        public void TheLightingIsPreparedOncePerOrientationAndNotOncePerWall()
        {
            // Forty wall segments of an axis-aligned house are four orientations, so the old
            // forty lighting prepares are four. This is the number the rule is for.
            var plan = new WallPassPlan(1);
            Assert.Equal(0, plan.LightingPrepares);

            var north = new Vector2(0, -1);
            var south = new Vector2(0, 1);
            var east = new Vector2(1, 0);
            var west = new Vector2(-1, 0);

            Assert.True(plan.NeedsLighting(north, 1));
            Assert.False(plan.NeedsLighting(north, 1));
            Assert.False(plan.NeedsLighting(north, 1));
            Assert.True(plan.NeedsLighting(south, 1));
            Assert.True(plan.NeedsLighting(east, 1));
            Assert.True(plan.NeedsLighting(west, 1));

            Assert.Equal(4, plan.LightingPrepares);
        }

        [Fact]
        public void AStoreyChangeIsADifferentLightingToPrepare()
        {
            // PrepareLighting also writes the lot view's level into the 2D world batch effect, so
            // the level is part of what it leaves behind and a different level is a different
            // answer - a ground floor wall and a first floor wall of the same house cannot share
            // one.
            var plan = new WallPassPlan(3);
            Assert.True(plan.NeedsLighting(new Vector2(0, -1), 1));
            Assert.True(plan.NeedsLighting(new Vector2(0, -1), 2));
            Assert.False(plan.NeedsLighting(new Vector2(0, -1), 2));
            Assert.Equal(2, plan.LightingPrepares);
        }

        [Fact]
        public void TheSameNormalIsTheSameNormalHoweverLongTheWallIs()
        {
            // The cache keys on the normal, and a normal is derived from two integer tile
            // coordinates and then normalised - so a ten tile wall and a two tile wall on the same
            // side of the house produce a bit identical normal and must share a prepare. The test
            // computes them the way the pass does rather than assuming it.
            var plan = new WallPassPlan(1);
            var tenTile = NormalOf(new Vector2(0, 0), new Vector2(160, 0));
            var twoTile = NormalOf(new Vector2(32, 96), new Vector2(64, 96));

            Assert.Equal(tenTile, twoTile);
            Assert.True(plan.NeedsLighting(tenTile, 1));
            Assert.False(plan.NeedsLighting(twoTile, 1));
            Assert.Equal(1, plan.LightingPrepares);
        }

        /// <summary>The outward normal the pass computes for a wall line, as it computes it.</summary>
        private static Vector2 NormalOf(Vector2 from, Vector2 to)
        {
            var rNorm = to - from;
            rNorm = new Vector2(-rNorm.Y, rNorm.X);
            rNorm.Normalize();
            return rNorm;
        }

        // ---- the restore-geometry rule --------------------------------------------------------

        [Fact]
        public void AnAllFalseCutawayNeedsNoGeometryRebuildOnTheWayOut()
        {
            // WallComponentRC.Generate derives every wall's height from the cutaway and the level,
            // and with no cuts every wall's height is 1 whatever the level is - so the rebuild the
            // pass did to put the caller's geometry back was producing the buffers already in
            // place. A whole sweep of the lot's walls, and a fresh set of vertex and index buffers
            // for every wall texture in it, for nothing.
            Assert.False(WallPassPlan.NeedsRestoreGenerate(null));
            Assert.False(WallPassPlan.NeedsRestoreGenerate(new bool[0]));
            Assert.False(WallPassPlan.NeedsRestoreGenerate(new[] { false, false, false }));
        }

        [Fact]
        public void ACutawayWithACutInItStillGetsItsGeometryBack()
        {
            // The other direction, and the one that matters for a live lot: a cutaway that is not
            // all-false has to be put back, or the caller's lot view is left holding a facade's
            // uncut walls.
            Assert.True(WallPassPlan.NeedsRestoreGenerate(new[] { false, true, false }));
            Assert.True(WallPassPlan.NeedsRestoreGenerate(new[] { true }));
        }

        // ---- the object pass, once per build --------------------------------------------------

        [Fact]
        public void TheObjectPassIsOnePassOverTheUnionAndNotOnePassPerWall()
        {
            // The other forty-fold multiplier. Which objects a wall can see is per wall and stays
            // per wall; drawing them was not, and this is the arithmetic of the difference. A lot
            // laid out so that every wall can see a slice of it, with the slices overlapping in
            // the middle.
            var objects = 200;
            var walls = 40;
            var batch = WallObjectBatch.FromBounds(
                Levels(objects), Spread(objects, 16, 13));

            //the old way: every wall selects and the sum of the selections is what got drawn
            long drawnPerWall = 0;
            for (int wall = 0; wall < walls; wall++)
            {
                drawnPerWall += batch.Select(WallCameraAt(wall % 16, wall / 16), -4);
            }

            //the new way: the same filter, and the union of what it chose
            var union = WallObjectBatch.FromBounds(Levels(objects), Spread(objects, 16, 13));
            for (int wall = 0; wall < walls; wall++)
            {
                union.AddToUnion(WallCameraAt(wall % 16, wall / 16), -4);
            }

            Assert.True(drawnPerWall > union.UnionCount,
                "the union should be smaller than the sum of the per-wall selections");
            Assert.True(union.UnionCount > 0, "the union should not be empty");
            //and the filter still looked at everything, because that is the part that is per wall
            Assert.Equal((long)objects * walls, union.Checks);
        }

        [Fact]
        public void AnObjectIsClaimedOnceHoweverManyWallsCanSeeIt()
        {
            // The specific claim: the union is exactly the union of the per-wall selections, so an
            // object four walls can see is drawn once and is not missing from the pass.
            var batch = WallObjectBatch.FromBounds(
                new[] { 1, 1, 1 }, Spread(3, 1, 1));
            var camera = WallCameraAt(0f, 0f);

            Assert.Equal(3, batch.AddToUnion(camera, -4));
            Assert.Equal(3, batch.AddToUnion(camera, -4));
            Assert.Equal(3, batch.AddToUnion(camera, -4));
            Assert.Equal(3, batch.UnionCount);
        }

        [Fact]
        public void TheUnionKeepsTheLotsOwnOrderForTheObjectsItAdds()
        {
            // The order the union comes out in is what a stable draw-order sort is stable
            // against, so it has to be the order the lot's own object list had.
            var levels = new[] { 1, 1, 1, 1 };
            var bounds = new[]
            {
                Box(10f, 0f, 0f), Box(11f, 0f, 0f), Box(0f, 0f, 0f), Box(12f, 0f, 0f)
            };
            var batch = WallObjectBatch.FromBounds(levels, bounds);

            Assert.Equal(3, batch.AddToUnion(WallCameraAt(10.5f, 0f), -4));
            Assert.Equal(0, batch.Union[0]);
            Assert.Equal(1, batch.Union[1]);
            Assert.Equal(3, batch.Union[2]);
        }

        [Fact]
        public void ALotWithNothingToDrawUnionsNothingRatherThanFailing()
        {
            var batch = WallObjectBatch.Gather(null);
            Assert.Equal(0, batch.AddToUnion(WallCameraAt(0f, 0f), -4));
            Assert.Equal(0, batch.UnionCount);
            //and the empty union is still a thing that can be drawn and sorted
            batch.OrderUnionByDrawOrder(null);
            batch.DrawUnion(null, null);
        }

        // ---- the object pass, indexed by where things are --------------------------------------
        // The term that made a few lots in a neighbourhood cost seconds. The object query is per
        // cell - each cell has its own camera - and it walked every object in the lot to answer
        // it, so the cost was objects * cells. A lot that is ordinary in both is a few tens of
        // thousands of bound tests; a lot with a few thousand objects and a few hundred wall
        // segments is a few million, and a few of those in one neighbourhood is the twenty second
        // stall. The index does not change which objects a cell draws - only how many it had to
        // ask about to find out - and that is the whole of the claim these four tests make.

        [Fact]
        public void TheIndexedObjectQueryChoosesTheSameObjectsInTheSameOrderAsTheLinearOne()
        {
            // A lot with a few thousand objects laid out over 64 by 64 units, and a camera put
            // into each of four different places in it - including two corners, where a bucket
            // range that clamps wrongly would show up first.
            var lot = LotOfObjects(64);
            var places = new[] { new Vector2(32f, 32f), new Vector2(0f, 0f), new Vector2(63f, 63f), new Vector2(16f, 48f) };
            foreach (var place in places)
            {
                var camera = WallCameraAt(place.X, place.Y);
                var corners = new Vector3[WallCellPlan.FrustumCornerCount];
                var region = WallCellPlan.RegionOf(camera, corners);

                var linear = WallObjectBatch.FromBounds(Levels(lot.Length), lot);
                var expected = linear.Select(camera, -4);
                var expectedIndices = new int[expected];
                Array.Copy(linear.Selected, expectedIndices, expected);

                var indexed = WallObjectBatch.FromBounds(Levels(lot.Length), lot);
                var actual = indexed.Select(camera, -4, region);
                var actualIndices = new int[actual];
                Array.Copy(indexed.Selected, actualIndices, actual);

                Assert.Equal(expected, actual);
                Assert.Equal(expectedIndices, actualIndices);
            }
        }

        [Fact]
        public void TheIndexedObjectQueryTestsAFractionOfWhatTheLinearOneDid()
        {
            // The point of it, as a number rather than a claim: the same answer, from a small
            // fraction of the bound tests. The index is sized from the lot, so the ratio is the
            // fraction of the lot a cell's camera can see.
            var lot = LotOfObjects(64);
            var camera = WallCameraAt(32f, 32f);
            var corners = new Vector3[WallCellPlan.FrustumCornerCount];
            var region = WallCellPlan.RegionOf(camera, corners);

            var indexed = WallObjectBatch.FromBounds(Levels(lot.Length), lot);
            indexed.Select(camera, -4, region);

            Assert.True(indexed.SelectedCount > 0, "the camera should see something");
            Assert.True(indexed.Checks < indexed.UnindexedChecks / 4,
                "the index tested " + indexed.Checks + " of the " + indexed.UnindexedChecks
                    + " the linear query would have");
        }

        [Fact]
        public void TheIndexedObjectQueryStillFindsTheObjectsItCannotIndex()
        {
            // The two kinds the grid cannot place. One the lot view cannot size, whose bounds are
            // the empty box and which therefore intersects nothing at all - so it must be found and
            // then rejected, exactly as the linear query found and rejected it. And one whose
            // bounds are the whole lot, which covers more buckets than a single object is allowed,
            // so it goes in the overflow list and is in every query's answer. Missing either is
            // the failure the index could not be allowed to have: a different picture.
            var bounds = new[]
            {
                new BoundingBox(),                                        // 0: nothing, finds nothing
                Box(32f, 0f, 32f),                                       // 1: at the camera
                new BoundingBox(new Vector3(-500f, -500f, -500f), new Vector3(500f, 500f, 500f)), // 2: huge
                Box(0f, 0f, 0f)                                          // 3: far away, in no cell
            };
            var levels = new[] { 1, 1, 1, 1 };
            var camera = WallCameraAt(32f, 32f);
            var corners = new Vector3[WallCellPlan.FrustumCornerCount];
            var region = WallCellPlan.RegionOf(camera, corners);

            var indexed = WallObjectBatch.FromBounds(levels, bounds);
            Assert.Equal(2, indexed.Select(camera, -4, region));
            Assert.Equal(new[] { 1, 2 }, new[]
            {
                indexed.Selected[0], indexed.Selected[1]
            });

            var linear = WallObjectBatch.FromBounds(levels, bounds);
            Assert.Equal(2, linear.Select(camera, -4));
            Assert.Equal(new[] { 1, 2 }, new[]
            {
                linear.Selected[0], linear.Selected[1]
            });
        }

        [Fact]
        public void AnIndexedQueryForACameraWithNoBoundsFallsBackToTheWholeLot()
        {
            // A wall line whose two points are the same has a physical length of zero, and the
            // camera built from it is CreateOrthographic(0, ...), whose bounds are not a box. The
            // index must not be asked about that: a wrong answer there is an empty cell where the
            // scissor used to have decided, on a lot that has geometry in it.
            var lot = LotOfObjects(16);
            var camera = WallCameraAt(8f, 8f);
            var corners = new Vector3[WallCellPlan.FrustumCornerCount];
            var degenerate = WallCellPlan.RegionOf(DegenerateWallCamera(), corners);

            var indexed = WallObjectBatch.FromBounds(Levels(lot.Length), lot);
            var expected = indexed.Select(camera, -4);
            var expectedIndices = new int[expected];
            Array.Copy(indexed.Selected, expectedIndices, expected);
            var before = indexed.Checks;

            var actual = indexed.Select(camera, -4, degenerate);
            var actualIndices = new int[actual];
            Array.Copy(indexed.Selected, actualIndices, actual);

            Assert.Equal(expected, actual);
            Assert.Equal(expectedIndices, actualIndices);
            //and it paid the whole lot for it, which is the point: the fallback is the old answer
            Assert.Equal(before + lot.Length, indexed.Checks);
        }

        [Fact]
        public void TheObjectIndexIsSizedFromTheLotAndBounded()
        {
            // The lattice is a decision, and it is the decision that decides what a cell's query
            // costs. Too coarse and every query is a scan of a bucket holding the whole lot; too
            // fine and a four hundred cell pass pays four hundred lattice walks for a handful of
            // objects. So: a small lot gets a small lattice rather than a lot-sized one, a large
            // lot is not given one that is too big to walk, and the whole thing is capped so the
            // offsets array cannot be what a lot's object count makes it.
            var small = WallObjectGrid.Build(LotOfObjects(8));
            var large = WallObjectGrid.Build(LotOfObjects(64));

            Assert.NotNull(small);
            Assert.NotNull(large);
            Assert.True(small.Buckets < large.Buckets);
            Assert.True(large.Buckets <= WallObjectGrid.MaxBuckets);
            //one bucket is not smaller than a minimum, so a lot of one-tile objects still gets a
            //bucket that can hold one of them rather than a lattice of sub-unit cells
            Assert.True(small.Columns >= 1 && small.Rows >= 1);
            //and a lot with nothing in it has no index to ask
            Assert.Null(WallObjectGrid.Build(new BoundingBox[0]));
            Assert.Null(WallObjectGrid.Build(null));
            //a lot whose every object has an inverted box has no extent to lay a lattice out over
            //either - which is a different thing from the lot view's empty box, which is a point at
            //the origin and is therefore a perfectly good place to put an object
            var inverted = new BoundingBox(new Vector3(1f, 1f, 1f), new Vector3(0f, 0f, 0f));
            Assert.Null(WallObjectGrid.Build(new[] { inverted, inverted }));
            //and an empty box is indexed like any other object, so the index is not a way for one
            //to be dropped. What decides it is still the caller's own frustum test, and the indexed
            //and linear queries agree about it either way.
            Assert.NotNull(WallObjectGrid.Build(new[] { new BoundingBox() }));
            var emptyBounds = new[] { new BoundingBox() };
            var at = WallCameraAt(0f, 0f);
            var region = WallCellPlan.RegionOf(at, new Vector3[WallCellPlan.FrustumCornerCount]);
            Assert.Equal(
                WallObjectBatch.FromBounds(new[] { 1 }, emptyBounds).Select(at, -4),
                WallObjectBatch.FromBounds(new[] { 1 }, emptyBounds).Select(at, -4, region));
        }

        /// <summary>
        /// A lot of <paramref name="span"/> by <paramref name="span"/> one-unit objects, one per
        /// position, in the lot's own order.
        /// </summary>
        private static BoundingBox[] LotOfObjects(int span)
        {
            var bounds = new BoundingBox[span * span];
            for (int z = 0; z < span; z++)
            {
                for (int x = 0; x < span; x++)
                {
                    bounds[(z * span) + x] = Box(x, 0f, z);
                }
            }
            return bounds;
        }

        // ---- the line that says where the time went --------------------------------------------

        [Fact]
        public void TheWallPassLineNamesEveryPhaseAndTheTotal()
        {
            // "walls 1.73s" is the sentence this pass replaced, and it is a sentence that cannot be
            // acted on. Every phase has to be in the new one, whether it cost anything or not, or
            // the next reader is back to guessing - and the draw counts have to be in it, because
            // the counts are the explanation the timings cannot give on their own.
            var line = Timings().Describe();

            Assert.Contains("40 wall(s) in 40 bin(s)", line);
            Assert.Contains("800 wall group draw(s)", line);
            Assert.Contains("96 object draw(s)", line);
            Assert.Contains("4 lighting prepare(s)", line);
            foreach (WallPassPhase phase in Enum.GetValues(typeof(WallPassPhase)))
            {
                Assert.Contains(WallPassTimings.DescribePhase(phase), line);
            }
        }

        [Fact]
        public void TheWallPassLineSaysHowManyWallVerticesAndIndicesItProduced()
        {
            // The line's other job. A pass that spends a second submitting work and produces no
            // wall is a different bug from a slow one, and "the lot's walls, roof and floor all came
            // back empty" cannot tell the two apart - so the counts are on the line, and they are
            // the counts the generator reports from its own output rather than a number a caller
            // has to remember to ask for.
            var line = Timings().Describe();

            Assert.Contains("160 wall vertex/vertices", line);
            Assert.Contains("240 wall index/indices", line);
            //and the cell count, so "the cells went down" and "the groups per cell went down" stay
            //separable in one line
            Assert.Contains("40 cell draw(s)", line);
        }

        [Fact]
        public void AWallPassThatProducedNoWallSaysZeroRatherThanSayingNothing()
        {
            // The failure this is for, read off the line rather than off a screenshot. A house with
            // no outside rooms is the honest zero; a house whose geometry was thrown away is the
            // same two numbers, and the line at least makes the first one a stated fact.
            var ms = new double[WallPassTimings.PhaseCount];
            var empty = new WallPassTimings(ms, new WallPassCounts { Bins = 3, Walls = 0 }, 4d);

            var line = empty.Describe();
            Assert.Contains("0 wall(s) in 3 bin(s)", line);
            Assert.Contains("0 wall vertex/vertices", line);
            Assert.Contains("0 wall index/indices", line);
            Assert.Contains("0 wall group draw(s)", line);
        }

        [Fact]
        public void APhaseThatCostHalfASecondIsWrittenInSeconds()
        {
            // 1730ms on a log line is read as a number and skipped. 1.73s is read as a freeze.
            var line = Timings(wallDrawMs: 1730d).Describe();
            Assert.Contains("wall draws 1.73s", line);
            Assert.DoesNotContain("wall draws 1730ms", line);
        }

        [Fact]
        public void TheLineSaysHowManyCellsHadToBeSubmittedWhole()
        {
            // The number that tells a fast pass from a correct one that happens to be slow. A pass
            // whose cells all had to fall back to the whole storey is drawing the right walls at
            // the old cost, and a pass with none of them is drawing the right walls at the new one -
            // and the difference between those two is not in the timings, so it has to be in the
            // line. It is also the number that would have said "your cell box is in the wrong
            // space" on the run where the houses came out with facades and no walls.
            var ms = new double[WallPassTimings.PhaseCount];
            var counts = new WallPassCounts
            {
                Walls = 20,
                CellDraws = 20,
                CellFallbacks = 20,
                WallTriangles = 0,
                WallTrianglesWhole = 11120
            };
            var line = new WallPassTimings(ms, counts, 1820d).Describe();

            Assert.Contains("20 cell fallback(s)", line);
            Assert.Contains("0 wall triangle(s) where a whole storey per cell would have been 11120", line);
            //and the object half of the same question: the bound checks it did against the ones
            //the linear query would have done
            counts.CellFallbacks = 0;
            counts.ObjectChecks = 480;
            counts.ObjectChecksWhole = 51200;
            var indexed = new WallPassTimings(ms, counts, 40d).Describe();
            Assert.Contains("0 cell fallback(s)", indexed);
            Assert.Contains("480 object bound check(s) where a whole lot per cell would have been 51200",
                indexed);
        }

        [Fact]
        public void TheTotalIsMeasuredRatherThanSummed()
        {
            // The throwaway world and the readback's stall sit outside every phase, and a phase
            // that threw is only charged for the part that ran - so adding the phases up would be a
            // different number from the one the game thread lost.
            var timings = Timings();
            double sum = 0d;
            foreach (WallPassPhase phase in Enum.GetValues(typeof(WallPassPhase)))
            {
                sum += timings[phase];
            }
            Assert.NotEqual(timings.TotalMs, sum, 3);
        }

        [Fact]
        public void APassThatNeverRanSaysSoWithZerosRatherThanThrowing()
        {
            // The log line is written after something has already gone wrong, from a struct that
            // may have been default-constructed. A line that throws is a line nobody reads.
            var none = WallPassTimings.None;
            Assert.Equal(0d, none.TotalMs);
            Assert.Equal(0, none.Walls);
            Assert.False(string.IsNullOrWhiteSpace(none.Describe()));
            foreach (WallPassPhase phase in Enum.GetValues(typeof(WallPassPhase)))
            {
                Assert.Equal(0d, none[phase]);
            }
            //and the default-constructed one, which has no table at all
            var missing = default(WallPassTimings);
            Assert.Equal(0d, missing[WallPassPhase.WallDraw]);
            Assert.False(string.IsNullOrWhiteSpace(missing.Describe()));
        }

        [Fact]
        public void EveryPhaseHasANameAndNoNameIsBlank()
        {
            // Or a phase is measured and never read, which is the same as not measuring it.
            var seen = new HashSet<string>();
            foreach (WallPassPhase phase in Enum.GetValues(typeof(WallPassPhase)))
            {
                var name = WallPassTimings.DescribePhase(phase);
                Assert.False(string.IsNullOrWhiteSpace(name), phase + " has no name");
                Assert.True(seen.Add(name), phase + " is named the same as another phase");
            }
            Assert.Equal(Enum.GetValues(typeof(WallPassPhase)).Length, seen.Count);
        }

        [Fact]
        public void AMeasurementThatDidNotHappenIsWrittenAsSomethingOtherThanANumber()
        {
            Assert.Equal("?ms", WallPassTimings.Milliseconds(double.NaN));
            Assert.Equal("?ms", WallPassTimings.Milliseconds(double.NegativeInfinity));
            Assert.Equal("0ms", WallPassTimings.Milliseconds(-5d));
            Assert.Equal("4ms", WallPassTimings.Milliseconds(4.2d));
            Assert.Equal("1.00s", WallPassTimings.Milliseconds(1000d));
        }

        // ---- the atlas's own resolution -------------------------------------------------------
        // The one lever on the wall pass that is arithmetic rather than a judgement about what a
        // house should look like. A neighbourhood facade is a low-detail silhouette seen from
        // above - a whole 64 tile lot is a few dozen pixels on screen - and every pixel-counted
        // part of the pass (the target, the rasterisation, the GetData that waits for the whole
        // pass, the managed per-cell edge bleed, the upload) scales with the atlas's area rather
        // than with the house. So the atlas is drawn at a divisor of its retail size for the
        // duration of a neighbourhood build, and these are the numbers it produces.

        [Fact]
        public void TheWallAtlasIsATwiceIsTwoThousandPixelsForAFortyBinHouse()
        {
            // The before and after, as the two numbers the pass allocates with. 512 x 960 is the
            // retail atlas at the neighbourhood's supersample of one; 256 x 520 is the same house
            // at a scale of two. Four times fewer pixels in the target, in the readback, in the
            // managed bleed and in the upload.
            //
            // The supersample is passed in rather than read from the static: it is lowered to one
            // only for the length of a build, so a test that quoted the ambient value would be
            // quoting the retail setting of two however hard it tried to assert otherwise.
            Assert.Equal(512, LotFacadeGenerator.WallAtlasWidth(1, 1));
            Assert.Equal(960, LotFacadeGenerator.WallAtlasHeight(40, 1, 1));
            Assert.Equal(512L * 960L, LotFacadeGenerator.WallAtlasPixels(40, 1, 1));

            Assert.Equal(256, LotFacadeGenerator.WallAtlasWidth(2, 1));
            Assert.Equal(520, LotFacadeGenerator.WallAtlasHeight(40, 2, 1));
            Assert.Equal(256L * 520L, LotFacadeGenerator.WallAtlasPixels(40, 2, 1));

            Assert.True(LotFacadeGenerator.WallAtlasPixels(40, 2, 1) * 3
                < LotFacadeGenerator.WallAtlasPixels(40, 1, 1),
                "halving both axes should be a large cut in the pixels read back");
        }

        [Fact]
        public void TheRetailSupersampleIsStillTheOtherHalfOfTheTarget()
        {
            // The two levers are separate and multiply. The OBJ exporter runs at the retail
            // supersample of two and the retail scale of one, and its atlas is four times the area
            // of the neighbourhood's on each axis again - which is exactly why the exporter is an
            // offline tool and the neighbourhood is not.
            Assert.Equal(1024, LotFacadeGenerator.WallAtlasWidth(1, 2));
            Assert.Equal(1920, LotFacadeGenerator.WallAtlasHeight(40, 1, 2));
            Assert.Equal(1024L * 1920L, LotFacadeGenerator.WallAtlasPixels(40, 1, 2));
            //and a supersample of zero or less is one rather than a zero-sized target
            Assert.Equal(512, LotFacadeGenerator.WallAtlasWidth(1, 0));
            Assert.Equal(960, LotFacadeGenerator.WallAtlasHeight(40, 1, -2));
        }

        [Fact]
        public void AScaledCellIsSmallerOnBothAxesSoTheWallIsNotStretched()
        {
            // The cell's pixel size is what the bin packing, the scissor rectangle, the camera
            // mapping, the edge bleed and the vertex texture coordinates all agree on. A scale that
            // moved only one of the two axes would give every cell a different aspect from the
            // wall it holds, and the wall would be drawn squashed into its cell.
            Assert.Equal(8, LotFacadeGenerator.WallCellWidth(1));
            Assert.Equal(22, LotFacadeGenerator.WallCellHeight(1));
            Assert.Equal(4, LotFacadeGenerator.WallCellWidth(2));
            Assert.Equal(11, LotFacadeGenerator.WallCellHeight(2));
            //and the row a bin packs into is the same division of MAX_WALL_WIDTH tiles
            Assert.Equal(512, LotFacadeGenerator.WallBinWidth(1));
            Assert.Equal(256, LotFacadeGenerator.WallBinWidth(2));
        }

        [Fact]
        public void AScaledCellStillHasAnInteriorToRender()
        {
            // The camera mapping is built from (height - 2), and a cell with no interior is a cell
            // the wall is drawn outside of. This is the floor under the scale: a divisor of
            // anything at all still produces a cell that can hold a wall.
            foreach (var scale in new[] { 1, 2, 4, 8, 16, 32, 64, 1024 })
            {
                Assert.True(LotFacadeGenerator.WallCellHeight(scale) >= LotFacadeGenerator.MinimumScaledCellHeight,
                    "a scale of " + scale + " produced a cell " + LotFacadeGenerator.WallCellHeight(scale)
                    + " pixels tall");
                Assert.True(LotFacadeGenerator.WallCellWidth(scale) >= 1);
                Assert.True(LotFacadeGenerator.WallAtlasWidth(scale) >= 1);
                Assert.True(LotFacadeGenerator.WallAtlasHeight(1, scale) >= 1);
            }
        }

        [Fact]
        public void AScaleThatIsNotAScaleIsTheRetailSize()
        {
            // Zero is what a caller that never set the field has, and a negative one is a typo. Both
            // have to be the retail atlas rather than a division by nothing: a wall pass that threw
            // in its own target allocation would cost every lot its walls, which is the failure
            // this whole file's log line exists to make visible.
            Assert.Equal(1, LotFacadeGenerator.NormaliseScale(0));
            Assert.Equal(1, LotFacadeGenerator.NormaliseScale(-3));
            Assert.Equal(1, LotFacadeGenerator.NormaliseScale(1));
            Assert.Equal(4, LotFacadeGenerator.NormaliseScale(4));
            //a scale of nothing is the retail cell, not a one pixel one
            Assert.Equal(8, LotFacadeGenerator.WallCellWidth(0));
            Assert.Equal(22, LotFacadeGenerator.WallCellHeight(-7));
            Assert.Equal(512, LotFacadeGenerator.WallAtlasWidth(0, 1));
            Assert.Equal(960, LotFacadeGenerator.WallAtlasHeight(40, 0, 1));
            Assert.Equal(512L * 960L, LotFacadeGenerator.WallAtlasPixels(40, 0, 1));
        }

        [Fact]
        public void TheNeighbourhoodsScaleIsTheOneThatGetsUsed()
        {
            // The two ends have to agree, or the constant in the builder is a comment. A
            // neighbourhood's wall atlas is drawn at a divisor of the retail size for the length of
            // a build and put back afterwards, because LotFacadeGenerator is a class the OBJ
            // exporter shares and the exporter wants the retail size - so the divisor is the
            // builder's to set and the generator's to honour.
            Assert.Equal(2, NeighbourhoodFacadeBuilder.FacadeWallTargetScale);
            Assert.True(NeighbourhoodFacadeBuilder.FacadeWallTargetScale >= 1);
            //and it is a divisor the atlas helpers actually accept: at two, the cell is four
            //pixels per wall tile and eleven pixels of storey, which is still more than a house a
            //few dozen pixels tall is drawn with
            Assert.Equal(4, LotFacadeGenerator.WallCellWidth(NeighbourhoodFacadeBuilder.FacadeWallTargetScale));
            Assert.Equal(11, LotFacadeGenerator.WallCellHeight(NeighbourhoodFacadeBuilder.FacadeWallTargetScale));
        }

        [Fact]
        public void AnAtlasWithNoBinsIsStillAnAtlasTheDeviceWillAccept()
        {
            // A house with no outside rooms has no wall segments, so the pass allocates a target
            // before it has anything to put in it. The height expression is the same one the pass
            // uses, with the same floor at one bin, and it has to round to a multiple of four -
            // which is what the retail supersample's own decimate depends on.
            foreach (var scale in new[] { 1, 2, 4 })
            {
                var height = LotFacadeGenerator.WallAtlasHeight(0, scale, 1);
                Assert.True(height >= 4, "a scale of " + scale + " produced a " + height + " pixel atlas");
                Assert.Equal(0, height % 4);
            }
            //a negative bin count is somebody else's arithmetic error and must not wrap
            Assert.True(LotFacadeGenerator.WallAtlasHeight(-5, 1, 1) > 0);
        }

        [Fact]
        public void TheWallPassLineSaysWhatSizeTheAtlasItReadBackWas()
        {
            // Without this the reduction is a claim. The line is written once per process, and
            // this is the number on it: the atlas a run read back, and the divisor it drew it at,
            // so "the wall pass is cheaper" has a measurement next to it rather than a promise.
            var line = Timings().Describe();

            Assert.Contains("atlas 256x520 at scale 2", line);
            Assert.Contains("133120px read back", line);
        }

        [Fact]
        public void TheWallPassLineSaysHowManyTrianglesTheCellPathSubmitted()
        {
            // The other measurement the line was missing, and the one that answers "is the per-cell
            // path reducing the work or only narrowing the draw calls". A cell that reaches two
            // groups costs two draw calls whether it kept two quads or the whole storey; the
            // triangle count is the number that moves, and the whole-storey figure is what it is
            // measured against.
            var line = Timings().Describe();

            Assert.Contains("480 wall triangle(s) where a whole storey per cell would have been 6400",
                line);
        }

        [Fact]
        public void APassThatRanWithNoCountsAtAllStillPrintsTheAtlasClause()
        {
            // The line is written after something has already gone wrong, from a struct that may
            // have been default-constructed. A line that throws is a line nobody reads, and a
            // line that omits the measurement is a line that cannot be compared against the next
            // run.
            var line = WallPassTimings.None.Describe();

            Assert.Contains("atlas 0x0 at scale 0", line);
            Assert.Contains("0 wall triangle(s) where a whole storey per cell would have been 0", line);
            Assert.Contains("0px read back", line);
        }

        // ---- helpers --------------------------------------------------------------------------

        private static WallPassTimings Timings(double wallDrawMs = 12d)
        {
            var ms = new double[WallPassTimings.PhaseCount];
            ms[(int)WallPassPhase.WallDraw] = wallDrawMs;
            return new WallPassTimings(ms, new WallPassCounts
            {
                Bins = 40,
                Walls = 40,
                LightingPrepares = 4,
                CellDraws = 40,
                WallDrawCalls = 800,
                ObjectsDrawn = 96,
                WallVertices = 160,
                WallIndices = 240,
                WallTriangles = 480,
                WallTrianglesWhole = 6400,
                AtlasWidth = 256,
                AtlasHeight = 520,
                AtlasScale = 2
            }, wallDrawMs + 30d);
        }

        private static int[] Levels(int count)
        {
            var levels = new int[count];
            for (int i = 0; i < count; i++) levels[i] = i % 4;
            return levels;
        }

        private static BoundingBox[] Spread(int count, int across, int deep)
        {
            var bounds = new BoundingBox[count];
            for (int i = 0; i < count; i++)
            {
                bounds[i] = Box(i % across, 0f, i / across % deep);
            }
            return bounds;
        }

        private static BoundingBox Box(float x, float y, float z, float size = 1f)
        {
            var min = new Vector3(x, y, z);
            return new BoundingBox(min, min + new Vector3(size, size, size));
        }

        /// <summary>
        /// A wall segment's camera: an orthographic slab, four tiles square, looking at the middle
        /// of x/z from ten tiles north.
        /// </summary>
        private static BoundingFrustum WallCameraAt(float x, float z)
        {
            var view = Matrix.CreateLookAt(
                new Vector3(x, 2f, z + 10f), new Vector3(x, 2f, z), Vector3.Up);
            return new BoundingFrustum(view * Matrix.CreateOrthographic(4f, 4f, 0f, 20f));
        }

        // ---- the per-cell geometry, which is the wall pass's actual cost --------------------------
        // The pass draws one cell of an atlas at a time and the ordinary draw submits the whole
        // storey each time, so the triangles the device is asked to transform are cells x the whole
        // house. These are the rules that replace that with the cell's own geometry, and the last
        // of them is the one that matters: a group the rules cannot answer for is submitted whole,
        // so none of this can turn a house into a house with no walls.

        [Fact]
        public void AQuadThatReachesACellIsKeptAndOneThatDoesNotIsNot()
        {
            // A two tile wall at the origin on the ground storey, and one eight tiles away on the
            // same storey. The cell is the first one's own rectangle.
            var cell = WallCellPlan.CellBox(new Vector2(0, 0), new Vector2(32, 0), 0);
            var quads = new[]
            {
                new WallQuad(0, 0, new BoundingBox(new Vector3(0, 0, 0), new Vector3(2, 3, 0.1f))),
                new WallQuad(4, 6, new BoundingBox(new Vector3(8, 0, 0), new Vector3(10, 3, 0.1f)))
            };

            Assert.Equal(new[] { 0 }, WallCellPlan.Select(quads, cell));
            //and the count form the buffer builder uses agrees with it
            var plan = WallCellPlan.For(quads, cell);
            Assert.Equal(1, plan.Kept);
            Assert.False(plan.Everything);
        }

        [Fact]
        public void ACellBoxIsTheWallsOwnRectangleOfTheHouseWidenedAndOneStoreyTall()
        {
            // Derived, not chosen. A wall line arrives in sixteenths of a tile; the cell is a
            // projection of that line's own rectangle, so the box has to be that rectangle and a
            // storey's worth of height or the scissor and the cull would disagree about the cell.
            //
            // The axes are the point of this one, and they are half of the bug this file now
            // exists for: a wall vertex sits at (tile, tile, height), so the box's two ground
            // axes are X and Y and the height is Z. It used to be the other way round - the storey
            // on Y, the lot's second axis on Z - and a box with the axes swapped intersects
            // nothing just as silently as one at the wrong scale: no throw, no warning, zero
            // triangles, and a house with facades and no walls on it. The box is therefore also
            // checked against quads built the way WallComponentRC.Generate builds them, in
            // TheCellBoxReachesTheQuadsTheWallComponentActuallyRecords, and this one pins the
            // arithmetic that produces it.
            var box = WallCellPlan.CellBox(new Vector2(32, 96), new Vector2(0, 96), 1);

            //two tiles wide, at x 0..2 and y 6, plus the slack - in tiles, and on X and Y
            Assert.Equal(0f - WallCellPlan.CellSlack, box.Min.X, 4);
            Assert.Equal(2f + WallCellPlan.CellSlack, box.Max.X, 4);
            Assert.Equal(6f - WallCellPlan.CellSlack, box.Min.Y, 4);
            Assert.Equal(6f + WallCellPlan.CellSlack, box.Max.Y, 4);
            //and one storey of the second storey up, on Z
            Assert.Equal(1 * WallCellPlan.StoreyHeight - WallCellPlan.CellSlack, box.Min.Z, 4);
            Assert.Equal(2 * WallCellPlan.StoreyHeight + WallCellPlan.CellSlack, box.Max.Z, 4);
        }

        /// <summary>
        /// The regression for "0 wall group draw(s), 0 wall triangle(s)" - the line a player's log
        /// carried while every house in the neighbourhood had facades and no walls on them.
        ///
        /// <para>
        /// The bug was a coordinate space, and a coordinate-space bug cannot be caught by a test
        /// that only puts the two halves against each other, because they were written to agree:
        /// the wall pass divided the wall line by 16 and <c>WallCellPlan.CellBox</c> divided it by
        /// 16 again, so every cell's box was a sixteenth of a tile across, sixteen times too small,
        /// sitting on the origin of the lot. Every quad was tested against it, every quad came back
        /// <c>Intersects == false</c>, every group was skipped, and the pass drew no walls at all
        /// while reporting a facade, a floor and a roof as though it had.
        /// </para>
        /// <para>
        /// So this builds the quads the way <c>WallComponentRC.Generate</c> records them - a tile
        /// index plus a wall line's end <em>within</em> its tile, which is a fraction - and the cell
        /// the way the pass builds it, from a wall line's ends in <em>sixteenths</em>. One
        /// conversion, in the one place that documents it.
        /// </para>
        /// </summary>
        [Fact]
        public void TheCellBoxReachesTheQuadsTheWallComponentActuallyRecords()
        {
            //A two tile wall on the far side of a 64 tile lot, on the ground storey. The line is
            //stored in sixteenths, which is what Room.WallLines is written in.
            var from = new Vector2(20 * 16, 30 * 16);
            var to = new Vector2(22 * 16, 30 * 16);
            var cell = WallCellPlan.CellBox(from, to, 0);

            //and the quads Generate would have recorded for it
            var quads = new[]
            {
                FenceQuad(0, 0, 20, 30, new Vector2(0, 1), new Vector2(0, 0), 0),
                FenceQuad(4, 6, 21, 30, new Vector2(0, 1), new Vector2(0, 0), 0)
            };

            Assert.Equal(new[] { 0, 1 }, WallCellPlan.Select(quads, cell));
            var plan = WallCellPlan.For(quads, cell);
            Assert.Equal(2, plan.Kept);
            Assert.True(plan.Everything);
        }

        [Fact]
        public void ACellBoxIsSixteenthsOfATileAndNotSixteenthsOfASixteenth()
        {
            // The scale half of the same bug, pinned on its own so that a fix to the axes cannot
            // quietly leave the scale wrong. A two tile line at x 20..22 is a box two tiles wide,
            // and is nowhere near the box an eighth of a tile wide that a caller dividing before
            // handing the line over produced - which is what put every cell's box on the origin of
            // the lot and made the gather find nothing.
            var box = WallCellPlan.CellBox(new Vector2(20 * 16, 30 * 16), new Vector2(22 * 16, 30 * 16), 0);

            Assert.Equal(20f, box.Min.X + WallCellPlan.CellSlack, 4);
            Assert.Equal(22f, box.Max.X - WallCellPlan.CellSlack, 4);
            Assert.Equal(30f, box.Min.Y + WallCellPlan.CellSlack, 4);
            Assert.Equal(30f, box.Max.Y - WallCellPlan.CellSlack, 4);
            //a box that had been divided twice is inside a tile of the origin
            Assert.True(box.Min.X > 1f);
        }

        [Fact]
        public void ACellBoxKeepsItsOwnStoreyAndLeavesTheNextOneOff()
        {
            // What the vertical slack is for, and what it must not cost. The same wall line on the
            // ground storey, on the one above it and two storeys up. Its own is in the cell's box;
            // the storey above is half in, because the slack is half a storey either side and the
            // scissor is what threw the extra away anyway; two storeys up is not in it at all, and
            // that is what keeps a per-cell gather off the rest of the house.
            var from = new Vector2(20 * 16, 30 * 16);
            var to = new Vector2(22 * 16, 30 * 16);
            var cell = WallCellPlan.CellBox(from, to, 0);

            Assert.Equal(new[] { 0, 1 }, WallCellPlan.Select(new[]
            {
                FenceQuad(0, 0, 20, 30, new Vector2(0, 1), new Vector2(0, 0), 0),
                FenceQuad(4, 6, 20, 30, new Vector2(0, 1), new Vector2(0, 0), 1),
                FenceQuad(8, 12, 20, 30, new Vector2(0, 1), new Vector2(0, 0), 2)
            }, cell));
        }

        [Fact]
        public void EveryCellOnAFourByFourLotKeepsItsOwnWall()
        {
            // The form of the regression that cannot be satisfied by a wrong space. A wall line at
            // every tile of a lot, on the ground storey, in three of the shapes Generate makes: a
            // plain fence, a thick wall's return face (whose ends are a wall thickness off the tile
            // edge rather than on it), and a diagonal. For every one of them and every position,
            // the cell's box has to reach the quad that wall line produced - which is what decides
            // whether the pass draws that wall at all.
            //
            // A spot test is not enough here, and that is the lesson: the original box was wrong
            // in a way that a test written beside it could not see, because the caller and the
            // callee had been written to agree. Walking the lot asks the question the other way
            // round - the geometry first, the box second - and a box in the wrong space, at the
            // wrong scale, or with the axes swapped, fails it somewhere.
            var shapes = new[]
            {
                new { Name = "fence", From = new Vector2(0f, 1f), To = new Vector2(0f, 0f) },
                new { Name = "thick", From = new Vector2(0.075f, 1.075f), To = new Vector2(-0.075f, -0.005f) },
                new { Name = "diagonal", From = new Vector2(0.075f, 1.075f), To = new Vector2(1f, 0f) }
            };
            const int tiles = 16;
            var missed = new List<string>();
            foreach (var shape in shapes)
            {
                for (int x = 0; x < tiles; x++)
                {
                    for (int y = 0; y < tiles; y++)
                    {
                        //the line as Room.WallLines stores it, in sixteenths
                        var cell = WallCellPlan.CellBox(
                            new Vector2((x * 16) + (shape.From.X * 16f), (y * 16) + (shape.From.Y * 16f)),
                            new Vector2((x * 16) + (shape.To.X * 16f), (y * 16) + (shape.To.Y * 16f)),
                            0);
                        var quad = FenceQuad(0, 0, x, y, shape.From, shape.To, 0);
                        var kept = WallCellPlan.Select(new[] { quad }, cell);
                        if (kept == null || kept.Length != 1)
                        {
                            missed.Add(shape.Name + " at " + x + "," + y);
                        }
                    }
                }
            }

            Assert.True(missed.Count == 0,
                missed.Count + " of " + (shapes.Length * tiles * tiles) + " cells lost their wall: "
                    + string.Join(", ", missed.ToArray(), 0, Math.Min(8, missed.Count)));
        }

        /// <summary>
        /// One quad of a wall group, built the way <c>WallComponentRC.Generate</c> builds one: a
        /// wall line's two ends <em>within</em> their tile - fractions, not sixteenths - added to
        /// the tile index, with the box running from the lower of the two to the upper of the two
        /// plus one storey of wall. <paramref name="storey"/> is the zero-based storey, which is
        /// what a room's <c>Floor</c> is and what <see cref="WallCellPlan.CellBox"/> takes.
        /// </summary>
        private static WallQuad FenceQuad(int firstVertex, int firstIndex, int tileX, int tileY,
            Vector2 from, Vector2 to, int storey)
        {
            const float wallHeight = 2.95f;
            var floor = storey * wallHeight;
            var p1 = new Vector3(from.X + tileX, from.Y + tileY, floor);
            var p2 = new Vector3(to.X + tileX, to.Y + tileY, floor);
            return new WallQuad(firstVertex, firstIndex, new BoundingBox(
                Vector3.Min(p1, p2), Vector3.Max(p1, p2) + new Vector3(0, 0, wallHeight)));
        }

        [Fact]
        public void ACellBoxIsWideEnoughToCoverTheThingsThatMoveAWallOffItsOwnRectangle()
        {
            // The three: the wall-offset translation WallComponentRC.Draw applies (a quarter of a
            // wall thickness times 0.7, so at most 0.075 world units, and a third of that again in
            // the vertex space the box is in), a thick wall's own return face (0.15 units), and the
            // diagonal caps. Half a tile is more than all three, and a box that is too wide only
            // costs a few quads.
            Assert.True(WallCellPlan.CellSlack > 0.15f);
            Assert.True(WallCellPlan.CellSlack > 0.075f);
            //and it is not so wide that it swallows the rest of the house, which is the whole
            //point: a cell's box has to exclude the walls on the far side of the lot
            Assert.True(WallCellPlan.CellSlack < 4f);
        }

        [Fact]
        public void ACellBoxIsHalfAStoreyTallEitherSideOfItsOwn()
        {
            // The vertical slack, which is the one with a number behind it. A wall vertex sits at
            // InterpAltitude(tile) + storey * storeyHeight, and the box knows nothing about the
            // terrain, so on a sloping lot a wall's own quads are lifted out of a box built on the
            // storey's arithmetic alone. Blueprint.TerrainFactor is 3/160 over a 0..255 terrain
            // range, so the whole possible lift is under five tiles and half a storey is one and a
            // half of them - which is why the slack is stated in storeys rather than tiles here,
            // and why a cell still contains its own wall on a hill.
            Assert.Equal(0.5f, WallCellPlan.CellSlack, 4);
            var half = WallCellPlan.CellSlack * WallCellPlan.StoreyHeight;
            //the whole 0..255 terrain range, at the lot view's own factor, still fits inside it
            Assert.True(half < 255f * (3f / 160f));
            //and a cell's box still does not reach the storey above, which is what keeps the
            //gather to one storey
            var box = WallCellPlan.CellBox(new Vector2(0, 0), new Vector2(16, 0), 0);
            Assert.True(box.Max.Z < WallCellPlan.StoreyHeight * 2f);
        }

        [Fact]
        public void TheStoreyHeightTheCellBoxUsesIsTheStoreyHeightThePlanDrawsWith()
        {
            // Two numbers, one storey, and a factor of three between them: the wall vertices are in
            // tiles and the wall camera is in the three-times-larger world units the effect's world
            // matrix scales by. If either number moves the other has to, or a cell's box is a
            // storey out and a house quietly loses the walls on its upper floors.
            Assert.Equal(WallPassPlan.StoreyHeight / 3f, WallCellPlan.StoreyHeight, 4);
        }

        [Fact]
        public void ARegionIsTheCamerasOwnBoundsAndADegenerateCameraIsNotAUsableOne()
        {
            // The object half of a cell. The region is what the spatial index is asked, so it has
            // to contain everything the camera contains - and a camera with no bounds at all, which
            // a zero length wall line produces by way of CreateOrthographic(0, ...), has to be
            // recognised so the caller falls back rather than drawing an empty cell.
            var frustum = WallCameraAt(20f, 30f);
            var corners = new Vector3[WallCellPlan.FrustumCornerCount];
            var region = WallCellPlan.RegionOf(frustum, corners);

            Assert.True(WallObjectBatch.IsUsable(region));
            Assert.Equal(18f, region.Min.X, 3);
            Assert.Equal(22f, region.Max.X, 3);
            Assert.Equal(20f, region.Min.Z, 3);
            Assert.Equal(40f, region.Max.Z, 3);
            //an object the camera can see is inside the region, or the index would miss it
            Assert.NotEqual(ContainmentType.Disjoint, region.Contains(new Vector3(20f, 2f, 30f)));
            Assert.Equal(ContainmentType.Disjoint, region.Contains(new Vector3(20f, 2f, 44f)));
            //a camera with no bounds at all: CreateOrthographic(0, ...) divides by its own width,
            //so the matrix carries an infinity and the corners carry a NaN
            Assert.False(WallObjectBatch.IsUsable(
                WallCellPlan.RegionOf(DegenerateWallCamera(), new Vector3[WallCellPlan.FrustumCornerCount])));
            //and the three ways a caller can be told "no region", none of which is a box the index
            //could be asked about. Not new BoundingBox(): that is the origin with no extent, which
            //is a box, and asking about it would ask about the objects at the lot's origin.
            Assert.False(WallObjectBatch.IsUsable(WallCellPlan.NoRegion));
            Assert.False(WallObjectBatch.IsUsable(
                WallCellPlan.RegionOf(WallCameraAt(20f, 30f), new Vector3[4])));
            Assert.False(WallObjectBatch.IsUsable(WallCellPlan.RegionOf(null, corners)));
        }

        /// <summary>
        /// The camera a wall line with two identical points produces: the right place, looking the
        /// right way, with a zero width because the line has no length to fill the cell with.
        /// </summary>
        private static BoundingFrustum DegenerateWallCamera()
        {
            var view = Matrix.CreateLookAt(
                new Vector3(20f, 2f, 40f), new Vector3(20f, 2f, 30f), Vector3.Up);
            return new BoundingFrustum(view * Matrix.CreateOrthographic(0f, 2.90f * 3f, 0f, 6f));
        }

        [Fact]
        public void AGroupWithNoRecordedBoxesIsSubmittedWholeRatherThanSkipped()
        {
            // The fallback, and the reason the per-cell path cannot lose a house. A group whose
            //quads were never recorded - geometry built by a path that predates them - keeps all
            //of it, and the scissor still clips, so the answer is the old behaviour rather than
            //nothing.
            var cell = WallCellPlan.CellBox(new Vector2(0, 0), new Vector2(16, 0), 0);

            Assert.Equal(0, WallCellPlan.For(null, cell).Kept);
            Assert.True(WallCellPlan.For(null, cell).Everything);
            Assert.Equal(0, WallCellPlan.For(new List<WallQuad>(), cell).Kept);
            Assert.True(WallCellPlan.For(new List<WallQuad>(), cell).Everything);
            //and Select says the same thing, as "keep them all"
            Assert.Null(WallCellPlan.Select(null, cell));
            Assert.Null(WallCellPlan.Select(new List<WallQuad>(), cell));
        }

        [Fact]
        public void ADegenerateQuadIsKeptRatherThanCulledAway()
        {
            // A wall line whose two points are the same produces a quad with no area, and a box
            //with no area intersects nothing - so a plain box test would silently delete it. The
            //scissor was keeping it, so the plan does too.
            var cell = WallCellPlan.CellBox(new Vector2(0, 0), new Vector2(16, 0), 0);
            var point = new Vector3(0.5f, 1f, 0f);
            var quads = new[]
            {
                new WallQuad(0, 0, new BoundingBox(point, point)),
                new WallQuad(4, 6, new BoundingBox(new Vector3(40, 0, 40), new Vector3(41, 3, 41)))
            };

            var kept = WallCellPlan.Select(quads, cell);
            Assert.NotNull(kept);
            Assert.Single(kept);
            Assert.Equal(0, kept[0]);
        }

        [Fact]
        public void AGroupWhoseEveryQuadReachesTheCellIsReportedAsKeepingEverything()
        {
            // So the buffer builder can take the whole group rather than re-gathering it - and, more
            //importantly, so "kept all" and "kept some" cannot be confused for each other.
            var cell = WallCellPlan.CellBox(new Vector2(0, 0), new Vector2(32, 0), 0);
            var both = new[]
            {
                new WallQuad(0, 0, new BoundingBox(new Vector3(0, 0, 0), new Vector3(2, 3, 0.1f))),
                new WallQuad(4, 6, new BoundingBox(new Vector3(1, 0, 0), new Vector3(3, 3, 0.1f)))
            };

            var plan = WallCellPlan.For(both, cell);
            Assert.Equal(2, plan.Kept);
            Assert.True(plan.Everything);
        }

        // ---- the effect pass apply, which cannot simply be hoisted --------------------------------

        [Fact]
        public void TheWallPassScopeAppliesUntilItHasBeenToldWhatIsBound()
        {
            // The rule the scope exists for. It has to apply for the first group of a pass, because
            // the apply is what binds the samplers - and then it may skip only while the pair is
            // bit-for-bit the one already bound. A bare hoist - apply once, set the two textures
            // per group - draws every group with the first group's wall texture, because in
            // MonoGame 3.8.5.1 EffectPass.Apply is what copies a texture parameter onto the device.
            //
            //Both textures stand in as nulls here, because a Texture2D cannot be made without a
            //graphics device and the comparison is a reference comparison. What that leaves
            //untested is the "a different texture still applies" half, which is the trivial half
            //of a != b.
            var scope = new WallPassScope();

            Assert.True(scope.NeedsApply(null, null));
            scope.Remember(null, null);
            Assert.False(scope.NeedsApply(null, null));
            //remembering again is idempotent, so a scope can be handed to every cell of a pass
            scope.Remember(null, null);
            Assert.False(scope.NeedsApply(null, null));

            //a caller that has bound the samplers from somewhere else has to say so
            scope.Forget();
            Assert.True(scope.NeedsApply(null, null));
        }

        // ---- the lines that decide all of it, read as source ---------------------------------------
        // Everything above is a rule. These are the places the rules have to be *used*, and all of
        // them are behind a graphics device, so they are read out of the file rather than run. The
        // pair is the bug report: the level the pass asks for, and what it hands to the wall
        // component.

        [Fact]
        public void ThePassClampsTheLevelItAsksForToTheStoreysTheWallComponentHas()
        {
            // The wall pass asks WallComponentRC for a storey by level. A level the component has no
            // dictionary for used to make it return before its own loop, so a lot whose walls were
            // in the storey below came back with an empty atlas and a log line saying the pass had
            // produced no walls. The clamp is the fix, and it has to be against the *geometry's*
            // storey count rather than the house's declared one - which for a TS1 lot is five
            // whatever the house is, because nothing in the house file rewrites it.
            var text = Stripped(Generator());

            Assert.Contains("bp.WCRC?.Storeys", text);
            Assert.Contains("plan.LevelFor(wall.Room.Floor, storeyLimit)", text);
        }

        [Fact]
        public void ThePassDrawsOneCellsGeometryRatherThanTheWholeStoreyPerCell()
        {
            // The cost. The pass draws one cell of the atlas at a time, and the ordinary draw
            //submits the whole storey for each of them, so the triangles submitted were cells x the
            //house. DrawCell submits the cell's own quads out of one pair of scratch buffers.
            var body = BodyOf(Stripped(Generator()), "GenerateWalls", "bool justTexture");

            Assert.Contains("WCRC?.DrawCell(", body);
            Assert.Contains("WallCellPlan.CellBox(", body);
            //and the plain per-cell draw of the whole storey is not what it calls any more
            Assert.DoesNotContain("WCRC?.Draw(gd, world.State", body);
        }

        [Fact]
        public void TheObjectPassIsPerCellAgainAndNotOneUnionDrawnThroughOneCellCamera()
        {
            // The picture, not the cost. The atlas is a set of independent cells, each with its own
            //orthographic camera, so an object two cells can see has to be submitted twice to
            //appear in both. Drawing the union once through the last cell's camera put every object
            //in that one cell and clipped the rest away - the windows that should show furniture
            //showed through to the cell behind.
            var body = BodyOf(Stripped(Generator()), "GenerateWalls", "bool justTexture");

            Assert.Contains("batch.Select(frustrum, wall.Room.Floor - 5, region)", body);
            Assert.Contains("batch.OrderByDrawOrder(state)", body);
            Assert.DoesNotContain("DrawUnion(", body);
            Assert.DoesNotContain("AddToUnion(", body);
        }

        [Fact]
        public void ThePassHandsTheWallLineToTheCellBoxAsItIsStored()
        {
            // The call site, which is where the walls actually went missing.
            //
            // A wall line is stored in sixteenths of a tile and WallCellPlan.CellBox converts it -
            // that is its documented contract and the arithmetic is asserted against it. This call
            // used to convert it *first*, so the box arrived a sixteenth of a tile across and
            // sixteen times too small: it intersected no quad, every group was skipped, the log
            // said "0 wall group draw(s), 0 wall triangle(s)" and the house came out as facades
            // with no walls on them. Nothing threw, because a box test that says "no" is a perfectly
            // good answer to the question it was asked.
            //
            // So the line goes in undivided, and this is here because a test that only exercised
            // CellBox and Generate against each other could not have caught it - the two halves
            // were written to agree, and it was the caller between them that was wrong.
            var body = BodyOf(Stripped(Generator()), "GenerateWalls", "bool justTexture");
            //the call as written, once the line break and the indentation are taken out - so that
            //reformatting the argument list cannot make this pass or fail
            var squeezed = body.Replace("\r", "").Replace("\n", "").Replace(" ", "");

            Assert.Contains("WallCellPlan.CellBox(wall.Points[0],wall.Points[1],wall.Room.Floor)", squeezed);
            //and the sixteen that took the walls away, in either form
            Assert.DoesNotContain("wall.Points[0]/16f", squeezed);
            Assert.DoesNotContain("wall.Points[1]/16f", squeezed);
        }

        [Fact]
        public void ThePassEmptiesItsBinsRatherThanPackingASecondHouseIntoThem()
        {
            // The bins are instance state on a generator that is reused - the OBJ exporter runs
            // this once for the day and once for the night on the same object - and they were
            // appended to, so a second pass re-tested every row the first had filled before it
            // reached the end, and grew the atlas by the whole house again for nothing.
            var body = BodyOf(Stripped(Generator()), "GenerateWalls", "bool justTexture");

            Assert.Contains("WallBins.Clear()", body);
        }

        [Fact]
        public void TheObjectPassAsksTheSpatialIndexWhereToLook()
        {
            // The cost, and the call site that gives it. The query stays per cell, because a cell
            // has its own camera; what changes is that it is handed the camera's own bounds and so
            // looks in the buckets those bounds are in rather than at every object in the lot.
            var body = BodyOf(Stripped(Generator()), "GenerateWalls", "bool justTexture");

            Assert.Contains("WallCellPlan.RegionOf(frustrum, frustumCorners)", body);
            Assert.Contains("batch.Select(frustrum, wall.Room.Floor - 5, region)", body);
            //the corner scratch is made once for the pass rather than once a cell
            Assert.Contains("new Vector3[WallCellPlan.FrustumCornerCount]", body);
        }

        [Fact]
        public void AFailedSurfaceUploadIsReportedRatherThanSwallowedIntoAnEmptyMesh()
        {
            // The reason "the lot's walls, roof and floor all came back empty" was such a bad log
            //line: the surface upload caught everything, so a device that refused all three buffers
            //printed a sentence about the geometry. The catch now names the surface and the throw.
            var body = BodyOf(Stripped(Builder()), "bool ownsTexture", "ref string uploadFailure");

            Assert.Contains("NeighbourhoodFacadePiece.SurfaceUpload", body);
            Assert.Contains("catch (Exception ex)", body);
            Assert.DoesNotContain("catch (Exception)", body);
        }

        [Fact]
        public void APerCellGatherThatFindsNothingIsOverruledRatherThanShipped()
        {
            // The safety net, and the reason it is a rule rather than a hope.
            //
            // The per-cell path can lose a house in exactly one way: a cell box that intersects
            // nothing in a storey that has geometry. Nothing throws, nothing warns, every group is
            // skipped, and the log reports "0 wall group draw(s), 0 wall triangle(s)" - which is
            // what a player saw as a neighbourhood of houses with facades and no walls on them.
            // A correct gather never hits it; a broken one must cost the old whole-storey draw
            // rather than a hole, and the count of how often it happened goes on the line so that
            // a run can tell a fast pass from a correct one that happens to be slow.
            var text = Stripped(RC());
            var body = BodyOf(text, "DrawCell", "BoundingBox cell", "WallPassScope reuse");

            Assert.Contains("GeometryThrough(level) > 0", body);
            Assert.Contains("CountCellGroups(groups, storeys, cell, true,", body);
            Assert.Contains("CellFallbacks++", body);
            //and the fill walk reads the same flag, or the buffers would be sized for a whole
            //storey and filled with a cell's worth
            Assert.Contains("cellWholeStorey ||", body);
            //and the pass resets the count per pass rather than reporting every cell this lot view
            //has ever drawn
            Assert.Contains("ResetCellFallbacks()", Stripped(Generator()));
        }

        // ---- source helpers ------------------------------------------------------------------------

        private static string Generator() => File.ReadAllText(Path.Combine(Root(),
            "FreeSO/TSOClient/tso.world/Facade/LotFacadeGenerator.cs"));

        private static string Builder() => File.ReadAllText(Path.Combine(Root(),
            "Client/Simitone/Simitone.Client/Neighbourhood/NeighbourhoodFacadeBuilder.cs"));

        private static string RC() => File.ReadAllText(Path.Combine(Root(),
            "FreeSO/TSOClient/tso.world/RC/WallComponentRC.cs"));

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FreeSO")))
            {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "could not find the repository root from " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        /// <summary>
        /// Comments and string literals replaced with spaces, keeping every offset, so a brace or a
        /// word inside one cannot move the end of a method body.
        /// </summary>
        private static string Stripped(string source)
        {
            var chars = source.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
                {
                    while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
                }
                else if (chars[i] == '"' || chars[i] == '\'')
                {
                    var quote = chars[i];
                    chars[i++] = ' ';
                    while (i < chars.Length && chars[i] != quote)
                    {
                        if (chars[i] == '\\') chars[i++] = ' ';
                        if (i < chars.Length) chars[i++] = ' ';
                    }
                    if (i < chars.Length) chars[i] = ' ';
                }
            }
            return new string(chars);
        }

        /// <summary>
        /// The body of the first method whose header contains every one of
        /// <paramref name="fragments"/>, from its opening brace to the one that closes it.
        /// </summary>
        private static string BodyOf(string text, params string[] fragments)
        {
            var found = -1;
            foreach (Match header in Regex.Matches(text, @"[\w<>,\[\]\?\. ]+\([^;{]*\)\s*\{"))
            {
                var all = true;
                foreach (var fragment in fragments)
                {
                    if (header.Value.IndexOf(fragment, StringComparison.Ordinal) < 0) { all = false; break; }
                }
                if (all) found = header.Index + header.Length - 1;
            }
            Assert.True(found > 0,
                "no method taking " + string.Join(" and ", fragments) + " was found");

            var depth = 0;
            for (int i = found; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(found, i - found + 1);
            }
            throw new InvalidOperationException("unbalanced braces from offset " + found);
        }
    }
}
