using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests
{
    /// <summary>
    /// A sweep over this repository's own source for two shapes that are silent in a shipped
    /// windowed game.
    ///
    /// <para>
    /// <b><c>Console.Write</c> in the content and file-format layers.</b> The game is a windowed
    /// Windows executable with no attached console, so a <c>Console.WriteLine</c> is not a
    /// degraded log - it is <i>no log at all</i>. The message is not hidden behind a window; it
    /// is simply never written. A save failure reported that way is a save failure the player can
    /// neither see nor send us, which is why <c>ContentLog</c> exists inside
    /// <c>FSO.Content</c> and <c>PIFFRegistry.Sink</c> / <c>Trace</c> inside <c>FSO.Files</c>.
    /// </para>
    ///
    /// <para>
    /// <b>An empty <c>catch</c> in the file-format layer.</b> The two deliberate swallowings
    /// (<c>IsOpenableNeighborhood</c>, <c>IsAnimatedFile</c>) are documented in place; anything
    /// else that catches and says nothing turns a corrupt file into a silently wrong one. The
    /// allow-list below is the set of exceptions, so the next one has to be added deliberately.
    /// </para>
    ///
    /// <para>
    /// It is a test about tests in the same way <see cref="GlobalMutationGuardTests"/> is: the
    /// failure it prevents is a regression that is invisible at runtime, and the alternative -
    /// a review checklist - is the thing that does not survive. Deliberately narrow - a false
    /// positive serialises a correct class for no reason, and a false negative lets the silence
    /// back in.
    /// </para>
    /// </summary>
    public sealed class SilentFailureGuardTests
    {
        /// <summary>
        /// The FreeSO projects whose failures the player can never see: the content layer and the
        /// file-format layer. The client is excluded because it has <c>SimitoneLog</c> and is
        /// audited for it separately; <c>FSO.UI</c> and the IDE are excluded because they are not
        /// on the player's save path.
        /// </summary>
        private static readonly string[] GuardedProjects =
        {
            "FreeSO/TSOClient/tso.content",
            "FreeSO/TSOClient/tso.files",
        };

        /// <summary>
        /// Catch blocks that are allowed to have no body, each with the reason it is deliberate.
        /// Matching is on the file name alone, so a new silent catch in one of these files has to
        /// be looked at rather than waved through.
        /// </summary>
        private static readonly (string File, string Marker, string Reason)[] AllowedSilentCatches =
        {
            ("TS1NeighbourProvider.cs", "unreadable or malformed path",
                "IsOpenableNeighborhood: a path it cannot vouch for is simply not a source"),
            ("RCMeshProvider.cs", "IsAnimatedFile",
                "IsAnimatedFile: a file that is not a gzip container is not an error, it is " +
                "the other branch of the question"),
            ("FAR3Archive.cs", "closing a handle we are throwing away anyway",
                "closing a handle that is being thrown away anyway"),
            ("FAR1Archive.cs", "closing a handle we are throwing away anyway",
                "closing a handle that is being thrown away anyway"),
            ("ContentLog.cs", "deliberately swallowed",
                "a broken log must never be the reason the game does not start"),
            ("FileFormatLog.cs", "deliberately swallowed",
                "a broken log must never be the reason the game does not start"),
            ("ResAction.cs", "a signal that never arrives",
                "the signalling side of a queue whose other half is documented below"),
            ("Decompresser.cs", "RefPack", "RefPack is reported through its own exception type"),
            ("WorldGlobalProvider.cs", "it probably doesn't exist",
                "an overlay that is not installed is the normal case, not a failure to report"),
            ("WorldObjectProvider.cs", "just silently ignore it",
                "an overlay that will not load is dropped on purpose - it is an override, not the object"),
            ("TSOp.cs", "we wanted it deleted anyways",
                "the copy step asked to delete a file that is already gone"),
            ("OBJD.cs", "really a mystery",
                "trailing object fields past the ones this build knows about"),
        };

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Client")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static IEnumerable<string> GuardedSourceFiles(string root)
        {
            foreach (var project in GuardedProjects)
            {
                var dir = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    //obj/ and bin/ are generated, and one of them is a copy of something else
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    {
                        continue;
                    }
                    yield return file;
                }
            }
        }

        /// <summary>
        /// A file that is never a report: a sentence about the problem, or the word "log" or
        /// "Console" appearing inside a doc comment. Narrow on purpose - the point is to catch the
        /// statement, not the documentation about it.
        /// </summary>
        private static readonly Regex ConsoleStatement = new Regex(
            @"^\s*Console\.(Write|WriteLine|Error|Out\.Write)\s*\(",
            RegexOptions.Compiled);

        [Fact]
        public void TheContentAndFileFormatLayersDoNotReportToTheConsole()
        {
            var root = RepositoryRoot();
            Assert.NotNull(root);

            var offenders = new List<string>();
            foreach (var file in GuardedSourceFiles(root))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (ConsoleStatement.IsMatch(lines[i]))
                    {
                        offenders.Add(Relative(root, file) + ":" + (i + 1) + "  " + lines[i].Trim());
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "these statements go nowhere at all in a windowed build. Use ContentLog in "
                + "tso.content, or Trace / PIFFRegistry.Sink in tso.files:\n"
                + string.Join("\n", offenders));
        }

        /// <summary>
        /// An <b>empty</b> catch in the file-format layer. This is the anti-pattern the audit
        /// named and the only shape narrow enough to guard without false positives: the body has
        /// nothing in it at all, or nothing but comments. A catch that logs, that rethrows, or
        /// that returns a documented sentinel is a contract, and there are a good many of those -
        /// the TS1 avatar save path returns a named decision, the BCF provider records the file in
        /// <c>FailedFiles</c>, the local tuning loader records a problem string. Those are all
        /// observable, and flagging them would make the guard noise rather than signal.
        /// </summary>
        [Fact]
        public void NoCatchInTheContentOrFileFormatLayerIsEmpty()
        {
            var root = RepositoryRoot();
            Assert.NotNull(root);

            var offenders = new List<string>();
            foreach (var file in GuardedSourceFiles(root))
            {
                var name = Path.GetFileName(file);
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], @"^\s*catch\s*(\(.*\))?\s*$")) continue;

                    //the braces of *this* catch, so a try's own body is not mistaken for it.
                    //Thirty lines is generous on purpose: a catch whose body is preceded by a long
                    //comment is still not an empty catch, and a short window would say it was.
                    var body = new List<string>();
                    var depth = 0;
                    var started = false;
                    for (int j = i + 1; j < lines.Length && j < i + 30; j++)
                    {
                        var line = lines[j].Trim();
                        if (line.StartsWith("{")) { started = true; depth++; continue; }
                        if (started)
                        {
                            if (line.StartsWith("}")) { depth--; if (depth == 0) break; continue; }
                            if (line.Length > 0) body.Add(line);
                        }
                    }

                    //nothing but comments left
                    var statements = body
                        .Where(b => !b.StartsWith("//") && !b.StartsWith("/*") && !b.StartsWith("*"))
                        .ToList();
                    if (statements.Count > 0) continue;

                    //documented deliberate swallowings
                    var exempt = AllowedSilentCatches.Any(a =>
                        string.Equals(a.File, name, StringComparison.OrdinalIgnoreCase));
                    if (exempt) continue;

                    offenders.Add(Relative(root, file) + ":" + (i + 1)
                        + "  " + lines[i].Trim()
                        + (body.Count == 0 ? "  (empty)" : "  (comments only)"));
                }
            }

            Assert.True(offenders.Count == 0,
                "these catch blocks have no body at all, which turns a corrupt file into a "
                + "silently wrong one. Log it, narrow the catch to what is actually recoverable, or "
                + "- if the swallowing really is deliberate - say so in a comment, and add the file "
                + "to this guard's allow-list:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void TheAllowListIsNotStaleAndTheExemptionsStillExist()
        {
            // A guard whose allow-list can rot is a guard that stops guarding. Every entry has to
            // still name a file that exists, or the entry is dead weight.
            var root = RepositoryRoot();
            Assert.NotNull(root);

            var present = new HashSet<string>(GuardedSourceFiles(root).Select(Path.GetFileName),
                StringComparer.OrdinalIgnoreCase);
            var stale = AllowedSilentCatches
                .Where(a => !present.Contains(a.File))
                .Select(a => a.File + " (" + a.Reason + ")")
                .ToList();

            Assert.True(stale.Count == 0,
                "these allow-list entries name files that are not in the guarded projects, so they "
                + "exempt nothing: " + string.Join(", ", stale));
        }

        private static string Relative(string root, string path)
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, '/')
                : full;
        }
    }
}
