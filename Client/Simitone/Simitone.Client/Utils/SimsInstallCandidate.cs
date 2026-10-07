using System;
using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Which edition of The Sims 1 an installation directory belongs to.
    ///
    /// The edition is not cosmetic: it decides where the playable save data lives.
    /// The Sims: Legacy Collection (the Steam/EA re-release) keeps its real
    /// <c>UserData</c> outside the game folder, under
    /// <c>%USERPROFILE%\Saved Games\Electronic Arts\The Sims 25</c>, while every other
    /// edition keeps it next to the game. <c>FSO.Content.TS1.TS1NeighbourhoodProvider</c>
    /// branches on <c>Content.TS1SteamInstall</c> when it seeds Simitone's own copy of a
    /// neighbourhood, and Simitone has to set that flag from the install it chose - hence
    /// the enum.
    ///
    /// The values are ordered as the discovery order, so the default (zero) member is
    /// "not classified" and never accidentally reads as a hit.
    /// </summary>
    public enum SimsInstallKind
    {
        /// <summary>Not classified. Treated as an ordinary retail/boxed install.</summary>
        Unknown = 0,

        /// <summary>A <c>../The Sims/</c> folder next to the Simitone executable.</summary>
        Portable = 1,

        /// <summary>An install registered with Windows by the classic Maxis installer.</summary>
        Registry = 2,

        /// <summary>The Sims: Legacy Collection installed through Steam.</summary>
        Steam = 3
    }

    /// <summary>
    /// Player-facing labels for <see cref="SimsInstallKind"/>, used by the installation
    /// selector and the confirmation dialog. Kept next to the enum so a new edition cannot
    /// be added without deciding what to call it.
    /// </summary>
    public static class SimsInstallKinds
    {
        public static string Describe(SimsInstallKind kind)
        {
            switch (kind)
            {
                case SimsInstallKind.Portable:
                    return "Portable install (next to Simitone)";
                case SimsInstallKind.Registry:
                    return "The Sims (CD/DVD/GOG, registered with Windows)";
                case SimsInstallKind.Steam:
                    return "The Sims: Legacy Collection (Steam)";
                default:
                    return "The Sims";
            }
        }
    }

    /// <summary>
    /// One plausible The Sims 1 installation: where it is, and which edition it is.
    ///
    /// <para>
    /// This is the value the discovery layer produces and the startup layer chooses between,
    /// so it carries no UI and does no IO. Everything the selector needs in order to explain
    /// itself - a label and a path - is here, which is also why the upstream port's
    /// <c>(description, path, type)</c> tuple grew a named type: a tuple cannot be
    /// documented, cannot be compared for equality in a test, and silently misorders when
    /// someone retypes it.
    /// </para>
    ///
    /// <para>
    /// The path is stored exactly as the probe produced it - forward slashes, one trailing
    /// slash - because that is the form <c>FSOEnvironment</c> and
    /// <see cref="GamePathArgument"/> expect everywhere downstream, and rewriting it here would
    /// change what the game is handed. That is why there are two forms of it rather than one:
    /// <see cref="Path"/> is what the game runs on, and <see cref="CanonicalPath"/> is what two
    /// spellings of the same directory are compared as. Compare with <see cref="IsSamePath"/>, or
    /// with <see cref="CanonicalPath"/>, rather than with <c>==</c>.
    /// </para>
    /// </summary>
    public sealed class SimsInstallCandidate
    {
        public SimsInstallCandidate(string path, SimsInstallKind kind, string description = null)
        {
            Path = path;
            // Reduced once, here, so "these two are the same directory" has one owner rather than
            // being a convention every comparison has to remember. It is derived from Path rather
            // than replacing it: Path is the form the game runs on, and Canonical deliberately
            // drops the trailing slash that form carries.
            CanonicalPath = Canonical(path);
            Kind = kind;
            // A candidate with no label of its own still has to render as something in the
            // selector, so fall back to the per-edition default at construction time rather
            // than leaving a null for every dialog to remember to handle.
            Description = string.IsNullOrEmpty(description) ? SimsInstallKinds.Describe(kind) : description;
        }

        /// <summary>Install directory, forward slashes, trailing slash.</summary>
        public string Path { get; }

        /// <summary>
        /// <see cref="Path"/> reduced by <see cref="Canonical"/>, so two spellings of one
        /// directory compare equal as a plain case-insensitive string. Null for a path that
        /// canonicalises to nothing, which is what a probe that found nothing produces.
        /// </summary>
        public string CanonicalPath { get; }

        /// <summary>Edition this install is.</summary>
        public SimsInstallKind Kind { get; }

        /// <summary>Label for the selector. Never null.</summary>
        public string Description { get; }

        /// <summary>
        /// Does this install keep its save data in Saved Games rather than next to the game?
        /// </summary>
        public bool IsSteam => Kind == SimsInstallKind.Steam;

        public override string ToString()
        {
            return Description + " - " + Path;
        }

        /// <summary>
        /// Reduce a path to the form two spellings of the same directory can be compared in:
        /// no surrounding whitespace, forward slashes, no trailing slash. A null/empty path
        /// canonicalises to null, so two "nothing" answers compare equal and any real path
        /// does not.
        /// </summary>
        public static string Canonical(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var canonical = path.Trim().Replace('\\', '/');

            // A drive root is a directory, not a trailing slash: "C:/" is the root of the
            // drive and "C:" is "wherever this process happens to be on C:", which are
            // different paths, so only strip a slash when there is a real segment in front
            // of it.
            var isDriveRoot = canonical.Length >= 2
                && canonical[canonical.Length - 1] == '/'
                && canonical[canonical.Length - 2] == ':';
            if (!isDriveRoot)
            {
                while (canonical.Length > 1 && canonical[canonical.Length - 1] == '/')
                {
                    canonical = canonical.Substring(0, canonical.Length - 1);
                }
            }

            return canonical.Length == 0 ? null : canonical;
        }

        /// <summary>
        /// Do two paths name the same directory? Compared case-insensitively, because
        /// Windows paths are, and after <see cref="Canonical"/>, because the same install
        /// reached through two different rungs arrives spelled differently - the registry
        /// probe appends a backslash the Steam probe does not, and a portable install is a
        /// relative <c>../The Sims/</c> at all.
        /// </summary>
        /// <remarks>
        /// For two candidates, <see cref="IsSameInstall"/> is just a case-insensitive compare of
        /// their <see cref="CanonicalPath"/>, which is the point of caching that: the reduction is
        /// done once per value instead of twice per comparison. It is a separate name rather than
        /// an overload, because an overload taking two reference types makes every
        /// <c>IsSamePath(null, null)</c> an ambiguous call at the call site.
        /// </remarks>
        public static bool IsSamePath(string a, string b)
        {
            return string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Do two candidates name the same directory? Equivalent to
        /// <see cref="IsSamePath(string,string)"/> on their <see cref="Path"/> values, without
        /// reducing either of them again.
        /// </summary>
        /// <returns>False for a null candidate - "no candidate" is not the same answer as "the
        /// same directory" the way two absent <em>paths</em> are.</returns>
        public static bool IsSameInstall(SimsInstallCandidate a, SimsInstallCandidate b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.CanonicalPath, b.CanonicalPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Is this path inside a Steam library, i.e. <c>&lt;library&gt;/steamapps/common/...</c>?
        ///
        /// <para>
        /// This is the one piece of Steam detection that does not need Steam to be involved:
        /// the path shape is fixed by Steam's library layout, and it is what
        /// <c>SteamGameLocator.GetGamePath</c> itself builds. It exists for the case where
        /// the player named the directory themselves with <c>-path</c>, where there is no
        /// discovery result to take the edition from.
        /// </para>
        /// </summary>
        public static bool LooksLikeSteamLibraryPath(string path)
        {
            var canonical = Canonical(path);
            return canonical != null &&
                   canonical.IndexOf("/steamapps/common/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Adds a discovered install to a roster, ignoring a probe that found nothing and a
        /// probe that re-found a directory an earlier rung already reported.
        ///
        /// <para>
        /// The dedupe is the point. Two rungs naming the same directory is ordinary, not
        /// exotic: a Steam install is very often also present in a leftover Maxis
        /// <c>InstallPath</c>, and a portable folder that happens to be a Steam library
        /// resolves from both. Offered as two rows, the player is asked to choose between
        /// two spellings of the answer they would have got either way - and the rows carry
        /// different editions, so the choice would silently change where saves are read
        /// from. The first rung to claim a directory keeps it, which is also the
        /// precedence order <see cref="GameInstallLadder"/> would have booted.
        /// </para>
        ///
        /// <returns>True if the roster grew, false for a null/blank path or a duplicate.</returns>
        /// <remarks>
        /// Every roster is built through here, which is what makes "no two entries in a roster
        /// are the same directory" a property of how the list is filled rather than something a
        /// caller has to remember afterwards. A list assembled by hand does not have it, and
        /// <see cref="FindByPath"/> will happily find both halves of a duplicate in one.
        /// </remarks>
        public static bool TryAddTo(IList<SimsInstallCandidate> roster, string path, SimsInstallKind kind, string description = null)
        {
            if (roster == null || Canonical(path) == null) return false;
            if (FindByPath(roster, path) != null) return false;

            roster.Add(new SimsInstallCandidate(path, kind, description));
            return true;
        }

        /// <summary>
        /// The roster entry for a path, or null. Used to recover the full candidate (and
        /// therefore the edition) from a path the player picked, and to test for duplicates.
        /// </summary>
        public static SimsInstallCandidate FindByPath(IEnumerable<SimsInstallCandidate> roster, string path)
        {
            if (roster == null || Canonical(path) == null) return null;

            foreach (var candidate in roster)
            {
                if (IsSamePath(candidate.Path, path)) return candidate;
            }
            return null;
        }
    }
}
