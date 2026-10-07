using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Simitone.MeshCleanup
{
    /// <summary>
    /// The OBJ side of the round trip, following the material convention the game's own FSOm
    /// exporter/importer (Volcanic's FSOM editor) established:
    ///
    /// <list type="bullet">
    /// <item><c>&lt;dyn&gt;_SPR_rot&lt;dir&gt;_&lt;spr&gt;</c> - the drawing group's own sprite at that
    /// world rotation and sprite index;</item>
    /// <item><c>&lt;dyn&gt;_TEX_&lt;id&gt;</c> - a custom texture shipped as
    /// <c>&lt;iff&gt;_TEX_&lt;id&gt;.png</c> next to the pack file.</item>
    /// </list>
    ///
    /// <para>Positions are the runtime's world coordinates (Y up); OBJ <c>vt</c> V is written
    /// bottom-up (<c>1 - v</c>) exactly like <c>DGRP3DGeometry.SaveOBJ</c>, and flipped back on
    /// import, so Blender's own OBJ conventions round-trip without an axis guess.</para>
    /// </summary>
    public static class ObjIo
    {
        static readonly Regex SprName = new Regex(@"^(?<dyn>\d+)_SPR_rot(?<dir>\d+)_(?<spr>\d+)$", RegexOptions.Compiled);
        static readonly Regex TexName = new Regex(@"^(?<dyn>\d+)_TEX_(?<id>\d+)$", RegexOptions.Compiled);

        public static string MaterialName(int dyn, FsomGeometry geom)
            => geom.CustomTexture
                ? dyn + "_TEX_" + geom.PixelSPR
                : dyn + "_SPR_rot" + geom.PixelDir + "_" + geom.PixelSPR;

        // ---------------------------------------------------------------- export

        public static void Export(string fsomPath, GameData game, DGRP dgrp, string outDir)
        {
            var fsom = FsomFile.Read(fsomPath);
            var baseName = Path.GetFileNameWithoutExtension(fsomPath);
            Directory.CreateDirectory(outDir);
            var store = new TextureStore(game, dgrp, fsom, outDir);

            var materials = new List<(string name, SpriteTexture tex, FsomGeometry geom)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var objPath = Path.Combine(outDir, baseName + ".obj");
            using (var obj = new StreamWriter(objPath))
            {
                obj.WriteLine("# Cleanup export of " + Path.GetFileName(fsomPath) + " (" + fsom.Name + ")");
                obj.WriteLine("# Material names follow the Volcanic convention and MUST NOT be renamed:");
                obj.WriteLine("#   <dyn>_SPR_rot<dir>_<spr>  - use the object's own sprite (no PNG needed)");
                obj.WriteLine("#   <dyn>_TEX_<id>            - use <material>.png next to this OBJ");
                obj.WriteLine("# Keep the world axes, origin and units: the game places the mesh by them.");
                obj.WriteLine("# Re-import with: Simitone.MeshCleanup import --obj this.obj --name " + fsom.Name);
                obj.WriteLine("mtllib " + baseName + ".mtl");
                obj.WriteLine("s 1");

                int baseIndex = 1;
                for (int dyn = 0; dyn < fsom.Groups.Count; dyn++)
                {
                    foreach (var geom in fsom.Groups[dyn])
                    {
                        if (geom.Verts.Count == 0 || geom.Indices.Count == 0) continue;
                        var name = MaterialName(dyn, geom);
                        if (!seen.Add(name))
                        {
                            Console.Error.WriteLine("warning: duplicate material " + name + "; OBJ cannot keep both, the runtime importer would keep one");
                            continue;
                        }

                        var tex = store.Get(geom);
                        materials.Add((name, tex, geom));

                        obj.WriteLine("o " + name);
                        obj.WriteLine("usemtl " + name);
                        foreach (var v in geom.Verts)
                            obj.WriteLine("v " + F(v.Position.X) + " " + F(v.Position.Y) + " " + F(v.Position.Z));
                        foreach (var v in geom.Verts)
                            obj.WriteLine("vt " + F(v.TextureCoordinate.X) + " " + F(1 - v.TextureCoordinate.Y));
                        foreach (var v in geom.Verts)
                            obj.WriteLine("vn " + F(v.Normal.X) + " " + F(v.Normal.Y) + " " + F(v.Normal.Z));

                        for (int i = 0; i + 2 < geom.Indices.Count; i += 3)
                        {
                            int a = geom.Indices[i] + baseIndex;
                            int b = geom.Indices[i + 1] + baseIndex;
                            int c = geom.Indices[i + 2] + baseIndex;
                            obj.WriteLine("f " + a + "/" + a + "/" + a + " " + b + "/" + b + "/" + b + " " + c + "/" + c + "/" + c);
                        }
                        baseIndex += geom.Verts.Count;
                    }
                }
            }

            var mtlPath = Path.Combine(outDir, baseName + ".mtl");
            using (var mtl = new StreamWriter(mtlPath))
            {
                mtl.WriteLine("# Textures for " + baseName + ".obj");
                foreach (var (name, tex, _) in materials)
                {
                    mtl.WriteLine("newmtl " + name);
                    mtl.WriteLine("Ka 1.000 1.000 1.000");
                    mtl.WriteLine("Kd 1.000 1.000 1.000");
                    mtl.WriteLine("Ks 0.000 0.000 0.000");
                    mtl.WriteLine("Ns 10.0000");
                    mtl.WriteLine("illum 2");
                    mtl.WriteLine("map_Kd " + name + ".png");
                    mtl.WriteLine("map_d " + name + ".png");
                    var png = Path.Combine(outDir, name + ".png");
                    if (tex != null) tex.SavePng(png);
                }
            }

            WriteManifest(Path.Combine(outDir, baseName + ".manifest.json"), fsom, fsomPath, materials);
            Console.WriteLine("exported " + materials.Count + " material(s), " + fsom.TriangleCount + " tris to " + objPath);
        }

        static void WriteManifest(string path, FsomFile fsom, string fsomPath, List<(string name, SpriteTexture tex, FsomGeometry geom)> materials)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"mesh\": \"").Append(fsom.Name).Append("\",\n");
            sb.Append("  \"source\": \"").Append(Path.GetFileName(fsomPath).Replace("\\", "/")).Append("\",\n");
            sb.Append("  \"sha256\": \"").Append(FsomFile.Sha256(fsomPath)).Append("\",\n");
            sb.Append("  \"animatedFrame\": ").Append(fsom.IsAnimated ? fsom.Frame.ToString(CultureInfo.InvariantCulture) : "null").Append(",\n");
            sb.Append("  \"frames\": ").Append(fsom.FrameCount.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"bounds\": [").Append(F(fsom.Bounds.Min.X)).Append(", ").Append(F(fsom.Bounds.Min.Y)).Append(", ").Append(F(fsom.Bounds.Min.Z))
              .Append(", ").Append(F(fsom.Bounds.Max.X)).Append(", ").Append(F(fsom.Bounds.Max.Y)).Append(", ").Append(F(fsom.Bounds.Max.Z)).Append("],\n");
            sb.Append("  \"materials\": [\n");
            for (int i = 0; i < materials.Count; i++)
            {
                var (name, tex, geom) = materials[i];
                var uv = UvBounds(geom);
                sb.Append("    { \"name\": \"").Append(name).Append("\", \"texture\": \"")
                  .Append(tex == null ? "" : name + ".png").Append("\", \"textureSize\": ")
                  .Append(tex == null ? "null" : "[" + tex.Width + ", " + tex.Height + "]").Append(", \"verts\": ")
                  .Append(geom.Verts.Count).Append(", \"tris\": ").Append(geom.Indices.Count / 3)
                  .Append(", \"uv\": [").Append(F(uv.X)).Append(", ").Append(F(uv.Y)).Append(", ").Append(F(uv.Z)).Append(", ").Append(F(uv.W)).Append("] }")
                  .Append(i == materials.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ]\n}\n");
            File.WriteAllText(path, sb.ToString());
        }

        static Vector4 UvBounds(FsomGeometry geom)
        {
            float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
            foreach (var v in geom.Verts)
            {
                u0 = Math.Min(u0, v.TextureCoordinate.X); u1 = Math.Max(u1, v.TextureCoordinate.X);
                v0 = Math.Min(v0, v.TextureCoordinate.Y); v1 = Math.Max(v1, v.TextureCoordinate.Y);
            }
            return new Vector4(u0, v0, u1, v1);
        }

        static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- import

        public sealed class ImportResult
        {
            public List<List<FsomGeometry>> Groups = new List<List<FsomGeometry>>();
            public BoundingBox Bounds;
            public int CustomTextures;
            public List<string> Warnings = new List<string>();
        }

        public static ImportResult Import(string objPath, string texSourceDir)
        {
            var parsed = ParseObj(objPath);
            var result = new ImportResult();

            var byDyn = new SortedDictionary<int, List<FsomGeometry>>();
            foreach (var kv in parsed.FacesByGroup)
            {
                var name = kv.Key;
                int dyn; FsomGeometry geom;
                var spr = SprName.Match(name);
                var tex = TexName.Match(name);
                if (spr.Success)
                {
                    dyn = int.Parse(spr.Groups["dyn"].Value);
                    geom = new FsomGeometry
                    {
                        PixelDir = ushort.Parse(spr.Groups["dir"].Value),
                        PixelSPR = ushort.Parse(spr.Groups["spr"].Value),
                    };
                }
                else if (tex.Success)
                {
                    dyn = int.Parse(tex.Groups["dyn"].Value);
                    geom = new FsomGeometry
                    {
                        PixelDir = 65535,
                        PixelSPR = ushort.Parse(tex.Groups["id"].Value),
                    };
                    result.CustomTextures++;
                }
                else
                {
                    throw new InvalidDataException("object/material '" + name + "' does not match the required naming: " +
                        "<dyn>_SPR_rot<dir>_<spr> or <dyn>_TEX_<id>");
                }

                BuildGeometry(parsed, kv.Value, geom);
                if (geom.Indices.Count == 0) continue;

                // Winding sanity: the runtime draws with the default (counter-clockwise) cull and
                // keeps the stored normals for lighting. The engine's own generated meshes store
                // normals opposite to their triangle order (GenerateNormals is called with
                // invert=true for unflipped sheets), so 0% and 100% are both self-consistent. Only
                // a mixed ratio means an edit left some faces oriented against the rest.
                var agreement = NormalAgreement(geom);
                if (agreement > 0.2f && agreement < 0.8f)
                    result.Warnings.Add(name + ": face winding and normals disagree on " +
                        (agreement * 100).ToString("0", CultureInfo.InvariantCulture) + "% of faces (mixed orientation)");

                if (!byDyn.TryGetValue(dyn, out var list)) byDyn[dyn] = list = new List<FsomGeometry>();
                list.Add(geom);
            }

            if (byDyn.Count == 0) throw new InvalidDataException("no faces found in " + objPath);

            int maxDyn = byDyn.Keys.Max();
            for (int d = 0; d <= maxDyn; d++)
                result.Groups.Add(byDyn.TryGetValue(d, out var list) ? list : new List<FsomGeometry>());

            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (var geom in result.Groups.SelectMany(g => g))
                foreach (var v in geom.Verts) { lo = Vector3.Min(lo, v.Position); hi = Vector3.Max(hi, v.Position); }
            result.Bounds = new BoundingBox(lo, hi);
            return result;
        }

        static void BuildGeometry(ParsedObj parsed, List<int[]> faces, FsomGeometry geom)
        {
            var map = new Dictionary<(int, int, int), int>();
            bool hasNormals = true;
            foreach (var f in faces)
                if (f[2] <= 0) { hasNormals = false; break; }

            foreach (var f in faces)
            {
                var corners = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    var key = (f[c * 3], f[c * 3 + 1], hasNormals ? f[c * 3 + 2] : 0);
                    if (!map.TryGetValue(key, out var index))
                    {
                        index = geom.Verts.Count;
                        var pos = parsed.Positions[f[c * 3] - 1];
                        var uv = f[c * 3 + 1] > 0 ? parsed.TexCoords[f[c * 3 + 1] - 1] : Vector2.Zero;
                        var nrm = hasNormals ? parsed.Normals[f[c * 3 + 2] - 1] : Vector3.Zero;
                        // OBJ V is bottom-up; the runtime stores sprite-space V (0 at the top).
                        geom.Verts.Add(new DGRP3DVert(pos, nrm, new Vector2(uv.X, 1 - uv.Y)));
                        map[key] = index;
                    }
                    corners[c] = index;
                }
                geom.Indices.Add(corners[0]); geom.Indices.Add(corners[1]); geom.Indices.Add(corners[2]);
            }

            if (!hasNormals) DGRP3DVert.GenerateNormals(false, geom.Verts, geom.Indices);
            else
            {
                for (int i = 0; i < geom.Verts.Count; i++)
                {
                    var v = geom.Verts[i];
                    var len = v.Normal.Length();
                    v.Normal = len > 1e-6f ? v.Normal / len : Vector3.UnitY;
                    geom.Verts[i] = v;
                }
            }
        }

        static float NormalAgreement(FsomGeometry geom)
        {
            int agree = 0, total = 0;
            for (int i = 0; i + 2 < geom.Indices.Count; i += 3)
            {
                var v0 = geom.Verts[geom.Indices[i]];
                var v1 = geom.Verts[geom.Indices[i + 1]];
                var v2 = geom.Verts[geom.Indices[i + 2]];
                var cross = Vector3.Cross(v1.Position - v0.Position, v2.Position - v0.Position);
                if (cross.LengthSquared() < 1e-12f) continue;
                var avg = v0.Normal + v1.Normal + v2.Normal;
                if (avg.LengthSquared() < 1e-12f) continue;
                total++;
                if (Vector3.Dot(Vector3.Normalize(cross), Vector3.Normalize(avg)) > 0) agree++;
            }
            return total == 0 ? 1f : (float)agree / total;
        }

        sealed class ParsedObj
        {
            public List<Vector3> Positions = new List<Vector3>();
            public List<Vector2> TexCoords = new List<Vector2>();
            public List<Vector3> Normals = new List<Vector3>();
            /// <summary>group name -> faces; each face is a flat triple of (v, vt, vn) index triples.</summary>
            public Dictionary<string, List<int[]>> FacesByGroup = new Dictionary<string, List<int[]>>(StringComparer.Ordinal);
        }

        static ParsedObj ParseObj(string path)
        {
            var parsed = new ParsedObj();
            string currentObject = null, currentMaterial = null;
            List<int[]> faces = null;

            string GroupForLine()
            {
                var name = currentObject ?? currentMaterial;
                if (string.IsNullOrEmpty(name)) throw new InvalidDataException("face before any 'o' or 'usemtl' name; cannot know its texture");
                if (!parsed.FacesByGroup.TryGetValue(name, out faces))
                {
                    faces = new List<int[]>();
                    parsed.FacesByGroup[name] = faces;
                }
                return name;
            }

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.TrimStart();
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                if (line.Length == 0) continue;
                var split = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (split.Length == 0) continue;
                switch (split[0])
                {
                    case "v":
                        parsed.Positions.Add(new Vector3(Fl(split[1]), Fl(split[2]), Fl(split[3])));
                        break;
                    case "vt":
                        parsed.TexCoords.Add(new Vector2(Fl(split[1]), split.Length > 2 ? Fl(split[2]) : 0));
                        break;
                    case "vn":
                        parsed.Normals.Add(new Vector3(Fl(split[1]), Fl(split[2]), Fl(split[3])));
                        break;
                    case "o":
                    case "g":
                        currentObject = split.Length > 1 ? split[1] : currentObject;
                        faces = null;
                        break;
                    case "usemtl":
                        currentMaterial = split.Length > 1 ? split[1] : null;
                        faces = null;
                        break;
                    case "f":
                        GroupForLine();
                        if (split.Length < 4) throw new InvalidDataException("face with fewer than 3 vertices: " + raw);
                        var corners = new List<int[]>(split.Length - 1);
                        for (int i = 1; i < split.Length; i++)
                            corners.Add(ParseFaceCorner(split[i], parsed));
                        for (int i = 1; i + 1 < corners.Count; i++)
                        {
                            var f = new int[9];
                            corners[0].CopyTo(f, 0);
                            corners[i].CopyTo(f, 3);
                            corners[i + 1].CopyTo(f, 6);
                            faces.Add(f);
                        }
                        break;
                }
            }
            return parsed;
        }

        static int[] ParseFaceCorner(string token, ParsedObj parsed)
        {
            var parts = token.Split('/');
            int v = ResolveIndex(parts[0], parsed.Positions.Count);
            int vt = parts.Length > 1 && parts[1].Length > 0 ? ResolveIndex(parts[1], parsed.TexCoords.Count) : 0;
            int vn = parts.Length > 2 && parts[2].Length > 0 ? ResolveIndex(parts[2], parsed.Normals.Count) : 0;
            return new[] { v, vt, vn };
        }

        static int ResolveIndex(string text, int count)
        {
            var value = int.Parse(text, CultureInfo.InvariantCulture);
            if (value < 0) return count + 1 + value; // -1 = last
            if (value == 0) throw new InvalidDataException("OBJ indices are 1-based; found 0");
            return value;
        }

        static float Fl(string text) => float.Parse(text, CultureInfo.InvariantCulture);
    }
}
