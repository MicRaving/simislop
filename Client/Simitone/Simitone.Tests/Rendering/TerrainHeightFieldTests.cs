using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using System;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for <see cref="TerrainHeightField"/> - the terrain's bilinear surface and the normal
    /// that comes off it, which is what <c>Blueprint.InterpNormal</c> gives
    /// <c>AvatarComponent.AltitudeNormal</c>.
    ///
    /// <para>
    /// The height interpolation is old and works. The normal is a gradient of that same
    /// interpolation, and its x half was wrong:
    /// </para>
    ///
    /// <code>
    /// float xDist1 = (h10 - h00) * TerrainFactor;   // d/dx on the baseY row
    /// float xDist2 = (h11 - h01) * TerrainFactor;   // d/dx on the nextY row
    /// float xChange = xLerp * xDist1 + (1 - xLerp) * xDist2;   // &lt;- weighted by the X fraction
    /// float yChange = (xl2 - xl1) * TerrainFactor;            // correct, and worth leaving alone
    /// </code>
    ///
    /// <para>
    /// <c>xDist1</c> and <c>xDist2</c> are the slope along x of two different <em>rows</em>, so
    /// what blends them is how far along y you are - the y fraction, not the x one. Weighting them
    /// by the x fraction reads the wrong end of the quad.
    /// </para>
    ///
    /// <para>
    /// The y half gets a note, because the audit proposed changing it and it should not be. The
    /// proposed <c>yLerp * (xl2 - xl1)</c> is a secant over the sub-interval between the near row
    /// and where you are standing, not a derivative. For a bilinear patch the y slope is constant
    /// in y - that is what bilinear <em>is</em> - so scaling it by yLerp makes the ground shade
    /// flat at one tile boundary and twice as steep at the other, and the two adjacent tiles then
    /// disagree. The engine already answers the same question a second way, in
    /// <c>TerrainComponent.GetNormalAt</c>, as a central difference - and that is what the terrain
    /// mesh's own vertex normals are built from, so it is what the avatar's normal has to agree
    /// with. The load-bearing test here is
    /// <see cref="TheGradientIsTheGradientOfTheAltitudeTheEngineComputes"/>, which differentiates
    /// the altitude formula numerically and compares, so the rule being pinned is "the normal and
    /// the height describe the same surface" rather than a number anyone wrote down twice.
    /// </para>
    /// </summary>
    public sealed class TerrainHeightFieldTests
    {
        private const float Factor = 3 / 160f;
        private const float Tolerance = 1e-4f;

        /// <summary>A quad whose four corners are all at the same height.</summary>
        private const float Flat = 100f;

        // ---- flat ground -------------------------------------------------------------------

        [Fact]
        public void FlatGroundHasNoGradient()
        {
            var gradient = TerrainHeightField.Gradient(Flat, Flat, Flat, Flat, 0.3f, 0.7f, Factor);

            Assert.Equal(0f, gradient.X, Tolerance);
            Assert.Equal(0f, gradient.Y, Tolerance);
        }

        [Fact]
        public void FlatGroundFacesStraightUp()
        {
            foreach (var x in new[] { 0f, 0.25f, 0.5f, 1f })
            {
                foreach (var y in new[] { 0f, 0.5f, 1f })
                {
                    var normal = TerrainHeightField.Normal(Flat, Flat, Flat, Flat, x, y, Factor);
                    Assert.True(Math.Abs(normal.X) < Tolerance && Math.Abs(normal.Z) < Tolerance
                        && Math.Abs(normal.Y - 1f) < Tolerance,
                        "flat ground gave " + normal + " at " + x + "," + y);
                }
            }
        }

        // ---- the x difference is blended by the y fraction ------------------------------------

        [Fact]
        public void TheXGradientFollowsTheYFractionAndNotTheXOne()
        {
            // A quad whose x slope is zero on the near row and non-zero on the far row. Read at
            // yLerp = 0 the answer must be the near row's slope, at yLerp = 1 the far row's, and
            // in between a blend of the two. Weighting by xLerp instead gives the far row's slope
            // at yLerp = 0 whenever xLerp happens to be 0 and the near row's at the far edge - the
            // same two numbers, attached to the wrong ends.
            const float nearSlope = 2f;
            const float farSlope = 20f;
            float h00 = 100f, h10 = 100f + nearSlope, h01 = 100f, h11 = 100f + farSlope;

            var atNearEdge = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.9f, 0f, Factor);
            var atFarEdge = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.9f, 1f, Factor);
            var halfway = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.9f, 0.5f, Factor);

            Assert.Equal(nearSlope * Factor, atNearEdge.X, Tolerance);
            Assert.Equal(farSlope * Factor, atFarEdge.X, Tolerance);
            Assert.Equal((nearSlope + farSlope) / 2f * Factor, halfway.X, Tolerance);
        }

        [Fact]
        public void TheXGradientDoesNotCareWhereAlongXYouAre()
        {
            // The x difference is a function of the row alone, so sampling the same row at
            // different x fractions has to give the same x gradient. It did not when the blend was
            // weighted by the x fraction.
            const float h00 = 5f, h10 = 11f, h01 = 30f, h11 = 60f;

            foreach (var y in new[] { 0.2f, 0.5f, 0.8f })
            {
                var first = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.1f, y, Factor).X;
                var second = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.7f, y, Factor).X;
                Assert.Equal(first, second, 1e-3f);
            }
        }

        [Fact]
        public void ARampThatOnlyRunsInYHasNoXGradientAnywhere()
        {
            // h00 = h10 and h01 = h11, so every row has zero x slope and every interpolation of
            // them does too. With the blend weighted by x this held as well, which is exactly why
            // it is worth stating separately from the diagonal case: it is the case the old code
            // got right by accident, and it is why a screenshot never caught it.
            var gradient = TerrainHeightField.Gradient(10f, 40f, 10f, 40f, 0.7f, 0.2f, Factor);

            Assert.Equal(0f, gradient.X, Tolerance);
            Assert.True(gradient.Y > 0f, "a ramp rising in y should have a positive dH/dy: " + gradient);
        }

        [Fact]
        public void APerfectlyAxisAlignedXRampIsAlsoUnaffected()
        {
            // The other accident. h01 = h00 and h11 = h10, so the two rows are parallel and the
            // x slope is the same in both; any weighting gives the same answer.
            var gradient = TerrainHeightField.Gradient(4f, 4f, 30f, 30f, 0.6f, 0.9f, Factor);

            Assert.Equal(26f * Factor, gradient.X, Tolerance);
            Assert.Equal(0f, gradient.Y, Tolerance);
        }

        // ---- the y difference does not move ---------------------------------------------------

        [Fact]
        public void TheYGradientIsTheSameEverywhereAlongTheTile()
        {
            // The property of a bilinear patch, and the one a yLerp factor would break. A ramp
            // rising in y has the same slope at the bottom of the tile as at the top; shading it
            // differently as you walk up is the artefact.
            const float h00 = 0f, h10 = 0f, h01 = 64f, h11 = 64f;

            foreach (var x in new[] { 0f, 0.25f, 0.5f, 1f })
            {
                foreach (var y in new[] { 0f, 0.5f, 1f })
                {
                    var gradient = TerrainHeightField.Gradient(h00, h01, h10, h11, x, y, Factor);
                    Assert.Equal(64f * Factor, gradient.Y, Tolerance);
                }
            }
        }

        [Fact]
        public void TheYGradientIsTheRowDifferenceAndNothingElse()
        {
            const float h00 = 3f, h10 = 11f, h01 = 20f, h11 = 44f;

            // far - near at the middle of the quad: 0.5*(44+20)/... written out.
            var expected = ((h01 + h11) - (h00 + h10)) / 2f;
            var gradient = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.5f, 0.5f, Factor);

            Assert.Equal(expected * Factor, gradient.Y, Tolerance);
        }

        [Fact]
        public void ARampLooksTheSameAtTheTopOfATileAsAtTheBottom()
        {
            // The user-visible half of the above: a pure y ramp is a constant lean in z, so the
            // normal does not drift as an avatar walks up the slope. With a yLerp factor on the y
            // gradient, the normal at yLerp = 0 would be dead vertical.
            const float h00 = 0f, h10 = 0f, h01 = 64f, h11 = 64f;

            var bottom = TerrainHeightField.Normal(h00, h01, h10, h11, 0.5f, 0f, Factor);
            var middle = TerrainHeightField.Normal(h00, h01, h10, h11, 0.5f, 0.5f, Factor);
            var top = TerrainHeightField.Normal(h00, h01, h10, h11, 0.5f, 1f, Factor);

            Assert.Equal(bottom, middle);
            Assert.Equal(middle, top);
            Assert.True(bottom.Z < 0f, "a ramp rising in +y should lean its normal towards -z: " + bottom);
        }

        // ---- the two axes are the same rule applied twice --------------------------------------

        [Fact]
        public void MirroringTheQuadAcrossItsDiagonalSwapsTheTwoGradientComponents()
        {
            // The function is symmetric under transposition - the two axes are handled by the same
            // two lines - so reading the transposed quad at the transposed point has to give the
            // two components the other way round. (A quarter *turn* would additionally flip one
            // sign, which is a fact about rotations rather than about this code.)
            float h00 = 3f, h01 = 17f, h10 = 41f, h11 = 92f;
            const float xLerp = 0.3f, yLerp = 0.6f;

            var upright = TerrainHeightField.Gradient(h00, h01, h10, h11, xLerp, yLerp, Factor);
            var mirrored = TerrainHeightField.Gradient(h00, h10, h01, h11, yLerp, xLerp, Factor);

            Assert.Equal(upright.X, mirrored.Y, 1e-3f);
            Assert.Equal(upright.Y, mirrored.X, 1e-3f);
        }

        // ---- it really is the gradient of the altitude -----------------------------------------

        [Fact]
        public void TheGradientIsTheGradientOfTheAltitudeTheEngineComputes()
        {
            // The load-bearing test. Blueprint.InterpAltitude's formula, transcribed, differentiated
            // by central differences, and compared against the analytic gradient. If the two ever
            // disagree then the normal is shading a surface that is not the one the lot is drawn
            // on - which is the whole of this finding, and the reason the rule is written as
            // "derivative of the height" rather than as a pair of blends that look reasonable.
            var quads = new (float h00, float h01, float h10, float h11)[]
            {
                (0f, 0f, 0f, 0f),                 // flat
                (0f, 0f, 7f, 7f),                 // ramp along y
                (0f, 40f, 12f, 3f),               // tilted both ways, unevenly
                (-30f, 5f, 90f, -14f),            // signs disagreeing
                (1000f, 1000f, 1004f, 996f),      // small relief on a high plateau
            };
            const float Step = 1e-3f;

            foreach (var q in quads)
            {
                foreach (var xLerp in new[] { 0.1f, 0.37f, 0.5f, 0.83f })
                {
                    foreach (var yLerp in new[] { 0.1f, 0.42f, 0.5f, 0.91f })
                    {
                        var analytic = TerrainHeightField.Gradient(q.h00, q.h01, q.h10, q.h11, xLerp, yLerp, Factor);

                        var dHdx = (Altitude(q, xLerp + Step, yLerp) - Altitude(q, xLerp - Step, yLerp)) / (2 * Step);
                        var dHdy = (Altitude(q, xLerp, yLerp + Step) - Altitude(q, xLerp, yLerp - Step)) / (2 * Step);

                        Assert.True(Math.Abs(analytic.X - dHdx) < 1e-2f,
                            "dH/dx at " + xLerp + "," + yLerp + " for " + q + ": analytic " + analytic.X + ", numeric " + dHdx);
                        Assert.True(Math.Abs(analytic.Y - dHdy) < 1e-2f,
                            "dH/dy at " + xLerp + "," + yLerp + " for " + q + ": analytic " + analytic.Y + ", numeric " + dHdy);
                    }
                }
            }
        }

        [Fact]
        public void TheGradientIsContinuousAcrossTheSeamBetweenTiles()
        {
            // Standing at the far edge of one tile is standing at the near edge of the next, so the
            // gradient has to agree there - and since the y gradient is constant in y it agrees
            // across the y seam automatically, which is the property that stops adjacent tiles from
            // shading at different strengths on the same continuous slope.
            float h00 = 4f, h10 = 19f, h01 = 25f, h11 = 33f;

            var topOfLower = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.5f, 1f, Factor);
            var bottomOfUpper = TerrainHeightField.Gradient(h01, h01, h11, h11, 0.5f, 0f, Factor);

            Assert.Equal(topOfLower.X, bottomOfUpper.X, 1e-3f);
        }

        // ---- scaling, direction and shape -------------------------------------------------------

        [Fact]
        public void TheTerrainFactorScalesTheGradientAndNothingElse()
        {
            const float h00 = 0f, h10 = 9f, h01 = 21f, h11 = 4f;

            var single = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.3f, 0.7f, 1f);
            var tripled = TerrainHeightField.Gradient(h00, h01, h10, h11, 0.3f, 0.7f, 3f);

            Assert.Equal(single.X * 3f, tripled.X, Tolerance);
            Assert.Equal(single.Y * 3f, tripled.Y, Tolerance);
        }

        [Fact]
        public void AGroundRisingInXTiltsTheNormalBackAgainstX()
        {
            // d/dx is positive, so the surface leans away from +x and the up-facing normal leans
            // towards -x. Stated because the sign is the difference between a hill and a pit.
            var normal = TerrainHeightField.Normal(0f, 0f, 10f, 10f, 0.5f, 0.5f, 1f);

            Assert.True(normal.X < 0f, "a ramp rising in +x should tilt its normal towards -x: " + normal);
            Assert.True(normal.Y > 0f, "the normal must still point upwards: " + normal);
        }

        [Fact]
        public void TheNormalIsAlwaysUnitLengthAndNeverUndefined()
        {
            // The cross product of a vector on the x axis with one on the z axis can never be zero,
            // so the normalise is always defined - including on flat ground. Asserted over a range
            // of gradients including the degenerate ones, so that a future sign flip cannot quietly
            // produce a NaN reaching a render target.
            foreach (var slope in new[] { -200f, -1f, 0f, 1f, 200f })
            {
                foreach (var other in new[] { -200f, 0f, 200f })
                {
                    var normal = TerrainHeightField.Normal(0f, other, slope, slope + other, 0.5f, 0.5f, 1f);

                    Assert.False(float.IsNaN(normal.X) || float.IsNaN(normal.Y) || float.IsNaN(normal.Z),
                        "NaN for a gradient of " + slope + "," + other);
                    Assert.Equal(1f, normal.Length(), 1e-3f);
                    Assert.True(normal.Y > 0f, "the normal must point up: " + normal);
                }
            }
        }

        [Fact]
        public void ABackwardsSlopeDoesNotFlipTheNormalDownwards()
        {
            // A gradient steep enough to dominate the cross product's Y term is still a surface
            // you stand on the top of, and the caller negates the cross product precisely so that
            // the answer comes back facing up.
            var cliff = TerrainHeightField.Normal(0f, 0f, 0f, 4000f, 0.5f, 0.5f, 1f);

            Assert.True(cliff.Y > 0f, "even a cliff faces up: " + cliff);
            Assert.Equal(1f, cliff.Length(), 1e-3f);
        }

        // ---- the x interpolation both paths share ----------------------------------------------

        [Fact]
        public void TheRowInterpolationIsTheOneTheAltitudeSamplerUses()
        {
            Assert.Equal(10f, TerrainHeightField.InterpolateAlongX(0f, 10f, 99f), Tolerance);
            Assert.Equal(99f, TerrainHeightField.InterpolateAlongX(1f, 10f, 99f), Tolerance);
            Assert.Equal(54.5f, TerrainHeightField.InterpolateAlongX(0.5f, 10f, 99f), Tolerance);
        }

        // ---- helpers ---------------------------------------------------------------------------

        /// <summary>
        /// <c>Blueprint.InterpAltitude</c>'s formula for a point inside one quad of the height
        /// field, without the BaseAlt offset (which is a constant and does not affect a
        /// difference). Transcribed rather than called, because a Blueprint needs content and a
        /// graphics device to construct.
        /// </summary>
        private static float Altitude((float h00, float h01, float h10, float h11) q, float xLerp, float yLerp)
        {
            var near = xLerp * q.h10 + (1 - xLerp) * q.h00;
            var far = xLerp * q.h11 + (1 - xLerp) * q.h01;
            return (yLerp * far + (1 - yLerp) * near) * Factor;
        }
    }
}
