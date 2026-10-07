using FSO.Files.RC;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace Simitone.GenerateMeshes
{
    /// <summary>
    /// Repairs the non-finite normals that <see cref="DGRP3DVert.GenerateNormals"/> leaves behind
    /// on degenerate faces, so a generated <c>.fsom</c> is written with a normal for every vertex.
    ///
    /// <para>
    /// <b>Why this exists.</b> <c>DGRP3DVert.GenerateNormals</c>
    /// (<c>FreeSO/TSOClient/tso.files/RC/DGRP3DVert.cs</c>) accumulates the cross product of each
    /// triangle into its three vertices and then normalises every vertex. A vertex touched only by
    /// zero-area triangles accumulates exactly zero, and <c>Vector3.Normalize</c> of a zero
    /// vector computes <c>1f / sqrt(0)</c> = <c>Infinity</c> and then multiplies
    /// <c>0 * Infinity</c>, producing <c>NaN</c> in all three components. The reconstructed lots
    /// in a The Sims 1 drawgroup sheet are full of degenerate triangles - a quad with a repeated
    /// corner, a sprite line collapsed to a point - so roughly a quarter of the generated files
    /// came out with one or two <c>NaN</c> normals among tens of thousands of vertices. See
    /// §4 of <c>docs/MESH_INGESTION_REPORT.md</c> for the measurement (2,043 of 8,648 files,
    /// 4,495 bad vertices) and §7.2 for why they were ingested rather than excluded.
    /// </para>
    ///
    /// <para>
    /// <b>Why here.</b> The obvious fix is a one-line guard in <c>DGRP3DVert.GenerateNormals</c>
    /// or in <c>DGRP3DFusion.FuseGroup</c>, but both are FreeSO-owned and both are also on the
    /// in-game fallback path, so changing either would alter the meshes a drawgroup gets when it
    /// has no replacement file at all - a much wider blast radius than an offline authoring tool.
    /// Repairing on the way to the writer fixes the files this tool produces and nothing else.
    /// The already-generated set on disk is <em>not</em> re-written: that is a full regeneration
    /// run (~2 hours), which is a deliberate, separate decision.
    /// </para>
    ///
    /// <para>
    /// <b>What a degenerate face's normal can be.</b> Nothing, really: a zero-area triangle has
    /// no facing, so its cross product is zero and every direction is as defensible as any other.
    /// What matters downstream is that the value is a <em>finite unit vector</em>, because the
    /// shaders normalise and dot against it and a <c>NaN</c> poisons a whole pixel's lighting
    /// rather than just this triangle. A neighbouring vertex's normal is preferred over a
    /// constant because it points roughly along the surface the degenerate face sits on, which
    /// keeps any large triangle that happens to include this vertex lit like its neighbours
    /// instead of like a fixed axis.
    /// </para>
    ///
    /// <para>
    /// Pure and allocation-light: no IO, no globals, no logging, and it mutates only the
    /// geometry it is handed. The repair rule is a separate method from the mesh walk so it can
    /// be tested without building a drawgroup.
    /// </para>
    /// </summary>
    public static class DegenerateNormalRepair
    {
        /// <summary>
        /// The direction given to a bad normal when its geometry has no good one to borrow.
        /// Straight up, because it is the one direction that is never inside a floor or a wall -
        /// every other candidate would light the surface from a fixed compass bearing.
        /// </summary>
        public static readonly Vector3 FallbackNormal = Vector3.UnitY;

        /// <summary>
        /// Whether <paramref name="normal"/> is something a shader can use: finite, and not
        /// zero-length (which <c>Normalize</c> would turn into <c>NaN</c> just as surely as it
        /// turns an infinite one into a zero).
        /// </summary>
        public static bool IsUsable(Vector3 normal)
        {
            return IsFinite(normal) && normal.LengthSquared() > 0f;
        }

        /// <summary>
        /// Whether every component of <paramref name="value"/> is a real number. Deliberately
        /// written out rather than using <c>float.IsFinite</c> so the same expression reads the
        /// same on every target this tool is built for.
        /// </summary>
        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// The normal to write for a vertex whose own is unusable: <paramref name="borrow"/>, the
        /// previous good normal seen in the same geometry, or <see cref="FallbackNormal"/>.
        /// </summary>
        /// <param name="normal">The normal the normal pass produced. May be anything.</param>
        /// <param name="borrow">A known-good normal from the same geometry, or null.</param>
        /// <remarks>
        /// A good <paramref name="normal"/> is normalised rather than passed through: the normal
        /// pass is supposed to leave every normal unit length, so a finite but off-length value
        /// means something upstream changed, and a shader that trusts the length is being fed a
        /// normal that is not one. Normalising is free here and cannot lose information.
        /// </remarks>
        public static Vector3 Repair(Vector3 normal, Vector3? borrow = null)
        {
            if (IsUsable(normal))
            {
                normal.Normalize();
                return normal;
            }

            if (borrow.HasValue && IsUsable(borrow.Value))
            {
                var borrowed = borrow.Value;
                borrowed.Normalize();
                return borrowed;
            }

            return FallbackNormal;
        }

        /// <summary>
        /// Repairs every vertex of one fused geometry in place, and reports how many needed it.
        /// </summary>
        /// <remarks>
        /// Two passes, because the two ends of the list are the awkward ones. Forward, each bad
        /// vertex borrows the last good normal behind it; but a bad vertex at the <em>head</em> of
        /// the list has nothing behind it yet, so the leading run is then re-pointed at the first
        /// good normal ahead of it. Both give a direction along the surface rather than the
        /// constant, and neither needs a second scan of anything.
        /// </remarks>
        /// <returns>The number of vertices whose normal was replaced.</returns>
        public static int Repair(FusionGeometry geometry)
        {
            if (geometry?.Verts == null) return 0;

            var verts = geometry.Verts;
            var repaired = 0;

            // Which vertices the normal pass could not produce a usable normal for. Needed because
            // by the time the leading run is revisited every vertex is usable, so the state has to
            // be remembered rather than re-derived from the list.
            var wasBroken = new bool[verts.Count];

            Vector3? previous = null;
            for (int i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                if (IsUsable(v.Normal))
                {
                    v.Normal.Normalize();
                    verts[i] = v;
                    previous = v.Normal;
                    continue;
                }

                wasBroken[i] = true;
                v.Normal = Repair(v.Normal, previous);
                verts[i] = v;
                previous = v.Normal;
                repaired++;
            }

            // The leading run only: the first vertex with a normal of its own is the only donor
            // needed, since the run is contiguous.
            int firstIntact = 0;
            while (firstIntact < wasBroken.Length && wasBroken[firstIntact]) firstIntact++;

            if (firstIntact > 0 && firstIntact < wasBroken.Length)
            {
                var donor = verts[firstIntact].Normal;
                for (int i = 0; i < firstIntact; i++)
                {
                    var v = verts[i];
                    v.Normal = donor;
                    verts[i] = v;
                }
            }

            return repaired;
        }

        /// <summary>
        /// Repairs every geometry of one drawing group, in place, and reports how many vertices
        /// needed it. The unit the normal pass works in is the geometry, not the group, so each is
        /// repaired against its own neighbours.
        /// </summary>
        /// <returns>The number of vertices whose normal was replaced, across the whole group.</returns>
        public static int RepairAll(List<List<FusionGeometry>> groups)
        {
            if (groups == null) return 0;

            var repaired = 0;
            foreach (var group in groups)
            {
                if (group == null) continue;
                foreach (var geometry in group) repaired += Repair(geometry);
            }
            return repaired;
        }
    }
}
