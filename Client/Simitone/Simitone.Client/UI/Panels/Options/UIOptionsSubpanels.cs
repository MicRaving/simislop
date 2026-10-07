using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// The Graphics page of the Options menu.
    ///
    /// <para>
    /// Everything on this page is a <see cref="GlobalSettings"/> property, applied the moment it
    /// is clicked and persisted by the base class. The two lighting quality rows (shadow detail
    /// and water reflections) describe themselves entirely in
    /// <see cref="Simitone.Client.Utils.GraphicsOptions"/>; the rows here only translate
    /// between the radio index the player clicks and the value the ini stores.
    /// </para>
    /// </summary>
    public class UIGraphicsOptionsSubpanel : UIOptionsSubpanelBase
    {
        private const string AATitle = "14", AboutAA = "15", AADesc = "16",
            ShadowTitle = "17", AboutShadows = "18", ShadowDesc = "19",
            LightTitle = "20", AboutLight = "21", LightDesc = "22";

        public UIGraphicsOptionsSubpanel(object SettingsObject) : base(SettingsObject)
        {

        }

        /// <summary>
        /// The rows on this page, as data.
        ///
        /// <para>
        /// Public and separate from <see cref="GetSettingsDescriptors"/> so the set of rows can
        /// be inspected without building a page: constructing the page needs a graphics device
        /// and the content tree, and a row whose property has been renamed or retyped is exactly
        /// the kind of mistake that would otherwise only show up as a row that does nothing.
        /// The settings object is a parameter rather than read off the page because one row
        /// (TexCompression) has to see the live value.
        /// </para>
        /// </summary>
        public static IEnumerable<UIOptionDescriprion> GraphicsDescriptors(object settingsObject) =>
        [
                    new UIIntPropOptionDescription(typeof(GlobalSettings).GetProperty("AntiAlias"), 0, 2) {
                        LocalizationInfo = new(AATitle, AboutAA, AADesc)
                    },
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty("EnableTransitions")) { },
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty("Weather")) { },
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty("SmoothZoom")) { },
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty("DirectionalLight3D")) { },
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty("ComplexShaders")) { },
                    //TexCompression is a bitfield: bit 0 is the on/off the checkbox shows, bit 1
                    //is "chosen by the user" rather than auto-detected. Reading it through
                    //ConvertFrom off FSOEnvironment keeps the checkbox in step with what the
                    //renderer is actually doing this session, and writing it preserves bit 1
                    //(and sets it, so the choice is not re-auto-detected on the next start).
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty("TexCompression"),
                        (obj) => (((((GlobalSettings)settingsObject).TexCompression) & 1) ^ 1) | 2,
                        (obj) => FSOEnvironment.TexCompress) { },
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty("Windowed")) { },
                    // Dynamic Sun. The one graphics setting that is not a GlobalSettings property:
                    // it is a change in how the lot is lit, so it is opt-in and defaults to off,
                    // and it is stored in Simitone's own sidecar ini rather than in FreeSO's. See
                    // SunCycleSetting for why, and UIExternalBoolPropOptionDescription for how the
                    // page can show a setting that has no property on the settings object.
                    new UIExternalBoolPropOptionDescription(
                        typeof(SunCycleOption).GetProperty(nameof(SunCycleOption.SunCycle)),
                        () => SunCycleSetting.Enabled,
                        value => SunCycleSetting.SetEnabled((bool)value)) {
                        // TS1 never had this switch, so there is no id in the Options text table
                        // to point at; the fallback strings are what it is captioned with.
                        LocalizationInfo = new(null, null, null,
                            "Dynamic Sun",
                            "Dynamic Sun",
                            SunCycleSetting.DescribeEffect())
                    },
                    /*new UITogglableIntPropOptionDescription(typeof(GlobalSettings).GetProperty("LightingMode"),
                        typeof(GlobalSettings).GetProperty("Lighting"),0,3){
                        LocalizationInfo = new(LightTitle, AboutLight, LightDesc)
                    },*/
                    // Four steps, not three, and the middle two are not the same thing: off,
                    // flat 2d lightmap, plus 3d wall and object shadows, then ultra. The generic
                    // Low/Medium/High captions would label two of them "Medium", so the row
                    // names its own steps - the same names the in-game settings strip shows.
                    new UIIntPropOptionDescription(typeof(GlobalSettings).GetProperty("LightingMode"), 0, 3)
                    {
                        LocalizationInfo = new(LightTitle, AboutLight, LightDesc),
                        ValueTooltips = Enumerable.Range(0, 4)
                            .Select(level => GraphicsOptions.DescribeLightingMode(level))
                            .ToArray()
                    },
                    //ShadowDetail stores light/shadow map pixels per lot tile; the page shows it
                    //as low/medium/high, so both directions have to be translated. This
                    //replaces the old "ShadowQuality" row, which was wired to a key nothing in
                    //the renderer read - the resolution was a literal in World.ChangedWorldConfig,
                    //so the row saved a preference that changed nothing.
                    new UIIntPropOptionDescription(typeof(GlobalSettings).GetProperty("ShadowDetail")) {
                        LocalizationInfo = new(ShadowTitle, AboutShadows, ShadowDesc),
                        ConvertTo = (radio) => GraphicsOptions.ShadowDetailFromQuality((int)radio),
                        ConvertFrom = (setting) => GraphicsOptions.ShadowDetailToQuality((int)setting)
                    },
                    // Water/pool surface sheen. The lot view has no cube map or screen-space
                    //reflection path, so this drives the specular exponent the 3d floor shader
                    //already uses for water - a fresnel-style approximation, not a reflection of
                    //the world. Off/On/Strong, and the ini value is the quality step itself.
                    new UIIntPropOptionDescription(typeof(GlobalSettings).GetProperty("WaterReflections")) {
                        // No TS1 UI text exists for a setting TS1 never had, so the water row
                        // has no ids to point at and carries its own caption and tooltip.
                        LocalizationInfo = new(null, null, null,
                            "Water Reflections",
                            "Water Reflections",
                            "How bright the sun and sky sheen on pools and other water is drawn. This is a shading approximation rather than a reflection of the world, and it only applies in the 3D view."),
                        ValueTooltips = Enumerable.Range(GraphicsOptions.MinWaterReflections,
                                GraphicsOptions.MaxWaterReflections - GraphicsOptions.MinWaterReflections + 1)
                            .Select(step => GraphicsOptions.DescribeWaterReflections(step))
                            .ToArray()
                    },
                    new UIIntPropOptionDescription(typeof(GlobalSettings).GetProperty("GlobalGraphicsMode"), (int)GlobalGraphicsMode.Full2D, (int)GlobalGraphicsMode.Full3D) {
                        ConvertTo = (radio) => (GlobalGraphicsMode)((int)radio), // convert to enum
                        ConvertFrom = (setting) => (int)(GlobalGraphicsMode)setting // convert from enum
                    },
                    // The two rows of the connected-neighbourhood feature, and they are two rows on
                    // purpose. Surrounding Lots is the in-lot half: it is a real GlobalSettings
                    // property the city renderer already reads, and it decides what is drawn around
                    // the lot the player is standing in. Neighbourhood View is the on-screen half:
                    // whether the neighbourhood picker is a painted map or a generated 3D world, and
                    // it lives in Simitone's own sidecar ini because GlobalSettings is FreeSO's to
                    // edit. Both are described in NeighbourhoodViewRow / SurroundingLotModeRow.
                    SurroundingLotModeRow.Descriptor(),
                    NeighbourhoodViewRow.Descriptor(),
                    // The third row of the same feature, and the one a player actually needs.
                    // Neighbourhood View says what the map screen draws and Surrounding Lots says
                    // what is drawn around the lot you are in; both are on their own by default.
                    // This one is the master switch for Part B itself - the five capabilities that
                    // ship - and it is off by default, because "nobody has asked for it" and "a
                    // player turned it off" have to be the same answer. It writes the same
                    // simitone.ini keys the flags already own; see NeighbourhoodWorldRow for why
                    // it does not consult the view row, and why it is not greyed out when the
                    // view is not 3D.
                    ..NeighbourhoodWorldRow.Descriptors(),
                    //new UIIntPropOptionDescription(typeof(GlobalSettings).GetProperty("DPIScaleFactor"), 1, 3),
        ];

        protected override IEnumerable<UIOptionDescriprion> GetSettingsDescriptors() => GraphicsDescriptors(SettingsObjectInstance);

        /// <summary>
        /// Persists the Dynamic Sun choice when its row is the one that changed.
        /// </summary>
        /// <remarks>
        /// Ordered after the base call on purpose. The base method saves the
        /// <c>GlobalSettings</c> object - which is all a reflected control can do, and is what
        /// every other row on this page relies on - re-applies the graphics snapshot, and rebuilds
        /// the page's controls. By the time this runs the choice is already in
        /// <c>WorldConfig</c>; all that is left is to write it down, which is the one step the
        /// base's save cannot do because the key is not in that object.
        /// </remarks>
        protected override void OnSettingsChanged(params UIOptionDescriprion[] ChangedSettings)
        {
            base.OnSettingsChanged(ChangedSettings);

            foreach (var changed in ChangedSettings)
            {
                if (SunCycleSetting.Describes(changed))
                {
                    SunCycleSetting.SetEnabled(SunCycleSetting.Enabled, save: true);
                }
                //The same shape, for the same reason: the neighbourhood view's key is not a
                //GlobalSettings property, so the base save cannot have written it down. Recorded
                //in memory by the row's own delegate; this is the step that persists it.
                if (NeighbourhoodViewRow.Describes(changed))
                {
                    NeighbourhoodViewRow.Apply();
                }
                //And the same for Part B's master switch, except that here the row's own
                //delegates have already recorded five flags - the base save wrote none of them,
                //because none of the five keys is a GlobalSettings property. Apply writes them
                //all down, one read-merge-write each, which leaves the sun cycle and the view
                //this same file holds untouched.
                if (NeighbourhoodWorldRow.Describes(changed))
                {
                    NeighbourhoodWorldRow.Apply();
                }
            }
        }

        protected override void OnSettingVerifying(UIOptionDescriprion VerifyOption)
        {
            if (VerifyOption.AttachedProperty?.Name == "TexCompression") {
                //textures are already resident at this point, so the change only lands on a restart
                UIAlert alert = null; // show restart alert
                alert = UIScreen.GlobalShowAlert(new UIAlertOptions()
                {
                    Message = OptionsStrings.Get("25", "This change will take effect the next time you start the game."),
                    Buttons = UIAlertButton.Ok(x => {
                        UIScreen.RemoveDialog(alert);
                    })
                }, true);
            }
            base.OnSettingVerifying(VerifyOption);
        }
    }

    /// <summary>
    /// The Gameplay page of the Options menu: the free-will and edge-scroll toggles, plus the
    /// language picker.
    /// </summary>
    public class UIGeneralOptionsSubpanel : UIOptionsSubpanelBase
    {
        internal const string FreeWillTitle = "59", AboutFreeWill = "60", FreeWillDesc = "61",
            EdgeScrollTitle = "62", AboutEdgeScroll = "63", EdgeScrollDesc = "64";

        /// <summary>
        /// Languages TS1 has no UI text for. Simitone reads its strings out of the TS1
        /// UIText.iff, which only carries the English set, so offering the rest of the enum
        /// would be offering the player a choice that changes nothing.
        /// </summary>
        private STRLangCode[] hiddenLanguages =
        {
            STRLangCode.Default,
            STRLangCode.Japanese,
            STRLangCode.SimplifiedChinese,
            STRLangCode.TraditionalChinese,
            STRLangCode.Thai,
            STRLangCode.Korean,
            STRLangCode.Slovak
        };

        public UIGeneralOptionsSubpanel(object SettingsObject) : base(SettingsObject)
        {

        }

        protected override void OnControlsPopulated(Rectangle ReservedSpace)
        {
            base.OnControlsPopulated(ReservedSpace);
            PopulateLanguages(ReservedSpace.Bottom);
        }

        private void PopulateLanguages(int Y)
        {
            int X = 30;
            UILabel titleLabel = new()
            {
                // TS1's own Options table has no id for this: the language page is a Simitone
                // addition, and the language names below it are enum identifiers rather than
                // words to translate.
                Caption = SimitoneStrings.Get("180", "Language"),
                Position = new(X, Y),
                CaptionStyle = TextStyle.DefaultTitle.Clone()
            };
            titleLabel.CaptionStyle.Size = 18;
            Add(titleLabel);
            Y += 35;
            STRLangCode[] DataSource = Enum.GetValues<STRLangCode>();
            for (int i = 0; i < DataSource.Length; i++)
            {
                var code = DataSource[i];
                if (hiddenLanguages.Contains(code)) continue;
                UITwoStateButton button = new UITwoStateButton(Content.Get().CustomUI.Get("blank_blue.png").Get(GameFacade.GraphicsDevice))
                {
                    Caption = code.ToString(),
                    Position = new(X, Y)
                };
                Add(button);
                if ((button.Position.X + button.Width) > Width)
                { // every five buttons, nextline
                    Y += (int)button.Size.Y + 10;
                    X = 30;
                    button.Position = new(X, Y);
                }
                button.OnButtonClick += (elem) => OnTranslationChanging(code);
                X += 10 + (int)button.Width;
            }
        }

        private void OnTranslationChanging(STRLangCode code)
        {
            if ((UIScreen.Current is not TS1GameScreen game)) return;
            game.RuntimeChangeLanguage(code);
            if (SettingsObjectInstance is GlobalSettings settings)
            {
                settings.LanguageCode = (byte)code;
                settings.CurrentLang = Enum.GetName(typeof(STRLangCode), code);
                settings.Save();
            }
            // reload the page in the new language
            if ((Parent?.Parent) is UIMobileDialog dlg)
                dlg.Close();
        }

        protected override IEnumerable<UIOptionDescriprion> GetSettingsDescriptors() =>
        [
                    // Free will is the only setting on this page that is not a plain
                    // GlobalSettings value: the engine reads VM.FreeWillEnabled, and the
                    // property is only where the choice is parked between sessions. The
                    // descriptor writes the property like any other and OnSettingsChanged pushes
                    // it into the engine, which is the one step a reflected checkbox cannot do
                    // for itself.
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty(FreeWillSetting.SettingsKey)) {
                        LocalizationInfo = new(FreeWillTitle, AboutFreeWill, FreeWillDesc)
                    },
                    new UIBoolPropOptionDescription(typeof(GlobalSettings).GetProperty("EdgeScroll")) {
                        LocalizationInfo = new(EdgeScrollTitle, AboutEdgeScroll, EdgeScrollDesc)
                    },
        ];

        /// <summary>
        /// After the base class has saved the settings, hands the free-will choice to the
        /// engine.
        ///
        /// <para>
        /// The base class writes the <c>TS1FreeWill</c> property and saves the ini, which is
        /// all a reflected control can do; nothing in <c>GlobalSettings</c> is what the lot
        /// reads, so without this the checkbox would save a preference the VM would never look
        /// at. Ordered after the base call on purpose: the base call rebuilds the page's
        /// controls from the settings object, so the row the player just clicked is rebuilt
        /// showing the value that was persisted.
        /// </para>
        /// </summary>
        protected override void OnSettingsChanged(params UIOptionDescriprion[] ChangedSettings)
        {
            base.OnSettingsChanged(ChangedSettings);

            if (SettingsObjectInstance is not GlobalSettings settings) return;
            foreach (var changed in ChangedSettings)
            {
                if (!FreeWillSetting.Describes(changed)) continue;
                FreeWillSetting.SetEnabled(settings.TS1FreeWill, settings);
            }
        }
    }

    /// <summary>
    /// The Sound page of the Options menu: the four volume sliders TS1's sound engine separates,
    /// each with a mute of its own.
    ///
    /// <para>
    /// The page was a placeholder, and the four levels it shows have been on
    /// <see cref="GlobalSettings"/> the whole time - written by the ini, read once at startup, and
    /// unreachable from inside the running game. A preference a player cannot reach is the same
    /// as no preference, so this is the page that makes <see cref="SoundVolumeSetting"/>'s four
    /// properties real.
    /// </para>
    /// </summary>
    public class UIAudioOptionsSubpanel : UIOptionsSubpanelBase
    {
        public UIAudioOptionsSubpanel(object SettingsObject) : base(SettingsObject)
        {

        }

        protected override IEnumerable<UIOptionDescriprion> GetSettingsDescriptors() => SoundDescriptors();

        /// <summary>
        /// The four rows, as data.
        ///
        /// <para>
        /// Public and separate from <see cref="GetSettingsDescriptors"/> for the reason
        /// <see cref="UIGraphicsOptionsSubpanel.GraphicsDescriptors"/> is: a page cannot be built
        /// in a test (it needs a graphics device and the content tree), but the set of rows it
        /// describes can be inspected, and a row whose property has been renamed is exactly the
        /// mistake that would otherwise only show up as a slider that does nothing.
        /// </para>
        /// </summary>
        public static IEnumerable<UIOptionDescriprion> SoundDescriptors() =>
        [
            VolumeRow(SoundChannel.SoundEffects, "960", "961", "962", "Sound Effects",
                "The sounds the lot makes: doors, appliances, footsteps, the click of a control you just pressed."),
            VolumeRow(SoundChannel.Music, "963", "964", "965", "Music",
                "The background music the lot plays."),
            VolumeRow(SoundChannel.Voice, "966", "967", "968", "Voice",
                "The Sims' own voices and anything the game narrates."),
            VolumeRow(SoundChannel.Ambience, "969", "970", "971", "Ambience",
                "Weather, and the sounds the lot plays at you in the background."),
        ];

        /// <summary>
        /// One group's row: a slider over the level, and a checkbox that silences the group
        /// without touching the level.
        /// </summary>
        /// <remarks>
        /// The tooltip ids are the group's own, and the fallbacks are what the row says in a
        /// build with no string table. The other two pages read TS1's Options table (145), and a
        /// sound row deliberately does not: the table came with the player's copy of the game and
        /// the audit that found this page empty could not read a single entry out of it, so a
        /// guessed id would put some unrelated string on a Simitone control. Simitone's own table
        /// is used instead, which is the pattern the Dynamic Sun row already set.
        /// </remarks>
        private static UIOptionDescriprion VolumeRow(SoundChannel channel, string titleId, string aboutTitleId,
            string aboutId, string title, string about)
        {
            return new UISliderPropOptionDescription(
                typeof(GlobalSettings).GetProperty(SoundVolumeSetting.SettingsKeyOf(channel)),
                SoundVolumeSetting.MinVolume, SoundVolumeSetting.MaxVolume)
            {
                // The caption carries an id as well as the tooltip: every other Options row's
                // caption is localized, and a slider row whose caption cannot be translated is
                // the one row a player reads first.
                LocalizationInfo = new UIOptionLocalizationDescription(null, null, null, title, title, about)
                    .WithSimitoneIds(titleId, aboutTitleId, aboutId),
                ReadMuted = () => SoundVolumeSetting.IsMuted(channel),
                WriteMuted = (muted) => SoundVolumeSetting.SetMuted(channel, muted),
                MuteTooltip = SoundVolumeSetting.DescribeMute(false)
            };
        }

        /// <summary>
        /// Saves the level, hands all four groups to the sound engine, writes the mute flags and
        /// rebuilds the page so the row the player just moved is drawn from the value that was
        /// stored.
        /// </summary>
        /// <remarks>
        /// Deliberately does not call the base implementation, and the reason is worth stating
        /// because the base is what the other two pages use. The base saves the settings object -
        /// which is right, and is done here - and then re-applies the whole graphics snapshot and
        /// asks the lot view to rebuild around it. That is correct on the graphics page; on this
        /// one it would rebuild every wall and floor in the lot once per frame of a slider drag,
        /// for a setting that has nothing to do with the renderer.
        /// </remarks>
        protected override void OnSettingsChanged(params UIOptionDescriprion[] ChangedSettings)
        {
            if (SettingsObjectInstance is GlobalSettings settings)
            {
                settings.Save();
                SoundVolumeSetting.Apply(settings);
            }
            SoundVolumeSetting.Save();
            CreateSettings();
        }
    }
}
