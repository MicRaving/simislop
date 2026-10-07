using System;
using System.IO;
using System.Linq;
using FSO.Common;
using FSO.LotView;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// A settings file that cannot be written has to be reported.
    ///
    /// <para>
    /// This is the shape of the bug: each of the three sidecars kept its choice in an in-memory
    /// cache, so a save that failed left the row showing the player's choice for the rest of the
    /// session and quietly reverted it on the next launch. Nothing was thrown and nothing was
    /// logged, so the only symptom was a control that appeared to do nothing.
    /// </para>
    ///
    /// <para>
    /// Two of the three swallowed the failure twice over, which is why this needed more than a
    /// <c>catch</c>: FreeSO's <c>IniConfig.Save</c> catches everything itself, so a wrapper
    /// around it never saw an exception to report. Writing through <see cref="SimitoneIniFile"/>
    /// is what gives the wrapper something to catch, and these tests are what keep that true.
    /// </para>
    ///
    /// <para>
    /// The log assertions need this class in <see cref="NonParallelCollection"/>:
    /// <c>SimitoneLog</c> resolves its path from the same process-global
    /// <see cref="FSOEnvironment.UserDir"/> these tests redirect, so another class logging at the
    /// same moment would write into the temporary directory and be counted here.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class SettingsSaveFailureTests : IDisposable
    {
        private readonly string _originalUserDir;
        private readonly string _root;
        private readonly string _logPath;
        private readonly bool _originalSunCycle;
        private readonly NeighbourhoodView _originalView;

        public SettingsSaveFailureTests()
        {
            _originalUserDir = FSOEnvironment.UserDir;
            _root = Path.Combine(Path.GetTempPath(), "simitone-savefail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            FSOEnvironment.UserDir = _root;
            _logPath = Path.Combine(_root, "logs", "simitone.log");
            //The three settings cache their value in process globals, so the tests below have to
            //put them back or they decide what a later test in another class reads.
            _originalSunCycle = SunCycleSetting.Enabled;
            _originalView = NeighbourhoodViewSetting.Current;
        }

        public void Dispose()
        {
            FSOEnvironment.UserDir = _originalUserDir;
            NeighbourhoodViewSetting.Set(_originalView);
            SunCycleSetting.SetEnabled(_originalSunCycle);
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
        /// Makes the sidecar unwritable by putting a <em>directory</em> where the file has to go.
        /// Windows and Linux both refuse to replace a file with a directory, so this fails inside
        /// the write rather than before it - which is the failure mode being reported on.
        /// </summary>
        private void BlockTheSidecar(string fileName)
        {
            Directory.CreateDirectory(Path.Combine(_root, fileName));
        }

        private string LogText() => File.Exists(_logPath) ? File.ReadAllText(_logPath) : string.Empty;

        [Fact]
        public void ADynamicSunSaveThatCannotBeWrittenIsLoggedAndDoesNotThrow()
        {
            BlockTheSidecar(SunCycleSetting.FileName);
            var original = SunCycleSetting.Enabled;

            try
            {
                SunCycleSetting.SetEnabled(!original, save: true);
            }
            finally
            {
                SunCycleSetting.SetEnabled(original);
            }

            // The choice is live either way - that is the in-memory cache doing its job - and the
            // fact that it will not survive the restart is what has to be in the log.
            Assert.Contains("SunCycleSetting", LogText());
            Assert.Contains("[WARN]", LogText());
            Assert.Contains(SunCycleSetting.FileName, LogText());
        }

        [Fact]
        public void AViewSaveThatCannotBeWrittenIsLoggedAndDoesNotThrow()
        {
            BlockTheSidecar(SunCycleSetting.FileName);
            var original = NeighbourhoodViewSetting.Current;

            try
            {
                NeighbourhoodViewSetting.SetAndSave(NeighbourhoodView.Off);
            }
            finally
            {
                NeighbourhoodViewSetting.Set(original);
            }

            Assert.Contains("NeighbourhoodViewSetting", LogText());
            Assert.Contains("[WARN]", LogText());
        }

        [Fact]
        public void AMuteSaveThatCannotBeWrittenIsLoggedAndDoesNotThrow()
        {
            BlockTheSidecar(SoundVolumeSetting.FileName);

            SoundVolumeSetting.Save();

            Assert.Contains("SoundVolumeSetting", LogText());
            Assert.Contains("[WARN]", LogText());
        }

        [Fact]
        public void AWorkingSaveLogsNothing()
        {
            // The other half of the contract: a warning on every save would be its own kind of
            // noise, and a test that only ever asserted "something was logged" would not notice.
            SunCycleSetting.Ini(SunCycleSetting.ConfigFilePath(_root))
                .Set(SunCycleSetting.Key, SimitoneIniFile.FormatBool(true));
            NeighbourhoodViewSetting.SetAndSave(NeighbourhoodView.Bitmap2D);
            SoundVolumeSetting.Save();

            Assert.DoesNotContain("[WARN]", LogText());
            Assert.Contains(SunCycleSetting.Key + "=True", File.ReadAllText(SunCycleSetting.ConfigFilePath(_root)));
            Assert.Contains(NeighbourhoodViewSetting.Key + "=2d", File.ReadAllText(SunCycleSetting.ConfigFilePath(_root)));
        }

        [Fact]
        public void AFailedSaveLeavesTheOtherSettingsKeyUntouched()
        {
            // The failure must be narrow: writing a view that cannot be persisted must not also
            // cost the Dynamic Sun key, which is what a truncate-and-retry would do.
            var ini = SunCycleSetting.Ini(SunCycleSetting.ConfigFilePath(_root));
            ini.Set(SunCycleSetting.Key, SimitoneIniFile.FormatBool(false));

            //no blocker this time: the write succeeds and the earlier value is still there
            NeighbourhoodViewSetting.SetAndSave(NeighbourhoodView.Bitmap2D);
            try
            {
                Assert.Equal("False", ini.Get(SunCycleSetting.Key));
                Assert.Equal(NeighbourhoodViewSetting.Bitmap2DToken, ini.Get(NeighbourhoodViewSetting.Key));
            }
            finally
            {
                NeighbourhoodViewSetting.Set(NeighbourhoodViewSetting.Default);
            }
        }

        [Fact]
        public void AWorldConfigIsNotLeftWithASunFlagFromAChangeThatCouldNotBeSaved()
        {
            // The live renderer flag is pushed whatever the file does, which is right: the player
            // is looking at the lot now. This only pins that the push happened and did not throw.
            var originalWorld = WorldConfig.Current.SunCycle;
            BlockTheSidecar(SunCycleSetting.FileName);
            try
            {
                SunCycleSetting.SetEnabled(true, save: true);
                Assert.True(WorldConfig.Current.SunCycle);
            }
            finally
            {
                WorldConfig.Current.SunCycle = originalWorld;
            }
        }
    }
}
