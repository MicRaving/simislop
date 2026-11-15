using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Content;
using FSO.LotView;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// Base for the pages of the Options menu. A page does not draw its own controls: it
    /// describes each setting it wants as a <see cref="UIOptionDescriprion"/> and this class
    /// reflects over the settings object to read the current value, build the matching
    /// switch / radio control, and write the new value back on click.
    ///
    /// <para>
    /// That is the whole point of the design: adding a setting to the Options menu is one
    /// line in a page's descriptor list rather than a constructor, a layout and an apply
    /// handler, and there is no way for a control to end up writing to a property the page
    /// forgot to list.
    /// </para>
    /// </summary>
    public abstract class UIOptionsSubpanelBase : UIContainer
    {
        /// <summary>
        /// The TS1 UI text table the Options strings live in.
        /// </summary>
        internal const string OptionsTable = "145";

        /// <summary>Localized "Low", the caption of the first radio in a value group.</summary>
        internal const string LowTitle = "39";

        /// <summary>Localized "Medium", the middle radio's caption.</summary>
        internal const string MedTitle = "40";

        /// <summary>Localized "High", the last radio's caption.</summary>
        internal const string HighTitle = "41";

        /// <summary>
        /// The layout width the Options pages are built for. Set by
        /// <see cref="UIOptionsPanel.ContentWidth"/>, which owns the pages' real extent.
        /// </summary>
        private int _contentWidth = UIOptionsPanel.DefaultContentWidth;

        /// <summary>
        /// How the value of a setting is represented on the page.
        /// </summary>
        public enum SettingType
        {
            /// <summary>Not a setting; the base type itself.</summary>
            None,

            /// <summary>An int/enum drawn as a group of radio buttons.</summary>
            Variable,

            /// <summary>An int/enum drawn as radio buttons behind an on/off switch.</summary>
            ToggleVariable,

            /// <summary>A bool drawn as a single on/off switch.</summary>
            BoolProp,

            /// <summary>
            /// An int drawn as a slider rather than a group of radios, for a setting with more
            /// than a handful of steps and a value that reads as a level rather than a choice -
            /// a volume is eleven values, and a row of eleven radio buttons is not a control,
            /// it is a menu.
            /// </summary>
            Slider,
        }

        /// <summary>
        /// How much vertical room a row of each type takes. Kept next to
        /// <see cref="SettingType"/> because the layout and the type are one decision: a slider
        /// is taller than a radio, and laying them all out as if they were the same height
        /// overlaps the rows.
        /// </summary>
        private static int RowHeight(SettingType type) => type == SettingType.Slider ? 34
            : type == SettingType.ToggleVariable ? BoolRowHeight : 25;

        /// <summary>
        /// Vertical step of a row that carries an on/off switch: the switch is
        /// <see cref="UISwitch.TrackHeight"/> tall, so a 25 step would overlap the next row.
        /// </summary>
        private const int BoolRowHeight = 30;

        /// <summary>
        /// The three UI-text ids a setting needs: its row caption, the title of the "about
        /// this setting" tooltip, and the tooltip's body.
        ///
        /// <para>
        /// The ids are a slot for localization, not a requirement. The TS1 UI text table only
        /// carries English and only covers the settings TS1 itself had, so a setting Simitone
        /// added has no id to point at; the optional fallback strings are what such a setting
        /// supplies instead, and are used whenever the id is absent from the table.
        /// </para>
        /// </summary>
        public struct UIOptionLocalizationDescription
        {
            public string TitleID;

            /// <summary>Text to use for the row caption when <see cref="TitleID"/> is not in the table.</summary>
            public string Title;

            public string AboutTitleTextID;
            public string AboutDescriptionID;

            /// <summary>Text to use for the tooltip's title when its id is not in the table.</summary>
            public string AboutTitle;

            /// <summary>Text to use for the tooltip's body when its id is not in the table.</summary>
            public string AboutDescription;

            /// <summary>
            /// Ids in Simitone's own string table, tried when the TS1 table has no entry.
            /// </summary>
            /// <remarks>
            /// A setting the game itself had points at table 145 and is translated with the
            /// player's copy of it. A setting Simitone added has no id there at all, and pointing
            /// it at a guessed one would put an unrelated game's words on a Simitone control -
            /// which is why the Dynamic Sun and Water Reflections rows carry literal captions
            /// instead. That leaves them English-only, which is a real gap for a player who does
            /// not read English, and this is the fix: the id belongs in Simitone's own table,
            /// which ships in every language folder a translator has filled in.
            /// </remarks>
            public string SimitoneTitleID;
            public string SimitoneAboutTitleID;
            public string SimitoneAboutDescriptionID;

            public UIOptionLocalizationDescription(string titleID, string aboutTitleTextID, string aboutDescriptionID)
            {
                TitleID = titleID;
                AboutTitleTextID = aboutTitleTextID;
                AboutDescriptionID = aboutDescriptionID;
                Title = null;
                AboutTitle = null;
                AboutDescription = null;
                SimitoneTitleID = null;
                SimitoneAboutTitleID = null;
                SimitoneAboutDescriptionID = null;
            }

            public UIOptionLocalizationDescription(string titleID, string aboutTitleTextID, string aboutDescriptionID,
                string title, string aboutTitle, string aboutDescription)
            {
                TitleID = titleID;
                AboutTitleTextID = aboutTitleTextID;
                AboutDescriptionID = aboutDescriptionID;
                Title = title;
                AboutTitle = aboutTitle;
                AboutDescription = aboutDescription;
                SimitoneTitleID = null;
                SimitoneAboutTitleID = null;
                SimitoneAboutDescriptionID = null;
            }

            /// <summary>
            /// Attaches Simitone-table ids to an already-built description, leaving the TS1 ids
            /// and every fallback exactly as they were.
            /// </summary>
            public UIOptionLocalizationDescription WithSimitoneIds(string titleId, string aboutTitleId,
                string aboutDescriptionId)
            {
                SimitoneTitleID = titleId;
                SimitoneAboutTitleID = aboutTitleId;
                SimitoneAboutDescriptionID = aboutDescriptionId;
                return this;
            }
        }

        /// <summary>
        /// One setting on a page: which property of the settings object it drives, how that
        /// property's value maps onto the control's value, and how it is captioned.
        /// </summary>
        public abstract class UIOptionDescriprion
        {
            public abstract SettingType Type { get; }

            /// <summary>The settings property this descriptor reads and writes.</summary>
            public PropertyInfo AttachedProperty { get; }

            public UIOptionLocalizationDescription? LocalizationInfo { get; set; }

            protected UIOptionDescriprion(PropertyInfo AttachedProperty)
            {
                this.AttachedProperty = AttachedProperty;
            }

            /// <summary>
            /// Convert To is used to convert from the value assigned to the RadioButton (e.g. LowBound -> Highbound) to the value of the setting.
            /// </summary>
            public Func<object, object> ConvertTo;

            /// <summary>
            /// Convert From is used to convert from the value of the setting to the value assigned to the RadioButton (e.g. LowBound -> Highbound)
            /// </summary>
            public Func<object, object> ConvertFrom;

            /// <summary>Reads the setting, already converted to the control-side value.</summary>
            public virtual object GetValue(object Instance)
            {
                var value = AttachedProperty.GetValue(Instance);
                return ConvertFrom != null ? ConvertFrom(value) : value;
            }

            /// <summary>Writes the setting from a control-side value.</summary>
            public virtual void SetValue(object Instance, object value)
            {
                value = ConvertTo != null ? ConvertTo(value) : value;
                AttachedProperty.SetValue(Instance, value);
            }

            public string GetTitle()
            {
                if (!LocalizationInfo.HasValue) return AttachedProperty?.Name ?? "No Name";
                var info = LocalizationInfo.Value;
                // TS1's own table first: for a setting the game had, that is the translation
                // that shipped with the player's copy. Simitone's table second, for a setting the
                // game did not have. The literal is the floor under both.
                var fromGame = OptionsStrings.Get(info.TitleID, null);
                if (!string.IsNullOrEmpty(fromGame)) return fromGame;
                var fromSimitone = SimitoneStrings.Get(info.SimitoneTitleID, null);
                if (!string.IsNullOrEmpty(fromSimitone)) return fromSimitone;
                return info.Title ?? AttachedProperty?.Name;
            }

            /// <summary>
            /// The tooltip body, or null when there is nothing to say. A setting with no ids and
            /// no fallbacks gets no tooltip rather than an empty one, which would otherwise be
            /// a clickable blank box.
            /// </summary>
            public string GetAboutInfo()
            {
                if (!LocalizationInfo.HasValue) return null;
                var info = LocalizationInfo.Value;
                var title = AboutText(info.AboutTitleTextID, info.SimitoneAboutTitleID, info.AboutTitle);
                var description = AboutText(info.AboutDescriptionID, info.SimitoneAboutDescriptionID, info.AboutDescription);
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(description)) return null;
                return (title ?? "") + "\n" + (description ?? "");
            }

            /// <summary>
            /// One half of a tooltip: TS1's table, then Simitone's, then the literal. Split out so
            /// the order is written down once - it is the whole of "which translation wins".
            /// </summary>
            private static string AboutText(string gameId, string simitoneId, string fallback)
            {
                var fromGame = OptionsStrings.Get(gameId, null);
                if (!string.IsNullOrEmpty(fromGame)) return fromGame;
                var fromSimitone = SimitoneStrings.Get(simitoneId, null);
                if (!string.IsNullOrEmpty(fromSimitone)) return fromSimitone;
                return fallback;
            }
        }

        /// <summary>A bool setting, drawn as one on/off switch.</summary>
        public class UIBoolPropOptionDescription : UIOptionDescriprion
        {
            public override SettingType Type => SettingType.BoolProp;

            public UIBoolPropOptionDescription(PropertyInfo AttachedProperty) : base(AttachedProperty)
            {
                if (AttachedProperty == null) throw new ArgumentNullException(nameof(AttachedProperty));
                if (AttachedProperty.PropertyType != typeof(bool)) throw new ArgumentException("Property must be of type bool");
            }

            /// <summary>
            /// A switch whose underlying setting is not itself a bool, where
            /// <paramref name="ConvertFrom"/> produces the bool the control shows and
            /// <paramref name="ConvertTo"/> produces the value to store. TexCompression is the
            /// case in point: it is a bitfield (bit 0 = on/off, bit 1 = auto/user chosen), and
            /// only bit 0 is the switch.
            ///
            /// <para>
            /// The property is deliberately not type-checked here. It is
            /// <see cref="ConvertFrom"/>'s result, not the property, that has to be a bool,
            /// and that cannot be known until the descriptor is bound to a value.
            /// </para>
            /// </summary>
            public UIBoolPropOptionDescription(PropertyInfo AttachedProperty, Func<object, object> ConvertTo, Func<object, object> ConvertFrom) : base(AttachedProperty)
            {
                if (AttachedProperty == null) throw new ArgumentNullException(nameof(AttachedProperty));
                this.ConvertTo = ConvertTo;
                this.ConvertFrom = ConvertFrom;
            }
        }

        /// <summary>
        /// An int (or enum) setting drawn as one radio per value in
        /// [<see cref="LowBound"/>, <see cref="Highbound"/>]. The bounds are the
        /// <em>control</em> values, not the setting's - use <see cref="ConvertTo"/> /
        /// <see cref="ConvertFrom"/> when the setting stores something else (a shadow map
        /// resolution, an enum, and so on).
        /// </summary>
        public class UIIntPropOptionDescription : UIOptionDescriprion
        {
            public override SettingType Type => SettingType.Variable;

            public int LowBound { get; }
            public int Highbound { get; }

            /// <summary>
            /// Per-radio captions, indexed from <see cref="LowBound"/>, for a group whose steps
            /// are not the plain Low/Medium/High triple - the lighting row has four of them and
            /// would otherwise caption two different buttons "Medium". Null keeps the default.
            /// </summary>
            public string[] ValueTooltips { get; set; }

            /// <summary>Localized "Low", used for the first radio when <see cref="ValueTooltips"/> does not cover it.</summary>
            public string LowTooltipText { get; set; } = "Low";

            /// <summary>Localized "Medium", used for a middle radio when <see cref="ValueTooltips"/> does not cover it.</summary>
            public string MediumTooltipText { get; set; } = "Medium";

            /// <summary>Localized "High", used for the last radio when <see cref="ValueTooltips"/> does not cover it.</summary>
            public string HighTooltipText { get; set; } = "High";

            public UIIntPropOptionDescription(PropertyInfo AttachedProperty, int LowBound = 0, int Highbound = 2) : base(AttachedProperty)
            {
                if (AttachedProperty == null) throw new ArgumentNullException(nameof(AttachedProperty));
                if (!AttachedProperty.PropertyType.IsEnum)
                    if (AttachedProperty.PropertyType != typeof(int)) throw new ArgumentException("Property must be of type int");
                this.LowBound = LowBound;
                this.Highbound = Highbound;
            }

            /// <summary>
            /// Caption for the radio that represents <paramref name="value"/>.
            ///
            /// <para>
            /// An explicit <see cref="ValueTooltips"/> entry wins; otherwise the captions fall
            /// back to the localized Low/Medium/High by position, which is what a three-step
            /// group wants and is close enough for any other.
            /// </para>
            /// </summary>
            public string GetValueTooltip(int value)
            {
                var index = value - LowBound;
                if (ValueTooltips != null && index >= 0 && index < ValueTooltips.Length)
                {
                    var tooltip = ValueTooltips[index];
                    if (!string.IsNullOrEmpty(tooltip)) return tooltip;
                }
                if (value == LowBound) return LowTooltipText;
                if (value == Highbound) return HighTooltipText;
                return MediumTooltipText;
            }
        }

        /// <summary>
        /// Marks a row whose storage is <em>not</em> a property of the settings object.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both external row types are exempt from the rule the page's reflection rests on - a row
        /// normally names a <c>GlobalSettings</c> property, and one that does not has to say so in
        /// its own type or it would be reflecting against an object it has no business touching.
        /// The exemption needs one name rather than a list, because there is more than one such
        /// row and the list is exactly the thing that quietly stops being updated: a test that
        /// exempts one concrete type passes forever after a second one is added, and the new row
        /// gets no coverage at all.
        /// </para>
        /// <para>
        /// A marker rather than a base class, deliberately. The two row types are otherwise
        /// unrelated - one is a switch and one is a radio group - and
        /// <see cref="UIBoolPropOptionDescription"/> has to stay the base of the switch row
        /// because the page casts to it to decide which control to build. A common base would
        /// mean one of the two inheriting from something it is not.
        /// </para>
        /// </remarks>
        public interface IUIExternalPropOptionDescription
        {
        }

        /// <summary>
        /// A bool setting whose storage is not a property of the settings object.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The reflected rows work by naming a <see cref="PropertyInfo"/> on the settings object,
        /// and that is the right shape for almost every setting: the ini is a bag of properties
        /// and the page is a view over it. It is the wrong shape for a setting Simitone owns
        /// itself, because <c>GlobalSettings</c> is not Simitone's to add a property to.
        /// </para>
        /// <para>
        /// So the descriptor carries the property only as a <em>name</em> - it is what
        /// <see cref="UIOptionDescriprion.GetTitle"/> falls back to and what a change-notification
        /// match keys off - and reads and writes through delegates instead. The instance handed in
        /// by the base class is ignored.
        /// </para>
        /// <para>
        /// <see cref="UIOptionDescriprion.GetValue"/> and <see cref="UIOptionDescriprion.SetValue"/>
        /// are virtual for exactly this: without it a derived row could not intercept them, and
        /// <c>new</c> would not help, because the base class's own call sites are compiled against
        /// the base type and never see a hidden member.
        /// </para>
        /// </remarks>
        public class UIExternalBoolPropOptionDescription : UIBoolPropOptionDescription, IUIExternalPropOptionDescription
        {
            private readonly Func<bool> read;
            private readonly Action<bool> write;

            public UIExternalBoolPropOptionDescription(PropertyInfo nameCarrier, Func<bool> read, Action<bool> write)
                : base(nameCarrier)
            {
                if (read == null) throw new ArgumentNullException(nameof(read));
                if (write == null) throw new ArgumentNullException(nameof(write));
                this.read = read;
                this.write = write;
            }

            public override object GetValue(object Instance) => read();

            public override void SetValue(object Instance, object value) => write((bool)value);
        }

        /// <summary>
        /// An int/enum setting whose storage is not a property of the settings object, drawn as a
        /// group of radio buttons.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The value-group twin of <see cref="UIExternalBoolPropOptionDescription"/>, and for the
        /// same reason: <c>GlobalSettings</c> is FreeSO's, so a setting Simitone owns itself cannot
        /// be a property of it, and the page's reflection is built around naming one. The property
        /// is carried as a name only - <see cref="UIOptionDescriprion.GetTitle"/> falls back to it
        /// and a change-notification match keys off it - and the value goes through delegates.
        /// </para>
        /// <para>
        /// The bounds are the <em>control's</em>, not the setting's, exactly as in
        /// <see cref="UIIntPropOptionDescription"/>: the radios are drawn from
        /// <see cref="LowBound"/> to <see cref="Highbound"/> inclusive and the read delegate
        /// returns one of those values. A delegate that returns something outside the range draws
        /// a group with nothing selected rather than throwing, which is the right answer for a
        /// value the setting itself has already resolved.
        /// </para>
        /// </remarks>
        public class UIExternalIntPropOptionDescription : UIIntPropOptionDescription, IUIExternalPropOptionDescription
        {
            private readonly Func<int> read;
            private readonly Action<int> write;

            public UIExternalIntPropOptionDescription(
                PropertyInfo nameCarrier, int lowBound, int highBound,
                Func<int> read, Action<int> write)
                : base(nameCarrier, lowBound, highBound)
            {
                if (read == null) throw new ArgumentNullException(nameof(read));
                if (write == null) throw new ArgumentNullException(nameof(write));
                this.read = read;
                this.write = write;
            }

            //boxed int, because CreateRadios unboxes the descriptor's value straight to an int and
            //a boxed enum would throw there rather than draw
            public override object GetValue(object Instance) => read();

            public override void SetValue(object Instance, object value)
                => write(Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// An int setting that is only meaningful while a separate bool setting is on - a
        /// value group with its own enable switch, which greys the radios out when off.
        /// </summary>
        public class UITogglableIntPropOptionDescription : UIIntPropOptionDescription
        {
            public override SettingType Type => SettingType.ToggleVariable;

            public PropertyInfo ToggleProperty { get; }

            public UITogglableIntPropOptionDescription(PropertyInfo AttachedProperty, PropertyInfo ToggleProperty, int LowBound = 0, int Highbound = 2) :
                base(AttachedProperty, LowBound, Highbound)
            {
                if (ToggleProperty == null) throw new ArgumentNullException(nameof(ToggleProperty));
                Debug.Assert(AttachedProperty.PropertyType == typeof(int), "Value property must be of type int");
                Debug.Assert(ToggleProperty.PropertyType == typeof(bool), "Toggle property must be of type bool");
                this.ToggleProperty = ToggleProperty;
            }
        }

        /// <summary>
        /// An int setting drawn as one slider, optionally with a mute switch beside it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The mute half is here because that is what a sound setting's row is: a level and a
        /// way to silence it, on one line. The switch reads and writes through delegates for
        /// the same reason <see cref="UIExternalBoolPropOptionDescription"/> does - a mute is
        /// not a property of the settings object - and a row with no delegates simply has no
        /// switch, so a slider that wants no mute does not have to say so.
        /// </para>
        /// <para>
        /// The value conversion is the other reason this is not the reflected
        /// <see cref="UIIntPropOptionDescription"/> with a different picture: the levels are
        /// stored as a <c>byte</c> and a slider's value is an <c>int</c>, and handing a boxed
        /// int to a byte property through reflection throws rather than converting.
        /// </para>
        /// </remarks>
        public class UISliderPropOptionDescription : UIOptionDescriprion
        {
            public override SettingType Type => SettingType.Slider;

            public int MinValue { get; }
            public int MaxValue { get; }

            /// <summary>Reads the mute state, or null for a row that has no mute switch.</summary>
            public Func<bool> ReadMuted { get; set; }

            /// <summary>Writes the mute state, or null for a row that has no mute switch.</summary>
            public Action<bool> WriteMuted { get; set; }

            /// <summary>Tooltip for the mute switch, or null for none.</summary>
            public string MuteTooltip { get; set; }

            /// <summary>Whether this row draws a mute switch at all.</summary>
            public bool HasMute => ReadMuted != null && WriteMuted != null;

            public UISliderPropOptionDescription(PropertyInfo AttachedProperty, int MinValue, int MaxValue) : base(AttachedProperty)
            {
                if (AttachedProperty == null) throw new ArgumentNullException(nameof(AttachedProperty));
                if (MinValue > MaxValue) throw new ArgumentException("A slider's minimum cannot be above its maximum.");
                this.MinValue = MinValue;
                this.MaxValue = MaxValue;
            }

            /// <summary>The stored level, as the slider's own value.</summary>
            public override object GetValue(object Instance)
            {
                return ToInt32(AttachedProperty.GetValue(Instance));
            }

            /// <summary>Stores a level, converted to whatever the property's type is.</summary>
            public override void SetValue(object Instance, object value)
            {
                var converted = ConvertTo?.Invoke(value) ?? value;
                AttachedProperty.SetValue(Instance, Convert.ChangeType(converted, AttachedProperty.PropertyType));
            }

            private static int ToInt32(object value)
            {
                if (value == null) return 0;
                if (value is int asInt) return asInt;
                if (value is short asShort) return asShort;
                if (value is byte asByte) return asByte;
                if (value is long asLong) return (int)asLong;
                return Convert.ToInt32(value);
            }
        }

        IEnumerable<UIOptionDescriprion> settings => GetSettingsDescriptors();

        /// <summary>
        /// The settings this page shows. Called more than once per rebuild (a lot - it is
        /// enumerated per header check and again per row), so it is expected to be cheap: a
        /// literal list, not a lookup that does work.
        /// </summary>
        protected abstract IEnumerable<UIOptionDescriprion> GetSettingsDescriptors();

        public Texture2D RadioTexture { get; }

        /// <summary>
        /// The width the two-column layout is laid out against. Pushed down by
        /// <see cref="UIOptionsPanel"/> once the page is actually parented, because the
        /// parent's own <c>Size</c> is not meaningful for a plain container.
        /// </summary>
        public int ContentWidth
        {
            get => _contentWidth;
            set
            {
                if (_contentWidth == value) return;
                _contentWidth = value;
                //re-lay the page out at the new width rather than leaving controls at the
                //positions they were built at.
                if (ChildCount > 0) CreateSettings();
            }
        }

        /// <summary>
        /// The horizontal space available to this page. The two columns are placed relative
        /// to it, so a wider dialog widens both.
        /// </summary>
        protected float Width => _contentWidth;

        private string LocalLowText, LocalMedText, LocalHighText;

        /// <summary>
        /// The settings object this page reads and writes - <see cref="GlobalSettings.Default"/>
        /// in practice, but a page works against any instance.
        /// </summary>
        protected object SettingsObjectInstance { get; }

        protected UIOptionsSubpanelBase(object SettingsObjectInstance) : base()
        {
            RadioTexture = Content.Get().CustomUI.Get("radio.png").Get(GameFacade.GraphicsDevice);

            LocalLowText = OptionsStrings.Get(LowTitle, "Low");
            LocalMedText = OptionsStrings.Get(MedTitle, "Medium");
            LocalHighText = OptionsStrings.Get(HighTitle, "High");

            this.SettingsObjectInstance = SettingsObjectInstance;
            CreateSettings();
        }

        /// <summary>
        /// Clears all controls and redraws controls from the provided <see cref="GetSettingsDescriptors"/> descriptors.
        /// </summary>
        protected void CreateSettings()
        {
            int column1Y = 50, column2Y = 50;
            int COLX(int c) => ((int)(Width / 2) * c) + 30;

            bool any = false;

            Children.Clear(); // any controls from prior session should be removed.

            // Enumerated once into a list: the descriptors are per-build objects (a page hands
            // back a fresh list each time) and are about to be decorated below, which would be
            // lost if the next enumeration built new ones.
            var descriptors = settings.ToList();

            // Hand the localized Low/Medium/High words to every value group. A group that names
            // its own steps through ValueTooltips keeps them; the rest fall back to these, so
            // the radio buttons read in the player's language rather than as a bare index.
            foreach (var descriptor in descriptors)
            {
                if (descriptor is not UIIntPropOptionDescription valueGroup) continue;
                valueGroup.LowTooltipText = LocalLowText;
                valueGroup.MediumTooltipText = LocalMedText;
                valueGroup.HighTooltipText = LocalHighText;
            }

            bool hasToggles = descriptors.Any(x => x.Type == SettingType.BoolProp);
            bool hasValues = descriptors.Any(x => x.Type == SettingType.Variable || x.Type == SettingType.ToggleVariable);
            bool hasSliders = descriptors.Any(x => x.Type == SettingType.Slider);

            if (hasToggles)
            {
                CreateColumnHeader(SimitoneStrings.Get("170", "Toggle Settings"), COLX(0), 10);
                any = true;
            }
            if (hasValues || hasSliders)
            {
                CreateColumnHeader(SimitoneStrings.Get("171", "Description"), COLX(1), 10);
                // One header over the control column, and it names whichever kind of control the
                // page actually has. A page of sliders has no Low/Medium/High to say, and a
                // header that says it anyway is a word the player has to work out the meaning
                // of before they can read the row under it.
                CreateColumnHeader(hasSliders
                        ? SimitoneStrings.Get("972", "Level")
                        : $"{LocalLowText}/{LocalMedText}/{LocalHighText}",
                    COLX(1) + SliderLabelWidth, 14, 12);
                any = true;
            }
            if (!any)
            {
                CreateColumnHeader(SimitoneStrings.Get("172", "There are no settings here."), COLX(0), 10);
                return;
            }

            foreach (var setting in descriptors.OrderByDescending(x => x.Type))
            {
                int ColumnX = COLX((setting.Type == SettingType.BoolProp) ? 0 : 1); // ON/OFF on right
                switch (setting.Type)
                {
                    case SettingType.BoolProp:
                        {
                            CreateCheckBox(setting as UIBoolPropOptionDescription, ColumnX, column2Y);
                            column2Y += BoolRowHeight;
                        }
                        continue;
                    case SettingType.ToggleVariable:
                        {
                            CreateToggleRadios(setting as UITogglableIntPropOptionDescription, ColumnX, column1Y);
                        }
                        break;
                    case SettingType.Variable:
                        {
                            CreateRadios(setting as UIIntPropOptionDescription, ColumnX, column1Y);
                        }
                        break;
                    case SettingType.Slider:
                        {
                            CreateSlider(setting as UISliderPropOptionDescription, ColumnX, column1Y);
                        }
                        break;
                }
                // ** not boolprop from beyond this point
                column1Y += RowHeight(setting.Type);
            }
            //allow derived classes to update their layout
            OnControlsPopulated(new Rectangle(COLX(0), 0, (int)(Width / (column2Y != 50 ? 1 : 2)), Math.Max(column1Y, column2Y)));
        }

        /// <summary>
        /// This method is called after <see cref="CreateSettings"/> is called.
        /// <para/>See: <see cref="CreateSettings"/>:
        /// <para/>
        /// <paramref name="ReservedSpace"/> is the region of the control that is occupied by generated controls, please add controls outside this area
        /// if manually creating controls.
        /// </summary>
        protected virtual void OnControlsPopulated(Rectangle ReservedSpace)
        {
            ;
        }

        private void CreateColumnHeader(string Header, int ColumnX, int ColumnY, int FontSize = 18)
        {
            var label = new UILabel()
            {
                Caption = Header,
                CaptionStyle = TextStyle.DefaultTitle.Clone(),
                Position = new Vector2(ColumnX, ColumnY)
            };
            label.CaptionStyle.Size = FontSize;
            Add(label);
        }

        /// <summary>Width of a slider row's caption, before the track starts.</summary>
        private const int SliderLabelWidth = 175;

        /// <summary>Width of a slider's track.</summary>
        private const int SliderTrackWidth = 210;

        /// <summary>Height of a slider's track. The art is 16 tall; this is the hit area.</summary>
        private const int SliderTrackHeight = 20;

        /// <summary>Gap between the end of a slider's track and its mute switch.</summary>
        private const int SliderMuteGap = 24;

        /// <summary>
        /// CustomUI name of the slider art, as <see cref="UISlider"/> wants it: one texture whose
        /// left three quarters are the track and whose right quarter is the thumb.
        /// </summary>
        private const string SliderTextureName = "volume_slider.png";

        /// <summary>
        /// Builds one slider row: the caption, the track, and the mute switch if the row has
        /// one.
        /// </summary>
        /// <remarks>
        /// Ordered so that setting the slider's value cannot fire the change handler: the value
        /// is assigned first, and the handler is subscribed after, so building the page never
        /// looks to the rest of the game like the player moved something.
        /// </remarks>
        private void CreateSlider(UISliderPropOptionDescription Descriptor, int ColumnX, int ColumnY)
        {
            int value = Convert.ToInt32(Descriptor.GetValue(SettingsObjectInstance));
            string Caption = Descriptor.GetTitle();

            int trackLeft = ColumnX + SliderLabelWidth;

            var slider = new UISlider()
            {
                Texture = Content.Get().CustomUI.Get(SliderTextureName).Get(GameFacade.GraphicsDevice),
                Position = new Vector2(trackLeft, ColumnY),
                Orientation = 0, // horizontal
                MinValue = Descriptor.MinValue,
                MaxValue = Descriptor.MaxValue,
                Tooltip = Descriptor.GetAboutInfo()
            };
            slider.SetSize(SliderTrackWidth, SliderTrackHeight);
            slider.Value = Math.Clamp(value, Descriptor.MinValue, Descriptor.MaxValue);
            slider.OnChange += (elem) =>
            {
                var moved = (UISlider)elem;
                int wanted = (int)moved.Value;
                Descriptor.SetValue(SettingsObjectInstance, wanted);
                // A mute is not a level, so a row that keeps both has to say which of the two the
                // player just touched. Anything the player can hear is a level, so a level off
                // the stop is an unmute; a mute is the switch, which stays where they left it.
                if (Descriptor.HasMute && wanted > Descriptor.MinValue && Descriptor.ReadMuted())
                {
                    Descriptor.WriteMuted(false);
                }
                OnSettingsChanged(Descriptor);
            };
            Add(slider);

            if (Descriptor.HasMute)
            {
                var mute = new UISwitch
                {
                    Position = new Vector2(trackLeft + SliderTrackWidth + SliderMuteGap, ColumnY),
                    Tooltip = Descriptor.MuteTooltip,
                    Selected = Descriptor.ReadMuted()
                };
                mute.OnButtonClick += (elem) =>
                {
                    // A UIButton does not flip Selected when it is clicked, so the switch still
                    // shows the old state here: the new one is its opposite. (Taking Selected
                    // as the answer wrote the old value back and left the mute dead.)
                    Descriptor.WriteMuted(!mute.Selected);
                    OnSettingsChanged(Descriptor);
                };
                Add(mute);
            }

            var label = new UILabel()
            {
                Position = new Vector2(ColumnX, ColumnY),
                Caption = Caption,
                Tooltip = Descriptor.GetAboutInfo()
            };
            Add(label);
        }

        private (UILabel Label, UIButton Button) CreateCheckBox(UIBoolPropOptionDescription Descriptor, int ColumnX, int ColumnY)
        {
            // get current setting state using reflection
            UIBoolPropOptionDescription setting = Descriptor;
            bool value = (bool)Descriptor.GetValue(SettingsObjectInstance);

            //get title
            string Caption = Descriptor.GetTitle();

            //create switch and label
            int left = 200 + ColumnX;
            var check = new UISwitch
            {
                Position = new Vector2(left, ColumnY),
                Tooltip = Descriptor.GetAboutInfo(),
                Selected = value
            };
            // ** set new setting value on click
            check.OnButtonClick += (elem) =>
            {
                Descriptor.SetValue(SettingsObjectInstance, !check.Selected);
                OnSettingsChanged(Descriptor);
            };
            Add(check);

            var label = new UILabel()
            {
                Caption = Caption,
                Position = new Vector2(ColumnX, ColumnY),
                Tooltip = Descriptor.GetAboutInfo()
            };
            Add(label);
            return (label, check);
        }

        private void CreateToggleRadios(UITogglableIntPropOptionDescription ToggleSetting, int ColumnX, int ColumnY)
        {
            int left = 200 + ColumnX, margin = 60, labelLeft = ColumnX;

            // get current setting state using reflection
            UITogglableIntPropOptionDescription setting = ToggleSetting;
            bool enabled = (bool)setting.ToggleProperty.GetValue(SettingsObjectInstance);
            int value = (int)setting.GetValue(SettingsObjectInstance);

            //get title
            string Caption = ToggleSetting.GetTitle();

            // ON/OFF TOGGLE
            var check = new UISwitch
            {
                Position = new(left, ColumnY),
                // Not one of TS1's own strings: the game never drew this switch, so there is no
                // id for it and this is Simitone's table or the English.
                Tooltip = SimitoneStrings.Get("173", "On/Off"),
                Selected = enabled
            };
            // set the ON/OFF setting value on click
            check.OnButtonClick += (elem) =>
            {
                ToggleSetting.ToggleProperty.SetValue(SettingsObjectInstance, !check.Selected);
                OnSettingsChanged(ToggleSetting);
            };
            Add(check);

            //RADIOs
            for (int i = setting.LowBound; i <= setting.Highbound; i++)
            {
                var radio = new UIButton(RadioTexture)
                {
                    Position = new(left + (margin * ((i - setting.LowBound) + 1)), ColumnY),
                    Disabled = !enabled,
                    Tooltip = setting.GetValueTooltip(i),
                    Selected = value == i,
                    ID = i.ToString(), // store the value here
                };
                // set the variable value equal to the current RadioButton value on click
                radio.OnButtonClick += (elem) =>
                {
                    int valueToApply = int.Parse(elem.ID);
                    setting.SetValue(SettingsObjectInstance, valueToApply);
                    OnSettingsChanged(ToggleSetting);
                };
                Add(radio);
            }
            var label = new UILabel()
            {
                Position = new Vector2(labelLeft, ColumnY),
                Caption = Caption,
                Tooltip = setting.GetAboutInfo()
            };
            Add(label);
        }

        private void CreateRadios(UIIntPropOptionDescription ToggleSetting, int ColumnX, int ColumnY)
        {
            int left = 200 + ColumnX, margin = 60, labelLeft = ColumnX;

            // get current setting state using reflection
            UIIntPropOptionDescription setting = ToggleSetting;
            int value = (int)setting.GetValue(SettingsObjectInstance);

            //get title
            string Caption = ToggleSetting.GetTitle();

            //RADIOs
            for (int i = setting.LowBound; i <= setting.Highbound; i++)
            {
                var radio = new UIButton(RadioTexture)
                {
                    Position = new(left + (margin * ((i - setting.LowBound) + 1)), ColumnY),
                    Tooltip = setting.GetValueTooltip(i),
                    ID = i.ToString(), // store the value here
                    Selected = value == i
                };
                // set the variable value equal to the current RadioButton value on click
                radio.OnButtonClick += (elem) =>
                {
                    int valueToApply = int.Parse(elem.ID);
                    setting.SetValue(SettingsObjectInstance, valueToApply);
                    OnSettingsChanged(ToggleSetting);
                };
                Add(radio);
            }
            var label = new UILabel()
            {
                Position = new Vector2(labelLeft, ColumnY),
                Caption = Caption,
                Tooltip = setting.GetAboutInfo()
            };
            Add(label);
        }

        /// <summary>
        /// When any settings values are changed, this method is called to refresh the UI, but can be overridden to allow additional functionality
        /// </summary>
        /// <param name="ChangedSettings">List of settings that have changed since the last time this was called.</param>
        protected virtual void OnSettingsChanged(params UIOptionDescriprion[] ChangedSettings)
        {
            if (SettingsObjectInstance is not GlobalSettings settings) return;

            for (int i = 0; i < ChangedSettings.Length; i++)
                OnSettingVerifying(ChangedSettings[i]); // show any warnings now.

            //save settings to file
            settings.Save();

            //set tex compression
            FSOEnvironment.TexCompress = (settings.TexCompression & 1) > 0;

            // Graphics settings are mirrored onto the live renderer here rather than at the
            // next launch. The mapping is GraphicsOptions.Apply's, shared with the in-game
            // settings strip and the startup path so the three cannot drift; it mutates
            // WorldConfig.Current in place rather than replacing it, because Mode (the
            // 2D/3D/hybrid choice) and the render offsets live on it and a fresh one would
            // silently drop the player back to the default graphics mode.
            GraphicsOptions.Apply(WorldConfig.Current, GraphicsPreferences.From(settings));

            // a TS1 lot is reached through the game screen's lot control, not through the VM's
            // world context (which is the TSO shape this used to assume).
            var game = GameFacade.Screens.CurrentUIScreen as TS1GameScreen;
            game?.LotControl?.World?.ChangedWorldConfig(GameFacade.GraphicsDevice);

            CreateSettings();
        }

        /// <summary>
        /// Before a setting is applied in-game, this method can be used to show the user information about the setting they changed,
        /// like a potential side-effect or recommendation to restart.
        /// </summary>
        /// <param name="VerifyOption">The option being verified.</param>
        protected virtual void OnSettingVerifying(UIOptionDescriprion VerifyOption)
        {

        }
    }

    /// <summary>
    /// Localized text for the Options menu, with an English fallback.
    ///
    /// <para>
    /// <see cref="FSO.Client.GameContent.ContentStrings.GetString(string, string)"/> returns
    /// the literal "***MISSING***" when the table exists but the id does not, and an empty
    /// string when the table itself is missing. Neither is something to show a player, and
    /// the Options pages are built out of ids that came from another build of the game, so
    /// every lookup here falls back to a real string.
    /// </para>
    ///
    /// <para>
    /// This is the lookup for the strings TS1 <em>had</em>: a setting's row caption, its
    /// tooltip, the tab titles, the Low/Medium/High radio words. It reads TS1's own table
    /// 145, which came with the player's copy of the game and is translated by whoever shipped
    /// it. A string Simitone invented - the column headers, the toggle switch's tooltip, the
    /// language page's own title - has no id in there at all, and goes through
    /// <see cref="Simitone.Client.Utils.SimitoneStrings"/> instead, against Simitone's own
    /// table. Every one of them carries the English as its second argument either way, so a
    /// page built with neither table available says what it always said.
    /// </para>
    /// </summary>
    internal static class OptionsStrings
    {
        /// <summary>
        /// Looks up a string in the Options table, returning <paramref name="Fallback"/>
        /// when the lookup produced nothing usable.
        ///
        /// <para>
        /// A null or empty <paramref name="id"/> means "there is no id for this" rather than
        /// "look up an empty key", and falls straight through to the fallback. The table lookup
        /// itself throws on a null key, and a setting Simitone added has no id to point at -
        /// that is the case the fallback strings exist for.
        /// </para>
        /// </summary>
        internal static string Get(string id, string Fallback = null)
        {
            if (string.IsNullOrEmpty(id)) return Fallback;
            var value = GameFacade.Strings?.GetString(UIOptionsSubpanelBase.OptionsTable, id);
            if (string.IsNullOrEmpty(value) || value == "***MISSING***") return Fallback;
            return value;
        }
    }
}
