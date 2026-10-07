using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels.CAS
{
    /// <summary>
    /// Lists all of the families that can be moved in or deleted.
    /// </summary>
    public class UIFamiliesCASPanel : UIContainer
    {
        public UITouchScroll FamilyList;
        public UILabel Title;
        public UITwoStateButton DeleteButton;
        public UITwoStateButton NewButton;

        public event Action OnNewFamily;
        /// <summary>
        /// Raised by the delete button with the currently selected index into <see cref="Families"/>.
        /// The screen is responsible for actually deleting the family and refreshing this panel.
        /// </summary>
        public event Action<int> OnDeleteFamily;

        public List<FAMI> Families = new List<FAMI>();
        private VM vm;
        private Texture2D WhitePx;

        public int Selection;

        /// <summary>
        /// The family the delete button would remove for the current <see cref="Selection"/>, or
        /// null when nothing is selected or the selected family is not one the CAS may delete
        /// (see <see cref="CasFamilyFilter.CanDelete(FAMI)"/>). The shipped unplaced families are
        /// in this list so they can be moved in, but they are not CAS-created and deleting one is
        /// not undoable, so the button is disabled while one of them is selected.
        /// </summary>
        public FAMI SelectedFamily
        {
            get
            {
                if (Selection < 0 || Selection >= (Families?.Count ?? 0)) return null;
                return Families[Selection];
            }
        }

        public float TitleI { get; set; }
        
        public UIFamiliesCASPanel()
        {
            var gd = GameFacade.GraphicsDevice;
            var ui = Content.Get().CustomUI;
            var sh = UIScreen.Current.ScreenHeight;
            var sw = UIScreen.Current.ScreenWidth;

            //** localization: the "select or create family" heading, with the English line as the
            //** fallback for a string table that has no entry for it. The English is Simitone's
            //** own table (id 100) rather than a literal, so a language whose table 151 has no
            //** entry for this string still gets it from somewhere.
            var titleText = GameFacade.Strings.GetString("151", "0");
            if (string.IsNullOrEmpty(titleText) || titleText == "***MISSING***")
                titleText = SimitoneStrings.Get("100", "Select a Family");

            FamilyList = new UITouchScroll(FamilyLength, FamilyProvider);
            FamilyList.VerticalMode = true;
            FamilyList.Size = new Vector2(810, sh);
            FamilyList.X = (sw - 810) / 2;
            FamilyList.ItemWidth = 180;
            FamilyList.Margin = 90;
            FamilyList.DrawBounds = false;
            Add(FamilyList);
            WhitePx = TextureGenerator.GetPxWhite(GameFacade.GraphicsDevice);

            Title = new UILabel();
            Title.NewStyle(UIStyle.Current.Text, 37);
            Title.Caption = titleText;
            Title.Size = new Vector2(sw, 60);
            Title.Alignment = TextAlignment.Middle | TextAlignment.Center;
            Title.Y = -85;
            Add(Title);

            DeleteButton = new UITwoStateButton(ui.Get("btn_deletefam.png").Get(gd));
            DeleteButton.Position = new Vector2(sw - 140, sh - 260);
            Add(DeleteButton);
            DeleteButton.OnButtonClick += (btn) => OnDeleteFamilyClick();

            NewButton = new UITwoStateButton(ui.Get("btn_createfam.png").Get(gd));
            NewButton.Position = new Vector2(sw - 140, sh - 380);
            Add(NewButton);
            NewButton.OnButtonClick += (btn) => OnNewFamily?.Invoke();

            TitleI = TitleI;
            SetSelection(-1);
        }

        public void SetSelection(int i)
        {
            Selection = i;
            RefreshDeleteButton();
        }

        /// <summary>
        /// The delete button is live only when a family is selected <em>and</em> that family is one
        /// the CAS created. Re-evaluated on every selection change so a shipped family - selectable
        /// for the move-in, but not ours to remove - cannot be deleted.
        /// </summary>
        private void RefreshDeleteButton()
        {
            if (DeleteButton == null) return;
            DeleteButton.Disabled = !CasFamilyFilter.CanDelete(SelectedFamily);
            if (NewButton != null) NewButton.Disabled = false;
        }

        private void OnDeleteFamilyClick()
        {
            //guard against clicks that race with a list refresh/selection reset.
            var family = SelectedFamily;
            if (family == null) return;
            if (!CasFamilyFilter.CanDelete(family)) return;
            OnDeleteFamily?.Invoke(Selection);
        }

        public override void GameResized()
        {
            var sh = UIScreen.Current.ScreenHeight;
            var sw = UIScreen.Current.ScreenWidth;
            FamilyList.Size = new Vector2(810, sh);
            FamilyList.X = (sw - 810) / 2;
            FamilyList.Reset();
            Title.Size = new Vector2(sw, 60);
            DeleteButton.Position = new Vector2(sw - 140, sh - 260);
            NewButton.Position = new Vector2(sw - 140, sh - 380);
            base.GameResized();
        }

        public int FamilyLength()
        {
            return Families.Count;
        }

        public UITSContainer FamilyProvider(int id)
        {
            var item = new UIFamilyCASItem(this, Families[id], vm);
            return item;
        }

        /// <summary>
        /// Replaces the list of families this panel offers.
        ///
        /// <para>
        /// The selection is carried across by family id, not by index. The list is rebuilt whenever
        /// a family is created, edited or deleted, and the new list can be a different length and
        /// in a different order; keeping the bare index would leave <see cref="Selection"/> pointing
        /// at whatever family slid into that slot, so the accept button (which reads the index) would
        /// move in - or the delete button would remove - a family the player never picked. Matching on
        /// the id gives back the right slot, and -1 when the family is genuinely gone, which leaves
        /// both buttons disabled rather than pointing at a neighbour.
        /// </para>
        /// </summary>
        /// <param name="families">The new list. Null is treated as empty.</param>
        /// <param name="vm">The CAS VM the family entries spawn their preview sims with.</param>
        public void UpdateFamilies(List<FAMI> families, VM vm)
        {
            var selectedId = CasFamilyFilter.SelectedChunkId(Families, Selection);

            this.vm = vm;
            Families = families ?? new List<FAMI>();
            var restored = CasFamilyFilter.IndexOfChunkId(Families, selectedId);

            FamilyList.Reset();
            Selection = restored;
            RefreshDeleteButton();

            //The scroll's cached entries were just thrown away, so re-highlight the restored slot
            //from Update rather than here: building a UIFamilyCASItem spawns a preview sim through
            //the VM, and this is called from a button handler, not from the VM's own tick.
            if (restored >= 0) PendingHighlight = restored;
        }

        /// <summary>Index to re-select on the next Update, or -1. See <see cref="UpdateFamilies"/>.</summary>
        private int PendingHighlight = -1;

        public override void Update(UpdateState state)
        {
            base.Update(state);
            if (PendingHighlight < 0) return;
            var index = PendingHighlight;
            PendingHighlight = -1;
            //Only worth drawing a highlight the player can see, and only if the selection is still
            //this family (the screen clears the selection on every mode change).
            if (index == Selection) FamilyList.SelectItem(index);
        }

        public override void Draw(UISpriteBatch batch)
        {
            var y = TitleI * 100 - 85;
            Title.Y = y;
            NewButton.X = UIScreen.Current.ScreenWidth - 140 * TitleI;
            DeleteButton.X = NewButton.X;

            FamilyList.Opacity = TitleI;
            FamilyList.Visible = FamilyList.Opacity > 0;
            Title.Visible = false;
            base.Draw(batch);
            DrawLocalTexture(batch, WhitePx, null, new Vector2(0, y), new Vector2(UIScreen.Current.ScreenWidth, 60), UIStyle.Current.Bg);
            Title.Visible = true;
            Title.Draw(batch);
        }
    }
}
