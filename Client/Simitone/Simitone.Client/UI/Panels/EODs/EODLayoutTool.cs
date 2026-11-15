using System;
using System.Collections.Generic;
using System.IO;

namespace Simitone.Client.UI.Panels.EODs
{
    /// <summary>
    /// The command-line face of <see cref="EODLayoutWriter"/>:
    /// <c>Simitone.Windows.exe -regen-eod-layouts &lt;contentDir&gt;</c> rewrites the generated
    /// <c>.uis</c> layouts under <c>&lt;contentDir&gt;\UI\uiscripts</c> and prints the generated
    /// <c>.cst</c> caption lines. It never starts the game.
    /// </summary>
    /// <remarks>
    /// To regenerate the committed files after changing <see cref="EODLayouts"/>:
    /// <c>Simitone.Windows.exe -regen-eod-layouts Client\Simitone\Simitone.Client\Content</c>,
    /// then paste the printed <c>.cst</c> block into <c>_f900_simitone.cst</c>.
    /// <c>EODLayoutBuilderTests</c> fails until the committed files match what this writes.
    /// </remarks>
    public static class EODLayoutTool
    {
        /// <summary>The switch that selects this tool.</summary>
        public const string Switch = "-regen-eod-layouts";

        /// <summary>Whether the command line asks for this tool.</summary>
        public static bool IsRequested(string[] args)
        {
            return args != null && args.Length > 0 && string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Runs the tool. Returns the process exit code: 0 on success, 2 for a missing or bad
        /// directory argument, 1 when writing failed.
        /// </summary>
        public static int Run(string[] args, TextWriter output)
        {
            if (args == null || args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                output.WriteLine("usage: " + Switch + " <content directory>");
                return 2;
            }

            try
            {
                var written = EODLayoutWriter.WriteLayouts(args[1]);
                foreach (var path in written) output.WriteLine("wrote " + path);
                output.WriteLine();
                output.WriteLine("; paste into _f900_simitone.cst:");
                foreach (var line in EODLayoutWriter.CstLines()) output.WriteLine(line);
                return 0;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                output.WriteLine("failed: " + e.Message);
                return 1;
            }
        }
    }
}
