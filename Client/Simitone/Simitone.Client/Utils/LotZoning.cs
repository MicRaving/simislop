/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The pure rules behind "rezone this lot": how a lot's zone is stored, how it is read back,
    /// and when a zoning line may be rewritten.
    ///
    /// <para>
    /// A TS1 neighbourhood keeps its zoning in <c>&lt;UserData&gt;/LotZoning.iff</c>, STR chunk 1,
    /// one line per entry:
    /// </para>
    /// <code>
    /// 21, residential
    /// 22, community
    /// </code>
    /// <para>
    /// <b>Everything here matches what <c>TS1NeighborhoodProvider.InitSpecific</c> does to that
    /// file</b>, because that is the only thing that reads it, and a rezone that the loader would
    /// not believe is not a rezone. The provider parses every line with
    /// <c>line.Split(',')</c>, keys the dictionary on <c>short.Parse(split[0])</c> and stores
    /// <c>split[1] == " community" ? 1 : 0</c>. Two consequences are load-bearing:
    /// </para>
    /// <list type="bullet">
    /// <item><b>The leading space is part of the token.</b> Splitting on the comma leaves
    /// <c>" community"</c> with its space, so the comparison is against the spaced token. A
    /// line written as <c>"22,community"</c> - or <c>"22, Community"</c>, or a capital C - reads
    /// back as <em>residential</em>, and would do so forever. <see cref="CommunityToken"/> is
    /// therefore the single place the written text is defined.</item>
    /// <item><b>The file is not only zones.</b> The same STR also carries the camera coordinates
    /// of the ten original lots ("<c>2, 636, 102</c>"), a resident GUID, and - for lots 50 to 98 -
    /// a <em>second</em> copy of the zoning lines, which is the copy the game actually reads. The
    /// provider does not care what a line means beyond the split, so <em>every one of those lines
    /// writes a zone into the dictionary</em> and the last line wins. Rewriting the wrong copy, or
    /// a line that a camera coordinate later overrules, would be a rezone that reverted on the
    /// next launch - see <see cref="RezoneDecision.ShadowedEntry"/>.</item>
    /// </list>
    ///
    /// <para>
    /// The tolerance here is a deliberate difference from the provider, in the safe direction. The
    /// provider would throw on a line with no comma or a non-numeric lot; <see cref="TryParse"/>
    /// reports such a line as unreadable and the rewrite leaves it exactly as it found it. A
    /// zoning line that cannot be understood is never a line to overwrite.
    /// </para>
    ///
    /// <para>
    /// Nothing in this class touches the disk or the game state. Turning a plan into a written
    /// <c>LotZoning.iff</c> and a new <c>ZoningDictionary</c> entry is
    /// <c>NeighborhoodService.RezoneLot</c>.
    /// </para>
    /// </summary>
    public static class LotZoning
    {
        /// <summary>Zone value for a lot a family may move into. Written as " residential".</summary>
        public const short ZoneResidential = 0;

        /// <summary>Zone value for a community lot. Written as " community".</summary>
        public const short ZoneCommunity = 1;

        /// <summary>
        /// The zone value the lot panel shows for a StudioTown lot, which is "not residential"
        /// without being community. It only exists as a display fallback - see
        /// <see cref="DefaultZoneForLot"/> - and is never written to the file.
        /// </summary>
        public const short ZoneStudio = 2;

        /// <summary>The zone name for a lot a family may move into.</summary>
        public const string ResidentialName = "residential";

        /// <summary>The zone name for a community lot.</summary>
        public const string CommunityName = "community";

        /// <summary>
        /// The exact text a residential zoning line ends with, leading space included. The space
        /// is the delimiter that survived the provider's split, not decoration.
        /// </summary>
        public const string ResidentialToken = " " + ResidentialName;

        /// <summary>
        /// The exact text a community zoning line ends with, leading space included. This is the
        /// literal the provider compares against, so it is the whole definition of "community".
        /// </summary>
        public const string CommunityToken = " " + CommunityName;

        /// <summary>First lot of the StudioTown range, which <see cref="DefaultZoneForLot"/> keys on.</summary>
        private const int StudioFirstLot = 81;

        /// <summary>Last lot of the StudioTown range, which <see cref="DefaultZoneForLot"/> keys on.</summary>
        private const int StudioLastLot = 89;

        /// <summary>
        /// Whether <paramref name="zone"/> is one of the two values the file can hold.
        /// </summary>
        /// <remarks>
        /// A caller that wants to record some other value - the panel's
        /// <see cref="ZoneStudio"/> display fallback, say - cannot put it in the file, so this is
        /// the gate in front of a write rather than a nicety.
        /// </remarks>
        /// <param name="zone">The zone value to check.</param>
        public static bool IsKnownZone(short zone)
        {
            return zone == ZoneResidential || zone == ZoneCommunity;
        }

        /// <summary>
        /// The token a zone value is written as, matching what the provider compares against.
        /// </summary>
        /// <remarks>
        /// Anything that is not <see cref="ZoneCommunity"/> is written as residential, so a caller
        /// that skipped <see cref="IsKnownZone"/> writes a residential line rather than nonsense.
        /// </remarks>
        /// <param name="zone">The zone value to render.</param>
        /// <returns>" community" for community, otherwise " residential".</returns>
        public static string ZoneToken(short zone)
        {
            return zone == ZoneCommunity ? CommunityToken : ResidentialToken;
        }

        /// <summary>
        /// The whole line for a lot at a given zone: <c>"&lt;lot&gt;,&lt;token&gt;"</c>.
        /// </summary>
        /// <remarks>
        /// Written invariantly, and <see cref="TryParse"/> reads on the current culture. The two
        /// are not interchangeable, so this is worth saying out loud: <b>do not "fix" one without
        /// the other.</b> The provider is the authority this pair mirrors, and it parses with
        /// <c>short.Parse</c>, which is the current culture - so the reader has to be too. The
        /// writer can be invariant because .NET formats a positive integer with ASCII digits in
        /// every culture, so the line it produces is one the provider's culture-whatever parse
        /// reads identically. That is what makes the round trip culture-independent rather than
        /// a latent disagreement, and
        /// <c>LotZoningCultureTests</c> is what holds that claim up.
        /// </remarks>
        /// <param name="lot">The lot number.</param>
        /// <param name="zone">The zone the lot should have.</param>
        public static string Format(int lot, short zone)
        {
            return lot.ToString(CultureInfo.InvariantCulture) + "," + ZoneToken(zone);
        }

        /// <summary>
        /// Reads one line the way the provider reads it.
        /// </summary>
        /// <remarks>
        /// A line counts as readable when it has a comma and the text before the comma is a
        /// <see cref="short"/>. Everything after the first comma is the zone token, so a
        /// coordinate line ("2, 636, 102") reads as a residential lot 2 - which is exactly what
        /// the provider does with it, and why <see cref="IsZoningEntry"/> exists separately.
        /// </remarks>
        /// <param name="line">The raw line, or <c>null</c>.</param>
        /// <param name="lot">The lot number the line is about.</param>
        /// <param name="zone"><see cref="ZoneCommunity"/> when the token is exactly
        /// <see cref="CommunityToken"/>, otherwise <see cref="ZoneResidential"/>.</param>
        /// <returns><c>false</c> for a line the provider would have thrown on.</returns>
        public static bool TryParse(string line, out short lot, out short zone)
        {
            lot = 0;
            zone = ZoneResidential;
            if (line == null) return false;

            var split = line.Split(',');
            if (split.Length < 2) return false;
            //short.Parse's own rules, minus the throw: NumberStyles.Integer on the current
            //culture, so a line the provider can read is a line this can read. Deliberately NOT
            //invariant, and deliberately not to be made so on its own - see Format's remarks.
            if (!short.TryParse(split[0], NumberStyles.Integer, CultureInfo.CurrentCulture, out lot)) return false;

            zone = split[1] == CommunityToken ? ZoneCommunity : ZoneResidential;
            return true;
        }

        /// <summary>
        /// Whether a line is a zoning entry - as opposed to one of the other lines the same chunk
        /// carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// True for a two-field line whose second field is one of the two zone names. The name is
        /// matched without its leading whitespace, so Maxis' hand-edited "90,residential" - one
        /// line in the shipped file with the delimiter space missing - counts as a zoning entry.
        /// It states a zone for lot 90, it is the line the game reads for that lot, and a player
        /// who rezones lot 90 should not be told the lot is unrezonable over a missing space.
        /// </para>
        ///
        /// <para>
        /// Everything else is not. A camera coordinate line ("2, 636, 102") is three fields and
        /// its second field is a number, so it is not a statement about a zone - though it is
        /// still a line the provider reads, which is what
        /// <see cref="RezoneDecision.ShadowedEntry"/> is about.
        /// </para>
        /// </remarks>
        /// <param name="line">The raw line, or <c>null</c>.</param>
        public static bool IsZoningEntry(string line)
        {
            if (!TryParse(line, out _, out _)) return false;
            var split = line.Split(',');
            return split.Length == 2 && IsZoneName(split[1]);
        }

        /// <summary>
        /// Whether a line's second field names a zone, however it is spaced.
        /// </summary>
        /// <remarks>
        /// The only thing that stops this from accepting a camera coordinate is that "636" is not
        /// one of the two names.
        /// </remarks>
        /// <param name="zoneField">The text after the first comma.</param>
        public static bool IsZoneName(string zoneField)
        {
            if (zoneField == null) return false;
            var name = zoneField.TrimStart();
            return name == ResidentialName || name == CommunityName;
        }

        /// <summary>
        /// Finds the line that states a lot's zone.
        /// </summary>
        /// <remarks>
        /// The <em>last</em> zoning entry for the lot, because that is the one the provider ends
        /// up with if a lot somehow has more than one. A lot the file never mentions has no
        /// index, and <c>false</c> comes back - which is the "unknown lot" case, and the reason
        /// <c>RezoneLot</c> refuses rather than appending a line to a fixed-size, index-addressed
        /// table.
        /// </remarks>
        /// <param name="lines">Every line of the zoning chunk, in file order.</param>
        /// <param name="lot">The lot number to look for.</param>
        /// <param name="index">Index of the line, or -1 when the lot has none.</param>
        /// <param name="zone">The zone that line states.</param>
        public static bool TryGetZoningEntry(IReadOnlyList<string> lines, int lot, out int index, out short zone)
        {
            index = -1;
            zone = ZoneResidential;
            if (lines == null) return false;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (TryParse(lines[i], out var lineLot, out var lineZone) && lineLot == lot && IsZoningEntry(lines[i]))
                {
                    index = i;
                    zone = lineZone;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The zone the provider will hold for a lot once it has read the file, or
        /// <c>null</c> when no line mentions the lot at all.
        /// </summary>
        /// <remarks>
        /// Last line wins, exactly as the provider's loop assigns. This is the value the rest of
        /// the game sees, so it - not the zoning entry - is what a rezone has to move.
        /// </remarks>
        /// <param name="lines">Every line of the zoning chunk, in file order.</param>
        /// <param name="lot">The lot number to resolve.</param>
        public static short? EffectiveZone(IReadOnlyList<string> lines, int lot)
        {
            if (lines == null) return null;
            short? result = null;
            for (int i = 0; i < lines.Count; i++)
            {
                if (TryParse(lines[i], out var lineLot, out var lineZone) && lineLot == lot)
                {
                    result = lineZone;
                }
            }
            return result;
        }

        /// <summary>
        /// The zone the lot panel shows for a lot the file says nothing about.
        /// </summary>
        /// <remarks>
        /// The lot panel needs a value for every lot it can open, and most lots it can open are
        /// not in the file at all - the Downtown, Vacation, Studio and Magic lots have no zoning
        /// line, and neither does residential lot 11. StudioTown is the one range that gets its
        /// own non-residential value; everything else falls back to community, which is what the
        /// panel has always shown for a lot with no entry.
        /// </remarks>
        /// <param name="houseId">The lot number.</param>
        public static short DefaultZoneForLot(int houseId)
        {
            return (houseId >= StudioFirstLot && houseId <= StudioLastLot) ? ZoneStudio : ZoneCommunity;
        }

        /// <summary>
        /// Decides what a rezone request should do to the zoning chunk, and with which line.
        /// </summary>
        /// <remarks>
        /// Pure: it reads the lines and returns a plan. Applying the plan (writing the file,
        /// updating the dictionary) is the caller's job - see
        /// <see cref="RezonePlan.ApplyTo"/>.
        /// </remarks>
        /// <param name="lines">Every line of the zoning chunk, in file order.</param>
        /// <param name="lot">The lot being rezoned.</param>
        /// <param name="zone">The zone it is being moved to.</param>
        /// <param name="occupied">Whether a family lives on the lot. An occupied lot is refused:
        /// a lot's zone decides whether a family may move into it, so changing it under a family
        /// is a change the family never agreed to - the original game made the player evict
        /// first, and so does this.</param>
        public static RezonePlan Rezoned(IReadOnlyList<string> lines, int lot, short zone, bool occupied)
        {
            //order matters and is the whole contract: the cheap refusals first, then "is this
            //lot in the file", then "would this edit survive a reload", then "is there anything to
            //do". A refusal for a lot that is not in the file is the same refusal whichever zone
            //was asked for, so it does not matter which of the first two comes first - but both
            //do come before the shadow check, which is only meaningful for a lot that is in the
            //file.
            if (occupied) return RezonePlan.Refused(RezoneDecision.Occupied);
            if (!IsKnownZone(zone)) return RezonePlan.Refused(RezoneDecision.UnknownZone);

            if (!TryGetZoningEntry(lines, lot, out var index, out var current))
            {
                return RezonePlan.Refused(RezoneDecision.NoZoningEntry);
            }

            //Anything after the zoning entry that also mentions this lot wins on the next load,
            //so editing the zoning entry would be undone the next time the game starts. The ten
            //camera coordinate lines are what do this, for the lots they are about. Refusing is
            //the only honest answer: the alternative is a rezone that looks like it worked and
            //silently reverts.
            for (int i = index + 1; i < lines.Count; i++)
            {
                if (TryParse(lines[i], out var laterLot, out _) && laterLot == lot)
                {
                    return RezonePlan.Refused(RezoneDecision.ShadowedEntry);
                }
            }

            if (current == zone) return RezonePlan.Unchanged(index, zone);

            return RezonePlan.Writing(index, Format(lot, zone), zone);
        }

        /// <summary>
        /// What a rezone request resolved to. See <see cref="Rezoned"/>.
        /// </summary>
        public enum RezoneDecision
        {
            /// <summary>The line exists and must be rewritten. The only outcome that writes.</summary>
            Write,

            /// <summary>The lot already has the requested zone; nothing to write.</summary>
            Unchanged,

            /// <summary>A family lives on the lot. Evict first.</summary>
            Occupied,

            /// <summary>No line in the file states this lot's zone.</summary>
            NoZoningEntry,

            /// <summary>
            /// A later line about the same lot decides its zone, so rewriting the zoning entry
            /// would be undone on the next load.
            /// </summary>
            ShadowedEntry,

            /// <summary>The requested zone is not one the file can hold.</summary>
            UnknownZone,
        }

        /// <summary>
        /// The result of <see cref="Rezoned"/>: what to do, which line to change it on, and what
        /// the lot will read as afterwards.
        /// </summary>
        /// <remarks>
        /// A plan with <see cref="RezoneDecision.Write"/> carries the replacement line;
        /// <see cref="RezoneDecision.Unchanged"/> carries the line it decided against, because that
        /// is the line a later rezone would change; every refusal carries neither and reports an
        /// index of -1. <see cref="ApplyTo"/> is the only thing that touches the lines, and it
        /// changes exactly the one the plan names.
        /// </remarks>
        public readonly struct RezonePlan
        {
            /// <summary>What to do with the chunk.</summary>
            public RezoneDecision Decision { get; }

            /// <summary>Index of the line to replace, or -1 for a refusal.</summary>
            public int Index { get; }

            /// <summary>
            /// The replacement line, or <c>null</c> unless <see cref="Decision"/> is
            /// <see cref="RezoneDecision.Write"/>.
            /// </summary>
            public string Line { get; }

            /// <summary>
            /// The zone the lot will read as after the plan is applied - which for
            /// <see cref="RezoneDecision.Unchanged"/> is the zone it already has.
            /// </summary>
            public short Zone { get; }

            private RezonePlan(RezoneDecision decision, int index, string line, short zone)
            {
                Decision = decision;
                Index = index;
                Line = line;
                Zone = zone;
            }

            /// <summary>True when applying this plan writes the file.</summary>
            public bool WritesFile
            {
                get { return Decision == RezoneDecision.Write; }
            }

            /// <summary>
            /// A refusal: there is no line to change, so there is nothing to report an index or a
            /// zone for.
            /// </summary>
            /// <param name="decision">The outcome to report.</param>
            internal static RezonePlan Refused(RezoneDecision decision)
            {
                return new RezonePlan(decision, -1, null, ZoneResidential);
            }

            /// <summary>The lot is already zoned this way.</summary>
            /// <param name="index">The zoning line found.</param>
            /// <param name="zone">The zone it states.</param>
            internal static RezonePlan Unchanged(int index, short zone)
            {
                return new RezonePlan(RezoneDecision.Unchanged, index, null, zone);
            }

            /// <summary>Replace the line at <paramref name="index"/> with <paramref name="line"/>.</summary>
            /// <param name="index">The zoning line to replace.</param>
            /// <param name="line">Its replacement.</param>
            /// <param name="zone">The zone the replacement states.</param>
            internal static RezonePlan Writing(int index, string line, short zone)
            {
                return new RezonePlan(RezoneDecision.Write, index, line, zone);
            }

            /// <summary>
            /// The chunk's lines with this plan applied: the same length, the same entries
            /// everywhere except <see cref="Index"/>, which becomes <see cref="Line"/>.
            /// </summary>
            /// <remarks>
            /// Untouched entries are the same references, not copies: a rezone rewrites one line
            /// of a 1000-line table that also holds the map's camera coordinates, and a rewrite
            /// that normalised, trimmed or reordered anything else in that table would be a change
            /// to the neighbourhood that nobody asked for.
            /// </remarks>
            /// <param name="lines">The chunk's current lines.</param>
            /// <returns>The new lines, or <c>null</c> when this plan does not write.</returns>
            public string[] ApplyTo(IReadOnlyList<string> lines)
            {
                if (!WritesFile) return null;
                if (lines == null) throw new ArgumentNullException(nameof(lines));
                if (Index < 0 || Index >= lines.Count) throw new ArgumentOutOfRangeException(nameof(lines));

                var rewritten = new string[lines.Count];
                for (int i = 0; i < lines.Count; i++)
                {
                    rewritten[i] = lines[i];
                }
                rewritten[Index] = Line;
                return rewritten;
            }
        }
    }
}
