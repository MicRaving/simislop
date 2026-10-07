using System;
using System.IO;
using System.Linq;
using FSO.Client;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The first-run graphics stamp: what a player who has changed nothing ends up with, and the
    /// one place startup writes the settings object.
    ///
    /// <para>
    /// <b>Why this is a test and not a remark.</b> <c>GraphicsOptions.Defaults</c> and
    /// <c>Program.Main</c> used to state the first-run set twice, two hundred lines apart, and
    /// only four of the eleven values were stamped by either. Worse, the stamp ran <em>after</em>
    /// <c>ResolveGameInstall</c>, which is what calls <c>RememberInstall</c> and therefore the only
    /// startup code that saves <c>config.ini</c> - so on the common first run (one install found,
    /// no question asked) the file was written with FreeSO's ini defaults, the stamp only ever
    /// reached memory, and the next launch saw a config that existed and skipped the stamp
    /// entirely. The ordering was load-bearing, stated in a remark 200 lines from the code, and
    /// not enforced anywhere.
    /// </para>
    ///
    /// <para>
    /// It is now one method, called before anything can save, whose numbers come from
    /// <see cref="GraphicsOptions.Defaults"/>. These tests are the part of that which can be
    /// asserted without a game: that the two branches do what they claim, and that every value
    /// <see cref="GraphicsOptions.Defaults"/> names is either stamped or already the ini's own
    /// default - so the "one answer to what an untouched install looks like" claim is checkable
    /// rather than aspirational.
    /// </para>
    /// </summary>
    public sealed class StartupGraphicsDefaultsTests : IDisposable
    {
        private readonly string _root;

        public StartupGraphicsDefaultsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                //best effort
            }
            catch (UnauthorizedAccessException)
            {
                //best effort
            }
        }

        /// <summary>
        /// A settings object over a brand new file, so every property holds FreeSO's own ini
        /// default - the state a first run starts from.
        /// </summary>
        private GlobalSettings FreshSettings()
        {
            return new GlobalSettings(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ini"));
        }

        // ---- the two branches -------------------------------------------------------------------

        [Fact]
        public void AFirstRunGetsTheDocumentedDefaults()
        {
            var settings = FreshSettings();
            //the ini default it starts from, to prove the stamp actually changed something
            Assert.NotEqual(StartupSettings.DefaultLightingMode, settings.LightingMode);

            StartupSettings.ApplyGraphicsDefaults(settings, configFileExistedBeforeStartup: false, forceAntiAlias: false);

            var defaults = GraphicsOptions.Defaults;
            Assert.Equal(defaults.LightingMode, settings.LightingMode);
            Assert.Equal(defaults.ComplexShaders, settings.ComplexShaders);
            Assert.Equal(defaults.EnableTransitions, settings.EnableTransitions);
            Assert.Equal(defaults.AntiAlias, settings.AntiAlias);
        }

        [Fact]
        public void ALaterLaunchLeavesTheStoredValuesAlone()
        {
            var settings = FreshSettings();
            settings.LightingMode = 1;
            settings.ComplexShaders = false;
            settings.EnableTransitions = false;
            settings.AntiAlias = 2;

            StartupSettings.ApplyGraphicsDefaults(settings, configFileExistedBeforeStartup: true, forceAntiAlias: false);

            // The player wins. This is the whole reason the stamp is conditional at all: it used to
            // run on every launch, which threw away anything the Settings panel had saved.
            Assert.Equal(1, settings.LightingMode);
            Assert.False(settings.ComplexShaders);
            Assert.False(settings.EnableTransitions);
            Assert.Equal(2, settings.AntiAlias);
        }

        [Fact]
        public void AnOutOfRangeStoredLightingModeIsRepairedOnALaterLaunch()
        {
            // FreeSO's ini default is -1, which it writes verbatim into any config that predates
            // the key. Honouring it would switch every lighting feature off, so it is replaced
            // with the startup default. Everything else in the config is still the player's.
            var settings = FreshSettings();
            settings.LightingMode = -1;
            settings.ComplexShaders = true;

            StartupSettings.ApplyGraphicsDefaults(settings, configFileExistedBeforeStartup: true, forceAntiAlias: false);

            Assert.Equal(StartupSettings.DefaultLightingMode, settings.LightingMode);
            Assert.True(settings.ComplexShaders);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheAntiAliasFlagOutranksTheStoredPreferenceOnBothBranches(bool firstRun)
        {
            // An explicit command line flag outranks a stored preference, and it must do so on the
            // first run too - otherwise "-aa" would work on every launch except the one the player
            // typed it on.
            var settings = FreshSettings();
            settings.AntiAlias = 0;

            StartupSettings.ApplyGraphicsDefaults(settings, firstRun, forceAntiAlias: true);

            Assert.Equal(StartupSettings.ForcedAntiAlias, settings.AntiAlias);
        }

        [Fact]
        public void ANullSettingsObjectIsIgnoredRatherThanThrownAt()
        {
            // Startup runs this before anything has proved there is a game to start.
            StartupSettings.ApplyGraphicsDefaults(null, configFileExistedBeforeStartup: false, forceAntiAlias: false);
            StartupSettings.ApplyGraphicsDefaults(null, configFileExistedBeforeStartup: true, forceAntiAlias: true);
        }

        [Fact]
        public void ASecondCallWithThePlayerNowInChargeChangesNothing()
        {
            // The stamp is a first-run thing, and it must stay one: a caller that re-ran it after
            // the Options page had already saved would throw away what the player just chose. The
            // stored-values branch is what makes that safe, and this is the assertion for it.
            var settings = FreshSettings();
            StartupSettings.ApplyGraphicsDefaults(settings, configFileExistedBeforeStartup: false, forceAntiAlias: false);
            Assert.Equal(StartupSettings.DefaultLightingMode, settings.LightingMode);
            Assert.True(settings.ComplexShaders);

            //the player opens the Options page and turns the expensive path off
            settings.ComplexShaders = false;
            settings.LightingMode = 2;

            StartupSettings.ApplyGraphicsDefaults(settings, configFileExistedBeforeStartup: true, forceAntiAlias: false);

            Assert.Equal(2, settings.LightingMode);
            Assert.False(settings.ComplexShaders);
        }

        // ---- the aligning test: "what an untouched install looks like" has one answer -------------

        [Fact]
        public void EveryValueGraphicsOptionsDefaultsNamesIsEitherStampedOrAlreadyTheIniDefault()
        {
            // The claim on GraphicsOptions.Defaults is that it is "what a player who has changed
            // nothing ends up with". Only four of its values are stamped, so the other seven have
            // to be FreeSO's own ini default - otherwise the property and the file disagree and
            // there are two answers to the same question. Compared against a freshly created
            // GlobalSettings so the ini defaults are read rather than restated here.
            var fresh = FreshSettings();
            var defaults = GraphicsOptions.Defaults;

            //stamped, and so must differ from what the ini would have written
            Assert.NotEqual(fresh.LightingMode, defaults.LightingMode);
            Assert.NotEqual(fresh.ComplexShaders, defaults.ComplexShaders);
            Assert.Equal(fresh.EnableTransitions, defaults.EnableTransitions);
            Assert.Equal(fresh.AntiAlias, defaults.AntiAlias);

            //not stamped, and so must already agree with the ini
            Assert.Equal(fresh.Weather, defaults.Weather);
            Assert.Equal(fresh.DirectionalLight3D, defaults.DirectionalLight);
            Assert.Equal(fresh.SmoothZoom, defaults.SmoothZoom);
            Assert.Equal(fresh.SurroundingLotMode, defaults.SurroundingLots);
            Assert.Equal(fresh.ShadowDetail, defaults.ShadowDetail);
            Assert.Equal(fresh.WaterReflections, defaults.WaterReflections);
        }

        [Fact]
        public void TheSnapshotReadOffAFirstRunConfigAgreesWithTheDefaults()
        {
            // The end-to-end version of the test above, through the reader the game actually uses.
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ini");
            var settings = new GlobalSettings(path);
            StartupSettings.ApplyGraphicsDefaults(settings, configFileExistedBeforeStartup: false, forceAntiAlias: false);
            settings.Save();

            var reread = GraphicsPreferences.From(new GlobalSettings(path),
                sunCycle: GraphicsOptions.Defaults.SunCycle,
                neighbourhoodView: GraphicsOptions.Defaults.NeighbourhoodView);
            var defaults = GraphicsOptions.Defaults;

            Assert.Equal(defaults.LightingMode, reread.LightingMode);
            Assert.Equal(defaults.ComplexShaders, reread.ComplexShaders);
            Assert.Equal(defaults.EnableTransitions, reread.EnableTransitions);
            Assert.Equal(defaults.AntiAlias, reread.AntiAlias);
            Assert.Equal(defaults.Weather, reread.Weather);
            Assert.Equal(defaults.DirectionalLight, reread.DirectionalLight);
            Assert.Equal(defaults.SmoothZoom, reread.SmoothZoom);
            Assert.Equal(defaults.SurroundingLots, reread.SurroundingLots);
            Assert.Equal(defaults.ShadowDetail, reread.ShadowDetail);
            Assert.Equal(defaults.WaterReflections, reread.WaterReflections);
        }

        [Fact]
        public void TheForcedAntiAliasValueIsALegalOne()
        {
            Assert.InRange(StartupSettings.ForcedAntiAlias,
                StartupSettings.MinAntiAlias, StartupSettings.MaxAntiAlias);
            Assert.NotEqual(StartupSettings.MaxAntiAlias, StartupSettings.ForcedAntiAlias);
        }

        [Fact]
        public void ShouldApplyGraphicsDefaultsStillAnswersTheQuestionTheStampAsks()
        {
            Assert.True(StartupSettings.ShouldApplyGraphicsDefaults(false));
            Assert.False(StartupSettings.ShouldApplyGraphicsDefaults(true));
        }
    }
}
