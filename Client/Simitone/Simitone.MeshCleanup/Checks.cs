using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Simitone.MeshCleanup
{
    /// <summary>
    /// Structural and sanity report for a mesh the cleanup pass is about to ship. "Looks right in a
    /// preview" and "will not break the renderer" are different properties; this checks the second:
    /// unreadable indices, non-finite data, bounds that no longer contain the vertices, floating
    /// debris, non-manifold edges, spikes and winding/normal disagreement.
    ///
    /// <para>The thresholds are deliberately advisory except for the hard invariants (index range,
    /// finiteness, bounds) - a cleaned mesh may be a loose set of sheets, so "closed manifold" is a
    /// goal, not a rule. The output is what the cleanup report cites as evidence.</para>
    /// </summary>
    public static class Checks
    {
        public sealed class GeomReport
        {
            public int Group;
            public int Index;
            public string Material;
            public int Verts;
            public int Tris;
            public int Degenerate;
            public int RepeatedIndex;
            public int BoundaryEdges;
            public int NonManifoldEdges;
            public int Components;
            public int Spikes;
            public float MaxEdge;
            public float MedianEdge;
            public float WindingAgreement;
            public Vector4 Uv;
        }

        public static int Run(string fsomPath, string jsonPath)
        {
            var fsom = FsomFile.Read(fsomPath);
            var errors = new List<string>();
            var warnings = new List<string>();
            var reports = new List<GeomReport>();

            if (fsom.IsAnimated)
                warnings.Add("animated container; only frame " + fsom.Frame + " of " + fsom.FrameCount + " was inspected");

            int groupIndex = 0;
            foreach (var group in fsom.Groups)
            {
                for (int gi = 0; gi < group.Count; gi++)
                {
                    var geom = group[gi];
                    var r = new GeomReport
                    {
                        Group = groupIndex,
                        Index = gi,
                        Material = ObjIo.MaterialName(groupIndex, geom),
                        Verts = geom.Verts.Count,
                        Tris = geom.Indices.Count / 3,
                    };
                    reports.Add(r);

                    foreach (var v in geom.Verts)
                    {
                        if (!IsFinite(v.Position) || !IsFinite(v.TextureCoordinate) || !IsFinite(v.Normal))
                        { errors.Add(r.Material + ": non-finite vertex data"); break; }
                    }

                    if (geom.Indices.Count % 3 != 0) errors.Add(r.Material + ": index count not a multiple of 3");
                    if (geom.Indices.Any(i => i < 0 || i >= geom.Verts.Count)) errors.Add(r.Material + ": index out of vertex range");

                    float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
                    foreach (var v in geom.Verts)
                    {
                        u0 = Math.Min(u0, v.TextureCoordinate.X); u1 = Math.Max(u1, v.TextureCoordinate.X);
                        v0 = Math.Min(v0, v.TextureCoordinate.Y); v1 = Math.Max(v1, v.TextureCoordinate.Y);
                    }
                    r.Uv = new Vector4(u0, v0, u1, v1);
                    if (u0 < -0.02f || u1 > 1.02f || v0 < -0.02f || v1 > 1.02f)
                        warnings.Add(r.Material + ": UV outside [0,1] (" + F(u0) + ".." + F(u1) + ", " + F(v0) + ".." + F(v1) + ")");

                    var edges = new Dictionary<long, int>();
                    var edgeLen = new List<float>();
                    var adjacency = new Dictionary<int, List<int>>();
                    var parent = Enumerable.Range(0, geom.Verts.Count).ToArray();

                    int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                    void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

                    int agree = 0, normTotal = 0;
                    for (int t = 0; t + 2 < geom.Indices.Count; t += 3)
                    {
                        int a = geom.Indices[t], b = geom.Indices[t + 1], c = geom.Indices[t + 2];
                        if (a >= geom.Verts.Count || b >= geom.Verts.Count || c >= geom.Verts.Count) continue;
                        if (a == b || b == c || a == c) { r.RepeatedIndex++; continue; }
                        var pa = geom.Verts[a].Position; var pb = geom.Verts[b].Position; var pc = geom.Verts[c].Position;
                        var cross = Vector3.Cross(pb - pa, pc - pa);
                        if (cross.LengthSquared() < 1e-14f) { r.Degenerate++; continue; }

                        var avg = geom.Verts[a].Normal + geom.Verts[b].Normal + geom.Verts[c].Normal;
                        if (avg.LengthSquared() > 1e-12f) { normTotal++; if (Vector3.Dot(Vector3.Normalize(cross), Vector3.Normalize(avg)) > 0) agree++; }

                        CountEdge(edges, a, b, out var ab);
                        CountEdge(edges, b, c, out var bc);
                        CountEdge(edges, c, a, out var ca);
                        edgeLen.Add(EdgeLen(pa, pb));
                        edgeLen.Add(EdgeLen(pb, pc));
                        edgeLen.Add(EdgeLen(pc, pa));

                        Union(a, b); Union(b, c);
                    }

                    foreach (var kv in edges)
                    {
                        if (kv.Value == 1) r.BoundaryEdges++;
                        else if (kv.Value > 2) r.NonManifoldEdges++;
                    }
                    r.Components = Enumerable.Range(0, geom.Verts.Count).Select(Find).Distinct().Count();
                    r.WindingAgreement = normTotal == 0 ? 1 : (float)agree / normTotal;

                    if (edgeLen.Count > 0)
                    {
                        edgeLen.Sort();
                        r.MedianEdge = edgeLen[edgeLen.Count / 2];
                        r.MaxEdge = edgeLen[edgeLen.Count - 1];
                        float spikeLimit = Math.Max(r.MedianEdge * 6f, 0.25f);
                        r.Spikes = edgeLen.Count(e => e > spikeLimit);
                    }

                    // Winding vs normals is reported, not judged: the engine's own generated meshes
                    // store normals opposite to their triangle order (GenerateNormals is called
                    // with invert=true for unflipped sheets), so 0% and 100% are both "consistent"
                    // in their own way. A mixed ratio is what actually indicates a broken edit.
                    if (r.WindingAgreement > 0.2f && r.WindingAgreement < 0.8f)
                        warnings.Add(r.Material + ": face winding and normals disagree on " + Pct(r.WindingAgreement) + " of faces (mixed orientation)");
                    if (r.NonManifoldEdges > 0)
                        warnings.Add(r.Material + ": " + r.NonManifoldEdges + " non-manifold edge(s)");
                    if (r.Spikes > 0)
                        warnings.Add(r.Material + ": " + r.Spikes + " spike edge(s) longer than " + F(Math.Max(r.MedianEdge * 6f, 0.25f)) + " (max " + F(r.MaxEdge) + ")");
                    if (r.Components > 4)
                        warnings.Add(r.Material + ": " + r.Components + " disconnected components (debris?)");
                }
                groupIndex++;
            }

            // Hard invariant: the stored bounds are what culling uses, so they must contain the mesh.
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            foreach (var v in fsom.AllGeoms.SelectMany(g => g.Verts)) { lo = Vector3.Min(lo, v.Position); hi = Vector3.Max(hi, v.Position); }
            if (lo.X <= hi.X)
            {
                var slack = new Vector3(0.02f);
                if (lo.X < fsom.Bounds.Min.X - slack.X || lo.Y < fsom.Bounds.Min.Y - slack.Y || lo.Z < fsom.Bounds.Min.Z - slack.Z ||
                    hi.X > fsom.Bounds.Max.X + slack.X || hi.Y > fsom.Bounds.Max.Y + slack.Y || hi.Z > fsom.Bounds.Max.Z + slack.Z)
                    errors.Add("stored bounds do not contain the vertices (stored " + F(fsom.Bounds.Min) + ".." + F(fsom.Bounds.Max) +
                               ", actual " + F(lo) + ".." + F(hi) + ")");
            }

            Console.WriteLine(fsom.Name + "  v" + fsom.Version + "/r" + fsom.ReconstructVersion + (fsom.IsAnimated ? "  animated frame " + fsom.Frame + "/" + fsom.FrameCount : ""));
            Console.WriteLine("  groups " + fsom.Groups.Count + "  geoms " + reports.Count + "  verts " + fsom.VertexCount + "  tris " + fsom.TriangleCount);
            Console.WriteLine("  bounds min " + F(fsom.Bounds.Min) + "  max " + F(fsom.Bounds.Max) + "  size " + F(fsom.Bounds.Max - fsom.Bounds.Min));
            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-22} {1,7} {2,7} {3,6} {4,6} {5,6} {6,6} {7,6} {8,7} {9,6}",
                "material", "verts", "tris", "degen", "rep", "bnd", "nonman", "comp", "winding", "spikes"));
            foreach (var r in reports)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-22} {1,7} {2,7} {3,6} {4,6} {5,6} {6,6} {7,6} {8,7:0.00} {9,6}",
                    r.Material, r.Verts, r.Tris, r.Degenerate, r.RepeatedIndex, r.BoundaryEdges, r.NonManifoldEdges, r.Components, r.WindingAgreement, r.Spikes));

            Console.WriteLine();
            foreach (var e in errors) Console.WriteLine("ERROR   " + e);
            foreach (var w in warnings) Console.WriteLine("warning " + w);
            Console.WriteLine(errors.Count == 0 ? "check: OK" + (warnings.Count > 0 ? " (" + warnings.Count + " warning(s))" : "") : "check: FAILED (" + errors.Count + " error(s))");

            if (jsonPath != null) WriteJson(jsonPath, fsomPath, fsom, reports, errors, warnings);
            return errors.Count == 0 ? 0 : 1;
        }

        static void CountEdge(Dictionary<long, int> edges, int a, int b, out int count)
        {
            var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            edges.TryGetValue(key, out count);
            edges[key] = count + 1;
        }

        static float EdgeLen(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

        static bool IsFinite(Vector3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
        static bool IsFinite(Vector2 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y);

        static string F(Vector3 v) => "(" + F(v.X) + "," + F(v.Y) + "," + F(v.Z) + ")";
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string Pct(float v) => (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        static void WriteJson(string path, string fsomPath, FsomFile fsom, List<GeomReport> reports, List<string> errors, List<string> warnings)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"mesh\": \"").Append(fsom.Name).Append("\",\n");
            sb.Append("  \"source\": \"").Append(Path.GetFileName(fsomPath)).Append("\",\n");
            sb.Append("  \"sha256\": \"").Append(FsomFile.Sha256(fsomPath)).Append("\",\n");
            sb.Append("  \"bounds\": [");
            sb.Append(F(fsom.Bounds.Min.X)).Append(", ").Append(F(fsom.Bounds.Min.Y)).Append(", ").Append(F(fsom.Bounds.Min.Z)).Append(", ");
            sb.Append(F(fsom.Bounds.Max.X)).Append(", ").Append(F(fsom.Bounds.Max.Y)).Append(", ").Append(F(fsom.Bounds.Max.Z)).Append("],\n");
            sb.Append("  \"verts\": ").Append(fsom.VertexCount).Append(",\n");
            sb.Append("  \"tris\": ").Append(fsom.TriangleCount).Append(",\n");
            sb.Append("  \"geoms\": [\n");
            for (int i = 0; i < reports.Count; i++)
            {
                var r = reports[i];
                sb.Append("    { \"material\": \"").Append(r.Material).Append("\", \"verts\": ").Append(r.Verts).Append(", \"tris\": ").Append(r.Tris)
                  .Append(", \"degenerate\": ").Append(r.Degenerate).Append(", \"repeated\": ").Append(r.RepeatedIndex)
                  .Append(", \"boundaryEdges\": ").Append(r.BoundaryEdges).Append(", \"nonManifoldEdges\": ").Append(r.NonManifoldEdges)
                  .Append(", \"components\": ").Append(r.Components).Append(", \"spikes\": ").Append(r.Spikes)
                  .Append(", \"maxEdge\": ").Append(F(r.MaxEdge)).Append(", \"medianEdge\": ").Append(F(r.MedianEdge))
                  .Append(", \"windingAgreement\": ").Append(r.WindingAgreement.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" }").Append(i == reports.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"errors\": [");
            for (int i = 0; i < errors.Count; i++) sb.Append(i == 0 ? "\n    \"" : ",\n    \"").Append(Escape(errors[i])).Append("\"");
            sb.Append(errors.Count == 0 ? "],\n" : "\n  ],\n");
            sb.Append("  \"warnings\": [");
            for (int i = 0; i < warnings.Count; i++) sb.Append(i == 0 ? "\n    \"" : ",\n    \"").Append(Escape(warnings[i])).Append("\"");
            sb.Append(warnings.Count == 0 ? "]\n" : "\n  ]\n");
            sb.Append("}\n");
            File.WriteAllText(path, sb.ToString());
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
