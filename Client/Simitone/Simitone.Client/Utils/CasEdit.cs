/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Pure, side effect free helpers for editing an existing TS1 family in Create-a-Sim.
    ///
    /// Editing a family in place is the one CAS flow that writes over neighbourhood data that
    /// already exists: <c>SimitoneNeighbourGenerator.PrepareTemplatePerson</c> regenerates a
    /// character's IFF and the family's FAMI member list has to be rebuilt around it. Both of
    /// those steps are easy to get subtly wrong (duplicate GUIDs, a 0 GUID sneaking into a
    /// FAMI, a ".bak" file poisoning the character directory), so the decisions live here
    /// where they can be regression tested without a GraphicsDevice, Content, a VM or audio.
    ///
    /// Nothing in here touches Content, the filesystem or the neighbourhood - it is all string
    /// and array arithmetic, and none of it throws on null or malformed input.
    /// </summary>
    public static class CasEdit
    {
        /// <summary>
        /// Number of personality bars the CAS exposes for a sim. Mirrors
        /// <c>UISimCASPanel.Personalities</c> and the length of
        /// <c>SimTemplateCreateInfo.PersonalityPoints</c>.
        /// </summary>
        public const int PersonalitySlots = 5;

        /// <summary>
        /// The five personality values live at fixed offsets in the 88-short person data array.
        /// These are the exact indices <c>SimTemplateCreateInfo.MakePersonData</c> writes, in
        /// the same order, so a load/save round trip through the CAS is a no-op on the file.
        /// Note that offset 4 (generous) is deliberately absent: the generator never writes it,
        /// so a CAS-created sim always has generous 0 and there is nothing to read back.
        /// </summary>
        private static readonly int[] PersonalityPersonDataIndices = { 2, 3, 5, 6, 7 };

        /// <summary>Name of the folder CAS character backups are written to.</summary>
        public const string BackupFolderName = "CASBackups";

        /// <summary>Suffix appended to a character path to name its backup copy.</summary>
        public const string BackupSuffix = ".bak";

        /// <summary>Default skin colour code when the stored one is missing or unrecognised.</summary>
        public const string DefaultSkinColor = "lgt";

        private const string AprExtension = ".apr";

        /// <summary>
        /// Prefix every TS1 hand group texture name carries ("huaolgt", "huaoY", ...). The CAS
        /// stores the suffix only, because that is what the carousel hands out.
        /// </summary>
        private const string HandgroupTexPrefix = "huao";

        /// <summary>
        /// Merges the add/remove sets of a family edit into a new GUID list.
        ///
        /// The result keeps the original order of <paramref name="existing"/> (minus anything
        /// removed), then appends the genuinely new GUIDs from <paramref name="add"/>. It never
        /// contains a duplicate, never contains a GUID that was also asked to be removed, and
        /// never contains 0 - a 0 GUID in a FAMI is not a valid sim and would make
        /// <c>VMContext.CreateObjectInstance</c> fail for the whole family.
        ///
        /// All three arguments are optional; null is treated as empty.
        /// </summary>
        /// <param name="existing">The family's current <c>FamilyGUIDs</c>, in order.</param>
        /// <param name="add">GUIDs of sims created during the edit session.</param>
        /// <param name="remove">GUIDs of sims deleted during the edit session.</param>
        /// <returns>A fresh array; the inputs are never modified.</returns>
        public static uint[] MergeFamilyGuids(uint[] existing, uint[] add, uint[] remove)
        {
            var removed = new HashSet<uint>();
            if (remove != null)
            {
                foreach (var guid in remove)
                {
                    if (guid != 0) removed.Add(guid);
                }
            }

            var seen = new HashSet<uint>();
            var result = new List<uint>();
            AppendUnique(result, seen, removed, existing);
            AppendUnique(result, seen, removed, add);
            return result.ToArray();
        }

        private static void AppendUnique(List<uint> result, HashSet<uint> seen, HashSet<uint> removed, uint[] source)
        {
            if (source == null) return;
            foreach (var guid in source)
            {
                if (guid == 0) continue;
                if (removed.Contains(guid)) continue;
                if (!seen.Add(guid)) continue;
                result.Add(guid);
            }
        }

        /// <summary>
        /// The path a character's backup copy is written to: the character path with
        /// <see cref="BackupSuffix"/> appended.
        ///
        /// Returns null for null input rather than throwing, so a caller that failed to resolve
        /// the character file can pass whatever it has straight through.
        ///
        /// Note this is a pure name transform. The caller decides <em>where</em> the file it is
        /// given lives - see <see cref="BackupDirectoryFor"/> for why that matters.
        /// </summary>
        public static string BackupPathFor(string characterIffPath)
        {
            if (characterIffPath == null) return null;
            return characterIffPath + BackupSuffix;
        }

        /// <summary>
        /// The folder CAS character backups must be written to, given the neighbourhood's
        /// "Characters" directory.
        ///
        /// This deliberately is <em>not</em> the Characters directory itself.
        /// <c>TS1NeighborhoodProvider.LoadCharacters</c> enumerates that directory and
        /// <c>return</c>s (rather than <c>continue</c>s) the first time it sees a file whose
        /// extension is not ".iff", so a "user00001.iff.bak" sitting next to the characters would
        /// abort that loop part way through and leave some characters unregistered. A
        /// subdirectory is invisible to a top-directory-only enumeration.
        /// </summary>
        /// <returns>The backup folder path, or null when <paramref name="charactersDirectory"/> is null or empty.</returns>
        public static string BackupDirectoryFor(string charactersDirectory)
        {
            if (string.IsNullOrEmpty(charactersDirectory)) return null;
            return Path.Combine(charactersDirectory, BackupFolderName);
        }

        /// <summary>
        /// Whether a CAS family member refers to a sim that already exists in the
        /// neighbourhood, and therefore has to be rewritten rather than created.
        ///
        /// <c>CASFamilyMember.RefGUID</c> is 0 for a sim that was built inside the current edit
        /// session (the default value of the field), so "non-zero" is exactly the "was this sim
        /// explicitly opened for editing" test. 0 is never a real neighbour GUID.
        /// </summary>
        public static bool ShouldRewriteInPlace(uint refGuid)
        {
            return refGuid != 0;
        }

        /// <summary>
        /// Picks the active entry out of a TS1 body string. A body string may hold several
        /// ';'-separated alternatives and the game always uses the first, which is what
        /// <c>FSO.Vitaboy.Outfit.Read(STR)</c> does.
        /// </summary>
        /// <returns>The first alternative, or <see cref="string.Empty"/> for null/empty input.</returns>
        public static string FirstBodyStringEntry(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var idx = value.IndexOf(';');
            return (idx == -1) ? value : value.Substring(0, idx);
        }

        /// <summary>
        /// Extracts the appearance ID from a TS1 body/head string, mirroring
        /// <c>FSO.Vitaboy.Outfit.ToApr</c> but returning the bare ID the CAS stores in
        /// <c>CASFamilyMember.Body</c>/<c>Head</c> (no ".apr" suffix).
        ///
        /// "mab,BODY=mab_lgt" and "hab,HEAD-HEAD=hab_lgt" both yield "mab" and "hab". A trailing
        /// ".apr" that is already present is stripped rather than doubled up, so a value that has
        /// been through the outfit reader still round trips.
        /// </summary>
        /// <returns>The appearance ID, or <see cref="string.Empty"/> for null/empty input.</returns>
        public static string AppearanceId(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var idx = value.IndexOf(',');
            var id = ((idx == -1) ? value : value.Substring(0, idx)).Trim();
            if (id.EndsWith(AprExtension, StringComparison.OrdinalIgnoreCase))
            {
                id = id.Substring(0, id.Length - AprExtension.Length);
            }
            return id;
        }

        /// <summary>
        /// Extracts the texture ID from a TS1 body/head string: everything after the first '='.
        /// "mab,BODY=mab_lgt" yields "mab_lgt", which is the bare name without an extension -
        /// exactly what <c>CASToNeighGen</c> writes and what <c>CASFamilyMember.BodyTex</c> holds.
        /// </summary>
        /// <returns>
        /// The texture ID, <c>null</c> when there is no '=' at all (the same "no texture" signal
        /// <c>FSO.Vitaboy.Outfit.ToTex</c> gives), or an empty string when '=' is present but
        /// nothing follows it.
        /// </returns>
        public static string TextureId(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var idx = value.IndexOf('=');
            return (idx == -1) ? null : value.Substring(idx + 1).Trim();
        }

        /// <summary>
        /// Recovers <c>CASFamilyMember.HandgroupTex</c> from a stored hand gesture string.
        /// The character IFF stores the full texture name ("huaolgt"); the CAS stores only the
        /// part after the "huao" prefix, because that is what <c>CASToNeighGen</c> re-prefixes on
        /// the way out. A value without the prefix is returned unchanged (best effort).
        /// </summary>
        public static string HandgroupTexFrom(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.StartsWith(HandgroupTexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return value.Substring(HandgroupTexPrefix.Length);
            }
            return value;
        }

        /// <summary>
        /// Normalises a stored skin colour token to one of the three codes the CAS understands
        /// ("lgt", "med", "drk"). Anything missing or unrecognised falls back to
        /// <see cref="DefaultSkinColor"/>; a ';'-separated list of alternatives is reduced to
        /// its first entry, matching how the game picks from one.
        /// </summary>
        public static string SkinColorCode(string value)
        {
            if (string.IsNullOrEmpty(value)) return DefaultSkinColor;
            var code = value.Trim().ToLowerInvariant();
            var idx = code.IndexOf(';');
            if (idx != -1) code = code.Substring(0, idx);
            if (code == "lgt" || code == "med" || code == "drk") return code;
            return DefaultSkinColor;
        }

        /// <summary>
        /// The skin colour code for a numeric skin tone as stored in person data
        /// (<c>VMPersonDataVariable.SkinColor</c>: 0 light, 1 medium, 2 dark). Used as the
        /// fallback when a character IFF has no usable skin token.
        /// </summary>
        public static string SkinColorFromPersonData(short skinTone)
        {
            switch (skinTone)
            {
                case 1: return "med";
                case 2: return "drk";
                default: return DefaultSkinColor;
            }
        }

        /// <summary>
        /// Whether a person data age marks a child. Mirrors what the sim engine itself uses
        /// (<c>VMAvatar.InitBodyData</c>: <c>age == 0 || age &lt; 18</c>), so a sim that reads as
        /// a child in the lot also reads as a child in the CAS. CAS-created sims store 9 for a
        /// child and 27 for an adult.
        /// </summary>
        public static bool IsChildPersonAge(short age)
        {
            return age == 0 || age < 18;
        }

        /// <summary>
        /// The 4-value gender code <c>CASFamilyMember.Gender</c> uses: 0 adult male,
        /// 1 adult female, 2 child male, 3 child female. This is the inverse of the decode done
        /// when loading a family, so a load/save round trip is stable.
        /// </summary>
        public static short GenderCode(bool child, bool female)
        {
            return (short)((female ? 1 : 0) | (child ? 2 : 0));
        }

        /// <summary>
        /// Reads the five CAS personality values out of a neighbour's 88-short person data array,
        /// in the order <c>SimTemplateCreateInfo.MakePersonData</c> writes them, so what the CAS
        /// shows is exactly what it would write back.
        /// </summary>
        /// <returns>
        /// A new 5 element array. Null or short input yields zeros rather than throwing - a sim
        /// with no personality data is still editable, it just starts neutral.
        /// </returns>
        public static short[] PersonalityFromPersonData(short[] personData)
        {
            var result = new short[PersonalitySlots];
            if (personData == null) return result;
            for (int i = 0; i < PersonalitySlots; i++)
            {
                var index = PersonalityPersonDataIndices[i];
                if (index >= 0 && index < personData.Length) result[i] = personData[index];
            }
            return result;
        }
    }
}
