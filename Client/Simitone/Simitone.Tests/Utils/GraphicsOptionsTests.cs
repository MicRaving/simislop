using FSO.Client;
using FSO.Common;
using FSO.LotView;
using FSO.LotView.Model;
using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Tests for <see cref="GraphicsOptions"/> and <see cref="GraphicsPreferences"/> - the
    /// mapping from a graphics preference in config.ini to a value the lot renderer can use.
    ///
    /// <para>
    /// This mapping is where two settings that used to be decorative were found.
    /// <c>GlobalSettings.ShadowQuality</c> was on the Options page, was written to the ini, and
    /// was read by nothing: the lot view's shadow resolution was a hard-coded 16 at the light
    /// batch construction site. <c>GlobalSettings.SmoothZoom</c> was the same - written, never
    /// read, because <c>WorldConfig.SmoothZoom</c>'s setter is empty. So the tests here are as
    /// much about "this preference reaches the renderer" as about the arithmetic.
    /// </para>
    ///
    /// <para>
    /// The other half of the file covers the Options page's own descriptors, because a row is
    /// one line of reflection over a property name: rename the property and the row still
    /// compiles, and the only symptom is a control that saves a preference nothing reads.
    /// </para>
    /// </summary>
    public sealed class GraphicsOptionsTests : IDisposable
    {
        private readonly string _tempRoot;

        public GraphicsOptionsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-graphics-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // best effort - a leftover temp directory must never fail the suite
            }
        }

        /// <summary>A settings object backed by its own throwaway ini, never the real one.</summary>
        private GlobalSettings NewSettings(string name = "config.ini")
        {
            return new GlobalSettings(Path.Combine(_tempRoot, name));
        }

        // ---- Shadow detail: the ladder -------------------------------------------------------

        [Theory]
        [InlineData(8, 8)]
        [InlineData(16, 16)]
        [InlineData(24, 24)]
        [InlineData(12, 16)]
        [InlineData(15, 16)]
        [InlineData(17, 24)]
        [InlineData(20, 24)]
        [InlineData(0, 8)]
        [InlineData(-16, 8)]
        [InlineData(2048, 24)]
        [InlineData(int.MaxValue, 24)]
        [InlineData(int.MinValue, 8)]
        public void ResolveShadowDetail_SnapsToTheNearestStepOffered(int stored, int expected)
        {
            Assert.Equal(expected, GraphicsOptions.ResolveShadowDetail(stored));
        }

        /// <summary>
        /// The point of the whole change: a player who touches nothing must get the resolution
        /// the engine was already using. 16 is the literal that used to sit at the
        /// LMapBatch construction site, and it is what the ini key defaults to.
        /// </summary>
        [Fact]
        public void ShadowDetail_DefaultsToTheResolutionTheEngineAlreadyUsed()
        {
            Assert.Equal(16, GraphicsOptions.DefaultShadowDetail);
            Assert.Equal(WorldConfig.DefaultShadowDetail, GraphicsOptions.DefaultShadowDetail);
            Assert.Equal(GraphicsOptions.ShadowDetailMedium, GraphicsOptions.DefaultShadowDetail);
            Assert.Equal(GraphicsOptions.ShadowDetailMedium, NewSettings().ShadowDetail);
        }

        [Fact]
        public void ShadowDetail_RadioConversionIsTheInverseOfItself()
        {
            for (int quality = 0; quality <= 2; quality++)
            {
                var resolution = GraphicsOptions.ShadowDetailFromQuality(quality);
                Assert.Equal(quality, GraphicsOptions.ShadowDetailToQuality(resolution));
            }
        }

        [Fact]
        public void ShadowDetail_RadioConversionSpansTheWholeLadder()
        {
            Assert.Equal(GraphicsOptions.ShadowDetailLow, GraphicsOptions.ShadowDetailFromQuality(0));
            Assert.Equal(GraphicsOptions.ShadowDetailMedium, GraphicsOptions.ShadowDetailFromQuality(1));
            Assert.Equal(GraphicsOptions.ShadowDetailHigh, GraphicsOptions.ShadowDetailFromQuality(2));
        }

        [Fact]
        public void ShadowDetail_RadioConversionClampsAnOutOfRangeIndex()
        {
            Assert.Equal(GraphicsOptions.ShadowDetailLow, GraphicsOptions.ShadowDetailFromQuality(-3));
            Assert.Equal(GraphicsOptions.ShadowDetailHigh, GraphicsOptions.ShadowDetailFromQuality(9));
        }

        [Theory]
        [InlineData(GraphicsOptions.ShadowDetailLow, "Low")]
        [InlineData(GraphicsOptions.ShadowDetailMedium, "Medium")]
        [InlineData(GraphicsOptions.ShadowDetailHigh, "High")]
        [InlineData(9999, "High")]
        public void DescribeShadowDetail_NamesEveryStep(int resolution, string expected)
        {
            Assert.Equal(expected, GraphicsOptions.DescribeShadowDetail(resolution));
        }

        // ---- Water reflections --------------------------------------------------------------

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(7, 2)]
        [InlineData(-1, 0)]
        public void ResolveWaterReflections_ClampsToTheOfferedRange(int stored, int expected)
        {
            Assert.Equal(expected, GraphicsOptions.ResolveWaterReflections(stored));
        }

        /// <summary>
        /// The "On" step has to be the exponent the floor shader was already using, or the
        /// default would quietly change how every pool in the game looks.
        /// </summary>
        [Fact]
        public void WaterReflections_DefaultStepIsTheExponentTheShaderAlreadyUsed()
        {
            Assert.Equal(GraphicsOptions.WaterReflectionsNormal, GraphicsOptions.DefaultWaterReflections);
            Assert.Equal(0.02f, GraphicsOptions.WaterShininessNormal);
            Assert.Equal(GraphicsOptions.WaterShininessNormal, GraphicsOptions.WaterShininessFor(GraphicsOptions.DefaultWaterReflections));
            Assert.Equal(GraphicsOptions.WaterReflectionsNormal, NewSettings().WaterReflections);
        }

        [Fact]
        public void WaterShininess_OffIsZeroAndStrongIsStrongerThanNormal()
        {
            Assert.Equal(0f, GraphicsOptions.WaterShininessFor(GraphicsOptions.WaterReflectionsOff));
            Assert.True(GraphicsOptions.WaterShininessFor(GraphicsOptions.WaterReflectionsStrong)
                > GraphicsOptions.WaterShininessFor(GraphicsOptions.WaterReflectionsNormal));
            // every step has to survive the engine's own clamp, or a step would be a no-op
            Assert.All(
                new[] { GraphicsOptions.WaterReflectionsOff, GraphicsOptions.WaterReflectionsNormal, GraphicsOptions.WaterReflectionsStrong },
                step => Assert.InRange(GraphicsOptions.WaterShininessFor(step), 0f, WorldConfig.MaxWaterShininess));
        }

        [Theory]
        [InlineData(GraphicsOptions.WaterReflectionsOff, "Off")]
        [InlineData(GraphicsOptions.WaterReflectionsNormal, "On")]
        [InlineData(GraphicsOptions.WaterReflectionsStrong, "Strong")]
        [InlineData(42, "Strong")]
        public void DescribeWaterReflections_NamesEveryStep(int quality, string expected)
        {
            Assert.Equal(expected, GraphicsOptions.DescribeWaterReflections(quality));
        }

        // ---- The engine-side clamps ---------------------------------------------------------

        [Fact]
        public void WorldConfig_ShadowDetailIsClampedToWhatTheRendererCanAllocate()
        {
            var config = new WorldConfig();

            Assert.Equal(WorldConfig.DefaultShadowDetail, config.ShadowDetail);

            config.ShadowDetail = 0;
            Assert.Equal(WorldConfig.MinShadowDetail, config.ShadowDetail);

            config.ShadowDetail = -1000;
            Assert.Equal(WorldConfig.MinShadowDetail, config.ShadowDetail);

            config.ShadowDetail = 9999;
            Assert.Equal(WorldConfig.MaxShadowDetail, config.ShadowDetail);

            config.ShadowDetail = 24;
            Assert.Equal(24, config.ShadowDetail);
        }

        /// <summary>
        /// The upper clamp is the interesting one: the largest lighting target is twice the
        /// light map in each axis, and at 64x64 tiles (the largest lot the game has) 32 is
        /// still 4032 - just under the 4096 every DirectX 9-era card guarantees.
        /// </summary>
        [Fact]
        public void WorldConfig_MaxShadowDetailStillFitsTheLargestLotInATexture()
        {
            const int largestLot = 64;
            var largestTarget = (WorldConfig.MaxShadowDetail * (largestLot - 1)) * 2;
            Assert.True(largestTarget <= 4096, $"{largestTarget} exceeds a 4096 texture");
        }

        [Fact]
        public void WorldConfig_WaterShininessIsClampedAndRefusesNaN()
        {
            var config = new WorldConfig();

            Assert.Equal(GraphicsOptions.WaterShininessNormal, config.WaterShininess);

            config.WaterShininess = -1f;
            Assert.Equal(0f, config.WaterShininess);

            config.WaterShininess = float.NaN;
            Assert.Equal(0f, config.WaterShininess);

            config.WaterShininess = 99f;
            Assert.Equal(WorldConfig.MaxWaterShininess, config.WaterShininess);

            config.WaterShininess = 0.06f;
            Assert.Equal(0.06f, config.WaterShininess);
        }

        // ---- ini -> snapshot ----------------------------------------------------------------

        /// <summary>
        /// A brand new config gets FreeSO's ini defaults, and one of those is a value the
        /// renderer cannot use: LightingMode defaults to -1, which is "no lighting at all".
        /// Repairing it here is the difference between a fresh install looking correctly lit
        /// and looking flat.
        /// </summary>
        [Fact]
        public void From_RepairsTheIniDefaultsTheRendererCannotUse()
        {
            var preferences = GraphicsPreferences.From(NewSettings());

            Assert.Equal(StartupSettings.DefaultLightingMode, preferences.LightingMode);
            Assert.Equal(3, preferences.LightingMode);
            Assert.Equal(GraphicsOptions.DefaultShadowDetail, preferences.ShadowDetail);
            Assert.Equal(GraphicsOptions.DefaultWaterReflections, preferences.WaterReflections);
        }

        [Fact]
        public void From_CarriesEveryPreferenceItIsGiven()
        {
            var settings = NewSettings();
            settings.LightingMode = 2;
            settings.AntiAlias = 1;
            settings.ShadowDetail = 8;
            settings.WaterReflections = 2;
            settings.Weather = false;
            settings.DirectionalLight3D = false;
            settings.ComplexShaders = false;
            settings.EnableTransitions = false;
            settings.SmoothZoom = false;
            settings.SurroundingLotMode = 0;

            var preferences = GraphicsPreferences.From(settings);

            Assert.Equal(2, preferences.LightingMode);
            Assert.Equal(1, preferences.AntiAlias);
            Assert.Equal(8, preferences.ShadowDetail);
            Assert.Equal(2, preferences.WaterReflections);
            Assert.False(preferences.Weather);
            Assert.False(preferences.DirectionalLight);
            Assert.False(preferences.ComplexShaders);
            Assert.False(preferences.EnableTransitions);
            Assert.False(preferences.SmoothZoom);
            Assert.Equal(0, preferences.SurroundingLots);
        }

        [Fact]
        public void From_RepairsAHandEditedValue()
        {
            var settings = NewSettings();
            settings.LightingMode = -1;
            settings.AntiAlias = 42;
            settings.ShadowDetail = 3;
            settings.WaterReflections = -5;

            var preferences = GraphicsPreferences.From(settings);

            Assert.Equal(StartupSettings.DefaultLightingMode, preferences.LightingMode);
            Assert.Equal(StartupSettings.MinAntiAlias, preferences.AntiAlias);
            Assert.Equal(GraphicsOptions.ShadowDetailLow, preferences.ShadowDetail);
            Assert.Equal(GraphicsOptions.WaterReflectionsOff, preferences.WaterReflections);
        }

        [Fact]
        public void From_ToleratesNoSettingsObjectAtAll()
        {
            var preferences = GraphicsPreferences.From(null);

            Assert.Equal(GraphicsOptions.Defaults.LightingMode, preferences.LightingMode);
            Assert.Equal(GraphicsOptions.Defaults.ShadowDetail, preferences.ShadowDetail);
            Assert.Equal(GraphicsOptions.Defaults.WaterReflections, preferences.WaterReflections);
        }

        // ---- snapshot -> renderer -----------------------------------------------------------

        [Fact]
        public void Apply_ReachesEveryRendererField()
        {
            var settings = NewSettings();
            settings.LightingMode = 3;
            settings.AntiAlias = 2;
            settings.ShadowDetail = 24;
            settings.WaterReflections = 2;
            settings.Weather = false;
            settings.DirectionalLight3D = false;
            settings.ComplexShaders = false;
            settings.SurroundingLotMode = 1;

            var config = new WorldConfig();
            GraphicsOptions.Apply(config, GraphicsPreferences.From(settings));

            Assert.Equal(3, config.LightingMode);
            Assert.True(config.UltraLighting);
            Assert.Equal(2, config.AA);
            Assert.Equal(24, config.ShadowDetail);
            Assert.Equal(GraphicsOptions.WaterShininessStrong, config.WaterShininess);
            Assert.False(config.Weather);
            Assert.False(config.Directional);
            Assert.False(config.Complex);
            Assert.Equal(1, config.SurroundingLots);
        }

        /// <summary>
        /// The render targets are allocated from the resolution the light batch is built with,
        /// so this is the assertion that the Shadows row is not decorative any more: before,
        /// the light batch was always constructed with a literal 16 no matter what the ini
        /// said.
        /// </summary>
        [Theory]
        [InlineData(8, 8)]
        [InlineData(16, 16)]
        [InlineData(24, 24)]
        [InlineData(0, 8)]
        [InlineData(4096, 24)]
        public void Apply_LandsTheShadowResolutionOnTheRenderer(int stored, int expected)
        {
            var settings = NewSettings();
            settings.ShadowDetail = stored;

            var config = new WorldConfig();
            GraphicsOptions.Apply(config, GraphicsPreferences.From(settings));

            Assert.Equal(expected, config.ShadowDetail);
        }

        [Fact]
        public void Apply_LeavesTheGraphicsModeAlone()
        {
            var config = new WorldConfig { Mode = GlobalGraphicsMode.Full3D };

            GraphicsOptions.Apply(config, GraphicsPreferences.From(NewSettings()));

            // WorldConfig.Current is mutated rather than replaced precisely so this survives;
            // a fresh instance would drop the player back to the default mode.
            Assert.Equal(GlobalGraphicsMode.Full3D, config.Mode);
        }

        [Fact]
        public void Apply_ToleratesNoRendererConfig()
        {
            GraphicsOptions.Apply(null, GraphicsPreferences.From(NewSettings()));
        }

        /// <summary>
        /// <c>WorldConfig.EnableTransitions</c> forces itself off unless 3D is enabled, so the
        /// contract is that the preference is written through the property - which is what the
        /// startup path used to skip. It only shows up on a <c>-3d</c> launch (a normal
        /// Simitone run leaves <c>FSOEnvironment.Enable3D</c> false, and the property wins),
        /// but skipping it meant the stored preference never reached the renderer at all.
        /// </summary>
        [Fact]
        public void Apply_WritesTransitionsThroughTheProperty()
        {
            var original = FSOEnvironment.Enable3D;
            try
            {
                FSOEnvironment.Enable3D = true;

                var on = new WorldConfig();
                GraphicsOptions.Apply(on, GraphicsPreferences.From(NewSettings()));
                Assert.True(on.EnableTransitions);

                var settings = NewSettings();
                settings.EnableTransitions = false;
                var off = new WorldConfig();
                GraphicsOptions.Apply(off, GraphicsPreferences.From(settings));
                Assert.False(off.EnableTransitions);
            }
            finally
            {
                FSOEnvironment.Enable3D = original;
            }
        }

        /// <summary>
        /// <c>WorldConfig.SmoothZoom</c>'s setter is empty in FreeSO and its getter reads the
        /// transitions flag, so there is no engine field for the ini's <c>SmoothZoom</c> at all:
        /// Simitone eases its own 2d zoom off its own target. The preference is still carried in
        /// the snapshot, and this is the reminder that <see cref="GraphicsOptions.Apply"/> must
        /// not come to depend on the write.
        /// </summary>
        [Fact]
        public void Apply_DoesNotWriteTheEngineSmoothZoomFlag()
        {
            var original = FSOEnvironment.Enable3D;
            try
            {
                FSOEnvironment.Enable3D = true;

                var settings = NewSettings();
                settings.EnableTransitions = true;
                settings.SmoothZoom = false;

                var preferences = GraphicsPreferences.From(settings);
                var config = new WorldConfig();
                GraphicsOptions.Apply(config, preferences);

                Assert.False(preferences.SmoothZoom);
                Assert.True(config.SmoothZoom); // reads the transitions flag, not the preference
            }
            finally
            {
                FSOEnvironment.Enable3D = original;
            }
        }

        // ---- The Options page's own rows ----------------------------------------------------

        private static List<UIOptionsSubpanelBase.UIOptionDescriprion> GraphicsRows(GlobalSettings settings)
        {
            return UIGraphicsOptionsSubpanel.GraphicsDescriptors(settings).ToList();
        }

        /// <summary>
        /// Every row is one reflection over a property name, so a renamed or retyped property
        /// shows up at runtime as a row that does nothing. This is the check that turns that
        /// into a test failure.
        /// </summary>
        /// <remarks>
        /// One kind of row is exempt from the "declared on GlobalSettings" half, and the exemption
        /// is checked rather than assumed: a setting Simitone owns itself has no
        /// <c>GlobalSettings</c> property to point at (<c>FSO.UI.GlobalSettings</c> is not
        /// Simitone's to extend), so it is one of the external descriptors, which keep a property
        /// only as a name and read and write through delegates. What is asserted for those rows is
        /// the property they carry and the marker interface that says the delegates are the ones
        /// in use - a row that has the name but not the marker would be reflecting against a
        /// <c>GlobalSettings</c> instance it has no business touching. The marker rather than one
        /// concrete descriptor type, because there is more than one of them and a list is exactly
        /// the thing that stops being updated.
        /// </remarks>
        [Fact]
        public void EveryGraphicsRowPointsAtARealGlobalSettingsProperty()
        {
            var rows = GraphicsRows(NewSettings());

            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                Assert.NotNull(row.AttachedProperty);
                Assert.True(row.AttachedProperty.PropertyType == typeof(bool) || row.AttachedProperty.PropertyType == typeof(int),
                    $"{row.AttachedProperty.Name} is neither a bool nor an int, so no row type can drive it");

                if (row is UIOptionsSubpanelBase.IUIExternalPropOptionDescription)
                {
                    continue;
                }
                Assert.True(row.AttachedProperty.DeclaringType == typeof(GlobalSettings),
                    $"{row.AttachedProperty.Name} is not a GlobalSettings property");
            }
        }

        /// <summary>
        /// Every row that does not point at a <c>GlobalSettings</c> property has to say so in its
        /// own type, so the exemption above can never quietly widen.
        /// </summary>
        [Fact]
        public void EveryRowOutsideGlobalSettingsIsAnExternalOne()
        {
            foreach (var row in GraphicsRows(NewSettings()))
            {
                if (row.AttachedProperty.DeclaringType == typeof(GlobalSettings))
                {
                    Assert.False(row is UIOptionsSubpanelBase.IUIExternalPropOptionDescription,
                        $"{row.AttachedProperty.Name} is a GlobalSettings property and should use the reflected row");
                    continue;
                }
                Assert.IsAssignableFrom<UIOptionsSubpanelBase.IUIExternalPropOptionDescription>(row);
            }
        }

        [Fact]
        public void TheGraphicsPageExposesTheTwoNewLightingRows()
        {
            var properties = GraphicsRows(NewSettings()).Select(x => x.AttachedProperty.Name).ToList();

            Assert.Contains("ShadowDetail", properties);
            Assert.Contains("WaterReflections", properties);
        }

        /// <summary>
        /// The row this replaces saved 512/1024/2048 into a key no renderer code read. It is
        /// kept out of the page rather than left as a working-looking control.
        /// </summary>
        [Fact]
        public void TheDeadShadowQualityRowIsGone()
        {
            var properties = GraphicsRows(NewSettings()).Select(x => x.AttachedProperty.Name).ToList();

            Assert.DoesNotContain("ShadowQuality", properties);
            // and the key it used is still on the settings object, so nothing else that reads
            // it can be broken by its absence here.
            Assert.NotNull(typeof(GlobalSettings).GetProperty("ShadowQuality"));
        }

        [Theory]
        [InlineData(0, GraphicsOptions.ShadowDetailLow)]
        [InlineData(1, GraphicsOptions.ShadowDetailMedium)]
        [InlineData(2, GraphicsOptions.ShadowDetailHigh)]
        public void TheShadowRowWritesTheResolutionTheRendererIsGiven(int radio, int expected)
        {
            var settings = NewSettings();
            var row = GraphicsRows(settings).OfType<UIOptionsSubpanelBase.UIIntPropOptionDescription>()
                .Single(x => x.AttachedProperty.Name == "ShadowDetail");

            // reading: the stored resolution has to come back as the radio that produced it
            settings.ShadowDetail = expected;
            Assert.Equal(radio, row.GetValue(settings));

            // writing: clicking the radio has to store a resolution the renderer accepts
            row.SetValue(settings, radio);
            Assert.Equal(expected, settings.ShadowDetail);
        }

        /// <summary>
        /// The row always has exactly one button selected, whatever the ini says: a value it
        /// cannot show snaps onto the ladder first, and a value below the ladder snaps down.
        /// </summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(8, 0)]
        [InlineData(16, 1)]
        [InlineData(20, 2)]
        [InlineData(24, 2)]
        [InlineData(9999, 2)]
        public void TheShadowRowShowsTheStepAStoredValueSnapsTo(int stored, int expectedRadio)
        {
            var settings = NewSettings();
            var row = GraphicsRows(settings).OfType<UIOptionsSubpanelBase.UIIntPropOptionDescription>()
                .Single(x => x.AttachedProperty.Name == "ShadowDetail");

            settings.ShadowDetail = stored;
            Assert.Equal(expectedRadio, row.GetValue(settings));
        }

        [Fact]
        public void TheWaterRowRoundTripsItsThreeSteps()
        {
            var settings = NewSettings();
            var row = GraphicsRows(settings).OfType<UIOptionsSubpanelBase.UIIntPropOptionDescription>()
                .Single(x => x.AttachedProperty.Name == "WaterReflections");

            for (int step = GraphicsOptions.MinWaterReflections; step <= GraphicsOptions.MaxWaterReflections; step++)
            {
                row.SetValue(settings, step);
                Assert.Equal(step, settings.WaterReflections);
                Assert.Equal(step, row.GetValue(settings));
            }
        }

        [Fact]
        public void TheLightingRowNamesAllFourOfItsSteps()
        {
            var row = GraphicsRows(NewSettings()).OfType<UIOptionsSubpanelBase.UIIntPropOptionDescription>()
                .Single(x => x.AttachedProperty.Name == "LightingMode");

            var captions = Enumerable.Range(row.LowBound, row.Highbound - row.LowBound + 1)
                .Select(row.GetValueTooltip)
                .ToList();

            // four steps, and the middle two are different features, so they cannot both be
            // captioned "Medium"
            Assert.Equal(4, captions.Count);
            Assert.Equal(4, captions.Distinct().Count());
            Assert.Contains("Ultra", captions);
        }

        [Fact]
        public void TheWaterRowNamesItsStepsAndCarriesItsOwnCaption()
        {
            var row = GraphicsRows(NewSettings()).OfType<UIOptionsSubpanelBase.UIIntPropOptionDescription>()
                .Single(x => x.AttachedProperty.Name == "WaterReflections");

            var captions = Enumerable.Range(row.LowBound, row.Highbound - row.LowBound + 1)
                .Select(row.GetValueTooltip)
                .ToList();

            Assert.Equal(new[] { "Off", "On", "Strong" }, captions);

            // TS1 has no UI text for a setting TS1 never had, so the row supplies its own; the
            // lookup has to fall through to it rather than to the property name or nothing.
            Assert.Equal("Water Reflections", row.GetTitle());
            Assert.NotNull(row.GetAboutInfo());
            Assert.Contains("Water Reflections", row.GetAboutInfo());
        }

        [Fact]
        public void ARowWithNoTextAtAllGetsNoTooltip()
        {
            var row = new UIOptionsSubpanelBase.UIIntPropOptionDescription(typeof(GlobalSettings).GetProperty("ShadowDetail"));
            Assert.Null(row.GetAboutInfo());
        }

        /// <summary>
        /// A value group with no per-step captions still has to caption its buttons, and
        /// <c>GetValueTooltip</c> is what both radio builders call. Checked here because the
        /// default (Low/Medium/High) is what every pre-existing row relies on.
        /// </summary>
        [Fact]
        public void AValueGroupWithoutItsOwnCaptionsFallsBackToLowMediumHigh()
        {
            var row = new UIOptionsSubpanelBase.UIIntPropOptionDescription(typeof(GlobalSettings).GetProperty("ShadowDetail"))
            {
                LowTooltipText = "Low",
                MediumTooltipText = "Medium",
                HighTooltipText = "High"
            };

            Assert.Equal("Low", row.GetValueTooltip(row.LowBound));
            Assert.Equal("Medium", row.GetValueTooltip(row.LowBound + 1));
            Assert.Equal("High", row.GetValueTooltip(row.Highbound));
        }

        [Fact]
        public void TheGraphicsModeRowStillRoundTripsItsEnum()
        {
            var settings = NewSettings();
            var row = GraphicsRows(settings).OfType<UIOptionsSubpanelBase.UIIntPropOptionDescription>()
                .Single(x => x.AttachedProperty.Name == "GlobalGraphicsMode");

            foreach (GlobalGraphicsMode mode in new[] { GlobalGraphicsMode.Full2D, GlobalGraphicsMode.Hybrid2D, GlobalGraphicsMode.Full3D })
            {
                row.SetValue(settings, (int)mode);
                Assert.Equal((int)mode, (int)settings.GlobalGraphicsMode);
                Assert.Equal((int)mode, row.GetValue(settings));
            }
        }

        [Fact]
        public void TheWaterRowIsAKnownPropertyOfTheEngineSettings()
        {
            // the descriptor is reflection over GlobalSettings; if the property ever goes away
            // the row cannot be built at all, and this names the dependency from the other end
            var property = typeof(GlobalSettings).GetProperty("WaterReflections");
            Assert.NotNull(property);
            Assert.Equal(typeof(int), property.PropertyType);
            Assert.Contains("WaterReflections", NewSettings().DefaultValues.Keys);
        }

        /// <summary>
        /// The ini defaults are what a config that predates the keys resolves to, so they are
        /// the defaults for every existing player, not just a new install. They have to be the
        /// values the renderer was already using.
        /// </summary>
        [Fact]
        public void TheNewKeysDefaultToWhatTheRendererAlreadyUsed()
        {
            var defaults = NewSettings().DefaultValues;

            Assert.Equal("16", defaults["ShadowDetail"]);
            Assert.Equal("1", defaults["WaterReflections"]);
        }

        // ---- Names --------------------------------------------------------------------------

        [Theory]
        [InlineData(0, "Off")]
        [InlineData(1, "Basic")]
        [InlineData(2, "+ Shadows")]
        [InlineData(3, "Ultra")]
        [InlineData(-1, "Ultra")]
        public void DescribeLightingMode_NamesEveryStep(int mode, string expected)
        {
            Assert.Equal(expected, GraphicsOptions.DescribeLightingMode(mode));
        }

        [Theory]
        [InlineData(0, "Off")]
        [InlineData(1, "1x")]
        [InlineData(2, "2x")]
        [InlineData(9, "Off")]
        public void DescribeAntiAlias_NamesEveryStep(int level, string expected)
        {
            Assert.Equal(expected, GraphicsOptions.DescribeAntiAlias(level));
        }

        [Fact]
        public void TheSettingsStripAgreesWithTheOptionsPageAboutEveryName()
        {
            // two places describe the same steps; if they drift, the strip and the page will
            // disagree about what a stored value means
            Assert.Equal(GraphicsOptions.DescribeLightingMode(2), UISettingsPanel.DescribeLightingMode(2));
            Assert.Equal(GraphicsOptions.DescribeAntiAlias(1), UISettingsPanel.DescribeAntiAlias(1));
        }
    }
}
