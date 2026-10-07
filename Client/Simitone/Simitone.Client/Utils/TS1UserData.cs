using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Where the The Sims 1 "user data" (neighbourhood) folders live, and when one of them is
    /// not there yet. On a retail install the answer is simply "next to the game" - it is the
    /// Legacy Collection (and any never-launched install) that turns the question into a startup
    /// precondition.
    ///
    /// <para>
    /// On first run <c>FSO.Content.TS1.TS1NeighbourhoodProvider.InitSpecific</c> seeds Simitone's
    /// own copy of a neighbourhood by copying <c>&lt;install&gt;/UserData</c> into the Simitone
    /// user directory, and then immediately opens <c>Neighborhood.iff</c> out of the copy. The
    /// copy loop used to enumerate its source with no existence check, so a missing source folder
    /// was a <see cref="DirectoryNotFoundException"/> thrown from inside content init - after the
    /// window is already up, with no hint about what to do.
    /// <para>
    /// That was not a hypothetical. The Sims never creates <c>UserData</c> during installation -
    /// the game writes it the first time it is launched and a neighbourhood is loaded - so a
    /// never-launched install of <em>any</em> edition has none, and the old pre-flight could only
    /// tell such a player to go and play The Sims once. What such an install does ship is
    /// <see cref="BaseTemplateFolderName"/>, the same neighbourhood with nothing in it, and the
    /// provider now falls back to it. This type exists so the startup pre-flight in
    /// <c>Simitone.Windows.Program</c> can name the exact files that have to be there, decide
    /// whether the bootstrap will actually succeed, and turn the remaining "there is genuinely
    /// nothing to copy" case into an actionable message.
    /// </para>
    ///
    /// <para>
    /// The edition this mostly protects is The Sims: Legacy Collection (the Steam/EA re-release).
    /// It does not ship a usable <c>UserData</c>: the game writes one on first launch, after the
    /// player has started a neighbourhood, and it keeps the real save data outside the install
    /// directory entirely - under <c>%USERPROFILE%\Saved Games\Electronic Arts\The Sims 25</c>.
    /// No edition ships <c>UserData</c> though; vanilla TS1 creates it on first launch too, so a
    /// never-launched retail install has exactly the same shape. A Legacy Collection install is
    /// read from Saved Games <em>and</em> from its own <see cref="BaseTemplateFolderName"/>,
    /// because the template ships with the game while the saves do not.
    /// </para>
    ///
    /// <para>
    /// Only the primary neighbourhood (<c>UserData</c>) is required: it is the one the content
    /// manager opens during startup, and the one an unplayed Legacy Collection install is missing.
    /// The numbered siblings (<c>UserData2</c> ... <c>UserData8</c>) are only read when the
    /// player switches to that neighbourhood, and requiring them here would lock out anyone who
    /// has only ever played neighbourhood one.
    /// </para>
    /// </summary>
    public static class TS1UserData
    {
        /// <summary>
        /// Folder holding the first neighbourhood's data, both in a The Sims install directory
        /// (the copy source) and in Simitone's own user directory.
        /// </summary>
        public const string NeighborhoodFolderName = "UserData";

        /// <summary>
        /// The base residential template inside a The Sims install: a complete but empty copy of
        /// neighbourhood 1, and the only template folder that contains a
        /// <see cref="NeighborhoodResourceName"/>. The expansion templates (<c>TemplateDowntown</c>
        /// and friends) are lot/house-description overlays the game layers onto a base
        /// neighbourhood, so they cannot be opened on their own.
        ///
        /// <para>
        /// This is what makes a never-launched install bootstrappable, and it is why a store
        /// install nobody has ever played can be booted on. Kept equal to
        /// <c>NeighbourhoodManager.BaseTemplateFolderName</c> - the engine provider, the
        /// management page and this pre-flight all have to name the same folder - which the tests
        /// assert rather than trust.
        /// </para>
        /// </summary>
        public const string BaseTemplateFolderName = "TemplateUserData";

        /// <summary>
        /// The IFF the neighbourhood provider opens first, and therefore the one file whose
        /// presence in a copy source proves the source is usable rather than an empty folder or a
        /// lot overlay.
        /// </summary>
        public const string NeighborhoodResourceName = "Neighborhood.iff";

        /// <summary>
        /// Folder name The Sims: Legacy Collection uses under
        /// <c>Saved Games\Electronic Arts</c> for its save data - the registry-less "25" of the
        /// original 1999 executable's name.
        /// </summary>
        public const string SteamSavesFolderName = "The Sims 25";

        /// <summary>
        /// Path of a neighbourhood folder inside a The Sims installation - the directory the
        /// bootstrap copies from. Returns null for a null/empty install path so the result can be
        /// handed straight to <see cref="File.Exists"/> without a separate guard.
        /// </summary>
        /// <param name="installPath">A Sims installation directory, with or without a trailing slash.</param>
        /// <param name="neighborhood">
        /// Folder name to resolve, e.g. <c>UserData</c> or <c>UserData2</c>. Defaults to
        /// <see cref="NeighborhoodFolderName"/>.
        /// </param>
        public static string InstallNeighborhoodPath(string installPath, string neighborhood = NeighborhoodFolderName)
        {
            if (string.IsNullOrEmpty(installPath)) return null;
            return Path.Combine(installPath, neighborhood);
        }

        /// <summary>
        /// Path of the base <see cref="BaseTemplateFolderName"/> inside a The Sims installation,
        /// or null for a null/empty install path. The second source the first-run bootstrap falls
        /// back to, and the one that exists on a store install that has never been launched.
        /// </summary>
        public static string InstallTemplatePath(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return null;
            return Path.Combine(installPath, BaseTemplateFolderName);
        }

        /// <summary>
        /// Does this folder hold a neighbourhood the provider could open - i.e. does it exist and
        /// contain a <see cref="NeighborhoodResourceName"/>?
        /// </summary>
        /// <param name="neighborhoodFolder">
        /// A neighbourhood folder itself (<c>&lt;install&gt;/UserData</c>, or a Saved Games
        /// neighbourhood), not an install directory - use <see cref="IsBootstrappable"/> for that.
        /// </param>
        /// <remarks>
        /// The single <see cref="File.Exists"/> rather than a directory probe followed by a file
        /// probe: an existing file implies its parent folder exists, and the file is the thing the
        /// copy actually has to produce. This is the single place the "would the copy produce
        /// something openable" question is asked, so the pre-flight and the engine cannot answer
        /// it differently.
        /// </remarks>
        public static bool HasNeighborhood(string neighborhoodFolder)
        {
            if (string.IsNullOrEmpty(neighborhoodFolder)) return false;
            return File.Exists(Path.Combine(neighborhoodFolder, NeighborhoodResourceName));
        }

        /// <summary>
        /// Root of The Sims: Legacy Collection's save data for a user profile, i.e. the
        /// <c>Saved Games\Electronic Arts\The Sims 25</c> folder. Returns null for a null/empty
        /// profile path.
        /// </summary>
        public static string SteamSavesRoot(string userProfile)
        {
            if (string.IsNullOrEmpty(userProfile)) return null;
            return Path.Combine(userProfile, "Saved Games", "Electronic Arts", SteamSavesFolderName);
        }

        /// <summary>
        /// Path of one neighbourhood inside The Sims: Legacy Collection's save data. This is
        /// where the Steam version of the game keeps the playable family and lots - useful to name
        /// in a message, and read by the bootstrap when this edition has been played, but not the
        /// only place Simitone looks: a Legacy Collection install that has never been launched has
        /// nothing here and is seeded from its own <see cref="BaseTemplateFolderName"/> instead.
        /// </summary>
        public static string SteamNeighborhoodPath(string userProfile, string neighborhood = NeighborhoodFolderName)
        {
            var root = SteamSavesRoot(userProfile);
            if (root == null) return null;
            return Path.Combine(root, neighborhood);
        }

        /// <summary>
        /// Can Simitone bootstrap its first neighbourhood from this installation?
        /// </summary>
        /// <remarks>
        /// Two sources, either of which is enough - the same two the engine provider tries, in the
        /// same order:
        /// <list type="number">
        /// <item><c>&lt;install&gt;/UserData/Neighborhood.iff</c> - the neighbourhood the player
        /// already played;</item>
        /// <item><c>&lt;install&gt;/TemplateUserData/Neighborhood.iff</c> - the base residential
        /// template that every store install ships.</item>
        /// </list>
        ///
        /// <para>
        /// Rung 2 is what lets a never-launched install boot, and it is the whole point of this
        /// type having a rule at all: the old rule demanded rung 1 alone, which no unplayed
        /// install can satisfy, so the only way in was to open The Sims once first - a step with
        /// no purpose of its own that a player who has not bought the game could not perform.
        /// </para>
        ///
        /// <para>
        /// <see cref="NeighborhoodResourceName"/> is required at <i>both</i> rungs, not merely the
        /// folder. A present-but-empty <c>UserData</c> folder is what The Sims: Legacy Collection
        /// looks like before the game has been launched; copying it produces a neighbourhood that
        /// fails on the very next line of the provider, so a folder-only check would let exactly
        /// the case this guards against through.
        /// </para>
        /// </remarks>
        public static bool IsBootstrappable(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return false;
            return HasNeighborhood(InstallNeighborhoodPath(installPath))
                || HasNeighborhood(InstallTemplatePath(installPath));
        }
    }
}
