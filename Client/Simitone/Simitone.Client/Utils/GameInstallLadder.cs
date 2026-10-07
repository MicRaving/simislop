using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The single authority for <i>which</i> The Sims installation Simitone should use, in what
    /// order it should be looked for, and what to do when none of them exist.
    ///
    /// <para>
    /// The ordering itself used to be an inline chain of <c>if</c>s in
    /// <c>WindowsLocator.FindTheSims1</c>, interleaved with live
    /// <see cref="Microsoft.Win32.Registry"/> reads. That made the only interesting part of the
    /// logic - the fallback policy, i.e. what happens on the overwhelmingly common case of a
    /// machine with no Sims installed - impossible to exercise without a populated registry, and
    /// therefore untested. Splitting the policy from the probes turns it into a pure function over
    /// three "return null if not found" delegates.
    /// </para>
    ///
    /// <para>
    /// Precedence, highest first: a portable <c>../The Sims/</c> folder next to the executable
    /// (the same convention the macOS and Linux locators use), the classic Maxis installer entry
    /// under the registry, the Steam Legacy Collection, and finally the hard-coded retail install
    /// directory. The last rung is not optional: the caller boots on the returned string, so a
    /// null here would have to be turned into a second, differently-shaped failure path.
    /// </para>
    /// </summary>
    public static class GameInstallLadder
    {
        /// <summary>
        /// The last-resort install directory. Returned verbatim, already in the forward-slash,
        /// trailing-slash form every other rung produces, so the caller never has to normalise it.
        /// </summary>
        public const string DefaultInstallDir = "C:/Program Files (x86)/Maxis/The Sims/";

        /// <summary>
        /// Returns the first install directory that a probe produces, or the default retail path
        /// if none of them produce one.
        /// </summary>
        /// <param name="relativeDir">
        /// A portable install directory (for example <c>../The Sims/</c>), or null when there is
        /// none. Returned exactly as given - it is a developer's own folder, so it is not
        /// rewritten or validated here.
        /// </param>
        /// <param name="maxisRegistryLookup">
        /// Probes the Maxis installer's <c>InstallPath</c>. Invoked only if
        /// <paramref name="relativeDir"/> is null. Return null when the key or value is absent.
        /// </param>
        /// <param name="steamLookup">
        /// Probes Steam for the Legacy Collection. Invoked only if both of the above are null.
        /// Return null when Steam or the game is not installed.
        /// </param>
        /// <returns>
        /// A directory with forward slashes and exactly one trailing slash - the form
        /// <c>GamePathArgument.Normalize</c> and <c>FSOEnvironment</c> both expect - or
        /// <see cref="DefaultInstallDir"/> when nothing was found.
        /// </returns>
        /// <remarks>
        /// Probes are lazy and short-circuiting: a hit on a higher rung means the lower probes are
        /// never invoked, so a Steam probe cannot cost a registry walk and vice versa.
        ///
        /// The probes are responsible for both the probing <i>and</i> the normalising, and they
        /// must signal "not found" with null. Any non-null result is treated as a hit and is
        /// returned as-is, without a second opinion: the ladder deliberately has no opinion about
        /// what a valid path looks like, so a probe that answers with an empty string has answered
        /// the question it was asked. Keeping that rule here means the ordering policy cannot
        /// acquire a hidden second layer of validation that the tests do not cover.
        /// </remarks>
        public static string FirstHit(string relativeDir, Func<string> maxisRegistryLookup, Func<string> steamLookup)
        {
            if (relativeDir != null) return relativeDir;
            if (maxisRegistryLookup != null)
            {
                var maxis = maxisRegistryLookup();
                if (maxis != null) return maxis;
            }
            if (steamLookup != null)
            {
                var steam = steamLookup();
                if (steam != null) return steam;
            }
            return DefaultInstallDir;
        }
    }
}
