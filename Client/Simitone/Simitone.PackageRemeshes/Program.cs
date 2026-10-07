using Simitone.Client.Utils;
using System;
using System.IO;

namespace Simitone.PackageRemeshes
{
    /// <summary>
    /// Flattens a community remesh pack into a <c>MeshReplace</c> folder.
    ///
    /// <para>Usage: <c>Simitone.PackageRemeshes &lt;remesh-root&gt; &lt;output-dir&gt;</c>.</para>
    ///
    /// <para>The pack is optional, machine-local content that is deliberately not committed to this
    /// repository (see the root <c>.gitignore</c>). A missing pack is therefore a normal, successful
    /// outcome, not an error: the tool reports it and exits 0 so that a build of Simitone without the
    /// pack produces exactly the same game as before. Only a genuinely unexpected failure - an
    /// unreadable pack, a copy that went wrong - exits non-zero.</para>
    ///
    /// <para>The actual naming and conflict rules live in <see cref="RemeshPackage"/> in
    /// Simitone.Client, where they are unit tested; this program only moves files.</para>
    /// </summary>
    public static class Program
    {
        /// <summary>Entry point. See the class summary for the argument contract.</summary>
        /// <returns>0 on success or "pack not installed"; 1 on an unexpected failure.</returns>
        public static int Main(string[] args)
        {
            if (args == null || args.Length < 2)
            {
                Console.Error.WriteLine("usage: Simitone.PackageRemeshes <remesh-root> <output-dir>");
                return 1;
            }

            var remeshRoot = args[0];
            var outputDir = args[1];

            try
            {
                return Package(remeshRoot, outputDir);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("remesh packaging failed: " + e);
                return 1;
            }
        }

        private static int Package(string remeshRoot, string outputDir)
        {
            if (string.IsNullOrWhiteSpace(remeshRoot) || !Directory.Exists(remeshRoot))
            {
                Console.WriteLine("remesh pack not present at '" + remeshRoot + "'; nothing to package.");
                return 0;
            }

            var rootMetadata = Path.Combine(remeshRoot, RemeshPackage.MetadataFileName);
            if (!File.Exists(rootMetadata))
            {
                Console.WriteLine("remesh pack at '" + remeshRoot + "' has no " +
                    RemeshPackage.MetadataFileName + "; not a pack, nothing to package.");
                return 0;
            }

            var plan = RemeshPackage.BuildPackPlan(remeshRoot);
            Directory.CreateDirectory(outputDir);

            foreach (var entry in plan.Entries)
            {
                // overwrite: an existing entry may be FreeSO's own bundled sample mesh, or a stale
                // copy from a previous pack version. Unrelated files in the folder are left alone.
                File.Copy(entry.SourcePath, Path.Combine(outputDir, entry.DestinationName), true);
            }

            foreach (var conflict in plan.Conflicts)
            {
                Console.WriteLine("remesh conflict: kept " + conflict.KeptSourcePath + " (priority " +
                    conflict.KeptPriority + ") over " + conflict.DiscardedSourcePath + " (priority " +
                    conflict.DiscardedPriority + ") for " + conflict.DestinationName);
            }

            Console.WriteLine("packaged " + plan.Entries.Count + " files (" + plan.ConflictCount +
                " conflicts resolved) into " + outputDir);
            return 0;
        }
    }
}
