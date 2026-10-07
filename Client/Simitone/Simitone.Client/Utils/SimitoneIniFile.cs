using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// A single <c>key=value</c> sidecar ini that Simitone owns outright, with a
    /// read-merge-write writer.
    ///
    /// <para>
    /// <b>Why this exists rather than FreeSO's <c>IniConfig</c>.</b> <c>IniConfig.Save</c>
    /// opens the file with <see cref="FileMode.Create"/> - it truncates - and then writes
    /// <em>only the properties declared on the concrete subclass</em>. A file with two
    /// independently-declared subclasses pointed at it therefore has two writers that each
    /// erase the other: whichever key was saved last is the only one left in the file, and
    /// because every reader treats a missing key as "use the default", the loss is silent -
    /// the setting reverts rather than complaining. <c>simitone.ini</c> is exactly that file
    /// (<see cref="UI.Panels.Options.SunCycleSetting"/> and
    /// <see cref="Neighbourhood.NeighbourhoodViewSetting"/> both name it), so the whole
    /// sidecar is written through here instead: one writer for the whole file, which reads
    /// what is there, changes only the keys it is asked to change, and writes everything
    /// back. A key this build does not know about - written by a future version, or by hand -
    /// survives untouched.
    /// </para>
    ///
    /// <para>
    /// The on-disk shape is deliberately the one <c>IniConfig</c> reads and writes: a
    /// <c>#</c> comment line, then <c>key=value</c> lines. An ini written by either mechanism is
    /// therefore readable by the other, so an existing <c>simitone.ini</c> keeps its keys
    /// across this change, and a player who edits the file by hand still works.
    /// </para>
    ///
    /// <para>
    /// <b>Writes are staged and swapped</b> through <see cref="AtomicFileCommit"/>, so a
    /// half-written sidecar is never visible and the previous contents stay in
    /// <c>&lt;path&gt;.bak</c>. Writes <b>throw</b> on failure - the caller decides what a
    /// settings file that cannot be written means for it, and every caller logs a warning
    /// rather than swallowing. Reads are total: an unreadable file is an empty set of values.
    /// </para>
    /// </summary>
    public sealed class SimitoneIniFile
    {
        /// <summary>Comment written on the first line of a file this class creates.</summary>
        private readonly string _heading;

        /// <summary>Full path of the ini, or null when there is no user directory to put it in.</summary>
        public string Path { get; }

        /// <summary>
        /// Opens the ini at <paramref name="path"/>.
        /// </summary>
        /// <param name="path">
        /// The sidecar's full path. Null or empty makes every read answer "nothing stored" and
        /// every write a no-op, which is what a game running with no user directory needs.
        /// </param>
        /// <param name="headingComment">The comment to write on the first line of a new file.</param>
        public SimitoneIniFile(string path, string headingComment)
        {
            Path = path;
            _heading = headingComment ?? string.Empty;
        }

        /// <summary>
        /// Every key currently in the file, in the spelling it is stored with, compared
        /// case-insensitively. A missing or unreadable file is an empty map.
        /// </summary>
        public IReadOnlyDictionary<string, string> ReadAll()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(Path)) return result;

            string[] lines;
            try
            {
                if (!File.Exists(Path)) return result;
                lines = File.ReadAllLines(Path);
            }
            catch (Exception)
            {
                // An unreadable settings file is the same as one nobody has written yet. The
                // caller logs what it is about to write over the top of it.
                return result;
            }

            foreach (var line in lines)
            {
                var key = KeyOf(line);
                if (key == null) continue;
                result[key] = ValueOf(line);
            }

            return result;
        }

        /// <summary>
        /// The stored value for <paramref name="key"/>, or <c>null</c> when the file does not
        /// carry it. A missing key is "never configured" - each setting resolves that to its own
        /// default, which is the same answer as a value it does not recognise.
        /// </summary>
        public string Get(string key)
        {
            if (key == null) return null;
            return ReadAll().TryGetValue(key, out var value) ? value : null;
        }

        /// <summary>Stores one key, leaving every other line in the file exactly as it was.</summary>
        /// <param name="key">The key to set. Required.</param>
        /// <param name="value">The value to store verbatim. Required.</param>
        public void Set(string key, string value)
        {
            SetAll(new[] { new KeyValuePair<string, string>(key, value) });
        }

        /// <summary>
        /// Stores several keys in one read-merge-write, which is how a sidecar that is written
        /// as a unit (the sound file's four mute flags) avoids rewriting the file four times.
        /// </summary>
        /// <param name="values">The keys to set. Null entries are rejected.</param>
        public void SetAll(IEnumerable<KeyValuePair<string, string>> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (string.IsNullOrEmpty(Path)) return;

            //The caller's order, kept: the keys a file did not have yet are appended in it, so the
            //same four mutes always produce the same four lines in the same order. A Dictionary
            //would not promise that, and a sidecar whose line order wobbles between saves is one
            //more thing for a player looking at it in an editor to wonder about.
            var wanted = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                if (pair.Key == null) throw new ArgumentException("A key may not be null.", nameof(values));
                //A repeated key keeps its first position and its last value, which is what a
                //merge-write should do: the caller's ordering is the file's ordering.
                if (seen.Add(pair.Key)) wanted.Add(pair);
                else wanted[wanted.FindIndex(p => string.Equals(p.Key, pair.Key, StringComparison.OrdinalIgnoreCase))] = pair;
            }

            AtomicFileCommit.Write(Path, RenderLines(ReadLines(), wanted));
        }

        // ---- the line shape ------------------------------------------------------------------

        /// <summary>
        /// The file's current lines, or an empty list when there is no file yet. Read once and
        /// reused for both the merge and the write, so a save cannot interleave with another
        /// writer's read and drop one of the two sets of keys.
        /// </summary>
        private List<string> ReadLines()
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(Path)) return result;

            try
            {
                if (File.Exists(Path)) result.AddRange(File.ReadAllLines(Path));
            }
            catch (Exception)
            {
                // Treated as "no file": the keys being saved are the ones that matter, and
                // anything unreadable could not have been read back by this build either.
            }

            return result;
        }

        /// <summary>
        /// The file's lines with <paramref name="wanted"/> merged in: a key already present is
        /// replaced in place, keeping its position, its spacing and its spelling; a key that is
        /// not there is appended, in the caller's order. Nothing else in the file changes.
        /// </summary>
        private byte[] RenderLines(List<string> existing, List<KeyValuePair<string, string>> wanted)
        {
            var lines = new List<string>(existing.Count + wanted.Count + 1);

            //a file with no comment line gets one, so a sidecar this class creates is
            //self-describing in an editor.
            var hasComment = false;
            foreach (var line in existing)
            {
                var clean = line.Trim();
                if (clean.Length > 0 && (clean[0] == '#' || clean[0] == ';' || clean[0] == '['))
                {
                    hasComment = true;
                    break;
                }
            }
            if (!hasComment) lines.Add("# " + _heading);

            var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in wanted) pending[pair.Key] = pair.Value;

            foreach (var line in existing)
            {
                var key = KeyOf(line);
                if (key == null || !pending.Remove(key, out var value))
                {
                    lines.Add(line);
                    continue;
                }
                lines.Add(key + "=" + value);
            }

            //Keys this file did not have yet, in the caller's order.
            foreach (var pair in wanted)
            {
                if (pending.ContainsKey(pair.Key)) lines.Add(pair.Key + "=" + pair.Value);
            }

            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                builder.Append(line).Append('\n');
            }
            return new UTF8Encoding(false).GetBytes(builder.ToString());
        }

        /// <summary>
        /// The key a line carries, or <c>null</c> when the line is not a <c>key=value</c> line.
        /// Comments (<c>#</c>, <c>;</c>), section headers and blank lines are skipped exactly the
        /// way <c>IniConfig.Load</c> skips them, so a file written by either is read the same.
        /// </summary>
        private static string KeyOf(string line)
        {
            if (line == null) return null;
            var clean = line.Trim();
            if (clean.Length == 0 || clean[0] == '#' || clean[0] == ';' || clean[0] == '[') return null;
            var split = clean.IndexOf('=');
            if (split <= 0) return null;
            return clean.Substring(0, split).Trim();
        }

        /// <summary>The value half of a <c>key=value</c> line, trimmed.</summary>
        private static string ValueOf(string line)
        {
            var clean = line.Trim();
            var split = clean.IndexOf('=');
            return split < 0 ? string.Empty : clean.Substring(split + 1).Trim();
        }

        // ---- value formatting ----------------------------------------------------------------

        /// <summary>
        /// A boolean as <c>IniConfig</c> wrote it (<c>True</c>/<c>False</c>). Stated here rather
        /// than left to <c>ToString</c> so the spelling of a stored flag is one line and not
        /// something a reader has to guess at: both <see cref="ReadBool"/> and every
        /// <c>Resolve</c> rule accept it case-insensitively either way.
        /// </summary>
        public static string FormatBool(bool value)
        {
            return value ? "True" : "False";
        }
    }
}
