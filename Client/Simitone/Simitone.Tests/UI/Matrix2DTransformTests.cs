/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="Matrix2D"/>'s transforms, and through them for
    /// <see cref="UIElement.LocalRect(float, float, float, float, float[])"/>.
    ///
    /// <para>
    /// The bug: <c>TransformPoint(float[] M, ref float x, ref float y)</c> overwrote <c>x</c> and
    /// then computed <c>y</c> from the <em>new</em> <c>x</c>.
    /// </para>
    /// <code>
    /// x = x * M[0] + y * M[2] + M[4];
    /// y = x * M[1] + y * M[3] + M[5];   // reads the line above's result
    /// </code>
    /// <para>
    /// The two <c>Vector2</c>-returning overloads right next to it were always correct, and the
    /// in-place one is the one <c>UIElement.LocalRect</c> uses. So the error is invisible for the
    /// transform most of the UI actually has - a translation plus a scale, where <c>M[1] == 0</c>
    /// and the stray term vanishes - and wrong the moment a container rotates
    /// (<c>M[1] != 0</c>) or has a non-uniform <c>ScaleX</c>/<c>ScaleY</c> with a nonzero
    /// translation. Those are exactly the two cases the audit could not rule out, and they are
    /// the two below.
    /// </para>
    /// <para>
    /// Why it matters beyond arithmetic: <c>LocalRect</c> is not decoration. It is
    /// <c>UIRectangle</c>'s mouse region, <c>UIMotiveDisplay</c>'s bars,
    /// <c>UIRelationshipDialog</c>'s, <c>UITextEdit</c>'s text frame border and
    /// <c>UISecureTradeEOD</c>'s offer rectangles. A wrong rect there is a wrong hit region.
    /// </para>
    /// <para>
    /// Nothing here needs a graphics device: <c>Matrix2D</c> is six floats, and a
    /// <see cref="UIElement"/> subclass is constructible with no device as long as nothing draws.
    /// </para>
    /// </summary>
    public sealed class Matrix2DTransformTests
    {
        /// <summary>The in-place overload must agree with the value-returning one, always.</summary>
        public static TheoryData<string> Transforms()
        {
            return new TheoryData<string>
            {
                "identity",
                "translate",
                "uniform scale",
                "non-uniform scale",
                "non-uniform scale + translation",
                "rotation 90",
                "rotation 30 + non-uniform scale + translation",
            };
        }

        private static float[] Build(string kind)
        {
            var m = Matrix2D.IDENTITY.CloneMatrix();
            switch (kind)
            {
                case "identity": break;
                case "translate":
                    m.Translate(37, -12);
                    break;
                case "uniform scale":
                    m.Scale(2f, 2f);
                    m.Translate(5, 5);
                    break;
                case "non-uniform scale":
                    // No translation, so this one is the case the bug cannot see.
                    m.Scale(3f, 0.5f);
                    break;
                case "non-uniform scale + translation":
                    m.Scale(3f, 0.5f);
                    m.Translate(17, -4);
                    break;
                case "rotation 90":
                    m.Rotate(MathHelper.PiOver2);
                    break;
                case "rotation 30 + non-uniform scale + translation":
                    m.Scale(2.5f, 0.75f);
                    m.Translate(11, 29);
                    m.Rotate(MathHelper.Pi / 6f);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
            return m;
        }

        [Theory]
        [MemberData(nameof(Transforms))]
        public void TheInPlaceOverloadAgreesWithTheValueReturningOverload(string kind)
        {
            var m = Build(kind);
            var expected = m.TransformPoint(13f, -7f);

            float x = 13f;
            float y = -7f;
            m.TransformPoint(ref x, ref y);

            Assert.Equal(expected.X, x, 4);
            Assert.Equal(expected.Y, y, 4);
        }

        [Theory]
        [MemberData(nameof(Transforms))]
        public void TheInPlaceOverloadAgreesOnEveryCornerOfARectangle(string kind)
        {
            // The parallelogram identity. LocalRect transforms the top-left corner and scales the
            // width/height, so if the transform is right for one point it is right for all of
            // them; the two <c>Vector2</c> overloads always were, and this says so independently
            // of the in-place one. The old in-place code failed the y half of it - the second
            // y below comes out as a scaled copy of the x.
            var m = Build(kind);
            var origin = m.TransformPoint(Vector2.Zero);
            var right = m.TransformPoint(new Vector2(40, 0));
            var down = m.TransformPoint(new Vector2(0, 25));
            var far = m.TransformPoint(new Vector2(40, 25));

            var rightStep = right - origin;
            var downStep = down - origin;

            // Moving right then down lands in the same place as down then right.
            AssertClose(origin + rightStep + downStep, far);
            AssertClose(right + downStep, far);
            AssertClose(down + rightStep, far);
        }

        private static void AssertClose(Vector2 expected, Vector2 actual)
        {
            Assert.True(Math.Abs(expected.X - actual.X) < 0.001f && Math.Abs(expected.Y - actual.Y) < 0.001f,
                "expected " + expected + " but got " + actual);
        }

        [Fact]
        public void RotationPutsTheOriginWhereTheMatrixSaysItGoes()
        {
            // A quarter turn about the origin: (x, y) -> (-y, x). The old code produced
            // (x*M0 + y*M2, (x*M0 + y*M2)*M1 + y*M3), i.e. the x answer used again for y.
            var m = Matrix2D.IDENTITY.CloneMatrix();
            m.Rotate(MathHelper.PiOver2);

            float x = 10f;
            float y = 0f;
            m.TransformPoint(ref x, ref y);

            Assert.Equal(0f, x, 4);
            Assert.Equal(10f, y, 4);
        }

        [Fact]
        public void ARotatedRectKeepsItsWidthAndHeight()
        {
            // The shape of the bug's blast radius: a rotated UI container's LocalRect came back
            // with a corner in the wrong place, because the y it used was built out of the x it
            // had already overwritten. LocalRect is UIRectangle's mouse region, UIMotiveDisplay's
            // bars and UITextEdit's frame border, so a wrong corner is a wrong hit region.
            var m = Matrix2D.IDENTITY.CloneMatrix();
            m.Rotate(MathHelper.PiOver2);
            var element = new RectHost();

            // A quarter turn about the origin sends (x, y) to (-y, x), so the rect whose top-left
            // corner is at (10, 20) has that corner at (-20, 10). LocalRect truncates to whole
            // pixels, so allow a pixel of rounding either way.
            var expected = m.TransformPoint(10f, 20f);
            var rect = element.LocalRect(10, 20, 100, 40, m);

            Assert.True(Math.Abs(rect.X - expected.X) <= 1f,
                "corner x was " + rect.X + ", expected about " + expected.X);
            Assert.True(Math.Abs(rect.Y - expected.Y) <= 1f,
                "corner y was " + rect.Y + ", expected about " + expected.Y);

            // The size is exact: it is the element's own scale, not the transform's.
            Assert.Equal(100, rect.Width);
            Assert.Equal(40, rect.Height);
        }

        [Fact]
        public void TheOldTransformPutARotatedRectSomewhereElseEntirely()
        {
            // The bug, as a literal. The old code computed
            //     x = x*M0 + y*M2 + M4
            //     y = x*M1 + y*M3 + M5   // <- reads the new x
            // For a quarter turn M1 == 1, so y came out as the already-rotated x plus a sliver of
            // the original y. Pinned so that "fixing" the overload back is a test failure.
            var m = new float[] { 0, 1, -1, 0, 0, 0 };

            float x = 10f;
            float y = 20f;
            // Reproduce the old arithmetic deliberately, using the value-returning overload on
            // the new x rather than the in-place one.
            x = m.TransformPoint(x, y).X;
            y = m.TransformPoint(x, y).Y;

            float ox = 10f;
            float oy = 20f;
            m.TransformPoint(ref ox, ref oy);

            Assert.Equal(-20f, ox, 4);
            Assert.Equal(10f, oy, 4);
            Assert.NotEqual(oy, y, 3);
        }

        [Fact]
        public void NonUniformScaleWithTranslationPutsTheRectWhereItBelongs()
        {
            // 3x on X and 0.5x on Y, then moved to (100, 40). Note the translation is in the
            // scaled space, so the matrix is [3, 0, 0, 0.5, 300, 20] and a point at (10, 20)
            // lands at (330, 30). A pure non-uniform scale has M1 == 0, which is why this is the
            // case the bug cannot see; the rotated case above is the one it destroys.
            var m = Matrix2D.IDENTITY.CloneMatrix();
            m.Scale(3f, 0.5f);
            m.Translate(100, 40);

            var element = new RectHost();
            var rect = element.LocalRect(10, 20, 100, 40, m);

            Assert.Equal(330, rect.X);
            Assert.Equal(30, rect.Y);
            Assert.Equal(100, rect.Width);
            Assert.Equal(40, rect.Height);
        }

        [Fact]
        public void InvertRoundTripsThroughBothOverloads()
        {
            var m = Matrix2D.IDENTITY.CloneMatrix();
            m.Scale(2.5f, 0.75f);
            m.Translate(11, 29);
            m.Rotate(MathHelper.Pi / 6f);
            var inverse = m.Invert();

            float x = 41f;
            float y = -13f;
            m.TransformPoint(ref x, ref y);
            inverse.TransformPoint(ref x, ref y);

            Assert.Equal(41f, x, 3);
            Assert.Equal(-13f, y, 3);
        }

        [Fact]
        public void TheIdentityIsNotSharedBetweenCallers()
        {
            // Matrix2D.IDENTITY used to be a public static float[] handed out by reference, so a
            // stray write to it corrupted the transform every element in the game starts from.
            var a = Matrix2D.IDENTITY;
            var b = Matrix2D.IDENTITY;
            Assert.NotSame(a, b);

            a[0] = 99f;
            Assert.Equal(1f, Matrix2D.IDENTITY[0]);
            Assert.Equal(1f, b[0]);
        }

        /// <summary>
        /// A minimal concrete element. <see cref="UIElement"/> is abstract only over
        /// <c>Draw</c>, and nothing here draws, so no graphics device is touched.
        /// </summary>
        private sealed class RectHost : UIElement
        {
            public override void Draw(UISpriteBatch batch) { }
        }
    }
}
