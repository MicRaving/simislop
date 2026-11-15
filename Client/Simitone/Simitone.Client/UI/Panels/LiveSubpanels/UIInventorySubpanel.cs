using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Simitone.Client.UI.Screens;
using FSO.Content;
using FSO.SimAntics.Model;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using Simitone.Client.UI.Controls;
using Microsoft.Xna.Framework;
using FSO.SimAntics;
using Microsoft.Xna.Framework.Graphics;
using FSO.LotView.Model;
using FSO.Client;
using FSO.Client.UI.Framework;
using Simitone.Client.UI.Model;
using FSO.Client.UI.Controls;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    public class UIInventorySubpanel : UISubpanel
    {
        public List<InventoryItem> Items = new List<InventoryItem>();
        public UITouchScroll ScrollView;
        public int CatSort = -1;

        public UITwoStateButton MagicButton;
        public UITwoStateButton IngButton;
        public UITwoStateButton OtherButton;
        public UITwoStateButton VacButton;

        /// <summary>
        /// The family MagiCoin balance, read out of the neighbourhood inventory. Null before the
        /// first read, which is also the state a lot with no neighbourhood open is in.
        /// </summary>
        public int? MagiCoins;

        private UILabel MagiCoinLabel;

        /// <summary>
        /// Taller than the shared 128px strip: the four sort buttons fill the left column, and
        /// the MagiCoin readout needs a row of its own underneath them.
        /// </summary>
        public override float PanelHeight => 128f + ExtraHeight;

        /// <summary>
        /// Draws the strip of panel behind the MagiCoin readout.
        /// </summary>
        /// <remarks>
        /// The live-mode strip's own background is painted by <c>UIMainPanel</c>, and it paints
        /// exactly 128px of it - so this panel's extra row is drawn over the lot view unless it
        /// brings a background of its own. One rectangle in the same colour as the strip's,
        /// which is why the row reads as part of the panel rather than as a caption floating
        /// over a wall. Drawn before the children so the label and the buttons sit on it.
        /// </remarks>
        public override void Draw(UISpriteBatch batch)
        {
            DrawLocalTexture(batch, TextureGenerator.GetPxWhite(GameFacade.GraphicsDevice), null,
                new Vector2(0, 128f), new Vector2(Size.X, ExtraHeight), UIStyle.Current.Bg);
            base.Draw(batch);
        }

        /// <summary>Y of the MagiCoin readout, below the two rows of sort buttons.</summary>
        private const int MagiCoinRowY = 124;

        /// <summary>
        /// How much taller than the shared 128px strip the inventory subpanel is.
        /// </summary>
        /// <remarks>
        /// Written out rather than read from <see cref="PanelHeight"/>, because the background
        /// plate below needs the same number and the virtual property is not safe to read while
        /// the base constructor is still running.
        /// </remarks>
        private const float ExtraHeight = InventorySections.MagiCoinRowHeight;

        public UIInventorySubpanel(TS1GameScreen game) : base(game)
        {
            var ui = Content.Get().CustomUI;
            ScrollView = new UITouchScroll(() => { return Items?.Count ?? 0; }, DisplayProvider);
            ScrollView.X = 148;
            ScrollView.ItemWidth = 90;
            ScrollView.Size = new Vector2(582, 128);
            Add(ScrollView);

            MagicButton = new UITwoStateButton(ui.Get("inv_mag_btn.png").Get(GameFacade.GraphicsDevice));
            MagicButton.Position = new Vector2(21, 9);
            MagicButton.OnButtonClick += (b) => { ChangeCat(InventorySections.MagicTokenType); };
            Add(MagicButton);
            IngButton = new UITwoStateButton(ui.Get("inv_ing_btn.png").Get(GameFacade.GraphicsDevice));
            IngButton.Position = new Vector2(81, 9);
            IngButton.OnButtonClick += (b) => { ChangeCat(InventorySections.IngredientTokenType); };
            Add(IngButton);
            OtherButton = new UITwoStateButton(ui.Get("inv_other_btn.png").Get(GameFacade.GraphicsDevice));
            OtherButton.Position = new Vector2(21, 69);
            OtherButton.OnButtonClick += (b) => { ChangeCat(InventorySections.SortAll); };
            Add(OtherButton);
            VacButton = new UITwoStateButton(ui.Get("inv_vac_btn.png").Get(GameFacade.GraphicsDevice));
            VacButton.Position = new Vector2(81, 69);
            VacButton.OnButtonClick += (b) => { ChangeCat(InventorySections.VacationTokenType); };
            Add(VacButton);

            MagiCoinLabel = new UILabel();
            MagiCoinLabel.CaptionStyle = MagiCoinLabel.CaptionStyle.Clone();
            MagiCoinLabel.CaptionStyle.Color = UIStyle.Current.BtnActive;
            MagiCoinLabel.CaptionStyle.Size = 15;
            MagiCoinLabel.Position = new Vector2(8, MagiCoinRowY);
            MagiCoinLabel.Size = new Vector2(132, 20);
            MagiCoinLabel.Alignment = TextAlignment.Middle | TextAlignment.Center;
            MagiCoinLabel.Wrapped = true;
            MagiCoinLabel.Caption = MagiCoinFormat.Caption(null);
            Add(MagiCoinLabel);

            ChangeCat(InventorySections.SortAll);
        }

        public void ChangeCat(int cat)
        {
            MagicButton.Selected = cat == InventorySections.MagicTokenType;
            IngButton.Selected = cat == InventorySections.IngredientTokenType;
            OtherButton.Selected = cat == InventorySections.SortAll;
            VacButton.Selected = cat == InventorySections.VacationTokenType;
            CatSort = cat;
        }

        public UITSContainer DisplayProvider(int index)
        {
            return new UIInventoryDisplay(Items[index]);
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            UpdateInventoryView();
            Invalidate();
        }

        public void UpdateInventoryView()
        {
            var sel = Game.SelectedAvatar;
            if (sel == null) return;
            var neighbourhood = Content.Get().Neighborhood;
            var neighbour = sel.GetPersonData(VMPersonDataVariable.NeighborId);
            var inventory = neighbourhood.GetInventoryByNID(neighbour)
                ?.Where(x => InventorySections.Matches(x.Type, CatSort)).ToList() ?? new List<InventoryItem>();

            bool difference = false;
            if (inventory.Count == Items.Count)
            {
                for (int i = 0; i < inventory.Count; i++)
                {
                    var i1 = inventory[i]; var i2 = Items[i];
                    if (i1.Count != i2.Count || i1.GUID != i2.GUID || i1.Type != i2.Type)
                    {
                        difference = true; break;
                    }
                }
            } else
            {
                difference = true;
            }

            if (difference)
            {
                Items.Clear();
                foreach (var item in inventory)
                {
                    Items.Add(item.Clone());
                }
                ScrollView.Reset();
            }

            UpdateMagiCoins(sel);
        }

        /// <summary>
        /// Reads the family MagiCoin balance and puts it on the strip.
        /// </summary>
        /// <remarks>
        /// The balance belongs to the <em>family</em>, not to the selected sim: MagiCoins are
        /// stored per sim in the neighbourhood inventory but spent and shown per household, which
        /// is why the move-in dialog reads them off the family that would be moving in
        /// (<c>UIHouseSelectPanel</c>) rather than off whoever is being moved. The family is
        /// resolved from the selected sim's own family slot first, so switching to another
        /// household's sim shows that household's coins, and the live lot's current family is
        /// only a fallback for a sim whose slot names no family.
        /// </remarks>
        private void UpdateMagiCoins(VMAvatar sel)
        {
            var neighbourhood = Content.Get().Neighborhood;
            if (neighbourhood == null) return;

            var familyNumber = sel.GetPersonData(VMPersonDataVariable.TS1FamilyNumber);
            var family = familyNumber > 0 ? neighbourhood.GetFamily((ushort)familyNumber) : null;
            if (family == null) family = Game.vm?.TS1State?.CurrentFamily;

            var balance = family == null ? 0 : neighbourhood.GetMagicoinsForFamily(family);
            if (MagiCoins == balance) return;
            MagiCoins = balance;
            MagiCoinLabel.Caption = MagiCoinFormat.Caption(balance);
        }
    }

    /// <summary>
    /// Which slice of the neighbourhood token inventory the panel is showing, and which slices
    /// the "Other" tab may show at all.
    ///
    /// <para>
    /// The token inventory is a flat per-sim list whose entries carry a one-byte
    /// <c>Type</c> - the <c>TokenType</c> byte of the engine's <c>manage_inventory</c> primitive
    /// (opcode 51), which the expansion scripts set by hand. Sections 7 and 8 are Makin' Magic's
    /// spells and ingredients, and have had buttons on this panel for a long time. Sections 2 and
    /// 6 did not: the panel hid them outright, so a sim's Vacation souvenirs, prize tokens and
    /// hotel key - the whole Vacation reward chain, which the engine and the shipped scripts
    /// both implement - could not be seen at all.
    /// </para>
    ///
    /// <para>
    /// Both are shown now. Section 6 gets a button of its own, because it is a coherent
    /// collection a player wants to look at on purpose. Section 2 is not named here: nothing in
    /// the tree or in the shipped data establishes what it holds, and inventing a name for it
    /// would be a guess printed on the screen. It is shown under "Other" instead, which is the
    /// honest half of the fix - visible, and not mislabelled.
    /// </para>
    ///
    /// <para>
    /// The rule is one predicate, <see cref="Matches"/>, so "which entries are on screen" is a
    /// question with a test rather than a question about a lambda in a render method.
    /// </para>
    /// </summary>
    public static class InventorySections
    {
        /// <summary>
        /// The <c>CatSort</c> value that means "everything this panel is allowed to show".
        /// </summary>
        public const int SortAll = -1;

        /// <summary>Makin' Magic's spell tokens.</summary>
        public const int MagicTokenType = 7;

        /// <summary>Makin' Magic's recipe ingredient tokens.</summary>
        public const int IngredientTokenType = 8;

        /// <summary>
        /// Vacation's tokens: the six vacation tokens, the hotel key and the prize tokens, plus
        /// the Vacation score itself.
        /// </summary>
        public const int VacationTokenType = 6;

        /// <summary>
        /// A second, unidentified token section. Shown, but with no button of its own and no
        /// name, because nothing in this tree says what it holds.
        /// </summary>
        public const int UnidentifiedTokenType = 2;

        /// <summary>
        /// Sections the "Other" tab keeps to itself, because they have a button.
        /// </summary>
        /// <remarks>
        /// Exposed as a fresh set per call rather than as a shared mutable one: this used to be
        /// a public field on the panel, which meant anything holding a reference to it could
        /// change what every panel instance considered visible.
        /// </remarks>
        public static HashSet<int> HiddenByDefault => new HashSet<int>()
        {
            MagicTokenType,
            IngredientTokenType
        };

        /// <summary>How much taller than the shared strip the inventory subpanel is.</summary>
        public const int MagiCoinRowHeight = 26;

        /// <summary>
        /// Does a token of this type belong on screen while the given sort is selected?
        /// </summary>
        public static bool Matches(int tokenType, int sort)
        {
            if (sort == SortAll) return !HiddenByDefault.Contains(tokenType);
            return sort == tokenType;
        }

        /// <summary>
        /// The sort values this panel's four buttons select, in the order they are laid out.
        /// </summary>
        /// <remarks>
        /// A test pins the set against <see cref="Matches"/> so a button that selects a value
        /// nothing can satisfy - which renders as a permanently empty list with no explanation -
        /// fails here instead.
        /// </remarks>
        public static readonly int[] ButtonSorts = new int[]
        {
            MagicTokenType,
            IngredientTokenType,
            SortAll,
            VacationTokenType
        };
    }

    /// <summary>
    /// The MagiCoin balance, as the inventory strip shows it.
    ///
    /// <para>
    /// Makin' Magic's currency is implemented end to end - the tokens are in the neighbourhood
    /// inventory, the magic-town lots refuse a family that has too few, and the purchasables
    /// spend them - but nothing showed the balance, so a player could be turned away at a magic
    /// lot by a number they had never been able to see. It is a family balance, so it belongs on
    /// the one panel that is already about what a household has rather than what a sim can do.
    /// </para>
    ///
    /// <para>
    /// The number is formatted for reading rather than for the engine: the game writes a
    /// MagiCoin count with its own thousands separator, and this is a label.
    /// </para>
    /// </summary>
    public static class MagiCoinFormat
    {
        /// <summary>The caption's id in Simitone's own string table.</summary>
        public const string CaptionId = "540";

        /// <summary>English for <see cref="CaptionId"/>.</summary>
        public const string CaptionFallback = "MagiCoins: %s";

        /// <summary>
        /// The balance line, e.g. "MagiCoins: 1,250".
        /// </summary>
        /// <param name="balance">The family's balance; null reads as zero.</param>
        /// <remarks>
        /// Grouped with the invariant separator rather than the player's own, because the game
        /// writes this number with a comma in every language it ships, and a balance that reads
        /// "1.250" in one language and "1,250" in another is the same number spelled two ways
        /// on two screens in the same session.
        /// </remarks>
        public static string Caption(int? balance)
        {
            var count = balance ?? 0;
            return SimitoneStrings.Format(CaptionId, CaptionFallback,
                count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public class UIInventoryDisplay : UITSContainer {
        private UILabel NameLabel;
        private UILabel CountLabel;
        private Texture2D ItemBg;
        private Texture2D Item;
        public UIInventoryDisplay(InventoryItem item) : base()
        {
            NameLabel = new UILabel();
            NameLabel.CaptionStyle = NameLabel.CaptionStyle.Clone();
            NameLabel.CaptionStyle.Color = UIStyle.Current.Text;
            NameLabel.CaptionStyle.Size = 10;
            NameLabel.Wrapped = true;
            NameLabel.Size = new Vector2(80, 33);
            NameLabel.Position = new Vector2(5, 5);
            NameLabel.Alignment = TextAlignment.Middle | TextAlignment.Center;
            Add(NameLabel);

            CountLabel = new UILabel();
            CountLabel.CaptionStyle = NameLabel.CaptionStyle.Clone();
            CountLabel.CaptionStyle.Size = 15;
            CountLabel.Size = new Vector2(80, 30);
            CountLabel.Position = new Vector2(5, 90);
            CountLabel.Alignment = TextAlignment.Middle | TextAlignment.Center;
            Add(CountLabel);

            ItemBg = Content.Get().CustomUI.Get("inv_item.png").Get(GameFacade.GraphicsDevice);

            var obj = Content.Get().WorldObjects.Get(item.GUID);
            if (obj != null)
            {
                Item = obj.Resource.Get<BMP>(obj.OBJ.CatalogStringsID)?.GetTexture(GameFacade.GraphicsDevice);
                NameLabel.Caption = obj.Resource.Get<CTSS>(obj.OBJ.CatalogStringsID)?.GetString(0) ?? obj.OBJ.ChunkLabel;
                CountLabel.Caption = item.Count.ToString();
            }
        }

        public override void Draw(UISpriteBatch batch)
        {
            DrawLocalTexture(batch, ItemBg, null, new Vector2(20, 40), Vector2.One, new Color(104, 164, 184, 255));

            if (Item != null)
            {
                DrawLocalTexture(batch, Item, new Rectangle(0, 0, Item.Width / 2, Item.Height), new Vector2(45 + 42 / -2, 65 + 42 / -2), new Vector2(42f / Item.Height, 42f / Item.Height));
            }

            base.Draw(batch);
        }
    }
}
