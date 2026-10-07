/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using Simitone.Client.Neighbourhood;
using System;
using System.Collections.Generic;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// The stored shape of the "Neighbourhood View" graphics option, as a property.
    ///
    /// <remarks>
    /// Exists only so the Options page's descriptor has a
    /// <see cref="System.Reflection.PropertyInfo"/> to name the setting with - the page's
    /// reflection is built around <c>Type.GetProperty</c>, and a setting with no property has no
    /// name to be found by. No instance of this is ever created and no value is ever read from or
    /// written to one; the row's reads and writes go through
    /// <see cref="NeighbourhoodViewSetting"/> and
    /// <see cref="NeighbourhoodViewRow"/>. See
    /// <c>UIOptionsSubpanelBase.UIExternalIntPropOptionDescription</c>.
    /// </remarks>
    public sealed class NeighbourhoodViewOption
    {
        /// <summary>
        /// The choice, as an int rather than as <see cref="NeighbourhoodView"/>.
        ///
        /// <remarks>
        /// Int because that is what the reflected radio group reads: it unboxes the descriptor's
        /// value straight to an <c>int</c>, and a boxed enum would throw there rather than draw. The
        /// enum and the int have the same values, and the row never lets the int out - it goes
        /// through <see cref="NeighbourhoodViewSetting.Resolve(NeighbourhoodView)"/> on the way in
        /// and <see cref="NeighbourhoodViewSetting.TokenFor"/> on the way out.
        /// </remarks>
        /// </summary>
        public int NeighbourhoodView { get; set; }
    }

    /// <summary>
    /// The "Neighbourhood View" row on the Graphics page, and the one place that knows how the
    /// three-way choice is stored, read back and captioned.
    ///
    /// <para>
    /// <b>Why a row and not a value in <c>GlobalSettings</c>.</b> The same reasoning
    /// <see cref="SunCycleSetting"/> spells out at length: <c>FSO.UI.GlobalSettings</c> is FreeSO's
    /// to edit, and every existing key either already means something else, belongs to TSO's city
    /// renderer, or is an engine flag this is not. So the choice lives in a second key in
    /// Simitone's own sidecar ini, and this row is the view over it.
    /// </para>
    ///
    /// <para>
    /// <b>What the row actually changes.</b> The setting reaches the renderer through
    /// <c>GraphicsPreferences.From</c>, which every graphics option in the game goes through, and
    /// from there into <c>UINeighborhoodSelectionPanel</c>, which is the only thing that reads it.
    /// The row itself is the one that writes it down - <see cref="Apply"/> - because a reflected
    /// control cannot write a key that is not a property of the settings object.
    /// </para>
    /// </summary>
    public static class NeighbourhoodViewRow
    {
        /// <summary>The lowest radio index, which is <see cref="NeighbourhoodView.Off"/>.</summary>
        public const int MinValue = 0;

        /// <summary>The highest radio index, which is <see cref="NeighbourhoodView.World3D"/>.</summary>
        public const int MaxValue = 2;

        /// <summary>
        /// Whether an Options descriptor is the row for this setting. Takes the property name rather
        /// than the descriptor, because the descriptor list is rebuilt every time the page lays
        /// itself out: there is no stable object to compare against, and a silent mismatch would
        /// not throw, it would leave a radio group that saves and does nothing.
        /// </summary>
        /// <param name="attachedPropertyName">The descriptor's attached property name.</param>
        public static bool Describes(string attachedPropertyName)
        {
            return string.Equals(attachedPropertyName, NeighbourhoodViewSetting.Key,
                StringComparison.Ordinal);
        }

        /// <summary>Whether an Options descriptor is the row for this setting.</summary>
        /// <param name="descriptor">The descriptor, which may be <c>null</c>.</param>
        public static bool Describes(UIOptionsSubpanelBase.UIOptionDescriprion descriptor)
        {
            return descriptor?.AttachedProperty?.Name == NeighbourhoodViewSetting.Key;
        }

        /// <summary>
        /// Builds the row, reading and writing through
        /// <see cref="NeighbourhoodViewSetting"/>. Public and separate from the page's descriptor
        /// list so the row can be built and inspected in a test - constructing the page itself needs
        /// a graphics device and the content tree, and a row whose property has been renamed is
        /// exactly the mistake that would otherwise only show up as a control that does nothing.
        /// </summary>
        public static UIOptionsSubpanelBase.UIOptionDescriprion Descriptor()
        {
            return new UIOptionsSubpanelBase.UIExternalIntPropOptionDescription(
                typeof(NeighbourhoodViewOption).GetProperty(NeighbourhoodViewSetting.Key),
                MinValue, MaxValue,
                () => (int)NeighbourhoodViewSetting.Current,
                value => NeighbourhoodViewSetting.Set((NeighbourhoodView)value))
            {
                LocalizationInfo = new UIOptionsSubpanelBase.UIOptionLocalizationDescription(
                    null, null, null, "Neighbourhood View", "Neighbourhood View", DescribeEffect())
                    .WithSimitoneIds(SimitoneTitleId, SimitoneAboutTitleId, SimitoneAboutDescriptionId),
                //Off / 2D / 3D. The order is the enum's and it is not moving - an existing
                //simitone.ini stores a token and a hand-edited one may hold a number - so 3D being
                //the default does not make it the first radio. It is the third, and the radio that
                //starts selected.
                ValueTooltips = new[]
                {
                    NeighbourhoodViewSetting.Describe(NeighbourhoodView.Off),
                    NeighbourhoodViewSetting.Describe(NeighbourhoodView.Bitmap2D),
                    NeighbourhoodViewSetting.Describe(NeighbourhoodView.World3D)
                }
            };
        }

        /// <summary>
        /// The caption and the description ids in Simitone's own string table, tried when TS1's
        /// table has no entry for them.
        ///
        /// <remarks>
        /// TS1 never had a 3D neighbourhood, so there is no id in table 145 to point at, and a
        /// guessed one would put an unrelated game's words on a Simitone control. The Dynamic Sun
        /// and Water Reflections rows already established the pattern.
        /// </remarks>
        public const string SimitoneTitleId = "181";

        /// <summary>See <see cref="SimitoneTitleId"/>.</summary>
        public const string SimitoneAboutTitleId = "182";

        /// <summary>See <see cref="SimitoneTitleId"/>.</summary>
        public const string SimitoneAboutDescriptionId = "183";

        /// <summary>
        /// Writes the row's value down, once the page has saved everything else.
        ///
        /// <remarks>
        /// Ordered after the base class's own save on purpose. The base saves the
        /// <c>GlobalSettings</c> object, which is all a reflected control can do, and re-applies the
        /// graphics snapshot; the choice is already in <see cref="NeighbourhoodViewSetting"/> by
        /// then, and all that is left is to write it to the sidecar ini - the one step the base's
        /// save cannot do because the key is not in that object. Mirrors
        /// <see cref="SunCycleSetting.SetEnabled"/>'s split.
        /// </remarks>
        public static void Apply()
        {
            NeighbourhoodViewSetting.SetAndSave(NeighbourhoodViewSetting.Current);
        }

        /// <summary>
        /// The row's tooltip. Extends <see cref="NeighbourhoodViewSetting.DescribeEffect"/> with the
        /// two things a player has actually to decide: that it is not what draws the world around a
        /// lot, and what it costs.
        /// </summary>
        public static string DescribeEffect()
        {
            return NeighbourhoodViewSetting.DescribeEffect()
                + " This is the view of the neighbourhood map itself. What you see around a lot"
                + " while you are standing in it is the Surrounding Lots setting.";
        }
    }

    /// <summary>
    /// The "Surrounding Lots" row on the Graphics page, which is the in-lot half of the same
    /// feature.
    ///
    /// <para>
    /// <b>Why it belongs next to <see cref="NeighbourhoodViewRow"/> and why it is not the same
    /// setting.</b> §8 says to add a <c>NeighbourhoodView</c> (Off / 2D / 3D) and <em>reuse</em>
    /// <c>SurroundingLotMode</c> for in-lot surroundings, and the two answer different questions.
    /// <c>NeighbourhoodView</c> decides whether the neighbourhood screen is a painted map or a
    /// generated world. <c>SurroundingLotMode</c> decides how much of the world is drawn
    /// <em>around the lot you are standing in</em>, and it already exists as a
    /// <c>GlobalSettings</c> property the renderer reads. Collapsing them would take a choice away
    /// that players of the city renderer already have, so the pair is kept and this row is what
    /// makes the second one reachable from inside the running game.
    /// </para>
    /// <para>
    /// <b>What the three steps mean.</b> 0 is off - the lot is drawn as it always was, with the
    /// hand-painted backdrop - 1 and 2 are the two detail levels TSO's city renderer already
    /// distinguishes. Names are TSO's own ("Low" / "High" terrain detail) because the setting is
    /// TSO's; the numeric values are the contract with <c>WorldConfig.SurroundingLots</c>, which
    /// reads them.
    /// </para>
    ///
    /// <para>
    /// <b>Why the default is the lowest on step.</b> M4's in-lot surroundings are gated on
    /// <c>SurroundingLotMode &gt; 0</c>, so the Off step is the one that would leave a lot standing
    /// in a vacuum with the 3D neighbourhood on - and the simitone.ini default has to be
    /// deliberately <em>on</em> for a lot to show the surrounding world out of the box. Low is that
    /// step: it is the cheapest of the two on values, and the engine does not treat 1 and 2
    /// differently (the city terrain reads them both as <c>houseRange = mode == 0 ? 0 : 1</c>, and
    /// <c>VMLotTerrainRestoreTools</c> returns only on <c>mode == 0</c>), so nothing TSO does is
    /// changed by the default. An existing <c>config.ini</c> keeps whatever it already said.
    /// </para>
    /// </summary>
    public static class SurroundingLotModeRow
    {
        /// <summary>Off: the lot is drawn as it always was.</summary>
        public const int Off = 0;

        /// <summary>Low detail: the immediate surroundings only.</summary>
        public const int Low = 1;

        /// <summary>High detail: the full surrounding terrain.</summary>
        public const int High = 2;

        /// <summary>
        /// The value a player who has changed nothing gets: the lowest step that is still on.
        /// </summary>
        public const int Default = Low;

        /// <summary>The lowest and highest values the setting is offered over.</summary>
        public const int MinValue = Off;

        /// <summary>The highest and only upper bound.</summary>
        public const int MaxValue = High;

        /// <summary>Captions for the three radios, in order.</summary>
        public static readonly string[] Captions = { "Off", "Low", "High" };

        /// <summary>Whether an Options descriptor is the row for this setting.</summary>
        /// <param name="attachedPropertyName">The descriptor's attached property name.</param>
        public static bool Describes(string attachedPropertyName)
        {
            return string.Equals(attachedPropertyName, "SurroundingLotMode", StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds the row over <c>GlobalSettings.SurroundingLotMode</c>, which is a real property
        /// of the settings object and therefore needs no external storage: the base page's own save
        /// writes it to <c>config.ini</c> and
        /// <c>GraphicsOptions.Apply</c> pushes it into <c>WorldConfig.SurroundingLots</c>.
        /// </summary>
        public static UIOptionsSubpanelBase.UIOptionDescriprion Descriptor()
        {
            return new UIOptionsSubpanelBase.UIIntPropOptionDescription(
                typeof(GlobalSettings).GetProperty("SurroundingLotMode"), MinValue, MaxValue)
            {
                //No TS1 ids, and deliberately. The setting is inherited from TSO's <em>city</em>
                //renderer's options dialog rather than from TS1, so table 145 - the table every
                //other row on this page is read from - has no entry for it. Pointing a row at a
                //guessed id puts an unrelated setting's words on it, which is the exact mistake
                //the sound rows' remarks describe. Simitone's own table is used instead.
                LocalizationInfo = new UIOptionsSubpanelBase.UIOptionLocalizationDescription(
                    null, null, null, "Surrounding Lots", "Surrounding Lots", DescribeEffect())
                    .WithSimitoneIds(SimitoneTitleId, SimitoneAboutTitleId, SimitoneAboutDescriptionId),
                ValueTooltips = Captions
            };
        }

        /// <summary>The row caption, in Simitone's own string table.</summary>
        public const string SimitoneTitleId = "184";

        /// <summary>The tooltip's title, in Simitone's own string table.</summary>
        public const string SimitoneAboutTitleId = "185";

        /// <summary>The tooltip's body, in Simitone's own string table.</summary>
        public const string SimitoneAboutDescriptionId = "186";

        /// <summary>What the setting does, for the row's tooltip.</summary>
        public static string DescribeEffect()
        {
            return "How much of the world is drawn around a lot while you are standing in it."
                + " Off leaves the lot exactly as it has always been drawn. Low and High draw the"
                + " surrounding terrain in more or less detail, which costs frame time on older"
                + " machines. This is not the same setting as Neighbourhood View, which is about"
                + " the neighbourhood map you pick a house from.";
        }

        /// <summary>
        /// The pair of rows, in the order the page shows them.
        ///
        /// <remarks>
        /// Exposed so a test can assert both exist, are the right kind of control and do not
        /// collide on a name, without building a page that needs a graphics device.
        /// </remarks>
        public static IEnumerable<UIOptionsSubpanelBase.UIOptionDescriprion> Descriptors()
        {
            yield return SurroundingLotModeRow.Descriptor();
            yield return NeighbourhoodViewRow.Descriptor();
        }
    }
}
