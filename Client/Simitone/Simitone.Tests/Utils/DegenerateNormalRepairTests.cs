/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.GenerateMeshes;
using System;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Tests for <see cref="DegenerateNormalRepair"/>, the fix for the one real defect in the
    /// generated mesh set: non-finite normals on degenerate faces
    /// (<c>docs/MESH_INGESTION_REPORT.md</c> §4 and §7.2).
    ///
    /// <para>
    /// The property being protected is not "the NaN is gone from this one list" but the general
    /// one: <b>every normal a generator writes is a finite unit vector</b>. A shader normalises and
    /// dots against it, so a single <c>NaN</c> does not fail one triangle - it poisons the shading
    /// of a whole pixel, and it survives a round trip through a gzip container that nothing in the
    /// pipeline ever re-validates. So the tests below assert the invariant over a variety of
    /// degenerate inputs rather than replaying one measured file.
    /// </para>
    ///
    /// <para>
    /// The inputs are built to match what the fusion pass actually produces:
    /// <c>DGRP3DVert.GenerateNormals</c> is called per <c>FusionGeometry</c>, so a vertex can only
    /// be reached by its own geometry's faces, and a bad vertex's accumulated normal is exactly
    /// <c>Vector3.Zero</c> on the way in - which <c>Normalize()</c> turns into
    /// <c>(NaN, NaN, NaN)</c>. The tests therefore hand the repair both the pre-normalise zero
    /// and the post-normalise NaN, because it has to cope with either.
    /// </para>
    /// </summary>
    public sealed class DegenerateNormalRepairTests
    {
        private static DGRP3DVert Vert(Vector3 position, Vector3 normal)
        {
            return new DGRP3DVert(position, normal, Vector2.Zero);
        }

        /// <summary>A vertex every property in the list has to satisfy after the repair.</summary>
        private static void AssertUsable(DGRP3DVert v, string what)
        {
            Assert.True(DegenerateNormalRepair.IsFinite(v.Normal), what + " has a non-finite normal: " + v.Normal);
            Assert.True(v.Normal.LengthSquared() > 0f, what + " has a zero-length normal.");
            // Unit length to within float precision: Vector3.Normalize divides by the length, so
            // this is a couple of ulps rather than exact.
            Assert.Equal(1f, v.Normal.Length(), 3);
        }

        // ---- the reported defect, reproduced ---------------------------------------------------

        [Fact]
        public void TheExactNaNTheNormalPassProducesIsRepaired()
        {
            // What GenerateNormals leaves behind for a zero accumulated normal: NaN in all three
            // components, which is what §4 of the ingestion report found in 2,043 files.
            var nan = new Vector3(float.NaN, float.NaN, float.NaN);

            var geometry = new FusionGeometry();
            geometry.Verts.Add(Vert(new Vector3(0, 0, 0), new Vector3(0, 1, 0)));
            geometry.Verts.Add(Vert(new Vector3(1, 0, 0), nan));

            var repaired = DegenerateNormalRepair.Repair(geometry);

            Assert.Equal(1, repaired);
            AssertUsable(geometry.Verts[0], "the good vertex");
            AssertUsable(geometry.Verts[1], "the NaN vertex");
        }

        [Fact]
        public void AZeroNormalIsRepairedEvenThoughItIsNotNaN()
        {
            // The state before Normalize() gets to it. A finite zero would sail past a NaN-only
            // check and then become NaN at the shader, so it has to be caught here.
            var geometry = new FusionGeometry();
            geometry.Verts.Add(Vert(Vector3.Zero, Vector3.Zero));

            Assert.Equal(1, DegenerateNormalRepair.Repair(geometry));
            AssertUsable(geometry.Verts[0], "the zero-normal vertex");
        }

        [Theory]
        [InlineData(float.NaN, 0f, 0f)]
        [InlineData(0f, float.NaN, 0f)]
        [InlineData(0f, 0f, float.NaN)]
        [InlineData(float.PositiveInfinity, 0f, 0f)]
        [InlineData(0f, float.NegativeInfinity, 0f)]
        public void OneBadComponentIsEnough(float x, float y, float z)
        {
            // A single NaN in the triple is the whole defect, so the test is not about all three
            // at once - a partial check would pass the real defect and fail this one.
            var geometry = new FusionGeometry();
            geometry.Verts.Add(Vert(Vector3.Zero, new Vector3(x, y, z)));

            Assert.Equal(1, DegenerateNormalRepair.Repair(geometry));
            AssertUsable(geometry.Verts[0], "the partly-bad vertex");
        }

        // ---- the invariant over a whole geometry ----------------------------------------------

        [Fact]
        public void EveryVertexOfAMeshFullOfDegenerateFacesEndsUpUsable()
        {
            // A plausible bad drawing group: a strip of quads whose every other triangle has a
            // repeated corner, so half the vertices are only ever touched by zero-area faces.
            var geometry = new FusionGeometry();
            var positions = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 0, 0), new Vector3(1, 0, 0),   // degenerate
                new Vector3(1, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),   // degenerate
                new Vector3(1, 1, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),   // degenerate
                new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 0),   // degenerate
            };
            foreach (var p in positions) geometry.Verts.Add(Vert(p, Vector3.Zero));

            var repaired = DegenerateNormalRepair.Repair(geometry);

            Assert.Equal(positions.Length, repaired);
            for (int i = 0; i < geometry.Verts.Count; i++)
            {
                AssertUsable(geometry.Verts[i], "vertex " + i);
            }
        }

        [Fact]
        public void PositionsAndTextureCoordinatesAreNeverTouched()
        {
            // The repair is about normals. Anything else it changed would be a silent corruption
            // of the mesh, which is a far worse defect than the one being fixed.
            var position = new Vector3(1.5f, -2.25f, 3f);
            var uv = new Vector2(0.25f, 0.75f);
            var geometry = new FusionGeometry();
            geometry.Verts.Add(new DGRP3DVert(position, new Vector3(float.NaN, float.NaN, float.NaN), uv));

            DegenerateNormalRepair.Repair(geometry);

            Assert.Equal(position, geometry.Verts[0].Position);
            Assert.Equal(uv, geometry.Verts[0].TextureCoordinate);
        }

        [Fact]
        public void AGoodNormalIsLeftPointingWhereItPointed()
        {
            // Only the length is corrected. Rotating a good normal towards the fallback would
            // quietly change the lighting of a mesh that was fine.
            var normal = new Vector3(0f, 3f, 0f); // same direction, not unit length
            var geometry = new FusionGeometry();
            geometry.Verts.Add(Vert(Vector3.Zero, normal));

            Assert.Equal(0, DegenerateNormalRepair.Repair(geometry));

            var result = geometry.Verts[0].Normal;
            Assert.Equal(0f, result.X, 6);
            Assert.Equal(1f, result.Y, 6);
            Assert.Equal(0f, result.Z, 6);
        }

        [Fact]
        public void ACleanGeometryIsReportedAsNeedingNoRepair()
        {
            // The count is what the generator prints, so a run that repairs something has to be
            // distinguishable from a run that did not. Zero here is as load-bearing as non-zero
            // elsewhere.
            var geometry = new FusionGeometry();
            geometry.Verts.Add(Vert(Vector3.Zero, Vector3.UnitX));
            geometry.Verts.Add(Vert(Vector3.One, new Vector3(0f, 0f, -2f)));

            Assert.Equal(0, DegenerateNormalRepair.Repair(geometry));
            AssertUsable(geometry.Verts[0], "the first vertex");
            AssertUsable(geometry.Verts[1], "the second vertex");
        }

        [Fact]
        public void AnEmptyGeometryIsNotAnError()
        {
            Assert.Equal(0, DegenerateNormalRepair.Repair(new FusionGeometry()));
            Assert.Equal(0, DegenerateNormalRepair.Repair(null));
        }

        // ---- what a repaired bad normal borrows -----------------------------------------------

        [Fact]
        public void ABadVertexBorrowsFromItsNeighbourRatherThanTheConstant()
        {
            // A borrowed direction is roughly along the surface; the constant is a fixed compass
            // bearing. For a degenerate face either is defensible, but the neighbour is better and
            // this pins the preference so it cannot be quietly reversed. The neighbour is already
            // unit length (3-4-5), so the assertion is about the direction rather than the scale.
            var neighbour = new Vector3(0.6f, 0f, -0.8f);
            var geometry = new FusionGeometry();
            geometry.Verts.Add(Vert(Vector3.Zero, neighbour));
            geometry.Verts.Add(Vert(Vector3.One, new Vector3(float.NaN, float.NaN, float.NaN)));

            DegenerateNormalRepair.Repair(geometry);

            Assert.NotEqual(DegenerateNormalRepair.FallbackNormal, geometry.Verts[1].Normal);
            Assert.Equal(0.6f, geometry.Verts[1].Normal.X, 5);
            Assert.Equal(0f, geometry.Verts[1].Normal.Y, 5);
            Assert.Equal(-0.8f, geometry.Verts[1].Normal.Z, 5);
        }

        [Fact]
        public void ALeadingBadVertexBorrowsFromTheFirstGoodOneAfterIt()
        {
            // The forward scan has nothing to lend the vertices before the first good normal, so
            // these are the ones a single-pass implementation would leave on the constant. The
            // donor has to be the first vertex that had a normal of its *own*, not merely the
            // first usable one: by the time the leading run is revisited the forward pass has made
            // them all usable, and borrowing from them would be borrowing from the constant.
            var geometry = new FusionGeometry();
            geometry.Verts.Add(Vert(Vector3.Zero, new Vector3(float.NaN, 0f, 0f)));
            geometry.Verts.Add(Vert(Vector3.One, new Vector3(0f, float.NaN, 0f)));
            geometry.Verts.Add(Vert(new Vector3(2, 0, 0), Vector3.UnitZ));

            var repaired = DegenerateNormalRepair.Repair(geometry);

            Assert.Equal(2, repaired);
            foreach (var i in new[] { 0, 1 })
            {
                Assert.NotEqual(DegenerateNormalRepair.FallbackNormal, geometry.Verts[i].Normal);
                Assert.Equal(0f, geometry.Verts[i].Normal.X, 5);
                Assert.Equal(0f, geometry.Verts[i].Normal.Y, 5);
                Assert.Equal(1f, geometry.Verts[i].Normal.Z, 5);
            }
        }

        [Fact]
        public void ALeadingBadVertexWithNoGoodNormalAfterItStillGetsTheConstant()
        {
            // The donor pass has nothing to offer when every vertex is broken, and must not read
            // its own forward-pass output back as a donor.
            var geometry = new FusionGeometry();
            geometry.Verts.Add(Vert(Vector3.Zero, Vector3.Zero));
            geometry.Verts.Add(Vert(Vector3.One, Vector3.Zero));
            geometry.Verts.Add(Vert(new Vector3(2, 0, 0), Vector3.Zero));

            Assert.Equal(3, DegenerateNormalRepair.Repair(geometry));

            foreach (var v in geometry.Verts)
            {
                AssertUsable(v, "an all-bad vertex");
                Assert.Equal(DegenerateNormalRepair.FallbackNormal, v.Normal);
            }
        }

        [Fact]
        public void AGeometryWithNoGoodNormalAtAllFallsBackToTheConstant()
        {
            // Every vertex bad, e.g. a geometry made entirely of collapsed quads. There is nothing
            // to borrow, and the answer still has to be a usable vector rather than a NaN.
            var geometry = new FusionGeometry();
            for (int i = 0; i < 4; i++) geometry.Verts.Add(Vert(Vector3.Zero, Vector3.Zero));

            var repaired = DegenerateNormalRepair.Repair(geometry);

            Assert.Equal(4, repaired);
            foreach (var v in geometry.Verts) AssertUsable(v, "a vertex of an all-bad geometry");
            Assert.Equal(DegenerateNormalRepair.FallbackNormal, geometry.Verts[0].Normal);
        }

        [Fact]
        public void ABorrowedNormalIsTakenAsGivenWhenNothingElseIsUsable()
        {
            // The rule on its own, so the fallback's behaviour is pinned independently of the mesh
            // walk that normally supplies the borrow.
            Assert.Equal(Vector3.UnitX, DegenerateNormalRepair.Repair(Vector3.Zero, new Vector3(1, 0, 0)));
            Assert.Equal(Vector3.UnitY, DegenerateNormalRepair.Repair(Vector3.Zero, null));
            Assert.Equal(Vector3.UnitY, DegenerateNormalRepair.Repair(new Vector3(float.NaN, 0, 0), Vector3.Zero));
        }

        [Fact]
        public void TheFallbackIsAUnitVector()
        {
            // The whole guarantee in one assertion: whatever else happens, this is the value a bad
            // normal becomes, so it has to be something a shader can use.
            AssertUsable(new DGRP3DVert(Vector3.Zero, DegenerateNormalRepair.FallbackNormal, Vector2.Zero), "the fallback");
        }

        // ---- across a whole drawing group ------------------------------------------------------

        [Fact]
        public void RepairingAGroupCountsEveryBadVertexAcrossEveryGeometry()
        {
            // The unit the normal pass works in is the geometry, not the group, so the count the
            // generator prints is a sum over geometries - and a per-geometry count that silently
            // reported only the last one would make the total meaningless.
            var groups = new List<List<FusionGeometry>>();

            var first = new List<FusionGeometry> { new FusionGeometry(), new FusionGeometry() };
            first[0].Verts.Add(Vert(Vector3.Zero, Vector3.Zero));
            first[0].Verts.Add(Vert(Vector3.One, Vector3.UnitZ));
            first[1].Verts.Add(Vert(Vector3.Zero, new Vector3(float.NaN, 0, 0)));
            groups.Add(first);

            var second = new List<FusionGeometry> { new FusionGeometry() };
            second[0].Verts.Add(Vert(Vector3.Zero, Vector3.UnitX));
            second[0].Verts.Add(Vert(Vector3.One, Vector3.UnitY));
            second[0].Verts.Add(Vert(new Vector3(2, 0, 0), Vector3.UnitZ));
            groups.Add(second);

            Assert.Equal(2, DegenerateNormalRepair.RepairAll(groups));

            foreach (var group in groups)
            {
                foreach (var geometry in group)
                {
                    foreach (var v in geometry.Verts) AssertUsable(v, "a vertex of the group");
                }
            }
        }

        [Fact]
        public void RepairingNothingIsZeroAndNotAnError()
        {
            Assert.Equal(0, DegenerateNormalRepair.RepairAll(null));
            Assert.Equal(0, DegenerateNormalRepair.RepairAll(new List<List<FusionGeometry>>()));
        }

        // ---- the rule on its own ---------------------------------------------------------------

        [Fact]
        public void ZeroAndNegativeZeroAreBothUnusable()
        {
            // -0f is a real value that a naive `> 0` on one component would wave through, and it
            // normalises to NaN just the same. Written in code rather than as InlineData because
            // an attribute argument cannot carry the sign bit.
            Assert.False(DegenerateNormalRepair.IsUsable(new Vector3(0f, 0f, 0f)));
            Assert.False(DegenerateNormalRepair.IsUsable(new Vector3(0f, 0f, -0f)));

            // Confirms the second case is genuinely negative zero and not the same literal twice.
            // Masked rather than shifted: the sign bit is 0x80000000, and an arithmetic shift
            // brings the 1 down as -1.
            Assert.Equal(1, (BitConverter.SingleToInt32Bits(-0f) >> 31) & 1);
            Assert.Equal(0, (BitConverter.SingleToInt32Bits(0f) >> 31) & 1);
        }

        [Fact]
        public void AUsableNormalIsOneThatIsFiniteAndNonZero()
        {
            Assert.True(DegenerateNormalRepair.IsUsable(Vector3.UnitY));
            Assert.True(DegenerateNormalRepair.IsUsable(new Vector3(0f, 1e-20f, 0f)));
            Assert.False(DegenerateNormalRepair.IsUsable(new Vector3(1f, 0f, float.NaN)));
            Assert.False(DegenerateNormalRepair.IsUsable(new Vector3(float.PositiveInfinity, 0f, 0f)));
            Assert.False(DegenerateNormalRepair.IsUsable(new Vector3(float.NegativeInfinity, 0f, 0f)));
        }

        [Fact]
        public void RepairingLeavesAUsableNormalPointingTheSameWay()
        {
            var repaired = DegenerateNormalRepair.Repair(new Vector3(0f, 0f, -5f), Vector3.UnitX);

            Assert.True(Vector3.Dot(repaired, Vector3.UnitZ) < -0.99f, "the normal was rotated away from where it pointed");
            Assert.Equal(1f, repaired.Length(), 3);
        }
    }
}
