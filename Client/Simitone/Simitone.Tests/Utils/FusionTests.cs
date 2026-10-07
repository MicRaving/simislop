using System;
using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Pins the camera model used by <see cref="DGRP3DFusion"/>. Everything downstream - carve,
    /// surface extraction, UV projection - assumes these two properties hold, and a silent sign or
    /// scale error here would show up only as misplaced geometry in-game:
    /// <list type="bullet">
    /// <item><c>Screen</c> must be the same isometric projection the 2D renderer uses
    /// (<c>WorldSpace.GetScreenFromTile</c>, Near zoom);</item>
    /// <item><c>DepthDir</c> must be exactly the null direction of <c>Screen</c>, so a depth value
    /// moves a point along the view ray without moving its pixel.</item>
    /// </list>
    /// </summary>
    public sealed class FusionTests
    {
        private static readonly int[] Rotations = { 0, 1, 2, 3 };

        [Fact]
        public void Screen_MatchesWorldSpaceIsometricProjection()
        {
            // Near zoom: TilePxWidthHalf=64, TilePxHeightHalf=32, one tile = 3 world units,
            // height unit = 128/sqrt(2) * cos(30) pixels.
            foreach (var (X, Y, Z) in new[] { (0f, 0f, 0f), (3f, 0f, 0f), (0f, 0f, 3f), (1.5f, 2f, -0.5f) })
            {
                var p = new Vector3(X, Y, Z);
                var expected = new Vector2((X / 3f - Z / 3f) * 64f,
                                           (X / 3f + Z / 3f) * 32f - (Y / 3f) * 78.383f);
                var actual = DGRP3DFusion.Screen(0, p);
                Assert.Equal(expected.X, actual.X, 3);
                Assert.Equal(expected.Y, actual.Y, 3);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void DepthDir_DoesNotMoveTheProjectedPixel(int rotation)
        {
            var d = DGRP3DFusion.DepthDir(rotation);
            foreach (var p in new[] { new Vector3(0.4f, 1.1f, -0.7f), new Vector3(-1.3f, 2.6f, 0.9f), new Vector3(0f, 0f, 0f) })
            {
                var a = DGRP3DFusion.Screen(rotation, p);
                var b = DGRP3DFusion.Screen(rotation, p + d * 1.7f);
                // DepthDir is derived from the projection's null space; float error is <1e-3.
                Assert.True(Math.Abs(a.X - b.X) < 1e-2f, $"x moved {a.X} -> {b.X}");
                Assert.True(Math.Abs(a.Y - b.Y) < 1e-2f, $"y moved {a.Y} -> {b.Y}");
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Inverse_RoundTrips_ScreenAndDepth(int rotation)
        {
            var d = DGRP3DFusion.DepthDir(rotation);
            foreach (var p in new[] { new Vector3(0.4f, 1.1f, -0.7f), new Vector3(-1.3f, 2.6f, 0.9f), new Vector3(1.9f, 0.3f, 1.4f) })
            {
                var s = DGRP3DFusion.Screen(rotation, p);
                var t = Vector3.Dot(p, d);
                var back = DGRP3DFusion.Inverse(rotation, s.X, s.Y, t);
                Assert.Equal(p.X, back.X, 2);
                Assert.Equal(p.Y, back.Y, 2);
                Assert.Equal(p.Z, back.Z, 2);
            }
        }

        [Fact]
        public void Rotation_StepsThroughFourRightAngles()
        {
            // A point on the +X ground axis should sweep the four screen quadrants as the world
            // rotates (the isometric projection is symmetric under 90 degree steps).
            var p = new Vector3(3f, 0f, 0f);
            var expected = new[] { new Vector2(64, 32), new Vector2(-64, 32), new Vector2(-64, -32), new Vector2(64, -32) };
            for (int r = 0; r < 4; r++)
            {
                var s = DGRP3DFusion.Screen(r, p);
                Assert.Equal(expected[r].X, s.X, 3);
                Assert.Equal(expected[r].Y, s.Y, 3);
            }
        }
    }
}
