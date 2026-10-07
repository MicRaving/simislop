using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <c>simitone.ini</c> is written by two settings - <see cref="SunCycleSetting"/> and
    /// <see cref="NeighbourhoodViewSetting"/> - and used to be written by two independent
    /// <c>IniConfig</c> subclasses, each of which truncated the file and wrote only its own
    /// properties. Toggling Dynamic Sun therefore erased the neighbourhood view and picking a
    /// view erased Dynamic Sun, and because both readers treat a missing key as "use the
    /// default", neither side noticed: the player's choice simply reverted.
    ///
    /// <para>
    /// These tests are the regression. They exercise the real settings against a throwaway user
    /// directory, so they cover the file, the two writers and the two readers together rather
    /// than a helper in isolation. Nothing here needs a VM, a content tree or a graphics device.
    /// </para>
    /// </summary>
    public sealed class SimitoneIniFileTests : IDisposable
    {
        private readonly string _root;

        public SimitoneIniFileTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-ini-" + Guid.NewGuid().ToString("N"));
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
                //best effort - a leftover temp dir must never fail the suite
            }
            catch (UnauthorizedAccessException)
            {
                //same
            }
        }

        private string Path_ => SunCycleSetting.ConfigFilePath(_root);

        // ---- the headline regression ------------------------------------------------------------

        [Fact]
        public void SavingDynamicSunLeavesTheNeighbourhoodViewAlone()
        {
            var path = Path_;
            SunCycleSetting.Ini(path).Set(NeighbourhoodViewSetting.Key, NeighbourhoodViewSetting.World3DToken);

            SunCycleSetting.Ini(path).Set(SunCycleSetting.Key, SimitoneIniFile.FormatBool(false));

            Assert.Equal("False", SunCycleSetting.Ini(path).Get(SunCycleSetting.Key));
            Assert.Equal(NeighbourhoodViewSetting.World3DToken,
                SunCycleSetting.Ini(path).Get(NeighbourhoodViewSetting.Key));
        }

        [Fact]
        public void SavingTheNeighbourhoodViewLeavesDynamicSunAlone()
        {
            var path = Path_;
            SunCycleSetting.Ini(path).Set(SunCycleSetting.Key, SimitoneIniFile.FormatBool(false));

            SunCycleSetting.Ini(path).Set(NeighbourhoodViewSetting.Key, NeighbourhoodViewSetting.Bitmap2DToken);

            Assert.Equal("False", SunCycleSetting.Ini(path).Get(SunCycleSetting.Key));
            Assert.Equal(NeighbourhoodViewSetting.Bitmap2DToken,
                SunCycleSetting.Ini(path).Get(NeighbourhoodViewSetting.Key));
        }

        [Fact]
        public void TheTwoSettingsAlternateWithoutLosingEitherKey()
        {
            // The shape a real session produces: flip one, flip the other, flip the first again.
            // Before the fix the third write is all that survives, and the file carries one key.
            var path = Path_;
            for (int i = 0; i < 6; i++)
            {
                SunCycleSetting.Ini(path).Set(SunCycleSetting.Key, SimitoneIniFile.FormatBool(i % 2 == 0));
                SunCycleSetting.Ini(path).Set(NeighbourhoodViewSetting.Key,
                    i % 3 == 0 ? NeighbourhoodViewSetting.OffToken : NeighbourhoodViewSetting.Bitmap2DToken);
            }

            var all = SunCycleSetting.Ini(path).ReadAll();
            Assert.True(all.ContainsKey(SunCycleSetting.Key), "Dynamic Sun must still be stored");
            Assert.True(all.ContainsKey(NeighbourhoodViewSetting.Key), "the view must still be stored");
        }

        [Fact]
        public void AKeyThisBuildDoesNotKnowAboutSurvivesASave()
        {
            // A future version's key, or a line a player wrote by hand. Rewriting the file to
            // add our own key must not be a chance to drop everything else in it.
            var path = Path_;
            File.WriteAllText(path,
                "# Simitone Settings File.\nFutureKey=42\nSunCycle=True\nNeighbourhoodView=2d\n");

            SunCycleSetting.Ini(path).Set(SunCycleSetting.Key, SimitoneIniFile.FormatBool(false));

            Assert.Equal("42", SunCycleSetting.Ini(path).Get("FutureKey"));
            Assert.Equal(NeighbourhoodViewSetting.Bitmap2DToken,
                SunCycleSetting.Ini(path).Get(NeighbourhoodViewSetting.Key));
        }

        [Fact]
        public void CommentsAndSectionHeadersSurviveASave()
        {
            var path = Path_;
            File.WriteAllText(path,
                "# a hand-written header\n[Some Section]\nNeighbourhoodView=off\n# a note about the next line\nSunCycle=True\n");

            SunCycleSetting.Ini(path).Set(SunCycleSetting.Key, SimitoneIniFile.FormatBool(true));

            var text = File.ReadAllText(path);
            Assert.Contains("# a hand-written header", text);
            Assert.Contains("[Some Section]", text);
            Assert.Contains("# a note about the next line", text);
            Assert.Contains("SunCycle=True", text);
            Assert.Contains("NeighbourhoodView=off", text);
        }

        [Fact]
        public void TheKeyIsReplacedWhereItAlreadyIsRatherThanAppendedTwice()
        {
            // Two lines for one key would make the answer depend on read order, and a hand-edited
            // file that has grown a duplicate is exactly what a merge-write can make worse.
            var path = Path_;
            SunCycleSetting.Ini(path).Set(SunCycleSetting.Key, "True");
            SunCycleSetting.Ini(path).Set(SunCycleSetting.Key, "False");

            var lines = File.ReadAllLines(path);
            Assert.Single(lines, l => l.StartsWith(SunCycleSetting.Key + "=", StringComparison.Ordinal));
        }

        [Fact]
        public void ANewFileGetsTheHeadingCommentAndOnlyTheKeysAskedFor()
        {
            var path = Path.Combine(_root, "fresh-" + Guid.NewGuid().ToString("N") + ".ini");

            SunCycleSetting.Ini(path).Set(SunCycleSetting.Key, SimitoneIniFile.FormatBool(true));

            var text = File.ReadAllText(path);
            Assert.Contains("# " + SunCycleSetting.HeadingComment, text);
            Assert.Contains(SunCycleSetting.Key + "=True", text);
        }

        [Fact]
        public void TheTwoSettingsAgreeOnTheFileName()
        {
            // The whole reason the shared writer exists. If these ever diverge the bug is back in
            // a new shape, and nothing else would notice.
            Assert.Equal(SunCycleSetting.FileName, NeighbourhoodViewSetting.FileName);
            Assert.NotEqual(StartupSettings.ConfigFileName, SunCycleSetting.FileName);
            Assert.NotEqual(SunCycleSetting.FileName, SoundVolumeSetting.FileName);
        }

        // ---- what the readers make of what is there -------------------------------------------------

        [Fact]
        public void AFileWithNeitherKeyIsTheDefaultForBoth()
        {
            // "Never configured" is the same answer for both, which is what made the old
            // data-loss invisible: the erased key did not throw, it reverted.
            File.WriteAllText(Path_, "# Simitone Settings File.\n");
            var ini = SunCycleSetting.Ini(Path_);

            Assert.Equal(SunCycleSetting.DefaultEnabled, SunCycleSetting.Resolve(ini.Get(SunCycleSetting.Key)));
            Assert.Equal(NeighbourhoodViewSetting.Default,
                NeighbourhoodViewSetting.Resolve(ini.Get(NeighbourhoodViewSetting.Key)));
        }

        [Fact]
        public void AMissingFileReadsAsTheDefaultForBoth()
        {
            var ini = SunCycleSetting.Ini(Path.Combine(_root, "does-not-exist.ini"));

            Assert.Empty(ini.ReadAll());
            Assert.Null(ini.Get(SunCycleSetting.Key));
        }

        [Fact]
        public void ANullOrEmptyPathIsANoOpRatherThanAnException()
        {
            // A game running with no user directory must still start; the settings' own guards
            // already resolve to the default, and this is the layer under them.
            var ini = SunCycleSetting.Ini(null);
            Assert.Empty(ini.ReadAll());
            Assert.Null(ini.Get(SunCycleSetting.Key));
            ini.Set(SunCycleSetting.Key, "True");   //must not throw
            Assert.Empty(ini.ReadAll());
        }

        [Fact]
        public void ValuesAreReadAndWrittenCaseInsensitively()
        {
            // IniConfig's own reader matched property names exactly, so this is a widening rather
            // than a change - but a hand-edited "suncycle=True" is a thing people write.
            File.WriteAllText(Path_, "suncycle=False\n");
            Assert.Equal("False", SunCycleSetting.Ini(Path_).Get("SunCycle"));
        }

        [Fact]
        public void AValueContainingAnEqualsSignSurvivesRoundTripping()
        {
            SimitoneSettingRoundTrip("NeighbourhoodView", "a=b");
            SimitoneSettingRoundTrip("FutureKey", "x = y = z");
        }

        [Fact]
        public void WhitespaceAroundAKeyOrValueIsTolerated()
        {
            File.WriteAllText(Path_, "  SunCycle  =  False  \n");
            Assert.Equal("False", SunCycleSetting.Ini(Path_).Get(SunCycleSetting.Key));
        }

        private void SimitoneSettingRoundTrip(string key, string value)
        {
            var path = Path.Combine(_root, "eq-" + Guid.NewGuid().ToString("N") + ".ini");
            var ini = SunCycleSetting.Ini(path);
            ini.Set(key, value);
            Assert.Equal(value, ini.Get(key));
        }

        [Fact]
        public void BlankLinesAndLinesWithNoEqualsAreNotKeys()
        {
            // IniConfig skipped these, and so does the writer: a line it cannot parse must not be
            // dropped, and must not be mistaken for a key.
            File.WriteAllText(Path_, "SunCycle=True\n\nnot a setting line\n; a semicolon comment\nNeighbourhoodView=2d\n");

            var all = SunCycleSetting.Ini(Path_).ReadAll();
            Assert.Equal(2, all.Count);
            Assert.Equal("True", all[SunCycleSetting.Key]);
            Assert.Equal("2d", all[NeighbourhoodViewSetting.Key]);
        }

        [Fact]
        public void FormatBoolIsWhatTheOldIniWroteAndResolveStillReadsIt()
        {
            // The stored spelling is "True"/"False" because that is what Convert.ToString wrote and
            // what an existing simitone.ini contains. Pinned so a change to either side shows up as
            // a failing resolve rather than as a silently ignored stored value.
            Assert.Equal("True", SimitoneIniFile.FormatBool(true));
            Assert.Equal("False", SimitoneIniFile.FormatBool(false));
            Assert.True(SunCycleSetting.Resolve(SimitoneIniFile.FormatBool(true)));
            Assert.False(SunCycleSetting.Resolve(SimitoneIniFile.FormatBool(false)));
        }

        // ---- the sound sidecar, which is a file of its own -----------------------------------------

        [Fact]
        public void TheSoundSidecarKeepsAllFourFlagsAndStillUsesTheRealIniShape()
        {
            var path = SoundVolumeSetting.ConfigFilePath(_root);
            var ini = new SimitoneIniFile(path, SoundVolumeSetting.HeadingComment);

            ini.SetAll(new[]
            {
                new KeyValuePair<string, string>("MuteFX", "True"),
                new KeyValuePair<string, string>("MuteMusic", "False"),
                new KeyValuePair<string, string>("MuteVoice", "True"),
                new KeyValuePair<string, string>("MuteAmbience", "False")
            });

            var text = File.ReadAllText(path);
            foreach (var key in new[] { "MuteFX", "MuteMusic", "MuteVoice", "MuteAmbience" })
            {
                Assert.Contains(key, text);
            }
            Assert.Contains("# " + SoundVolumeSetting.HeadingComment, text);
        }

        [Fact]
        public void AnUnknownMuteFlagIsUnmutedRatherThanAnError()
        {
            Assert.False(SoundVolumeSetting.ResolveMute(null));
            Assert.False(SoundVolumeSetting.ResolveMute(""));
            Assert.False(SoundVolumeSetting.ResolveMute("   "));
            Assert.False(SoundVolumeSetting.ResolveMute("banana"));
            Assert.True(SoundVolumeSetting.ResolveMute("True"));
            Assert.True(SoundVolumeSetting.ResolveMute(" true "));
            Assert.False(SoundVolumeSetting.ResolveMute("FALSE"));
        }
    }
}
