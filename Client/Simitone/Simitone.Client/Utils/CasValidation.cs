/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Small, side-effect free validation helpers for the TS1 Create-a-Sim screen.
    ///
    /// These exist because the CAS screen used to assume its data was always well formed and
    /// indexed/substring-ed straight out of content-derived strings. A single malformed content
    /// entry (or a user who typed a 200 character family name) reached
    /// <c>TS1CASScreen.CASToNeighGen</c> via a button click and threw out of a UI callback.
    /// Every rule the screen needs is pure static logic, so it lives here where it can be
    /// regression tested without a GraphicsDevice, Content or audio.
    /// </summary>
    public static class CasValidation
    {
        /// <summary>
        /// The hard cap on the number of sims that can be in a single TS1 family. The FAMI/FAM
        /// record and the underlying game data cannot represent more, and the neighbourhood
        /// generator would happily write them.
        /// </summary>
        public const int MaxSimsPerFamily = 8;

        /// <summary>
        /// The maximum length accepted for a sim or family name. Anything longer is truncated on
        /// the way in rather than being written out to the neighbourhood file.
        /// </summary>
        public const int MaxNameLength = 32;

        /// <summary>
        /// Extracts the 3 character body type code from a TS1 body string of the form
        /// <c>"mab_body"</c> (the code sits immediately before the first underscore).
        ///
        /// The original inline code was <c>body.Substring(body.IndexOf("_") - 3, 3)</c>, which
        /// throws <see cref="ArgumentOutOfRangeException"/> both when there is no underscore at
        /// all (<c>IndexOf</c> returns -1) and when the prefix is shorter than 3 characters. This
        /// is best effort and total: it never throws, and returns a short/empty string when the
        /// input is too malformed to yield a real code.
        /// </summary>
        /// <param name="body">The content-derived body string. May be null or empty.</param>
        /// <returns>
        /// The 3 characters preceding the first underscore, or the first 3 characters of the
        /// input when there is no usable underscore position, or <see cref="string.Empty"/> for
        /// null/empty input.
        /// </returns>
        public static string BodyTypeCode(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            var ind = body.IndexOf('_');
            //no underscore, or fewer than 3 characters before it: fall back to a best-effort prefix.
            if (ind < 3) return body.Substring(0, Math.Min(3, body.Length));
            return body.Substring(ind - 3, 3);
        }

        /// <summary>
        /// Whether another sim may be appended to a family that currently holds
        /// <paramref name="currentCount"/> members. Negative counts are rejected rather than
        /// clamped, so a corrupted list cannot slip through the cap check.
        /// </summary>
        public static bool CanAddFamilyMember(int currentCount)
        {
            return currentCount >= 0 && currentCount < MaxSimsPerFamily;
        }

        /// <summary>
        /// Whether a family name is usable: non-null, not blank once trimmed, and within
        /// <see cref="MaxNameLength"/>.
        /// </summary>
        public static bool IsValidFamilyName(string name)
        {
            if (name == null) return false;
            if (name.Trim().Length == 0) return false;
            return name.Length <= MaxNameLength;
        }

        /// <summary>
        /// Trims surrounding whitespace and truncates to <see cref="MaxNameLength"/>. Returns
        /// <see cref="string.Empty"/> for null input rather than propagating it.
        /// </summary>
        public static string SanitizeSimName(string name)
        {
            if (name == null) return "";
            var trimmed = name.Trim();
            if (trimmed.Length > MaxNameLength) trimmed = trimmed.Substring(0, MaxNameLength);
            return trimmed;
        }

        /// <summary>
        /// Trims surrounding whitespace and truncates to <see cref="MaxNameLength"/>. Returns
        /// <see cref="string.Empty"/> for null input rather than propagating it.
        /// </summary>
        public static string SanitizeFamilyName(string name)
        {
            if (name == null) return "";
            var trimmed = name.Trim();
            if (trimmed.Length > MaxNameLength) trimmed = trimmed.Substring(0, MaxNameLength);
            return trimmed;
        }
    }
}
