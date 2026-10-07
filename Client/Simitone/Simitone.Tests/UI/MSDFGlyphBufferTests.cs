/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.UI.Framework;
using Microsoft.Xna.Framework;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="MSDFGlyphBuffer"/> - the reusable geometry storage behind the vector
    /// font draw.
    ///
    /// <para>
    /// The finding (R-44) was that every VFont string draw cost a sprite-batch
    /// <c>End</c>/<c>Begin</c> pair and, underneath it, a fresh
    /// <c>Dictionary&lt;MSDFFont, MSDFRenderGroup&gt;</c>, a box, two <c>List</c>s per font and
    /// two array copies for <c>DrawUserIndexedPrimitives</c> - six or so allocations for every
    /// label, every frame.
    /// </para>
    ///
    /// <para>
    /// The submit stays per string: the UI draws in tree order and glyph quads are interleaved
    /// with sprite-batch quads in that order, so batching a frame's glyphs into one flush would
    /// put the first dialog's caption on top of the last dialog. What is testable - and what was
    /// actually the cost - is the allocation behaviour of the geometry, and that the geometry and
    /// the submission order are byte for byte what they were.
    /// </para>
    /// </summary>
    public sealed class MSDFGlyphBufferTests
    {
        private static readonly Vector2 Derivative = new Vector2(0.5f, 0.5f);

        /// <summary>
        /// Stands in for an <c>MSDFFont</c>. The buffer only needs a reference-identity key, and
        /// an <c>MSDFFont</c> cannot be built without a font file and a graphics device - which
        /// is exactly why the key is untyped.
        /// </summary>
        private sealed class FakeFont
        {
            public string Name;
            public FakeFont(string name) { Name = name; }
        }

        private static void AddQuad(MSDFGlyphBuffer buffer, int run, float x)
        {
            buffer.AddQuad(run,
                new Vector2(x, 0f), new Vector2(x + 10f, -12f),
                new Vector2(0.1f, 0.9f), new Vector2(0.2f, 0.1f), Derivative);
        }

        [Fact]
        public void AQuadIsFourVerticesAndSixIndices()
        {
            var buffer = new MSDFGlyphBuffer();
            buffer.Begin();
            var run = buffer.BeginRun(new FakeFont("a"));

            AddQuad(buffer, run, 0f);
            AddQuad(buffer, run, 10f);

            Assert.Equal(1, buffer.RunCount);
            Assert.Equal(4 * 2, buffer.RunVertexCount(run));
            Assert.Equal(6 * 2, buffer.RunIndexCount(run));
            // PrimitiveType.TriangleList, so this is the primitive count the draw call is given.
            Assert.Equal(4, buffer.RunIndexCount(run) / 3);
        }

        [Fact]
        public void IndicesAreRelativeToTheRunNotTheBuffer()
        {
            // The second quad in a run has to index from 4, or the two quads draw on top of each
            // other. The vertex base is the run's own vertex count at the time of the add, and the
            // two triangles are b,b+1,b+2 then b+2,b+3,b.
            var buffer = new MSDFGlyphBuffer();
            buffer.Begin();
            var run = buffer.BeginRun(new FakeFont("a"));

            AddQuad(buffer, run, 0f);
            AddQuad(buffer, run, 10f);

            buffer.GetIndices(run, out var indices, out var count);
            Assert.Equal(12, count);
            Assert.Equal(new[] { 0, 1, 2, 2, 3, 0 }, indices[0..6]);
            Assert.Equal(new[] { 4, 5, 6, 6, 7, 4 }, indices[6..12]);
        }

        [Fact]
        public void TheVertexOrderAndTheWindingAreUnchanged()
        {
            // The exact four corners MSDFFont.RenderQuad used to emit, in the order it emitted
            // them. If this changes, glyphs render mirrored or inside out, which no compile error
            // and no other unit test would notice.
            var buffer = new MSDFGlyphBuffer();
            buffer.Begin();
            var run = buffer.BeginRun(new FakeFont("a"));
            buffer.AddQuad(run,
                new Vector2(1f, 2f), new Vector2(3f, 4f),
                new Vector2(5f, 6f), new Vector2(7f, 8f), Derivative);

            buffer.GetVertices(run, out var verts, out var count);
            Assert.Equal(4, count);

            Assert.Equal(new Vector3(3f, 2f, 0f), verts[0].Position);
            Assert.Equal(new Vector3(1f, 2f, 0f), verts[1].Position);
            Assert.Equal(new Vector3(1f, 4f, 0f), verts[2].Position);
            Assert.Equal(new Vector3(3f, 4f, 0f), verts[3].Position);

            Assert.Equal(new Vector2(7f, 6f), verts[0].TextureCoordinate);
            Assert.Equal(new Vector2(5f, 6f), verts[1].TextureCoordinate);
            Assert.Equal(new Vector2(5f, 8f), verts[2].TextureCoordinate);
            Assert.Equal(new Vector2(7f, 8f), verts[3].TextureCoordinate);

            Assert.Equal(Derivative, verts[0].Derivative);
            Assert.Equal(Derivative, verts[3].Derivative);
        }

        [Fact]
        public void ReenteringAFontReturnsTheSameRunAndKeepsItsQuads()
        {
            // A string drawn with a primary font and a fallback, then back to the primary: the
            // two primary stretches are one submission, which is what the Dictionary this
            // replaced did. Getting this wrong would sample the fallback's atlas for the primary
            // glyphs, or drop them.
            var primary = new FakeFont("primary");
            var fallback = new FakeFont("fallback");
            var buffer = new MSDFGlyphBuffer();

            buffer.Begin();
            var p = buffer.BeginRun(primary);
            AddQuad(buffer, p, 0f);

            var f = buffer.BeginRun(fallback);
            AddQuad(buffer, f, 10f);

            var pAgain = buffer.BeginRun(primary);
            AddQuad(buffer, pAgain, 20f);

            Assert.Equal(p, pAgain);
            Assert.NotEqual(p, f);
            Assert.Equal(2, buffer.RunCount);
            // Two quads on the primary font, one on the fallback: four vertices each.
            Assert.Equal(8, buffer.RunVertexCount(p));
            Assert.Equal(4, buffer.RunVertexCount(f));
        }

        [Fact]
        public void ANewRunStartsEmptyEvenForAKeyUsedInAPreviousString()
        {
            // Same font, next string. The run object itself is reused, so the handle is the same -
            // what has to restart is its counter, or the second Draw would submit the first
            // string's glyphs again.
            var font = new FakeFont("primary");
            var buffer = new MSDFGlyphBuffer();

            buffer.Begin();
            var first = buffer.BeginRun(font);
            AddQuad(buffer, first, 0f);
            AddQuad(buffer, first, 10f);
            Assert.Equal(8, buffer.RunVertexCount(first));

            buffer.Begin();
            var second = buffer.BeginRun(font);
            AddQuad(buffer, second, 0f);

            // The same run is handed back, so the handle is the same; the counter is what
            // restarts, and it has to be 1 quad rather than 3.
            Assert.Equal(first, second);
            Assert.Equal(4, buffer.RunVertexCount(second));
            Assert.Equal(6, buffer.RunIndexCount(second));
        }

        [Fact]
        public void RunsComeOutInFirstUseOrder()
        {
            // The old code iterated a Dictionary's Values, which is insertion order as long as
            // nothing is removed - so submission order was first use order, and it has to stay
            // that way.
            var a = new FakeFont("a");
            var b = new FakeFont("b");
            var c = new FakeFont("c");
            var buffer = new MSDFGlyphBuffer();

            buffer.Begin();
            buffer.BeginRun(a);
            buffer.BeginRun(b);
            buffer.BeginRun(a);
            buffer.BeginRun(c);

            Assert.Equal(3, buffer.RunCount);
            Assert.Same(a, buffer.RunKey(0));
            Assert.Same(b, buffer.RunKey(1));
            Assert.Same(c, buffer.RunKey(2));
        }

        [Fact]
        public void BeginClearsTheRunCount()
        {
            var buffer = new MSDFGlyphBuffer();
            buffer.Begin();
            var run = buffer.BeginRun(new FakeFont("a"));
            AddQuad(buffer, run, 0f);
            AddQuad(buffer, run, 10f);

            buffer.Begin();
            Assert.Equal(0, buffer.RunCount);
        }

        [Fact]
        public void AStringLongerThanTheInitialCapacityGrows()
        {
            // 64 vertices is 16 glyphs. A 40 character label is well past that and has to work.
            var buffer = new MSDFGlyphBuffer();
            buffer.Begin();
            var run = buffer.BeginRun(new FakeFont("a"));
            for (var i = 0; i < 200; i++)
            {
                AddQuad(buffer, run, i * 10f);
            }

            Assert.Equal(800, buffer.RunVertexCount(run));
            Assert.Equal(1200, buffer.RunIndexCount(run));
            Assert.True(buffer.VertexCapacity(run) >= 800);
            Assert.True(buffer.IndexCapacity(run) >= 1200);
        }

        [Fact]
        public void CapacityIsNeverGivenBackSoASteadyFrameStopsAllocating()
        {
            // The point of the change. The old form allocated a dictionary, a box, two lists and
            // two arrays per label per frame; this asserts the buffer reaches a fixed point and
            // stays there, so a screen full of labels stops producing garbage after the first
            // frame that needed the room.
            var buffer = new MSDFGlyphBuffer();
            var font = new FakeFont("a");

            // Warm up with a long string.
            buffer.Begin();
            var warm = buffer.BeginRun(font);
            for (var i = 0; i < 120; i++) { AddQuad(buffer, warm, i * 10f); }
            var warmGrowth = buffer.GrowthCount;
            var warmVertices = buffer.VertexCapacity(0);
            var warmIndices = buffer.IndexCapacity(0);

            // Then a hundred frames of ordinary labels.
            for (var frame = 0; frame < 100; frame++)
            {
                buffer.Begin();
                var run = buffer.BeginRun(font);
                for (var i = 0; i < 12; i++) { AddQuad(buffer, run, i * 10f); }
                Assert.Equal(warmGrowth, buffer.GrowthCount);
                Assert.Equal(warmVertices, buffer.VertexCapacity(0));
                Assert.Equal(warmIndices, buffer.IndexCapacity(0));
            }
        }

        [Fact]
        public void EveryFrameSeesTheSameGeometry()
        {
            // Reuse must not leak state between strings: the counts, the indices and the
            // positions have to come out identical on every repetition.
            var buffer = new MSDFGlyphBuffer();
            var font = new FakeFont("a");

            int[] firstIndices = null;
            Vector3[] firstPositions = null;

            for (var frame = 0; frame < 5; frame++)
            {
                buffer.Begin();
                var run = buffer.BeginRun(font);
                AddQuad(buffer, run, 0f);
                AddQuad(buffer, run, 10f);

                buffer.GetIndices(run, out var indices, out _);
                buffer.GetVertices(run, out var verts, out _);

                var positions = new[] { verts[0].Position, verts[1].Position, verts[2].Position, verts[3].Position };
                if (firstIndices == null)
                {
                    firstIndices = (int[])indices.Clone();
                    firstPositions = positions;
                }
                else
                {
                    Assert.Equal(firstIndices, indices);
                    Assert.Equal(firstPositions, positions);
                }
            }
        }

        [Fact]
        public void TheNumberOfFontsIsNotCapped()
        {
            // A cap would silently draw the overflow with the wrong atlas. Two fonts is enough
            // to leave the initial run array; a dozen has to keep working.
            var buffer = new MSDFGlyphBuffer();
            buffer.Begin();
            var fonts = new FakeFont[12];
            for (var i = 0; i < fonts.Length; i++)
            {
                fonts[i] = new FakeFont("font" + i);
                AddQuad(buffer, buffer.BeginRun(fonts[i]), i * 10f);
            }

            Assert.Equal(fonts.Length, buffer.RunCount);
            for (var i = 0; i < fonts.Length; i++)
            {
                Assert.Same(fonts[i], buffer.RunKey(i));
                Assert.Equal(4, buffer.RunVertexCount(i));
            }
        }
    }
}
