/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;

namespace Simitone.Client.UI.Panels.LotControls
{
    /// <summary>
    /// The Makin' Magic book dialogs: the family spellbook
    /// (<see cref="VMDialogType.TS1Spellbook"/>) and the family cookbook
    /// (<see cref="VMDialogType.TS1Cookbook"/>).
    ///
    /// <para>
    /// <b>Where the list comes from.</b> The engine does not hand one over. A dialog arrives as
    /// a <see cref="VMDialogInfo"/> carrying the caller's title, message and button captions and
    /// nothing else - no spell list, no ingredient list, no array of object GUIDs. So the panel
    /// builds the list the only two places a client can reach:
    /// <list type="bullet">
    /// <item>the game's own catalogue in <c>GameData/magic.iff</c> (25 spell titles, 9 recipe
    /// titles, each with a paragraph of flavour text), read by <see cref="MakingMagicTables"/>;</item>
    /// <item>which of those the sim actually owns, which <em>is</em> in the engine: Makin' Magic
    /// keeps spells and recipe components as tokens in the sim's neighbourhood inventory, under
    /// <see cref="MakingMagic.MagicTokenType"/> and <see cref="MakingMagic.IngredientTokenType"/>,
    /// and the objects those GUIDs name are in the world object catalogue under their own
    /// spell names.</item>
    /// </list>
    /// The two are lined up by name, which is the same string in both, and the owned ones are
    /// marked. When <c>magic.iff</c> is not there at all the panel falls back to listing the
    /// owned objects alone, by their own catalogue names.
    /// </para>
    ///
    /// <para>
    /// <b>What happens to the answer.</b> The choice goes back through
    /// <c>VMNetDialogResponseCmd</c> exactly as the phone book and the outfit chooser answer
    /// theirs: a 0/1 in <c>ResponseCode</c> and the entry's index in <c>ResponseText</c>. That
    /// unblocks the thread - which matters, because a TS1 blocking dialog holds the whole lot at
    /// speed -2 until it is answered - and the engine parses the index into the dialog's temp
    /// register: <c>VMDialogPrivateStrings.ExecuteGeneric</c> lists <c>TS1Spellbook</c> and
    /// <c>TS1Cookbook</c> alongside the other numeric dialogs, so both take the same
    /// <c>NumericEntry</c> path the travel picker and the phone book take. The object's script
    /// does not branch on that register, so the answer unblocks the sim and nothing more; a
    /// cast is not actually performed from here.
    /// </para>
    /// </remarks>
    public class UIMakingMagicAlert : UIMobileDialog
    {
        /// <summary>The book this dialog is showing.</summary>
        public MakingMagicBook Book { get; private set; }

        /// <summary>The panel, exposed for the same reason the other dialogs expose theirs.</summary>
        public UIMakingMagicPanel BookPanel { get; private set; }

        /// <summary>
        /// Raised with the chosen <c>magic.iff</c> entry index, or -1 when the player backed
        /// out. Raised exactly once per dialog, including when something else tore the dialog
        /// down before the player answered.
        /// </summary>
        public event Action<int> OnResult;

        private bool answered;

        public UIMakingMagicAlert(VMDialogInfo info, MakingMagicBook book)
            : this(book, info?.Caller as VMAvatar, info?.Message)
        {
        }

        public UIMakingMagicAlert(MakingMagicBook book, VMAvatar caller, string message)
        {
            Book = book;
            SetHeight(600);
            Caption = MakingMagic.FormatBookTitle(book,
                MakingMagicTables.Shared.Dialog(MakingMagic.TitleCaption(book)),
                MakingMagicCaller.FamilyName(caller));

            BookPanel = new UIMakingMagicPanel(book, caller, message);
            BookPanel.Position = new Vector2((Width - UIMakingMagicPanel.PanelWidth) / 2, 110);
            BookPanel.OnResult += (index) => Answer(index);
            Add(BookPanel);
        }

        /// <summary>
        /// Sends the answer and closes, at most once.
        /// </summary>
        private void Answer(int entryIndex)
        {
            if (answered) return;
            answered = true;
            OnResult?.Invoke(entryIndex);
            Close();
        }

        /// <summary>
        /// Answers a cancelled dialog that was removed rather than dismissed.
        /// </summary>
        /// <remarks>
        /// A TS1 blocking dialog stops the lot until it is answered, so a spellbook that
        /// disappeared because a travel picker arrived on top of it would otherwise leave a
        /// thread waiting out the engine's 30-minute dialog timeout. Answering from
        /// <see cref="UIElement.Removed"/> covers every way the dialog can leave the screen, and
        /// the <c>answered</c> guard makes it a no-op on the ordinary path, where the answer has
        /// already gone out before <see cref="Close"/>'s tween finishes.
        /// </remarks>
        public override void Removed()
        {
            base.Removed();
            if (answered) return;
            answered = true;
            OnResult?.Invoke(-1);
        }

        public override void GameResized()
        {
            base.GameResized();
            BookPanel.Position = new Vector2((Width - UIMakingMagicPanel.PanelWidth) / 2, 110);
        }
    }

    /// <summary>
    /// The contents of a Makin' Magic book: the entries on the current page, the description of
    /// whichever one is highlighted, and the four buttons.
    /// </summary>
    /// <remarks>
    /// Positions are absolute and re-applied on a window resize, in the same way
    /// <c>UICallNeighborPanel</c> does it, because the panel is a fixed width that is centred by
    /// its dialog rather than laid out by the screen.
    /// </remarks>
    public class UIMakingMagicPanel : UIContainer
    {
        /// <summary>Fixed content width; the dialog centres this.</summary>
        public const int PanelWidth = 1030;

        /// <summary>Height of the entry list and the description column.</summary>
        private const int ColumnHeight = 380;

        private const int ButtonWidth = 275;
        private const int RightColumn = 620;

        /// <summary>Raised with the chosen entry index, or -1 for a cancel.</summary>
        public event Action<int> OnResult;

        private readonly MakingMagicBook book;
        private readonly List<MakingMagicEntry> entries = new List<MakingMagicEntry>();
        private readonly List<int> entryIndices = new List<int>();

        private UITouchStringList entryList;
        private UILabel messageLabel;
        private UILabel descriptionLabel;
        private UILabel pageLabel;
        private UIBigButton commitButton;
        private UIBigButton cancelButton;
        private UIBigButton backButton;
        private UIBigButton nextButton;

        private int page = 1;
        private int selected = -1;

        /// <summary>How many entries the book holds, across every page.</summary>
        public int EntryCount
        {
            get { return entries.Count; }
        }

        public UIMakingMagicPanel(MakingMagicBook book, VMAvatar caller, string message)
        {
            this.book = book;

            var tables = MakingMagicTables.Shared;

            messageLabel = MakeLabel(0, 0, PanelWidth, 44, 15);
            messageLabel.Wrapped = true;
            messageLabel.MaxLines = 2;
            messageLabel.Caption = message ?? "";
            Add(messageLabel);

            entryList = new UITouchStringList();
            entryList.Size = new Vector2(360, ColumnHeight);
            entryList.Position = new Vector2(0, 60);
            entryList.OnSelectionChange += OnEntryPicked;
            Add(entryList);

            descriptionLabel = MakeLabel(390, 60, PanelWidth - 390, ColumnHeight, 15);
            descriptionLabel.Wrapped = true;
            descriptionLabel.MaxLines = 16;
            descriptionLabel.Alignment = TextAlignment.Top | TextAlignment.Left;
            Add(descriptionLabel);

            pageLabel = MakeLabel(RightColumn, 330, ButtonWidth, 30, 15);
            pageLabel.Alignment = TextAlignment.Middle | TextAlignment.Left;
            Add(pageLabel);

            commitButton = MakeButton(true);
            // The four words the panel adds around magic.iff's own strings, as ids 450 to 453 of
            // Simitone's own table. TS1's spellbook dialogs are the one Makin' Magic dialog pair
            // that had no string of its own for its buttons, so there is nothing to read them out
            // of and the English is the second argument of every lookup.
            commitButton.Caption = book == MakingMagicBook.Cookbook
                ? SimitoneStrings.Get("450", "Cook")
                : SimitoneStrings.Get("451", "Cast");
            commitButton.OnButtonClick += (btn) => { if (selected >= 0) OnResult?.Invoke(selected); };
            Add(commitButton);

            cancelButton = MakeButton(false);
            cancelButton.Caption = SimitoneStrings.Get("452", "Cancel");
            cancelButton.OnButtonClick += (btn) => OnResult?.Invoke(-1);
            Add(cancelButton);

            backButton = MakeButton(false);
            backButton.Caption = tables.Dialog(MakingMagicCaption.Back);
            backButton.OnButtonClick += (btn) => ShowPage(page - 1);
            Add(backButton);

            nextButton = MakeButton(false);
            nextButton.Caption = tables.Dialog(MakingMagicCaption.Next);
            nextButton.OnButtonClick += (btn) => ShowPage(page + 1);
            Add(nextButton);

            // the entries are built once: a book does not change while it is open, and the
            // page is a view onto this list rather than a second copy of it
            BuildEntries(MakingMagicCaller.InventoryEntries(caller, book));
            ShowPage(1);
            PlaceControls();
        }

        private void BuildEntries(IReadOnlyList<MakingMagicEntry> found)
        {
            entries.Clear();
            entryIndices.Clear();

            var tables = MakingMagicTables.Shared;
            var titles = tables.Titles(book);

            // the game's own catalogue is the spine of the book: the indices it hands out are the
            // numbers the engine would want, and its text is the only description of a spell
            // anywhere in the game
            var usable = MakingMagic.UsableEntries(titles);
            for (int i = 0; i < usable.Count; i++)
            {
                var index = usable[i];
                var title = titles[index] ?? "";
                entries.Add(new MakingMagicEntry()
                {
                    Index = index,
                    Title = title,
                    Description = tables.Description(book, index),
                    Owned = MakingMagicCaller.IsOwned(found, title),
                });
            }

            // nothing readable in magic.iff (no expansion, or a file we do not understand): the
            // entries the sim owns are still worth listing, under their own object names
            if (entries.Count == 0 && found != null)
            {
                for (int i = 0; i < found.Count; i++)
                {
                    var entry = found[i];
                    entries.Add(new MakingMagicEntry()
                    {
                        Index = i,
                        Title = entry.Title,
                        Description = "",
                        Owned = true,
                    });
                }
            }

            for (int i = 0; i < entries.Count; i++) entryIndices.Add(entries[i].Index);
        }

        private void ShowPage(int wanted)
        {
            var tables = MakingMagicTables.Shared;
            var pages = MakingMagic.PageCount(entries.Count);
            page = MakingMagic.ClampPage(wanted, pages);

            var onPage = MakingMagic.PageOf(entryIndices, page);

            entryList.BackingList.Clear();
            for (int i = 0; i < onPage.Count; i++)
            {
                var entry = entries[onPage[i]];
                // the brackets are the panel's, not the translation's: a language that does not
                // set an aside off with them can still have them drawn around whatever 453 says
                entryList.BackingList.Add(entry.Owned ? entry.Title
                    : entry.Title + " (" + SimitoneStrings.Get("453", "not yet discovered") + ")");
            }
            entryList.Refresh();

            // a page change drops the highlight with the old page: the row numbers the player
            // clicked no longer mean what they did, and Cast stays greyed until a new pick
            selected = -1;
            ShowDescription(null);

            pageLabel.Caption = MakingMagic.FormatPageCaption(tables.Dialog(MakingMagicCaption.PageOf), page, pages);
            backButton.Disabled = page <= 1;
            nextButton.Disabled = page >= pages;
            // nothing is picked on a fresh page (selected was just cleared), so the commit button
            // has to start greyed - as the comment above says - rather than only when the book is
            // empty
            commitButton.Disabled = true;
        }

        private void OnEntryPicked(int row)
        {
            var onPage = MakingMagic.PageOf(entryIndices, page);
            if (row < 0 || row >= onPage.Count)
            {
                selected = -1;
                ShowDescription(null);
                commitButton.Disabled = true;
                return;
            }

            selected = entries[onPage[row]].Index;
            ShowDescription(entries[onPage[row]]);
            commitButton.Disabled = false;
        }

        private void ShowDescription(MakingMagicEntry entry)
        {
            if (entry == null)
            {
                descriptionLabel.Caption = "";
                return;
            }
            descriptionLabel.Caption = string.IsNullOrEmpty(entry.Description) ? entry.Title : entry.Description;
        }

        private UILabel MakeLabel(int x, int y, int width, int height, int size)
        {
            // UILabel hands out the shared default style, so it has to be cloned before it is
            // restyled or every other label in the game inherits this size
            var label = new UILabel()
            {
                Position = new Vector2(x, y),
                Size = new Vector2(width, height),
                CaptionStyle = TextStyle.DefaultLabel.Clone(),
            };
            label.CaptionStyle.Size = size;
            label.CaptionStyle.Color = UIStyle.Current.Text;
            return label;
        }

        private UIBigButton MakeButton(bool green)
        {
            var button = new UIBigButton(green) { Width = ButtonWidth };
            return button;
        }

        private void PlaceControls()
        {
            commitButton.Position = new Vector2(RightColumn, 60);
            cancelButton.Position = new Vector2(RightColumn, 150);
            backButton.Position = new Vector2(RightColumn, 390);
            nextButton.Position = new Vector2(RightColumn, 470);
        }

        public override void GameResized()
        {
            base.GameResized();
            PlaceControls();
        }
    }

    /// <summary>One row of a book.</summary>
    public class MakingMagicEntry
    {
        /// <summary>
        /// The entry's index in <c>magic.iff</c>'s titles table - the number that goes back to the
        /// engine. When the table could not be read at all this is the entry's position in the
        /// sim's inventory instead.
        /// </summary>
        public int Index { get; set; }

        /// <summary>The spell's or recipe's name.</summary>
        public string Title { get; set; }

        /// <summary>What it does, out of <c>magic.iff</c>. May be empty.</summary>
        public string Description { get; set; }

        /// <summary>Whether the sim has this one.</summary>
        public bool Owned { get; set; }
    }

    /// <summary>
    /// The two lookups a book panel needs from the running game, in one place: which sim is
    /// asking, and what that sim owns.
    /// </summary>
    /// <remarks>
    /// Everything here is defensive on purpose. These calls run while a blocking dialog is up -
    /// the lot is stopped at speed -2 - and the data they reach belongs to the neighbourhood
    /// chunk rather than to the VM, so it can legitimately be absent: an unplayed install has no
    /// inventory at all, and a spell GUID can name an object the world catalogue has never
    /// heard of.
    /// </remarks>
    internal static class MakingMagicCaller
    {
        /// <summary>
        /// The name of the family the book belongs to, for the "%s Family Spellbook" caption,
        /// or an empty string when there is no family to name.
        /// </summary>
        public static string FamilyName(VMAvatar caller)
        {
            try
            {
                var vm = caller?.Thread?.Context?.VM;
                var family = vm?.TS1State?.CurrentFamily;
                if (family == null) return "";
                return Content.Get().Neighborhood?.GetFamilyString((ushort)family.ChunkID)?.GetString(0) ?? "";
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("magic book: could not resolve the family name", e);
                return "";
            }
        }

        /// <summary>
        /// The spell (or ingredient) entries in a sim's neighbourhood inventory, named after the
        /// objects their GUIDs point at.
        /// </summary>
        /// <remarks>
        /// The lookup order is <see cref="MakingMagic.TokenTypesFor"/>: the book's own entry type
        /// first, the other one only if that found nothing. An entry whose object cannot be
        /// named is skipped rather than shown blank - a blank row the player can pick but cannot
        /// read is worse than a shorter list.
        /// </remarks>
        public static IReadOnlyList<MakingMagicEntry> InventoryEntries(VMAvatar caller, MakingMagicBook book)
        {
            var found = new List<MakingMagicEntry>();
            if (caller == null || book == MakingMagicBook.None) return found;

            List<InventoryItem> inventory;
            try
            {
                var neighbourhood = Content.Get().Neighborhood;
                if (neighbourhood == null) return found;
                inventory = neighbourhood.GetInventoryByNID(
                    caller.GetPersonData(VMPersonDataVariable.NeighborId));
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("magic book: could not read the sim's inventory", e);
                return found;
            }
            if (inventory == null) return found;

            var types = MakingMagic.TokenTypesFor(book);
            for (int t = 0; t < types.Count; t++)
            {
                found.Clear();
                for (int i = 0; i < inventory.Count; i++)
                {
                    var item = inventory[i];
                    if (item == null || item.Type != types[t] || item.Count == 0) continue;
                    var title = ObjectName(item.GUID);
                    if (title == null) continue;
                    found.Add(new MakingMagicEntry() { Title = title, Owned = true });
                }
                if (found.Count > 0) break;
            }
            return found;
        }

        /// <summary>Is this catalogue name one the sim owns?</summary>
        public static bool IsOwned(IReadOnlyList<MakingMagicEntry> owned, string title)
        {
            if (owned == null || string.IsNullOrWhiteSpace(title)) return false;
            for (int i = 0; i < owned.Count; i++)
            {
                if (string.Equals(owned[i].Title?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// A world object's catalogue name, or null when the object is not in the catalogue.
        /// </summary>
        /// <remarks>
        /// The same two-hop lookup the phone book uses for a neighbour's name: GUID to
        /// <c>GameObject</c>, then the object's own <c>CTSS</c> table for the name the game
        /// would have shown. The Makin' Magic spell objects are named after their spells ("Banish",
        /// "LoveStruck"), which is what makes them line up with <c>magic.iff</c>'s titles.
        /// </remarks>
        private static string ObjectName(uint guid)
        {
            try
            {
                var obj = Content.Get().WorldObjects?.Get(guid);
                if (obj?.Resource == null || obj.OBJ == null) return null;
                var name = obj.Resource.Get<CTSS>(obj.OBJ.CatalogStringsID)?.GetString(0);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
