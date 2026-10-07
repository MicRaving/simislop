/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics;
using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Recovers the interaction number a pie-menu entry actually names, after the pie menu has
    /// flattened a 32-bit <c>TTAB</c> index down to 8 bits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The round trip, and where it breaks.</b> A <c>TTAB</c> entry's identity is
    /// <c>TTABInteraction.TTAIndex</c>, a <see cref="uint"/>, and <c>TTAB.InteractionByIndex</c> is
    /// keyed by it. The pie menu carries that number to the player as
    /// <c>VMPieMenuInteraction.ID</c>, which is a <see cref="byte"/>, and the player sends it back
    /// as <c>VMNetInteractionCmd.Interaction</c>, a <see cref="ushort"/>. The engine then resolves
    /// it with <c>VMEntity.GetAction</c>, which indexes <c>InteractionByIndex</c> by
    /// <c>(uint)interaction</c> - the full 32-bit value, unchanged.
    /// </para>
    /// <para>
    /// So the <b>send</b> side is 8-bit and the <b>receive</b> side is 32-bit, and anything above
    /// 255 is destroyed in between: index 300 arrives as 44 (300 &amp; 0xFF), and
    /// <c>GetAction(44)</c> returns a <em>different</em> interaction or null. This is the same
    /// defect the E-25 audit recorded in <c>VMActionCallback</c> - "interaction 300 aliases 44 and
    /// every interaction from 256 up aliases onto the one 256 below it" - fixed there and left
    /// here, on the path the player actually uses.
    /// </para>
    /// <para>
    /// <b>Why a cooked dinner is the thing that breaks.</b> A Cooked Dinner is a contained object
    /// in a counter's slot, and it is clicked the same way as anything else: the pie menu is built
    /// from the object's own tree table and the chosen entry is sent straight back. The dinner's
    /// own interactions are not the ones with low indices - the food interactions live high in the
    /// table - so "Eat" is exactly the entry whose number does not survive the trip. A click
    /// resolves to the wrong interaction, the engine's check tree refuses it, and the player sees
    /// nothing at all: the failure is silent at every layer, which is why it reads as "the eat
    /// action is completely broken" rather than as a wrong-interaction bug.
    /// </para>
    /// <para>
    /// <b>The fix.</b> <c>VMPieMenuInteraction</c> carries the whole
    /// <see cref="FSO.Files.Formats.IFF.Chunks.TTABInteraction"/> as
    /// <c>VMPieMenuInteraction.Entry</c>, and that is the untruncated record - it is assigned
    /// alongside the byte on every path that builds a pie entry. So the number to send is
    /// <see cref="Resolve"/>d from <c>Entry.TTAIndex</c> when an entry is present, and the byte is
    /// only a fallback for an entry with no <c>Entry</c> (which autonomy's synthetic entries and a
    /// hand-built pie both produce). Widening <c>VMPieMenuInteraction.ID</c> itself is the real
    /// repair, but that field is written from the engine and read by the net protocol, so this
    /// keeps the change on the client where the truncation is introduced.
    /// </para>
    /// <para>
    /// Pure: the whole decision is "which of these two numbers is the interaction", with no VM, no
    /// graphics device and no IO. See
    /// <c>Client/Simitone/Simitone.Tests/UI/InteractionIdTests.cs</c>.
    /// </para>
    /// </remarks>
    public static class InteractionId
    {
        /// <summary>
        /// The widest interaction number the client can name: the width of
        /// <c>VMNetInteractionCmd.Interaction</c>, and the width the engine's own
        /// <c>InteractionByIndex</c> lookup preserves.
        /// </summary>
        public const int MaxInteraction = ushort.MaxValue;

        /// <summary>
        /// The interaction number to send for a pie-menu entry.
        /// </summary>
        /// <param name="option">
        /// The pie-menu entry. May be null, in which case 0 is returned. Note that 0 is also a real
        /// interaction number - the first row of a tree table - so a null entry and a row numbered
        /// zero both answer 0. That is safe: the engine resolves an interaction with
        /// <c>InteractionByIndex[(uint)interaction]</c> and only a missing row fails, so sending 0
        /// runs the row numbered zero rather than nothing.
        /// </param>
        /// <returns>
        /// The entry's <c>TTAIndex</c> when the entry carries its table row, otherwise the entry's
        /// (8-bit) id widened. Always in <c>[0, 65535]</c>.
        /// </returns>
        public static int Resolve(VMPieMenuInteraction option)
        {
            if (option == null) return 0;

            // The table row is the authority: TTAIndex is what InteractionByIndex is keyed by, and
            // it is the number the entry was built from in the first place.
            if (option.Entry != null) return Clamp(option.Entry.TTAIndex);

            // No row - autonomy's motive-ad placeholder entry, or a pie assembled by hand. The byte
            // is all there is, and widening it is strictly better than reading it as a signed or
            // 32-bit value would be.
            return Clamp(option.ID);
        }

        /// <summary>
        /// Whether the pie menu may send this interaction number.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Zero is a real interaction number, not "nothing": the engine resolves it with
        /// <c>InteractionByIndex[(uint)interaction]</c>, and the first row of a tree table is
        /// numbered 0. A "Use" entry is the common one, and refusing it is what made clicking that
        /// entry queue no interaction at all.
        /// </para>
        /// <para>
        /// Only a value the wire cannot carry is refused. <see cref="Resolve"/> already clamps to
        /// <c>[0, MaxInteraction]</c>, so this is the last line of defence rather than a filter.
        /// </para>
        /// </remarks>
        public static bool IsSendable(int interaction)
        {
            return interaction >= 0 && interaction <= MaxInteraction;
        }

        /// <summary>
        /// Whether a <c>TTAIndex</c> survives the pie menu's 8-bit <c>ID</c> field. False for
        /// anything at or above 256, which is what <see cref="Resolve"/> exists to repair.
        /// </summary>
        public static bool SurvivesByteField(uint ttaIndex)
        {
            return ttaIndex <= byte.MaxValue;
        }

        /// <summary>
        /// Whether a pie-menu entry's number was altered on its way to the client - i.e. the entry
        /// carries a table row whose index does not fit the byte the pie stored. True means a
        /// client that sends <c>VMPieMenuInteraction.ID</c> would run a different interaction than
        /// the one the player picked.
        /// </summary>
        public static bool WasTruncated(VMPieMenuInteraction option)
        {
            if (option?.Entry == null) return false;
            return option.Entry.TTAIndex != Clamp(option.Entry.TTAIndex) || !SurvivesByteField(option.Entry.TTAIndex);
        }

        private static int Clamp(uint value)
        {
            return value > MaxInteraction ? MaxInteraction : (int)value;
        }
    }
}
