using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Simitone.MeshCleanup
{
    /// <summary>
    /// A small deterministic software renderer for the preview loop. It exists so an agent can see
    /// what a mesh looks like from the game's own four isometric views without launching the game:
    /// the camera comes from <see cref="DGRP3DFusion.Inverse"/>, the exact projection the runtime
    /// uses, so a preview and the in-game draw agree about where a vertex lands.
    ///
    /// <para>Lighting is a fixed headlight so a change in shape is what changes the image; faces are
    /// shaded two-sided because a cleaned mesh may legitimately contain a one-sided sheet (the
    /// original generator's output is exactly that).</para>
    /// </summary>
    public static class Renderer
    {
        static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.35f, 0.85f, -0.45f));

        /// <summary>World -> camera for one world rotation: X/Y are the engine screen pixels, Z is depth along the view.</summary>
        static Matrix CameraFor(int rotation)
        {
            // Inverse(r, u, v, t) is linear in (u, v, t); its basis images are the columns of the
            // mapping (u,v,t) -> world. Inverting gives world -> (engine x, engine y, depth).
            var c0 = DGRP3DFusion.Inverse(rotation, 1, 0, 0);
            var c1 = DGRP3DFusion.Inverse(rotation, 0, 1, 0);
            var c2 = DGRP3DFusion.Inverse(rotation, 0, 0, 1);
            var m = new Matrix(
                c0.X, c0.Y, c0.Z, 0,
                c1.X, c1.Y, c1.Z, 0,
                c2.X, c2.Y, c2.Z, 0,
                0, 0, 0, 1);
            return Matrix.Invert(m);
        }

        public static void RenderMesh(string fsomPath, GameData game, DGRP dgrp, string outPath, int panelSize, string texDir, bool largerIsNear, float? azDeg = null, float? elDeg = null, bool bilinear = false)
        {
            var fsom = FsomFile.Read(fsomPath);
            var store = new TextureStore(game, dgrp, fsom, texDir);

            if (azDeg.HasValue)
            {
                // Off-axis preview for judging the 3D form: the game only shows the four
                // canonical rotations, but a replacement that only exists from those four is a
                // billboard, and this is the view that shows it.
                var single = new Image<Rgba32>(panelSize, panelSize, new Rgba32(18, 18, 22));
                RenderPanel(single, 0, panelSize, fsom, store, CustomCamera(azDeg.Value, elDeg ?? 30f), true, bilinear);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
                single.SaveAsPng(outPath);
                Console.WriteLine("rendered " + fsom.TriangleCount + " tris to " + outPath + " (az " + azDeg.Value + ", el " + (elDeg ?? 30f) + ")");
                return;
            }

            var canvas = new Image<Rgba32>(panelSize * 4, panelSize, new Rgba32(18, 18, 22));
            for (int rotation = 0; rotation < 4; rotation++)
                RenderPanel(canvas, rotation * panelSize, panelSize, fsom, store, CameraFor(rotation), largerIsNear, bilinear);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            canvas.SaveAsPng(outPath);
            Console.WriteLine("rendered " + fsom.TriangleCount + " tris to " + outPath + " (panels: rotations 0,1,2,3)");
        }

        /// <summary>Orthographic camera at an arbitrary azimuth/elevation, for off-axis previews.</summary>
        static Matrix CustomCamera(float azDeg, float elDeg)
        {
            float az = MathHelper.ToRadians(azDeg), el = MathHelper.ToRadians(elDeg);
            var dir = new Vector3((float)(Math.Cos(el) * Math.Cos(az)), (float)(Math.Sin(el)), (float)(Math.Cos(el) * Math.Sin(az)));
            var up0 = new Vector3(0, 1, 0);
            var right = Vector3.Normalize(Vector3.Cross(up0, dir));
            var up = Vector3.Cross(dir, right);
            // Rows are the output axes: x = right, y = down (screen), z = depth (toward the camera).
            return new Matrix(
                right.X, right.Y, right.Z, 0,
                -up.X, -up.Y, -up.Z, 0,
                dir.X, dir.Y, dir.Z, 0,
                0, 0, 0, 1);
        }

        static void RenderPanel(Image<Rgba32> canvas, int xoff, int size, FsomFile fsom, TextureStore store, Matrix toCamera, bool largerIsNear, bool bilinear = false)
        {
            var verts = new List<(Vector3 cam, DGRP3DVert v)>();
            var all = fsom.AllGeoms.ToList();

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var geom in all)
                foreach (var v in geom.Verts)
                {
                    var c = Vector3.Transform(v.Position, toCamera);
                    verts.Add((c, v));
                    minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X);
                    minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
                }

            if (all.Count == 0 || verts.Count == 0) return;
            float margin = 14f;
            float spanX = Math.Max(1e-3f, maxX - minX), spanY = Math.Max(1e-3f, maxY - minY);
            float scale = Math.Min((size - margin * 2) / spanX, (size - margin * 2) / spanY);
            float ox = (size - spanX * scale) * 0.5f - minX * scale;
            float oy = (size - spanY * scale) * 0.5f - minY * scale;

            var z = new float[size * size];
            var px = new Rgba32[size * size];
            for (int i = 0; i < z.Length; i++) { z[i] = largerIsNear ? float.MinValue : float.MaxValue; px[i] = new Rgba32(18, 18, 22); }

            int baseVert = 0;
            foreach (var geom in all)
            {
                var tex = store.Get(geom);
                for (int t = 0; t + 2 < geom.Indices.Count; t += 3)
                {
                    var i0 = baseVert + geom.Indices[t];
                    var i1 = baseVert + geom.Indices[t + 1];
                    var i2 = baseVert + geom.Indices[t + 2];
                    var c0 = verts[i0]; var c1 = verts[i1]; var c2 = verts[i2];
                    Raster(z, px, size, ox, oy, scale,
                        c0.cam, c1.cam, c2.cam,
                        c0.v, c1.v, c2.v,
                        tex, largerIsNear, bilinear);
                }
                baseVert += geom.Verts.Count;
            }

            canvas.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < size; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < size; x++) row[xoff + x] = px[y * size + x];
                }
            });
        }

        static void Raster(float[] zbuf, Rgba32[] px, int size, float ox, float oy, float scale,
            Vector3 a, Vector3 b, Vector3 c, DGRP3DVert va, DGRP3DVert vb, DGRP3DVert vc, SpriteTexture tex, bool largerIsNear, bool bilinear = false)
        {
            float ax = ox + a.X * scale, ay = oy + a.Y * scale;
            float bx = ox + b.X * scale, by = oy + b.Y * scale;
            float cx = ox + c.X * scale, cy = oy + c.Y * scale;

            float minx = MathF.Min(ax, MathF.Min(bx, cx)), maxx = MathF.Max(ax, MathF.Max(bx, cx));
            float miny = MathF.Min(ay, MathF.Min(by, cy)), maxy = MathF.Max(ay, MathF.Max(by, cy));
            int x0 = Math.Max(0, (int)MathF.Floor(minx)), x1 = Math.Min(size - 1, (int)MathF.Ceiling(maxx));
            int y0 = Math.Max(0, (int)MathF.Floor(miny)), y1 = Math.Min(size - 1, (int)MathF.Ceiling(maxy));
            if (x0 > x1 || y0 > y1) return;

            float area = Edge(ax, ay, bx, by, cx, cy);
            if (MathF.Abs(area) < 1e-6f) return;

            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float w0 = Edge(bx, by, cx, cy, x + 0.5f, y + 0.5f) / area;
                    float w1 = Edge(cx, cy, ax, ay, x + 0.5f, y + 0.5f) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;

                    float depth = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                    int idx = y * size + x;
                    if (largerIsNear ? depth <= zbuf[idx] : depth >= zbuf[idx]) continue;

                    Rgba32 col;
                    if (tex != null)
                    {
                        float u = w0 * va.TextureCoordinate.X + w1 * vb.TextureCoordinate.X + w2 * vc.TextureCoordinate.X;
                        float v = w0 * va.TextureCoordinate.Y + w1 * vb.TextureCoordinate.Y + w2 * vc.TextureCoordinate.Y;
                        var sample = tex.Sample(u, v, bilinear);
                        if (sample.A < 8) continue;
                        var normal = w0 * va.Normal + w1 * vb.Normal + w2 * vc.Normal;
                        float shade = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(SafeNormalize(normal), Light));
                        col = new Rgba32(
                            (byte)Math.Clamp(sample.R * shade, 0, 255),
                            (byte)Math.Clamp(sample.G * shade, 0, 255),
                            (byte)Math.Clamp(sample.B * shade, 0, 255));
                    }
                    else
                    {
                        col = new Rgba32(150, 80, 160);
                    }

                    zbuf[idx] = depth;
                    px[idx] = col;
                }
        }

        static Vector3 SafeNormalize(Vector3 v)
        {
            var len = v.Length();
            return len > 1e-6f ? v / len : Vector3.UnitY;
        }

        static float Edge(float ax, float ay, float bx, float by, float px, float py)
            => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

        // ---------------------------------------------------------------- sprite reference

        /// <summary>
        /// Dumps the object's own sprites as the ground truth to compare a cleaned mesh against:
        /// one PNG per sprite, one composite with a panel per world rotation, and (optionally) the
        /// depth map the original displacement was built from.
        /// </summary>
        public static void RenderSprites(GameData game, DGRP dgrp, string outDir, bool depth)
        {
            Directory.CreateDirectory(outDir);
            var panels = new List<Image<Rgba32>>();
            int panelW = 1, panelH = 1;

            for (int rotation = 0; rotation < 4; rotation++)
            {
                var image = dgrp.GetImage(1, 3, (uint)rotation);
                if (image == null) continue;
                for (int si = 0; si < image.Sprites.Length; si++)
                {
                    var sprite = image.Sprites[si];
                    var frame = game.GetFrame(dgrp, rotation, si);
                    if (frame?.PixelData == null) continue;
                    var tex = SpriteTexture.FromFrame(frame);
                    tex.SavePng(Path.Combine(outDir, $"rot{rotation}_spr{si}_{frame.Width}x{frame.Height}.png"));
                    panels.Add(ToImage(tex, new Rgba32(18, 18, 22)));
                    panelW = Math.Max(panelW, tex.Width);
                    panelH = Math.Max(panelH, tex.Height);

                    if (depth)
                    {
                        var z = sprite.GetDepth();
                        if (z != null)
                        {
                            var map = new Image<Rgba32>(frame.Width, frame.Height);
                            map.ProcessPixelRows(acc =>
                            {
                                for (int y = 0; y < frame.Height; y++)
                                {
                                    var row = acc.GetRowSpan(y);
                                    for (int x = 0; x < frame.Width; x++)
                                    {
                                        var d = z[y * frame.Width + x];
                                        // 0 = near, 255 = empty; near is bright.
                                        byte g = (byte)(255 - d);
                                        var src = frame.PixelData[y * frame.Width + x];
                                        row[x] = src.A < 8 ? new Rgba32(18, 18, 22) : new Rgba32(g, g, g, 255);
                                    }
                                }
                            });
                            map.SaveAsPng(Path.Combine(outDir, $"rot{rotation}_spr{si}_depth.png"));
                        }
                    }
                }
            }

            if (panels.Count == 0) { Console.Error.WriteLine("no sprite pixels found for this drawing group"); return; }
            var composite = new Image<Rgba32>((panelW + 12) * 4 + 12, panelH + 24, new Rgba32(18, 18, 22));
            for (int i = 0; i < panels.Count && i < 4; i++)
                composite.MutateImage(panels[i], 12 + i * (panelW + 12), 12 + (panelH - panels[i].Height) / 2);
            composite.SaveAsPng(Path.Combine(outDir, "sprites.png"));
            Console.WriteLine("wrote " + panels.Count + " sprite(s) and sprites.png to " + outDir);
        }

        static Image<Rgba32> ToImage(SpriteTexture tex, Rgba32 background)
        {
            var image = new Image<Rgba32>(tex.Width, tex.Height);
            image.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < tex.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < tex.Width; x++)
                    {
                        var c = tex.Pixels[y * tex.Width + x];
                        row[x] = c.A < 8 ? background : c;
                    }
                }
            });
            return image;
        }
    }

    static class ImageExtensions
    {
        public static void MutateImage(this Image<Rgba32> canvas, Image<Rgba32> source, int x, int y)
        {
            canvas.ProcessPixelRows(source, (dest, src) =>
            {
                for (int sy = 0; sy < src.Height; sy++)
                {
                    var destRow = dest.GetRowSpan(y + sy);
                    var srcRow = src.GetRowSpan(sy);
                    for (int sx = 0; sx < src.Width; sx++) destRow[x + sx] = srcRow[sx];
                }
            });
        }
    }
}
