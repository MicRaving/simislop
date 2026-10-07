using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Normalizes the value of Simitone's <c>-path</c> startup argument into a usable
    /// game directory (forward slashes, exactly one trailing slash).
    ///
    /// The command line is split on spaces, so the flag and its value arrive glued together
    /// as one token. All of these are accepted and mean the same directory:
    /// <code>
    ///   -path:C:\The Sims
    ///   -path:"C:\The Sims"
    ///   -path=C:\The Sims
    ///   -pathC:\The Sims
    /// </code>
    /// Previously the parser only stripped the <c>path</c> prefix and then <c>Trim('"')</c>,
    /// which left the leading <c>:</c> (or <c>=</c>) in place for every documented form —
    /// so <c>-path:"C:\The Sims"</c> resolved to <c>:"C:/The Sims/"</c>, the sentinel check
    /// failed, and the game reported the data as missing even though it was right there.
    /// </summary>
    public static class GamePathArgument
    {
        /// <summary>
        /// Normalize the text following the literal "path" in a <c>-path</c> argument.
        /// Returns null when the value is empty (caller should keep any existing path).
        /// </summary>
        public static string Normalize(string rawValueAfterFlag)
        {
            if (rawValueAfterFlag == null) return null;

            var value = rawValueAfterFlag.Trim();

            // Optional separator between the flag and the value: "-path:..." / "-path=...".
            if (value.Length > 0 && (value[0] == ':' || value[0] == '='))
            {
                value = value.Substring(1).TrimStart();
            }

            // Surrounding quotes are optional. A path with a trailing backslash before the
            // closing quote ("C:\The Sims\") would escape the quote on a real shell, but the
            // game is typically launched with the path quoted only when it contains spaces.
            value = value.Trim('"').Trim();
            if (value.Length == 0) return null;

            value = value.Replace('\\', '/');
            if (!value.EndsWith("/", StringComparison.Ordinal)) value += "/";
            return value;
        }
    }
}
