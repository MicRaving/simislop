using Simitone.Client.Utils;
using System.IO;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Simitone used to overwrite the saved graphics settings on every launch, which made the
    /// in-game Settings panel unable to persist anything. These tests pin the rule that the
    /// hardcoded first-run defaults are only written when no config file existed beforehand.
    ///
    /// Everything here is pure: the helper never touches <c>GlobalSettings.Default</c> (whose
    /// accessor resolves a path from <c>FSOEnvironment.UserDir</c> and performs IO) and never
    /// touches the filesystem, so the suite stays headless.
    /// </summary>
    public sealed class StartupSettingsTests
    {
        [Fact]
        public void AppliesGraphicsDefaultsOnAFirstRun()
        {
            Assert.True(StartupSettings.ShouldApplyGraphicsDefaults(configFileExistedBeforeStartup: false));
        }

        [Fact]
        public void LeavesSavedGraphicsAloneOnEveryLaterRun()
        {
            Assert.False(StartupSettings.ShouldApplyGraphicsDefaults(configFileExistedBeforeStartup: true));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void ShouldApplyGraphicsDefaultsIsExactlyTheInverseOfTheFlag(bool existed, bool expected)
        {
            Assert.Equal(expected, StartupSettings.ShouldApplyGraphicsDefaults(existed));
        }

        [Theory]
        [InlineData(@"C:\Users\sims\Documents\Simitone/")]
        [InlineData("C:/Users/sims/Documents/Simitone/")]
        [InlineData("relative/user/dir/")]
        [InlineData("no-trailing-slash")]
        public void ConfigFilePathCombinesTheUserDirWithConfigIni(string userDir)
        {
            Assert.Equal(Path.Combine(userDir, "config.ini"), StartupSettings.ConfigFilePath(userDir));
        }

        [Fact]
        public void ConfigFilePathMatchesTheNameGlobalSettingsUses()
        {
            // GlobalSettings.Default is built as Path.Combine(FSOEnvironment.UserDir, "config.ini"),
            // so probing any other file would always report "first run" and clobber the config.
            Assert.Equal("config.ini", StartupSettings.ConfigFileName);
            Assert.EndsWith("config.ini", StartupSettings.ConfigFilePath("some/dir/"));
        }

        [Fact]
        public void ConfigFilePathReturnsNullForANullUserDir()
        {
            Assert.Null(StartupSettings.ConfigFilePath(null));
        }

        [Fact]
        public void ConfigFilePathReturnsNullForAnEmptyUserDir()
        {
            Assert.Null(StartupSettings.ConfigFilePath(""));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 3)]
        [InlineData(-1, 3)]
        [InlineData(int.MinValue, 3)]
        public void ResolveLightingModeFallsBackToTheStartupDefaultOutsideTheLegalRange(int stored, int expected)
        {
            // FreeSO ships LightingMode with an ini default of -1 and writes that value out
            // verbatim into configs that predate the key. Honouring it would switch every
            // lighting feature off now that startup no longer overwrites the setting.
            Assert.Equal(expected, StartupSettings.ResolveLightingMode(stored));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 0)]
        [InlineData(-1, 0)]
        public void ResolveAntiAliasClampsToOffOutsideTheLegalRange(int stored, int expected)
        {
            Assert.Equal(expected, StartupSettings.ResolveAntiAlias(stored));
        }

        [Fact]
        public void BothResolversAreTotalAndAlwaysAnswerWithSomethingTheRendererAccepts()
        {
            // The contract the two siblings share, asserted over inputs no player can produce:
            // whatever is in the file, the answer is in range, and nothing throws. Which end of
            // the range the garbage collapses to differs on purpose - lighting falls back to the
            // default, antialiasing to off - and that difference is documented on each method
            // rather than pinned here, so neither can drift into the other by accident.
            foreach (var stored in new[] { int.MinValue, -1, 0, 1, 2, 3, 4, int.MaxValue })
            {
                var lighting = StartupSettings.ResolveLightingMode(stored);
                Assert.InRange(lighting, StartupSettings.MinLightingMode, StartupSettings.MaxLightingMode);

                var antiAlias = StartupSettings.ResolveAntiAlias(stored);
                Assert.InRange(antiAlias, StartupSettings.MinAntiAlias, StartupSettings.MaxAntiAlias);
            }
        }

        [Fact]
        public void TheResolversCollapseToOppositeEndsOnPurpose()
        {
            // Stated as a fact rather than left as folklore, because the temptation to "make these
            // consistent" is exactly what would break the lighting case: -1 is FreeSO's own ini
            // default and means "off", so it has to be replaced, while a corrupt antialiasing
            // level has no such reading and the cheapest answer is the safe one.
            var garbage = 99;

            Assert.Equal(StartupSettings.DefaultLightingMode, StartupSettings.ResolveLightingMode(garbage));
            Assert.Equal(StartupSettings.MinAntiAlias, StartupSettings.ResolveAntiAlias(garbage));
        }
    }
}
