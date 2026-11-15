/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client.Neighbourhood;
using System;
using System.Collections.Generic;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// The stored shape of the connected-neighbourhood graphics options, as properties.
    ///
    /// <remarks>
    /// The third instance of this trick, for the reason
    /// <see cref="SunCycleOption"/> and <see cref="NeighbourhoodViewOption"/> give: the Options
    /// page's reflection is built around <c>Type.GetProperty</c>, so a setting with no property
    /// has no name to be found by, and a row whose name does not match what
    /// <c>OnSettingsChanged</c> matches on would save nothing and throw nothing. No instance of
    /// this is ever created; the three rows' reads and writes go through
    /// <see cref="NeighbourhoodWorldRow"/>, <see cref="NeighbourhoodWorldCapabilityRow"/> and
    /// <see cref="NeighbourhoodWorldFlags"/>.
    ///
    /// <para>
    /// One type carries all three names rather than a type each, because the third property was
    /// added to an existing pair and a second near-identical class would be a second place to
    /// forget. Three properties, three rows, one class - and
    /// <see cref="NeighbourhoodWorldRow.Descriptors"/> is what turns the three into the page's
    /// three descriptors.
    /// </para>
    /// </remarks>
    public sealed class NeighbourhoodWorldOption
    {
        /// <summary>
        /// The master switch, as a bool because that is the only shape a checkbox can draw.
        /// Carried as a name only - the checkbox's value comes from
        /// <see cref="NeighbourhoodWorldRow.Enabled"/> and goes back through
        /// <see cref="NeighbourhoodWorldRow.Set(bool)"/>.
        /// </summary>
        public bool ConnectedNeighbourhood { get; set; }

        /// <summary>
        /// B6 alone - <see cref="NeighbourhoodWorldCapabilityRow.SimulateNeighbours"/>. Carried
        /// as a name only, exactly as <see cref="ConnectedNeighbourhood"/> is.
        /// </summary>
        public bool SimulateNeighbours { get; set; }
    }

    /// <summary>
    /// The "Connected Neighbourhood" row on the Graphics page: one checkbox over the five Part B
    /// capabilities that are shippable, and the one place that knows which five they are.
    ///
    /// <para>
    /// <b>Why a master switch at all, when the flags are deliberately separate.</b>
    /// <see cref="NeighbourhoodWorldFlags"/> splits Part B into six flags because §7 is a
    /// dependency graph and each milestone's acceptance test is "with the flag off, the previous
    /// stage is byte-identical" - a build and a test harness need the separate switches. A
    /// <em>player</em> does not: the five that exist are on together or not at all, and asking
    /// someone to open a hand-editable <c>simitone.ini</c> and flip five keys to try the feature
    /// is the same as not offering it. So the flags stay independent - the milestone switches are
    /// untouched - and this row is the human-sized front door onto the subset that ships.
    /// </para>
    ///
    /// <para>
    /// <b>Which five, and why not six.</b> <see cref="MasterFlags"/> is
    /// <see cref="NeighbourhoodWorldFlag.World"/>, <c>NeighbourSubWorlds</c>, <c>Travel</c>,
    /// <c>Services</c> and <c>Persist</c>. <c>Offscreen</c> (B6, the world moving on while the
    /// player is elsewhere) is deliberately absent: it is not shippable, and a checkbox that
    /// turns a capability on for a player who cannot see it is the exact failure the all-off
    /// default exists to prevent. It has a row of its own -
    /// <see cref="NeighbourhoodWorldCapabilityRow.SimulateNeighbours"/> - over exactly one flag,
    /// off by default. It is a row rather than one more name in this one because a player who
    /// wants the world to keep running while they are in a menu should not have to turn on the
    /// five that ship to get it. (B4, walkable streets, was cut in 2026-11 and has no flag.)
    /// </para>
    ///
    /// <para>
    /// <b>Independent, but not independent of the master.</b> The two capability rows do not
    /// consult this one and this one does not consult them, in either direction: unticking
    /// Connected Neighbourhood clears the five and leaves B4 and B6 exactly as they were, and
    /// ticking B6 does not make this row draw as on. The reason is the same one that keeps the
    /// view row out of it - a hidden dependency between rows that write one file is how a
    /// player's choices get lost - and it is why a milestone that switches B4 on by hand still
    /// finds its flag on after somebody unticks the master row. What <em>is</em> true is that
    /// the two are inert without it: B4 and B6 are B1's and B3's children, so a capability that
    /// needs a world to live in has nothing to act on. Both rows' tooltips say so, and neither is
    /// greyed out - see the row's remarks for why the page has no way to grey a row out anyway.
    /// </para>
    ///
    /// <para>
    /// <b>What the checkbox shows.</b> <see cref="Enabled"/> is true only when every flag in
    /// <see cref="MasterFlags"/> is on, so a partially-on install - five keys somebody edited by
    /// hand, or a milestone that has switched one of them - reads as <em>off</em>. That is
    /// deliberate, because the alternative is a checkbox whose drawn state depends on which of
    /// six unrelated ini keys happens to be set, and a half-on world is not a world this row
    /// has a truthful caption for. The asymmetry that follows is also deliberate: ticking the
    /// box <em>completes</em> the set, and unticking it <em>clears</em> all five rather than only
    /// the ones this row turned on. Off is the state every capability starts and must return to
    /// between milestones, so clearing completely is what "off" has to mean.
    /// </para>
    ///
    /// <para>
    /// <b>It does not consult the Neighbourhood View row, on purpose.</b> Part B's
    /// <see cref="NeighbourhoodWorldFlag.World"/> flag and the
    /// <see cref="NeighbourhoodView"/> choice are two different things: the view decides what
    /// the neighbourhood <em>screen</em> draws, and the flag decides whether the connected world
    /// runs underneath everything. With the view on Off or 2D this row is <b>inert</b> - it is
    /// still clickable, it still writes its five keys, and nothing consults the view on its way -
    /// because the alternative is a hidden dependency between two rows that both write to the
    /// same file, where putting the view back to 3D would silently resurrect a Part B set the
    /// player had switched off. The tooltip says so instead, which is the only way a player can
    /// be told about it. It is not greyed out, and the row is never force-disabled: the page
    /// builds its controls from a flat descriptor list and has no notion of a row being
    /// unavailable, and a checkbox that draws as on while doing nothing is worse than one that
    /// works.
    /// </para>
    ///
    /// <para>
    /// <b>Where it is stored.</b> The five keys are
    /// <see cref="NeighbourhoodWorldFlags"/>' own, in the same <c>simitone.ini</c> the Dynamic
    /// Sun row and the Neighbourhood View row use, written through the same single read-merge-write
    /// writer - so ticking this box cannot cost the player their sun cycle or their map view. The
    /// row's write delegate records in memory only and <see cref="Apply"/> writes the keys down,
    /// which is the same split <see cref="NeighbourhoodViewRow"/> uses and for the same reason: a
    /// reflected control cannot write a key that is not a property of the settings object.
    /// </para>
    /// </summary>
    public static class NeighbourhoodWorldRow
    {
        /// <summary>
        /// The name the row is found by - the attached property name the change notification
        /// matches, and the key it occupies on the Graphics page. Nothing is stored under it.
        /// </summary>
        public const string Key = "ConnectedNeighbourhood";

        /// <summary>
        /// The five capabilities this row owns, in the order §7 lists them: B1, B2, B3, B5, B7.
        /// B6 (<c>Offscreen</c>) is not here and must not be added: it has a row of its own in
        /// <see cref="NeighbourhoodWorldCapabilityRow"/>, and folding it into this set would make
        /// one checkbox answer two questions at once.
        /// </summary>
        public static readonly IReadOnlyList<NeighbourhoodWorldFlag> MasterFlags = new[]
        {
            NeighbourhoodWorldFlag.World,
            NeighbourhoodWorldFlag.NeighbourSubWorlds,
            NeighbourhoodWorldFlag.Travel,
            NeighbourhoodWorldFlag.Services,
            NeighbourhoodWorldFlag.Persist
        };

        /// <summary>How many flags the master set is, stated once so a test need not count.</summary>
        public const int MasterFlagCount = 5;

        /// <summary>
        /// The row caption, in Simitone's own string table (<c>_f900_simitone.cst</c>).
        ///
        /// <remarks>
        /// 187-189, unused at the time of writing and the next block after the Neighbourhood View
        /// row's 181-183 and the Surrounding Lots row's 184-186. TS1 never had a connected
        /// neighbourhood, so table 145 has no entry for it and a guessed id there would put an
        /// unrelated setting's words on a Simitone control; the literals below are the floor
        /// under both tables.
        /// </remarks>
        public const string SimitoneTitleId = "187";

        /// <summary>See <see cref="SimitoneTitleId"/>.</summary>
        public const string SimitoneAboutTitleId = "188";

        /// <summary>See <see cref="SimitoneTitleId"/>.</summary>
        public const string SimitoneAboutDescriptionId = "189";

        /// <summary>
        /// Whether the checkbox is on: every flag in <see cref="MasterFlags"/> is on.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                foreach (var flag in MasterFlags)
                {
                    if (!NeighbourhoodWorldFlags.IsOn(flag)) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Whether an Options descriptor is one of this feature's rows - the master row or either
        /// of the two <see cref="NeighbourhoodWorldCapabilityRow"/> rows. Takes the property name
        /// rather than the descriptor, for the reason
        /// <see cref="NeighbourhoodViewRow.Describes(string)"/> gives: the descriptor list is
        /// rebuilt every time the page lays itself out, so there is no stable object to compare
        /// against and a silent mismatch would not throw.
        ///
        /// <para>
        /// <b>All three rows, not one, and deliberately.</b> This is the predicate the Graphics
        /// page's change notification matches on before it calls <see cref="Apply"/>, and it is
        /// the only hook that page has for a setting stored in <c>simitone.ini</c>. Matching the
        /// master row's name alone would leave a tick on either capability row recorded in memory
        /// and never written down - a checkbox that appears to work and resets at the next
        /// launch, which is the exact failure the two-rows-of-one-flag test exists to catch. One
        /// predicate over the feature's own three names, and one <see cref="Apply"/> that writes
        /// all six flags, is what keeps the page's write path a one-liner.
        /// </para>
        /// </summary>
        /// <param name="attachedPropertyName">The descriptor's attached property name.</param>
        public static bool Describes(string attachedPropertyName)
        {
            if (string.Equals(attachedPropertyName, Key, StringComparison.Ordinal)) return true;
            foreach (var row in NeighbourhoodWorldCapabilityRow.All)
            {
                if (string.Equals(attachedPropertyName, row.Key, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Whether an Options descriptor is one of this feature's three rows.</summary>
        /// <param name="descriptor">The descriptor, which may be <c>null</c>.</param>
        public static bool Describes(UIOptionsSubpanelBase.UIOptionDescriprion descriptor)
        {
            return descriptor?.AttachedProperty?.Name != null && Describes(descriptor.AttachedProperty.Name);
        }

        /// <summary>
        /// Builds the row, reading and writing through <see cref="NeighbourhoodWorldFlags"/>.
        /// Public and separate from the page's descriptor list for the reason
        /// <see cref="NeighbourhoodViewRow.Descriptor"/> is: constructing the page needs a
        /// graphics device and the content tree, and a row whose property has been renamed is
        /// exactly the mistake that would otherwise only show up as a checkbox that does nothing.
        /// </summary>
        public static UIOptionsSubpanelBase.UIOptionDescriprion Descriptor()
        {
            return new UIOptionsSubpanelBase.UIExternalBoolPropOptionDescription(
                typeof(NeighbourhoodWorldOption).GetProperty(Key),
                () => Enabled,
                value => Set((bool)value))
            {
                //No TS1 ids, deliberately: table 145 covers the settings TS1 itself had, and a
                //connected neighbourhood is not one of them. Simitone's own table is used, which
                //is what the Neighbourhood View and Surrounding Lots rows already do.
                LocalizationInfo = new UIOptionsSubpanelBase.UIOptionLocalizationDescription(
                    null, null, null, "Connected Neighbourhood", "Connected Neighbourhood",
                    DescribeEffect())
                    .WithSimitoneIds(SimitoneTitleId, SimitoneAboutTitleId, SimitoneAboutDescriptionId)
            };
        }

        /// <summary>
        /// Records the master switch across all five flags, without writing to disk.
        ///
        /// <remarks>
        /// Saving is the caller's decision for the same reason it is for
        /// <see cref="SunCycleSetting.SetEnabled"/>: the Options page saves every changed row in
        /// one go, and five keys is one read-merge-write each at the most.
        /// </remarks>
        /// <param name="on">Whether the connected neighbourhood is on.</param>
        public static void Set(bool on)
        {
            foreach (var flag in MasterFlags)
            {
                NeighbourhoodWorldFlags.Set(flag, on, save: false);
            }
        }

        /// <summary>
        /// Writes the feature's six keys down, from the values already recorded in memory.
        ///
        /// <remarks>
        /// Ordered after the base class's own save on purpose, and re-reads each flag rather than
        /// re-applying one value: by the time the page gets here the checkbox has recorded the
        /// choice, and what is left is the one step the base's save cannot do because none of
        /// these keys is a <c>GlobalSettings</c> property. Re-reading is also what makes a
        /// partially-on install survive a click on some other row - this writes what is there,
        /// rather than an assumption about what ought to be.
        ///
        /// <para>
        /// All six, not the master's five, and that is not an expansion of scope: the page
        /// reaches this through <see cref="Describes(UIOptionsSubpanelBase.UIOptionDescriprion)"/>,
        /// which now matches either capability row too, so a write that covered only the five
        /// would drop the sixth key of whichever capability row was just ticked. Nothing is
        /// overwritten: every flag is written with the value it already has in memory, so a tick
        /// on one row cannot move another row's key off disk.
        /// </para>
        /// </remarks>
        public static void Apply()
        {
            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                NeighbourhoodWorldFlags.Set(flag, NeighbourhoodWorldFlags.IsOn(flag), save: true);
            }
        }

        /// <summary>
        /// The three connected-neighbourhood rows, in the order the page shows them: the master
        /// switch first, then the two capabilities that are deliberately not in it.
        ///
        /// <remarks>
        /// <b>Wiring this into the Graphics page is a one-line follow-up in
        /// <c>UIOptionsSubpanels.GraphicsDescriptors</c>:</b> replace the single
        /// <c>NeighbourhoodWorldRow.Descriptor(),</c> entry in that collection expression with a
        /// spread of this, <c>..NeighbourhoodWorldRow.Descriptors(),</c>. Nothing else changes -
        /// the change notification already matches all three names through
        /// <see cref="Describes(string)"/>, and <see cref="Apply"/> already writes all six keys.
        ///
        /// <para>
        /// Exposed here rather than only inside the page's list for the reason
        /// <see cref="Descriptor"/> is: the page cannot be built in a test, and a row whose
        /// property has been renamed or retyped is exactly the mistake that would otherwise only
        /// show up as a checkbox that does nothing.
        /// </para>
        /// </remarks>
        public static IEnumerable<UIOptionsSubpanelBase.UIOptionDescriprion> Descriptors()
        {
            yield return Descriptor();
            foreach (var row in NeighbourhoodWorldCapabilityRow.All)
            {
                yield return row.Descriptor();
            }
        }

        /// <summary>What the row does, for its tooltip.</summary>
        public static string DescribeEffect()
        {
            return "Turns on the connected neighbourhood: the lots around yours are real, you can"
                + " travel between them through the world, build/buy and jobs go through it, and"
                + " what the world knows is written down. It is off unless you turn it on, and it"
                + " has no effect at all until Neighbourhood View is set to 3D - with the view on"
                + " Off or 2D this switch is remembered but nothing is drawn with it, and the two"
                + " settings are kept apart so that changing one never changes the other."
                + " This is the switch for the part of the world that ships. Simulating your"
                + " neighbours while you are elsewhere is a separate row below, and it is not"
                + " turned on by this one.";
        }
    }

    /// <summary>
    /// A single Part B capability that has a row of its own on the Graphics page, over exactly
    /// one of <see cref="NeighbourhoodWorldFlags"/>'s six flags.
    ///
    /// <para>
    /// <b>Why a class per flag rather than two more booleans on the master row.</b>
    /// <see cref="NeighbourhoodWorldRow"/>'s checkbox is a question about the feature - "is the
    /// connected neighbourhood on" - and its answer is a conjunction over five flags, so there is
    /// nowhere in it to record one flag's state. B6 (<c>Offscreen</c>) is a question about a
    /// capability, and a different question from the master. Making them rows rather than ini keys a player has to hand-edit is the
    /// whole point of this class: a setting with no row does not exist as far as a player is
    /// concerned.
    /// </para>
    ///
    /// <para>
    /// <b>Both default off, and the flag's own default is what that means.</b>
    /// <see cref="NeighbourhoodWorldFlags.Default"/> is <c>false</c> for every flag, so a fresh
    /// install - or one whose key is missing, empty or unrecognised - draws both rows unticked.
    /// There is no value here that is not already the flags' own, which is the point: the row
    /// has no state of its own to get out of step with the file.
    /// </para>
    ///
    /// <para>
    /// <b>They do not consult the master row, and the master row does not consult them.</b>
    /// Ticking or unticking Connected Neighbourhood leaves B4 and B6 exactly as they were, in
    /// memory and on disk, so a milestone that has switched one of them on is not silently reset
    /// by somebody tidying up the Graphics page; and turning one of these on does not make the
    /// master draw as on, because the master is a conjunction over <em>its</em> five flags and
    /// these two are not among them. The two are still <b>inert</b> without it - B4 and B6 are
    /// B1's and B3's children in §7's graph, and a capability that needs a world has nothing to
    /// act on until there is one - so each tooltip says so. Neither row is greyed out or forced
    /// off, for the reason <see cref="NeighbourhoodWorldRow"/>'s remarks give: the page builds
    /// its controls from a flat descriptor list and has no notion of a row being unavailable, and
    /// a checkbox that draws as on while doing nothing is worse than one that works.
    /// </para>
    ///
    /// <para>
    /// <b>Storage and persistence are the flags', not the rows'.</b> Each row writes one
    /// <c>simitone.ini</c> key through <see cref="NeighbourhoodWorldFlags"/>, so the same
    /// read-merge-write writer the master row and the other two settings on this page use is what
    /// keeps a tick on one of these from resetting the player's sun cycle or map view. The page
    /// writes the key down through <see cref="NeighbourhoodWorldRow.Apply"/>, which
    /// <see cref="NeighbourhoodWorldRow.Describes(string)"/> reaches for all three rows'
    /// names - see that method's remarks for why the predicate covers the whole feature rather
    /// than the master row alone.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldCapabilityRow
    {
        /// <summary>
        /// B6: the world moves on while the player is elsewhere. Off, world time frozen off-lot.
        ///
        /// <remarks>
        /// The caption is about the player's own experience rather than about a scheduler,
        /// because "off-screen simulation" is §7's name for it and not a sentence anybody would
        /// want to read in a tooltip.
        /// </remarks>
        /// </summary>
        public static readonly NeighbourhoodWorldCapabilityRow SimulateNeighbours =
            new NeighbourhoodWorldCapabilityRow(
                "SimulateNeighbours", NeighbourhoodWorldFlag.Offscreen,
                "Simulate Neighbours",
                "Turns on the world going on while you are somewhere else: the families around"
                + " you keep living their days, and the time in the world passes whether or not"
                + " you are looking at it. It is off unless you turn it on, and it does nothing at"
                + " all unless Connected Neighbourhood is on as well - this switch is one part of"
                + " that feature rather than a feature of its own, and it is kept separate from it"
                + " so that turning the rest of the world off never quietly turns this off too.",
                "940", "941", "942");

        private NeighbourhoodWorldCapabilityRow(string key, NeighbourhoodWorldFlag flag, string title,
            string aboutDescription, string simitoneTitleId, string simitoneAboutTitleId,
            string simitoneAboutDescriptionId)
        {
            Key = key;
            Flag = flag;
            Title = title;
            AboutDescription = aboutDescription;
            SimitoneTitleId = simitoneTitleId;
            SimitoneAboutTitleId = simitoneAboutTitleId;
            SimitoneAboutDescriptionId = simitoneAboutDescriptionId;
        }

        /// <summary>The capability rows, in the order the page shows them.</summary>
        /// <remarks>
        /// A property rather than a field so the list cannot be built before the rows above it
        /// have been constructed - static initialisers run in declaration order, and a
        /// <c>static readonly</c> array declared first would capture nulls.
        /// </remarks>
        public static IEnumerable<NeighbourhoodWorldCapabilityRow> All
        {
            get { return new[] { SimulateNeighbours }; }
        }

        /// <summary>
        /// The name the row is found by - the attached property name the change notification
        /// matches, and the key it occupies on the Graphics page. Nothing is stored under it.
        /// </summary>
        public string Key { get; }

        /// <summary>The one flag this row owns, and the only one it may ever touch.</summary>
        public NeighbourhoodWorldFlag Flag { get; }

        /// <summary>The row caption in English, and the floor under both string tables.</summary>
        public string Title { get; }

        /// <summary>The tooltip's body, which is the only half that is not the caption again.</summary>
        public string AboutDescription { get; }

        /// <summary>
        /// The row caption in Simitone's own string table (<c>_f900_simitone.cst</c>).
        ///
        /// <remarks>
        /// 940-945, the next free run of six in the Graphics-page neighbourhood rather than a
        /// guess at table 145: 180-189 are the Graphics page's own block and 187-189 are the
        /// master row's, 190-194 are the Free Will quick toggle's, and 200 is the object query
        /// panel's. TS1 never had a connected neighbourhood, so there is nothing in table 145 to
        /// point at and a guessed id there would put an unrelated setting's words on a Simitone
        /// control. The literals above are the floor under both tables, exactly as the master
        /// row's are.
        /// </remarks>
        public string SimitoneTitleId { get; }

        /// <summary>See <see cref="SimitoneTitleId"/>.</summary>
        public string SimitoneAboutTitleId { get; }

        /// <summary>See <see cref="SimitoneTitleId"/>.</summary>
        public string SimitoneAboutDescriptionId { get; }

        /// <summary>
        /// Whether the checkbox is on: the one flag is on. Unlike the master row there is no
        /// partial state to reconcile - one flag, one value, and a checkbox that draws exactly
        /// what the file says.
        /// </summary>
        public bool Enabled
        {
            get { return NeighbourhoodWorldFlags.IsOn(Flag); }
        }

        /// <summary>
        /// Whether an Options descriptor is this row. Takes the property name rather than the
        /// descriptor, for the reason <see cref="NeighbourhoodWorldRow.Describes(string)"/> gives.
        /// </summary>
        /// <param name="attachedPropertyName">The descriptor's attached property name.</param>
        public bool Describes(string attachedPropertyName)
        {
            return string.Equals(attachedPropertyName, Key, StringComparison.Ordinal);
        }

        /// <summary>Whether an Options descriptor is this row.</summary>
        /// <param name="descriptor">The descriptor, which may be <c>null</c>.</param>
        public bool Describes(UIOptionsSubpanelBase.UIOptionDescriprion descriptor)
        {
            return descriptor?.AttachedProperty?.Name == Key;
        }

        /// <summary>
        /// Builds the row, reading and writing through <see cref="NeighbourhoodWorldFlags"/>.
        /// Public and separate from the page's descriptor list for the reason
        /// <see cref="NeighbourhoodWorldRow.Descriptor"/> is: the page cannot be built in a test,
        /// and a row whose property has been renamed is exactly the mistake that would otherwise
        /// only show up as a checkbox that does nothing.
        /// </summary>
        public UIOptionsSubpanelBase.UIOptionDescriprion Descriptor()
        {
            return new UIOptionsSubpanelBase.UIExternalBoolPropOptionDescription(
                typeof(NeighbourhoodWorldOption).GetProperty(Key),
                () => Enabled,
                value => Set((bool)value))
            {
                //No TS1 ids, for the reason the master row's are null: table 145 covers the
                //settings TS1 itself had, and a connected neighbourhood is not one of them.
                //Simitone's own table is used, which is what the Neighbourhood View, Surrounding
                //Lots and Connected Neighbourhood rows already do.
                LocalizationInfo = new UIOptionsSubpanelBase.UIOptionLocalizationDescription(
                    null, null, null, Title, Title, AboutDescription)
                    .WithSimitoneIds(SimitoneTitleId, SimitoneAboutTitleId, SimitoneAboutDescriptionId)
            };
        }

        /// <summary>
        /// Records the flag, without writing to disk. Only ever this row's own flag - there is no
        /// loop and no second member to reach for, which is what makes "ticking this row leaves
        /// the other six alone" a property of the code rather than of a test.
        /// </summary>
        /// <param name="on">Whether the capability is on.</param>
        public void Set(bool on)
        {
            NeighbourhoodWorldFlags.Set(Flag, on, save: false);
        }

        /// <summary>
        /// Writes the flag's key down, from the value already recorded in memory. Re-read rather
        /// than re-apply, for the reason <see cref="NeighbourhoodWorldRow.Apply"/> gives.
        /// </summary>
        public void Apply()
        {
            NeighbourhoodWorldFlags.Set(Flag, NeighbourhoodWorldFlags.IsOn(Flag), save: true);
        }
    }
}
