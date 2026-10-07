using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Utils;
using FSO.Content;
using FSO.LotView;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;

namespace Simitone.Client.UI.Panels
{
    /// <summary>
    /// In-game settings panel, reachable from the Options submenu of the live mode panel.
    ///
    /// FreeSO's own options UI (<c>FSO.Client.UI.Panels.UIGraphicsOptionsDialog</c>) cannot be
    /// reused here: its layout comes from <c>graphicspanel.uis</c>, which Simitone does not
    /// ship (it only exists in FreeSO's TSO content tree, and TS1 has no
    /// <c>GameData/uiscripts</c> for the <c>UIContainer.RenderScript</c> fallback to find, so the
    /// script parse throws a FileNotFoundException), and its apply path dereferences
    /// <c>((IGameScreen)GameFacade.Screens.CurrentUIScreen).vm.Context.World</c>, a TSO-only world
    /// host that a TS1 <c>VM</c> does not have. This panel is therefore Simitone-styled and
    /// writes the same <see cref="GlobalSettings.Default"/> keys, so anything saved here is what
    /// the rest of the game reads on the next launch.
    ///
    /// Values are staged locally and only committed to <see cref="GlobalSettings.Default"/>
    /// when Apply is pressed, which mirrors how the FreeSO dialog behaves.
    /// </summary>
    public class UISettingsPanel : UISubpanel
    {
        /// <summary>Number of toggle columns laid out to the left of the action buttons.</summary>
        private const int ItemCount = 7;

        private const int ToggleSize = 50;
        private const int ButtonMargin = 6;
        private const int LabelFontSize = 11;

        // Staged values - not written to GlobalSettings until Apply is pressed.
        private int pendingAntiAlias;
        private int pendingLightingMode;
        private bool pendingComplexShaders;
        private bool pendingEnableTransitions;
        private bool pendingEdgeScroll;
        private int pendingShadowDetail;
        private int pendingWaterReflections;

        private readonly UITwoStateButton[] toggles = new UITwoStateButton[ItemCount];
        private readonly UILabel[] nameLabels = new UILabel[ItemCount];
        private readonly UILabel[] valueLabels = new UILabel[ItemCount];
        private UILabel statusLabel;

        private UIButton applyButton;
        private UIButton revertButton;

        /// <summary>
        /// Row captions, in column order. "Shadows" and "Water" are the two lighting quality
        /// rows the Options &gt; Graphics page owns; they are here as well because this strip is
        /// what a player has open while looking at a lot, which is when a shadow quality
        /// change is worth making.
        ///
        /// <para>
        /// Every one is a literal in the shipped English table, ids 23 to 29, and is read through
        /// <see cref="SimitoneStrings"/> rather than off <c>GlobalSettings</c>: a property name is
        /// an identifier and not something a language spells differently. The English stays as
        /// the second argument, so a build with no table shows the same words it always did.
        /// </para>
        /// </summary>
        private static readonly string[] ItemNames =
        {
            SimitoneStrings.Get("23", "Anti-Alias"),
            SimitoneStrings.Get("24", "Lighting"),
            SimitoneStrings.Get("25", "Shadows"),
            SimitoneStrings.Get("26", "Water"),
            SimitoneStrings.Get("27", "Shaders"),
            SimitoneStrings.Get("28", "Transitions"),
            SimitoneStrings.Get("29", "Edge Scroll")
        };

        /// <summary>
        /// The word under a toggle that is on, and the word under one that is off - ids 33 and
        /// 34.
        /// </summary>
        /// <remarks>
        /// Read through the table rather than TS1's own "On"/"Off" because nothing in the game
        /// ever showed the two words under a switch on this strip, so there is no id for the
        /// pairing in a shipped table to point at; the English is the second argument either
        /// way.
        /// </remarks>
        private static readonly string OnText = SimitoneStrings.Get("33", "On");
        private static readonly string OffText = SimitoneStrings.Get("34", "Off");

        private static Texture2D settingsIcon;
        private static Texture2D applyButtonTexture;
        private static Texture2D revertButtonTexture;

        public UISettingsPanel(TS1GameScreen game) : base(game)
        {
            var settings = GlobalSettings.Default;

            // Seed the staged values from what the game is actually running with. The values are
            // range-checked on the way in because FreeSO's ini defaults for LightingMode (-1)
            // and a hand-edited AntiAlias are not values the renderer can use.
            pendingAntiAlias = StartupSettings.ResolveAntiAlias(settings.AntiAlias);
            pendingLightingMode = StartupSettings.ResolveLightingMode(settings.LightingMode);
            pendingComplexShaders = settings.ComplexShaders;
            pendingEnableTransitions = settings.EnableTransitions;
            pendingEdgeScroll = settings.EdgeScroll;
            pendingShadowDetail = GraphicsOptions.ResolveShadowDetail(settings.ShadowDetail);
            pendingWaterReflections = GraphicsOptions.ResolveWaterReflections(settings.WaterReflections);

            var toggleTexture = Content.Get().CustomUI.Get("blank_blue.png").Get(GameFacade.GraphicsDevice);

            toggles[0] = MakeToggle(toggleTexture);
            toggles[0].OnButtonClick += (b) => CycleAntiAlias();

            toggles[1] = MakeToggle(toggleTexture);
            toggles[1].OnButtonClick += (b) => CycleLightingMode();

            toggles[2] = MakeToggle(toggleTexture);
            toggles[2].OnButtonClick += (b) => CycleShadowDetail();

            toggles[3] = MakeToggle(toggleTexture);
            toggles[3].OnButtonClick += (b) => CycleWaterReflections();

            toggles[4] = MakeToggle(toggleTexture);
            toggles[4].OnButtonClick += (b) => Flip(ref pendingComplexShaders);

            toggles[5] = MakeToggle(toggleTexture);
            toggles[5].OnButtonClick += (b) => Flip(ref pendingEnableTransitions);

            toggles[6] = MakeToggle(toggleTexture);
            toggles[6].OnButtonClick += (b) => Flip(ref pendingEdgeScroll);

            for (int i = 0; i < ItemCount; i++)
            {
                var name = new UILabel();
                name.NewStyle(UIStyle.Current.Text, LabelFontSize);
                name.Alignment = TextAlignment.Middle | TextAlignment.Center;
                name.Caption = ItemNames[i];
                nameLabels[i] = name;
                Add(name);

                var value = new UILabel();
                value.NewStyle(UIStyle.Current.BtnActive, LabelFontSize);
                value.Alignment = TextAlignment.Middle | TextAlignment.Center;
                valueLabels[i] = value;
                Add(value);
            }

            statusLabel = new UILabel();
            statusLabel.NewStyle(UIStyle.Current.SecondaryText, LabelFontSize);
            statusLabel.Alignment = TextAlignment.Middle | TextAlignment.Left;
            statusLabel.Caption = SimitoneStrings.Get("20", "Settings");
            Add(statusLabel);

            // Caption is assigned before AutoMargins on purpose: both setters recompute the
            // auto width through CaptionStyle.MeasureString, and the caption is what that
            // measures. This is the same order UIPieMenu uses for its text buttons.
            applyButton = new UIButton(GetApplyButtonTexture(GameFacade.GraphicsDevice));
            applyButton.ImageStates = 1;
            applyButton.CaptionStyle = applyButton.CaptionStyle.Clone();
            applyButton.CaptionStyle.Size = 13;
            applyButton.CaptionStyle.Color = UIStyle.Current.GreenBtnTxt;
            applyButton.CaptionStyle.SelectedColor = UIStyle.Current.GreenBtnTxt;
            applyButton.Caption = SimitoneStrings.Get("21", "Apply & Save");
            applyButton.AutoMargins = ButtonMargin;
            applyButton.OnButtonClick += (b) => Apply();
            Add(applyButton);

            revertButton = new UIButton(GetRevertButtonTexture(GameFacade.GraphicsDevice));
            revertButton.ImageStates = 1;
            revertButton.CaptionStyle = revertButton.CaptionStyle.Clone();
            revertButton.CaptionStyle.Size = 13;
            revertButton.CaptionStyle.Color = UIStyle.Current.BtnTxt;
            revertButton.CaptionStyle.SelectedColor = UIStyle.Current.BtnTxt;
            revertButton.Caption = SimitoneStrings.Get("22", "Revert");
            revertButton.AutoMargins = ButtonMargin;
            revertButton.OnButtonClick += (b) => Revert();
            Add(revertButton);

            Relayout();
            Refresh();
        }

        private UITwoStateButton MakeToggle(Texture2D texture)
        {
            var button = new UITwoStateButton(texture);
            Add(button);
            return button;
        }

        /// <summary>
        /// Positions the toggle columns and the two action buttons for the current subpanel
        /// width. Called on construction and on every resize, because the subpanel width is
        /// derived from the screen width.
        /// </summary>
        private void Relayout()
        {
            int actionWidth = (int)(applyButton.Width + revertButton.Width + (2 * ButtonMargin));
            float columnSpace = Math.Max(ItemCount * (ToggleSize + 8), Size.X - actionWidth - 12);
            float spacing = columnSpace / ItemCount;

            for (int i = 0; i < ItemCount; i++)
            {
                float columnLeft = 6 + (i * spacing);
                float inset = (spacing - ToggleSize) / 2;
                toggles[i].Position = new Vector2(columnLeft + inset, 14);

                var name = nameLabels[i];
                name.Position = new Vector2(columnLeft, 68);
                name.Size = new Vector2(spacing - 4, name.CaptionStyle.LineHeight);

                var value = valueLabels[i];
                value.Position = new Vector2(columnLeft, 86);
                value.Size = new Vector2(spacing - 4, value.CaptionStyle.LineHeight);
            }

            float revertX = Math.Max(columnSpace + 12, Size.X - actionWidth + ButtonMargin);
            revertButton.Position = new Vector2(revertX, 30);
            applyButton.Position = new Vector2(revertX + revertButton.Width + (2 * ButtonMargin), 30);

            SetStatusBounds();
        }

        private void SetStatusBounds()
        {
            statusLabel.Position = new Vector2(6, 0);
            statusLabel.Size = new Vector2(Math.Max(1, Size.X - 12), statusLabel.CaptionStyle.LineHeight);
        }

        public override void GameResized()
        {
            base.GameResized();
            Relayout();
        }

        private void Flip(ref bool value)
        {
            value = !value;
            Refresh();
            SetStatus(SimitoneStrings.Get("30", "Settings - unsaved changes"), UIStyle.Current.ActiveSelection);
        }

        private void CycleAntiAlias()
        {
            pendingAntiAlias = (pendingAntiAlias + 1) % (StartupSettings.MaxAntiAlias + 1);
            Refresh();
            SetStatus(SimitoneStrings.Get("30", "Settings - unsaved changes"), UIStyle.Current.ActiveSelection);
        }

        private void CycleLightingMode()
        {
            pendingLightingMode = (pendingLightingMode + 1) % (StartupSettings.MaxLightingMode + 1);
            Refresh();
            SetStatus(SimitoneStrings.Get("30", "Settings - unsaved changes"), UIStyle.Current.ActiveSelection);
        }

        /// <summary>
        /// Steps the shadow resolution through the low/medium/high ladder. The staged value is
        /// the resolution itself (pixels per lot tile), not the radio index, so that what the
        /// strip shows and what the ini stores are the same number.
        /// </summary>
        private void CycleShadowDetail()
        {
            var next = (GraphicsOptions.ShadowDetailToQuality(pendingShadowDetail) + 1) % 3;
            pendingShadowDetail = GraphicsOptions.ShadowDetailFromQuality(next);
            Refresh();
            SetStatus(SimitoneStrings.Get("30", "Settings - unsaved changes"), UIStyle.Current.ActiveSelection);
        }

        /// <summary>Steps the water surface sheen through off/on/strong.</summary>
        private void CycleWaterReflections()
        {
            pendingWaterReflections = (pendingWaterReflections + 1) % (GraphicsOptions.MaxWaterReflections + 1);
            Refresh();
            SetStatus(SimitoneStrings.Get("30", "Settings - unsaved changes"), UIStyle.Current.ActiveSelection);
        }

        /// <summary>Pushes the staged values into the toggle and value visuals.</summary>
        private void Refresh()
        {
            toggles[0].Selected = pendingAntiAlias > 0;
            toggles[1].Selected = pendingLightingMode > 0;
            toggles[2].Selected = pendingShadowDetail > GraphicsOptions.ShadowDetailLow;
            toggles[3].Selected = pendingWaterReflections > GraphicsOptions.WaterReflectionsOff;
            toggles[4].Selected = pendingComplexShaders;
            toggles[5].Selected = pendingEnableTransitions;
            toggles[6].Selected = pendingEdgeScroll;

            valueLabels[0].Caption = DescribeAntiAlias(pendingAntiAlias);
            valueLabels[1].Caption = DescribeLightingMode(pendingLightingMode);
            valueLabels[2].Caption = GraphicsOptions.DescribeShadowDetail(pendingShadowDetail);
            valueLabels[3].Caption = GraphicsOptions.DescribeWaterReflections(pendingWaterReflections);
            valueLabels[4].Caption = pendingComplexShaders ? OnText : OffText;
            valueLabels[5].Caption = pendingEnableTransitions ? OnText : OffText;
            valueLabels[6].Caption = pendingEdgeScroll ? OnText : OffText;
        }

        private void SetStatus(string caption, Color color)
        {
            statusLabel.Caption = caption;
            statusLabel.CaptionStyle.Color = color;
        }

        /// <summary>Copies the staged values back out of <see cref="GlobalSettings.Default"/>.</summary>
        private void Revert()
        {
            var settings = GlobalSettings.Default;
            pendingAntiAlias = StartupSettings.ResolveAntiAlias(settings.AntiAlias);
            pendingLightingMode = StartupSettings.ResolveLightingMode(settings.LightingMode);
            pendingComplexShaders = settings.ComplexShaders;
            pendingEnableTransitions = settings.EnableTransitions;
            pendingEdgeScroll = settings.EdgeScroll;
            pendingShadowDetail = GraphicsOptions.ResolveShadowDetail(settings.ShadowDetail);
            pendingWaterReflections = GraphicsOptions.ResolveWaterReflections(settings.WaterReflections);

            Refresh();
            SetStatus(SimitoneStrings.Get("32", "Settings - reverted"), UIStyle.Current.SecondaryText);
        }

        /// <summary>
        /// Commits the staged values, persists them to config.ini and pushes the graphics ones
        /// into the live renderer configuration.
        /// </summary>
        private void Apply()
        {
            var settings = GlobalSettings.Default;
            settings.AntiAlias = StartupSettings.ResolveAntiAlias(pendingAntiAlias);
            settings.LightingMode = StartupSettings.ResolveLightingMode(pendingLightingMode);
            settings.ShadowDetail = GraphicsOptions.ResolveShadowDetail(pendingShadowDetail);
            settings.WaterReflections = GraphicsOptions.ResolveWaterReflections(pendingWaterReflections);
            settings.ComplexShaders = pendingComplexShaders;
            settings.EnableTransitions = pendingEnableTransitions;
            settings.EdgeScroll = pendingEdgeScroll;
            settings.Save();

            ApplyWorldConfig();

            // Re-normalise the staged values from what was actually persisted. Note that
            // Transitions stays "on" here even in 2D: WorldConfig.EnableTransitions forces
            // itself off unless FSOEnvironment.Enable3D, but the stored preference is kept so
            // it takes effect for a -3d launch.
            pendingAntiAlias = StartupSettings.ResolveAntiAlias(settings.AntiAlias);
            pendingLightingMode = StartupSettings.ResolveLightingMode(settings.LightingMode);
            pendingShadowDetail = GraphicsOptions.ResolveShadowDetail(settings.ShadowDetail);
            pendingWaterReflections = GraphicsOptions.ResolveWaterReflections(settings.WaterReflections);

            Refresh();
            SetStatus(SimitoneStrings.Get("31", "Settings - saved"), UIStyle.Current.BtnActive);
        }

        /// <summary>
        /// Mirrors the settings into <see cref="WorldConfig.Current"/> and asks the lot's world to
        /// rebuild whatever the new configuration affects. This is the Simitone equivalent of
        /// FreeSO's <c>vm.Context.World.ChangedWorldConfig(...)</c> - a TS1 lot is reached through
        /// <c>Game.LotControl.World</c> instead of the TSO VM's world context.
        ///
        /// <para>
        /// The mapping is <see cref="GraphicsOptions.Apply"/>'s, the same one the Options page
        /// and the startup path use, so applying from this strip cannot leave the renderer
        /// configured differently from what the ini says.
        /// </para>
        /// </summary>
        private void ApplyWorldConfig()
        {
            GraphicsOptions.Apply(WorldConfig.Current, GraphicsPreferences.From(GlobalSettings.Default));

            Game?.LotControl?.World?.ChangedWorldConfig(GameFacade.GraphicsDevice);
        }

        #region Pure value formatting

        // These live in GraphicsOptions with the mappings they name, so the strip and the
        // Options page's radio captions cannot disagree about what a step is called. Kept here
        // because they were public on this panel first.

        /// <summary>Human readable name for an <c>AntiAlias</c> level (off / 4x MSAA / 2x SSAA).</summary>
        public static string DescribeAntiAlias(int antiAlias) => GraphicsOptions.DescribeAntiAlias(antiAlias);

        /// <summary>Human readable name for a <c>LightingMode</c> level.</summary>
        public static string DescribeLightingMode(int lightingMode) => GraphicsOptions.DescribeLightingMode(lightingMode);

        #endregion

        #region Generated textures

        /// <summary>
        /// White-on-transparent "sliders" glyph used as the Settings tab icon. Simitone ships no
        /// settings artwork and the options submenu is built from 65x65 stencil glyphs, so the
        /// icon is rasterised once at runtime instead of adding an asset.
        /// </summary>
        public static Texture2D GetSettingsIcon(GraphicsDevice gd)
        {
            if (settingsIcon == null)
            {
                const int size = 65;
                var data = new Color[size * size];
                for (int i = 0; i < data.Length; i++) data[i] = Color.Transparent;

                // three horizontal tracks...
                for (int track = 0; track < 3; track++)
                {
                    FillRect(data, size, new Rectangle(11, 15 + (track * 18), 43, 5));
                }
                // ...each with a knob at a different position
                FillRect(data, size, new Rectangle(13, 10, 9, 15));
                FillRect(data, size, new Rectangle(42, 28, 9, 15));
                FillRect(data, size, new Rectangle(25, 46, 9, 15));

                settingsIcon = new Texture2D(gd, size, size);
                settingsIcon.SetData(data);
            }
            return settingsIcon;
        }

        /// <summary>Green plate behind the Apply button.</summary>
        public static Texture2D GetApplyButtonTexture(GraphicsDevice gd)
        {
            if (applyButtonTexture == null)
                applyButtonTexture = TextureGenerator.GenerateRoundedRectangle(gd, new Color(0, 110, 55), 24, 40, 6);
            return applyButtonTexture;
        }

        /// <summary>Blue plate behind the Revert button.</summary>
        public static Texture2D GetRevertButtonTexture(GraphicsDevice gd)
        {
            if (revertButtonTexture == null)
                revertButtonTexture = TextureGenerator.GenerateRoundedRectangle(gd, new Color(0, 40, 140), 24, 40, 6);
            return revertButtonTexture;
        }

        private static void FillRect(Color[] data, int texSize, Rectangle dest)
        {
            for (int y = dest.Y; y < dest.Y + dest.Height; y++)
            {
                if (y < 0 || y >= texSize) continue;
                for (int x = dest.X; x < dest.X + dest.Width; x++)
                {
                    if (x < 0 || x >= texSize) continue;
                    data[y * texSize + x] = Color.White;
                }
            }
        }

        #endregion
    }
}
