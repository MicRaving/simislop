/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using System.Collections.Generic;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    /// <summary>
    /// The words several live-mode panels share out of The Sims 1's own live-mode string table.
    ///
    /// <para>
    /// <c>live.dir</c>'s table 136 carries the six skill captions and the word for having no job.
    /// Three panels now want them: the job subpanel's own skill bars, the career page that hangs
    /// off its Career button, and (for its six requirement lines) anything else that lists what a
    /// job asks for. The lookup was a private method on <see cref="UIJobSubpanel"/> and is here
    /// instead, because a private method on a panel is not somewhere a second panel can reach.
    /// </para>
    ///
    /// <para>
    /// Nothing here is Simitone's own text, so nothing here goes in Simitone's own table: the
    /// strings come from the copy of the game the player owns and are already translated by
    /// whoever shipped it. The English in the second argument of every lookup is only the floor
    /// for a build whose table has no entry for that id.
    /// </para>
    /// </summary>
    public static class LivePanelStrings
    {
        /// <summary>The live-mode table the career/skill captions live in.</summary>
        public const string CareerTable = "136";

        /// <summary>Ids 1..6 of <see cref="CareerTable"/>, in bar order: cooking then creativity.</summary>
        public static readonly string[] SkillStringIds = { "1", "2", "3", "4", "5", "6" };

        /// <summary>Id of the word for having no job.</summary>
        public const string UnemployedId = "7";

        /// <summary>English captions for <see cref="SkillStringIds"/>, in the same order.</summary>
        public static readonly string[] SkillNameFallback =
        {
            "Cooking",
            "Mechanical",
            "Charisma",
            "Body",
            "Logic",
            "Creativity"
        };

        /// <summary>English for <see cref="UnemployedId"/>.</summary>
        public const string UnemployedFallback = "Unemployed";

        /// <summary>
        /// Reads one of this table's strings, falling back to the English it was written with.
        /// </summary>
        public static string Get(string id, string fallback)
        {
            var value = GameFacade.Strings?.GetString("live", CareerTable, id);
            return (value == null || value.Length == 0 || value == "***MISSING***")
                ? fallback
                : value;
        }

        /// <summary>
        /// The caption of one of the six skills, by its position in bar order.
        /// </summary>
        /// <param name="skillIndex">0 = cooking, through 5 = creativity.</param>
        public static string SkillName(int skillIndex)
        {
            if (skillIndex < 0 || skillIndex >= SkillStringIds.Length) return "";
            return Get(SkillStringIds[skillIndex], SkillNameFallback[skillIndex]);
        }

        /// <summary>The game's own word for having no job.</summary>
        public static string Unemployed()
        {
            return Get(UnemployedId, UnemployedFallback);
        }
    }
}
