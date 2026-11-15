/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Common;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The "Connected Neighbourhood" row: the master switch that makes Part B reachable from
    /// inside the running game.
    ///
    /// <para>
    /// <b>What is worth pinning.</b> Three failures, and none of them would throw. A row that is
    /// not on the page is a feature that does not exist. A row whose attached property name
    /// drifts from what <c>OnSettingsChanged</c> matches on is a checkbox that sets the flags in
    /// memory and never writes them down - the whole reason the write half lives in a separate
    /// <c>Apply</c>, and the same quiet failure <c>SunCycleSettingTests</c> guards for the Dynamic
    /// Sun row. And a write that dropped the rest of <c>simitone.ini</c> would silently reset the
    /// player's Dynamic Sun choice and their map view, which is precisely what the sidecar's one
    /// read-merge-write writer exists to prevent.
    /// </para>
    ///
    /// <para>
    /// The last one is the reason this class redirects <c>FSOEnvironment.UserDir</c> and so
    /// belongs in <see cref="NonParallelCollection"/>: the user directory is a process-global, and
    /// the flags cache their resolved values in a static as well. The other half of the class -
    /// the descriptor list - needs no such isolation and is asserted on directly, because
    /// <c>GraphicsDescriptors</c> is public precisely so the page's rows can be checked without a
    /// graphics device.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class NeighbourhoodWorldRowTests : IDisposable
    {
        private readonly string _root;
        private readonly string _originalUserDir;
        private readonly bool _originalSunCycle;
        private readonly NeighbourhoodView _originalView;
        private readonly bool[] _originalFlags;

        public NeighbourhoodWorldRowTests()
        {
            //Captured before the redirect and put back in Dispose: all of these are process-wide,
            //and two of them are somebody else's cached setting.
            _originalUserDir = FSOEnvironment.UserDir;
            _originalSunCycle = SunCycleSetting.Enabled;
            _originalView = NeighbourhoodViewSetting.Current;
            _originalFlags = NeighbourhoodWorldFlags.All
                .Select(NeighbourhoodWorldFlags.IsOn).ToArray();

            _root = Path.Combine(Path.GetTempPath(), "simitone-world-row-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            FSOEnvironment.UserDir = _root;
            NeighbourhoodWorldFlags.Reset();
            NeighbourhoodViewSetting.Set(NeighbourhoodViewSetting.Default);
        }

        public void Dispose()
        {
            FSOEnvironment.UserDir = _originalUserDir;
            SunCycleSetting.SetEnabled(_originalSunCycle);
            NeighbourhoodViewSetting.Set(_originalView);
            var flags = NeighbourhoodWorldFlags.All;
            for (var i = 0; i < flags.Count; i++)
            {
                NeighbourhoodWorldFlags.Set(flags[i], _originalFlags[i], save: false);
            }
            NeighbourhoodWorldFlags.Reset();

            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                //best effort - a leftover temp dir must never fail the suite
            }
            catch (UnauthorizedAccessException)
            {
                //same: a locked handle on Windows must not turn into a test failure
            }
        }

        private string Sidecar => NeighbourhoodWorldFlags.ConfigFilePath(_root);

        private static List<UIOptionsSubpanelBase.UIOptionDescriprion> GraphicsRows() =>
            UIGraphicsOptionsSubpanel.GraphicsDescriptors(null).ToList();

        private static UIOptionsSubpanelBase.UIOptionDescriprion Row(string name) =>
            GraphicsRows().Single(x => x.AttachedProperty?.Name == name);

        /// <summary>
        /// The feature's own three rows, as <see cref="NeighbourhoodWorldRow.Descriptors"/> hands
        /// them back.
        ///
        /// <para>
        /// Deliberately not the page's list. Putting the two capability rows on the Graphics page
        /// is a one-line change to <c>UIGraphicsOptionsSubpanel.GraphicsDescriptors</c> - a spread
        /// of these descriptors in place of the single master row's - and until that edit lands
        /// they exist only here, so a test that looked for them on the page would be testing the
        /// follow-up rather than the rows.
        /// </para>
        /// </summary>
        private static List<UIOptionsSubpanelBase.UIOptionDescriprion> WorldRows() =>
            NeighbourhoodWorldRow.Descriptors().ToList();

        private static UIOptionsSubpanelBase.UIOptionDescriprion WorldRow(string name) =>
            WorldRows().Single(x => x.AttachedProperty?.Name == name);

        private static NeighbourhoodWorldCapabilityRow Simulate =>
            NeighbourhoodWorldCapabilityRow.SimulateNeighbours;

        /// <summary>Puts a hand-written sidecar in place and forgets the flags' cache.</summary>
        private void Seed(params string[] lines)
        {
            File.WriteAllLines(Sidecar, lines.Length == 0
                ? new[] { "# Simitone Settings File. Simitone's own keys; FreeSO's are in config.ini." }
                : lines);
            NeighbourhoodWorldFlags.Reset();
        }

        /// <summary>One stored value, read the way the game reads it.</summary>
        private string Raw(string key) => SunCycleSetting.Ini(Sidecar).Get(key);

        /// <summary>
        /// The whole capability set as the seven booleans it is, so "it left the other six
        /// alone" can be compared as one value instead of six assertions that could each be
        /// wrong in a different way.
        /// </summary>
        private static bool[] FlagSet()
        {
            var all = NeighbourhoodWorldFlags.All;
            var values = new bool[all.Count];
            for (var i = 0; i < all.Count; i++) values[i] = NeighbourhoodWorldFlags.IsOn(all[i]);
            return values;
        }

        /// <summary>Asserts that exactly one capability is on, and names it if none is.</summary>
        private static void AssertOnly(NeighbourhoodWorldFlag on)
        {
            var all = NeighbourhoodWorldFlags.All;
            for (var i = 0; i < all.Count; i++)
            {
                Assert.Equal(all[i] == on, NeighbourhoodWorldFlags.IsOn(all[i]));
            }
        }

        /// <summary>Ticks the checkbox and does the half only the page does: write it down.</summary>
        private void ToggleAndApply(bool on)
        {
            Row(NeighbourhoodWorldRow.Key).SetValue(null, on);
            NeighbourhoodWorldRow.Apply();
        }

        /// <summary>
        /// The same two steps for a capability row: the row's own delegate records the flag, and
        /// <see cref="NeighbourhoodWorldRow.Apply"/> is the page's step that writes it down.
        /// </summary>
        private void ToggleAndApply(NeighbourhoodWorldCapabilityRow row, bool on)
        {
            WorldRow(row.Key).SetValue(null, on);
            NeighbourhoodWorldRow.Apply();
        }

        // ---- the row is on the page -------------------------------------------------------------

        [Fact]
        public void TheGraphicsPageOffersTheConnectedNeighbourhoodRow()
        {
            var names = GraphicsRows().Select(x => x.AttachedProperty?.Name).ToList();

            Assert.Contains(NeighbourhoodWorldRow.Key, names);
        }

        [Fact]
        public void EveryDescriptorStillHasAUniqueName()
        {
            // A duplicate would make NeighbourhoodWorldRow.Describes - and NeighbourhoodViewRow's
            // and SunCycleSetting's equivalents - match the wrong row, which is the same silent
            // failure the guard is here to stop.
            var names = GraphicsRows().Select(x => x.AttachedProperty?.Name).ToList();

            Assert.Equal(names.Count, names.Distinct().Count());
        }

        [Fact]
        public void TheNeighbourhoodViewAndSurroundingLotsRowsAreStillThere()
        {
            // The edit that added this row is a change to a plain list of literals, and a row that
            // fell out of it would leave the other two of the feature with no way to be reached.
            var names = GraphicsRows().Select(x => x.AttachedProperty?.Name).ToList();

            Assert.Contains(NeighbourhoodViewSetting.Key, names);
            Assert.Contains("SurroundingLotMode", names);
        }

        // ---- the row's shape --------------------------------------------------------------------

        [Fact]
        public void TheRowIsACheckboxOverANameTheChangeNotificationMatches()
        {
            // Deliberately clumsy, deliberately so - the same reasoning as the Dynamic Sun row. The
            // page's reflection is built on Type.GetProperty over the attached property, so the
            // name is the only stable handle.
            var property = typeof(NeighbourhoodWorldOption).GetProperty(NeighbourhoodWorldRow.Key);
            var row = Row(NeighbourhoodWorldRow.Key);

            Assert.NotNull(property);
            Assert.Equal(typeof(bool), property.PropertyType);
            Assert.IsType<UIOptionsSubpanelBase.UIExternalBoolPropOptionDescription>(row);
            Assert.Equal(UIOptionsSubpanelBase.SettingType.BoolProp, row.Type);
            Assert.True(NeighbourhoodWorldRow.Describes(row));
        }

        [Fact]
        public void TheRowIsFoundByItsOwnNameAndNotByTheOtherRows()
        {
            // Separate settings, so a row that matched the other's name would be written into the
            // wrong store.
            Assert.True(NeighbourhoodWorldRow.Describes(NeighbourhoodWorldRow.Key));
            Assert.False(NeighbourhoodWorldRow.Describes(NeighbourhoodViewSetting.Key));
            Assert.False(NeighbourhoodWorldRow.Describes("SurroundingLotMode"));
            Assert.False(NeighbourhoodWorldRow.Describes((string)null));
            Assert.False(NeighbourhoodWorldRow.Describes((UIOptionsSubpanelBase.UIOptionDescriprion)null));
        }

        [Fact]
        public void TheRowIsCaptionedAndExplained()
        {
            // No TS1 id exists - the game never had a connected neighbourhood - so the literal is
            // the floor, and the tooltip has to say what the row is not: it is inert until the
            // Neighbourhood View is 3D, and it is off by default.
            var row = Row(NeighbourhoodWorldRow.Key);

            Assert.NotNull(row.LocalizationInfo);
            Assert.Equal("Connected Neighbourhood", row.GetTitle());
            var about = row.GetAboutInfo();
            Assert.False(string.IsNullOrWhiteSpace(about));
            Assert.Contains("Neighbourhood View", about);
            Assert.Contains("3D", about);
            Assert.Equal("187", NeighbourhoodWorldRow.SimitoneTitleId);
            Assert.Equal("188", NeighbourhoodWorldRow.SimitoneAboutTitleId);
            Assert.Equal("189", NeighbourhoodWorldRow.SimitoneAboutDescriptionId);
        }

        [Fact]
        public void TheMasterSetIsTheFiveShippedFlagsAndNotAllSeven()
        {
            // The list is the contract with §7 and the milestone notes: B1, B2, B3, B5, B7. B4 and
            // B6 are unimplemented, and a checkbox that switched a capability a player cannot see
            // on is the exact failure the all-off default exists to prevent.
            Assert.Equal(NeighbourhoodWorldFlag.World, NeighbourhoodWorldRow.MasterFlags[0]);
            Assert.Contains(NeighbourhoodWorldFlag.NeighbourSubWorlds, NeighbourhoodWorldRow.MasterFlags);
            Assert.Contains(NeighbourhoodWorldFlag.Travel, NeighbourhoodWorldRow.MasterFlags);
            Assert.Contains(NeighbourhoodWorldFlag.Services, NeighbourhoodWorldRow.MasterFlags);
            Assert.Contains(NeighbourhoodWorldFlag.Persist, NeighbourhoodWorldRow.MasterFlags);
            Assert.DoesNotContain(NeighbourhoodWorldFlag.Offscreen, NeighbourhoodWorldRow.MasterFlags);
            Assert.Equal(NeighbourhoodWorldRow.MasterFlagCount, NeighbourhoodWorldRow.MasterFlags.Count);
        }

        // ---- what ticking the box does -----------------------------------------------------------

        [Fact]
        public void TheRowIsOffWhenNothingHasTurnedItOn()
        {
            // A Part B capability that is on because nobody looked at the value is how a world
            // that is not finished reaches a player.
            Assert.False(NeighbourhoodWorldRow.Enabled);
            Assert.False((bool)Row(NeighbourhoodWorldRow.Key).GetValue(null));
        }

        [Fact]
        public void TickingTheRowTurnsOnAllFiveFlags()
        {
            Row(NeighbourhoodWorldRow.Key).SetValue(null, true);

            foreach (var flag in NeighbourhoodWorldRow.MasterFlags)
            {
                Assert.True(NeighbourhoodWorldFlags.IsOn(flag), flag.ToString());
            }
            Assert.True(NeighbourhoodWorldRow.Enabled);
        }

        [Fact]
        public void UntickingTheRowTurnsAllFiveFlagsOff()
        {
            Row(NeighbourhoodWorldRow.Key).SetValue(null, true);

            Row(NeighbourhoodWorldRow.Key).SetValue(null, false);

            foreach (var flag in NeighbourhoodWorldRow.MasterFlags)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(flag), flag.ToString());
            }
            Assert.False(NeighbourhoodWorldRow.Enabled);
        }

        [Fact]
        public void TickingTheRowLeavesTheUnshippedFlagAlone()
        {
            // Offscreen is a milestone switch, not this row's, and it must not quietly gate a
            // capability its implementation does not own.
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Offscreen, true, save: false);

            Row(NeighbourhoodWorldRow.Key).SetValue(null, true);

            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Offscreen));

            //and unticking clears the five without reaching across to it either
            Row(NeighbourhoodWorldRow.Key).SetValue(null, false);
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Offscreen));
        }

        [Fact]
        public void APartlyOnInstallReadsAsOffSoTheCheckboxIsNotLying()
        {
            // Five keys somebody edited by hand, or a milestone that has switched one of them, is
            // not a state this row has a truthful caption for - so it draws off, and ticking it
            // completes the set rather than erasing the keys.
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Persist, true, save: false);

            var row = Row(NeighbourhoodWorldRow.Key);
            Assert.False((bool)row.GetValue(null));

            row.SetValue(null, true);
            Assert.True(NeighbourhoodWorldRow.Enabled);
        }

        // ---- what gets written to disk ------------------------------------------------------------

        [Fact]
        public void ApplyWritesTheFiveKeysToTheSidecar()
        {
            // The write half the base page cannot do: none of these five keys is a GlobalSettings
            // property, so a save that did not happen here would save nothing at all.
            ToggleAndApply(true);

            //The token is the writer's own spelling rather than a literal typed here, so a change
            //to how a flag is stored fails this test once, with a name, instead of silently
            //leaving the assertion agreeing with a format nobody reads any more.
            foreach (var flag in NeighbourhoodWorldRow.MasterFlags)
            {
                Assert.Equal(NeighbourhoodWorldFlags.TokenFor(true),
                    Raw(NeighbourhoodWorldFlags.KeyFor(flag)));
            }
            Assert.True(NeighbourhoodWorldFlags.ReadFrom(Sidecar, NeighbourhoodWorldFlag.World));
            Assert.True(NeighbourhoodWorldFlags.ReadFrom(Sidecar, NeighbourhoodWorldFlag.Persist));
        }

        [Fact]
        public void ApplyWritesFalseAsWellAsTrue()
        {
            // An off that is only implied by a missing key is a value this build never wrote, and
            // Resolve treats it as off for a different reason - so "off" has to be on the file.
            ToggleAndApply(true);

            ToggleAndApply(false);

            foreach (var flag in NeighbourhoodWorldRow.MasterFlags)
            {
                Assert.Equal(NeighbourhoodWorldFlags.TokenFor(false),
                    Raw(NeighbourhoodWorldFlags.KeyFor(flag)));
            }
        }

        [Fact]
        public void TheRowLeavesTheOtherKeysInTheFileAlone()
        {
            // simitone.ini is shared: the Dynamic Sun row and the Neighbourhood View row own two
            // more keys, and the flags own seven. A write that dropped them would silently reset
            // a player's choices - the failure SimitoneIniFile's read-merge-write exists for.
            Seed(
                SunCycleSetting.Key + "=true",
                NeighbourhoodViewSetting.Key + "=" + NeighbourhoodViewSetting.Bitmap2DToken,
                "SomebodyElsesKey=do not lose me");

            ToggleAndApply(true);

            Assert.Equal("true", Raw(SunCycleSetting.Key));
            Assert.Equal(NeighbourhoodViewSetting.Bitmap2DToken, Raw(NeighbourhoodViewSetting.Key));
            Assert.Equal("do not lose me", Raw("SomebodyElsesKey"));

            //and again on the way back off, which is the direction that used to truncate
            ToggleAndApply(false);

            Assert.Equal("true", Raw(SunCycleSetting.Key));
            Assert.Equal(NeighbourhoodViewSetting.Bitmap2DToken, Raw(NeighbourhoodViewSetting.Key));
            Assert.Equal("do not lose me", Raw("SomebodyElsesKey"));
        }

        [Fact]
        public void TheChoiceSurvivesARestart()
        {
            // What "persists through simitone.ini" means, stated as a fact rather than an
            // inference from the default: write, forget every cache, and read the file again.
            ToggleAndApply(true);

            NeighbourhoodWorldFlags.Reset();

            foreach (var flag in NeighbourhoodWorldRow.MasterFlags)
            {
                Assert.True(NeighbourhoodWorldFlags.IsOn(flag), flag.ToString());
            }
            Assert.True(NeighbourhoodWorldRow.Enabled);
        }

        // ---- relationship to the view row ----------------------------------------------------------

        [Fact]
        public void TheRowIsInertWhenTheNeighbourhoodViewIsNot3D()
        {
            // Documented, not enforced: with the view on Off or 2D the checkbox still works and
            // still writes its five keys, and nothing consults the view on the way. The alternative
            // is a hidden dependency between two rows that write the same file, where putting the
            // view back to 3D would resurrect a Part B set the player had switched off.
            NeighbourhoodViewSetting.Set(NeighbourhoodView.Off);

            ToggleAndApply(true);

            Assert.Equal(NeighbourhoodView.Off, NeighbourhoodViewSetting.Current);
            Assert.True(NeighbourhoodWorldRow.Enabled);
            Assert.Equal(NeighbourhoodWorldFlags.TokenFor(true), Raw(NeighbourhoodWorldFlags.WorldKey));
        }

        [Fact]
        public void ChangingTheNeighbourhoodViewDoesNotTouchTheFlags()
        {
            // The other direction of the same independence. If the view row wrote the flags, then
            // a player who picked 2D would switch Part B off without being told and a player who
            // came back to 3D would switch it back on.
            ToggleAndApply(true);

            var view = Row(NeighbourhoodViewSetting.Key);
            view.SetValue(null, (int)NeighbourhoodView.Bitmap2D);
            NeighbourhoodViewRow.Apply();

            Assert.Equal(NeighbourhoodView.Bitmap2D, NeighbourhoodViewSetting.Current);
            Assert.True(NeighbourhoodWorldRow.Enabled);
            Assert.Equal(NeighbourhoodWorldFlags.TokenFor(true), Raw(NeighbourhoodWorldFlags.TravelKey));
        }

        // ---- the capability row: Simulate Neighbours (B6) -----------------------------------------
        //
        // What is worth pinning, in the same order as the rest of the class: that the rows exist
        // at all (a setting with no row does not exist as far as a player is concerned), that
        // each owns exactly one flag and reaches for no other - six wrong flags would be silent -
        // that the master row and the two do not move each other in either direction, and that
        // the keys reach the sidecar, because the page's whole write path for this feature is one
        // predicate and one Apply, and a row the predicate does not match is a checkbox that
        // resets at the next launch.

        [Fact]
        public void BothCapabilityRowsExistBesideTheMasterRow()
        {
            var rows = WorldRows();
            var names = rows.Select(x => x.AttachedProperty?.Name).ToList();

            Assert.Equal(2, rows.Count);
            Assert.Contains(NeighbourhoodWorldRow.Key, names);
            Assert.Contains(Simulate.Key, names);
            Assert.Equal(names.Count, names.Distinct().Count());

            //And the capability rows, in the order the page shows them.
            Assert.Equal(new[] { NeighbourhoodWorldFlag.Offscreen },
                NeighbourhoodWorldCapabilityRow.All.Select(x => x.Flag));
        }

        [Fact]
        public void EachCapabilityRowIsACheckboxOverItsOwnBoolProperty()
        {
            // The same clumsy shape as the master row, for the same reason: the page's reflection
            // is built on Type.GetProperty over the attached property, so the name is the only
            // stable handle, and a name with no property behind it is a checkbox that does
            // nothing.
            foreach (var capability in NeighbourhoodWorldCapabilityRow.All)
            {
                var property = typeof(NeighbourhoodWorldOption).GetProperty(capability.Key);
                var row = WorldRow(capability.Key);

                Assert.NotNull(property);
                Assert.Equal(typeof(bool), property.PropertyType);
                Assert.IsType<UIOptionsSubpanelBase.UIExternalBoolPropOptionDescription>(row);
                Assert.Equal(UIOptionsSubpanelBase.SettingType.BoolProp, row.Type);
                Assert.True(capability.Describes(row));
            }
        }

        [Fact]
        public void EachCapabilityRowIsOffWhenNothingHasTurnedItOn()
        {
            // A Part B capability that is on because nobody looked at the value is how a world
            // that is not finished reaches a player, and these two are the two that are not
            // finished. The default has to be the flags' own: there is no value here that is not
            // already off unless something recorded one.
            foreach (var capability in NeighbourhoodWorldCapabilityRow.All)
            {
                Assert.False(capability.Enabled, capability.Key);
                Assert.False((bool)WorldRow(capability.Key).GetValue(null));
            }
        }

        [Fact]
        public void TickingSimulateNeighboursTurnsOffscreenOnAndNothingElse()
        {
            WorldRow(Simulate.Key).SetValue(null, true);

            AssertOnly(NeighbourhoodWorldFlag.Offscreen);
            Assert.True(Simulate.Enabled);

            //and the master row is a conjunction over its own five, so it must not follow
            Assert.False(NeighbourhoodWorldRow.Enabled);
            Assert.False((bool)WorldRow(NeighbourhoodWorldRow.Key).GetValue(null));
        }

        [Fact]
        public void UntickingACapabilityRowLeavesTheMasterAlone()
        {
            // Off has to be as complete as on, or a player who switches B6 back off is still
            // running the world while they are away.
            WorldRow(Simulate.Key).SetValue(null, true);
            Row(NeighbourhoodWorldRow.Key).SetValue(null, true);

            WorldRow(Simulate.Key).SetValue(null, false);

            Assert.False(Simulate.Enabled);
            Assert.True(NeighbourhoodWorldRow.Enabled);
            Assert.Equal(NeighbourhoodWorldFlags.All.Count - 1, FlagSet().Count(x => x));
        }

        [Fact]
        public void TheMasterRowDoesNotMoveTheCapabilityFlagInEitherDirection()
        {
            // The independence the master row's remarks promise. A milestone that has switched B6
            // on by hand must still find its flag on after somebody unticks Connected
            // Neighbourhood, and a tick on B6 must not make the master draw as on.
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Offscreen, true, save: false);

            Row(NeighbourhoodWorldRow.Key).SetValue(null, true);
            Assert.True(Simulate.Enabled);

            Row(NeighbourhoodWorldRow.Key).SetValue(null, false);
            Assert.True(Simulate.Enabled);
            Assert.False(NeighbourhoodWorldRow.Enabled);
            Assert.Equal(1, FlagSet().Count(x => x));
        }

        [Fact]
        public void TheCapabilityIsNotInTheMasterSet()
        {
            // A capability folded into MasterFlags would become part of the conjunction: ticking
            // the master would switch it on and unticking would clear it, which is precisely the
            // dependency the capability rows exist to avoid.
            Assert.DoesNotContain(NeighbourhoodWorldFlag.Offscreen, NeighbourhoodWorldRow.MasterFlags);
            Assert.Equal(NeighbourhoodWorldRow.MasterFlagCount, NeighbourhoodWorldRow.MasterFlags.Count);
        }

        [Fact]
        public void ThePageFindsBothRowsByTheirOwnNames()
        {
            // The one predicate UIGraphicsOptionsSubpanel.OnSettingsChanged has for this feature,
            // and the reason Apply covers every flag: a row it did not match would be recorded in
            // memory and never written down.
            Assert.True(NeighbourhoodWorldRow.Describes(NeighbourhoodWorldRow.Key));
            Assert.True(NeighbourhoodWorldRow.Describes(Simulate.Key));

            Assert.True(NeighbourhoodWorldRow.Describes(WorldRow(Simulate.Key)));

            //and still nobody else's
            Assert.False(NeighbourhoodWorldRow.Describes(NeighbourhoodViewSetting.Key));
            Assert.False(NeighbourhoodWorldRow.Describes("SurroundingLotMode"));
            Assert.False(NeighbourhoodWorldRow.Describes(SunCycleSetting.Key));
            Assert.False(NeighbourhoodWorldRow.Describes((string)null));
            Assert.False(NeighbourhoodWorldRow.Describes((UIOptionsSubpanelBase.UIOptionDescriprion)null));
        }

        [Fact]
        public void TheCapabilityRowIsFoundByItsOwnNameAndNotByTheMasterRows()
        {
            // A row that matched the master's name would write a player's choice into the wrong
            // ini key with no exception anywhere.
            Assert.True(Simulate.Describes(Simulate.Key));
            Assert.False(Simulate.Describes(NeighbourhoodWorldRow.Key));
            Assert.False(Simulate.Describes((string)null));
            Assert.False(Simulate.Describes((UIOptionsSubpanelBase.UIOptionDescriprion)null));
        }

        [Fact]
        public void TheCapabilityRowsAreCaptionedAndSayTheyNeedTheMaster()
        {
            // No TS1 id exists - the game never had a connected neighbourhood - so the literal is
            // the floor under both tables, and the tooltip has to answer the two things a player
            // cannot otherwise see: that it is off by default, and that it does nothing on its own.
            Assert.Equal("Simulate Neighbours", WorldRow(Simulate.Key).GetTitle());

            foreach (var capability in NeighbourhoodWorldCapabilityRow.All)
            {
                var about = WorldRow(capability.Key).GetAboutInfo();

                Assert.False(string.IsNullOrWhiteSpace(about));
                Assert.Contains("Connected Neighbourhood", about);
                Assert.Contains("off unless you turn it on", about);
            }

            Assert.Equal("940", Simulate.SimitoneTitleId);
            Assert.Equal("941", Simulate.SimitoneAboutTitleId);
            Assert.Equal("942", Simulate.SimitoneAboutDescriptionId);
        }

        [Fact]
        public void TheCapabilityRowsUseIdsNoOtherGraphicsRowHasTaken()
        {
            // 940-945 are the next free run of six beside the Graphics page's own block, and a
            // collision would put one row's caption on another's control. Only the page's own
            // rows are compared: FreeWillSetting's 190-194 and the sound page's 960s are checked
            // by their own files' tests, and 940-945 are clear of both.
            //The capability rows are now on the page themselves, so they are excluded from the
            //set they are checked against - otherwise a row would always collide with its own ids.
            var capabilityKeys = new HashSet<string>(
                NeighbourhoodWorldCapabilityRow.All.Select(c => c.Key), StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in GraphicsRows())
            {
                if (capabilityKeys.Contains(row.AttachedProperty?.Name)) continue;
                var info = row.LocalizationInfo;
                if (!info.HasValue) continue;
                if (info.Value.SimitoneTitleID != null) taken.Add(info.Value.SimitoneTitleID);
                if (info.Value.SimitoneAboutTitleID != null) taken.Add(info.Value.SimitoneAboutTitleID);
                if (info.Value.SimitoneAboutDescriptionID != null) taken.Add(info.Value.SimitoneAboutDescriptionID);
            }

            foreach (var capability in NeighbourhoodWorldCapabilityRow.All)
            {
                Assert.DoesNotContain(capability.SimitoneTitleId, taken);
                Assert.DoesNotContain(capability.SimitoneAboutTitleId, taken);
                Assert.DoesNotContain(capability.SimitoneAboutDescriptionId, taken);
            }
        }

        // ---- what gets written to disk ------------------------------------------------------------

        [Fact]
        public void ApplyWritesTheCapabilityKeyAndOnlyItsOwnValue()
        {
            // The write half the base page cannot do, once per row. Asserted as the whole set
            // rather than as one key, because a write that reached across to a second flag would
            // still put the right value under the key being looked for.
            ToggleAndApply(Simulate, true);

            Assert.Equal(NeighbourhoodWorldFlags.TokenFor(true), Raw(NeighbourhoodWorldFlags.OffscreenKey));
            Assert.Null(Raw("NeighbourhoodWorldWalk")); //the cut flag is never written back
            Assert.True(NeighbourhoodWorldFlags.ReadFrom(Sidecar, NeighbourhoodWorldFlag.Offscreen));

            //Every one of the master's five stays off on disk, because it was off in memory.
            foreach (var flag in NeighbourhoodWorldRow.MasterFlags)
            {
                Assert.False(NeighbourhoodWorldFlags.ReadFrom(Sidecar, flag), flag.ToString());
            }
        }

        [Fact]
        public void ApplyWritesFalseAsWellAsTrueForACapabilityRow()
        {
            // An off that is only implied by a missing key is a value this build never wrote, and
            // Resolve treats it as off for a different reason - so "off" has to be on the file.
            ToggleAndApply(Simulate, true);

            ToggleAndApply(Simulate, false);

            Assert.Equal(NeighbourhoodWorldFlags.TokenFor(false), Raw(NeighbourhoodWorldFlags.OffscreenKey));
            Assert.False(Simulate.Enabled);
        }

        [Fact]
        public void EachCapabilityChoiceSurvivesARestart()
        {
            // What "persists through simitone.ini" means for each row: write, forget every cache,
            // and read the file again.
            foreach (var capability in NeighbourhoodWorldCapabilityRow.All)
            {
                ToggleAndApply(capability, true);
            }

            NeighbourhoodWorldFlags.Reset();

            foreach (var capability in NeighbourhoodWorldCapabilityRow.All)
            {
                Assert.True(NeighbourhoodWorldFlags.IsOn(capability.Flag), capability.Key);
                Assert.True(capability.Enabled, capability.Key);
                Assert.True((bool)WorldRow(capability.Key).GetValue(null));
            }

            //and nothing else came back with it
            Assert.Equal(1, FlagSet().Count(x => x));
        }

        [Fact]
        public void TheMasterRowsWriteDoesNotDisturbTheCapabilityKey()
        {
            // The other direction of the same file. The master row's Apply covers every flag, so
            // it has to write the one it does not own with the value it already had - a save of
            // an unrelated setting must not be able to reset B6.
            ToggleAndApply(Simulate, true);

            ToggleAndApply(true);

            Assert.True(NeighbourhoodWorldFlags.ReadFrom(Sidecar, NeighbourhoodWorldFlag.Offscreen));

            ToggleAndApply(false);

            Assert.True(NeighbourhoodWorldFlags.ReadFrom(Sidecar, NeighbourhoodWorldFlag.Offscreen));
        }

        [Fact]
        public void ACapabilityRowLeavesTheOtherKeysInTheFileAlone()
        {
            // simitone.ini is shared with the Dynamic Sun row, the Neighbourhood View row and the
            // master row's five keys. A write that dropped them would silently reset a player's
            // choices, which is the failure SimitoneIniFile's read-merge-write exists for.
            Seed(
                SunCycleSetting.Key + "=true",
                NeighbourhoodViewSetting.Key + "=" + NeighbourhoodViewSetting.Bitmap2DToken,
                NeighbourhoodWorldFlags.TravelKey + "=" + NeighbourhoodWorldFlags.TokenFor(true),
                "SomebodyElsesKey=do not lose me");

            ToggleAndApply(Simulate, true);

            Assert.Equal("true", Raw(SunCycleSetting.Key));
            Assert.Equal(NeighbourhoodViewSetting.Bitmap2DToken, Raw(NeighbourhoodViewSetting.Key));
            Assert.Equal(NeighbourhoodWorldFlags.TokenFor(true), Raw(NeighbourhoodWorldFlags.TravelKey));
            Assert.Equal("do not lose me", Raw("SomebodyElsesKey"));

            //and the way back off, which is the direction that used to truncate
            ToggleAndApply(Simulate, false);

            Assert.Equal("true", Raw(SunCycleSetting.Key));
            Assert.Equal(NeighbourhoodViewSetting.Bitmap2DToken, Raw(NeighbourhoodViewSetting.Key));
            Assert.Equal(NeighbourhoodWorldFlags.TokenFor(true), Raw(NeighbourhoodWorldFlags.TravelKey));
            Assert.Equal("do not lose me", Raw("SomebodyElsesKey"));
        }

        // ---- the page wiring that is still to be done ----------------------------------------------

        [Fact]
        public void TheGraphicsPageOffersTheCapabilityRows()
        {
            // The two rows are built by NeighbourhoodWorldRow.Descriptors() and reach the page
            // through the spread in GraphicsDescriptors. This pins that they are on it and that
            // neither collides with the master row's name.
            var names = GraphicsRows().Select(x => x.AttachedProperty?.Name).ToList();

            Assert.Contains(Simulate.Key, names);
            Assert.Contains(NeighbourhoodWorldRow.Key, names);
        }
    }
}
