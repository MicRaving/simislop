/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics.Utils;
using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The six handgroup body-string slots (17-22) a Create-a-Sim sim is written out with, and the
    /// one place that decides which hand meshes it gets.
    ///
    /// <para>
    /// <b>Why this exists rather than six lines in the CAS screen.</b> A TS1 sim's body string
    /// names its own hands six times, and <c>FSO.Vitaboy.Outfit.Read</c> reads them as the idle,
    /// pointing and <em>closed</em> gesture of the left and right hands: 17 left open, 19 left
    /// point, 21 left fist, and the same three for the right. The mesh names are the only place
    /// the hand letter and the gesture are tied together - "H" + hand + "LO"/"RO" (open),
    /// "LP"/"RP" (point), "LC"/"RC" (closed) - so writing "LO" into slot 21 saves a sim whose left
    /// fist is drawn with their open hand. The CAS used to do exactly that, while the preview
    /// standing next to it in the same screen (<c>SetBody</c>/<c>SetFamilyMember</c>) named
    /// "hu...lc.apr": the sim looked right in CAS and came out of it with the wrong hand.
    /// </para>
    ///
    /// <para>
    /// <b>Why it delegates instead of assembling the strings.</b> These are not the CAS's to
    /// write. They are the sim's, and there is exactly one correct set of them however they are
    /// written - which is why the engine grew a single writer,
    /// <see cref="VMTS1BodyTransformHelper.HandBodyStrings"/>, shared by a grown-up sim's
    /// ChildToAdult/PetToAdult transform and by <c>VMTS1MakeNewCharacter</c> for a sim born from
    /// a lot script. A CAS-created sim is a third writer of the same six strings, so it goes
    /// through the same writer rather than keeping a third copy of the rule. The two engine
    /// writers used to disagree with each other about slot 21, and a third copy is how that class
    /// of bug comes back.
    /// </para>
    ///
    /// <para>
    /// Nothing here touches Content, a VM or the filesystem: it is a gender code and a handgroup
    /// token in, six strings out.
    /// </para>
    /// </summary>
    public static class CasHandSlots
    {
        /// <summary>
        /// The hand mesh letter for a <c>CASFamilyMember.Gender</c> code: 'm' for an adult male,
        /// 'f' for an adult female, and 'u' for a child, whose hands are the genderless "Hu"
        /// meshes.
        ///
        /// <remarks>
        /// The child's 'u' is the same rule <c>VMTS1MakeNewCharacter</c> applies - a child has no
        /// adult build, so its bodies ("b003mcchd_maur") say nothing about a hand mesh, and the
        /// game gives every child the same pair. Anything outside 0-3 is treated as a child
        /// rather than guessed at, because a hand mesh that does not exist is a sim with no hands
        /// and a wrong one is a sim wearing somebody else's.
        /// </remarks>
        /// </summary>
        public static char HandLetterFor(short genderCode)
        {
            switch (genderCode)
            {
                case 0:
                    return 'm';
                case 1:
                    return 'f';
                default:
                    return 'u';
            }
        }

        /// <summary>
        /// The six hand slots for a CAS sim, ready to be copied into a
        /// <c>SimTemplateCreateInfo.BodyStringReplace</c>.
        /// </summary>
        /// <param name="genderCode">
        /// <c>CASFamilyMember.Gender</c>: 0 adult male, 1 adult female, 2 child male, 3 child
        /// female. See <see cref="HandLetterFor"/>.
        /// </param>
        /// <param name="handgroupTex">
        /// The handgroup token <em>without</em> its "huao" prefix - what
        /// <c>CASFamilyMember.HandgroupTex</c> holds: a skin ("lgt") or a named set, which keeps
        /// its leading underscore ("_macssx"). The engine's writer re-adds the prefix, so passing
        /// a full "huaolgt" here would produce "huaohuaolgt"; pass the stored token.
        /// </param>
        public static Dictionary<int, string> BodyStringsFor(short genderCode, string handgroupTex)
        {
            return VMTS1BodyTransformHelper.HandBodyStrings(HandLetterFor(genderCode), handgroupTex);
        }
    }
}
