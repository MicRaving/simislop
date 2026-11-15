using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Simitone.MeshCleanup
{
    /// <summary>
    /// Minimal option parser: <c>--key value</c>, <c>--flag</c>, <c>--key=value</c>. Every command
    /// in this tool is driven from a shell, so a shared parser keeps the commands readable and the
    /// error text identical everywhere ("unknown option" should mean the same thing in all of them).
    /// </summary>
    public sealed class Cli
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Cli Parse(string[] args, int start)
        {
            var cli = new Cli();
            for (int i = start; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--")) throw new ArgumentException("unexpected argument '" + arg + "' (options start with --)");
                var body = arg.Substring(2);
                string key, inline = null;
                var eq = body.IndexOf('=');
                if (eq >= 0) { key = body.Substring(0, eq); inline = body.Substring(eq + 1); }
                else key = body;

                if (inline != null) cli.values[key] = inline;
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) cli.values[key] = args[++i];
                else cli.flags.Add(key);
            }
            return cli;
        }

        public bool Has(string key) => flags.Contains(key) || values.ContainsKey(key);

        public string Get(string key, string fallback = null)
            => values.TryGetValue(key, out var v) ? v : fallback;

        public string Require(string key)
        {
            if (!values.TryGetValue(key, out var v)) throw new ArgumentException("missing required option --" + key);
            return v;
        }

        public int GetInt(string key, int fallback = 0)
            => values.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

        public float GetFloat(string key, float fallback = 0f)
            => values.TryGetValue(key, out var v) ? float.Parse(v, CultureInfo.InvariantCulture) : fallback;

        /// <summary>
        /// Splits <c>stem_iff_dgrp</c> (the runtime file naming, e.g.
        /// <c>gargoyle_iff_100.fsom</c>) back into the IFF name the game data uses and the
        /// drawing group id. IFF stems themselves may contain underscores, so the split is
        /// anchored on the last two underscores rather than the first.
        /// </summary>
        public static bool TrySplitMeshName(string fileName, out string iffStem, out int dgrpId, out string rawName)
        {
            iffStem = null; dgrpId = 0; rawName = null;
            var name = Path.GetFileNameWithoutExtension(fileName);
            // The runtime name is <iff stem>_iff_<dgrp> because the file extension is folded in
            // ("Gargoyle.iff" -> "gargoyle_iff_100"). Anchor on the extension marker, not the last
            // two underscores: an IFF stem may itself contain underscores.
            var marker = name.LastIndexOf("_iff_", StringComparison.OrdinalIgnoreCase);
            if (marker <= 0) return false;
            if (!int.TryParse(name.Substring(marker + 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out dgrpId)) return false;
            iffStem = name.Substring(0, marker);
            rawName = name;
            return true;
        }
    }
}
