/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
//for the DialogService the switch confirm goes through, named in the remarks on AskForOpen
using Simitone.Client.UI.Services;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Simitone.Client.UI.Panels
{
    /// <summary>
    /// What the management page knows about one slot while it is deciding whether "Open" may be
    /// pressed on it, gathered into a value so the decision is a pure function of a value.
    ///
    /// <para>
    /// Every field is a fact about the running session or about the disk, and all of them are
    /// read by the page: the open slot id from the provider's own <c>UserPath</c>, the dirty count
    /// from the provider's dirty-avatar set, the two existence flags from the slot folder. Nothing
    /// here reaches for a content manager or a <c>UIElement</c>, which is what lets the whole
    /// state table be asserted in <c>NeighbourhoodManagementPanelTests</c> without a graphics
    /// device.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodOpenFacts
    {
        /// <summary>The slot the player picked in the list.</summary>
        public readonly int Id;

        /// <summary>
        /// Whether the slot has a folder in the user directory. Only a slot with one can be
        /// opened, because only a slot with one is a neighbourhood rather than a free space.
        /// </summary>
        public readonly bool Exists;

        /// <summary>
        /// Whether that folder holds a <c>Neighborhood.iff</c> - i.e. whether it is a
        /// neighbourhood the provider can open at all.
        ///
        /// <para>
        /// This is the check the engine provider does <em>not</em> make: it opens the file on the
        /// first line of its load and throws if it is not there, so a folder that is not a real
        /// neighbourhood is otherwise discovered by crashing half way through a switch. Reading it
        /// here is what lets a bad slot be refused with the running session untouched.
        /// </para>
        /// </summary>
        public readonly bool IsOpenable;

        /// <summary>
        /// The slot the running session has open, or <c>null</c> when nothing is known to be.
        /// <c>null</c> is not "nothing is open": it is "we could not work it out", which is a
        /// state a switch refuses rather than guesses at.
        /// </summary>
        public readonly int? LoadedSlotId;

        /// <summary>
        /// Whether a lot is loaded. Switching underneath one would swap out the data the lot was
        /// built from while its VM is still holding it, so the switch is refused instead of
        /// attempted - see the remark on <c>OpenRefusal</c>.
        /// </summary>
        public readonly bool InLot;

        /// <summary>
        /// Whether a family is waiting to move in. The next lot picked after a switch would be
        /// bought for a family of the neighbourhood being closed, so the switch is refused.
        /// </summary>
        public readonly bool MoveInPending;

        /// <summary>
        /// How many avatars in the open neighbourhood have changes that have not been written -
        /// the provider's own dirty set, which <c>InitSpecific</c> clears without asking. Zero
        /// means the confirm has no reason to offer a save.
        /// </summary>
        public readonly int DirtyAvatars;

        /// <summary>
        /// Gathers the facts behind one "may this slot be opened?" question. The page reads every
        /// one of them off the running session; nothing here reaches for a disk or a content
        /// manager.
        /// </summary>
        /// <param name="id">The slot the player picked.</param>
        /// <param name="exists">Whether the slot has a folder.</param>
        /// <param name="isOpenable">Whether that folder holds a <c>Neighborhood.iff</c>.</param>
        /// <param name="loadedSlotId">The open slot, or <c>null</c> when unknown.</param>
        /// <param name="inLot">Whether a lot is loaded.</param>
        /// <param name="moveInPending">Whether a family is waiting to move in.</param>
        /// <param name="dirtyAvatars">How many avatars have unsaved changes.</param>
        public NeighbourhoodOpenFacts(
            int id,
            bool exists,
            bool isOpenable,
            int? loadedSlotId,
            bool inLot = false,
            bool moveInPending = false,
            int dirtyAvatars = 0)
        {
            Id = id;
            Exists = exists;
            IsOpenable = isOpenable;
            LoadedSlotId = loadedSlotId;
            InLot = inLot;
            MoveInPending = moveInPending;
            DirtyAvatars = dirtyAvatars;
        }
    }

    /// <summary>
    /// The pure presentation half of <see cref="UINeighbourhoodManagementPanel"/>: what a slot
    /// row says, which buttons are offered for the selected slot, and why a typed name is not
    /// accepted.
    ///
    /// <para>
    /// None of this touches the disk, the content manager or a <c>UIElement</c>, so all of it is
    /// asserted in <c>NeighbourhoodManagementPanelTests</c>. It is a separate type rather than a
    /// nest of statics on the panel for the same reason the codebase keeps the decisions in
    /// <c>GraphicsOptions</c> and <c>LotZoning</c> out of their panels: the rules are worth
    /// reading - and testing - without a graphics device in the picture, and a test that has to
    /// build a dialog to check a caption is a test that stops being run.
    /// </para>
    ///
    /// <para>
    /// The truth it presents comes from <see cref="NeighbourhoodManager"/>; nothing here decides
    /// anything about the filesystem. What it does have to be careful about is <em>agreeing</em>
    /// with the backend: a rule that let the page enable Delete on the primary neighbourhood
    /// would be a rule the backend then refuses, which is a worse experience than a greyed-out
    /// button with the reason next to it. Every rule below is therefore the same predicate
    /// <see cref="NeighbourhoodManager"/> applies, restated as a question about the UI.
    /// </para>
    ///
    /// <para>
    /// Every caption, refusal and message here is read out of Simitone's own string table
    /// (<see cref="SimitoneStrings"/>) with the English kept as the second argument, so with no
    /// table - a stripped install, or a language nobody has translated this page into - the page
    /// says exactly what it said before any of this was routed. The captions that never change
    /// are therefore <c>static readonly</c> fields rather than <c>const</c> ones: a lookup cannot
    /// happen in a <c>const</c>, and the tests read these fields to compare whole sentences, so
    /// they have to keep being fields.
    /// </para>
    ///
    /// <para>
    /// Two of the members are still <c>const</c>, on purpose. <see cref="Separator"/> is the
    /// punctuation a row's detail line is split back apart on, so a translated one would break
    /// the page rather than translate it; <see cref="ReservedNameChars"/> is what the filesystem
    /// refuses, not what a language says, and the message below it splices the same constant in
    /// so the two cannot drift apart.
    /// </para>
    /// </summary>
    public static class NeighbourhoodPanelModel
    {
        /// <summary>Row state for a slot that has a folder in the user directory.</summary>
        public static readonly string StateInUse = SimitoneStrings.Get("300", "In use");

        /// <summary>Row state for a slot with no folder yet - a slot a new neighbourhood can go in.</summary>
        public static readonly string StateMissing = SimitoneStrings.Get("301", "Not created");

        /// <summary>
        /// Shown for slots 7..9, which The Sims ships no dedicated area template for. See
        /// <see cref="NeighbourhoodManager.AreaNameFor"/>.
        /// </summary>
        public static readonly string NoAreaTemplate = SimitoneStrings.Get("302", "No area template");

        /// <summary>Marker added to a row whose slot the player has given a name of its own.</summary>
        public static readonly string RenamedMarker = SimitoneStrings.Get("303", "named");

        /// <summary>Marker added to the row for the neighbourhood the running session has open.</summary>
        public static readonly string LoadedMarker = SimitoneStrings.Get("304", "open now");

        /// <summary>Joins the parts of a row's detail line.</summary>
        public const string Separator = "  -  ";

        /// <summary>
        /// What the "New (blank)" button says when every slot is taken. Reported as the page's
        /// status rather than as a refusal, because the list already shows nine "In use" rows -
        /// the player is not being told something the table has not already said.
        /// </summary>
        public static readonly string AllSlotsInUse = SimitoneStrings.Get("305",
            "All nine neighbourhoods are in use. Delete one to make room for another.");

        /// <summary>Reported instead of a copy when no install path is known.</summary>
        public static readonly string NoInstallPath = SimitoneStrings.Get("306",
            "No The Sims install path is known, so a neighbourhood cannot be copied in.");

        /// <summary>Shown for a backend call that reported failure. The reason is in the log.</summary>
        public static readonly string ActionFailed = SimitoneStrings.Get("307",
            "That did not work. See the Simitone log for the reason.");


        /// <summary>
        /// The characters <see cref="NeighbourhoodManager.IsValidName"/> refuses beyond the path
        /// separators and the control characters, as a string the rename message can splice in.
        /// </summary>
        /// <remarks>
        /// Derived from the one copy of the rule rather than typed out. This used to be a
        /// <c>const string</c> of its own, matching a <c>private char[]</c> in
        /// <see cref="NeighbourhoodManager"/> by hand and by comment: a character added to one
        /// and not the other produced a message naming a character the button did not care
        /// about, and nothing noticed. One definition, read from the manager, is what stops it.
        ///
        /// <para>
        /// A field rather than a <c>const</c> because the underlying array is not a constant, and
        /// the order is not part of the message - only the set is.
        /// </para>
        /// </remarks>
        public static string ReservedNameChars => NeighbourhoodManager.ReservedNameCharsText;

        /// <summary>
        /// The label a row's title shows: the player's own name when they set one, otherwise
        /// "Neighbourhood 1" .. "Neighbourhood 9".
        /// </summary>
        /// <returns>The row title, or <c>null</c> for a slot that does not exist.</returns>
        public static string RowName(int id, string customName)
        {
            if (!NeighbourhoodManager.IsKnownSlot(id)) return null;
            return NeighbourhoodManager.SlotName(id, customName);
        }

        /// <summary>
        /// The area a slot is themed around, or <see cref="NoAreaTemplate"/> when The Sims names
        /// none for it.
        /// </summary>
        /// <returns>The area caption, or <c>null</c> for a slot that does not exist.</returns>
        public static string RowArea(int id)
        {
            if (!NeighbourhoodManager.IsKnownSlot(id)) return null;
            return NeighbourhoodManager.AreaNameFor(id) ?? NoAreaTemplate;
        }

        /// <summary>Whether a row reads "In use" or "Not created".</summary>
        public static string RowState(bool exists)
        {
            return exists ? StateInUse : StateMissing;
        }

        /// <summary>
        /// Everything a row says about a slot other than its name: the folder that backs it, the
        /// area, whether it is in use, whether the player has named it, and whether the session
        /// has it open.
        /// </summary>
        /// <remarks>
        /// The folder name is in the detail line rather than nowhere because it is the only
        /// visible sign of the slot convention - a player who has looked at
        /// <c>Documents\Simitone</c> can match <c>UserData5</c> to the row they renamed.
        /// </remarks>
        /// <param name="id">Slot id.</param>
        /// <param name="customName">The slot's stored name, or <c>null</c>.</param>
        /// <param name="exists">Whether the slot has a folder.</param>
        /// <param name="isLoaded">Whether the running session has this slot open.</param>
        /// <returns>The detail line, or <c>null</c> for a slot that does not exist.</returns>
        public static string RowDetail(int id, string customName, bool exists, bool isLoaded = false)
        {
            if (!NeighbourhoodManager.IsKnownSlot(id)) return null;

            var parts = new List<string>(5)
            {
                NeighbourhoodManager.SlotFolderName(id),
                RowArea(id),
                RowState(exists)
            };

            if (isLoaded) parts.Add(LoadedMarker);
            if (!string.IsNullOrEmpty(customName)) parts.Add(RenamedMarker);

            return string.Join(Separator, parts);
        }

        /// <summary>
        /// Which of the nine slots the running session has open, worked out from the path the
        /// neighbourhood provider resolved.
        /// </summary>
        /// <remarks>
        /// <c>TS1NeighborhoodProvider.InitSpecific</c> does not remember the id it was given - it
        /// keeps the <c>UserPath</c> it built - so this re-derives it by asking which slot folder
        /// that path is. The comparison goes through
        /// <see cref="NeighbourhoodManager.IsSameFolder"/>, so the provider's trailing separator
        /// and the mixed separators <c>UserDir</c> arrives with cannot make the answer come out
        /// <c>null</c> for a slot that is in fact open - which is what would quietly re-enable the
        /// Delete button on the one folder the game holds handles into.
        ///
        /// <para>
        /// <c>null</c> means "nothing is known to be open", and that is exactly what the caller
        /// passes to <see cref="NeighbourhoodManager.Delete(string, int, int?)"/> as its
        /// <c>loadedSlotId</c> when the provider is not up at all.
        /// </para>
        /// </remarks>
        /// <param name="loadedUserPath">
        /// <c>TS1NeighborhoodProvider.UserPath</c>, or <c>null</c> when content is not up.
        /// </param>
        /// <param name="userDir"><c>FSOEnvironment.UserDir</c>.</param>
        /// <returns>The open slot's id, or <c>null</c> when the path names no slot.</returns>
        public static int? LoadedSlotId(string loadedUserPath, string userDir)
        {
            if (string.IsNullOrEmpty(loadedUserPath) || string.IsNullOrEmpty(userDir)) return null;

            for (var id = NeighbourhoodManager.MinSlotId; id <= NeighbourhoodManager.MaxSlotId; id++)
            {
                if (NeighbourhoodManager.IsSameFolder(loadedUserPath, NeighbourhoodManager.SlotPath(userDir, id)))
                {
                    return id;
                }
            }

            return null;
        }

        /// <summary>
        /// The slot a freshly opened page should have selected: the first one that already exists,
        /// so it opens on something the player recognises rather than on an empty slot.
        /// </summary>
        /// <remarks>
        /// Falls back to the first empty slot - so a player with no neighbourhoods at all lands on
        /// slot 1 - and then to the primary, which is the one slot guaranteed to mean something.
        /// </remarks>
        /// <param name="exists">One entry per slot, in id order.</param>
        public static int InitialSelection(IReadOnlyList<bool> exists)
        {
            if (exists == null || exists.Count == 0) return NeighbourhoodManager.PrimarySlotId;

            for (var id = 0; id < exists.Count; id++)
            {
                if (exists[id]) return id;
            }

            for (var id = 0; id < exists.Count; id++)
            {
                if (!exists[id]) return id;
            }

            return NeighbourhoodManager.PrimarySlotId;
        }

        /// <summary>
        /// May "New (blank)" be pressed? Only when there is a slot to put it in, i.e. when
        /// <see cref="NeighbourhoodManager.FirstEmptySlot"/> did not answer <c>-1</c>.
        /// </summary>
        public static bool CanCreateNew(int firstEmptySlot)
        {
            return NeighbourhoodManager.IsKnownSlot(firstEmptySlot);
        }

        /// <summary>
        /// May "Copy from TS1" be pressed for this slot? The backend always wants an explicit
        /// install path and there is none to give it before
        /// <see cref="NeighbourhoodManager.DefaultInstallPath"/> has been published, so a build
        /// that never resolved an install offers the button greyed out with
        /// <see cref="NoInstallPath"/> next to it rather than a copy that fails.
        /// </summary>
        public static bool CanCopyFromInstall(int id, string installPath)
        {
            return NeighbourhoodManager.IsKnownSlot(id) && !string.IsNullOrEmpty(installPath);
        }

        /// <summary>May this slot be renamed? Only a slot that exists has anything to name.</summary>
        public static bool CanRename(int id, bool exists)
        {
            return NeighbourhoodManager.IsKnownSlot(id) && exists;
        }

        /// <summary>
        /// May "Reset name" be pressed? Only when there is a name to drop - otherwise it is a
        /// no-op button, and <see cref="NeighbourhoodManager.ClearName"/> would rewrite the
        /// sidecar to exactly the file it already had.
        /// </summary>
        public static bool CanResetName(int id, string customName)
        {
            return NeighbourhoodManager.IsKnownSlot(id) && !string.IsNullOrEmpty(customName);
        }

        /// <summary>
        /// May this slot be deleted? Mirrors the two guards
        /// <see cref="NeighbourhoodManager.Delete(string, int, int?)"/> applies: the primary
        /// neighbourhood is what Simitone boots from, and the open neighbourhood has its files
        /// held by the running session.
        /// </summary>
        public static bool CanDelete(int id, int? loadedSlotId)
        {
            return DeleteRefusal(id, loadedSlotId) == null;
        }

        /// <summary>
        /// Why this slot may not be deleted, in the player's words, or <c>null</c> when it may.
        /// </summary>
        /// <remarks>
        /// Returns the reason rather than a bool because "the button is greyed out" on its own is
        /// not something a player can act on. This is what the page shows under the button column
        /// for the selected slot.
        /// </remarks>
        public static string DeleteRefusal(int id, int? loadedSlotId)
        {
            if (!NeighbourhoodManager.IsKnownSlot(id))
            {
                return NotASlotRefusal();
            }

            if (id == NeighbourhoodManager.PrimarySlotId)
            {
                return SimitoneStrings.Get("309",
                    "The first neighbourhood is the one Simitone starts from, so it cannot be deleted.");
            }

            if (loadedSlotId.HasValue && loadedSlotId.Value == id)
            {
                return SimitoneStrings.Get("310",
                    "This is the neighbourhood that is open right now. Go to another one and come back to delete it.");
            }

            return null;
        }

        /// <summary>
        /// "That is not one of the 9 neighbourhoods." - one string for both refusals, because
        /// they are the same sentence and a translator should not have to translate it twice
        /// (and then keep the two in step).
        /// </summary>
        private static string NotASlotRefusal()
        {
            var count = NeighbourhoodManager.SlotCount.ToString(CultureInfo.InvariantCulture);
            return SimitoneStrings.Format("308", "That is not one of the " + count + " neighbourhoods.", count);
        }

        // ---- the action column ----------------------------------------------------------------

        /// <summary>
        /// The six things the action column can do to the selected slot.
        /// </summary>
        /// <remarks>
        /// An enum rather than six names because the page needs to say three things about the
        /// column that have to agree: which button is which when it re-gates them after a
        /// selection changes, which caption goes on which, and how many of them there are for the
        /// layout. A list of six <c>UIButton</c> fields gives the first of those and nothing else
        /// a test can see; this gives all three from one declaration.
        ///
        /// <para>
        /// The members are declared in stacking order and the table below is written in the same
        /// order, so the i-th button drawn is the i-th one in <see cref="ActionButtons"/> - which
        /// is what lets the page address them by index rather than by a second hand-kept map.
        /// </para>
        /// </remarks>
        public enum NeighbourhoodAction
        {
            /// <summary>Make the running session use this slot - behind the switch confirmation.</summary>
            Open,

            /// <summary>Create a new, empty neighbourhood in the first free slot.</summary>
            CreateNew,

            /// <summary>Copy a neighbourhood in from the resolved The Sims install.</summary>
            CopyFromInstall,

            /// <summary>Give this slot a name of its own.</summary>
            Rename,

            /// <summary>Drop the stored name and put the default label back.</summary>
            ResetName,

            /// <summary>Delete the slot and everything in it - behind the delete confirmation.</summary>
            Delete,
        }

        /// <summary>One button of the action column: what it is, and what it says.</summary>
        public readonly struct NeighbourhoodActionButton
        {
            /// <summary>What the button does, and what re-gates it when the selection changes.</summary>
            public readonly NeighbourhoodAction Action;

            /// <summary>What the button reads, from Simitone's own string table.</summary>
            public readonly string Caption;

            /// <summary>Captures one row of the column.</summary>
            public NeighbourhoodActionButton(NeighbourhoodAction action, string caption)
            {
                Action = action;
                Caption = caption;
            }
        }

        /// <summary>
        /// The action column, in the order the page stacks it.
        /// </summary>
        /// <remarks>
        /// The captions live here rather than in the panel's constructor for the reason every
        /// other string on this page does: a caption written at the point of use is a caption a
        /// test cannot check. Here it is data, so the set - seven buttons, in an order, each one
        /// saying something distinct - is asserted in
        /// <c>NeighbourhoodManagementPanelTests</c> without a graphics device.
        /// </remarks>
        public static readonly IReadOnlyList<NeighbourhoodActionButton> ActionButtons =
            new[]
            {
                new NeighbourhoodActionButton(NeighbourhoodAction.Open,
                    SimitoneStrings.Get("351", "Open")),
                new NeighbourhoodActionButton(NeighbourhoodAction.CreateNew,
                    SimitoneStrings.Get("352", "New (blank)")),
                new NeighbourhoodActionButton(NeighbourhoodAction.CopyFromInstall,
                    SimitoneStrings.Get("353", "Copy from TS1")),
                new NeighbourhoodActionButton(NeighbourhoodAction.Rename,
                    SimitoneStrings.Get("354", "Rename...")),
                new NeighbourhoodActionButton(NeighbourhoodAction.ResetName,
                    SimitoneStrings.Get("355", "Reset name")),
                new NeighbourhoodActionButton(NeighbourhoodAction.Delete,
                    SimitoneStrings.Get("356", "Delete")),
            };

        /// <summary>
        /// How many buttons the action column holds, which is also how tall the column is. Read
        /// off the table rather than written down, because the layout's arithmetic is a function of
        /// this number and a second count kept by hand is a button that silently falls out of the
        /// arrangement.
        /// </summary>
        public static int ActionCount
        {
            get { return ActionButtons.Count; }
        }

        /// <summary>
        /// The footer's button.
        /// </summary>
        /// <remarks>
        /// Not part of <see cref="ActionButtons"/> and deliberately so: it acts on the page rather
        /// than on the selected slot, it is never greyed out, and it is the one control the layout
        /// must never let the column push into. Counting it as a seventh action would put it in
        /// the middle of the column's arithmetic, which is exactly what the layout is written to
        /// prevent.
        /// </remarks>
        public static readonly string CloseCaption = SimitoneStrings.Get("357", "Close");

        // ---- Open (switching neighbourhoods) ---------------------------------------------------

        /// <summary>Title of the confirmation a switch is behind.</summary>
        public static readonly string OpenTitle = SimitoneStrings.Get("311", "Open neighbourhood");

        /// <summary>The English both confirmation bodies are built from.</summary>
        private const string OpenConfirmBodyEnglish =
            "Open {0}? The neighbourhood that is open now, {1}, will be closed, and anything in it"
                + " that you have not saved will be lost.";

        /// <summary>The English the unnamed-target variant is built from.</summary>
        private const string OpenConfirmBodyUnnamedEnglish =
            "Open {0}? The neighbourhood that is open now will be closed, and anything in it that"
                + " you have not saved will be lost.";

        /// <summary>
        /// What the confirmation says, with the slot's display name and the name of the one that is
        /// being closed. Always warns about the loss, whether or not a save is offered: the
        /// provider's <c>InitSpecific</c> discards whatever the open neighbourhood is holding, and
        /// it does that without asking.
        /// </summary>
        public static readonly string OpenConfirmBody =
            SimitoneStrings.Get("312", OpenConfirmBodyEnglish);

        /// <summary>
        /// The same warning for the case where the open slot's name is not known, which is the case
        /// the switch refuses anyway - kept separate so the sentence never reads "the
        /// neighbourhood that is open now, , will be closed".
        /// </summary>
        public static readonly string OpenConfirmBodyUnnamed =
            SimitoneStrings.Get("313", OpenConfirmBodyUnnamedEnglish);

        /// <summary>
        /// The one extra paragraph, shown only when a save is being offered, spelling out what the
        /// three buttons do.
        ///
        /// <para>
        /// It has to. The confirmation is a Yes/No/Cancel through <c>DialogService</c>, whose
        /// buttons are captioned "Yes" and "No" and cannot be relabelled, and "No" meaning "open it
        /// anyway" is a trap for exactly the player this paragraph is for.
        /// </para>
        /// </summary>
        public static readonly string OpenConfirmSaveGuide = SimitoneStrings.Get("314",
            "It has changes that have not been saved. Yes saves it and then opens the other one, No"
                + " opens the other one without saving, and Cancel goes back.");

        /// <summary>
        /// Shown for a switch that failed while the neighbourhood that was open was put back
        /// successfully, i.e. the only case in which the session is still what it was.
        /// </summary>
        public static readonly string OpenFailed = SimitoneStrings.Get("315",
            "That neighbourhood could not be opened, so the one you were in is still open."
                + " See the Simitone log for the reason.");

        /// <summary>
        /// Shown for the one case that is worse: the switch failed <em>and</em> the neighbourhood
        /// that was open could not be put back either. Said plainly, because telling this player
        /// that everything is fine would be a lie.
        /// </summary>
        public static readonly string OpenFailedFatal = SimitoneStrings.Get("316",
            "That neighbourhood could not be opened, and the one you were in could not be put back"
                + " either. Restart Simitone; the Simitone log has the reason.");

        /// <summary>
        /// Shown when "Save and open" was asked for but the save did not happen. Nothing is
        /// switched in this case: carrying on would throw away exactly the changes the player asked
        /// to keep.
        /// </summary>
        public static readonly string OpenSaveFailed = SimitoneStrings.Get("317",
            "The neighbourhood could not be saved, so nothing was switched. See the Simitone log"
                + " for the reason.");

        /// <summary>
        /// Shown for the one failure that is neither a refused switch nor a failed one: the data
        /// switched, and the map could not be built on it. Distinguished from
        /// <see cref="OpenFailed"/> because "it is still open" would be true of the data and false
        /// of what the player can see.
        /// </summary>
        public static readonly string OpenMapFailed = SimitoneStrings.Get("318",
            "That neighbourhood was opened, but its map could not be drawn. See the Simitone log"
                + " for the reason; if it keeps happening, restart Simitone.");

        /// <summary>
        /// Shown when the page is not up over a neighbourhood screen, which is the only place a
        /// switch can be carried out. A guard, not an expected state: the page is opened from the
        /// neighbourhood screen's own button.
        /// </summary>
        public static readonly string NoScreenToSwitch = SimitoneStrings.Get("319",
            "Neighbourhoods can only be opened from the neighbourhood map.");

        /// <summary>May "Open" be pressed for this slot? See <see cref="OpenRefusal"/>.</summary>
        public static bool CanOpen(in NeighbourhoodOpenFacts facts)
        {
            return OpenRefusal(facts) == null;
        }

        /// <summary>
        /// Why this slot may not be opened, in the player's words, or <c>null</c> when it may.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The order is the order of how badly each refusal would mislead. A slot with no folder
        /// and a slot the game has open are both things the player can see for themselves; the two
        /// that matter most are the last pair, because they are the ones a player would otherwise
        /// have to discover by losing something.
        /// </para>
        ///
        /// <para>
        /// <b>The lot and the move-in guards.</b> The page is opened from the neighbourhood
        /// screen, where neither can be true, so in practice they never fire. They are here
        /// because the alternative is a switch that quietly corrupts a session if the page is ever
        /// reachable from a lot: <c>InitSpecific</c> replaces the very data a live lot was loaded
        /// from, and a pending move-in would buy the next lot for a family of the neighbourhood
        /// being closed. A guard that cannot be reached today is cheaper than the bug it is here to
        /// stop.
        /// </para>
        ///
        /// <para>
        /// <b>Nothing known to be open.</b> A <c>null</c> <paramref name="facts.LoadedSlotId"/>
        /// means the provider's path named no slot, not that the session is empty. Switching then
        /// would mean discarding a neighbourhood whose folder we cannot name - and, worse, a failed
        /// switch would have nothing to put back. Refusing is the only answer that keeps the
        /// promise the button makes.
        /// </para>
        ///
        /// <para>
        /// <b>Not openable.</b> The last guard, and the only one the engine does not make for us:
        /// <c>InitSpecific</c> opens <c>Neighborhood.iff</c> on its first line and throws if the
        /// file is not there, which would leave the open neighbourhood already half torn down. A
        /// folder that is not a real neighbourhood is what a hand-made directory, or a copy of a
        /// <c>Template*</c> overlay, looks like.
        /// </para>
        /// </remarks>
        public static string OpenRefusal(in NeighbourhoodOpenFacts facts)
        {
            if (!NeighbourhoodManager.IsKnownSlot(facts.Id))
            {
                return NotASlotRefusal();
            }

            if (!facts.Exists)
            {
                return SimitoneStrings.Get("320",
                    "This slot has no neighbourhood in it yet. Make a new one or copy one in from"
                        + " The Sims first.");
            }

            if (facts.InLot)
            {
                return SimitoneStrings.Get("321",
                    "A lot is open. Go back to the neighbourhood map before opening another"
                        + " neighbourhood.");
            }

            if (facts.MoveInPending)
            {
                return SimitoneStrings.Get("322",
                    "A family is waiting to move in. Finish that, or go back, before opening"
                        + " another neighbourhood.");
            }

            if (!facts.LoadedSlotId.HasValue)
            {
                return SimitoneStrings.Get("323",
                    "Simitone cannot tell which neighbourhood is open right now, so it will not"
                        + " put one out from under the other. See the Simitone log for the reason.");
            }

            if (facts.LoadedSlotId.Value == facts.Id)
            {
                return SimitoneStrings.Get("324", "This is the neighbourhood that is open right now.");
            }

            if (!facts.IsOpenable)
            {
                return SimitoneStrings.Format("325",
                    "This folder has no " + TS1UserData.NeighborhoodResourceName + " in it, so"
                        + " the game cannot open it as a neighbourhood. Delete it and copy a new one in.",
                    TS1UserData.NeighborhoodResourceName);
            }

            return null;
        }

        /// <summary>
        /// Must the confirmation offer to save before it switches?
        ///
        /// <para>
        /// Yes whenever there is anything unsaved to lose: a lot is loaded, or the open
        /// neighbourhood has avatars whose changes have not been written. The first is the rule
        /// rather than the reachable case - the page only opens on the map, so the dirty-avatar
        /// count is the branch that actually fires - and it is kept because a save that only
        /// wrote the neighbourhood would not save a lot.
        /// </para>
        /// </summary>
        public static bool ShouldOfferSave(in NeighbourhoodOpenFacts facts)
        {
            return facts.InLot || facts.DirtyAvatars > 0;
        }

        /// <summary>
        /// What the confirmation says, built from the facts and the two slots' display names.
        /// </summary>
        /// <param name="facts">The facts of the switch being confirmed.</param>
        /// <param name="name">What the slot being opened is called.</param>
        /// <param name="loadedName">
        /// What the slot being closed is called, or <c>null</c> when it is not known - which the
        /// refusal rule above has already rejected, so this only keeps the sentence grammatical.
        /// </param>
        public static string OpenConfirmMessage(in NeighbourhoodOpenFacts facts, string name, string loadedName)
        {
            var target = string.IsNullOrEmpty(name)
                ? SimitoneStrings.Get("326", "this neighbourhood")
                : name;

            var body = ComposeConfirmBody(target, loadedName);

            return ShouldOfferSave(facts) ? body + "\n\n" + OpenConfirmSaveGuide : body;
        }

        /// <summary>
        /// Fills in the confirmation body, and does not throw if it cannot.
        /// </summary>
        /// <remarks>
        /// The body comes out of a string table now, so its <c>{0}</c> / <c>{1}</c> specifiers are
        /// whatever a translator typed. One that does not line up with the two arguments this
        /// method always passes - a <c>{2}</c>, a stray brace - makes <c>string.Format</c> throw,
        /// and a <c>FormatException</c> out of a dialog handler takes the game down over a piece of
        /// text. The English is composed instead: a mis-translated confirmation is a bad sentence,
        /// a crash is worse.
        /// </remarks>
        private static string ComposeConfirmBody(string target, string loadedName)
        {
            var named = !string.IsNullOrEmpty(loadedName);
            try
            {
                return string.Format(CultureInfo.InvariantCulture,
                    named ? OpenConfirmBody : OpenConfirmBodyUnnamed, target, loadedName);
            }
            catch (FormatException)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    named ? OpenConfirmBodyEnglish : OpenConfirmBodyUnnamedEnglish, target, loadedName);
            }
        }

        /// <summary>
        /// May the rename dialog's OK be pressed? Exactly
        /// <see cref="NeighbourhoodManager.IsValidName"/>, run against the text box on every
        /// keystroke. The dialog does not get to "fix" what was typed, because the backend gates
        /// rather than rewrites, and a name the player did not type is worse than a refused one.
        /// </summary>
        public static bool CanAcceptRename(string typed)
        {
            return NeighbourhoodManager.IsValidName(typed);
        }

        /// <summary>
        /// Why a typed name was refused, or <c>null</c> when it is acceptable. Shown under the
        /// dialog's buttons, and checked in the same order as
        /// <see cref="NeighbourhoodManager.IsValidName"/> so the message is about the reason the
        /// button is greyed out rather than the first rule in the file.
        /// </summary>
        public static string RenameRejection(string typed)
        {
            if (CanAcceptRename(typed)) return null;

            if (typed == null || typed.Trim().Length == 0) return SimitoneStrings.Get("327", "Type a name first.");

            var trimmed = typed.Trim();
            if (trimmed == "." || trimmed == "..")
            {
                return SimitoneStrings.Get("328", "\".\" and \"..\" are not names.");
            }

            if (trimmed.Length > NeighbourhoodManager.MaxNameLength)
            {
                var limit = NeighbourhoodManager.MaxNameLength.ToString(CultureInfo.InvariantCulture);
                return SimitoneStrings.Format("329", "Names can be at most " + limit + " characters.", limit);
            }

            if (typed.IndexOf('/') >= 0 || typed.IndexOf('\\') >= 0)
            {
                return SimitoneStrings.Get("330", "A name cannot contain / or \\.");
            }

            for (var i = 0; i < typed.Length; i++)
            {
                var c = typed[i];
                if (char.IsControl(c)) return SimitoneStrings.Get("331", "A name cannot contain control characters.");
                if (ReservedNameChars.IndexOf(c) >= 0)
                {
                    // The set is spliced in rather than written into the table, so a translated
                    // message cannot name a different set of characters from the one the backend
                    // refuses.
                    return SimitoneStrings.Format("332",
                        "A name cannot contain any of " + ReservedNameChars + ".", ReservedNameChars);
                }
            }

            //Unreachable while IsValidName is the only thing that can reject. Kept so that a rule
            //added there without a message here still leaves the player with an explanation.
            return SimitoneStrings.Get("333", "That name cannot be used.");
        }

        /// <summary>"Created UserData3." after a successful blank creation.</summary>
        public static string CreatedBlank(int id)
        {
            var label = SlotLabel(id);
            return SimitoneStrings.Format("334", "Created " + label + ".", label);
        }

        /// <summary>"Copied UserData3 from The Sims." after a successful copy.</summary>
        public static string CopiedFromInstall(int id)
        {
            var label = SlotLabel(id);
            return SimitoneStrings.Format("335", "Copied " + label + " from The Sims.", label);
        }

        /// <summary>"Deleted UserData3." after a successful delete.</summary>
        public static string DeletedSlot(int id)
        {
            var label = SlotLabel(id);
            return SimitoneStrings.Format("336", "Deleted " + label + ".", label);
        }

        /// <summary>"Renamed UserData3 to Springfield." after a successful rename.</summary>
        public static string RenamedSlot(int id, string name)
        {
            var label = SlotLabel(id);
            return SimitoneStrings.Format("337", "Renamed " + label + " to " + name + ".", label, name);
        }

        /// <summary>"UserData3 is back to its default name." after a successful reset.</summary>
        public static string NameReset(int id)
        {
            var label = SlotLabel(id);
            return SimitoneStrings.Format("338", label + " is back to its default name.", label);
        }

        /// <summary>
        /// "Opened UserData3." after a successful switch - the one action on the page that changed
        /// what the game is showing rather than what is on disk.
        /// </summary>
        public static string OpenedSlot(int id)
        {
            var label = SlotLabel(id);
            return SimitoneStrings.Format("339", "Opened " + label + ".", label);
        }

        /// <summary>
        /// What a status line calls the slot. The folder name where there is one, because that is
        /// what the player will find in <c>Documents\Simitone</c>, and a numbered stand-in for a
        /// slot that is not a slot.
        /// </summary>
        private static string SlotLabel(int id)
        {
            var folder = NeighbourhoodManager.SlotFolderName(id);
            if (folder != null) return folder;

            var number = id.ToString(CultureInfo.InvariantCulture);
            return SimitoneStrings.Format("340", "slot " + number, number);
        }
    }

    /// <summary>
    /// Where the page's parts sit vertically, worked out once per relayout and in one place.
    ///
    /// <para>
    /// The action column is the only part of the page whose height depends on how many buttons it
    /// has, and the explanation under it is the only part with no fixed height - so those are the
    /// two that can collide when the column grows. This is their arithmetic, kept out of the dialog
    /// so the collision can be asserted: the rule is that the <em>hint gives way</em> and the
    /// Close button never moves.
    /// </para>
    /// </summary>
    public static class NeighbourhoodPanelLayout
    {
        /// <summary>One page's vertical arrangement, in the dialog's own coordinates.</summary>
        public readonly struct Vertical
        {
            /// <summary>Height of the slot list, which scrolls to fill whatever is left over.</summary>
            public readonly int ListHeight;

            /// <summary>Bottom edge of the last action button, i.e. where the hint starts.</summary>
            public readonly int ActionBottom;

            /// <summary>Where the explanation under the button column starts.</summary>
            public readonly int HintTop;

            /// <summary>
            /// How much of the explanation actually fits. Zero on a page too short to hold the
            /// buttons and the footer, which is why it is computed rather than assumed.
            /// </summary>
            public readonly int HintHeight;

            /// <summary>Where the footer row - the status line and Close - starts.</summary>
            public readonly int FooterTop;

            /// <summary>Captures one page's arrangement.</summary>
            public Vertical(int listHeight, int actionBottom, int hintTop, int hintHeight, int footerTop)
            {
                ListHeight = listHeight;
                ActionBottom = actionBottom;
                HintTop = hintTop;
                HintHeight = hintHeight;
                FooterTop = footerTop;
            }
        }

        /// <summary>
        /// The shortest page on which this many action buttons and the footer do not collide.
        ///
        /// <para>
        /// The action column is the only thing on the page with a height that is not derived from
        /// the dialog's own, so it is also the only thing that can push past the footer. This is
        /// the floor the page's height is raised to when the window is too short to give it
        /// naturally - a page that overlaps itself is a worse failure than a page that is a little
        /// taller than the window wanted, and a window too short for six buttons has no usable
        /// arrangement at any height.
        /// </para>
        /// </summary>
        /// <param name="listTop">Top of the slot list, where the column starts.</param>
        /// <param name="footerHeight">Height of the footer row.</param>
        /// <param name="buttonHeight">Height of one action button.</param>
        /// <param name="actionGap">Gap between two stacked elements in the action column.</param>
        /// <param name="actionCount">How many action buttons the column holds.</param>
        /// <returns>The smallest height at which the column and the footer still fit.</returns>
        public static int RequiredHeight(
            int listTop, int footerHeight, int buttonHeight, int actionGap, int actionCount)
        {
            //the column, a gap, then the footer row - whose controls sit a quarter of a button
            //down into it, so that much of the row is above them
            return listTop
                + Math.Max(0, actionCount * (buttonHeight + actionGap))
                + actionGap
                + footerHeight
                - (buttonHeight / 4);
        }

        /// <summary>
        /// Works out the page's vertical arrangement.
        /// </summary>
        /// <param name="height">The dialog's height.</param>
        /// <param name="listTop">Top of the slot list.</param>
        /// <param name="footerHeight">Height of the footer row.</param>
        /// <param name="buttonHeight">Height of one action button.</param>
        /// <param name="actionGap">Gap between two stacked elements in the action column.</param>
        /// <param name="actionCount">How many action buttons the column holds.</param>
        /// <param name="rowHeight">Height of one slot row, which is the list's floor.</param>
        /// <param name="detailSize">Text size the hint is drawn at, which sets its three-line floor.</param>
        public static Vertical Arrange(
            int height, int listTop, int footerHeight, int buttonHeight,
            int actionGap, int actionCount, int rowHeight, int detailSize)
        {
            var listHeight = Math.Max(rowHeight, height - listTop - footerHeight);

            //the last button's bottom edge, and a gap before whatever comes next
            var actionBottom = listTop + Math.Max(0, actionCount * (buttonHeight + actionGap));
            var hintTop = actionBottom + actionGap;

            //the footer's top edge: the row is footerHeight tall, but its controls are one button
            //tall and sit a quarter of a button down it
            var footerTop = height - footerHeight + (buttonHeight / 4);

            //the hint gets at least three lines where there is room for them, and never more than
            //the room there is. Clamped rather than merely preferred, because a preference would
            //put the explanation under the Close button on a short screen.
            var room = Math.Max(0, footerTop - actionGap - hintTop);

            return new Vertical(
                listHeight,
                actionBottom,
                hintTop,
                Math.Min(Math.Max(detailSize * 3, room), room),
                footerTop);
        }
    }

    /// <summary>
    /// The Neighbourhood Management page: the nine neighbourhood slots The Sims supports, listed
    /// with the name, area and state of each, and the six things a player can do to them.
    ///
    /// <para>
    /// <b>What it is built on.</b> A <see cref="UIMobileDialog"/>, so it gets the same striped
    /// plate, title and slide-in/slide-out that the Options menu and every confirm in the game
    /// use, and so closing it takes it off the screen without the page having to find its own way
    /// out. The list is a <see cref="UITouchScroll"/> of nine rows - the shape the rest of
    /// Simitone's touch UI is made of - and the actions are a column of
    /// <see cref="UIBigButton"/>s, which is the plate the Options dialog's OK, every
    /// <see cref="UIMobileAlert"/> answer and the lot picker are drawn from. Nothing here
    /// rasterises a plate at runtime any more; a rounded rectangle invented per open was the one
    /// thing on the page that did not look like the rest of the game.
    /// </para>
    ///
    /// <para>
    /// <b>Why every button is the plain colourway.</b> The themed set has exactly two plates -
    /// <c>button.png</c> and <c>greenbutton.png</c>, each with a mobile pair - and green means
    /// "the affirmative answer" everywhere the game uses it: the OK on the Options dialog, the
    /// Yes on a confirm, the Back on a submenu. Nothing on this page is an affirmative answer,
    /// and the one that most needed marking out, Delete, was given a red plate that no themed
    /// button can express. Painting Delete green instead would have said the opposite of what
    /// the red said. So the column is all one colourway, and Delete is told apart the way this
    /// game tells an irreversible thing apart: it is the last button in the column, it goes
    /// through a confirmation, and it is off with the reason written underneath whenever it would
    /// be refused. The green plate is also two pixels taller than the plain one on desktop, which
    /// a single pitch cannot hold - see <see cref="DesktopButtonHeight"/>.
    /// </para>
    ///
    /// <para>
    /// <b>What it changes.</b> Four of the five actions change what is on disk and nothing about
    /// what is open: the list refreshes, the running neighbourhood is untouched. <b>Open</b> is
    /// the exception, and it is behind a confirmation because it has to be.
    /// </para>
    ///
    /// <para>
    /// <b>Why a switch is not just another button.</b> Switching means
    /// <c>Content.Get().Neighborhood.InitSpecific(id)</c>, which discards whatever the running
    /// session has open - the provider's own comment on that method says so outright, and its
    /// first act is to clear the dirty-avatar set. So the page does the four things that cannot
    /// lose anything without asking, and asks before the one that can.
    /// </para>
    ///
    /// <para>
    /// The switch is deliberately the smallest thing that can work, and it stays inside the
    /// boundary the rest of the game draws. What the page does itself:
    /// <list type="number">
    /// <item>decides, from <see cref="NeighbourhoodOpenFacts"/>, whether a switch is allowed at
    /// all - the slot exists, it is a folder the provider could open, it is not the one already
    /// open, no lot is loaded and no family is waiting to move in;</item>
    /// <item>asks, through the screen's own <c>DialogService</c>, offering to save first when
    /// there is anything unsaved;</item>
    /// <item>calls <c>InitSpecific</c> inside a <c>try</c> and puts the previous slot back if it
    /// throws - see <c>ApplyOpen</c> for why that is not optional.</item>
    /// </list>
    /// What it does <em>not</em> do is re-derive the screen-level flow. Re-initialising the
    /// provider makes every lot button, thumbnail and family list on screen stale, and rebuilding
    /// those belongs to <c>TS1GameScreen</c> and <c>NeighborhoodService</c>, which own them. So
    /// the page makes exactly one call over there - <c>RefreshNeighbourhoodUi</c>, which tears the
    /// picker and its switcher down and builds them again from the slot that is now open.
    /// </para>
    ///
    /// <para>
    /// The switch also deliberately does not change which <em>map</em> is being looked at. The
    /// nine slots and the six neighbourhood views are different things - the switcher's buttons
    /// move between downtown, vacation, studio and magic, all of which live inside the one slot
    /// Simitone has open - so a slot switch re-reads the same view out of the new folder and
    /// leaves <c>CurrentNeighborhoodMode</c> alone. Settling what a "neighbourhood view" is
    /// meant to mean is a bigger question than this page, and guessing at it here would be how a
    /// player's downtown lots end up in their residential folder.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing here overwrites.</b> Both creation paths go through
    /// <see cref="NeighbourhoodManager"/>, which refuses to write into a folder that already
    /// exists, and this page adds no way around that. Delete refuses the primary neighbourhood
    /// and the open one for the same reasons the backend does, and shows the reason rather than a
    /// mute greyed-out button.
    /// </para>
    ///
    /// <para>
    /// The page's own wording - the title, the seven button captions and the two dialogs - is read
    /// out of Simitone's string table with the English as the fallback, exactly as the rules in
    /// <see cref="NeighbourhoodPanelModel"/> are, and the seven button captions are that model's
    /// <see cref="NeighbourhoodPanelModel.ActionButtons"/> rather than literals in a constructor.
    /// Nothing about the page depends on that table being present: a build whose
    /// <c>Content/UI/uitext</c> folder never made it out of the package gets the English it always
    /// got.
    /// </para>
    /// </summary>
    public class UINeighbourhoodManagementPanel : UIMobileDialog
    {
        /// <summary>Dialog title, and the tooltip of the front-end button that opens the page.</summary>
        public static readonly string Title = SimitoneStrings.Get("350", "Neighbourhoods");

        // ---- layout ------------------------------------------------------------------------

        private const int ListTop = 96;
        private const int ListLeft = 50;
        private const int ActionColumnWidth = 260;
        private const int ActionColumnRight = 50;
        private const int ActionGap = 10;
        private const int FooterHeight = 96;
        private const int MaxHeight = 660;

        /// <summary>
        /// How tall the page is allowed to get on a touch build.
        /// <para>
        /// Much taller than <see cref="MaxHeight"/>, because the touch plate is
        /// <see cref="TouchButtonHeight"/> tall rather than a number this page chose, and six of
        /// them at a 10px gap is 660px before the title band, the explanation and the footer are
        /// counted at all - 837px in all, which is more than any of the heights that were here
        /// before. The extra beyond that buys the explanation under the column its three lines at
        /// 22pt; without it the column and the footer clear each other and the reasons a button
        /// is greyed out have nowhere to go, which on a build with no mouse hover is the one
        /// thing this page cannot do without.
        ///
        /// <para>
        /// It is a ceiling, not a target, and it is the honest outcome: six of the game's own
        /// touch plates plus a title band and a footer need about 840px, so on a touch screen
        /// shorter than that the page is taller than the window rather than overlapping itself.
        /// Overlapping would put Delete on top of Close.
        /// </para>
        /// </summary>
        private const int MaxTouchHeight = 960;
        private const int MinHeight = 420;
        private const int MinimumListWidth = 220;

        /// <summary>
        /// How wide the slot list is allowed to get. Past this the name and the state it belongs to
        /// are far enough apart to stop reading as one line.
        /// </summary>
        private const int MaxListWidth = 780;

        private const int DesktopRowHeight = 42;

        /// <summary>
        /// Height of the desktop plate, which is the height of <c>button.png</c>.
        /// <para>
        /// Not a number this page picked: <see cref="UIBigButton"/> is a <see cref="UIButton"/>
        /// and takes its height from its texture - a three-slice of four button states across -
        /// and there is no setter for it. So the column's pitch, the page's minimum height and
        /// the footer's offset are all this number, and it is asserted against the shipped
        /// artwork's own pixel height in <c>NeighbourhoodManagementPanelTests</c>, because the
        /// one failure this number can have is silently disagreeing with the plate.
        /// </para>
        /// </summary>
        private const int DesktopButtonHeight = 48;
        private const int DesktopBodySize = 15;
        private const int DesktopDetailSize = 12;

        private const int TouchRowHeight = 70;

        /// <summary>
        /// Height of the touch plate, which is the height of <c>button.mobile.png</c>. The touch
        /// and desktop plates are not interchangeable - which is most of why
        /// <see cref="MaxTouchHeight"/> is what it is - and neither of them may be swapped for
        /// the other half way up the column.
        /// </summary>
        private const int TouchButtonHeight = 100;
        private const int TouchBodySize = 28;
        private const int TouchDetailSize = 22;

        /// <summary>
        /// Touch builds use the larger type and taller controls throughout, matched from
        /// <see cref="UIBigButton"/>: the same pair of plates the button column is built from, so
        /// a build that draws the mobile artwork also gets the mobile type sizes.
        /// </summary>
        private bool Desktop
        {
            get { return (UIScreen.Current as TS1GameScreen)?.Desktop ?? true; }
        }

        private int RowHeight { get { return Desktop ? DesktopRowHeight : TouchRowHeight; } }
        private int ButtonHeight { get { return Desktop ? DesktopButtonHeight : TouchButtonHeight; } }
        private int BodySize { get { return Desktop ? DesktopBodySize : TouchBodySize; } }
        private int DetailSize { get { return Desktop ? DesktopDetailSize : TouchDetailSize; } }

        /// <summary>
        /// The ceiling for this build's page height. Touch gets the taller one because its plate
        /// is; see <see cref="MaxTouchHeight"/>. Both are asserted to be at least what the column
        /// needs, because a ceiling under that would clamp the page to a height at which the last
        /// button is drawn over Close.
        /// </summary>
        private int MaxDialogHeight
        {
            get { return Desktop ? MaxHeight : MaxTouchHeight; }
        }

        // ---- state -------------------------------------------------------------------------

        /// <summary>Where the slots are. <c>FSOEnvironment.UserDir</c>, captured when the page opens.</summary>
        private readonly string _userDir;

        /// <summary>
        /// The install a new neighbourhood is seeded from, read from
        /// <see cref="NeighbourhoodManager.DefaultInstallPath"/> on every relayout rather than
        /// cached, so a page opened before the install path has been published recovers on its own.
        /// </summary>
        private string InstallPath
        {
            get { return NeighbourhoodManager.DefaultInstallPath; }
        }

        /// <summary>The slot the action column is pointed at.</summary>
        private int _selectedId = NeighbourhoodManager.PrimarySlotId;

        /// <summary>The slot the running session has open, or <c>null</c> when nothing is open.</summary>
        private int? _loadedSlotId;

        /// <summary>
        /// The names sidecar as of the last <see cref="RefreshList"/>.
        /// </summary>
        /// <remarks>
        /// One read per refresh rather than one per row. The list used to ask
        /// <see cref="NeighbourhoodManager.GetName"/> for a name per row title, a second one per
        /// row detail line and two more in the button refresh - roughly twenty reads and JSON
        /// parses of the same small file on construction, on every window resize, and after every
        /// rename, create and delete. Every one of those reads happened on the UI thread while
        /// the player was dragging a window edge.
        ///
        /// <para>
        /// Rebuilt in <see cref="RefreshList"/> before the rows are built, so a row, the button
        /// column and the selection line all describe the same instant. The action handlers still
        /// read through <see cref="NeighbourhoodManager"/> directly, because they are one press
        /// each and want the current answer rather than the one from the last rebuild.
        /// </para>
        /// </remarks>
        private NeighbourhoodNames _names = NeighbourhoodNames.Read(null);

        /// <summary>
        /// The page currently up, so a second press of the front-end button replaces it instead of
        /// stacking a second copy on top. Per-process rather than per-screen, because there is only
        /// ever one neighbourhood screen.
        /// </summary>
        private static UINeighbourhoodManagementPanel _open;

        private readonly UITouchScroll _list;
        private readonly UILabel _status;
        private readonly UILabel _hint;

        /// <summary>
        /// The action column, one themed button per <see cref="NeighbourhoodPanelModel"/> action,
        /// indexed by that action. Indexed rather than held as six named fields so the set on
        /// screen and the set in the model cannot drift: the array is built by walking
        /// <see cref="NeighbourhoodPanelModel.ActionButtons"/>, and its length is the count the
        /// layout's arithmetic is given.
        /// </summary>
        private readonly UIBigButton[] _actionButtons;

        private readonly UIBigButton _closeButton;

        /// <summary>One of the column's buttons, by action.</summary>
        private UIBigButton ButtonFor(NeighbourhoodPanelModel.NeighbourhoodAction action)
        {
            return _actionButtons[(int)action];
        }

        /// <summary>
        /// Builds the page and puts it up modally over whatever screen is current, replacing one
        /// that is already up.
        /// </summary>
        /// <remarks>
        /// Fire-and-forget because every caller wants the same thing - the page - and none of them
        /// wants to own its lifetime. <see cref="UIMobileDialog.Close"/> removes the dialog from
        /// the screen when its close animation finishes, so there is nothing for a caller to clean
        /// up; replacing an already-open page mirrors what <c>TS1GameScreen.ShowOptionsDialog</c>
        /// does with the Options menu.
        /// </remarks>
        public static UINeighbourhoodManagementPanel Show()
        {
            if (_open != null)
            {
                UIScreen.RemoveDialog(_open);
                _open = null;
            }

            var panel = new UINeighbourhoodManagementPanel();
            _open = panel;
            UIScreen.GlobalShowDialog(panel, true);
            return panel;
        }

        /// <summary>
        /// Clears <see cref="_open"/> when this page is the one that was open.
        /// </summary>
        /// <remarks>
        /// The static was only ever nulled inside <see cref="Show"/>, so the Close button, the
        /// modal dismiss and a screen change all left it pointing at a torn-down page - a live
        /// object graph (a <see cref="UITouchScroll"/> of rows, each holding textures, plus a
        /// user directory path) kept alive for the rest of the process, and a later
        /// <c>RemoveDialog</c> called on an element no longer in the dialog stack. The one
        /// dismissal path that did clean up was the one <see cref="Show"/> itself performs.
        /// </remarks>
        public override void Removed()
        {
            base.Removed();
            if (ReferenceEquals(_open, this)) _open = null;
        }

        public UINeighbourhoodManagementPanel() : base()
        {
            _userDir = FSOEnvironment.UserDir;
            _loadedSlotId = ReadLoadedSlotId();

            _list = new UITouchScroll(GetListLength, MakeRow);
            _list.VerticalMode = true;
            _list.DrawBounds = false;
            _list.Margin = 0;
            Add(_list);

            _actionButtons = BuildActionColumn();

            _closeButton = MakeButton(NeighbourhoodPanelModel.CloseCaption, (b) => Close());
            Add(_closeButton);

            _hint = new UILabel();
            _hint.NewStyle(UIStyle.Current.SecondaryText, DetailSize);
            _hint.Alignment = TextAlignment.Top | TextAlignment.Left;
            _hint.Wrapped = true;
            Add(_hint);

            _status = new UILabel();
            _status.NewStyle(UIStyle.Current.Text, DetailSize);
            _status.Alignment = TextAlignment.Middle | TextAlignment.Left;
            Add(_status);

            Caption = Title;
            SetHeight(DesiredHeight());
            Relayout();
            RefreshList();
        }

        /// <summary>
        /// Builds one of the page's buttons out of the game's own plate, the same
        /// <see cref="UIBigButton"/> the Options dialog's OK, every <see cref="UIMobileAlert"/>
        /// answer and the lot picker's action row are drawn from.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The whole reason this is a <see cref="UIBigButton"/> and not a <see cref="UIButton"/>
        /// over a plate made here: the page used to rasterise its own rounded rectangle in
        /// <see cref="TextureGenerator.GenerateRoundedRectangle"/>, which is not a button anything
        /// else in the game looks like. The themed button is a four-state three-slice from
        /// CustomUI - <c>button.png</c> on desktop, <c>button.mobile.png</c> on touch - carrying
        /// its own caption style, highlight colour and disabled colour, so none of the styling
        /// this method used to do by hand is needed and none of it can drift from the plate.
        /// </para>
        ///
        /// <para>
        /// <c>false</c> is the plain colourway, for every button on the page including Delete -
        /// see the class remarks for why. The height is not a parameter because there is no way to
        /// set one: <see cref="UIButton"/> takes it from the texture, which is exactly why
        /// <see cref="DesktopButtonHeight"/> and <see cref="TouchButtonHeight"/> are the artwork's
        /// numbers.
        /// </para>
        ///
        /// <para>
        /// The button is not added to the dialog. The column is added in stacking order
        /// separately, so the draw and hit-test order follows the order the model declares rather
        /// than the order the table happens to be walked in.
        /// </para>
        /// </remarks>
        private UIBigButton MakeButton(string caption, ButtonClickDelegate onClick)
        {
            var button = new UIBigButton(false) { Caption = caption };

            //Caption before the width, not after: setting a caption re-measures the button, and
            //the measurement must not be what decides the width of a fixed column.
            button.Width = ActionColumnWidth;
            button.OnButtonClick += onClick;
            return button;
        }

        /// <summary>
        /// The action column: one themed button per entry in
        /// <see cref="NeighbourhoodPanelModel.ActionButtons"/>, indexed by its action and added
        /// to the dialog in stacking order.
        /// </summary>
        /// <remarks>
        /// Built from the model's table rather than from six constructor lines because the model
        /// is where the set is asserted: a caption written where the button is made is a caption
        /// no test can read, and a button added to the column without a caption is a blank
        /// control. Indexing by action is what lets <see cref="RefreshButtons"/> re-gate the
        /// column after a selection change without a field per button.
        /// </remarks>
        private UIBigButton[] BuildActionColumn()
        {
            var column = new UIBigButton[NeighbourhoodPanelModel.ActionCount];

            foreach (var entry in NeighbourhoodPanelModel.ActionButtons)
            {
                column[(int)entry.Action] = MakeButton(entry.Caption, HandlerFor(entry.Action));
            }

            foreach (var button in column) Add(button);
            return column;
        }

        /// <summary>
        /// What each action does when its button is pressed. The one place the model says which
        /// action exists and here says which method carries it out, so a button that exists and
        /// does nothing is a missing case rather than a silent no-op.
        /// </summary>
        private ButtonClickDelegate HandlerFor(NeighbourhoodPanelModel.NeighbourhoodAction action)
        {
            switch (action)
            {
                case NeighbourhoodPanelModel.NeighbourhoodAction.Open: return (b) => AskForOpen();
                case NeighbourhoodPanelModel.NeighbourhoodAction.CreateNew: return (b) => CreateBlank();
                case NeighbourhoodPanelModel.NeighbourhoodAction.CopyFromInstall: return (b) => CopyFromInstall();
                case NeighbourhoodPanelModel.NeighbourhoodAction.Rename: return (b) => AskForRename();
                case NeighbourhoodPanelModel.NeighbourhoodAction.ResetName: return (b) => ResetName();
                case NeighbourhoodPanelModel.NeighbourhoodAction.Delete: return (b) => AskForDelete();
                default:
                    //Only reachable if an action is added to the model without a case here, and
                    //then the button is a dead control. Said in the log rather than thrown, because
                    //a null case in a table of known actions is not worth taking the game down
                    //for from inside a constructor.
                    SimitoneLog.Error("The Neighbourhood Management page has no handler for action "
                        + action + ", so its button does nothing.");
                    return null;
            }
        }

        private int DesiredHeight()
        {
            //the height the window would give the page, raised to what the button column needs so
            //the last button cannot end up underneath the footer, and capped at the build's maximum
            var offered = Math.Max(ScrHeight - 80, NeighbourhoodPanelLayout.RequiredHeight(
                ListTop, FooterHeight, ButtonHeight, ActionGap, _actionButtons.Length));

            return Math.Max(MinHeight, Math.Min(MaxDialogHeight, offered));
        }

        public override void GameResized()
        {
            base.GameResized();
            //base has just re-read the screen size, so the height, the layout and the row widths
            //are all stale. The rows are rebuilt rather than respanned because a row is laid out
            //from the width it was built with, and UIContainer.Size is a no-op.
            SetHeight(DesiredHeight());
            Relayout();
            RefreshList();
        }

        private void Relayout()
        {
            int rowHeight = RowHeight;
            int buttonHeight = ButtonHeight;

            //The list is capped rather than allowed to span the dialog: on a 2560-wide window a
            //full-width list puts the name on the left and its state 2000 pixels away on the
            //right, and the buttons end up stranded against the far edge. So the column sits just
            //past the list, and only falls back to the right edge when the two will not fit
            //beside each other - which is the 800px minimum window.
            int listWidth = Math.Max(MinimumListWidth, Math.Min(MaxListWidth,
                Width - ListLeft - ActionColumnWidth - ActionColumnRight - 20));
            int actionX = ListLeft + listWidth + 24;

            if (actionX + ActionColumnWidth + ActionColumnRight > Width)
            {
                actionX = Math.Max(ListLeft + MinimumListWidth, Width - ActionColumnWidth - ActionColumnRight);
                listWidth = Math.Max(MinimumListWidth, actionX - ListLeft - 24);
            }

            //the vertical arrangement is worked out once, in the pure helper, so the explanation
            //under the column and the footer cannot be laid out against two different ideas of
            //where the column ends
            var vertical = NeighbourhoodPanelLayout.Arrange(Height, ListTop, FooterHeight,
                buttonHeight, ActionGap, _actionButtons.Length, rowHeight, DetailSize);

            _list.Position = new Vector2(ListLeft, ListTop);
            _list.Size = new Vector2(listWidth, vertical.ListHeight);
            _list.ItemWidth = rowHeight;

            for (var i = 0; i < _actionButtons.Length; i++)
            {
                _actionButtons[i].Position = new Vector2(actionX, ListTop + (i * (buttonHeight + ActionGap)));
            }

            _hint.Position = new Vector2(actionX, vertical.HintTop);
            _hint.Size = new Vector2(ActionColumnWidth, vertical.HintHeight);

            //Close sits under the button column rather than pinned to the window's right edge:
            //on a 2560-wide window the far edge is most of a screen away from everything else on
            //the page. The status line runs under the list, not the whole dialog, because a
            //result message about one slot reads as belonging to the list it came from.
            _closeButton.Position = new Vector2(actionX, vertical.FooterTop);
            _closeButton.Width = ActionColumnWidth;

            _status.Position = new Vector2(ListLeft, vertical.FooterTop);
            _status.Size = new Vector2(Math.Max(100, listWidth), buttonHeight);
        }

        // ---- the list ----------------------------------------------------------------------

        private int GetListLength()
        {
            return NeighbourhoodManager.AllSlots.Count;
        }

        private UITSContainer MakeRow(int index)
        {
            var id = NeighbourhoodManager.AllSlots[index].Id;
            var row = new NeighbourhoodSlotRow(RowWidth(), RowHeight, BodySize, DetailSize, OnRowSelected);
            //Bound here rather than off ItemID: the scroll assigns ItemID after this factory
            //returns, and the two happen to be the same number, but only this one is guaranteed
            //to be slot order.
            row.Bind(id, RowName(id), RowDetail(id));
            return row;
        }

        private float RowWidth()
        {
            return Math.Max(MinimumListWidth, _list.Size.X);
        }

        private string RowName(int id)
        {
            var name = NeighbourhoodPanelModel.RowName(id, _names.NameOf(id));
            if (name != null) return name;

            // Only a slot the model does not recognise gets here, which is not a state the page can
            // normally be in - the list is built from AllSlots. The label still has to say
            // something rather than leave the row blank.
            var number = (id + 1).ToString(CultureInfo.InvariantCulture);
            return SimitoneStrings.Format("358", "Slot " + number, number);
        }

        private string RowDetail(int id)
        {
            return NeighbourhoodPanelModel.RowDetail(id,
                _names.NameOf(id),
                NeighbourhoodManager.Exists(_userDir, id),
                _loadedSlotId.HasValue && _loadedSlotId.Value == id);
        }

        /// <summary>
        /// A row was picked. Only the buttons and the status line change - the highlight and the
        /// scroll bookkeeping belong to <see cref="UITouchScroll"/>, and re-selecting from here
        /// would recurse through its own <c>SelectItem</c>.
        /// </summary>
        private void OnRowSelected(int slotId)
        {
            _selectedId = slotId;
            RefreshButtons();
            SetStatus(string.Empty, UIStyle.Current.Text);
        }

        /// <summary>
        /// Rebuilds the list from what is on disk and re-points the button column.
        /// </summary>
        /// <remarks>
        /// Called on open, on resize, and after every operation that changes the disk.
        /// <see cref="UITouchScroll.Reset"/> throws the row elements away rather than rebinding
        /// them, so a row cannot go on showing the state of a slot that has since been created or
        /// deleted; the selection is then reapplied deliberately, which also puts the highlight
        /// back onto the same slot.
        /// </remarks>
        public void RefreshList()
        {
            _loadedSlotId = ReadLoadedSlotId();
            //Before the rows are built: MakeRow is called by _list.Reset() below, and the button
            //refresh reads the same snapshot, so all of them describe the same read.
            _names = NeighbourhoodNames.Read(_userDir);
            if (!NeighbourhoodManager.IsKnownSlot(_selectedId))
            {
                _selectedId = NeighbourhoodManager.PrimarySlotId;
            }
            _list.Reset();
            _list.SelectItem(_selectedId);
            RefreshButtons();
        }

        private int? ReadLoadedSlotId()
        {
            try
            {
                var provider = FSO.Content.Content.Get()?.Neighborhood;
                return NeighbourhoodPanelModel.LoadedSlotId(provider?.UserPath, _userDir);
            }
            catch (Exception e)
            {
                //The provider is absent or half-built. That is "nothing is known to be open",
                //which is the same answer the backend gets when it is passed no loadedSlotId -
                //and Delete is additionally off for a slot with no folder at all, so the one
                //guard this weakens cannot be reached by a slot that does not exist. A switch,
                //which has to be able to put the old neighbourhood back, refuses outright.
                SimitoneLog.Warn("Could not work out which neighbourhood is open, so the management"
                    + " page will not treat any slot as the loaded one.", e);
                return null;
            }
        }

        /// <summary>
        /// Does this slot's folder hold a neighbourhood the provider could open? The one
        /// pre-flight the engine does not do for itself - see
        /// <see cref="NeighbourhoodOpenFacts.IsOpenable"/>.
        /// </summary>
        private bool IsOpenable(int id)
        {
            try
            {
                return TS1UserData.HasNeighborhood(NeighbourhoodManager.SlotPath(_userDir, id));
            }
            catch (Exception e)
            {
                //an unreadable path is a slot we cannot vouch for, which is "not openable" - the
                //button stays off and the reason is on the page
                SimitoneLog.Warn("Could not look inside " + NeighbourhoodManager.SlotFolderName(id)
                    + " to see whether it is a neighbourhood.", e);
                return false;
            }
        }

        /// <summary>
        /// How many avatars in the open neighbourhood have changes that have not been written.
        /// Read on demand rather than cached, because the set is emptied by a save as well as by a
        /// switch and a stale count would offer a save that has nothing to do.
        /// </summary>
        private int ReadDirtyAvatarCount()
        {
            try
            {
                return FSO.Content.Content.Get()?.Neighborhood?.DirtyAvatars?.Count ?? 0;
            }
            catch (Exception e)
            {
                //"nothing is known to be unsaved" is the safe answer: the confirmation still warns
                //that the open neighbourhood is discarded, it just does not offer a save.
                SimitoneLog.Warn("Could not tell whether the open neighbourhood has unsaved"
                    + " changes, so the switch confirmation will not offer to save.", e);
                return 0;
            }
        }

        /// <summary>
        /// Gathers what the "Open" rules need about one slot, off the running session.
        /// </summary>
        /// <param name="id">The slot to describe.</param>
        /// <param name="exists">Whether the slot has a folder, already read by the caller.</param>
        private NeighbourhoodOpenFacts OpenFacts(int id, bool exists)
        {
            var screen = UIScreen.Current as TS1GameScreen;
            return new NeighbourhoodOpenFacts(
                id,
                exists,
                IsOpenable(id),
                _loadedSlotId,
                screen?.InLot ?? false,
                screen?.MoveInFamily != null,
                ReadDirtyAvatarCount());
        }

        // ---- the button column --------------------------------------------------------------

        /// <summary>
        /// Re-evaluates every button against the selected slot. The rules are
        /// <see cref="NeighbourhoodPanelModel"/>'s, so the greying-out and what the backend would
        /// actually do cannot disagree.
        /// </summary>
        private void RefreshButtons()
        {
            var id = _selectedId;
            var customName = _names.NameOf(id);
            var exists = NeighbourhoodManager.Exists(_userDir, id);
            int firstEmpty = NeighbourhoodManager.FirstEmptySlot(_userDir);
            var open = OpenFacts(id, exists);

            ButtonFor(NeighbourhoodPanelModel.NeighbourhoodAction.Open).Disabled =
                !NeighbourhoodPanelModel.CanOpen(open);
            ButtonFor(NeighbourhoodPanelModel.NeighbourhoodAction.CreateNew).Disabled =
                !NeighbourhoodPanelModel.CanCreateNew(firstEmpty);
            ButtonFor(NeighbourhoodPanelModel.NeighbourhoodAction.CopyFromInstall).Disabled =
                !NeighbourhoodPanelModel.CanCopyFromInstall(id, InstallPath);
            ButtonFor(NeighbourhoodPanelModel.NeighbourhoodAction.Rename).Disabled =
                !NeighbourhoodPanelModel.CanRename(id, exists);
            ButtonFor(NeighbourhoodPanelModel.NeighbourhoodAction.ResetName).Disabled =
                !NeighbourhoodPanelModel.CanResetName(id, customName);
            ButtonFor(NeighbourhoodPanelModel.NeighbourhoodAction.Delete).Disabled =
                !NeighbourhoodPanelModel.CanDelete(id, _loadedSlotId);

            _hint.Caption = DescribeSelection(id, customName, exists, firstEmpty, open);
        }

        /// <summary>
        /// The line under the button column: what the selected slot is, plus the things that are
        /// stopping the player from doing what they came to do.
        /// </summary>
        private string DescribeSelection(int id, string customName, bool exists, int firstEmpty,
            in NeighbourhoodOpenFacts open)
        {
            var lines = new StringBuilder();
            lines.Append(NeighbourhoodPanelModel.RowDetail(id, customName, exists,
                _loadedSlotId.HasValue && _loadedSlotId.Value == id));

            var refusal = NeighbourhoodPanelModel.OpenRefusal(open);
            if (refusal != null) lines.Append('\n').Append(refusal);

            var deleteRefusal = NeighbourhoodPanelModel.DeleteRefusal(id, _loadedSlotId);
            if (deleteRefusal != null) lines.Append('\n').Append(deleteRefusal);

            if (firstEmpty < 0) lines.Append('\n').Append(NeighbourhoodPanelModel.AllSlotsInUse);

            if (string.IsNullOrEmpty(InstallPath)) lines.Append('\n').Append(NeighbourhoodPanelModel.NoInstallPath);

            return lines.ToString();
        }

        private void SetStatus(string caption, Color color)
        {
            _status.Caption = caption;
            _status.CaptionStyle.Color = color;
        }

        private void SetStatus(string caption)
        {
            SetStatus(caption, UIStyle.Current.SecondaryText);
        }

        // ---- actions -----------------------------------------------------------------------

        /// <summary>
        /// Creates a new, empty neighbourhood in the first free slot, out of the base
        /// <c>TemplateUserData</c> in the resolved install.
        /// </summary>
        /// <remarks>
        /// "Empty" means no Sims in it, not no lots - see
        /// <see cref="NeighbourhoodManager.CreateBlank(string, int)"/>. The target slot is worked
        /// out at press time rather than at relayout, so a second press after a create cannot aim
        /// at the slot the first one just filled.
        /// </remarks>
        private void CreateBlank()
        {
            var target = NeighbourhoodManager.FirstEmptySlot(_userDir);
            if (!NeighbourhoodPanelModel.CanCreateNew(target))
            {
                SetStatus(NeighbourhoodPanelModel.AllSlotsInUse, UIStyle.Current.ActiveSelection);
                return;
            }

            var made = NeighbourhoodManager.CreateBlank(_userDir, target);
            _selectedId = target;
            RefreshList();
            SetStatus(made
                ? NeighbourhoodPanelModel.CreatedBlank(target)
                : NeighbourhoodPanelModel.ActionFailed,
                made ? UIStyle.Current.BtnActive : UIStyle.Current.ActiveSelection);
        }

        /// <summary>
        /// Copies a neighbourhood in from the resolved The Sims install into the selected slot.
        /// </summary>
        /// <remarks>
        /// The backend never writes into a slot that already exists, so pressing this on a slot
        /// that is in use reports a failure rather than replacing the neighbourhood. The
        /// "delete it first" that would make it possible is in the log rather than in a second
        /// confirmation the player would have to reason about.
        /// </remarks>
        private void CopyFromInstall()
        {
            var id = _selectedId;
            if (!NeighbourhoodPanelModel.CanCopyFromInstall(id, InstallPath))
            {
                SetStatus(NeighbourhoodPanelModel.NoInstallPath, UIStyle.Current.ActiveSelection);
                return;
            }

            var copied = NeighbourhoodManager.CopyFromInstall(InstallPath, _userDir, id);
            RefreshList();
            SetStatus(copied
                ? NeighbourhoodPanelModel.CopiedFromInstall(id)
                : NeighbourhoodPanelModel.ActionFailed,
                copied ? UIStyle.Current.BtnActive : UIStyle.Current.ActiveSelection);
        }

        /// <summary>
        /// Opens the rename dialog for the selected slot, pre-filled with the name it shows now.
        /// </summary>
        /// <remarks>
        /// Pre-filled with the <em>display</em> name rather than the stored one, so pressing OK
        /// without typing anything stores exactly what is on screen. A slot with no custom name is
        /// pre-filled with its default label for the same reason: the field is a starting point,
        /// not a record of what the sidecar holds.
        /// </remarks>
        private void AskForRename()
        {
            var id = _selectedId;
            if (!NeighbourhoodPanelModel.CanRename(id, NeighbourhoodManager.Exists(_userDir, id)))
            {
                return;
            }

            new RenameNeighbourhoodAlert(
                SimitoneStrings.Get("359", "Rename neighbourhood"),
                SimitoneStrings.Get("360",
                    "Give this neighbourhood a name of its own. The game will use it in its lists; the"
                        + " folder on disk keeps its number."),
                NeighbourhoodManager.GetDisplayName(_userDir, id),
                (typed) => ApplyRename(id, typed)).Show();
        }

        private void ApplyRename(int id, string typed)
        {
            //The dialog has already refused anything IsValidName rejects, so a failure here can
            //only be the sidecar refusing to be written - reported as a failure rather than as a
            //silent no-op.
            var renamed = NeighbourhoodManager.Rename(_userDir, id, typed);
            RefreshList();
            if (renamed)
            {
                SetStatus(NeighbourhoodPanelModel.RenamedSlot(id, NeighbourhoodManager.SanitizeName(typed)),
                    UIStyle.Current.BtnActive);
            }
            else
            {
                SetStatus(NeighbourhoodPanelModel.ActionFailed, UIStyle.Current.ActiveSelection);
            }
        }

        /// <summary>Drops the selected slot's custom name, putting its default label back.</summary>
        private void ResetName()
        {
            var id = _selectedId;
            if (!NeighbourhoodPanelModel.CanResetName(id, NeighbourhoodManager.GetName(_userDir, id)))
            {
                return;
            }

            var cleared = NeighbourhoodManager.ClearName(_userDir, id);
            RefreshList();
            SetStatus(cleared
                ? NeighbourhoodPanelModel.NameReset(id)
                : NeighbourhoodPanelModel.ActionFailed,
                cleared ? UIStyle.Current.BtnActive : UIStyle.Current.ActiveSelection);
        }

        /// <summary>
        /// Asks before opening the selected slot, and - if there is anything unsaved to lose - asks
        /// whether to save it first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The confirmation is the screen's own <see cref="DialogService"/>, latched the same way
        /// <c>ReturnToNeighbourhood</c> latches its own: a dialog already holding the gate means
        /// we are inside a button handler of the confirmation this very method put up, so there is
        /// nothing to do here. Everything the rules need is read <em>now</em> rather than when the
        /// page opened, so a neighbourhood opened or a family moved in while the dialog was up is
        /// still seen.
        /// </para>
        ///
        /// <para>
        /// Yes saves and switches, No switches without saving, Cancel goes back. The message spells
        /// that out, because a button captioned "No" that opens the neighbourhood anyway is a trap
        /// for precisely the player who is being offered it - see
        /// <see cref="NeighbourhoodPanelModel.OpenConfirmSaveGuide"/>.
        /// </para>
        /// </remarks>
        private void AskForOpen()
        {
            var id = _selectedId;
            var exists = NeighbourhoodManager.Exists(_userDir, id);
            var facts = OpenFacts(id, exists);

            var refusal = NeighbourhoodPanelModel.OpenRefusal(facts);
            if (refusal != null)
            {
                SetStatus(refusal, UIStyle.Current.ActiveSelection);
                return;
            }

            var screen = UIScreen.Current as TS1GameScreen;
            if (screen == null)
            {
                SetStatus(NeighbourhoodPanelModel.NoScreenToSwitch, UIStyle.Current.ActiveSelection);
                return;
            }

            var dialogs = screen.Dialogs;
            if (!dialogs.TryBeginConfirm()) return;

            dialogs.ShowConfirm(
                NeighbourhoodPanelModel.OpenTitle,
                NeighbourhoodPanelModel.OpenConfirmMessage(
                    facts, RowName(id), LoadedRowName()),
                () => ApplyOpen(id, true),
                () => ApplyOpen(id, false),
                null);
        }

        /// <summary>
        /// What the slot the session has open is called, for the confirmation. Empty when nothing
        /// is known to be open - which the rules refuse to switch away from anyway.
        /// </summary>
        private string LoadedRowName()
        {
            if (!_loadedSlotId.HasValue) return null;
            return NeighbourhoodPanelModel.RowName(_loadedSlotId.Value, _names.NameOf(_loadedSlotId.Value));
        }

        /// <summary>
        /// Saves (if asked to), switches the provider onto <paramref name="id"/>, and puts the
        /// picker back together around it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The save comes first and is not advisory. <c>InitSpecific</c> clears the provider's
        /// dirty-avatar set before it has read anything, so a save after the switch would be a
        /// save of the wrong neighbourhood. With no lot loaded - the only state this page can be
        /// open in - the lot save has nothing to write and says so in the log, so the
        /// neighbourhood is committed directly; that is the same call the lot save ends with, and
        /// the one that flushes the dirty avatars' own files.
        /// </para>
        ///
        /// <para>
        /// A save that fails aborts the switch. Carrying on would throw away exactly the changes
        /// the player asked to keep, and they would have been told the save had happened.
        /// </para>
        ///
        /// <para>
        /// <b>Why a failed switch is put back.</b> <c>InitSpecific</c> is not atomic: it clears
        /// three collections, and it may copy a seed folder in, before it opens the first IFF and
        /// throws on a folder that is not a neighbourhood. By then the neighbourhood that was open
        /// is gone, and simply reporting the failure would leave the player with no neighbourhood
        /// at all and a screen full of lot buttons built out of files that are no longer loaded.
        /// So the previous slot is re-initialised, and only if that also fails is the player told
        /// that the session is not what it was - rather than being told the opposite.
        /// </para>
        /// </remarks>
        /// <param name="id">The slot to open.</param>
        /// <param name="saveFirst">Whether the player asked for the open neighbourhood to be saved.</param>
        private void ApplyOpen(int id, bool saveFirst)
        {
            var screen = UIScreen.Current as TS1GameScreen;
            if (screen == null)
            {
                SetStatus(NeighbourhoodPanelModel.NoScreenToSwitch, UIStyle.Current.ActiveSelection);
                return;
            }

            //re-read at press time, for the same reason the delete confirm does: a neighbourhood
            //opened while the dialog was up must still be protected, and must still be the one put
            //back if this switch fails.
            _loadedSlotId = ReadLoadedSlotId();
            var facts = OpenFacts(id, NeighbourhoodManager.Exists(_userDir, id));
            var refusal = NeighbourhoodPanelModel.OpenRefusal(facts);
            if (refusal != null)
            {
                SetStatus(refusal, UIStyle.Current.ActiveSelection);
                return;
            }

            if (saveFirst && !SaveOpenNeighbourhood(screen))
            {
                SetStatus(NeighbourhoodPanelModel.OpenSaveFailed, UIStyle.Current.ActiveSelection);
                screen.Dialogs.ShowAlert(NeighbourhoodPanelModel.OpenTitle,
                    NeighbourhoodPanelModel.OpenSaveFailed);
                return;
            }

            var provider = FSO.Content.Content.Get()?.Neighborhood;
            if (provider == null)
            {
                SimitoneLog.Error("Could not open " + NeighbourhoodManager.SlotFolderName(id)
                    + ": the content manager has no neighbourhood provider. Nothing was switched.");
                SetStatus(NeighbourhoodPanelModel.OpenFailed, UIStyle.Current.ActiveSelection);
                screen.Dialogs.ShowAlert(NeighbourhoodPanelModel.OpenTitle,
                    NeighbourhoodPanelModel.OpenFailed);
                return;
            }

            try
            {
                provider.InitSpecific(id);
            }
            catch (Exception e)
            {
                SimitoneLog.Error("Could not open " + NeighbourhoodManager.SlotFolderName(id)
                    + ". Putting the neighbourhood that was open back.", e);

                var restored = PutBackLoadedSlot(provider, _loadedSlotId);
                RefreshList();

                var caption = restored
                    ? NeighbourhoodPanelModel.OpenFailed
                    : NeighbourhoodPanelModel.OpenFailedFatal;
                SetStatus(caption, UIStyle.Current.ActiveSelection);
                screen.Dialogs.ShowAlert(NeighbourhoodPanelModel.OpenTitle, caption);
                return;
            }

            //the provider is on the new slot; the picker and the switcher in front of it were
            //built out of the old one's files, so both have to be rebuilt before the player can
            //see or click any of it. This is the one call the page makes into the screen.
            //
            //Guarded, because this one is the part most likely to throw of anything on the page:
            //the picker opens a House{NN}.iff per lot, and a slot that came from anywhere but a
            //real neighbourhood can be missing one. Letting that escape would take the game down
            //from inside a button handler, and the switch itself has already succeeded by then -
            //so the player is told which half went wrong.
            try
            {
                screen.RefreshNeighbourhoodUi();
            }
            catch (Exception e)
            {
                SimitoneLog.Error("Opened " + NeighbourhoodManager.SlotFolderName(id)
                    + ", but the neighbourhood map could not be built on it at '" + provider.UserPath
                    + "'. The neighbourhood is open; the map is not.", e);

                _selectedId = id;
                RefreshList();
                SetStatus(NeighbourhoodPanelModel.OpenMapFailed, UIStyle.Current.ActiveSelection);
                screen.Dialogs.ShowAlert(NeighbourhoodPanelModel.OpenTitle,
                    NeighbourhoodPanelModel.OpenMapFailed);
                return;
            }

            SimitoneLog.Info("Opened " + NeighbourhoodManager.SlotFolderName(id) + " (slot " + id
                + ") at '" + provider.UserPath + "'; the neighbourhood map and its switcher were"
                + " rebuilt on the new folder.");

            _selectedId = id;
            RefreshList();
            SetStatus(NeighbourhoodPanelModel.OpenedSlot(id), UIStyle.Current.BtnActive);
        }

        /// <summary>
        /// Writes the neighbourhood that is open now, so a switch cannot throw it away. Reports
        /// whether the save happened; a false here is a switch that must not go ahead.
        /// </summary>
        private static bool SaveOpenNeighbourhood(TS1GameScreen screen)
        {
            //a lot save ends by committing the neighbourhood, so with a lot loaded that is both
            //the lot and the neighbourhood in one call. There is no lot on the screen this page is
            //opened from, and SaveService logs a "nothing to save" warning for that case, so the
            //neighbourhood is written directly.
            if (screen.InLot)
            {
                screen.Save();
                return true;
            }

            try
            {
                var provider = FSO.Content.Content.Get()?.Neighborhood;
                if (provider == null)
                {
                    SimitoneLog.Error("Could not save the neighbourhood before switching: there is"
                        + " no neighbourhood provider. Nothing was switched.");
                    return false;
                }

                provider.SaveNeighbourhood(true);
                SimitoneLog.Info("Saved the neighbourhood at '" + provider.UserPath
                    + "' before switching away from it.");
                return true;
            }
            catch (Exception e)
            {
                SimitoneLog.Error("Could not save the neighbourhood before switching away from it."
                    + " Nothing was switched, so the changes are still in memory.", e);
                return false;
            }
        }

        /// <summary>
        /// Re-initialises the provider onto the slot that was open before a failed switch, so the
        /// session is left what it was rather than with no neighbourhood at all.
        /// </summary>
        /// <returns><c>false</c> when even that failed, which the caller has to say out loud.</returns>
        private static bool PutBackLoadedSlot(FSO.Content.TS1.TS1NeighborhoodProvider provider, int? loadedSlotId)
        {
            if (!loadedSlotId.HasValue)
            {
                //the rules refuse a switch when nothing is known to be open, so this is
                //unreachable from the button. It is still handled: an un-restorable provider that
                //cannot even name the old slot is exactly the case that must not be papered over.
                SimitoneLog.Error("Could not put the previous neighbourhood back: Simitone does not"
                    + " know which slot was open. Restart the game.");
                return false;
            }

            try
            {
                provider.InitSpecific(loadedSlotId.Value);
                SimitoneLog.Info("Put " + NeighbourhoodManager.SlotFolderName(loadedSlotId.Value)
                    + " back after a failed switch.");
                return true;
            }
            catch (Exception e)
            {
                SimitoneLog.Error("Could not put "
                    + NeighbourhoodManager.SlotFolderName(loadedSlotId.Value)
                    + " back after a failed switch. The game has no open neighbourhood; restart it.", e);
                return false;
            }
        }

        /// <summary>
        /// Confirms, then deletes the selected slot. A confirmation because this is the only
        /// irreversible thing on the page.
        /// </summary>
        private void AskForDelete()
        {
            var id = _selectedId;
            var refusal = NeighbourhoodPanelModel.DeleteRefusal(id, _loadedSlotId);
            if (refusal != null)
            {
                SetStatus(refusal, UIStyle.Current.ActiveSelection);
                return;
            }

            var name = RowName(id);
            Confirm(SimitoneStrings.Get("361", "Delete neighbourhood"),
                SimitoneStrings.Format("362",
                    "Delete " + name + " and everything in it? This cannot be undone.", name),
                () => ApplyDelete(id));
        }

        private void ApplyDelete(int id)
        {
            //The loaded slot is re-read at press time rather than captured when the confirm was
            //built: a neighbourhood opened while the dialog was up must still be protected.
            _loadedSlotId = ReadLoadedSlotId();
            var deleted = NeighbourhoodManager.Delete(_userDir, id, _loadedSlotId);
            if (deleted && _selectedId == id)
            {
                _selectedId = NeighbourhoodPanelModel.InitialSelection(ExistsPerSlot());
            }
            RefreshList();
            SetStatus(deleted
                ? NeighbourhoodPanelModel.DeletedSlot(id)
                : NeighbourhoodPanelModel.ActionFailed,
                deleted ? UIStyle.Current.BtnActive : UIStyle.Current.ActiveSelection);
        }

        private List<bool> ExistsPerSlot()
        {
            var states = new List<bool>(NeighbourhoodManager.SlotCount);
            for (var id = NeighbourhoodManager.MinSlotId; id <= NeighbourhoodManager.MaxSlotId; id++)
            {
                states.Add(NeighbourhoodManager.Exists(_userDir, id));
            }
            return states;
        }

        // ---- dialogs -----------------------------------------------------------------------

        /// <summary>
        /// Shows a modal Yes/No, closing itself before running <paramref name="onYes"/>.
        /// </summary>
        /// <remarks>
        /// Goes through <see cref="DialogService.ShowPlainConfirm"/> rather than building its own
        /// alert. This is the one confirmation on the page that needs no
        /// <c>TryBeginConfirm</c> - Delete cannot be reached from inside another button's handler,
        /// so there is no re-entry to suppress - and it used to keep its own copy of the "both
        /// buttons handler-free, Yes gets the work afterwards, let the alert do the closing" rule.
        /// That is the shape of copy that drifts from the one the service owns, so it is now one
        /// call beside the service's own.
        ///
        /// <para>
        /// The service it goes through is the <i>screen's</i>, the same one the switch
        /// confirmation and <c>CloseAttempt</c> use, so it matters that this dialog takes that
        /// service's claim: a Delete confirmation that did not would answer "no dialog is up"
        /// while a modal was, and the quit confirmation would land on top of it.
        /// </para>
        /// </remarks>
        private void Confirm(string title, string message, Action onYes)
        {
            var screen = UIScreen.Current as TS1GameScreen;
            if (screen == null)
            {
                // The switch confirmation refuses outright rather than showing into nothing; the
                // delete is irreversible, so it is not one to go ahead on a hunch.
                SimitoneLog.Warn("The neighbourhood page could not confirm a delete: there is no game screen to confirm it on.");
                return;
            }
            screen.Dialogs.ShowPlainConfirm(title, message, DeleteConfirmWidth, onYes);
        }

        /// <summary>
        /// The width the delete confirmation's plate is built at: long enough for a neighbourhood
        /// name inside the sentence without wrapping onto three lines.
        /// </summary>
        private const int DeleteConfirmWidth = 520;

        /// <summary>
        /// The rename prompt: a text-entry <see cref="UIMobileAlert"/> whose OK button is enabled
        /// only while what is in the box is a name <see cref="NeighbourhoodManager"/> will store.
        /// </summary>
        /// <remarks>
        /// A subclass purely so the OK button can be re-gated every frame and the reason shown.
        /// <see cref="UIMobileAlert"/> publishes its button map and its response text but has no
        /// "text changed" event, and the alternative - accepting whatever came back and letting
        /// the backend refuse it - would leave the player with a dialog that closes and nothing
        /// happening.
        ///
        /// <para>
        /// The reason label hangs off the OK button's own position rather than off the text box
        /// (which is private) or a fixed offset (which would be wrong the moment the buttons wrap
        /// onto a second row on a narrow window).
        /// </para>
        /// </remarks>
        private sealed class RenameNeighbourhoodAlert : UIMobileAlert
        {
            private const int ReasonSize = 16;
            private const int ReasonGap = 12;
            private const int ReasonInset = 30;

            private readonly UILabel _reason;

            public RenameNeighbourhoodAlert(string title, string message, string initial, Action<string> onAccept)
                : base(BuildOptions(title, message))
            {
                _reason = new UILabel();
                _reason.NewStyle(UIStyle.Current.SecondaryText, ReasonSize);
                _reason.Alignment = TextAlignment.Top | TextAlignment.Right;
                Add(_reason);

                //set after construction: the base builds the text box from options only, and this
                //is the field's starting value rather than a stored one.
                ResponseText = initial;

                //Both buttons already have the base class's internal close (their handlers were
                //null), so this runs after it - the dialog closes, then the name is applied.
                ButtonMap[UIAlertButtonType.OK].OnButtonClick += (b) => onAccept?.Invoke(ResponseText);

                RefreshGating();
            }

            private static UIAlertOptions BuildOptions(string title, string message)
            {
                return new UIAlertOptions()
                {
                    Title = title,
                    Message = message,
                    Width = 560,
                    TextEntry = true,
                    //the field will not hold more than the backend can store, so the button greys
                    //out for a reason the player can see rather than characters silently vanishing
                    MaxChars = NeighbourhoodManager.MaxNameLength,
                    Buttons = UIAlertButton.OkCancel(null, null)
                };
            }

            public void Show()
            {
                UIScreen.GlobalShowDialog(this, true);
            }

            public override void Update(UpdateState state)
            {
                base.Update(state);
                //RefreshSize may have moved the buttons (a resize, or a re-wrap of the message),
                //so the reason follows them rather than sitting where it was last frame.
                RefreshGating();
            }

            private void RefreshGating()
            {
                var rejection = NeighbourhoodPanelModel.RenameRejection(ResponseText);
                ButtonMap[UIAlertButtonType.OK].Disabled = rejection != null;

                var caption = rejection ?? string.Empty;
                if (_reason.Caption == caption && _reason.Visible) return;

                _reason.Caption = caption;
                var ok = ButtonMap[UIAlertButtonType.OK];
                _reason.Position = new Vector2(ok.X - Width + ReasonInset, ok.Y - ReasonSize - ReasonGap);
                _reason.Size = new Vector2(Width - (ReasonInset * 2), ReasonSize);
                _reason.Visible = rejection != null;
            }
        }

        /// <summary>
        /// One neighbourhood slot in the list: a plate, the name on the left, and the detail line
        /// the row model produced on the right. The plate is a scaled white pixel rather than a
        /// texture, so a row can be any width and the selection tint costs one colour multiply.
        /// </summary>
        private sealed class NeighbourhoodSlotRow : UITSContainer
        {
            private readonly UILabel _name;
            private readonly UILabel _detail;
            private readonly Action<int> _onSelected;
            private readonly Texture2D _plate;
            private readonly float _rowWidth;
            private readonly int _rowHeight;

            private float _highlight;

            /// <summary>The slot this row stands for. Set by <see cref="Bind"/> before the row is shown.</summary>
            public int SlotId { get; private set; }

            public NeighbourhoodSlotRow(float width, int rowHeight, int bodySize, int detailSize, Action<int> onSelected)
            {
                _onSelected = onSelected;
                _rowWidth = width;
                _rowHeight = rowHeight;
                _plate = TextureGenerator.GetPxWhite(GameFacade.GraphicsDevice);

                _name = new UILabel();
                _name.NewStyle(UIStyle.Current.Text, bodySize);
                _name.Alignment = TextAlignment.Middle | TextAlignment.Left;
                _name.Position = new Vector2(12, 4);
                _name.Size = new Vector2(width * 0.45f, rowHeight - 8);
                Add(_name);

                _detail = new UILabel();
                _detail.NewStyle(UIStyle.Current.SecondaryText, detailSize);
                _detail.Alignment = TextAlignment.Middle | TextAlignment.Right;
                _detail.Position = new Vector2(width * 0.45f, 4);
                _detail.Size = new Vector2(width * 0.55f - 12, rowHeight - 8);
                Add(_detail);
            }

            /// <summary>
            /// Points the row at a slot. Both captions are elided rather than wrapped: a row is one
            /// line tall, and a name that does not fit is still recognisable from its first
            /// characters.
            /// </summary>
            public void Bind(int slotId, string name, string detail)
            {
                SlotId = slotId;
                _name.Caption = _name.CaptionStyle.TruncateToWidth(name ?? string.Empty, (int)_name.Size.X);
                _detail.Caption = _detail.CaptionStyle.TruncateToWidth(detail ?? string.Empty, (int)_detail.Size.X);
            }

            public override void Selected()
            {
                base.Selected();
                _highlight = 1f;
                _onSelected?.Invoke(SlotId);
            }

            public override void Deselected()
            {
                base.Deselected();
                _highlight = 0f;
            }

            public override void Draw(UISpriteBatch batch)
            {
                //selected rows are tinted towards the selection colour rather than switched to it,
                //so the row's own text stays readable on top of the plate
                var plate = Color.Lerp(new Color(40, 60, 90), UIStyle.Current.ActiveSelection, _highlight * 0.45f);
                DrawLocalTexture(batch, _plate, null, Vector2.Zero, new Vector2(_rowWidth, _rowHeight), plate);
                base.Draw(batch);
            }
        }
    }
}
