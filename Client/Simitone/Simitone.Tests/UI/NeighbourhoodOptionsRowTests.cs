/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.LotView;
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
    /// The two Options rows the connected-neighbourhood feature adds, and the coherence between
    /// them.
    ///
    /// <para>
    /// §8 asks for a <c>NeighbourhoodView</c> (Off / 2D / 3D) and says to <em>reuse</em>
    /// <c>SurroundingLotMode</c> for the in-lot surroundings. Two rows rather than one, because
    /// they answer two different questions - "is the neighbourhood map a painted picture or a
    /// generated world" and "how much world is drawn around the lot you are standing in" - and
    /// collapsing them would take away a choice players of the city renderer already have.
    /// </para>
    ///
    /// <para>
    /// The failures worth guarding are the quiet ones, the same ones
    /// <c>SunCycleSettingTests</c> guards for the Dynamic Sun row: a row that is not on the page,
    /// a row whose property name no longer matches the key the storage looks for, and a row that
    /// would set a preference the renderer never reads. A page cannot be built here - it needs a
    /// graphics device - so the assertions are made on the descriptor list, which is exactly what
    /// the page builds itself from.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodOptionsRowTests : IDisposable
    {
        private readonly string _tempRoot;

        public NeighbourhoodOptionsRowTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-neigh-options-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            //NeighbourhoodViewSetting caches the live choice process-wide, and a test that set it
            //to 3D and stopped there would change what every later test in the run sees. Put it
            //back to the default, which is what a fresh process would have loaded.
            NeighbourhoodViewSetting.Set(NeighbourhoodViewSetting.Default);
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>A settings object backed by its own throwaway ini, never the real one.</summary>
        private GlobalSettings NewSettings() => new GlobalSettings(Path.Combine(_tempRoot, "config.ini"));

        private static List<UIOptionsSubpanelBase.UIOptionDescriprion> GraphicsRows() =>
            UIGraphicsOptionsSubpanel.GraphicsDescriptors(null).ToList();

        private static UIOptionsSubpanelBase.UIOptionDescriprion Row(string name) =>
            GraphicsRows().Single(x => x.AttachedProperty?.Name == name);

        // ---- both rows exist ------------------------------------------------------------------

        [Fact]
        public void TheGraphicsPageOffersBothRows()
        {
            var names = GraphicsRows().Select(x => x.AttachedProperty?.Name).ToList();

            Assert.Contains(NeighbourhoodViewSetting.Key, names);
            Assert.Contains("SurroundingLotMode", names);
        }

        [Fact]
        public void EveryDescriptorStillHasAUniqueName()
        {
            // A duplicate name would make NeighbourhoodViewRow.Describes - and SunCycleSetting's
            // and FreeWillSetting's equivalents - match the wrong row.
            var names = GraphicsRows().Select(x => x.AttachedProperty?.Name).ToList();

            Assert.Equal(names.Count, names.Distinct().Count());
        }

        [Fact]
        public void TheExistingGraphicsRowsAreUnaffected()
        {
            // A regression guard on the edit that added the two rows: the descriptor list is a
            // plain list of literals and one wrong entry silently reorders the page.
            var names = GraphicsRows().Select(x => x.AttachedProperty?.Name).ToList();

            foreach (var expected in new[]
            {
                "AntiAlias", "EnableTransitions", "Weather", "SmoothZoom", "DirectionalLight3D",
                "ComplexShaders", "TexCompression", "Windowed", "LightingMode", "ShadowDetail",
                "WaterReflections", "GlobalGraphicsMode", "SunCycle"
            })
            {
                Assert.Contains(expected, names);
            }
        }

        // ---- the neighbourhood view row ----------------------------------------------------------

        [Fact]
        public void TheNeighbourhoodViewRowIsThreeRadiosInAFixedOrder()
        {
            // The order is Off / 2D / 3D and it is not moving - an existing simitone.ini stores a
            // token and a hand-edited one may hold a number - so 3D being the default does not
            // make it the first radio. A row that reordered them would be a different setting
            // wearing the same name.
            var row = Row(NeighbourhoodViewSetting.Key) as UIOptionsSubpanelBase.UIIntPropOptionDescription;

            Assert.NotNull(row);
            Assert.Equal(UIOptionsSubpanelBase.SettingType.Variable, row.Type);
            Assert.Equal((int)NeighbourhoodView.Off, row.LowBound);
            Assert.Equal((int)NeighbourhoodView.World3D, row.Highbound);
            Assert.Equal(
                new[] { NeighbourhoodView.Off, NeighbourhoodView.Bitmap2D, NeighbourhoodView.World3D },
                NeighbourhoodViewSetting.All.Select(v => v));
            Assert.Equal("Off", row.GetValueTooltip((int)NeighbourhoodView.Off));
            Assert.Equal("2D", row.GetValueTooltip((int)NeighbourhoodView.Bitmap2D));
            Assert.Equal("3D", row.GetValueTooltip((int)NeighbourhoodView.World3D));
        }

        [Fact]
        public void TheNeighbourhoodViewRowReadsAndWritesTheSidecarNotTheSettingsObject()
        {
            // The row's storage is Simitone's own key, so a change must not land on a
            // GlobalSettings property - and the change notification has to still recognise the row,
            // or the page would set it and never write it down.
            var row = Row(NeighbourhoodViewSetting.Key);

            Assert.IsType<UIOptionsSubpanelBase.UIExternalIntPropOptionDescription>(row);
            Assert.True(NeighbourhoodViewRow.Describes(row));
            Assert.True(NeighbourhoodViewSetting.Describes(NeighbourhoodViewSetting.Key));
            Assert.False(NeighbourhoodViewRow.Describes((string)null));
            Assert.False(NeighbourhoodViewRow.Describes((UIOptionsSubpanelBase.UIOptionDescriprion)null));
            Assert.False(NeighbourhoodViewRow.Describes("SurroundingLotMode"));
        }

        [Fact]
        public void TheNeighbourhoodViewRowIsFoundByTheNameTheChangeNotificationMatches()
        {
            // Deliberately clumsy, and deliberately so - the same reasoning as the Dynamic Sun row.
            // The page's reflection is built on Type.GetProperty over the descriptor's attached
            // property, so the name is the only stable handle, and a rename that missed
            // NeighbourhoodViewSetting.Key would save nothing and throw nothing.
            var property = typeof(NeighbourhoodViewOption).GetProperty(NeighbourhoodViewSetting.Key);

            Assert.NotNull(property);
            Assert.Equal(typeof(int), property.PropertyType);
        }

        [Fact]
        public void TheNeighbourhoodViewRowIsCaptionedAndExplained()
        {
            // It has no TS1 id to point at - the game never had a 3D neighbourhood - so the literal
            // is the floor, and the tooltip has to say what the setting does and what it does not.
            var row = Row(NeighbourhoodViewSetting.Key);

            Assert.NotNull(row.LocalizationInfo);
            Assert.Equal("Neighbourhood View", row.GetTitle());
            var about = row.GetAboutInfo();
            Assert.False(string.IsNullOrWhiteSpace(about));
            Assert.Contains("Surrounding Lots", about);
            Assert.Contains("painted map", about);
        }

        [Fact]
        public void TheNeighbourhoodViewRowRoundTripsTheStoredValue()
        {
            // A boxed int, because the radio group unboxes the descriptor's value straight to an
            // int; a boxed enum would throw there rather than draw. So: read, set, read again, and
            // assert the enum the setting resolves is the one that was asked for.
            var row = Row(NeighbourhoodViewSetting.Key);

            foreach (var view in NeighbourhoodViewSetting.All)
            {
                row.SetValue(null, (int)view);
                Assert.Equal(view, NeighbourhoodViewSetting.Current);
                Assert.Equal((int)view, (int)row.GetValue(null));
            }

            //and it comes back to the default when the sidecar says nothing
            row.SetValue(null, (int)NeighbourhoodViewSetting.Default);
            Assert.Equal(NeighbourhoodViewSetting.Default, NeighbourhoodViewSetting.Current);
        }

        [Fact]
        public void AValueOutsideTheThreeStepsIsCoercedRatherThanStored()
        {
            // The store resolves whatever it is handed, so a radio index from a future build, a
            // hand-edited ini or a cast cannot leave the setting in a state the panel cannot draw.
            var row = Row(NeighbourhoodViewSetting.Key);

            row.SetValue(null, 9);
            Assert.Equal(NeighbourhoodViewSetting.Default, NeighbourhoodViewSetting.Current);

            row.SetValue(null, (int)NeighbourhoodViewSetting.Default);
        }

        [Fact]
        public void TheNeighbourhoodViewRowStartsOn2DAndStillSwitches()
        {
            // The default is the painted map, and the row is the thing a player uses to opt into
            // the generated world. Both halves matter: a default that quietly followed the last
            // row a test touched, or a row that had become decorative.
            var row = Row(NeighbourhoodViewSetting.Key);

            row.SetValue(null, (int)NeighbourhoodViewSetting.Default);
            Assert.Equal(NeighbourhoodView.Bitmap2D, NeighbourhoodViewSetting.Current);

            row.SetValue(null, (int)NeighbourhoodView.World3D);
            Assert.Equal(NeighbourhoodView.World3D, NeighbourhoodViewSetting.Current);

            row.SetValue(null, (int)NeighbourhoodView.Off);
            Assert.Equal(NeighbourhoodView.Off, NeighbourhoodViewSetting.Current);

            row.SetValue(null, (int)NeighbourhoodViewSetting.Default);
            Assert.Equal(NeighbourhoodView.Bitmap2D, NeighbourhoodViewSetting.Current);
        }

        // ---- the surrounding lots row ------------------------------------------------------------

        [Fact]
        public void TheSurroundingLotsRowIsAReflectedRadioOverTheRealProperty()
        {
            // It is a GlobalSettings property, not a Simitone key, so the row is an ordinary
            // reflected one: the base page's save writes it to config.ini and
            // GraphicsOptions.Apply pushes it into WorldConfig.SurroundingLots. No external
            // descriptor here is the point - if it had one, the change would never be written down.
            var row = Row("SurroundingLotMode");

            Assert.Equal(UIOptionsSubpanelBase.SettingType.Variable, row.Type);
            Assert.IsNotType<UIOptionsSubpanelBase.UIExternalIntPropOptionDescription>(row);
            Assert.Equal(typeof(GlobalSettings), row.AttachedProperty.DeclaringType);
            Assert.Equal(typeof(int), row.AttachedProperty.PropertyType);

            var radios = row as UIOptionsSubpanelBase.UIIntPropOptionDescription;
            Assert.Equal(SurroundingLotModeRow.Off, radios.LowBound);
            Assert.Equal(SurroundingLotModeRow.High, radios.Highbound);
            Assert.Equal(new[] { "Off", "Low", "High" }, SurroundingLotModeRow.Captions);
            Assert.Equal("Off", radios.GetValueTooltip(SurroundingLotModeRow.Off));
            Assert.Equal("High", radios.GetValueTooltip(SurroundingLotModeRow.High));
        }

        [Fact]
        public void TheSurroundingLotsRowPersistsThroughTheSettingsObject()
        {
            // The property is written and read back through a real GlobalSettings, which is what
            // the base page's save does. Asserting it round-trips is the check that this row is a
            // real setting rather than a control wired to nothing.
            var settings = NewSettings();
            var row = Row("SurroundingLotMode");
            var wanted = settings.SurroundingLotMode == SurroundingLotModeRow.High
                ? SurroundingLotModeRow.Off
                : SurroundingLotModeRow.High;

            row.SetValue(settings, wanted);

            Assert.Equal(wanted, settings.SurroundingLotMode);
            Assert.Equal(wanted, (int)row.GetValue(settings));

            //and it survives a save/reload, which is the part "persists correctly" means
            settings.Save();
            Assert.Equal(wanted, NewSettings().SurroundingLotMode);
        }

        [Fact]
        public void TheSurroundingLotsRowReachesTheRendererThroughThePreferences()
        {
            // The whole point of the row: a preference a player cannot reach is the same as no
            // preference. GraphicsPreferences is the one place the mapping from ini to renderer
            // lives, so the row's value has to arrive there unchanged.
            var settings = NewSettings();
            var row = Row("SurroundingLotMode");
            row.SetValue(settings, SurroundingLotModeRow.Low);

            var preferences = GraphicsPreferences.From(settings, false, NeighbourhoodView.Bitmap2D);
            Assert.Equal(SurroundingLotModeRow.Low, preferences.SurroundingLots);

            //and Apply is what actually hands it to the lot view
            var config = new WorldConfig();
            GraphicsOptions.Apply(config, preferences);
            Assert.Equal(SurroundingLotModeRow.Low, config.SurroundingLots);
        }

        [Fact]
        public void TheSurroundingLotsRowIsCaptionedAndExplained()
        {
            var row = Row("SurroundingLotMode");

            Assert.NotNull(row.LocalizationInfo);
            Assert.Equal("Surrounding Lots", row.GetTitle());
            var about = row.GetAboutInfo();
            Assert.False(string.IsNullOrWhiteSpace(about));
            Assert.Contains("Neighbourhood View", about);
        }

        [Fact]
        public void TheTwoRowsAreFoundByTheirOwnNames()
        {
            // Deliberately separate, because they are separate settings. A row that matched the
            // other's name would be saved into the wrong store.
            Assert.True(NeighbourhoodViewRow.Describes(NeighbourhoodViewSetting.Key));
            Assert.False(NeighbourhoodViewRow.Describes("SurroundingLotMode"));
            Assert.True(SurroundingLotModeRow.Describes("SurroundingLotMode"));
            Assert.False(SurroundingLotModeRow.Describes(NeighbourhoodViewSetting.Key));
            Assert.False(SurroundingLotModeRow.Describes((string)null));
        }

        [Fact]
        public void TheTwoRowsCanBeBuiltOnTheirOwn()
        {
            // Public and separate from the page's descriptor list, for the reason
            // GraphicsDescriptors is public: a page cannot be built in a test, but the rows it is
            // made of can be inspected, and a row whose property has been renamed is exactly the
            // mistake that would otherwise only show up as a control that does nothing.
            foreach (var row in SurroundingLotModeRow.Descriptors())
            {
                Assert.NotNull(row.AttachedProperty);
                Assert.False(string.IsNullOrWhiteSpace(row.GetTitle()));
            }
            Assert.Equal(2, SurroundingLotModeRow.Descriptors().Count());
        }

        // ---- coherence between the pair ----------------------------------------------------------

        [Fact]
        public void TheTwoSettingsAreIndependentAndThePaintedMapIsTheDefault()
        {
            // The pair has to be coherent in the one sense that matters to a player who changes
            // neither: the neighbourhood is the painted map and a lot standing on it is the lot as
            // it always was. Asking for the generated world is what opens both gates -
            // NeighbourhoodView 3D and a SurroundingLotMode above 0 - and the Surrounding Lots row
            // still defaults to an on step, so opting into 3D gives the lot its surroundings.
            var preferences = GraphicsOptions.Defaults;

            Assert.Equal(NeighbourhoodViewSetting.Default, preferences.NeighbourhoodView);
            Assert.Equal(NeighbourhoodView.Bitmap2D, preferences.NeighbourhoodView);
            Assert.Equal(SurroundingLotModeRow.Default, preferences.SurroundingLots);
            Assert.Equal(SurroundingLotModeRow.Low, preferences.SurroundingLots);
            Assert.True(preferences.SurroundingLots > 0);

            Assert.False(SimitoneSurroundingsSelection.For(
                preferences.NeighbourhoodView, preferences.SurroundingLots).Enabled);
            Assert.True(SimitoneSurroundingsSelection.For(
                NeighbourhoodView.World3D, preferences.SurroundingLots).Enabled);
        }

        [Fact]
        public void ABrandNewConfigFileGetsTheLowestOnSurroundingLotDetail()
        {
            // GlobalSettings._DefaultValues is the ini a first run is stamped with, and the gate
            // reads it before anything else. This is the assertion that the Out step - the one
            // value that turns the surroundings off - is not what a new install gets.
            var settings = NewSettings();
            Assert.Equal(SurroundingLotModeRow.Default, settings.SurroundingLotMode);

            var preferences = GraphicsPreferences.From(settings, true, NeighbourhoodView.World3D);
            Assert.Equal(SurroundingLotModeRow.Default, preferences.SurroundingLots);

            var config = new WorldConfig();
            GraphicsOptions.Apply(config, preferences);
            Assert.Equal(SurroundingLotModeRow.Default, config.SurroundingLots);
            Assert.True(SimitoneSurroundingsSelection.For(
                preferences.NeighbourhoodView, config.SurroundingLots).Enabled);
        }

        [Fact]
        public void TheSurroundingLotsRowStillTurnsSurroundingsOff()
        {
            // The row is the player's existing way of saying "not", and M4 honours it rather than
            // overriding it - so the default moving must not have made Off unreachable.
            var settings = NewSettings();
            var row = Row("SurroundingLotMode");

            row.SetValue(settings, SurroundingLotModeRow.Off);

            var preferences = GraphicsPreferences.From(settings, true, NeighbourhoodView.World3D);
            Assert.Equal(SurroundingLotModeRow.Off, preferences.SurroundingLots);
            Assert.False(SimitoneSurroundingsSelection.For(
                preferences.NeighbourhoodView, preferences.SurroundingLots).Enabled);

            row.SetValue(settings, SurroundingLotModeRow.High);
            Assert.Equal(SurroundingLotModeRow.High,
                GraphicsPreferences.From(settings, true, NeighbourhoodView.World3D).SurroundingLots);
        }

        [Fact]
        public void TurningTheNeighbourhoodViewOnDoesNotTouchTheSurroundingLots()
        {
            // With a settings object in hand, the two rows must not be wired to each other: M3's
            // WithSunCycle/WithNeighbourhoodView helpers copy the rest of the snapshot forward, and
            // a snapshot that lost SurroundingLots on the way would silently turn a player's
            // terrain detail back to the default when they changed the map's appearance.
            var settings = NewSettings();
            settings.SurroundingLotMode = SurroundingLotModeRow.Off;

            var preferences = GraphicsPreferences.From(settings, true, NeighbourhoodView.World3D);
            Assert.Equal(NeighbourhoodView.World3D, preferences.NeighbourhoodView);
            Assert.Equal(SurroundingLotModeRow.Off, preferences.SurroundingLots);
        }
    }
}
