/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using System;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Tests for <see cref="_2DStandaloneSprite.TryBuildQuad"/>, the dirty-range half of the 2D
    /// object-sprite partial vertex buffer update.
    /// <para>
    /// The lot view used to allocate a four corner <c>_2DSpriteVertex</c> array and push all four
    /// corners to the GPU on every <c>PrepareVertices</c> call, and <c>DGRPRenderer</c> threw the
    /// sprites (and their vertex buffers) away and rebuilt them on every zoom step or rotation. The
    /// roadmap claimed that had been replaced by a partial update; it had not. These tests pin the
    /// two properties that make the partial update worth having, both of which are invisible in a
    /// screenshot:
    /// </para>
    /// <list type="number">
    /// <item><b>Equivalence.</b> The corners produced have to be exactly what the old code
    /// produced, corner for corner, or every 2D object in the game moves or mis-lights. Corner
    /// order, flip handling and the uv arithmetic are asserted against literals rather than against
    /// the implementation.</item>
    /// <item><b>Minimality.</b> The reported range has to be the smallest contiguous run covering
    /// the change, and an unchanged re-preparation has to report no change at all - that second
    /// case is what lets <c>PrepareVertices</c> skip the driver call entirely, so it is the one
    /// that actually removes work rather than shrinking it.</item>
    /// </list>
    /// <para>
    /// The function is deliberately free of any graphics device or GPU resource, which is what makes
    /// it testable at all; the device side of it is a few lines of buffer plumbing.
    /// </para>
    /// </summary>
    public sealed class StandaloneSpriteQuadTests
    {
        private const int TexW = 128;
        private const int TexH = 64;

        private static Rectangle Src(int w = 16, int h = 16) => new Rectangle(0, 0, w, h);

        private static _2DSpriteVertex[] New() => new _2DSpriteVertex[4];

        private static bool Build(_2DSpriteVertex[] dest, Rectangle src, Rectangle dst,
            out int first, out int last,
            bool flipH = false, bool flipV = false, Vector3? world = null,
            float objectID = 0, ushort room = 0, sbyte floor = 1)
        {
            return _2DStandaloneSprite.TryBuildQuad(dest, src, dst, flipH, flipV, TexW, TexH,
                world ?? Vector3.Zero, objectID, room, floor, out first, out last);
        }

        // ---- equivalence with the pre-existing corner layout -------------------------------------

        [Fact]
        public void ProducesTheFourCornersInTopLeftTopRightBottomRightBottomLeftOrder()
        {
            var dest = New();
            Assert.True(Build(dest, Src(), new Rectangle(10, 20, 16, 8), out var first, out var last));
            Assert.Equal(0, first);
            Assert.Equal(3, last);

            Assert.Equal(new Vector3(10, 20, 0), dest[0].Position);
            Assert.Equal(new Vector3(26, 20, 0), dest[1].Position);
            Assert.Equal(new Vector3(26, 28, 0), dest[2].Position);
            Assert.Equal(new Vector3(10, 28, 0), dest[3].Position);
        }

        [Fact]
        public void NormalisesUvsAgainstTheWholeTextureNotTheSourceRect()
        {
            var dest = New();
            // A source rect in the bottom right of a 128x64 texture. If the code divided by the
            // source rect size instead of the texture size this would come out as all zeroes.
            Assert.True(Build(dest, new Rectangle(64, 32, 16, 16), new Rectangle(0, 0, 16, 16),
                out _, out _));

            Assert.Equal(0.5f, dest[0].TextureCoordinate.X);
            Assert.Equal(0.5f, dest[0].TextureCoordinate.Y);
            Assert.Equal(0.625f, dest[1].TextureCoordinate.X);
            Assert.Equal(0.75f, dest[3].TextureCoordinate.Y);
        }

        [Fact]
        public void HorizontalFlipSwapsLeftAndRightUvsButNotScreenPositions()
        {
            var flipped = New();
            Assert.True(Build(flipped, new Rectangle(0, 0, 16, 16), new Rectangle(10, 20, 16, 8),
                out _, out _, flipH: true));

            // Screen positions are unaffected by the flip: only the sampled region changes.
            Assert.Equal(new Vector3(10, 20, 0), flipped[0].Position);
            Assert.Equal(new Vector3(26, 20, 0), flipped[1].Position);

            // The top left corner now samples what the top right corner used to. A rect spanning
            // x 0..16 of a 128 wide texture: 16/128 = 0.125.
            Assert.Equal(0.125f, flipped[0].TextureCoordinate.X);
            Assert.Equal(0f, flipped[1].TextureCoordinate.X);
        }

        [Fact]
        public void VerticalFlipSwapsTopAndBottomUvsButNotScreenPositions()
        {
            var flipped = New();
            Assert.True(Build(flipped, new Rectangle(0, 0, 16, 32), new Rectangle(10, 20, 16, 8),
                out _, out _, flipV: true));

            Assert.Equal(new Vector3(10, 20, 0), flipped[0].Position);
            Assert.Equal(new Vector3(10, 28, 0), flipped[3].Position);

            // The top edge now samples the bottom of the source rect: 32/64 = 0.5.
            Assert.Equal(0.5f, flipped[0].TextureCoordinate.Y);
            Assert.Equal(0f, flipped[3].TextureCoordinate.Y);
        }

        [Fact]
        public void BothFlipsTogetherAreIndependentOfEachOther()
        {
            var dest = New();
            Assert.True(Build(dest, new Rectangle(0, 0, 16, 32), new Rectangle(0, 0, 16, 16),
                out _, out _, flipH: true, flipV: true));

            // top left samples (16, 32), top right samples (0, 32).
            Assert.Equal(0.125f, dest[0].TextureCoordinate.X);
            Assert.Equal(0.5f, dest[0].TextureCoordinate.Y);
            Assert.Equal(0f, dest[1].TextureCoordinate.X);
            Assert.Equal(0.5f, dest[1].TextureCoordinate.Y);
        }

        [Fact]
        public void EncodesObjectIdAndRoomTheSameWayTheVertexConstructorDoes()
        {
            var dest = New();
            // The values the 2D world batch shader unpacks: object id in 0..1, floor in the y of the
            // same vector, and the room split into two bytes.
            Assert.True(Build(dest, Src(), new Rectangle(0, 0, 16, 16), out _, out _,
                objectID: 65535, room: 0x1234, floor: 3));

            var expected = new _2DSpriteVertex(Vector3.Zero, Vector2.Zero, Vector3.Zero, 65535, 0x1234, 3);
            Assert.Equal(expected.ObjectID, dest[0].ObjectID);
            Assert.Equal(expected.Room, dest[0].Room);
            Assert.Equal(new Vector2(1f, 2f), dest[0].ObjectID);
            Assert.Equal(new Vector2(0x34 / 256f, 0x12 / 256f), dest[0].Room);
        }

        [Fact]
        public void CarriesTheWorldPositionOntoEveryCorner()
        {
            var world = new Vector3(1.5f, -2.25f, 3f);
            var dest = New();
            Assert.True(Build(dest, Src(), new Rectangle(0, 0, 16, 16), out _, out _, world: world));

            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(world, dest[i].WorldPosition);
            }
        }

        // ---- minimality of the reported dirty range ----------------------------------------------

        [Fact]
        public void ReportsNoChangeWhenNothingMoved()
        {
            var dest = New();
            Assert.True(Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _));

            // This is the whole point of the partial update: a revalidation that changes nothing
            // must not touch the GPU at all.
            Assert.False(Build(dest, Src(), new Rectangle(10, 20, 16, 8), out var first, out var last));
            Assert.Equal(-1, first);
            Assert.Equal(-1, last);
        }

        [Fact]
        public void RepeatedPreparationIsIdempotent()
        {
            var dest = New();
            Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _);
            for (var i = 0; i < 5; i++)
            {
                Assert.False(Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _));
            }
        }

        [Fact]
        public void ReportsTheWholeQuadWhenThePositionMovesByAnIntegerPixel()
        {
            // A scroll, a zoom step or a rotation re-offsets every corner by the same amount, so
            // all four are dirty. The partial update cannot help here, and the range has to say so
            // rather than under-reporting and leaving stale corners on the GPU.
            var dest = New();
            Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _);

            Assert.True(Build(dest, Src(), new Rectangle(11, 21, 16, 8), out var first, out var last));
            Assert.Equal(0, first);
            Assert.Equal(3, last);
        }

        [Fact]
        public void ReportsOnlyTheRightHandCornersWhenOnlyTheRightEdgeMoves()
        {
            var dest = New();
            Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _);

            // Width 16 -> 17 moves the right edge (corners 1 and 2) and nothing else. Corners 0
            // and 3 keep the same screen position, uv, world position and light, so their GPU
            // copies are still correct.
            Assert.True(Build(dest, Src(), new Rectangle(10, 20, 17, 8), out var first, out var last));
            Assert.Equal(1, first);
            Assert.Equal(2, last);

            Assert.Equal(new Vector3(10, 20, 0), dest[0].Position);
            Assert.Equal(new Vector3(27, 20, 0), dest[1].Position);
        }

        [Fact]
        public void ReportsOnlyTheTopCornersWhenOnlyTheTopEdgeMoves()
        {
            var dest = New();
            Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _);

            // Top edge 20 -> 21, bottom edge pinned at 28 (so the height shrinks by one). The top
            // left and top right corners move; the bottom two do not, so the dirty run is [0,1].
            Assert.True(Build(dest, Src(), new Rectangle(10, 21, 16, 7), out var first, out var last));
            Assert.Equal(0, first);
            Assert.Equal(1, last);

            Assert.Equal(new Vector3(10, 21, 0), dest[0].Position);
            Assert.Equal(new Vector3(10, 28, 0), dest[3].Position);
        }

        [Fact]
        public void ReportsOnlyTheBottomCornersWhenOnlyTheBottomEdgeMoves()
        {
            var dest = New();
            Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _);

            Assert.True(Build(dest, Src(), new Rectangle(10, 20, 16, 9), out var first, out var last));
            Assert.Equal(2, first);
            Assert.Equal(3, last);
        }

        [Fact]
        public void ReportsAChangeWhenTheRoomChangesEvenThoughTheScreenQuadIsIdentical()
        {
            // Room is packed into the vertex for the ambient light lookup, so a room change has to
            // dirty the corners even though nothing about the quad on screen moved. If this ever
            // regressed to a position/uv-only comparison, objects would keep sampling the light of
            // the room they used to be in.
            var dest = New();
            Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _, room: 4);

            Assert.True(Build(dest, Src(), new Rectangle(10, 20, 16, 8), out var first, out var last, room: 5));
            Assert.Equal(0, first);
            Assert.Equal(3, last);
        }

        [Fact]
        public void ReportsAChangeWhenTheFloorChanges()
        {
            var dest = New();
            Build(dest, Src(), new Rectangle(10, 20, 16, 8), out _, out _, floor: 1);

            Assert.True(Build(dest, Src(), new Rectangle(10, 20, 16, 8), out var first, out var last, floor: 2));
            Assert.Equal(0, first);
            Assert.Equal(3, last);
        }

        [Fact]
        public void ReportsAChangeWhenTheSourceRectMoves()
        {
            // Same quad on screen, different region of the texture sampled. Only the uvs change.
            var dest = New();
            Build(dest, new Rectangle(0, 0, 16, 16), new Rectangle(10, 20, 16, 16), out _, out _);

            Assert.True(Build(dest, new Rectangle(32, 0, 16, 16), new Rectangle(10, 20, 16, 16),
                out var first, out var last));
            Assert.Equal(0, first);
            Assert.Equal(3, last);
            Assert.Equal(0.25f, dest[0].TextureCoordinate.X);
        }

        [Fact]
        public void DirtyRangeAlwaysCoversEveryCornerThatDiffers()
        {
            // The invariant behind the partial upload: after a preparation, every corner outside the
            // reported range must still hold what the GPU has, and every corner inside it must hold
            // the new value. If a corner that actually differed fell outside the range, the upload
            // would leave a stale corner behind and the sprite would be drawn from a mix of two
            // states. Swept over destination rectangles so an off-by-one in the range is caught
            // whichever corner it lands on.
            var baselineRect = new Rectangle(20, 20, 16, 16);
            var world = new Vector3(1, 2, 3);
            var baseline = New();
            Build(baseline, Src(16, 16), baselineRect, out _, out _,
                world: world, objectID: 7, room: 9, floor: 1);

            var sizes = new[] { 1, 8, 15, 16, 17, 20 };
            var heights = new[] { 1, 8, 16, 17 };

            foreach (var x in new[] { 0, 19, 20, 21, 39 })
            {
                foreach (var y in new[] { 0, 19, 20, 21, 39 })
                {
                    foreach (var w in sizes)
                    {
                        foreach (var h in heights)
                        {
                            var dst = new Rectangle(x, y, w, h);
                            var scratch = (_2DSpriteVertex[])baseline.Clone();

                            var changed = Build(scratch, Src(16, 16), dst, out var first, out var last,
                                world: world, objectID: 7, room: 9, floor: 1);

                            Assert.Equal(dst != baselineRect, changed);

                            for (var i = 0; i < 4; i++)
                            {
                                var inside = changed && i >= first && i <= last;
                                var differs = !scratch[i].ContentEquals(baseline[i]);
                                // A corner the range does not cover must be byte for byte the value
                                // the GPU still has, which is what makes skipping it safe.
                                if (!inside) Assert.False(differs);
                                // A corner that did change must be inside the range, or the upload
                                // would leave a stale corner on the GPU and the sprite would be drawn
                                // from a mix of two states. (The converse does not hold: the range is
                                // contiguous, so a corner can be covered and still match - as when
                                // the top and bottom corners move but the two between them do not.)
                                if (differs) Assert.True(inside);
                            }
                        }
                    }
                }
            }
        }

        // ---- the vertex comparison itself -------------------------------------------------------

        [Fact]
        public void ContentEqualsComparesEveryFieldNotJustPosition()
        {
            var a = new _2DSpriteVertex(new Vector3(1, 2, 0), new Vector2(0.1f, 0.2f),
                new Vector3(3, 4, 5), 100, 300, 2);

            Assert.True(a.ContentEquals(a));

            Assert.False(a.ContentEquals(new _2DSpriteVertex(new Vector3(1, 2, 0.5f), new Vector2(0.1f, 0.2f),
                new Vector3(3, 4, 5), 100, 300, 2)));
            Assert.False(a.ContentEquals(new _2DSpriteVertex(new Vector3(1, 2, 0), new Vector2(0.3f, 0.2f),
                new Vector3(3, 4, 5), 100, 300, 2)));
            Assert.False(a.ContentEquals(new _2DSpriteVertex(new Vector3(1, 2, 0), new Vector2(0.1f, 0.2f),
                new Vector3(3, 4, 6), 100, 300, 2)));
            // Same object id, different room.
            Assert.False(a.ContentEquals(new _2DSpriteVertex(new Vector3(1, 2, 0), new Vector2(0.1f, 0.2f),
                new Vector3(3, 4, 5), 100, 301, 2)));
            // Same room, different floor.
            Assert.False(a.ContentEquals(new _2DSpriteVertex(new Vector3(1, 2, 0), new Vector2(0.1f, 0.2f),
                new Vector3(3, 4, 5), 100, 300, 3)));
        }

        [Fact]
        public void VertexStrideMatchesTheStructLayout()
        {
            // The SetData(data, startIndex, elementCount) partial update works in elements, so a
            // wrong stride here would silently smear the wrong number of bytes across the buffer.
            Assert.Equal(48, _2DSpriteVertex.SizeInBytes);
            Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf(typeof(_2DSpriteVertex)));
        }
    }
}
