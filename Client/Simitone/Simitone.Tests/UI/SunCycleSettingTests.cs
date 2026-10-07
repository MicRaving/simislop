using FSO.Client;
using FSO.Common;
using FSO.LotView;
using FSO.LotView.Model;
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
    /// Tests for the "Dynamic Sun" graphics option: the Simitone half
    /// (<see cref="SunCycleSetting"/>) and the mapping onto the renderer
    /// (<see cref="GraphicsPreferences"/> / <see cref="GraphicsOptions"/>).
    ///
    /// <para>
    /// What matters here is narrow and mostly about things that fail silently. The option's
    /// storage is not a <see cref="GlobalSettings"/> property - it is a key in Simitone's own
    /// sidecar ini - so there is a whole class of mistake available that a reflected checkbox
    /// would otherwise make impossible: a row that is not on the page, a row whose property name
    /// no longer matches what the change-notification match looks for, and a resolve rule that
    /// treats a corrupt value as something nobody asked for. Each of those is pinned here,
    /// together with the default - which is <em>on</em>, because in 3D the sun only moves when
    /// this flag is set - and with the fact that the row still turns it back off.
    /// </para>
    ///
    /// <para>
    /// <b>Why this class is in the non-parallel collection.</b> One test in it writes a
    /// process-global. <c>TheChoiceReachesTheFileAndTheLiveConfig</c> assigns
    /// <c>FSOEnvironment.UserDir</c>, <c>WorldConfig.Current.SunCycle</c> and
    /// <c>SunCycleSetting.Enabled</c> so the round trip can run against a throwaway directory
    /// without touching the developer's own <c>simitone.ini</c>. All three are process-wide, and
    /// xUnit runs test <em>classes</em> in parallel by default: any other class reading
    /// <c>UserDir</c>, building a <see cref="WorldConfig"/>, or resolving the sidecar while this
    /// one has them pointed at a temp folder sees the redirect. That is not hypothetical in this
    /// codebase - it is what <c>NonParallelCollection</c> was created for, after
    /// <c>Log_IsRotatedOnceItReachesTwoMebibytes</c> failed intermittently with four lines
    /// instead of one. <c>GlobalMutationGuardTests</c> checks that membership by reflection.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class SunCycleSettingTests
    {
        // ---- the default -------------------------------------------------------------------------

        [Fact]
        public void TheDefaultIsOn()
        {
            Assert.True(SunCycleSetting.DefaultEnabled);
        }

        [Fact]
        public void AFirstRunGetsTheMovingSun()
        {
            // GraphicsOptions.Defaults is what an untouched install is handed, so this is the
            // statement that a 3D lot's sun moves without anybody asking for it. It used to be
            // opt-in; the row is still there to turn it off.
            Assert.True(GraphicsOptions.Defaults.SunCycle);
        }

        [Fact]
        public void TheEngineConfigsOwnFieldDefaultIsStillLegacyLighting()
        {
            // WorldConfig's own literal is not Simitone's to move - tso.world is the engine's - and
            // it is not what decides anything: Simitone writes the flag through
            // GraphicsOptions.Apply at startup, from SunCycleSetting. Pinned so the two defaults
            // being different is a fact rather than a surprise.
            Assert.False(new WorldConfig().SunCycle);
            Assert.Equal(SunLightMode.Legacy, new WorldConfig().SunMode);
        }

        [Fact]
        public void ASettingsFreeSnapshotHasTheSunOn()
        {
            // GraphicsPreferences.From(null) is the defensive path, and the explicit overload has
            // to be able to say the opposite of the default.
            Assert.True(GraphicsPreferences.From(null).SunCycle);
            Assert.False(GraphicsPreferences.From(null, false).SunCycle);
            Assert.True(GraphicsPreferences.From(null, true).SunCycle);
        }

        [Fact]
        public void TheToggleStillTurnsTheSunOff()
        {
            // The default moving must not have made the row decorative: a player who unchecks it
            // gets the legacy sweep, and the snapshot is what carries that to the renderer.
            var config = new WorldConfig();
            GraphicsOptions.Apply(config, GraphicsOptions.Defaults.WithSunCycle(false));

            Assert.False(config.SunCycle);
            Assert.Equal(SunLightMode.Legacy, config.SunMode);

            // and back again, which is what a player who re-checks the box gets
            GraphicsOptions.Apply(config, GraphicsOptions.Defaults);
            Assert.True(config.SunCycle);
            Assert.Equal(SunLightMode.Weather, config.SunMode);
        }

        // ---- reading a value out of the file ------------------------------------------------------

        [Theory]
        [InlineData("true", true)]
        [InlineData("True", true)]
        [InlineData("TRUE", true)]
        [InlineData("  true  ", true)]
        [InlineData("false", false)]
        [InlineData("False", false)]
        [InlineData("FALSE", false)]
        public void ResolveReadsTheObviousValues(string stored, bool expected)
        {
            Assert.Equal(expected, SunCycleSetting.Resolve(stored));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1")]
        [InlineData("yes")]
        [InlineData("on")]
        [InlineData("0")]
        [InlineData("banana")]
        public void ResolveTreatsAnythingElseAsNeverConfigured(string stored)
        {
            // Same garbage-in-known-good-out contract as StartupSettings.ResolveLightingMode: this
            // is a user-editable file and a boolean nobody can predict is worse than the default.
            Assert.Equal(SunCycleSetting.DefaultEnabled, SunCycleSetting.Resolve(stored));
        }

        [Fact]
        public void TheSidecarLivesBesideConfigIni()
        {
            Assert.Equal(Path.Combine("somewhere", SunCycleSetting.FileName),
                SunCycleSetting.ConfigFilePath("somewhere"));
            Assert.NotEqual(StartupSettings.ConfigFileName, SunCycleSetting.FileName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NoUserDirectoryMeansNoPath(string userDir)
        {
            // Null so the result can go straight to File.Exists, which treats null as missing.
            Assert.Null(SunCycleSetting.ConfigFilePath(userDir));
        }

        [Fact]
        public void TheChoiceReachesTheFileAndTheLiveConfig()
        {
            // The round trip, against a throwaway user directory so the developer's own
            // simitone.ini is never touched. FSOEnvironment.UserDir and the renderer config are
            // process globals, so both are put back.
            var originalDir = FSOEnvironment.UserDir;
            var originalConfig = WorldConfig.Current.SunCycle;
            var originalEnabled = SunCycleSetting.Enabled;
            var root = Path.Combine(Path.GetTempPath(), "simitone-suncycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                FSOEnvironment.UserDir = root;
                SunCycleSetting.SetEnabled(true, save: true);

                var path = SunCycleSetting.ConfigFilePath(root);
                Assert.True(File.Exists(path), "the sidecar should have been written");
                var text = File.ReadAllText(path);
                Assert.Contains("# Simitone Settings File", text);
                // IniConfig writes bools with ToString(), so this is "True", not "true" - which
                // Resolve has to accept, and does.
                Assert.Contains(SunCycleSetting.Key + "=True", text);

                Assert.True(SunCycleSetting.Enabled);
                Assert.True(WorldConfig.Current.SunCycle);

                // And back off again, which is the state a player who changed nothing is in.
                SunCycleSetting.SetEnabled(false, save: true);
                Assert.Contains(SunCycleSetting.Key + "=False", File.ReadAllText(path));
                Assert.False(SunCycleSetting.Enabled);
                Assert.False(WorldConfig.Current.SunCycle);
            }
            finally
            {
                FSOEnvironment.UserDir = originalDir;
                WorldConfig.Current.SunCycle = originalConfig;
                SunCycleSetting.SetEnabled(originalEnabled);
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }

        // ---- the mapping onto the renderer --------------------------------------------------------

        [Fact]
        public void ApplyPushesTheChoiceOntoTheLiveConfig()
        {
            var config = new WorldConfig();

            GraphicsOptions.Apply(config, GraphicsOptions.Defaults.WithSunCycle(true));
            Assert.True(config.SunCycle);
            Assert.Equal(SunLightMode.Weather, config.SunMode);

            GraphicsOptions.Apply(config, GraphicsOptions.Defaults.WithSunCycle(false));
            Assert.False(config.SunCycle);
            Assert.Equal(SunLightMode.Legacy, config.SunMode);
        }

        [Fact]
        public void ApplyStillLeavesTheGraphicsModeAlone()
        {
            // Apply mutates in place because Mode and the render offsets live on this instance.
            // Replacing it would silently drop the player back to the default mode, so this
            // guards the one thing a "just make a new one" refactor would break.
            var config = new WorldConfig();
            config.Mode = GlobalGraphicsMode.Full3D;

            GraphicsOptions.Apply(config, GraphicsOptions.Defaults.WithSunCycle(true));

            Assert.Equal(GlobalGraphicsMode.Full3D, config.Mode);
        }

        [Fact]
        public void WithSunCycleChangesNothingElse()
        {
            var before = GraphicsOptions.Defaults;
            var after = before.WithSunCycle(!before.SunCycle);

            Assert.Equal(before.LightingMode, after.LightingMode);
            Assert.Equal(before.AntiAlias, after.AntiAlias);
            Assert.Equal(before.ShadowDetail, after.ShadowDetail);
            Assert.Equal(before.WaterReflections, after.WaterReflections);
            Assert.Equal(before.Weather, after.Weather);
            Assert.Equal(before.DirectionalLight, after.DirectionalLight);
            Assert.Equal(before.ComplexShaders, after.ComplexShaders);
            Assert.Equal(before.EnableTransitions, after.EnableTransitions);
            Assert.Equal(before.SmoothZoom, after.SmoothZoom);
            Assert.Equal(before.SurroundingLots, after.SurroundingLots);
            Assert.NotEqual(before.SunCycle, after.SunCycle);
        }

        [Fact]
        public void ApplyIgnoresANullConfig()
        {
            GraphicsOptions.Apply(null, GraphicsOptions.Defaults.WithSunCycle(true));
        }

        // ---- the Options page row -------------------------------------------------------------------

        [Fact]
        public void TheGraphicsPageOffersTheRow()
        {
            var names = UIGraphicsOptionsSubpanel.GraphicsDescriptors(null)
                .Select(x => x.AttachedProperty?.Name)
                .ToList();

            Assert.Contains(SunCycleSetting.Key, names);
        }

        [Fact]
        public void TheRowIsACheckbox()
        {
            var row = UIGraphicsOptionsSubpanel.GraphicsDescriptors(null)
                .Single(x => x.AttachedProperty?.Name == SunCycleSetting.Key);

            Assert.Equal(UIOptionsSubpanelBase.SettingType.BoolProp, row.Type);
            Assert.NotNull(row.LocalizationInfo);
            Assert.Equal("Dynamic Sun", row.GetTitle());
            Assert.False(string.IsNullOrWhiteSpace(row.GetAboutInfo()));
        }

        [Fact]
        public void TheRowReadsAndWritesTheSunCycleStoreNotTheSettingsObject()
        {
            // The row's storage is Simitone's own, so a change must not land on a
            // GlobalSettings property - and the change notification has to still recognise it, or
            // the page would set it and never write it down.
            var row = UIGraphicsOptionsSubpanel.GraphicsDescriptors(null)
                .Single(x => x.AttachedProperty?.Name == SunCycleSetting.Key);

            Assert.IsType<UIOptionsSubpanelBase.UIExternalBoolPropOptionDescription>(row);
            Assert.True(SunCycleSetting.Describes(row));
            Assert.False(SunCycleSetting.Describes(null));
        }

        [Fact]
        public void TheRowIsFoundByTheNameTheChangeNotificationMatches()
        {
            // Deliberately clumsy, and deliberately so: the page's reflection is built on
            // Type.GetProperty over the descriptor's attached property, so the name is the only
            // stable handle. A rename that missed SunCycleSetting.Key would save nothing and
            // throw nothing.
            var property = typeof(SunCycleOption).GetProperty(SunCycleSetting.Key);

            Assert.NotNull(property);
            Assert.Equal(typeof(bool), property.PropertyType);
        }

        [Fact]
        public void TheOtherGraphicsRowsAreUnaffected()
        {
            // A regression guard on the edit that added the row: the descriptor list is a plain
            // list of literals and one wrong entry silently reorders the page.
            var names = UIGraphicsOptionsSubpanel.GraphicsDescriptors(null)
                .Select(x => x.AttachedProperty?.Name)
                .ToList();

            foreach (var expected in new[]
            {
                "AntiAlias", "EnableTransitions", "Weather", "SmoothZoom", "DirectionalLight3D",
                "ComplexShaders", "TexCompression", "Windowed", "LightingMode", "ShadowDetail",
                "WaterReflections", "GlobalGraphicsMode"
            })
            {
                Assert.Contains(expected, names);
            }
        }

        [Fact]
        public void TheDeadShadowQualityRowIsStillGone()
        {
            // Kept from the pass that added ShadowDetail: this option must not have brought the
            // legacy key back as a carrier, which is the obvious wrong way to avoid a sidecar.
            var names = UIGraphicsOptionsSubpanel.GraphicsDescriptors(null)
                .Select(x => x.AttachedProperty?.Name)
                .ToList();

            Assert.DoesNotContain("ShadowQuality", names);
        }

        // ---- the description -------------------------------------------------------------------------

        [Fact]
        public void TheDescriptionSaysWhatTheSettingDoes()
        {
            var text = SunCycleSetting.DescribeEffect();

            Assert.Contains("sun", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rain", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EveryDescriptorStillHasAUniqueName()
        {
            // A duplicate name would make SunCycleSetting.Describes - and FreeWillSetting's
            // equivalent - match the wrong row.
            var names = UIGraphicsOptionsSubpanel.GraphicsDescriptors(null)
                .Select(x => x.AttachedProperty?.Name)
                .ToList();

            Assert.Equal(names.Count, names.Distinct().Count());
        }

        [Fact]
        public void TheSunRowIsNotMistakenForTheFreeWillRow()
        {
            // Both Simitone-owned settings are recognised by property name, so the two names must
            // not collide: one row being persisted as the other would be a very quiet bug.
            var row = UIGraphicsOptionsSubpanel.GraphicsDescriptors(null)
                .Single(x => SunCycleSetting.Describes(x));

            Assert.True(SunCycleSetting.Describes(row));
            Assert.False(FreeWillSetting.Describes(row));
            Assert.NotEqual(FreeWillSetting.SettingsKey, SunCycleSetting.Key);
        }
    }
}
