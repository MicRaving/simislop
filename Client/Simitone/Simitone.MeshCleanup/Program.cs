using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Simitone.MeshCleanup
{
    /// <summary>
    /// Headless authoring tool for the manual mesh-cleanup pass: export a generated <c>.fsom</c> to
    /// OBJ + textures, look at it from the game's four views, import the edited OBJ back, and check
    /// the result before it ships. This is the offline equivalent of Volcanic's FSOM editor
    /// (export/import OBJ), with a preview renderer added so an agent working alone can see what it
    /// is doing. See docs/MESH_MANUAL_CLEANUP.md for the procedure this implements.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) { Usage(); return 2; }
                switch (args[0].ToLowerInvariant())
                {
                    case "sprites": return Sprites(args);
                    case "export": return Export(args);
                    case "render": return Render(args);
                    case "import": return Import(args);
                    case "check": return Check(args);
                    case "help":
                    case "--help":
                    case "-h": Usage(); return 0;
                    default:
                        Console.Error.WriteLine("unknown command '" + args[0] + "'");
                        Usage();
                        return 2;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                if (Environment.GetEnvironmentVariable("MESHCLEANUP_DEBUG") == "1") Console.Error.WriteLine(e.ToString());
                return 1;
            }
        }

        static void Usage()
        {
            Console.Error.WriteLine(@"Simitone.MeshCleanup - offline cleanup tools for generated .fsom meshes

  sprites  --game <install|far|iff> [--iff <name>] --dgrp <id> --out <dir> [--depth]
           Writes the object's own sprites (ground truth) and optional depth maps.

  export   --fsom <file> --game <install|far|iff> [--iff <name>] --out <dir>
           Writes <mesh>.obj + <mesh>.mtl + one PNG per material + <mesh>.manifest.json.

  render   --fsom <file> --game <install|far|iff> [--iff <name>] --out <png>
           [--size N] [--near smaller|larger] [--tex-dir <dir>] [--az <deg>] [--el <deg>]
           [--bilinear]
           Renders the mesh from the game's four isometric rotations (panels 0,1,2,3),
           or a single off-axis panel when --az is given. --bilinear matches the GPU's
           linear texture filtering (the game's look) instead of nearest sampling.

  import   --obj <file> --name <stem_iff_dgrp> --out <file|dir> [--tex-out <dir>]
           [--game <install|far|iff>] [--iff <name>]
           Writes a .fsom from an edited OBJ; custom textures are copied next to it.

  check    --fsom <file> [--json <report>]
           Structural report: indices, finiteness, bounds, debris, spikes, winding.

Notes: the OBJ material names are the texture contract and must not be renamed
(<dyn>_SPR_rot<dir>_<spr> or <dyn>_TEX_<id>).");
        }

        static int Sprites(string[] args)
        {
            var cli = Cli.Parse(args, 1);
            var game = OpenGame(cli, cli.Get("iff"));
            var dgrp = game.GetDgrp(cli.GetInt("dgrp"));
            Renderer.RenderSprites(game, dgrp, cli.Require("out"), cli.Has("depth"));
            game.Dispose();
            return 0;
        }

        static int Export(string[] args)
        {
            var cli = Cli.Parse(args, 1);
            var fsom = cli.Require("fsom");
            int dgrpId;
            if (!Cli.TrySplitMeshName(fsom, out var stem, out dgrpId, out _))
                throw new ArgumentException("--fsom must be named <stem>_iff_<dgrp>.fsom so the drawgroup is known");
            var gameArg = cli.Get("game") ?? cli.Get("far");
            GameData game = null;
            DGRP dgrp = null;
            try
            {
                if (gameArg != null)
                {
                    game = OpenGame(cli, cli.Get("iff") ?? stem);
                    dgrp = game.GetDgrp(dgrpId);
                }
                else
                {
                    Console.Error.WriteLine("warning: no --game/--far given; sprite textures will be missing from the OBJ");
                }
                ObjIo.Export(fsom, game, dgrp, cli.Require("out"));
            }
            finally { game?.Dispose(); }
            return 0;
        }

        static int Render(string[] args)
        {
            var cli = Cli.Parse(args, 1);
            var fsom = cli.Require("fsom");
            int dgrpId;
            if (!Cli.TrySplitMeshName(fsom, out var stem, out dgrpId, out _))
                throw new ArgumentException("--fsom must be named <stem>_iff_<dgrp>.fsom so the drawgroup is known");
            var gameArg = cli.Get("game") ?? cli.Get("far");
            GameData game = null;
            DGRP dgrp = null;
            try
            {
                if (gameArg != null)
                {
                    game = OpenGame(cli, cli.Get("iff") ?? stem);
                    dgrp = game.GetDgrp(dgrpId);
                }
                var larger = !string.Equals(cli.Get("near", "larger"), "smaller", StringComparison.OrdinalIgnoreCase);
                float? az = cli.Get("az") != null ? cli.GetFloat("az") : (float?)null;
                float? el = cli.Get("el") != null ? cli.GetFloat("el") : (float?)null;
                Renderer.RenderMesh(fsom, game, dgrp, cli.Require("out"), cli.GetInt("size", 512), cli.Get("tex-dir"), larger, az, el, cli.Has("bilinear"));
            }
            finally { game?.Dispose(); }
            return 0;
        }

        static int Import(string[] args)
        {
            var cli = Cli.Parse(args, 1);
            var obj = cli.Require("obj");
            var name = cli.Require("name");
            if (!Cli.TrySplitMeshName(name + ".fsom", out var stem, out var dgrpId, out _))
                throw new ArgumentException("--name must look like <stem>_iff_<dgrp> (e.g. gargoyle_iff_100)");

            var outArg = cli.Require("out");
            var outFile = outArg.EndsWith(".fsom", StringComparison.OrdinalIgnoreCase) ? outArg : Path.Combine(outArg, name + ".fsom");
            var texOut = cli.Get("tex-out", Path.GetDirectoryName(Path.GetFullPath(outFile)));
            Directory.CreateDirectory(texOut);

            var result = ObjIo.Import(obj, Path.GetDirectoryName(Path.GetFullPath(obj)));
            foreach (var w in result.Warnings) Console.Error.WriteLine("warning " + w);

            // Optional plausibility check: every sprite selector must resolve in the real DGRP.
            var gameArg = cli.Get("game") ?? cli.Get("far");
            if (gameArg != null)
            {
                var game = OpenGame(cli, cli.Get("iff") ?? stem);
                var dgrp = game.GetDgrp(dgrpId);
                for (int dyn = 0; dyn < result.Groups.Count; dyn++)
                    foreach (var geom in result.Groups[dyn])
                    {
                        if (geom.CustomTexture) continue;
                        if (game.GetFrame(dgrp, geom.PixelDir, geom.PixelSPR) == null)
                            Console.Error.WriteLine("warning " + ObjIo.MaterialName(dyn, geom) +
                                ": sprite selector does not resolve in " + game.IffName);
                    }
                game.Dispose();
            }

            var bounds = FsomFile.WriteFsom(outFile, name, result.Groups);
            Console.WriteLine("wrote " + outFile + "  verts " + result.Groups.Sum(g => g.Sum(x => x.Verts.Count)) +
                "  tris " + (result.Groups.Sum(g => g.Sum(x => x.Indices.Count)) / 3) +
                "  bounds " + Vec(bounds.Min) + ".." + Vec(bounds.Max));

            // Custom textures: copy the PNGs the OBJ referenced next to the pack file with the name
            // the runtime provider looks up: <iff stem>_iff_TEX_<id>.png (the drawgroup is absent
            // from the texture name on purpose - one texture can serve several states).
            int copied = 0;
            for (int dyn = 0; dyn < result.Groups.Count; dyn++)
                foreach (var geom in result.Groups[dyn])
                {
                    if (!geom.CustomTexture) continue;
                    var source = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(obj)), ObjIo.MaterialName(dyn, geom) + ".png");
                    if (!File.Exists(source)) throw new FileNotFoundException("custom texture missing next to the OBJ: " + source);
                    var dest = Path.Combine(texOut, stem + "_iff_TEX_" + geom.PixelSPR + ".png");
                    File.Copy(source, dest, true);
                    copied++;
                }
            if (copied > 0) Console.WriteLine("copied " + copied + " custom texture(s) to " + texOut);
            return 0;
        }

        static int Check(string[] args)
        {
            var cli = Cli.Parse(args, 1);
            return Checks.Run(cli.Require("fsom"), cli.Get("json"));
        }

        static GameData OpenGame(Cli cli, string iffName)
        {
            var game = cli.Get("game") ?? cli.Get("far");
            if (string.IsNullOrWhiteSpace(game))
                throw new ArgumentException("--game <install|far|iff> is required to resolve the object's sprites");
            if (string.IsNullOrWhiteSpace(iffName))
                throw new ArgumentException("--iff <name> is required when the mesh name does not carry the IFF stem");
            return GameData.Open(game, iffName);
        }

        static string Vec(Microsoft.Xna.Framework.Vector3 v)
            => FormattableString.Invariant($"({v.X:0.###},{v.Y:0.###},{v.Z:0.###})");
    }
}
