/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// One neighbourhood slot: the id the game uses, the <c>UserData*</c> folder that backs it,
    /// the install-side folder it can be seeded from, and the label the management page shows
    /// before the player has renamed anything.
    /// </summary>
    /// <remarks>
    /// This is a description, not a handle. Building the nine of them is pure path/name
    /// arithmetic, so <see cref="NeighbourhoodManager.AllSlots"/> can be handed straight to a
    /// list control and asserted on in a test without touching a disk.
    /// </remarks>
    public sealed class NeighbourhoodSlot
    {
        /// <summary>
        /// Slot id, 0..8. This is the value the neighbourhood provider is switched with
        /// (<c>TS1NeighborhoodProvider.InitSpecific(id)</c>) and the value every helper in
        /// <see cref="NeighbourhoodManager"/> takes.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// The neighbourhood's folder name, both in a The Sims install and in Simitone's own user
        /// directory: <c>UserData</c> for the primary, then <c>UserData2</c> .. <c>UserData8</c>.
        /// </summary>
        public string FolderName { get; }

        /// <summary>
        /// The install-side folder this slot is seeded from, e.g. <c>TemplateDowntown</c> for
        /// slot 1. Slots 6..8 have no dedicated area template and report
        /// <see cref="NeighbourhoodManager.BaseTemplateFolderName"/>, because the base residential
        /// template is the only neighbourhood-shaped folder the game ships for them.
        /// </summary>
        public string TemplateFolderName { get; }

        /// <summary>
        /// The label shown when the player has not renamed the slot: "Neighbourhood 1" ..
        /// "Neighbourhood 9", numbered the way The Sims numbered them (the primary is
        /// <c>UserData</c>, i.e. neighbourhood 1).
        /// </summary>
        public string DefaultName { get; }

        /// <summary>
        /// The game's own name for the area this slot is themed around ("Downtown",
        /// "Magictown", ...), or <c>null</c> when the slot has no dedicated area template. Purely
        /// a label: nothing in Simitone branches on it.
        /// </summary>
        public string AreaName { get; }

        /// <summary>
        /// True for the primary slot (<c>UserData</c>), the one the content manager opens during
        /// startup. It is the slot that can never be deleted or overwritten.
        /// </summary>
        public bool IsPrimary
        {
            get { return Id == NeighbourhoodManager.PrimarySlotId; }
        }

        public NeighbourhoodSlot(int id, string folderName, string templateFolderName, string defaultName, string areaName)
        {
            Id = id;
            FolderName = folderName;
            TemplateFolderName = templateFolderName;
            DefaultName = defaultName;
            AreaName = areaName;
        }

        public override string ToString()
        {
            return FolderName;
        }
    }

    /// <summary>
    /// The backend of the Neighbourhood Management page: what the nine neighbourhood slots are,
    /// how to copy one in from a The Sims install, how to make a new empty one, how to delete one
    /// safely, and where a player's custom name for a slot is kept.
    ///
    /// <para>
    /// <b>The slot convention.</b> Simitone does not invent a folder layout of its own. The
    /// neighbourhood provider resolves slot <c>id</c> as
    /// <c>"UserData" + (id == 0 ? "" : id + 1)</c> inside <c>FSOEnvironment.UserDir</c>
    /// (<c>TS1NeighborhoodProvider.InitSpecific</c>), which yields <c>UserData</c> for the primary
    /// and <c>UserData2</c> .. <c>UserData8</c> for the eight numbered siblings. The same
    /// convention is used in a The Sims install, so a slot id is directly a folder name and no
    /// translation table is needed anywhere: <see cref="SlotFolderName"/> is the single place that
    /// spells it out.
    /// </para>
    ///
    /// <para>
    /// <b>Where a seed comes from.</b> A The Sims install ships the playable neighbourhoods
    /// (<c>UserData</c> .. <c>UserData8</c>) and, for each expansion area, a lot-only
    /// <c>Template*</c> folder. Only <c>TemplateUserData</c> carries a
    /// <see cref="TS1UserData.NeighborhoodResourceName"/>; the expansion templates
    /// (<c>TemplateDowntown</c>, <c>TemplateMagictown</c>, <c>TemplateStudiotown</c>,
    /// <c>TemplateVacation</c>, <c>TemplateCommunity</c>) are house/desc overlays that the game
    /// layers onto a base neighbourhood and cannot be opened on their own. That is why
    /// <see cref="ResolveSource"/> probes in order and <em>requires</em>
    /// <c>Neighborhood.iff</c> at every rung, rather than taking the first folder that merely
    /// exists: a slot seeded from a bare <c>TemplateDowntown</c> would open as a broken
    /// neighbourhood on the very next line of <c>InitSpecific</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing here overwrites.</b> <see cref="CopyFromInstall"/> and
    /// <see cref="CreateBlank"/> both refuse to write into a folder that already exists, so no
    /// sequence of calls from the UI can destroy a neighbourhood the player has. Re-seeding a
    /// slot means deleting it first (or leaving the existing one alone).
    /// </para>
    ///
    /// <para>
    /// <b>Custom names live outside the slots.</b> A The Sims neighbourhood folder is the game's
    /// own data and has no room for our metadata - and
    /// <c>TS1NeighborhoodProvider.LoadCharacters</c> aborts on the first file in
    /// <c>Characters/</c> whose extension is not <c>.iff</c>, so a stray marker file in there is
    /// actively harmful. Names therefore go in a single sidecar,
    /// <c>&lt;userDir&gt;Neighbourhoods/names.json</c>, keyed by slot id, written through
    /// <see cref="AtomicFileCommit"/> so a crash mid-write cannot leave the previous names
    /// unreadable. Deleting a slot drops its name; the sidecar is otherwise independent of the
    /// folders, so it survives a slot being deleted and recreated.
    /// </para>
    ///
    /// <para>
    /// Every member that touches the disk catches and logs through <see cref="SimitoneLog"/>
    /// rather than throwing: this is called from UI button handlers, and a missing folder or a
    /// locked file must surface as "the operation did not happen", not as an unhandled
    /// exception on the game thread.
    /// </para>
    /// </summary>
    public static class NeighbourhoodManager
    {
        /// <summary>Lowest valid slot id. The primary neighbourhood is 0, as in the provider.</summary>
        public const int MinSlotId = 0;

        /// <summary>Highest valid slot id. The Sims supports nine neighbourhoods, 1..9.</summary>
        public const int MaxSlotId = 8;

        /// <summary>Number of neighbourhood slots: ids <see cref="MinSlotId"/>..<see cref="MaxSlotId"/>.</summary>
        public const int SlotCount = MaxSlotId - MinSlotId + 1;

        /// <summary>
        /// The primary slot - <c>UserData</c>, The Sims' neighbourhood 1. It is the one the
        /// content manager opens during startup, so it is never deletable and never overwritable.
        /// </summary>
        public const int PrimarySlotId = 0;

        /// <summary>
        /// The residential neighbourhood's folder name, in a The Sims install and in Simitone's
        /// user directory alike. Re-exported from <see cref="TS1UserData"/> so the two cannot
        /// drift apart.
        /// </summary>
        public const string PrimaryFolderName = TS1UserData.NeighborhoodFolderName;

        /// <summary>
        /// The one install-side folder that is a complete neighbourhood: the base residential
        /// template every other seed ultimately falls back to, and the source
        /// <see cref="CreateBlank(string, int, string)"/> uses for a new slot.
        /// </summary>
        public const string BaseTemplateFolderName = "TemplateUserData";

        /// <summary>Folder under the user's Simitone directory that holds <see cref="NamesFileName"/>.</summary>
        public const string NamesFolderName = "Neighbourhoods";

        /// <summary>Filename of the custom-name sidecar.</summary>
        public const string NamesFileName = "names.json";

        /// <summary>
        /// Version stamped into the sidecar. A file whose version this build does not recognise
        /// is ignored (and reported) rather than half-read, so a sidecar written by a future
        /// Simitone cannot be silently reinterpreted.
        /// </summary>
        public const int NamesFileVersion = 1;

        /// <summary>
        /// Longest accepted custom name, in characters. Long enough for
        /// "Springfield - The Simpons Family", short enough that the management page's slot
        /// cards stay readable without scrolling.
        /// </summary>
        public const int MaxNameLength = 48;

        /// <summary>
        /// Prefix of the default (un-renamed) label for slot <c>id</c>: "Neighbourhood 1" is the
        /// primary, matching The Sims' own 1-based numbering.
        /// </summary>
        public const string DefaultNamePrefix = "Neighbourhood ";

        /// <summary>
        /// Characters a name may not contain, on top of the two path separators and the control
        /// characters.
        /// </summary>
        /// <remarks>
        /// Spelled out rather than taken from <see cref="Path.GetInvalidFileNameChars"/>, which is
        /// not the list it looks like: that one omits <c>:</c>, <c>*</c> and <c>?</c>, and on a
        /// non-Windows host it is almost empty. A name is only ever stored as JSON, so these are
        /// not strictly load-bearing, but "can this string be mistaken for a path" is a far easier
        /// question to answer when the answer is the same on every host.
        ///
        /// <para>
        /// <b>Public, and the single copy of the rule.</b> The management page's rename dialog
        /// names these characters in its message, and it used to keep its own hand-written
        /// <c>const string</c> of them: a character added here and not there produced a dialog
        /// naming a character the button did not care about, and nothing noticed. One definition,
        /// published, is what stops that.
        /// </para>
        /// </remarks>
        public static readonly char[] ReservedNameChars = { ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>
        /// <see cref="ReservedNameChars"/> as one string, for a message that has to name them.
        /// </summary>
        /// <remarks>
        /// Derived rather than declared, so there is no second list to forget. The order is the
        /// array's own and is not part of any contract.
        /// </remarks>
        public static string ReservedNameCharsText => new string(ReservedNameChars);

        /// <summary>
        /// The per-area template folder for each slot id, or <c>null</c> where The Sims ships no
        /// dedicated one. The order is the install's own expansion order (Downtown, Magictown,
        /// Studiowtown, Vacation, Community), which is the order the neighbourhoods were added to
        /// the game and therefore the order the numbered <c>UserData</c> folders follow.
        /// </summary>
        private static readonly string[] AreaTemplateNames =
        {
            BaseTemplateFolderName,   // 0 - residential
            "TemplateDowntown",       // 1
            "TemplateMagictown",      // 2
            "TemplateStudiotown",     // 3
            "TemplateVacation",       // 4
            "TemplateCommunity",      // 5
            null,                     // 6 - no dedicated area template
            null,                     // 7
            null                      // 8
        };

        /// <summary>
        /// The game's name for each area, for display only. Same order as
        /// <see cref="AreaTemplateNames"/>; the trailing entries are <c>null</c> because the game
        /// has no name for them.
        /// </summary>
        private static readonly string[] AreaNames =
        {
            "Residential", "Downtown", "Magictown", "Studiottown", "Vacation", "Community",
            null, null, null
        };

        private static readonly ReadOnlyCollection<NeighbourhoodSlot> SlotTable = BuildSlots();

        /// <summary>
        /// The nine slots in id order, for a management page to bind to. Immutable.
        /// </summary>
        public static IReadOnlyList<NeighbourhoodSlot> AllSlots
        {
            get { return SlotTable; }
        }

        /// <summary>
        /// Install directory <see cref="CreateBlank(string, int)"/> copies the base template
        /// from when the caller does not pass one. Set it once at startup - the same value the
        /// content manager resolved - so the management page's "New neighbourhood" button needs
        /// no install path of its own. <see cref="CopyFromInstall(string, string, int)"/> always
        /// takes an explicit one.
        /// </summary>
        /// <remarks>
        /// A <c>null</c> default is not an error - it just means "no default source", and
        /// <see cref="CreateBlank(string, int)"/> reports failure rather than guessing a drive.
        /// Tests set and restore it so they do not depend on ambient state.
        /// </remarks>
        public static string DefaultInstallPath { get; set; }

        // ---- pure slot model -----------------------------------------------------------------

        /// <summary>
        /// Is <paramref name="id"/> one of the nine neighbourhood slots?
        /// </summary>
        /// <remarks>
        /// The gate every other helper applies first, because a bad id is the one mistake that
        /// can turn a folder name into an arbitrary path.
        /// </remarks>
        public static bool IsKnownSlot(int id)
        {
            return id >= MinSlotId && id <= MaxSlotId;
        }

        /// <summary>
        /// The folder a slot lives in: <c>UserData</c> for the primary, <c>UserData2</c> ..
        /// <c>UserData8</c> otherwise.
        /// </summary>
        /// <remarks>
        /// This is the exact expression <c>TS1NeighborhoodProvider.InitSpecific</c> uses to build
        /// <c>UserPath</c>. If that ever changes, it has to change here too, which is why there
        /// is no second copy of the rule anywhere in Simitone.
        /// </remarks>
        /// <returns>The folder name, or <c>null</c> for an unknown slot.</returns>
        public static string SlotFolderName(int id)
        {
            if (!IsKnownSlot(id)) return null;
            if (id == PrimarySlotId) return PrimaryFolderName;
            return PrimaryFolderName + (id + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The install-side folder a slot is seeded from - the area template for that slot, e.g.
        /// <c>TemplateDowntown</c> for slot 1.
        /// </summary>
        /// <remarks>
        /// Slots 6..8 have no dedicated area template, so they report
        /// <see cref="BaseTemplateFolderName"/>: the base residential template is the only
        /// neighbourhood-shaped folder the game ships for them, and
        /// <see cref="CreateBlank(string, int, string)"/> uses it for every new slot anyway.
        /// Note that only the base template actually contains a
        /// <see cref="TS1UserData.NeighborhoodResourceName"/>; the expansion templates are lot
        /// overlays, so <see cref="ResolveSource"/> treats this as a preference order and
        /// validates each candidate.
        /// </remarks>
        /// <returns>The template folder name, or <c>null</c> for an unknown slot.</returns>
        public static string TemplateFolderName(int id)
        {
            if (!IsKnownSlot(id)) return null;
            var area = AreaTemplateNames[id - MinSlotId];
            return area ?? BaseTemplateFolderName;
        }

        /// <summary>The game's name for a slot's area, for display. <c>null</c> when it has none.</summary>
        public static string AreaNameFor(int id)
        {
            if (!IsKnownSlot(id)) return null;
            return AreaNames[id - MinSlotId];
        }

        /// <summary>
        /// The label a slot shows when the player has not renamed it: "Neighbourhood 1" for the
        /// primary, up to "Neighbourhood 9".
        /// </summary>
        /// <returns>The default name, or <c>null</c> for an unknown slot.</returns>
        public static string DefaultSlotName(int id)
        {
            if (!IsKnownSlot(id)) return null;
            return DefaultNamePrefix + (id + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The label a slot should actually be displayed with: the player's custom name when
        /// there is one, otherwise <see cref="DefaultSlotName"/>.
        /// </summary>
        /// <remarks>
        /// A stored name is passed through <see cref="SanitizeName"/> again here rather than
        /// trusted, so a hand-edited sidecar cannot put a separator or an over-long string into
        /// a list control. <c>GetName</c> has already done this once; the cost is a string scan
        /// and it keeps a single definition of "displayable" that both the read path and the
        /// UI agree on.
        /// </remarks>
        /// <param name="id">Slot id.</param>
        /// <param name="customName">The stored name, or <c>null</c>/blank when the slot is un-renamed.</param>
        public static string SlotName(int id, string customName)
        {
            var clean = SanitizeName(customName);
            return clean ?? DefaultSlotName(id);
        }

        // ---- names ---------------------------------------------------------------------------

        /// <summary>
        /// Can <paramref name="name"/> be stored as a slot name?
        /// </summary>
        /// <remarks>
        /// The strict gate <see cref="Rename"/> applies. It is deliberately structural rather
        /// than cosmetic, so a UI can run it against a text box on every keystroke to decide
        /// whether to enable its OK button, and <see cref="SanitizeName"/> is the separate
        /// "coerce whatever was typed into something storable" operation.
        ///
        /// <para>Rejected: null; nothing but whitespace; longer than
        /// <see cref="MaxNameLength"/> once trimmed; anything containing a path separator
        /// (<c>/</c> or <c>\</c>); any character Windows forbids in a file name (see
        /// <see cref="ReservedNameChars"/>); a control character; and the relative-path
        /// spellings <c>.</c> and <c>..</c>.</para>
        /// </summary>
        public static bool IsValidName(string name)
        {
            if (name == null) return false;

            var trimmed = name.Trim();
            if (trimmed.Length == 0) return false;
            if (trimmed.Length > MaxNameLength) return false;
            if (IsReservedName(trimmed)) return false;

            for (int i = 0; i < name.Length; i++)
            {
                if (!IsNameChar(name[i])) return false;
            }

            return true;
        }

        /// <summary>
        /// The two names that are not names. <c>.</c> and <c>..</c> are what a path builder
        /// turns into "this folder" and "the parent folder" - exactly the kind of thing that must
        /// never reach the filesystem from a text box, and the one thing
        /// <see cref="SanitizeName"/> cannot fix character by character without destroying
        /// ".hidden" and "Springfield.KS".
        /// </summary>
        private static bool IsReservedName(string trimmed)
        {
            return trimmed == "." || trimmed == "..";
        }

        /// <summary>May this character appear in a stored slot name? Everything else is folded
        /// into a space by <see cref="SanitizeName"/> or rejected by <see cref="IsValidName"/>.</summary>
        private static bool IsNameChar(char c)
        {
            if (c == '/' || c == '\\') return false;
            if (char.IsControl(c)) return false;
            return Array.IndexOf(ReservedNameChars, c) < 0;
        }

        /// <summary>
        /// Coerce arbitrary text into a storable slot name: outer whitespace removed, runs of
        /// whitespace collapsed to a single space, every character a name may not contain
        /// (separators, control characters, characters Windows forbids) replaced with a space,
        /// and the result truncated to <see cref="MaxNameLength"/>.
        /// </summary>
        /// <remarks>
        /// Idempotent, and whenever it returns a non-null value that value satisfies
        /// <see cref="IsValidName"/> - both are asserted in the tests, because the UI's
        /// "did the player type something usable" path depends on the pair agreeing.
        /// Returns <c>null</c> when nothing usable is left, which is how a name made entirely of
        /// separators is expressed.
        /// </remarks>
        public static string SanitizeName(string raw)
        {
            if (raw == null) return null;

            var builder = new System.Text.StringBuilder(raw.Length);
            var pendingSpace = false;
            for (int i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                var usable = !char.IsWhiteSpace(c) && IsNameChar(c);

                if (!usable)
                {
                    //remember that a replacement is owed, but do not emit it yet: leading and
                    //trailing runs collapse away entirely, and an interior run becomes one space.
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    if (builder.Length < MaxNameLength) builder.Append(' ');
                    pendingSpace = false;
                }

                if (builder.Length < MaxNameLength) builder.Append(c);
            }

            if (builder.Length == 0) return null;

            var clean = builder.ToString();
            //".", ".." and the empty result all mean "the player typed nothing usable"
            return IsReservedName(clean) ? null : clean;
        }

        // ---- paths ---------------------------------------------------------------------------

        /// <summary>
        /// The full folder a slot lives in, or <c>null</c> for an unknown slot or a missing user
        /// directory. <paramref name="userDir"/> may be a relative path or carry a trailing
        /// separator, which is how <c>FSOEnvironment.UserDir</c> arrives.
        /// </summary>
        public static string SlotPath(string userDir, int id)
        {
            var folder = SlotFolderName(id);
            if (folder == null || string.IsNullOrEmpty(userDir)) return null;
            return Path.Combine(userDir, folder);
        }

        /// <summary>
        /// The full path of the custom-name sidecar,
        /// <c>&lt;userDir&gt;Neighbourhoods/names.json</c>, or <c>null</c> when there is no user
        /// directory.
        /// </summary>
        public static string NamesPath(string userDir)
        {
            if (string.IsNullOrEmpty(userDir)) return null;
            return Path.Combine(userDir, NamesFolderName, NamesFileName);
        }

        /// <summary>
        /// The full path of a slot's preferred template inside a The Sims install, or
        /// <c>null</c> for an unknown slot or a missing install path. This is only a path; whether
        /// the folder is a usable neighbourhood is <see cref="ResolveSource"/>'s question.
        /// </summary>
        public static string TemplatePath(string installPath, int id)
        {
            var folder = TemplateFolderName(id);
            if (folder == null || string.IsNullOrEmpty(installPath)) return null;
            return Path.Combine(installPath, folder);
        }

        /// <summary>
        /// Do two paths name the same folder?
        /// </summary>
        /// <remarks>
        /// Both sides are made absolute and stripped of trailing separators first, so
        /// <c>C:\Sims</c>, <c>C:\Sims\</c> and <c>C:/Sims</c> all compare equal, and the
        /// comparison is case-insensitive because the filesystem is. Returns <c>false</c> rather
        /// than throwing for null/empty/unresolvable input.
        ///
        /// This is the last line of the delete guard: the folder about to be removed recursively
        /// is re-derived from the slot id and compared against the user directory and against the
        /// primary slot, so no future change to <see cref="SlotFolderName"/> can quietly turn
        /// "delete slot 1" into "delete the whole user directory".
        /// </remarks>
        public static bool IsSameFolder(string first, string second)
        {
            var left = NormalizeFolder(first);
            var right = NormalizeFolder(second);
            if (left == null || right == null) return false;
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var full = Path.GetFullPath(path);
                var trimmed = full.TrimEnd('/', '\\');
                //a root such as "C:\" would trim away to nothing; keep it as-is in that case
                return trimmed.Length == 0 ? full : trimmed;
            }
            catch (Exception)
            {
                //an invalid path is simply "not a folder we can vouch for"
                return null;
            }
        }

        // ---- reading the sidecar -------------------------------------------------------------

        /// <summary>
        /// Every stored custom name, keyed by slot id. Missing, unreadable, malformed and
        /// future-versioned sidecars all yield an empty map rather than an exception.
        /// </summary>
        public static IReadOnlyDictionary<int, string> GetNames(string userDir)
        {
            return ReadNames(userDir);
        }

        /// <summary>
        /// The stored custom name for one slot, or <c>null</c> when it has none.
        /// </summary>
        public static string GetName(string userDir, int id)
        {
            if (!IsKnownSlot(id)) return null;
            string name;
            return ReadNames(userDir).TryGetValue(id, out name) ? name : null;
        }

        /// <summary>
        /// The label a slot should be shown with, reading the sidecar: the player's name if they
        /// set one, otherwise the default. The one call a list control needs per row.
        /// </summary>
        public static string GetDisplayName(string userDir, int id)
        {
            if (!IsKnownSlot(id)) return null;
            return SlotName(id, GetName(userDir, id));
        }

        private static Dictionary<int, string> ReadNames(string userDir)
        {
            var result = new Dictionary<int, string>();

            var path = NamesPath(userDir);
            if (path == null) return result;

            string json;
            try
            {
                if (!File.Exists(path)) return result;
                json = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodManager: could not read the neighbourhood names at '" + path + "'.", e);
                return result;
            }

            try
            {
                using (var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        SimitoneLog.Warn("NeighbourhoodManager: '" + path + "' is not a names object; ignoring it.");
                        return result;
                    }

                    //a missing version is treated as the first one, so a hand-written file works
                    var version = 1;
                    if (root.TryGetProperty("version", out var versionElement)
                        && versionElement.ValueKind == JsonValueKind.Number
                        && versionElement.TryGetInt32(out var parsedVersion))
                    {
                        version = parsedVersion;
                    }

                    if (version < 1 || version > NamesFileVersion)
                    {
                        SimitoneLog.Warn("NeighbourhoodManager: '" + path + "' is version " + version
                            + ", which this build does not understand; ignoring it. Custom names are unavailable.");
                        return result;
                    }

                    if (!root.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Object)
                    {
                        //an empty sidecar is legitimate: the player has not renamed anything yet
                        return result;
                    }

                    foreach (var entry in names.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.String) continue;
                        if (!int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;

                        //ignore entries for slots that do not exist rather than surfacing them:
                        //a sidecar outlives the folders it names
                        if (!IsKnownSlot(id)) continue;

                        var name = SanitizeName(entry.Value.GetString());
                        if (name != null) result[id] = name;
                    }
                }
            }
            catch (JsonException e)
            {
                SimitoneLog.Warn("NeighbourhoodManager: '" + path + "' is not valid JSON; ignoring it.", e);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodManager: could not parse the neighbourhood names at '" + path + "'.", e);
            }

            return result;
        }

        // ---- writing the sidecar -------------------------------------------------------------

        /// <summary>
        /// Store <paramref name="name"/> as slot <paramref name="id"/>'s custom name.
        /// </summary>
        /// <remarks>
        /// The name is gated, not rewritten: a value <see cref="IsValidName"/> rejects changes
        /// nothing and returns <c>false</c>, so the manager never silently turns what the player
        /// typed into something else. A UI that wants to accept messy input should run
        /// <see cref="SanitizeName"/> on the text box value first (and can use
        /// <see cref="IsValidName"/> for live feedback).
        ///
        /// <para>
        /// Passing <c>null</c>, <c>""</c> or an all-whitespace string clears the slot's custom
        /// name instead, so the slot falls back to its default label. The slot must exist; there
        /// is nothing to name otherwise.
        /// </para>
        /// </remarks>
        /// <returns><c>true</c> when the sidecar was written.</returns>
        public static bool Rename(string userDir, int id, string name)
        {
            if (!IsKnownSlot(id))
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot rename slot " + id + "; it is not one of the "
                    + SlotCount + " neighbourhood slots.");
                return false;
            }

            if (!Exists(userDir, id))
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot rename " + SlotFolderName(id) + "; it does not exist.");
                return false;
            }

            if (name == null || name.Trim().Length == 0)
            {
                return WriteNames(userDir, ReadNamesWithout(userDir, id));
            }

            if (!IsValidName(name))
            {
                SimitoneLog.Warn("NeighbourhoodManager: '" + name + "' is not a usable neighbourhood name; nothing was changed.");
                return false;
            }

            var names = ReadNames(userDir);
            names[id] = SanitizeName(name);
            return WriteNames(userDir, names);
        }

        /// <summary>
        /// Drop slot <paramref name="id"/>'s custom name, so it shows its default label again.
        /// Identical to <c>Rename(userDir, id, null)</c>.
        /// </summary>
        public static bool ClearName(string userDir, int id)
        {
            return Rename(userDir, id, null);
        }

        private static Dictionary<int, string> ReadNamesWithout(string userDir, int id)
        {
            var names = ReadNames(userDir);
            names.Remove(id);
            return names;
        }

        private static bool WriteNames(string userDir, Dictionary<int, string> names)
        {
            var path = NamesPath(userDir);
            if (path == null)
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot record neighbourhood names; there is no user directory.");
                return false;
            }

            try
            {
                //same stage/backup/commit contract as the house save: a crash while renaming can
                //lose the new name but can never corrupt the previous one
                AtomicFileCommit.Write(path, SerializeNames(names));
                return true;
            }
            catch (Exception e)
            {
                SimitoneLog.Error("NeighbourhoodManager: could not write the neighbourhood names to '" + path + "'.", e);
                return false;
            }
        }

        /// <summary>
        /// Renders the sidecar. Keys are written in ascending slot order so the file is stable
        /// and diffable rather than reshuffling on every rename.
        /// </summary>
        /// <remarks>
        /// The format, in full:
        /// <code>
        /// {
        ///   "version": 1,
        ///   "names": {
        ///     "0": "Springfield",
        ///     "3": "Ogdenville"
        ///   }
        /// }
        /// </code>
        /// Slot ids are decimal strings (JSON object keys must be strings); an absent id means
        /// "un-renamed". Unknown properties are ignored on read, so a later version can add
        /// fields, but a version this build does not know is refused outright.
        /// </remarks>
        private static byte[] SerializeNames(Dictionary<int, string> names)
        {
            var ids = new List<int>(names.Keys);
            ids.Sort();

            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", NamesFileVersion);
                    writer.WriteStartObject("names");
                    foreach (var id in ids)
                    {
                        writer.WriteString(id.ToString(CultureInfo.InvariantCulture), names[id]);
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                return buffer.ToArray();
            }
        }

        // ---- slot existence ------------------------------------------------------------------

        /// <summary>
        /// Does this slot have a folder in Simitone's user directory? <c>false</c> for an unknown
        /// slot, a missing user directory, or any error.
        /// </summary>
        public static bool Exists(string userDir, int id)
        {
            var path = SlotPath(userDir, id);
            if (path == null) return false;
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The lowest slot id that has no folder yet, or <c>-1</c> when all nine are in use.
        /// What a "New neighbourhood" button should target.
        /// </summary>
        public static int FirstEmptySlot(string userDir)
        {
            if (string.IsNullOrEmpty(userDir)) return -1;
            for (var id = MinSlotId; id <= MaxSlotId; id++)
            {
                if (!Exists(userDir, id)) return id;
            }
            return -1;
        }

        // ---- creating slots ------------------------------------------------------------------

        /// <summary>
        /// Copy a neighbourhood in from a The Sims install into slot <paramref name="id"/>.
        /// </summary>
        /// <remarks>
        /// The source is the first of these that is a <em>usable neighbourhood</em>, i.e. a
        /// folder containing a <see cref="TS1UserData.NeighborhoodResourceName"/>:
        /// <list type="number">
        /// <item><c>&lt;install&gt;/&lt;SlotFolderName(id)&gt;</c> - the neighbourhood the
        /// player already played in The Sims, which is the only option that brings Sims, money
        /// and house contents with it;</item>
        /// <item><c>&lt;install&gt;/&lt;TemplateFolderName(id)&gt;</c> - the area template for
        /// this slot;</item>
        /// <item><c>&lt;install&gt;/TemplateUserData</c> - the base residential template.</item>
        /// </list>
        ///
        /// <para>
        /// Rung 2 never fires on a stock retail install: the expansion templates are lot
        /// overlays with no <c>Neighborhood.iff</c>, so requiring that file is what stops a
        /// "successful" copy from producing a folder the provider cannot open. The chain
        /// collapses to rung 1 then rung 3, and the same check is applied to every rung.
        /// </para>
        /// </remarks>
        /// <returns>
        /// <c>true</c> when the slot was created. <c>false</c> - with the reason logged - when the
        /// slot id is unknown, the destination already exists, no usable source was found, or the
        /// copy failed. A partial copy is discarded rather than left behind, so a slot is never
        /// created in a state the provider would choke on.
        /// </returns>
        public static bool CopyFromInstall(string installPath, string userDir, int id)
        {
            if (!IsKnownSlot(id))
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot copy into slot " + id + "; it is not one of the "
                    + SlotCount + " neighbourhood slots.");
                return false;
            }

            var destination = SlotPath(userDir, id);
            if (destination == null)
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot copy " + SlotFolderName(id) + "; there is no user directory.");
                return false;
            }

            if (!RefuseToOverwrite(destination, id)) return false;

            var source = ResolveSource(installPath, id);
            if (source == null)
            {
                SimitoneLog.Warn("NeighbourhoodManager: no usable neighbourhood to copy into " + SlotFolderName(id)
                    + " from '" + (installPath ?? "(no install path)") + "'. Expected a folder containing a "
                    + TS1UserData.NeighborhoodResourceName + ".");
                return false;
            }

            return CopyInto(source, destination, "neighbourhood " + SlotFolderName(id) + " from '" + source + "'");
        }

        /// <summary>
        /// Create slot <paramref name="id"/> as a new, empty neighbourhood by copying the base
        /// <see cref="BaseTemplateFolderName"/>, taken from <see cref="DefaultInstallPath"/>.
        /// </summary>
        /// <returns><c>false</c> when <see cref="DefaultInstallPath"/> is unset, when the
        /// destination already exists, or on any copy failure.</returns>
        public static bool CreateBlank(string userDir, int id)
        {
            return CreateBlank(userDir, id, DefaultInstallPath);
        }

        /// <summary>
        /// Create slot <paramref name="id"/> as a new, empty neighbourhood by copying the base
        /// <see cref="BaseTemplateFolderName"/> out of <paramref name="installPath"/>.
        /// </summary>
        /// <remarks>
        /// "Empty" means "no Sims in it", not "no lots": <c>TemplateUserData</c> is the base
        /// residential neighbourhood, so the new slot has all twenty residential lots with their
        /// lot files, the street names and the empty <c>Characters/</c> folder the provider
        /// enumerates on load - which is exactly the shape a fresh The Sims neighbourhood has. It
        /// is the one install folder that is a complete neighbourhood, so a new slot is
        /// openable the moment it is created.
        /// </remarks>
        /// <returns>
        /// <c>true</c> when the slot was created. <c>false</c> when the slot already exists, the
        /// install has no base template, or the copy failed.
        /// </returns>
        public static bool CreateBlank(string userDir, int id, string installPath)
        {
            if (!IsKnownSlot(id))
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot create slot " + id + "; it is not one of the "
                    + SlotCount + " neighbourhood slots.");
                return false;
            }

            var destination = SlotPath(userDir, id);
            if (destination == null)
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot create " + SlotFolderName(id) + "; there is no user directory.");
                return false;
            }

            if (!RefuseToOverwrite(destination, id)) return false;

            string source = null;
            if (!string.IsNullOrEmpty(installPath))
            {
                var candidate = Path.Combine(installPath, BaseTemplateFolderName);
                if (IsUsableNeighborhoodSource(candidate))
                {
                    source = candidate;
                }
                else
                {
                    SimitoneLog.Warn("NeighbourhoodManager: '" + candidate + "' is not a usable neighbourhood (no "
                        + TS1UserData.NeighborhoodResourceName + "), so no blank neighbourhood can be made from it.");
                }
            }
            else
            {
                SimitoneLog.Warn("NeighbourhoodManager: no The Sims install path is known, so no blank neighbourhood "
                    + "can be made. Set NeighbourhoodManager.DefaultInstallPath or pass the install path explicitly.");
            }

            if (source == null) return false;

            return CopyInto(source, destination, "blank neighbourhood " + SlotFolderName(id) + " from '" + source + "'");
        }

        /// <summary>
        /// The single rule that keeps existing neighbourhoods alive: a slot folder that is
        /// already there is never written into, with the primary called out separately in the log
        /// so "the game would have overwritten your first neighbourhood" is greppable.
        /// </summary>
        private static bool RefuseToOverwrite(string destination, int id)
        {
            if (!Directory.Exists(destination)) return true;

            if (id == PrimarySlotId)
            {
                SimitoneLog.Warn("NeighbourhoodManager: refusing to overwrite the primary neighbourhood ("
                    + PrimaryFolderName + "); it holds the Simitone bootstrap data and cannot be replaced.");
            }
            else
            {
                SimitoneLog.Warn("NeighbourhoodManager: " + SlotFolderName(id) + " already exists at '" + destination
                    + "'; refusing to overwrite it. Delete it first if you meant to replace it.");
            }
            return false;
        }

        // ---- deleting slots ------------------------------------------------------------------

        /// <summary>
        /// Delete slot <paramref name="id"/>'s folder and forget its custom name.
        /// </summary>
        /// <remarks>
        /// Two guards, both reported through <see cref="SimitoneLog"/> and a <c>false</c>:
        /// the primary slot is never deletable (the content manager opened it during startup and
        /// nothing else would bring it back), and the slot the caller says is loaded
        /// (<paramref name="loadedSlotId"/>) is never deletable, because its files are still open
        /// by the running session. Finally the folder to be removed is re-derived and compared
        /// with <see cref="IsSameFolder"/> against the user directory and the primary slot, so a
        /// mis-resolved path cannot escalate into deleting everything.
        /// </remarks>
        /// <param name="userDir">The user's Simitone folder.</param>
        /// <param name="id">Slot to delete.</param>
        /// <param name="loadedSlotId">
        /// The slot id the running session has loaded, or <c>null</c> when nothing is loaded. The
        /// caller owns this because only it knows what the game has open.
        /// </param>
        /// <returns><c>true</c> when the folder was removed.</returns>
        public static bool Delete(string userDir, int id, int? loadedSlotId = null)
        {
            if (!IsKnownSlot(id))
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot delete slot " + id + "; it is not one of the "
                    + SlotCount + " neighbourhood slots.");
                return false;
            }

            if (id == PrimarySlotId)
            {
                SimitoneLog.Warn("NeighbourhoodManager: refusing to delete the primary neighbourhood ("
                    + PrimaryFolderName + "); it is the one Simitone boots from.");
                return false;
            }

            if (loadedSlotId.HasValue && loadedSlotId.Value == id)
            {
                SimitoneLog.Warn("NeighbourhoodManager: refusing to delete " + SlotFolderName(id)
                    + "; it is the neighbourhood currently loaded. Switch away from it first.");
                return false;
            }

            var path = SlotPath(userDir, id);
            if (path == null)
            {
                SimitoneLog.Warn("NeighbourhoodManager: cannot delete " + SlotFolderName(id) + "; there is no user directory.");
                return false;
            }

            //defence in depth: the path is rebuilt from the id above, and this confirms the
            //folder about to be removed recursively is neither the user directory itself nor the
            //primary slot before anything is touched.
            if (IsSameFolder(path, userDir) || IsSameFolder(path, SlotPath(userDir, PrimarySlotId)))
            {
                SimitoneLog.Error("NeighbourhoodManager: refusing to delete '" + path
                    + "'; it resolves to the user directory or to the primary neighbourhood.");
                return false;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    SimitoneLog.Warn("NeighbourhoodManager: cannot delete " + SlotFolderName(id) + "; it does not exist.");
                    return false;
                }
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodManager: could not look at '" + path + "'.", e);
                return false;
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception e)
            {
                SimitoneLog.Error("NeighbourhoodManager: could not delete '" + path + "'.", e);
                return false;
            }

            SimitoneLog.Info("NeighbourhoodManager: deleted " + SlotFolderName(id) + " at '" + path + "'.");

            //a label for a neighbourhood that no longer exists is just noise, but failing to
            //drop it must not make an otherwise successful delete look like a failure
            WriteNames(userDir, ReadNamesWithout(userDir, id));
            return true;
        }

        // ---- copy machinery ------------------------------------------------------------------

        /// <summary>
        /// Picks the folder to copy slot <paramref name="id"/> from, in the order documented on
        /// <see cref="CopyFromInstall"/>, accepting only folders that actually contain a
        /// <see cref="TS1UserData.NeighborhoodResourceName"/>.
        /// </summary>
        /// <returns>The full source path, or <c>null</c> when nothing in the install qualifies.</returns>
        private static string ResolveSource(string installPath, int id)
        {
            if (string.IsNullOrEmpty(installPath)) return null;

            //de-duplicated because TemplateFolderName(0) and the base fallback are the same
            //folder, and a duplicate probe would only produce a duplicate log line
            var candidates = new List<string>(3);
            AddCandidate(candidates, SlotFolderName(id));
            AddCandidate(candidates, TemplateFolderName(id));
            AddCandidate(candidates, BaseTemplateFolderName);

            foreach (var name in candidates)
            {
                var path = Path.Combine(installPath, name);
                if (!IsUsableNeighborhoodSource(path)) continue;
                SimitoneLog.Info("NeighbourhoodManager: seeding " + SlotFolderName(id) + " from '" + path + "'.");
                return path;
            }

            return null;
        }

        private static void AddCandidate(List<string> candidates, string folderName)
        {
            if (string.IsNullOrEmpty(folderName) || candidates.Contains(folderName)) return;
            candidates.Add(folderName);
        }

        /// <summary>
        /// Is this folder a neighbourhood the provider could open? The same test - and for the
        /// same reason - as <see cref="TS1UserData.IsBootstrappable"/>: a folder without
        /// <c>Neighborhood.iff</c> is either empty or a lot overlay, and copying it produces a
        /// slot that fails on the first <c>new IffFile(...)</c> of
        /// <c>TS1NeighborhoodProvider.InitSpecific</c>.
        /// </summary>
        private static bool IsUsableNeighborhoodSource(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path)
                    && Directory.Exists(path)
                    && File.Exists(Path.Combine(path, TS1UserData.NeighborhoodResourceName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Copies <paramref name="source"/> over <paramref name="destination"/>, or reports
        /// failure and removes whatever it managed to write. All-or-nothing on purpose: a
        /// half-copied neighbourhood is indistinguishable from a broken one.
        /// </summary>
        private static bool CopyInto(string source, string destination, string label)
        {
            int failures;
            try
            {
                failures = CopyDirectory(source, destination);
            }
            catch (Exception e)
            {
                SimitoneLog.Error("NeighbourhoodManager: could not copy " + label + ".", e);
                DiscardPartialCopy(destination);
                return false;
            }

            if (failures > 0)
            {
                SimitoneLog.Error("NeighbourhoodManager: " + failures + " file(s) of " + label
                    + " could not be written; discarding the partial copy at '" + destination + "'.");
                DiscardPartialCopy(destination);
                return false;
            }

            SimitoneLog.Info("NeighbourhoodManager: created '" + destination + "' from " + label + ".");
            return true;
        }

        /// <summary>
        /// Recursive copy with per-file error accounting. A single locked or unreadable file
        /// should not hide the rest of the copy - but it does make the whole copy fail, because
        /// the caller cannot know how much is missing.
        /// </summary>
        /// <returns>How many files and folders could not be written; 0 on a complete copy.</returns>
        private static int CopyDirectory(string source, string destination)
        {
            var failures = 0;
            Directory.CreateDirectory(destination);

            //directories first, so a file copy never has to create its own parent
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                try
                {
                    Directory.CreateDirectory(TargetPath(destination, source, directory));
                }
                catch (Exception e)
                {
                    failures++;
                    SimitoneLog.Warn("NeighbourhoodManager: could not create '" + directory + "' while copying '"
                        + source + "'.", e);
                }
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Copy(file, TargetPath(destination, source, file), true);
                }
                catch (Exception e)
                {
                    failures++;
                    SimitoneLog.Warn("NeighbourhoodManager: could not copy '" + file + "' from '" + source + "'.", e);
                }
            }

            return failures;
        }

        /// <summary>
        /// Re-roots one entry of an enumeration under <paramref name="source"/> onto
        /// <paramref name="destination"/>. Both ends of the enumeration come from the same
        /// <c>Directory.*</c> call on <paramref name="source"/>, so the prefix always matches and
        /// this is plain string work rather than path comparison.
        /// </summary>
        private static string TargetPath(string destination, string source, string full)
        {
            var root = source.TrimEnd('/', '\\');
            var relative = full.Substring(root.Length).TrimStart('\\', '/');

            //the source's own root, which is "" for the top level and must stay the destination
            return relative.Length == 0 ? destination : Path.Combine(destination, relative);
        }

        private static void DiscardPartialCopy(string destination)
        {
            //destination is always Path.Combine(userDir, SlotFolderName(id)), so it is a
            //child of the user directory and can never be the user directory or an install
            //folder - see the delete guard for the equivalent check on the destructive path.
            try
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
            }
            catch (Exception e)
            {
                SimitoneLog.Error("NeighbourhoodManager: could not remove the partial copy at '"
                    + destination + "'. Delete it by hand; the neighbourhood is incomplete.", e);
            }
        }

        // ---- table construction --------------------------------------------------------------

        private static ReadOnlyCollection<NeighbourhoodSlot> BuildSlots()
        {
            var slots = new List<NeighbourhoodSlot>(SlotCount);
            for (var id = MinSlotId; id <= MaxSlotId; id++)
            {
                slots.Add(new NeighbourhoodSlot(
                    id,
                    SlotFolderName(id),
                    TemplateFolderName(id),
                    DefaultSlotName(id),
                    AreaNameFor(id)));
            }
            return new ReadOnlyCollection<NeighbourhoodSlot>(slots);
        }
    }
}
