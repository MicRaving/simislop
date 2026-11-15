using FSO.Files.FAR1;
using FSO.Files.FAR3;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.RC;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Simitone.GenerateMeshes
{
    /// <summary>
    /// Offline mesh generator for Simitone's 3D mode.
    ///
    /// <para>Scans a The Sims 1 install for object drawing groups that have no hand-authored
    /// remesh, rebuilds each one with <see cref="DGRP3DLegacy"/> (the original per-pixel
    /// displacement sheets, with parked parts dropped and the depth smoothed), and writes the
    /// results as flat
    /// <c>&lt;iff&gt;_iff_&lt;drawgroup&gt;.fsom</c> files - the same names and folder shape the
    /// runtime mesh loader (<c>FSO.Content.RCMeshProvider</c>) already looks for.</para>
    ///
    /// <para>Usage:
    /// <c>Simitone.GenerateMeshes &lt;ts1-path&gt; &lt;output-dir&gt; [--pack &lt;remesh-root&gt;]
    /// [--filter &lt;substring&gt;] [--max N] [--verify]</c>.</para>
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var opts = Options.Parse(args);
            if (opts == null) return 1;

            try
            {
                return Run(opts);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("mesh generation failed: " + e);
                return 1;
            }
        }

        sealed class Options
        {
            public string Ts1Path;
            public string OutputDir;
            public string PackRoot;
            public string Filter;
            /// <summary>Text file of mesh file names (one per line) that are hand-cleaned and must never be generated.</summary>
            public string SkipList;
            public int Max = int.MaxValue;
            public bool Verify;
            public bool Verbose;
            public bool Quiet;
            public string FarFilter;
            /// <summary>Do not merge animation frames; write one file per drawgroup.</summary>
            public bool NoMerge;
            /// <summary>Only merge existing per-frame files (no reconstruction).</summary>
            public bool MergeOnly;
            /// <summary>With --merge-only, report what would be merged without writing or deleting.</summary>
            public bool DryRun;

            public static Options Parse(string[] args)
            {
                if (args == null || args.Length < 2)
                {
                    Usage();
                    return null;
                }

                var o = new Options { Ts1Path = args[0], OutputDir = args[1] };
                for (int i = 2; i < args.Length; i++)
                {
                    var a = args[i];
                    switch (a)
                    {
                        case "--pack": if (++i >= args.Length) return Bad(a); o.PackRoot = args[i]; break;
                        case "--skip-list": if (++i >= args.Length) return Bad(a); o.SkipList = args[i]; break;
                        case "--filter": if (++i >= args.Length) return Bad(a); o.Filter = args[i]; break;
                        case "--max": if (++i >= args.Length || !int.TryParse(args[i], out o.Max)) return Bad(a); break;
                        case "--verify": o.Verify = true; break;
                        case "--verbose": o.Verbose = true; break;
                        case "--quiet": o.Quiet = true; break;
                        case "--far-filter": if (++i >= args.Length) return Bad(a); o.FarFilter = args[i]; break;
                        case "--no-merge": o.NoMerge = true; break;
                        case "--merge-only": o.MergeOnly = true; break;
                        case "--dry-run": o.DryRun = true; break;
                        default: return Bad(a);
                    }
                }
                return o;
            }

            static Options Bad(string a)
            {
                Console.Error.WriteLine("unknown or malformed option: " + a);
                Usage();
                return null;
            }

            static void Usage()
            {
                Console.Error.WriteLine(
                    "usage: Simitone.GenerateMeshes <ts1-path> <output-dir> [--pack <remesh-root>] " +
                    "[--skip-list <file>] " +
                    "[--filter <substring>] [--far-filter <substring>] " +
                    "[--max N] [--verbose] [--quiet] [--no-merge] [--merge-only] [--dry-run] [--verify]");
            }
        }

        static int Run(Options o)
        {
            if (!Directory.Exists(o.Ts1Path))
            {
                Console.Error.WriteLine("The Sims install not found: " + o.Ts1Path);
                return 1;
            }

            DGRP3DLegacy.Verbose = o.Verbose;

            // Fill the fold holes. A depth discontinuity used to have its connecting quad culled
            // (DGRP3DLegacy.MaxAllowedSq), so a table edge or a wall-floor junction came out as a
            // hole in the reconstructed mesh - and an object with only two sprite rotations, whose
            // "net" is two sheets rather than four, showed the gaps most. docs/MESH_GENERATION_
            // RESEARCH.md section 13 (option A) is the assessment: connecting the depths closes
            // them cleanly, the front view is unchanged, and the depth smoothing already in place
            // keeps the new walls short. This is a generator setting, so it takes effect on the
            // next regeneration of the pack, not on meshes already on disk.
            DGRP3DLegacy.FillFolds = true;

            // Drawing groups that already have a hand-authored mesh are never generated.
            var haveMesh = new HashSet<string>(StringComparer.Ordinal);
            RemeshAliasSet aliases = new RemeshAliasSet(null);
            if (!string.IsNullOrWhiteSpace(o.PackRoot) && Directory.Exists(o.PackRoot))
            {
                var rootMetadata = Path.Combine(o.PackRoot, RemeshPackage.MetadataFileName);
                if (File.Exists(rootMetadata)) aliases = RemeshPackage.ParseAliases(File.ReadAllText(rootMetadata));
                foreach (var entry in RemeshPackage.BuildPlan(o.PackRoot)) haveMesh.Add(entry.DestinationName);
                Console.WriteLine($"remesh pack: {haveMesh.Count} destination files (skipped)");
            }

            // Hand-cleaned meshes are protected exactly like community ones: their names are listed
            // in a manifest (updated by the cleanup procedure when a cleaned mesh ships) so a
            // regeneration never produces a competing file for them. See docs/MESH_MANUAL_CLEANUP.md.
            if (!string.IsNullOrWhiteSpace(o.SkipList) && File.Exists(o.SkipList))
            {
                int listed = 0;
                foreach (var line in File.ReadAllLines(o.SkipList))
                {
                    // One file name per line; '#' starts a comment (whole-line or trailing).
                    var name = line.Split('#')[0].Trim();
                    if (name.Length == 0) continue;
                    if (haveMesh.Add(name)) listed++;
                }
                Console.WriteLine($"cleaned-mesh list: {listed} name(s) protected ({Path.GetFileName(o.SkipList)})");
            }

            Directory.CreateDirectory(o.OutputDir);

            if (o.MergeOnly) return MergeExisting(o, haveMesh, aliases);

            var fars = DiscoverFars(o.Ts1Path);
            if (o.FarFilter != null)
                fars = fars.Where(f => Path.GetFileName(f).IndexOf(o.FarFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Console.WriteLine($"scanning {fars.Count} FAR archive(s) under {o.Ts1Path}");

            int dgrps = 0, generated = 0, skippedMesh = 0, skippedEmpty = 0, failed = 0, reused = 0;
            // Every vertex whose normal the fusion pass could not produce. Zero on a clean run:
            // see DegenerateNormalRepair.
            int degenerateNormals = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            foreach (var farPath in fars)
            {
                if (generated >= o.Max) break;
                List<FarEntry> entryList;
                FAR1Archive archive;
                try
                {
                    archive = new FAR1Archive(farPath, false);
                    entryList = archive.GetAllFarEntries();
                }
                catch
                {
                    try
                    {
                        archive = new FAR1Archive(farPath, true);
                        entryList = archive.GetAllFarEntries();
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("  ! could not open " + Path.GetFileName(farPath) + ": " + e.Message);
                        continue;
                    }
                }

                var iffCount = entryList.Count(x => x.Filename.EndsWith(".iff", StringComparison.OrdinalIgnoreCase));
                if (!o.Quiet) Console.WriteLine($"-- {Path.GetFileName(farPath)} ({iffCount} object file(s))");

                foreach (var entry in entryList)
                {
                    if (generated >= o.Max) break;
                    if (!entry.Filename.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) continue;
                    if (o.Filter != null && entry.Filename.IndexOf(o.Filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    byte[] bytes;
                    try
                    {
                        bytes = ReadEntry(archive, entry);
                        if (bytes == null) continue;
                    }
                    catch { continue; }

                    IffFile iff;
                    List<DGRP> dgrpList;
                    try
                    {
                        using var ms = new MemoryStream(bytes, false);
                        iff = new IffFile();
                        iff.Read(ms);
                        iff.SetFilename(entry.Filename);
                        dgrpList = iff.List<DGRP>();
                    }
                    catch { continue; }
                    if (dgrpList == null || dgrpList.Count == 0) continue;

                    var objds = iff.List<OBJD>();
                    var obj = objds?.FirstOrDefault(o2 => o2.NumGraphics > 0) ?? objds?.FirstOrDefault();
                    var stem = Path.GetFileName(entry.Filename).Replace('.', '_').ToLowerInvariant();

                    // Objects animate by stepping the VM Graphic variable through consecutive
                    // drawgroups (BaseGraphicID .. +NumGraphics). Gather that mapping so the whole
                    // animation can be merged into one file instead of one file per state.
                    var frameOwner = new Dictionary<int, (OBJD obj, int frame)>();
                    if (objds != null)
                    {
                        foreach (var o2 in objds)
                        {
                            if (o2.NumGraphics <= 1 || o2.BaseGraphicID == 0) continue;
                            for (int fi = 0; fi < o2.NumGraphics; fi++)
                                frameOwner[o2.BaseGraphicID + fi] = (o2, fi);
                        }
                    }
                    var mergeDecided = new HashSet<int>();
                    var mergedBases = new HashSet<int>();

                    foreach (var dgrp in dgrpList)
                    {
                        if (generated >= o.Max) break;
                        dgrps++;

                        // One merged animated file per object, emitted while handling its first frame.
                        if (!o.NoMerge && frameOwner.TryGetValue(dgrp.ChunkID, out var owner))
                        {
                            var baseId = owner.obj.BaseGraphicID;
                            if (mergedBases.Contains(baseId)) continue;
                            if (!mergeDecided.Contains(baseId))
                            {
                                mergeDecided.Add(baseId);
                                if (TryMergeAnimated(iff, stem, owner.obj, aliases, haveMesh, o,
                                        ref generated, ref reused, ref skippedEmpty, ref failed, ref degenerateNormals))
                                {
                                    mergedBases.Add(baseId);
                                    continue;
                                }
                            }
                        }

                        var outName = stem + "_" + dgrp.ChunkID + ".fsom";
                        if (haveMesh.Contains(outName) || haveMesh.Contains(RemeshPackage.MapFileName(outName, aliases)))
                        {
                            skippedMesh++;
                            continue;
                        }

                        var outPath = Path.Combine(o.OutputDir, outName);
                        if (File.Exists(outPath)) { reused++; continue; }

                        var fileWatch = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            var groups = DGRP3DLegacy.Build(dgrp, FindOwner(objds, (int)dgrp.ChunkID) ?? obj, out var bounds);
                            // The fusion pass normalises each vertex, which turns the zero
                            // accumulated normal of a degenerate face into (NaN, NaN, NaN).
                            // Repair here, on the way to the writer, so the next run is clean.
                            degenerateNormals += DegenerateNormalRepair.RepairAll(groups);
                            var geoms = Convert(groups);
                            int tris = geoms.Sum(g => g.Sum(x => x.SIndices.Count / 3));
                            if (tris == 0)
                            {
                                skippedEmpty++;
                                continue;
                            }

                            WriteAtomic(outPath, fs => DGRP3DMesh.WriteFSOM(fs, stem + "_" + dgrp.ChunkID, geoms, bounds));

                            generated++;
                            if (!o.Quiet)
                                Console.WriteLine($"[{generated,5}] {outName}  {tris} tris  {fileWatch.ElapsedMilliseconds} ms");
                        }
                        catch (Exception e)
                        {
                            failed++;
                            Console.Error.WriteLine($"  ! {outName}: {e.GetType().Name} {e.Message}");
                            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                        }
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"drawing groups seen:     {dgrps}");
            Console.WriteLine($"generated:               {generated}");
            Console.WriteLine($"already present (kept):  {reused}");
            Console.WriteLine($"skipped (human mesh):    {skippedMesh}");
            Console.WriteLine($"skipped (no geometry):   {skippedEmpty}");
            Console.WriteLine($"failed:                  {failed}");
            Console.WriteLine($"degenerate normals fixed:{degenerateNormals,6}");
            Console.WriteLine($"elapsed:                 {stopwatch.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"written to {Path.GetFullPath(o.OutputDir)}");

            if (o.Verify)
            {
                return Verify(o.OutputDir);
            }
            return 0;
        }

        /// <summary>
        /// Re-packs an existing folder of per-frame meshes into animated containers without
        /// reconstructing anything. This upgrades a pack generated before merging existed (and is
        /// idempotent: an object that is already a container is left alone). Only multi-graphic
        /// objects are touched; single-state meshes stay as they are.
        /// </summary>
        static int MergeExisting(Options o, HashSet<string> haveMesh, RemeshAliasSet aliases)
        {
            var fars = DiscoverFars(o.Ts1Path);
            if (o.FarFilter != null)
                fars = fars.Where(f => Path.GetFileName(f).IndexOf(o.FarFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Console.WriteLine($"merge-only: scanning {fars.Count} FAR archive(s) for animated objects in {o.OutputDir}");

            int objects = 0, merged = 0, reused = 0, skipped = 0, removed = 0, failed = 0, converted = 0;

            foreach (var farPath in fars)
            {
                if (merged >= o.Max) break;
                List<FarEntry> entryList;
                FAR1Archive archive;
                try
                {
                    archive = new FAR1Archive(farPath, false);
                    entryList = archive.GetAllFarEntries();
                }
                catch
                {
                    try { archive = new FAR1Archive(farPath, true); entryList = archive.GetAllFarEntries(); }
                    catch { continue; }
                }

                foreach (var entry in entryList)
                {
                    if (merged >= o.Max) break;
                    if (!entry.Filename.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) continue;
                    if (o.Filter != null && entry.Filename.IndexOf(o.Filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    IffFile iff;
                    List<DGRP> dgrps;
                    try
                    {
                        var bytes = ReadEntry(archive, entry);
                        if (bytes == null) continue;
                        using var ms = new MemoryStream(bytes, false);
                        iff = new IffFile();
                        iff.Read(ms);
                        iff.SetFilename(entry.Filename);
                        dgrps = iff.List<DGRP>() ?? new List<DGRP>();
                    }
                    catch { continue; }

                    var objds = iff.List<OBJD>();
                    if (objds == null || dgrps.Count == 0) continue;
                    var stem = Path.GetFileName(entry.Filename).Replace('.', '_').ToLowerInvariant();

                    foreach (var obj in objds)
                    {
                        if (obj.NumGraphics <= 1 || obj.BaseGraphicID == 0) continue;
                        objects++;
                        var baseId = obj.BaseGraphicID;
                        var num = obj.NumGraphics;

                        // never shadow a hand-authored per-frame mesh
                        bool covered = false;
                        for (int i = 0; i < num && !covered; i++)
                        {
                            var n = stem + "_" + (baseId + i) + ".fsom";
                            if (haveMesh.Contains(n) || haveMesh.Contains(RemeshPackage.MapFileName(n, aliases))) covered = true;
                        }
                        if (covered) { skipped++; continue; }

                        var animPath = Path.Combine(o.OutputDir, stem + "_" + baseId + ".fsom");
                        if (IsAnimatedFile(animPath))
                        {
                            // Upgrade an older container to the current (group-shared) format.
                            try
                            {
                                DGRP3DAnim existing;
                                using (var s = File.OpenRead(animPath)) existing = DGRP3DAnim.Read(s);
                                if (existing.Version >= DGRP3DAnim.VERSION) { reused++; continue; }

                                if (!o.DryRun) WriteAtomic(animPath, fs => existing.Write(fs));
                                converted++;
                                if (!o.Quiet)
                                    Console.WriteLine($"convert {stem}_{baseId}.fsom  v{existing.Version}->v{DGRP3DAnim.VERSION}  {existing.FrameCount} frames, {existing.BlockCount} block(s)");
                            }
                            catch (Exception e)
                            {
                                failed++;
                                Console.Error.WriteLine($"  ! {stem}_{baseId}.fsom (convert): {e.GetType().Name} {e.Message}");
                            }
                            continue;
                        }

                        var frames = new List<List<List<FusionGeometry>>>(num);
                        bool any = false;
                        for (int i = 0; i < num; i++)
                        {
                            var g = ReadFsomGeometry(Path.Combine(o.OutputDir, stem + "_" + (baseId + i) + ".fsom"));
                            if (g.Count > 0) any = true;
                            frames.Add(g);
                        }
                        if (!any) { skipped++; continue; }

                        try
                        {
                            var anim = DGRP3DAnim.FromFrames(stem + "_" + baseId, frames);
                            if (!o.DryRun) WriteAtomic(animPath, fs => anim.Write(fs));

                            // the base file is now the container; the other frames are redundant
                            for (int i = 1; i < num; i++)
                            {
                                var p = Path.Combine(o.OutputDir, stem + "_" + (baseId + i) + ".fsom");
                                if (File.Exists(p))
                                {
                                    if (!o.DryRun) File.Delete(p);
                                    removed++;
                                }
                            }

                            merged++;
                            if (!o.Quiet)
                                Console.WriteLine($"{(o.DryRun ? "would merge" : "")}[{merged,5}] {stem}_{baseId}.fsom  {num} frames, {anim.BlockCount} block(s)");
                        }
                        catch (Exception e)
                        {
                            failed++;
                            Console.Error.WriteLine($"  ! {stem}_iff_{baseId}.fsom: {e.GetType().Name} {e.Message}");
                        }
                    }
                }
            }

            Console.WriteLine();
            if (o.DryRun) Console.WriteLine("(dry run — nothing written or deleted)");
            Console.WriteLine($"animated objects found:  {objects}");
            Console.WriteLine($"merged:                  {merged}");
            Console.WriteLine($"upgraded containers:     {converted}");
            Console.WriteLine($"already current:         {reused}");
            Console.WriteLine($"skipped (empty/human):   {skipped}");
            Console.WriteLine($"redundant frames removed:{removed}");
            Console.WriteLine($"failed:                  {failed}");
            if (o.Verify) return Verify(o.OutputDir);
            return failed == 0 ? 0 : 1;
        }

        /// <summary>Reads one per-frame FSOM into per-dynamic-group geometry (empty if absent).</summary>
        static List<List<FusionGeometry>> ReadFsomGeometry(string path)
        {
            var result = new List<List<FusionGeometry>>();
            if (!File.Exists(path)) return result;
            try
            {
                using var fs = File.OpenRead(path);
                using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                ms.Position = 0;
                using var io = FSO.Files.Utils.IoBuffer.FromStream(ms, FSO.Files.Utils.ByteOrder.LITTLE_ENDIAN);
                if (io.ReadCString(4) != "FSOm") return result;
                io.ReadInt32(); // version
                io.ReadInt32(); // reconstruct version
                io.ReadPascalString(); // name
                int groups = io.ReadInt32();
                for (int g = 0; g < groups; g++)
                {
                    int geoms = io.ReadInt32();
                    var list = new List<FusionGeometry>(geoms);
                    for (int j = 0; j < geoms; j++)
                    {
                        var fg = new FusionGeometry { PixelSPR = io.ReadUInt16(), PixelDir = io.ReadUInt16() };
                        int vertCount = io.ReadInt32();
                        for (int v = 0; v < vertCount; v++) fg.Verts.Add(DGRP3DAnim.ReadVert(io));
                        int indexCount = io.ReadInt32();
                        for (int k = 0; k < indexCount; k++) fg.Indices.Add(io.ReadInt32());
                        list.Add(fg);
                    }
                    result.Add(list);
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Merges every Graphic frame of one animated object into a single DGRP3DAnim file.
        /// Returns false when the object must stay on the ordinary per-drawgroup path (a
        /// hand-authored mesh covers any frame), in which case the caller generates the frame
        /// normally. Returns true when the object is fully handled (merged, already merged, or
        /// empty).
        /// </summary>
        /// <summary>
        /// Finds the OBJD whose graphic range owns a drawgroup. Using the first OBJD in the file (as
        /// before) mislabels dynamic sprites on multi-object files, which can union animation parts
        /// parked far from the main sprite and blow the mesh up.
        /// </summary>
        static OBJD FindOwner(List<OBJD> objds, int chunkId)
        {
            if (objds == null) return null;
            foreach (var o in objds)
                if (o.NumGraphics > 0 && o.BaseGraphicID > 0 && chunkId >= o.BaseGraphicID && chunkId < o.BaseGraphicID + o.NumGraphics)
                    return o;
            return null;
        }

        static bool TryMergeAnimated(IffFile iff, string stem, OBJD obj, RemeshAliasSet aliases,
            HashSet<string> haveMesh, Options o, ref int generated, ref int reused, ref int skippedEmpty, ref int failed, ref int degenerateNormals)
        {
            var baseId = obj.BaseGraphicID;
            var num = obj.NumGraphics;
            if (num <= 1 || baseId == 0) return false;

            // Human-authored meshes always win: if any frame is covered, leave the whole object
            // on the per-frame path so the hand-made mesh is not shadowed.
            for (int i = 0; i < num; i++)
            {
                var n = stem + "_" + (baseId + i) + ".fsom";
                if (haveMesh.Contains(n) || haveMesh.Contains(RemeshPackage.MapFileName(n, aliases))) return false;
            }

            var animRaw = stem + "_" + baseId + ".fsom";
            var outPath = Path.Combine(o.OutputDir, animRaw);
            if (IsAnimatedFile(outPath)) { reused++; return true; }

            var frames = new List<List<List<FusionGeometry>>>(num);
            int tris = 0;
            for (int i = 0; i < num; i++)
            {
                var d = iff.Get<DGRP>((ushort)(baseId + i));
                if (d == null) { frames.Add(new List<List<FusionGeometry>>()); continue; }
                var groups = DGRP3DLegacy.Build(d, obj, out _);
                // The merged container bypasses Convert(), so the repair has to happen here too -
                // an animated object is exactly as likely to carry a degenerate triangle as a
                // single-state one, and a NaN normal would end up baked into the container.
                degenerateNormals += DegenerateNormalRepair.RepairAll(groups);
                frames.Add(groups);
                tris += groups.Sum(g => g.Sum(x => x.Indices.Count / 3));
            }

            if (tris == 0) { skippedEmpty++; return true; }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var anim = DGRP3DAnim.FromFrames(stem + "_" + baseId, frames);
                WriteAtomic(outPath, fs => anim.Write(fs));
                generated++;
                if (!o.Quiet)
                    Console.WriteLine($"[{generated,5}] {animRaw}  {num} frames, {anim.BlockCount} block(s), {tris} tris  {watch.ElapsedMilliseconds} ms");
                return true;
            }
            catch (Exception e)
            {
                failed++;
                Console.Error.WriteLine($"  ! {animRaw}: {e.GetType().Name} {e.Message}");
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                return false; // fall back to per-frame generation
            }
        }

        /// <summary>
        /// Writes <paramref name="path"/> through a sibling temp file and renames it into place, so a
        /// run that is killed (or a write that throws) mid-file never leaves a truncated .fsom behind.
        /// That matters because an existing output file is treated as finished and skipped on resume
        /// ("already present"), so a truncated one would be kept forever.
        /// </summary>
        static void WriteAtomic(string path, Action<Stream> write)
        {
            var temp = path + ".tmp";
            try
            {
                using (var fs = File.Create(temp)) write(fs);
                File.Move(temp, path, true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        /// <summary>True when a file exists and is a gzip-wrapped DGRP3DAnim container.</summary>
        static bool IsAnimatedFile(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using var fs = File.OpenRead(path);
                using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                var head = new byte[4];
                int n = gz.Read(head, 0, 4);
                return n == 4 && head[0] == (byte)'F' && head[1] == (byte)'S' && head[2] == (byte)'O' && head[3] == (byte)'a';
            }
            catch { return false; }
        }

        static List<List<DGRP3DGeometry>> Convert(List<List<FusionGeometry>> groups)
        {
            var result = new List<List<DGRP3DGeometry>>();
            foreach (var group in groups)
            {
                var list = new List<DGRP3DGeometry>();
                foreach (var fg in group)
                {
                    if (fg.Indices.Count == 0) continue;
                    list.Add(new DGRP3DGeometry
                    {
                        PixelDir = fg.PixelDir,
                        PixelSPR = fg.PixelSPR,
                        SVerts = fg.Verts,
                        SIndices = fg.Indices,
                    });
                }
                result.Add(list);
            }
            return result;
        }

        /// <summary>Reads a FAR entry, decompressing RefPack entries when the two length fields differ.</summary>
        static byte[] ReadEntry(FAR1Archive archive, FarEntry entry)
        {
            var data = archive.GetEntry(entry);
            if (data == null) return null;
            if (entry.DataLength == entry.DataLength2) return data;
            try
            {
                var dec = new Decompresser { DecompressedSize = entry.DataLength2 };
                return dec.Decompress(data);
            }
            catch
            {
                return data;
            }
        }

        static List<string> DiscoverFars(string root)
        {
            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Sound", "SoundData", "Music", "support", "UIGraphics", "Animation", "Textures"
            };

            var result = new List<string>();
            void Walk(string dir)
            {
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir); } catch { return; }
                foreach (var sub in subdirs)
                    if (!skipDirs.Contains(Path.GetFileName(sub))) Walk(sub);
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.far"); } catch { return; }
                result.AddRange(files);
            }
            Walk(root);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        static int Verify(string outputDir)
        {
            var files = Directory.GetFiles(outputDir, "*.fsom");
            long bytes = 0;
            int ok = 0, bad = 0;
            long tris = 0;
            foreach (var f in files)
            {
                bytes += new FileInfo(f).Length;
                try
                {
                    tris += ParseFsom(f);
                    ok++;
                }
                catch (Exception e)
                {
                    bad++;
                    Console.Error.WriteLine($"  ! verify failed {Path.GetFileName(f)}: {e.Message}");
                }
            }
            Console.WriteLine();
            Console.WriteLine($"verify: {files.Length} .fsom file(s), {bytes / 1024} KiB, {tris:n0} triangles, {ok} ok / {bad} bad");
            return bad == 0 ? 0 : 1;
        }

        /// <summary>Reads an FSOM or animated container back and returns its triangle count.</summary>
        static long ParseFsom(string path)
        {
            using var fs = File.OpenRead(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            ms.Position = 0;
            using var io = FSO.Files.Utils.IoBuffer.FromStream(ms, FSO.Files.Utils.ByteOrder.LITTLE_ENDIAN);
            var magic = io.ReadCString(4);
            if (magic == "FSOa") return ParseAnim(io);
            if (magic != "FSOm") throw new Exception("bad magic " + magic);
            io.ReadInt32(); // version
            io.ReadInt32(); // reconstruct version
            io.ReadPascalString(); // name
            int geomGroups = io.ReadInt32();
            long tris = 0;
            for (int g = 0; g < geomGroups; g++)
            {
                int geoms = io.ReadInt32();
                for (int i = 0; i < geoms; i++)
                {
                    io.ReadUInt16(); // PixelSPR
                    io.ReadUInt16(); // PixelDir
                    int vertCount = io.ReadInt32();
                    io.Seek(SeekOrigin.Current, vertCount * 32); // 8 floats
                    int indexCount = io.ReadInt32();
                    io.Seek(SeekOrigin.Current, indexCount * 4);
                    tris += indexCount / 3;
                }
            }
            return tris;
        }

        /// <summary>Validates an animated container (v1 or v2) and returns its triangle count.</summary>
        static long ParseAnim(FSO.Files.Utils.IoBuffer io)
        {
            int version = io.ReadInt32();
            io.ReadInt32(); // reconstruct version
            io.ReadPascalString(); // name
            long tris = 0;

            if (version <= 1)
            {
                int blocks = io.ReadInt32();
                for (int b = 0; b < blocks; b++)
                {
                    int groups = io.ReadInt32();
                    for (int g = 0; g < groups; g++)
                    {
                        int geoms = io.ReadInt32();
                        for (int j = 0; j < geoms; j++) tris += SkipGeom(io);
                    }
                }
                int frames = io.ReadInt32();
                for (int f = 0; f < frames; f++)
                {
                    io.ReadInt32(); // block index
                    io.Seek(SeekOrigin.Current, io.ReadInt32() * 4); // selectors
                }
            }
            else
            {
                int blocks = io.ReadInt32();
                for (int b = 0; b < blocks; b++)
                {
                    int geoms = io.ReadInt32();
                    for (int j = 0; j < geoms; j++) tris += SkipGeom(io);
                }
                int frames = io.ReadInt32();
                for (int f = 0; f < frames; f++)
                {
                    int groups = io.ReadInt32();
                    for (int g = 0; g < groups; g++)
                    {
                        io.ReadInt32(); // block index
                        io.Seek(SeekOrigin.Current, io.ReadInt32() * 4); // selectors
                    }
                }
            }

            io.ReadInt32(); // mask type
            io.Seek(SeekOrigin.Current, 24); // bounds (6 floats)
            return tris;
        }

        static long SkipGeom(FSO.Files.Utils.IoBuffer io)
        {
            io.Seek(SeekOrigin.Current, io.ReadInt32() * 32); // verts (8 floats)
            int indexCount = io.ReadInt32();
            io.Seek(SeekOrigin.Current, indexCount * 4);
            return indexCount / 3;
        }
    }
}
