using FSO.Files.FAR1;
using FSO.Files.FAR3;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Simitone.MeshCleanup
{
    /// <summary>
    /// The The Sims 1 side of the pipeline: opens one object IFF either from an extracted file or
    /// from inside a FAR archive, and resolves the DGRP/SPR2/OBJD chunks the mesh refers to.
    ///
    /// <para>The FSOM itself stores only sprite selectors (<c>PixelDir</c>/<c>PixelSPR</c>), so
    /// every texture the game would bind is recovered from the install, never from the pack.</para>
    /// </summary>
    public sealed class GameData : IDisposable
    {
        public IffFile Iff;
        public string IffName;
        public string FarPath;

        FAR1Archive archive;

        public static GameData Open(string farOrIff, string iffName)
        {
            if (string.IsNullOrWhiteSpace(farOrIff)) throw new ArgumentException("a Sims install path, .far or .iff is required");
            var path = Path.GetFullPath(farOrIff);
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("game data path not found: " + farOrIff);

            if (path.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
            {
                var result = new GameData { IffName = Path.GetFileName(path) };
                result.Iff = ReadIff(File.ReadAllBytes(path), result.IffName);
                return result;
            }

            var far = path;
            if (Directory.Exists(path))
            {
                // The install root keeps its archives in per-expansion subfolders, so the scan is
                // recursive; the FAR that actually contains the IFF is the only one opened for
                // real (the rest are only indexed).
                var matches = Directory.GetFiles(path, "*.far", SearchOption.AllDirectories).ToList();
                if (matches.Count == 0) throw new FileNotFoundException("no .far archives in " + path);
                if (iffName != null)
                {
                    far = matches.FirstOrDefault(f => ContainsIffName(f, iffName));
                }
                else
                {
                    // No target name given: caller is expected to scan. Not supported by open.
                    throw new ArgumentException("an IFF name is required when the source is a directory of FARs");
                }
                if (far == null) throw new FileNotFoundException("no FAR under " + path + " contains " + iffName);
            }

            var data = new GameData { FarPath = far };
            data.archive = OpenFAR(far);
            var entries = data.archive.GetAllFarEntries();
            var entry = FindEntry(entries, iffName);
            if (entry == null)
            {
                data.Dispose();
                throw new FileNotFoundException("'" + iffName + "' not found in " + Path.GetFileName(far));
            }
            data.IffName = entry.Filename;
            data.Iff = ReadIff(ReadEntry(data.archive, entry), entry.Filename);
            return data;
        }

        static FAR1Archive OpenFAR(string path)
        {
            try { return new FAR1Archive(path, false); }
            catch { return new FAR1Archive(path, true); }
        }

        /// <summary>Finds the FAR that contains an IFF whose runtime stem matches (case-insensitive).</summary>
        public static bool ContainsIffName(string farPath, string iffName)
        {
            try
            {
                using (var far = OpenFAR(farPath))
                {
                    // SetFilename replaces '.' with '_' and lowercases; compare the way the pack
                    // names files so "Gargoyle.iff" can be found from "gargoyle_iff".
                    var wanted = iffName.ToLowerInvariant();
                    var wantedWithExt = wanted.EndsWith(".iff") ? wanted : wanted + ".iff";
                    return far.GetAllFarEntries().Any(e =>
                        e.Filename.ToLowerInvariant() == wantedWithExt ||
                        e.Filename.Replace('.', '_').ToLowerInvariant() == wantedWithExt.Replace('.', '_'));
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Resolves the IFF name for a mesh stem - the reverse of the pack naming rule
        /// (<c>gargoyle_iff_100.fsom</c> came from <c>Gargoyle.iff</c>). Searches every FAR under the
        /// install because a stem is unique in practice and the caller should not have to know
        /// which expansion pack shipped the object.
        /// </summary>
        public static string FindIffName(string installOrFar, string stem)
        {
            var wanted = (stem + ".iff").ToLowerInvariant();
            var fars = new List<string>();
            var path = Path.GetFullPath(installOrFar);
            if (path.EndsWith(".far", StringComparison.OrdinalIgnoreCase)) fars.Add(path);
            else if (File.Exists(path) && path.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(path);
            else if (Directory.Exists(path)) fars.AddRange(Directory.GetFiles(path, "*.far", SearchOption.AllDirectories));

            foreach (var farPath in fars)
            {
                try
                {
                    using (var far = OpenFAR(farPath))
                    {
                        foreach (var entry in far.GetAllFarEntries())
                        {
                            if (!entry.Filename.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) continue;
                            if (entry.Filename.ToLowerInvariant() == wanted || entry.Filename.Replace('.', '_').ToLowerInvariant() == wanted.Replace('.', '_'))
                                return entry.Filename;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        static FarEntry FindEntry(List<FarEntry> entries, string iffName)
        {
            if (iffName == null) return null;
            var wanted = iffName.ToLowerInvariant();
            var wantedWithExt = wanted.EndsWith(".iff") ? wanted : wanted + ".iff";
            return entries.FirstOrDefault(e =>
                e.Filename.ToLowerInvariant() == wantedWithExt ||
                e.Filename.Replace('.', '_').ToLowerInvariant() == wantedWithExt.Replace('.', '_'));
        }

        public static byte[] ReadEntry(FAR1Archive archive, FarEntry entry)
        {
            var data = archive.GetEntry(entry);
            if (data == null) return null;
            if (entry.DataLength == entry.DataLength2) return data;
            try
            {
                var dec = new Decompresser { DecompressedSize = entry.DataLength2 };
                return dec.Decompress(data);
            }
            catch { return data; }
        }

        static IffFile ReadIff(byte[] bytes, string name)
        {
            using (var ms = new MemoryStream(bytes, false))
            {
                var iff = new IffFile();
                iff.Read(ms);
                iff.SetFilename(name);
                return iff;
            }
        }

        public DGRP GetDgrp(int dgrpId)
        {
            var dgrp = Iff.Get<DGRP>((ushort)dgrpId);
            if (dgrp == null)
            {
                var all = Iff.List<DGRP>() ?? new List<DGRP>();
                throw new InvalidDataException("drawing group " + dgrpId + " not in " + IffName +
                    " (ids: " + string.Join(",", all.Select(d => d.ChunkID).Take(40)) + ")");
            }
            return dgrp;
        }

        /// <summary>The OBJD whose graphic range owns a drawgroup, mirroring the generator's rule.</summary>
        public OBJD FindOwner(int dgrpId)
        {
            var objds = Iff.List<OBJD>();
            if (objds == null) return null;
            foreach (var o in objds)
                if (o.NumGraphics > 0 && o.BaseGraphicID > 0 && dgrpId >= o.BaseGraphicID && dgrpId < o.BaseGraphicID + o.NumGraphics)
                    return o;
            return null;
        }

        public SPR2Frame GetFrame(DGRP dgrp, int rotation, int spriteIndex)
        {
            var image = dgrp.GetImage(1, 3, (uint)rotation);
            if (image == null) return null;
            if (spriteIndex < 0 || spriteIndex >= image.Sprites.Length) return null;
            var spirte = image.Sprites[spriteIndex];
            var spr2 = Iff.Get<SPR2>((ushort)spirte.SpriteID);
            if (spr2 == null || spirte.SpriteFrameIndex >= spr2.Frames.Length) return null;
            var frame = spr2.Frames[spirte.SpriteFrameIndex];
            frame.DecodeIfRequired(false);
            return frame;
        }

        public void Dispose()
        {
            archive?.Dispose();
            archive = null;
        }
    }

    public sealed class SpriteTexture
    {
        public int Width;
        public int Height;
        public Rgba32[] Pixels;

        public Rgba32 Sample(float u, float v, bool bilinear = false)
        {
            if (Pixels == null || Width <= 0 || Height <= 0) return new Rgba32(255, 0, 255, 255);
            if (!bilinear) return SampleNearest(u, v);
            // In-game the sprite is bound as a texture and magnified by the GPU, i.e. bilinear.
            // The preview uses this to look like the game rather than like a nearest-neighbour blit.
            // Filtering is done premultiplied: the RGB under a transparent texel is arbitrary (often
            // magenta in the atlas) and would otherwise bleed a coloured fringe onto alpha edges.
            float x = u * Width - 0.5f, y = v * Height - 0.5f;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            int x1 = Math.Clamp(x0 + 1, 0, Width - 1);
            int y1 = Math.Clamp(y0 + 1, 0, Height - 1);
            x0 = Math.Clamp(x0, 0, Width - 1);
            y0 = Math.Clamp(y0, 0, Height - 1);
            var c00 = Pixels[y0 * Width + x0];
            var c10 = Pixels[y0 * Width + x1];
            var c01 = Pixels[y1 * Width + x0];
            var c11 = Pixels[y1 * Width + x1];

            float a = Bilin(c00.A, c10.A, c01.A, c11.A, fx, fy);
            if (a <= 0.05f) return new Rgba32(0, 0, 0, 0);
            float r = Bilin(c00.R * c00.A / 255f, c10.R * c10.A / 255f, c01.R * c01.A / 255f, c11.R * c11.A / 255f, fx, fy);
            float g = Bilin(c00.G * c00.A / 255f, c10.G * c10.A / 255f, c01.G * c01.A / 255f, c11.G * c11.A / 255f, fx, fy);
            float b = Bilin(c00.B * c00.A / 255f, c10.B * c10.A / 255f, c01.B * c01.A / 255f, c11.B * c11.A / 255f, fx, fy);
            return new Rgba32(
                (byte)Math.Clamp(r * 255f / a, 0, 255),
                (byte)Math.Clamp(g * 255f / a, 0, 255),
                (byte)Math.Clamp(b * 255f / a, 0, 255),
                (byte)Math.Clamp(a, 0, 255));
        }

        static float Bilin(float c00, float c10, float c01, float c11, float fx, float fy)
            => (c00 + (c10 - c00) * fx) * (1 - fy) + (c01 + (c11 - c01) * fx) * fy;

        static byte Lerp(byte a, byte b, float t) => (byte)Math.Clamp(a + (b - a) * t, 0, 255);

        Rgba32 SampleNearest(float u, float v)
        {
            int x = Math.Clamp((int)(u * Width), 0, Width - 1);
            int y = Math.Clamp((int)(v * Height), 0, Height - 1);
            return Pixels[y * Width + x];
        }

        public static SpriteTexture FromFrame(SPR2Frame frame)
        {
            if (frame?.PixelData == null) return null;
            var tex = new SpriteTexture { Width = frame.Width, Height = frame.Height, Pixels = new Rgba32[frame.Width * frame.Height] };
            for (int i = 0; i < tex.Pixels.Length && i < frame.PixelData.Length; i++)
            {
                var c = frame.PixelData[i];
                tex.Pixels[i] = new Rgba32(c.R, c.G, c.B, c.A);
            }
            return tex;
        }

        public static SpriteTexture FromPng(string path)
        {
            if (!File.Exists(path)) return null;
            var image = PixelImage.LoadPng(path);
            return new SpriteTexture { Width = image.Width, Height = image.Height, Pixels = image.Pixels };
        }

        public void SavePng(string path)
        {
            var image = new PixelImage(Width, Height);
            Array.Copy(Pixels, image.Pixels, image.Pixels.Length);
            image.SavePng(path);
        }
    }

    /// <summary>
    /// Resolves the texture every geometry in an FSOM would use, from the game data for sprite
    /// slots and from PNG files next to the mesh for custom slots - exactly the order the runtime
    /// provider uses. Used by both the OBJ export and the preview renderer so they cannot disagree
    /// about which image a face shows.
    /// </summary>
    public sealed class TextureStore
    {
        readonly GameData game;
        readonly DGRP dgrp;
        readonly Dictionary<(int, int), SpriteTexture> sprites = new Dictionary<(int, int), SpriteTexture>();
        readonly Dictionary<int, SpriteTexture> custom = new Dictionary<int, SpriteTexture>();

        public TextureStore(GameData game, DGRP dgrp, FsomFile fsom, string texDir)
        {
            this.game = game;
            this.dgrp = dgrp;

            foreach (var geom in fsom.AllGeoms)
            {
                if (geom.CustomTexture)
                {
                    if (custom.ContainsKey(geom.PixelSPR)) continue;
                    var tex = LoadCustom(fsom, geom.PixelSPR, texDir);
                    custom[geom.PixelSPR] = tex;
                }
                else
                {
                    var key = (geom.PixelDir, geom.PixelSPR);
                    if (sprites.ContainsKey(key)) continue;
                    // Sprite slots need the object's DGRP. When the caller only has the pack (a
                    // community remesh preview, say) the geometry still renders, untextured.
                    var frame = game != null && dgrp != null ? game.GetFrame(dgrp, geom.PixelDir, geom.PixelSPR) : null;
                    sprites[key] = SpriteTexture.FromFrame(frame);
                }
            }
        }

        SpriteTexture LoadCustom(FsomFile fsom, int id, string texDir)
        {
            // Two naming conventions exist in the wild: the runtime's own
            // <iff stem>_iff_TEX_<id>.png (used by the community pack, e.g.
            // sculptures2_iff_TEX_469.png) and <mesh name>_TEX_<id>.png (the file name
            // including the drawgroup). Try both, with the lowercase spill of each.
            var names = new List<string>();
            if (Cli.TrySplitMeshName(fsom.Name + ".fsom", out var stem, out var dgrp, out _))
            {
                names.Add(stem + "_iff_TEX_" + id + ".png");
                names.Add(stem + "_iff_" + dgrp + "_TEX_" + id + ".png");
            }
            names.Add(fsom.Name + "_TEX_" + id + ".png");

            var dirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(texDir)) dirs.Add(texDir);
            var dir = Path.GetDirectoryName(Path.GetFullPath(fsom.SourcePath ?? "."));
            if (dir != null && !dirs.Contains(dir)) dirs.Add(dir);

            var candidates = new List<string>();
            foreach (var d in dirs)
                foreach (var n in names)
                {
                    candidates.Add(Path.Combine(d, n));
                    candidates.Add(Path.Combine(d, n.ToLowerInvariant()));
                }
            foreach (var candidate in candidates) { var t = SpriteTexture.FromPng(candidate); if (t != null) return t; }
            Console.Error.WriteLine("warning: custom texture " + id + " for " + fsom.Name + " not found (looked for " + names[0] + " next to the mesh)");
            return null;
        }

        public SpriteTexture Get(FsomGeometry geom)
            => geom.CustomTexture
                ? custom.TryGetValue(geom.PixelSPR, out var t) ? t : null
                : sprites.TryGetValue((geom.PixelDir, geom.PixelSPR), out var s) ? s : null;
    }
}
