/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics.Primitives;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Which of Makin' Magic's two book dialogs a script asked for.
    /// </summary>
    /// <remarks>
    /// The engine has one dialog type per book - <see cref="VMDialogType.TS1Spellbook"/> (12)
    /// and <see cref="VMDialogType.TS1Cookbook"/> (15) - and the two are told apart by nothing
    /// but that number. There is a third Makin' Magic dialog,
    /// <see cref="VMDialogType.TS1TransformMe"/> (14), which is the "transform me" charm and
    /// which this type deliberately does not cover: it is raised by
    /// <c>SpellTransform.iff</c> with no data of its own, and it has no table in
    /// <c>magic.iff</c>, so there is nothing for a book panel to list.
    /// </remarks>
    public enum MakingMagicBook
    {
        /// <summary>Not a Makin' Magic book dialog.</summary>
        None = 0,

        /// <summary>The family spellbook: <c>SpellBook.iff</c>'s "Interaction - Read".</summary>
        Spellbook,

        /// <summary>The family cookbook: <c>Cookbook.iff</c>'s "Interaction - Read".</summary>
        Cookbook,
    }

    /// <summary>
    /// The five strings the spellbook/cookbook dialog itself is captioned with, in the order
    /// they sit in <c>magic.iff</c>'s "Magic Book Dialog Strings" table.
    /// </summary>
    public enum MakingMagicCaption
    {
        /// <summary>"%s Family Spellbook" - the family's name goes in the <c>%s</c>.</summary>
        SpellbookTitle = 0,

        /// <summary>"%s Family Cookbook".</summary>
        CookbookTitle = 1,

        /// <summary>The caption of the button that turns the page back.</summary>
        Back = 2,

        /// <summary>The caption of the button that turns the page forward.</summary>
        Next = 3,

        /// <summary>"Page %d of %d".</summary>
        PageOf = 4,
    }

    /// <summary>
    /// The parts of the Makin' Magic dialogs that are pure data: where the game's own text
    /// lives, which entries in it are real rather than placeholders, how the pages are cut, and
    /// what a choice looks like on the wire.
    ///
    /// <para>
    /// <b>Where the text is.</b> Makin' Magic ships a loose data file,
    /// <c>&lt;install&gt;/GameData/magic.iff</c>, holding six string tables. Nothing in the
    /// content pipeline opens it - not <c>ContentStrings</c>, which reads
    /// <c>GameData/UIText.iff</c>, and not the world object providers, which read
    /// <c>GameData/Global/Global.far</c> - so it has to be opened by hand.
    /// <see cref="MakingMagicTables"/> is that hand; this type is everything that does not need
    /// the file open.
    /// </para>
    ///
    /// <para>
    /// <b>What a book shows.</b> The two tables that matter are the titles and the
    /// descriptions: 25 spell titles ("Unknown Charm", "Dish Wish", ... "Unknown Spell") and 10
    /// recipe titles ("not used", then the nine nectar-and-baking entries). They are in
    /// <em>the game's</em> order, and that order is the number the engine would want back, so
    /// the index into the raw table - not into a filtered list - is what this type carries
    /// around.
    /// </para>
    ///
    /// <para>
    /// <b>Which entries are real.</b> Some slots are the expansion's own placeholders: "not
    /// used", "TBD Charm (If wanted)", "Unknown Charm", "Unknown Spell". Every one of them is
    /// marked "Needs translation" in <c>magic.iff</c>, which means they read exactly the same in
    /// every language - so <see cref="IsPlaceholderTitle"/> matches on those words rather than on
    /// index ranges, and works in a build that is not English.
    /// </para>
    ///
    /// <para>
    /// Nothing here touches a file, a VM or a graphics device.
    /// </para>
    /// </remarks>
    public static class MakingMagic
    {
        #region where the game keeps the text

        /// <summary>Folder inside a The Sims install that <see cref="ResourceName"/> lives in.</summary>
        public const string ResourceFolder = "GameData";

        /// <summary>
        /// Makin' Magic's loose string file. Present in every edition that has the expansion and
        /// absent from every edition that does not, which is why every lookup here has an
        /// English fallback rather than an assumption.
        /// </summary>
        public const string ResourceName = "magic.iff";

        /// <summary>Chunk label of the spell/recipe <em>titles</em>.</summary>
        public const string SpellTitlesLabel = "Spell Titles";

        /// <summary>Chunk label of the spell <em>descriptions</em>.</summary>
        public const string SpellDescriptionLabel = "Spell Description";

        /// <summary>Chunk label of the cookbook recipe titles.</summary>
        public const string RecipeTitlesLabel = "Nectar Bar/ Bakers Oven Titles";

        /// <summary>Chunk label of the cookbook recipe descriptions.</summary>
        public const string RecipeDescriptionLabel = "Nectar/Bakers Oven Description";

        /// <summary>Chunk label of the dialog's own five captions.</summary>
        public const string DialogStringsLabel = "Magic Book Dialog Strings";

        /// <summary>
        /// Path of <see cref="ResourceName"/> inside a The Sims installation, or null for a
        /// null/empty install path so the result can go straight into
        /// <see cref="File.Exists"/>.
        /// </summary>
        public static string ResourcePath(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return null;
            return Path.Combine(installPath, ResourceFolder, ResourceName);
        }

        #endregion

        #region which book, and out of which tables

        /// <summary>
        /// The book a dialog type asks for, or <see cref="MakingMagicBook.None"/> for anything
        /// that is not one of the two Makin' Magic books.
        /// </summary>
        /// <remarks>
        /// The binding is to the engine's own enum, so renaming
        /// <c>VMDialogType.TS1Spellbook</c> breaks this at compile time rather than quietly
        /// turning the panel off. Note that the enum deliberately aliases several values
        /// (<c>TS1Downtown</c> is <c>NumericEntry</c>, <c>TS1Vacation</c> is <c>Custom</c>), so
        /// the comparison is on the numeric value the engine actually raises.
        /// </remarks>
        public static MakingMagicBook BookFor(VMDialogType type)
        {
            if (type == VMDialogType.TS1Spellbook) return MakingMagicBook.Spellbook;
            if (type == VMDialogType.TS1Cookbook) return MakingMagicBook.Cookbook;
            return MakingMagicBook.None;
        }

        /// <summary>The <c>magic.iff</c> chunk label holding a book's entry titles, or null.</summary>
        public static string TitlesLabel(MakingMagicBook book)
        {
            switch (book)
            {
                case MakingMagicBook.Spellbook: return SpellTitlesLabel;
                case MakingMagicBook.Cookbook: return RecipeTitlesLabel;
                default: return null;
            }
        }

        /// <summary>
        /// The <c>magic.iff</c> chunk label holding a book's entry descriptions, or null.
        /// </summary>
        /// <remarks>
        /// The spell description table has one more entry than the title table does (26 against
        /// 25). The extra one is the tail of the English block rather than a 26th spell, which
        /// is why nothing here relies on the two being the same length.
        /// </remarks>
        public static string DescriptionsLabel(MakingMagicBook book)
        {
            switch (book)
            {
                case MakingMagicBook.Spellbook: return SpellDescriptionLabel;
                case MakingMagicBook.Cookbook: return RecipeDescriptionLabel;
                default: return null;
            }
        }

        /// <summary>
        /// The index of a dialog caption inside the "Magic Book Dialog Strings" table, which is
        /// five entries per language in the order the enum declares.
        /// </summary>
        public static int DialogStringIndex(MakingMagicCaption caption)
        {
            var index = (int)caption;
            return (index < 0 || index > (int)MakingMagicCaption.PageOf) ? -1 : index;
        }

        /// <summary>
        /// The English text of a dialog caption, used when <c>magic.iff</c> could not be opened
        /// (no expansion installed) or has no entry for it.
        /// </summary>
        public static string DialogStringFallback(MakingMagicCaption caption)
        {
            switch (caption)
            {
                case MakingMagicCaption.SpellbookTitle: return "%s Family Spellbook";
                case MakingMagicCaption.CookbookTitle: return "%s Family Cookbook";
                case MakingMagicCaption.Back: return "back";
                case MakingMagicCaption.Next: return "next";
                case MakingMagicCaption.PageOf: return "Page %d of %d";
                default: return "";
            }
        }

        /// <summary>The dialog caption that names this book.</summary>
        public static MakingMagicCaption TitleCaption(MakingMagicBook book)
        {
            return book == MakingMagicBook.Cookbook
                ? MakingMagicCaption.CookbookTitle
                : MakingMagicCaption.SpellbookTitle;
        }

        /// <summary>
        /// Substitutes the family's name into a book title, in the same substitution
        /// <c>ContentStrings.GetString(table, id, args)</c> does.
        /// </summary>
        /// <param name="book">
        /// Which book's English title to fall back on when <paramref name="template"/> is null or
        /// empty. Taken as a parameter rather than assumed so that a cookbook whose caption could
        /// not be read still says "Cookbook".
        /// </param>
        public static string FormatBookTitle(MakingMagicBook book, string template, string familyName)
        {
            if (string.IsNullOrEmpty(template)) template = DialogStringFallback(TitleCaption(book));
            return SimitoneStrings.Substitute(template, new[] { familyName ?? "" });
        }

        /// <summary>Substitutes the page numbers into a "Page %d of %d" caption.</summary>
        public static string FormatPageCaption(string template, int page, int pages)
        {
            return SimitoneStrings.Substitute(
                string.IsNullOrEmpty(template) ? DialogStringFallback(MakingMagicCaption.PageOf) : template,
                new[] { page.ToString(CultureInfo.InvariantCulture), pages.ToString(CultureInfo.InvariantCulture) });
        }

        #endregion

        #region which entries are real

        /// <summary>
        /// Title prefixes that mean "this slot is not a spell/recipe". All of them are the
        /// expansion's own untranslated placeholders.
        /// </summary>
        public static readonly string[] PlaceholderPrefixes = { "not used", "tbd", "unknown" };

        /// <summary>
        /// Is this entry in the table a real one?
        /// </summary>
        /// <remarks>
        /// False for null, empty and whitespace, and for anything beginning with one of
        /// <see cref="PlaceholderPrefixes"/>. Matching the words rather than the indices is what
        /// makes this work in a translated build: <c>magic.iff</c> marks every one of them
        /// "Needs translation", so a French or German copy still reads "not used" where an
        /// English one does.
        /// </remarks>
        public static bool IsPlaceholderTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            var trimmed = title.Trim();
            for (int i = 0; i < PlaceholderPrefixes.Length; i++)
            {
                if (trimmed.StartsWith(PlaceholderPrefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// The indices of the entries in a titles table that are real spells or recipes, in
        /// table order.
        /// </summary>
        /// <remarks>
        /// The indices are the raw ones - the numbers the game itself uses - so a choice can be
        /// handed straight back to the engine without being re-mapped. A null or empty table
        /// yields an empty list rather than a null, so a caller can count it without a guard.
        /// </remarks>
        public static IReadOnlyList<int> UsableEntries(IReadOnlyList<string> titles)
        {
            var entries = new List<int>();
            if (titles == null) return entries;
            for (int i = 0; i < titles.Count; i++)
            {
                if (!IsPlaceholderTitle(titles[i])) entries.Add(i);
            }
            return entries;
        }

        /// <summary>
        /// The raw table index for a real entry that the table's own text names, or -1.
        /// </summary>
        /// <remarks>
        /// How a spell object in a sim's inventory is lined up with its flavour text: the
        /// object's catalogue name and the title in <c>magic.iff</c> are the same string, so the
        /// name is enough to find the description. Case-insensitive and whitespace-insensitive,
        /// because one side comes from a <c>CTSS</c> chunk and the other from a <c>STR</c> one.
        /// </remarks>
        public static int IndexOfTitle(IReadOnlyList<string> titles, string name)
        {
            if (titles == null || string.IsNullOrWhiteSpace(name)) return -1;
            var wanted = name.Trim();
            for (int i = 0; i < titles.Count; i++)
            {
                if (titles[i] == null) continue;
                if (IsPlaceholderTitle(titles[i])) continue;
                if (string.Equals(titles[i].Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        #endregion

        #region paging

        /// <summary>
        /// Entries a page holds. Five is what the game shipped: the dialog's own caption table
        /// has five strings, and a page of five is what fits the book art.
        /// </summary>
        public const int EntriesPerPage = 5;

        /// <summary>How many pages a run of entries needs, never less than one.</summary>
        public static int PageCount(int entryCount, int perPage = EntriesPerPage)
        {
            if (perPage < 1) perPage = EntriesPerPage;
            if (entryCount <= 0) return 1;
            return (entryCount + perPage - 1) / perPage;
        }

        /// <summary>
        /// Brings a page number into range for a book of <paramref name="pageCount"/> pages.
        /// Pages are 1-based, matching the "Page %d of %d" caption.
        /// </summary>
        public static int ClampPage(int page, int pageCount)
        {
            if (pageCount < 1) pageCount = 1;
            if (page < 1) return 1;
            return page > pageCount ? pageCount : page;
        }

        /// <summary>
        /// The entry indices on one page, 1-based, in table order.
        /// </summary>
        /// <remarks>
        /// The page is clamped rather than refused, so "next" on the last page and "back" on the
        /// first are the same no-op the buttons in the game are. An empty entry list gives an
        /// empty page: a family that has discovered nothing is a real state, and it is shown as
        /// an empty book rather than as a crash.
        /// </remarks>
        public static IReadOnlyList<int> PageOf(IReadOnlyList<int> entries, int page, int perPage = EntriesPerPage)
        {
            var result = new List<int>();
            if (entries == null || entries.Count == 0) return result;
            if (perPage < 1) perPage = EntriesPerPage;

            var pages = PageCount(entries.Count, perPage);
            var wanted = ClampPage(page, pages);
            var start = (wanted - 1) * perPage;
            for (int i = start; i < entries.Count && i < start + perPage; i++)
            {
                result.Add(entries[i]);
            }
            return result;
        }

        #endregion

        #region the inventory the spells are actually kept in

        /// <summary>
        /// Neighbourhood-inventory entry type Makin' Magic keeps its spells under.
        ///
        /// <para>
        /// The number is not an engine enum: it is the <c>TokenType</c> byte of the
        /// <c>manage_inventory</c> primitive (opcode 51) in the expansion's own scripts, where
        /// "DEBUG - Get All Spells" hands every spell to the sim's inventory with type 7 and
        /// "DEBUG - Get All Components" does the same for the recipe ingredients with type 8.
        /// The engine reads that byte straight out of the operand
        /// (<c>VMTS1InventoryOperationsOperand.TokenType</c>), so the two agree by
        /// construction.
        /// </para>
        /// </summary>
        public const int MagicTokenType = 7;

        /// <summary>
        /// The same field for the cookbook's ingredients and recipe components. See
        /// <see cref="MagicTokenType"/>.
        /// </summary>
        public const int IngredientTokenType = 8;

        /// <summary>The entry type a book's own entries are stored under.</summary>
        public static int PrimaryTokenType(MakingMagicBook book)
        {
            return book == MakingMagicBook.Cookbook ? IngredientTokenType : MagicTokenType;
        }

        /// <summary>
        /// The entry types to look in for a book: its own first, then the other one.
        /// </summary>
        /// <remarks>
        /// The hedge is deliberate. The two type numbers come out of the expansion's scripts
        /// rather than out of a named enum, and the two books are the only two things in the
        /// game that use them, so a build that has one of the objects but not the other would
        /// otherwise show an empty book. Reading the other type as a fallback costs one extra
        /// pass over a list of a few dozen entries and cannot hide a real entry, because the
        /// two sets are disjoint in the shipped data.
        /// </remarks>
        public static IReadOnlyList<int> TokenTypesFor(MakingMagicBook book)
        {
            return book == MakingMagicBook.Cookbook
                ? new[] { IngredientTokenType, MagicTokenType }
                : new[] { MagicTokenType, IngredientTokenType };
        }

        #endregion

        #region the answer

        /// <summary>Response code for a choice: the engine's "yes/ok".</summary>
        public const byte ResponseAccepted = 1;

        /// <summary>Response code for backing out: the engine's "no/cancel".</summary>
        public const byte ResponseCancelled = 0;

        /// <summary>
        /// The response code a choice is sent with. Every entry is accepted, backing out is
        /// cancelled.
        /// </summary>
        public static byte ResponseCodeFor(int entryIndex)
        {
            return entryIndex < 0 ? ResponseCancelled : ResponseAccepted;
        }

        /// <summary>
        /// The text a choice is sent with: the raw <c>magic.iff</c> table index, or -1 for
        /// backing out.
        /// </summary>
        /// <remarks>
        /// This is the same shape the neighbouring numeric dialogs use - the travel picker, the
        /// phone book and the pet/outfit chooser all answer with a number in
        /// <c>ResponseText</c> and a 0/1 in <c>ResponseCode</c>, and
        /// <c>VMDialogPrivateStrings</c> parses it straight into <c>TempRegisters[0]</c> for
        /// those types. The Makin' Magic types sit in the same case group, so they are parsed
        /// the same way; the object script does not read the register back, so the index ends
        /// the dialog rather than selecting an action.
        /// </remarks>
        public static string ResponseTextFor(int entryIndex)
        {
            return entryIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The inverse of <see cref="ResponseCodeFor"/> / <see cref="ResponseTextFor"/>: the
        /// entry a response names, or -1 when it is not one.
        /// </summary>
        public static bool TryParseResponse(byte responseCode, string responseText, out int entryIndex)
        {
            entryIndex = -1;
            if (responseCode != ResponseAccepted) return false;
            return int.TryParse(responseText, NumberStyles.Integer, CultureInfo.InvariantCulture, out entryIndex)
                && entryIndex >= 0;
        }

        #endregion
    }
}
