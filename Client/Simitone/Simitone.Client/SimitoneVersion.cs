// The dependency runs version -> parser, never the other way: UpdateChecker needs to know what
// this build is, so it must not be the thing that decides what a version is. That direction
// also keeps SimitoneVersion's initialisation free of any UpdateChecker static state.
using Simitone.Client.Services;
using System;
using System.Reflection;

namespace Simitone.Client
{
    /// <summary>
    /// Single source of truth for "which Simitone is this, and where do its releases live".
    ///
    /// <para>
    /// The upstream fork this file came from hardcoded both answers as string constants
    /// (<c>"0.8.20-forked"</c>, plus a URL pointing at the fork). Two things were wrong with
    /// that shape:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A literal version string drifts. Nothing links it to the build, so a release can ship a
    /// binary that reports the previous version, and the update checker then either nags about
    /// a build the player is already running or stays quiet about one they are not. Here the
    /// version is <em>derived</em> from the assembly this type is declared in, so the answer
    /// moves only when the build stamps a new one.
    /// </description></item>
    /// <item><description>
    /// The destination was a second thing to remember. Here every URL is built from
    /// <see cref="RepositorySlug"/>, so retargeting the checker is a one-line edit and the
    /// host allowlist the update checker enforces follows it for free.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Where the number comes from.</b> <see cref="Current"/> reads the assembly the game
    /// actually starts in - <c>Simitone.exe</c>, whose <c>&lt;Version&gt;</c> is
    /// <c>Simitone.Windows.csproj</c>'s - and falls back to the assembly this type is declared
    /// in. That ordering matters: only the entry assembly is the shipped product, and only it
    /// carries the release stamp, because the stamp lives on the executable's project. Reading
    /// only the declaring assembly meant a build with no explicit version reported the SDK
    /// default of 1.0.0, which is newer than every published 0.x release, so the checker
    /// answered "up to date" and could never do anything.
    ///
    /// <para>
    /// The entry assembly is only trusted when it is actually a Simitone build (see
    /// <see cref="IsGameAssemblyName"/>). A test host, a profiler or a script that loads
    /// Simitone.Client.dll into its own process would otherwise be reported as the game, and
    /// <see cref="Current"/> would be whatever version that host happened to be - which is worse
    /// than the fallback it replaces.
    /// </para>
    /// </summary>
    public static class SimitoneVersion
    {
        /// <summary>
        /// The GitHub repository whose releases this build belongs to, as <c>owner/name</c>.
        ///
        /// This is the one and only place the destination is named. Every URL the update
        /// checker uses is built from it, and <c>UpdateChecker</c> derives the host it is
        /// willing to open a browser to from <see cref="ReleasesPageUrl"/>, so changing this
        /// constant retargets the whole feature - and its URL allowlist - coherently.
        ///
        /// This has to be the upstream Simitone, the project whose releases players install,
        /// not a personal fork: pointing the checker at a fork would advertise builds the
        /// rest of the player base does not have.
        /// </summary>
        public const string RepositorySlug = "riperiperi/Simitone";

        /// <summary>
        /// The GitHub REST endpoint describing the newest published release of
        /// <see cref="RepositorySlug"/>. Unauthenticated GitHub API calls are rate limited to
        /// 60/hour per IP, which is one more reason not to poll this from a loop.
        /// </summary>
        public const string LatestReleaseApiUrl =
            "https://api.github.com/repos/" + RepositorySlug + "/releases/latest";

        /// <summary>
        /// The human-facing releases page. Used as the fallback destination when a release
        /// payload has no usable <c>html_url</c>, and as the manual "get the latest build"
        /// link the UI can always offer.
        /// </summary>
        public const string ReleasesPageUrl =
            "https://github.com/" + RepositorySlug + "/releases";

        /// <summary>
        /// Version reported when this build carries no parseable version at all. A .NET
        /// assembly always has a numeric <c>AssemblyVersion</c>, so this is unreachable in
        /// practice; it exists so <see cref="Current"/> is total rather than null, and because
        /// erring towards "older than everything" is the conservative answer - it costs at
        /// most one extra dialog.
        /// </summary>
        public const string UnstampedVersion = "0.0.0";

        /// <summary>
        /// The simple name of the assembly that is the game: <c>Simitone.Windows.csproj</c>'s
        /// <c>&lt;AssemblyName&gt;</c>, which is what the process starts in.
        /// </summary>
        public const string GameAssemblyName = "Simitone";

        /// <summary>
        /// Whether an assembly with this simple name is the game, and so may be believed about
        /// what version it is.
        /// </summary>
        /// <remarks>
        /// The only test that keeps a non-Simitone host out: the version is read off
        /// <see cref="System.Reflection.Assembly.GetEntryAssembly"/>, and the test runner, an IDE
        /// and anything else embedding these types are all entry assemblies too. They are
        /// excluded by name rather than by a flag so the decision needs no initialisation order
        /// and no environment at all.
        /// </remarks>
        public static bool IsGameAssemblyName(string simpleName)
        {
            return !string.IsNullOrEmpty(simpleName)
                && string.Equals(simpleName, GameAssemblyName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The assembly whose version stamp is the game's, or the assembly this type is declared
        /// in when the process did not start as Simitone. See <see cref="IsGameAssemblyName"/>.
        /// </summary>
        /// <remarks>
        /// Declared <em>before</em> <see cref="Current"/> on purpose. Both are static field
        /// initialisers, and the compiler assigns them in the order they are written, so a
        /// <see cref="Current"/> initialiser that read this first would see null.
        /// </remarks>
        private static readonly Assembly VersionAssembly = ResolveVersionAssembly();

        /// <summary>
        /// The version of Simitone that is running, as a normalised <c>X.Y.Z</c> string.
        ///
        /// <para>
        /// Read once from assembly metadata, in this order:
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// The <em>entry</em> assembly's <c>AssemblyInformationalVersion</c> - what
        /// <c>&lt;Version&gt;</c> in <c>Simitone.Windows.csproj</c> stamps, and what a human reads
        /// off the build. This is the one that matters, and it is first because it is the only
        /// candidate that describes the shipped product rather than the library inside it. It may
        /// carry SemVer build metadata ("0.9.0+sha.abcdef"), which is stripped.
        /// </description></item>
        /// <item><description>
        /// The entry assembly's <c>AssemblyVersion</c>, whose fourth component is dropped so the
        /// result has the same three-part shape a release tag is reduced to.
        /// </description></item>
        /// <item><description>
        /// The same two candidates from the assembly this type is declared in, for a host that
        /// did not start as Simitone (a test run, a tool): those still describe a real build, and
        /// are better than nothing.
        /// </description></item>
        /// </list>
        /// <para>
        /// A candidate is only accepted once it parses as a release version, so this can never
        /// hand the caller something the update checker would reject as malformed. Falls back to
        /// <see cref="UnstampedVersion"/>.
        /// </para>
        /// </summary>
        public static string Current { get; } = ResolveCurrentVersion();

        private static Assembly ResolveVersionAssembly()
        {
            // Assembly.GetEntryAssembly can be unhappy in a host that has not finished starting;
            // it returns null for a library loaded into a process it does not recognise. A null
            // is exactly "not a Simitone entry point", so the declaring assembly is the answer.
            Assembly entry = null;
            try
            {
                entry = Assembly.GetEntryAssembly();
            }
            catch (Exception)
            {
                // Same reasoning as SimitoneVersion's own GetInformationalVersion: reflecting over
                // a partially started host is allowed to fail, and a version string is not worth
                // failing startup over.
            }

            try
            {
                if (entry != null && IsGameAssemblyName(entry.GetName().Name)) return entry;
            }
            catch (Exception)
            {
                // An assembly with no readable name is not something to believe about.
            }

            return typeof(SimitoneVersion).Assembly;
        }

        private static string ResolveCurrentVersion()
        {
            // Assembly metadata cannot change within a process, so a property initialiser is
            // both sufficient and cheap enough to read from anywhere.
            var declaring = typeof(SimitoneVersion).Assembly;

            var candidates = new[]
            {
                GetInformationalVersion(VersionAssembly),
                VersionAssembly.GetName().Version?.ToString(),
                GetInformationalVersion(declaring),
                declaring.GetName().Version?.ToString()
            };

            foreach (var candidate in candidates)
            {
                if (UpdateChecker.TryParseVersion(candidate, out var parsed)) return parsed.ToString();
            }

            return UnstampedVersion;
        }

        private static string GetInformationalVersion(Assembly assembly)
        {
            try
            {
                return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            }
            catch (Exception)
            {
                // Reflecting over the metadata of a partially loaded or trimmed assembly is
                // allowed to be unhappy. That is not a reason to fail startup over a version
                // string - the AssemblyVersion candidate is still there to try.
                return null;
            }
        }
    }
}
