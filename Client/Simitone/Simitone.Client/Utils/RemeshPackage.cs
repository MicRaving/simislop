using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// One entry of the <c>alias.simitone</c> array in a remesh pack's root <c>metadata.json</c>,
    /// in the "split" form: a drawgroup range of one Simitone object family is redirected at a
    /// different family. <c>trees3_iff</c> 1600..1603 becomes <c>trees_iff</c> 400..403, while
    /// 400..403 stays where it is, because Simitone has several distinct tree families where FreeSO
    /// had one.
    /// </summary>
    public sealed class RemeshAliasSplit
    {
        /// <summary>Human-readable label from the metadata; diagnostics only.</summary>
        public string Name { get; }

        /// <summary>Object family the matching drawgroups are redirected to.</summary>
        public string To { get; }

        /// <summary>First source drawgroup id covered by this split (inclusive).</summary>
        public int DrawGroupFrom { get; }

        /// <summary>How many drawgroup ids are covered, i.e. the range is [From, From + Range).</summary>
        public int Range { get; }

        /// <summary>Drawgroup id the first covered drawgroup is redirected to.</summary>
        public int DrawGroupTo { get; }

        public RemeshAliasSplit(string name, string to, int drawGroupFrom, int range, int drawGroupTo)
        {
            Name = name;
            To = to;
            DrawGroupFrom = drawGroupFrom;
            Range = range;
            DrawGroupTo = drawGroupTo;
        }

        /// <summary>True when <paramref name="drawGroup"/> falls in this split's half-open range.</summary>
        public bool Covers(int drawGroup)
        {
            return drawGroup >= DrawGroupFrom && drawGroup < DrawGroupFrom + Range;
        }

        /// <summary>The drawgroup id <paramref name="drawGroup"/> is redirected to.</summary>
        public int Remap(int drawGroup)
        {
            return DrawGroupTo + (drawGroup - DrawGroupFrom);
        }
    }

    /// <summary>
    /// One entry of the <c>alias.simitone</c> array, in either of its two shapes.
    ///
    /// <para><b>Simple rename</b> (<c>{"from":"doors3_iff","to":"doors3_sp_iff"}</c>): every
    /// file whose name starts with <c>from + "_"</c> has that prefix swapped for
    /// <c>to + "_"</c>. The drawgroup id and texture id are untouched, so this applies to mesh
    /// (<c>_&lt;drawgroup&gt;.fsom</c>) and texture (<c>_TEX_&lt;id&gt;.png</c>) files alike.</para>
    ///
    /// <para><b>Split</b> (see <see cref="RemeshAliasSplit"/>): only a mesh file whose drawgroup
    /// falls inside a listed range is renamed. Files outside every listed range keep their name.</para>
    /// </summary>
    public sealed class RemeshAlias
    {
        /// <summary>The object family prefix this alias reacts to (e.g. <c>doors3_iff</c>).</summary>
        public string From { get; }

        /// <summary>
        /// The replacement prefix for a simple rename, or null for a split alias (whose
        /// replacement depends on which drawgroup range a file falls in - see
        /// <see cref="RemeshAliasSplit.To"/>).
        /// </summary>
        public string To { get; }

        /// <summary>
        /// The drawgroup ranges this alias redirects. Empty for a simple rename, in which case
        /// <see cref="IsSplit"/> is false.
        /// </summary>
        public IReadOnlyList<RemeshAliasSplit> Splits { get; }

        /// <summary>True when this is a drawgroup-ranged ("split") alias rather than a plain rename.</summary>
        public bool IsSplit
        {
            get { return Splits.Count > 0; }
        }

        public RemeshAlias(string from, string to, IReadOnlyList<RemeshAliasSplit> splits)
        {
            From = from;
            To = to;
            Splits = splits ?? new ReadOnlyCollection<RemeshAliasSplit>(new List<RemeshAliasSplit>());
        }
    }

    /// <summary>
    /// The parsed <c>alias.simitone</c> array of a remesh pack, in pack order. Immutable.
    /// </summary>
    public sealed class RemeshAliasSet
    {
        private static readonly ReadOnlyCollection<RemeshAlias> NoAliases =
            new ReadOnlyCollection<RemeshAlias>(new List<RemeshAlias>());

        /// <summary>The aliases, in the order they appeared in the metadata.</summary>
        public IReadOnlyList<RemeshAlias> Aliases { get; }

        public RemeshAliasSet(IList<RemeshAlias> aliases)
        {
            Aliases = aliases == null || aliases.Count == 0
                ? NoAliases
                : new ReadOnlyCollection<RemeshAlias>(new List<RemeshAlias>(aliases));
        }
    }

    /// <summary>
    /// One file the pack contributes: where it is now, the flat name it must have under
    /// <c>Content/MeshReplace/</c> for the runtime loader to find it, and the priority of the group
    /// it came from (used to settle two groups claiming the same destination).
    /// </summary>
    public sealed class RemeshPackEntry
    {
        /// <summary>Absolute path of the source file inside the pack.</summary>
        public string SourcePath { get; }

        /// <summary>File name to write into the MeshReplace folder, always lowercased.</summary>
        public string DestinationName { get; }

        /// <summary>The <c>priority</c> of the owning group's metadata (0 when unset).</summary>
        public int Priority { get; }

        public RemeshPackEntry(string sourcePath, string destinationName, int priority)
        {
            SourcePath = sourcePath;
            DestinationName = destinationName;
            Priority = priority;
        }

        public override string ToString()
        {
            return DestinationName + " <- " + SourcePath;
        }
    }

    /// <summary>
    /// A destination name two or more pack files claimed, and which one survived. Recorded rather
    /// than silently swallowed: a collision means a contributor is shadowed, which is a pack bug
    /// worth surfacing.
    /// </summary>
    public sealed class RemeshPackConflict
    {
        /// <summary>The contested destination file name.</summary>
        public string DestinationName { get; }

        /// <summary>The source file that won.</summary>
        public string KeptSourcePath { get; }

        /// <summary>Priority of the winning file's group.</summary>
        public int KeptPriority { get; }

        /// <summary>The source file that lost.</summary>
        public string DiscardedSourcePath { get; }

        /// <summary>Priority of the losing file's group.</summary>
        public int DiscardedPriority { get; }

        public RemeshPackConflict(string destinationName, string keptSourcePath, int keptPriority,
            string discardedSourcePath, int discardedPriority)
        {
            DestinationName = destinationName;
            KeptSourcePath = keptSourcePath;
            KeptPriority = keptPriority;
            DiscardedSourcePath = discardedSourcePath;
            DiscardedPriority = discardedPriority;
        }
    }

    /// <summary>
    /// The flattened packaging plan: what to copy, and what got shadowed on the way.
    /// </summary>
    public sealed class RemeshPackPlan
    {
        private static readonly ReadOnlyCollection<RemeshPackEntry> NoEntries =
            new ReadOnlyCollection<RemeshPackEntry>(new List<RemeshPackEntry>());

        private static readonly ReadOnlyCollection<RemeshPackConflict> NoConflicts =
            new ReadOnlyCollection<RemeshPackConflict>(new List<RemeshPackConflict>());

        private readonly List<RemeshPackEntry> _entries = new List<RemeshPackEntry>();
        private readonly List<RemeshPackConflict> _conflicts = new List<RemeshPackConflict>();

        /// <summary>
        /// The surviving files, one per destination name, ordered by destination name so the
        /// packaging step is byte-for-byte reproducible.
        /// </summary>
        public IReadOnlyList<RemeshPackEntry> Entries
        {
            get { return _entries.Count == 0 ? NoEntries : new ReadOnlyCollection<RemeshPackEntry>(_entries); }
        }

        /// <summary>Every shadowed file, in the order the collision was found.</summary>
        public IReadOnlyList<RemeshPackConflict> Conflicts
        {
            get { return _conflicts.Count == 0 ? NoConflicts : new ReadOnlyCollection<RemeshPackConflict>(_conflicts); }
        }

        /// <summary>How many files were shadowed, i.e. <c>Conflicts.Count</c>.</summary>
        public int ConflictCount
        {
            get { return _conflicts.Count; }
        }

        internal void Add(RemeshPackEntry entry)
        {
            _entries.Add(entry);
        }

        internal void ReplaceEntries(List<RemeshPackEntry> entries)
        {
            _entries.Clear();
            _entries.AddRange(entries);
        }

        internal void AddConflict(RemeshPackConflict conflict)
        {
            _conflicts.Add(conflict);
        }
    }

    /// <summary>
    /// Turns the community remesh pack's <c>Author/Group/</c> tree into the flat
    /// <c>Content/MeshReplace/</c> file list the runtime loader actually looks at.
    ///
    /// <para>The loader (<c>FSO.Content.RCMeshProvider</c>) builds the name
    /// <c>&lt;iff filename with dots replaced by underscores, lowercased&gt;_&lt;drawgroup id&gt;.fsom</c>
    /// and probes for that one flat file name, and asks for textures as
    /// <c>&lt;same prefix&gt;_TEX_&lt;texture id&gt;.png</c> in the same folder. Nothing about the
    /// author/group nesting survives that, so the pack has to be flattened - and flattened with the
    /// names Simitone actually uses, not the names FreeSO uses, which is what the pack's
    /// <c>alias.simitone</c> block is for.</para>
    ///
    /// <para>Two group-level switches gate the walk: <c>"game": "freeso"</c> (FreeSO-only meshes)
    /// and <c>"game": "none"</c> (a placeholder directory) are skipped, and <c>"priority"</c> breaks
    /// ties when two groups produce the same destination name.</para>
    ///
    /// <para>This is pure logic plus filesystem walking so it can be regression-tested; the tool
    /// (<c>Simitone.PackageRemeshes</c>) does the copying.</para>
    /// </summary>
    public static class RemeshPackage
    {
        /// <summary>Name of the pack-level and group-level metadata file.</summary>
        public const string MetadataFileName = "metadata.json";

        /// <summary>The key under the root metadata's <c>alias</c> object holding our rename list.</summary>
        public const string AliasKey = "simitone";

        /// <summary>Directory name (any casing) holding authoring files, which is never packaged.</summary>
        public const string SourceDirectoryName = "source";

        /// <summary>Prefix of an alternates directory, which is never packaged.</summary>
        public const string AlternateDirectoryPrefix = "alt_";

        /// <summary>
        /// Parse the root <c>metadata.json</c> of a remesh pack and return its Simitone rename rules.
        /// </summary>
        /// <remarks>
        /// Only the <c>alias.simitone</c> array is read; everything else in the file is packaging
        /// metadata that Simitone has no use for. Anything malformed - invalid JSON, a missing
        /// <c>alias</c> object, an entry with no usable <c>from</c> - is skipped rather than thrown,
        /// so a bad entry in one corner of a community-edited pack cannot cost us the whole pack.
        /// A null/blank argument yields an empty set.
        /// </remarks>
        public static RemeshAliasSet ParseAliases(string rootMetadataJson)
        {
            var aliases = new List<RemeshAlias>();
            if (!TryParseRoot(rootMetadataJson, out var root)) return new RemeshAliasSet(aliases);

            if (!TryGetProperty(root, "alias", out var aliasObject) || aliasObject.ValueKind != JsonValueKind.Object)
            {
                return new RemeshAliasSet(aliases);
            }
            if (!TryGetProperty(aliasObject, AliasKey, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return new RemeshAliasSet(aliases);
            }

            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var from = GetString(entry, "from");
                if (string.IsNullOrEmpty(from)) continue;

                var splits = new List<RemeshAliasSplit>();
                if (TryGetProperty(entry, "split", out var splitArray) && splitArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var split in splitArray.EnumerateArray())
                    {
                        if (split.ValueKind != JsonValueKind.Object) continue;
                        var to = GetString(split, "to");
                        if (string.IsNullOrEmpty(to)) continue;
                        if (!TryGetInt32(split, "dgrpFrom", out int dgrpFrom)) continue;
                        if (!TryGetInt32(split, "range", out int range) || range <= 0) continue;
                        if (!TryGetInt32(split, "dgrpTo", out int dgrpTo)) continue;
                        splits.Add(new RemeshAliasSplit(GetString(split, "name"), to, dgrpFrom, range, dgrpTo));
                    }
                }

                // A "split" with no usable sub-entry is meaningless; treat the entry as a plain
                // rename if it also carries a "to", otherwise drop it entirely.
                if (splits.Count > 0)
                {
                    aliases.Add(new RemeshAlias(from, GetString(entry, "to"), splits));
                }
                else
                {
                    var to = GetString(entry, "to");
                    if (string.IsNullOrEmpty(to)) continue;
                    aliases.Add(new RemeshAlias(from, to, null));
                }
            }

            return new RemeshAliasSet(aliases);
        }

        /// <summary>
        /// Map one pack file name to the name it must be published under, applying the first
        /// matching alias. The result is always lowercased, matching what the runtime loader
        /// probes for.
        /// </summary>
        /// <remarks>
        /// <para>A simple alias only rewrites the <c>from</c> prefix, so it covers both mesh and
        /// texture files.</para>
        ///
        /// <para>A split alias only rewrites <b>mesh</b> files, i.e. ones whose part after the prefix
        /// is a bare drawgroup number. It deliberately does not touch <c>_TEX_&lt;id&gt;.png</c>
        /// names: in this pack the number after <c>_TEX_</c> is a texture slot within the group
        /// (<c>food_iff_TEX_6.png</c> through <c>_TEX_15.png</c> serve drawgroups 119..315), not a
        /// drawgroup id, so testing it against a drawgroup range and rewriting it would silently
        /// move a texture onto an unrelated mesh. A texture therefore keeps its name under a split
        /// alias. A simple alias still renames its prefix, which is all that is needed there.</para>
        ///
        /// <para>A file that matches no alias keeps its name (lowercased).</para>
        /// </remarks>
        public static string MapFileName(string fileName, RemeshAliasSet aliases)
        {
            var name = fileName == null ? string.Empty : Path.GetFileName(fileName);
            if (name.Length == 0) return string.Empty;

            if (aliases != null)
            {
                foreach (var alias in aliases.Aliases)
                {
                    if (!StartsWithPrefix(name, alias.From)) continue;

                    if (!alias.IsSplit)
                    {
                        if (string.IsNullOrEmpty(alias.To)) continue;
                        return (alias.To + name.Substring(alias.From.Length)).ToLowerInvariant();
                    }

                    var remapped = TryMapSplit(name, alias);
                    if (remapped != null) return remapped;
                    // In range nowhere: this alias does not apply to this file, so fall through and
                    // leave the name alone rather than guessing.
                }
            }

            return name.ToLowerInvariant();
        }

        /// <summary>
        /// Whether a group's <c>metadata.json</c> allows its files to be packaged for Simitone.
        /// </summary>
        /// <remarks>
        /// <c>"game": "freeso"</c> marks a group whose meshes are replacements for FreeSO-only
        /// objects, and <c>"game": "none"</c> marks a directory kept for organising work in
        /// progress; both are excluded. A missing or unrecognised <c>game</c> is included, as is
        /// unparseable or absent metadata - defaulting to "ship it" is the safe direction for
        /// hand-authored content.
        /// </remarks>
        public static bool IsGroupIncluded(string groupMetadataJson)
        {
            if (!TryParseObject(groupMetadataJson, out var root)) return true;
            var game = GetString(root, "game");
            if (string.IsNullOrEmpty(game)) return true;

            var normalized = game.Trim().ToLowerInvariant();
            return normalized != "freeso" && normalized != "none";
        }

        /// <summary>
        /// The group's <c>priority</c>, defaulting to 0 when absent or unreadable. Higher wins a
        /// collision on a destination name.
        /// </summary>
        public static int GroupPriority(string groupMetadataJson)
        {
            if (!TryParseObject(groupMetadataJson, out var root)) return 0;
            if (TryGetInt32(root, "priority", out int priority)) return priority;
            return 0;
        }

        /// <summary>
        /// Walk a remesh pack and return the files to copy, one entry per destination name.
        /// </summary>
        /// <remarks>
        /// Convenience wrapper over <see cref="BuildPackPlan"/> for callers that do not care about
        /// the shadowed files. The result is ordered by destination name.
        /// </remarks>
        public static List<RemeshPackEntry> BuildPlan(string remeshRoot)
        {
            return new List<RemeshPackEntry>(BuildPackPlan(remeshRoot).Entries);
        }

        /// <summary>
        /// Walk a remesh pack and return the full plan: the files to copy plus every collision.
        /// </summary>
        /// <remarks>
        /// <para>The walk is <c>&lt;root&gt;/&lt;author&gt;/&lt;group&gt;/&lt;file&gt;</c>, restricted to
        /// the two extensions the loader understands (".fsom" meshes and ".png" textures), skipping
        /// any <c>source</c> or <c>alt_*</c> directory, and skipping groups whose metadata excludes
        /// them. Author directories hold no packable files themselves, so they are only a level to
        /// descend through.</para>
        ///
        /// <para>Authors, groups and files are all visited in sorted order, so two runs over the
        /// same pack produce the same plan. On a collision the higher <c>priority</c> wins; a tie
        /// keeps the first one the walk reached and records the other as
        /// <see cref="RemeshPackPlan.Conflicts"/>.</para>
        ///
        /// <para>A missing root yields an empty plan rather than an exception: the pack is optional
        /// content and may simply not be checked out.</para>
        /// </remarks>
        public static RemeshPackPlan BuildPackPlan(string remeshRoot)
        {
            var plan = new RemeshPackPlan();
            if (string.IsNullOrWhiteSpace(remeshRoot) || !Directory.Exists(remeshRoot)) return plan;

            var rootMetadata = Path.Combine(remeshRoot, MetadataFileName);
            var aliases = File.Exists(rootMetadata)
                ? ParseAliases(File.ReadAllText(rootMetadata))
                : new RemeshAliasSet(null);

            var candidates = new List<RemeshPackEntry>();
            foreach (var authorDir in SortedSubdirectories(remeshRoot))
            {
                foreach (var groupDir in SortedSubdirectories(authorDir))
                {
                    if (IsSkippedDirectory(Path.GetFileName(groupDir))) continue;

                    var groupMetadataPath = Path.Combine(groupDir, MetadataFileName);
                    string groupMetadata = null;
                    if (File.Exists(groupMetadataPath)) groupMetadata = File.ReadAllText(groupMetadataPath);
                    if (!IsGroupIncluded(groupMetadata)) continue;

                    var priority = GroupPriority(groupMetadata);
                    foreach (var file in SortedFiles(groupDir))
                    {
                        if (!IsPackableExtension(Path.GetExtension(file))) continue;
                        var destination = MapFileName(Path.GetFileName(file), aliases);
                        if (destination.Length == 0) continue;
                        candidates.Add(new RemeshPackEntry(file, destination, priority));
                    }
                }
            }

            var chosen = new Dictionary<string, RemeshPackEntry>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                RemeshPackEntry existing;
                if (!chosen.TryGetValue(candidate.DestinationName, out existing))
                {
                    chosen.Add(candidate.DestinationName, candidate);
                    continue;
                }

                if (candidate.Priority > existing.Priority)
                {
                    chosen[candidate.DestinationName] = candidate;
                    plan.AddConflict(new RemeshPackConflict(candidate.DestinationName,
                        candidate.SourcePath, candidate.Priority, existing.SourcePath, existing.Priority));
                }
                else
                {
                    plan.AddConflict(new RemeshPackConflict(candidate.DestinationName,
                        existing.SourcePath, existing.Priority, candidate.SourcePath, candidate.Priority));
                }
            }

            foreach (var entry in chosen.Values)
            {
                plan.Add(entry);
            }
            SortByDestination(plan);

            return plan;
        }

        /// <summary>Whether a directory under an author or group is packaging-irrelevant.</summary>
        public static bool IsSkippedDirectory(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName)) return false;
            return string.Equals(directoryName, SourceDirectoryName, StringComparison.OrdinalIgnoreCase)
                || directoryName.StartsWith(AlternateDirectoryPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether an extension is one the runtime loader can consume.</summary>
        public static bool IsPackableExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;
            return string.Equals(extension, ".fsom", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Apply a split alias to a file name, or return null when the alias does not cover it.
        /// </summary>
        private static string TryMapSplit(string name, RemeshAlias alias)
        {
            // Everything after "<from>_". The caller has already checked that prefix matches.
            var rest = name.Substring(alias.From.Length + 1);

            var extensionIndex = rest.LastIndexOf('.');
            var stem = extensionIndex < 0 ? rest : rest.Substring(0, extensionIndex);
            var extension = extensionIndex < 0 ? string.Empty : rest.Substring(extensionIndex);

            int drawGroup;
            if (!int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out drawGroup))
            {
                // A "_TEX_<id>" texture, or anything else that is not a bare drawgroup id: the
                // number is not a drawgroup, so a drawgroup-ranged alias cannot apply to it.
                return null;
            }

            foreach (var split in alias.Splits)
            {
                if (!split.Covers(drawGroup)) continue;
                return (split.To + "_" + split.Remap(drawGroup).ToString(CultureInfo.InvariantCulture) + extension)
                    .ToLowerInvariant();
            }

            return null;
        }

        private static bool StartsWithPrefix(string name, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            if (name.Length < prefix.Length + 1) return false;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return name[prefix.Length] == '_';
        }

        private static List<string> SortedSubdirectories(string directory)
        {
            var result = new List<string>();
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                return result;
            }

            Array.Sort(directories, PathOrder);
            result.AddRange(directories);
            return result;
        }

        private static List<string> SortedFiles(string directory)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (IOException)
            {
                return new List<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<string>();
            }

            Array.Sort(files, PathOrder);
            return new List<string>(files);
        }

        /// <summary>
        /// A total order over path names: case-insensitive first (so "Chaul" and "chaul" are
        /// adjacent and the walk reads naturally) with a case-sensitive tiebreak so two names that
        /// differ only in case still have a defined order rather than an arbitrary one.
        /// </summary>
        private static int PathOrder(string a, string b)
        {
            int result = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return result != 0 ? result : string.CompareOrdinal(a, b);
        }

        private static void SortByDestination(RemeshPackPlan plan)
        {
            var entries = new List<RemeshPackEntry>();
            foreach (var entry in plan.Entries) entries.Add(entry);
            entries.Sort((a, b) => string.CompareOrdinal(a.DestinationName, b.DestinationName));
            plan.ReplaceEntries(entries);
        }

        private static bool TryParseObject(string json, out JsonElement root)
        {
            root = default(JsonElement);
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using (var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
                    root = document.RootElement.Clone();
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryParseRoot(string json, out JsonElement root)
        {
            return TryParseObject(json, out root);
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            value = default(JsonElement);
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind == JsonValueKind.Null
                    || property.Value.ValueKind == JsonValueKind.Undefined) return false;
                value = property.Value;
                return true;
            }
            return false;
        }

        private static string GetString(JsonElement element, string name)
        {
            JsonElement value;
            if (!TryGetProperty(element, name, out value)) return null;
            if (value.ValueKind != JsonValueKind.String) return null;
            return value.GetString();
        }

        private static bool TryGetInt32(JsonElement element, string name, out int result)
        {
            result = 0;
            JsonElement value;
            if (!TryGetProperty(element, name, out value)) return false;
            if (value.ValueKind != JsonValueKind.Number) return false;
            return value.TryGetInt32(out result);
        }
    }
}
