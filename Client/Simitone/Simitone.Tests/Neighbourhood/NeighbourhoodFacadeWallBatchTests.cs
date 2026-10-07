/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using FSO.LotView.Facade;
using Microsoft.Xna.Framework;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// How a facade's wall pass decides which of a lot's objects each wall segment draws.
    ///
    /// <para><b>Why this is worth a test at all.</b> The wall pass draws every wall of a house into
    /// one atlas, and it draws them one at a time, each with its own little orthographic camera
    /// scissored to one cell of the atlas. The only thing that changes from wall to wall is that
    /// camera - the lot's objects and their bounds do not move - and the query that used to be run
    /// per wall rebuilt all of that from scratch every time: a forty-segment house with two hundred
    /// objects is eight thousand bound evaluations and forty sort buffers to answer the same
    /// question forty times. <c>WallObjectBatch</c> gathers the bounds once and filters per wall
    /// out of that, and the property that makes it worth having is only visible as a number: the
    /// bounds are read once per build, whatever the number of walls.</para>
    ///
    /// <para><b>What cannot be tested here, and is not.</b> The draw order itself. An object's
    /// draw order is a function of the camera the lot view was last given, and asking for it needs
    /// a lot view state and the objects themselves. What is tested is the half that decides
    /// <em>which</em> objects are asked: the set, and the order they come out in - which is what
    /// makes the stable sort in <c>OrderByDrawOrder</c> a replacement for the old
    /// <c>OrderBy</c> rather than an approximation of it.</para>
    /// </summary>
    public class NeighbourhoodFacadeWallBatchTests
    {
        /// <summary>One object, as the batch sees it: a storey and a box, with no object in it.</summary>
        private struct Standing
        {
            public int Level;
            public BoundingBox Bounds;
        }

        private static BoundingBox Box(float x, float y, float z, float size = 1f)
        {
            var min = new Vector3(x, y, z);
            return new BoundingBox(min, min + new Vector3(size, size, size));
        }

        /// <summary>A batch of boxes, one storey each, with nothing to draw.</summary>
        private static WallObjectBatch BatchOf(params Standing[] objects)
        {
            var levels = new int[objects.Length];
            var bounds = new BoundingBox[objects.Length];
            for (int i = 0; i < objects.Length; i++)
            {
                levels[i] = objects[i].Level;
                bounds[i] = objects[i].Bounds;
            }
            return WallObjectBatch.FromBounds(levels, bounds);
        }

        private static Standing On(int level, float x, float z)
        {
            return new Standing { Level = level, Bounds = Box(x, 0f, z) };
        }

        /// <summary>
        /// The camera one wall segment is drawn with: an orthographic slab, four tiles square,
        /// looking at the middle of <paramref name="x"/>/<paramref name="z"/> from ten tiles north.
        /// </summary>
        private static BoundingFrustum WallCameraAt(float x, float z)
        {
            var view = Matrix.CreateLookAt(
                new Vector3(x, 2f, z + 10f), new Vector3(x, 2f, z), Vector3.Up);
            return new BoundingFrustum(view * Matrix.CreateOrthographic(4f, 4f, 0f, 20f));
        }

        [Fact]
        public void AnObjectsBoundsAreReadOncePerBuildAndNotOncePerWall()
        {
            // The whole of the fix, as a number. Forty wall segments over a two hundred object lot
            // used to be 8,000 GetBounds calls; the batch reads 200, whatever the wall count.
            const int objects = 200;
            const int walls = 40;
            var levels = new int[objects];
            var bounds = new BoundingBox[objects];
            for (int i = 0; i < objects; i++)
            {
                levels[i] = i % 4;
                bounds[i] = Box(i % 16, 0f, i / 16);
            }
            var batch = WallObjectBatch.FromBounds(levels, bounds);
            Assert.Equal(objects, batch.BoundsRead);

            for (int wall = 0; wall < walls; wall++)
            {
                batch.Select(WallCameraAt(wall % 16, wall / 16), -4);
            }

            Assert.Equal(objects, batch.BoundsRead);
            //the filter still looked at every object on every wall - that is the part that is
            //genuinely per wall - so the saving is in the bounds, not in the comparisons
            Assert.Equal((long)objects * walls, batch.Checks);
        }

        [Fact]
        public void AWallTakesTheObjectsItsOwnCameraCanSee()
        {
            // Three objects, one camera, one answer. A frustum test over a slab four tiles square
            // is a real test of a real matrix, so the rule is exercised rather than restated.
            var batch = BatchOf(On(1, 0f, 0f), On(1, 10f, 0f), On(1, 20f, 0f));

            var chosen = batch.Select(WallCameraAt(10f, 0f), -4);

            Assert.Equal(1, chosen);
            Assert.Equal(1, batch.SelectedCount);
        }

        [Fact]
        public void TheSelectionIsInTheLotsOwnOrderNotTheCameras()
        {
            // The order the objects come out in is what the draw-order sort is stable against, so
            // it has to be the order the lot's own object list had, and not the order a bounding
            // box test happened to produce.
            var batch = BatchOf(
                On(1, 0f, 0f), On(1, 10f, 0f), On(1, 10.2f, 0f), On(1, 10.4f, 0f));

            var chosen = batch.Select(WallCameraAt(10f, 0f), -4);

            Assert.Equal(3, chosen);
            //three of the four are inside the slab, and the first of them is the first that is
            Assert.Equal(1, batch.Selected[0]);
            Assert.Equal(2, batch.Selected[1]);
            Assert.Equal(3, batch.Selected[2]);
        }

        [Fact]
        public void AWallOnlySeesObjectsFromFiveStoreysBelowItDownwards()
        {
            // The storey filter is the other half of the old Where and it is per wall, because it
            // is a function of the wall: `x.Level >= wall.Room.Floor - 5`. It is a floor and not a
            // ceiling - a wall never stops looking at what is above it, it stops looking at what is
            // far enough below it. On a one-storey house, which is every stock TS1 house, the bound
            // is -5 and the filter never bites; it starts to on a wall several storeys up.
            var batch = BatchOf(
                On(0, 10f, 0f), On(1, 10f, 0f), On(2, 10f, 0f), On(3, 10f, 0f));
            var camera = WallCameraAt(10f, 0f);

            //a ground floor wall, which is what a stock one-storey house has: everything
            Assert.Equal(4, batch.Select(camera, 0 - 5));
            //a wall on the third storey still looks through the window at the ground floor
            Assert.Equal(4, batch.Select(camera, 3 - 5));
            //and past that the lowest storeys drop out of the atlas, lowest first
            Assert.Equal(3, batch.Select(camera, 1));
            Assert.Equal(2, batch.Select(camera, 2));
            Assert.Equal(1, batch.Select(camera, 3));
            Assert.Equal(0, batch.Select(camera, 4));

            //the last selection is the one the draw-order sort and the draw then work on
            Assert.Equal(0, batch.SelectedCount);
        }

        [Fact]
        public void ALotWithNothingInItDrawsNothing()
        {
            var batch = WallObjectBatch.Gather(null);
            Assert.Equal(0, batch.Count);
            Assert.Equal(0, batch.Select(WallCameraAt(0f, 0f), -4));
            Assert.Null(batch.Objects);
        }

        [Fact]
        public void AnEmptyBatchIsEmptyRatherThanNull()
        {
            // A lot with no objects at all is an empty plot, and the wall pass still runs for it.
            var batch = BatchOf();
            Assert.Equal(0, batch.Count);
            Assert.Equal(0, batch.BoundsRead);
            Assert.Equal(0, batch.Select(WallCameraAt(0f, 0f), -4));

            //and asking twice does not make the counts disagree: one pass is one set of bounds
            batch.Select(WallCameraAt(0f, 0f), -4);
            Assert.Equal(0, batch.BoundsRead);
            Assert.Equal(0, batch.Checks);
        }

        [Fact]
        public void ABatchOfMismatchedArraysIsARefusalRatherThanAWrongAnswer()
        {
            // Levels and bounds are two views of one list of objects. A batch handed one of each
            // at different lengths would otherwise silently filter against the wrong object's
            // storey, which is a house with furniture in the wrong place.
            Assert.Throws<ArgumentException>(() => WallObjectBatch.FromBounds(
                new[] { 1, 2 }, new[] { Box(0f, 0f, 0f) }));
        }
    }
}
