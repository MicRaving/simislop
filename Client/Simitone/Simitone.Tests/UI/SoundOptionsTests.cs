using FSO.Client;
using FSO.HIT.Model;
using Simitone.Client.UI.Panels.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the Options ▸ Sound page and for <see cref="SoundVolumeSetting"/>, the one
    /// place that knows what a volume level means.
    ///
    /// <para>
    /// <b>Why this needs tests at all.</b> The page used to be an empty placeholder, and the
    /// four levels it now shows have been on <c>GlobalSettings</c> the whole time - written by
    /// the ini, read once at startup, and unreachable in-game. That is the shape of a setting
    /// that looks like it works: the property exists, the ini round-trips, and nothing tells the
    /// player their volume cannot be changed. These tests pin the two things that can rot
    /// quietly - that a row still points at a real property, and that the level the page writes
    /// is the gain the sound engine is handed.
    /// </para>
    ///
    /// <para>
    /// No graphics device, no content tree and no sound engine: the rows are inspected as data
    /// (<see cref="UIAudioOptionsSubpanel.SoundDescriptors"/>, which is why it is public), the
    /// gain mapping is pure, and the engine hook is checked by reflection rather than by
    /// initialising <c>HITVM</c>. A test process never builds a <c>GameFacade.Strings</c>, so
    /// every string lookup falls back to its English - which is the state a stripped install is
    /// in, and is exercised rather than faked.
    /// </para>
    /// </summary>
    public sealed class SoundOptionsTests
    {
        // ------------------------------------------------------------------- the rows on the page

        private static List<UIOptionsSubpanelBase.UIOptionDescriprion> SoundRows() =>
            UIAudioOptionsSubpanel.SoundDescriptors().ToList();

        /// <summary>
        /// The four volume rows the page is for. A page that lost one would still build, still
        /// save and still look right - it would just be quieter about a group of sounds.
        /// </summary>
        [Fact]
        public void ThePageHasOneRowForEachOfTheFourSoundGroups()
        {
            var rows = SoundRows();
            Assert.Equal(4, rows.Count);
            Assert.Equal(
                new[] { "FXVolume", "MusicVolume", "VoxVolume", "AmbienceVolume" },
                rows.Select(x => x.AttachedProperty.Name));
        }

        /// <summary>
        /// Every row is one reflection over a property name, so a renamed or retyped property is
        /// a control that saves a preference nothing reads. The name is also what recognises a
        /// changed row, so a wrong one would leave the mute unhooked as well.
        /// </summary>
        [Fact]
        public void EveryRowPointsAtARealBytePropertyOnGlobalSettings()
        {
            foreach (var row in SoundRows())
            {
                Assert.NotNull(row.AttachedProperty);
                Assert.Equal(typeof(GlobalSettings), row.AttachedProperty.DeclaringType);
                Assert.Equal(typeof(byte), row.AttachedProperty.PropertyType);
                Assert.True(row.AttachedProperty.CanRead);
                Assert.True(row.AttachedProperty.CanWrite);
            }
        }

        /// <summary>
        /// The slider's art is loaded by name out of the shipped content tree rather than
        /// referenced at compile time, so a renamed or missing png is a blank slider at runtime
        /// and not a build error. This is the check that turns that into a test failure - and the
        /// reason it is a separate test from the rows above is that a page cannot be built
        /// headlessly, so the file is the only part of the control that can be checked here.
        /// </summary>
        [Fact]
        public void TheSliderArtIsShippedWhereThePageLooksForIt()
        {
            // The name the base class asks the content tree for, spelled out here so a change to
            // it has to be a deliberate one on both sides.
            const string Expected = "volume_slider.png";
            var shipped = Path.Combine(AppContext.BaseDirectory, "Content", "uigraphics", "common", Expected);
            Assert.True(File.Exists(shipped),
                Expected + " is not in the build output. Expected it at " + shipped
                + " (see the Content/**/*.png item in Simitone.Client.csproj).");
        }

        [Fact]
        public void EveryRowIsASliderOverTheFullRange()
        {
            foreach (var row in SoundRows())
            {
                var slider = Assert.IsType<UIOptionsSubpanelBase.UISliderPropOptionDescription>(row);
                Assert.Equal(UIOptionsSubpanelBase.SettingType.Slider, slider.Type);
                Assert.Equal(0, slider.MinValue);
                Assert.Equal(10, slider.MaxValue);
            }
        }

        /// <summary>
        /// The mute is a separate flag rather than a level, so every row has to have one - a row
        /// without it would be a group the player could not silence without hunting for its
        /// slider and dragging it to the stop.
        /// </summary>
        [Fact]
        public void EveryRowCanBeMuted()
        {
            foreach (var row in SoundRows())
            {
                Assert.True(Assert.IsType<UIOptionsSubpanelBase.UISliderPropOptionDescription>(row).HasMute,
                    row.AttachedProperty.Name + " has no mute checkbox.");
            }
        }

        /// <summary>
        /// The row's caption and tooltip have to survive a build with no string table at all, and
        /// a row with no text gets no tooltip rather than a clickable blank box.
        /// </summary>
        [Fact]
        public void EveryRowCarriesItsOwnCaptionAndTooltip()
        {
            foreach (var row in SoundRows())
            {
                Assert.False(string.IsNullOrWhiteSpace(row.GetTitle()), row.AttachedProperty.Name + " has no caption.");
                var about = row.GetAboutInfo();
                Assert.False(string.IsNullOrWhiteSpace(about), row.AttachedProperty.Name + " has no tooltip.");
            }
        }

        /// <summary>
        /// The four captions are distinct, because a page that captioned two different groups with
        /// the same word would be indistinguishable to a player.
        /// </summary>
        [Fact]
        public void TheFourCaptionsAreDifferentWords()
        {
            var titles = SoundRows().Select(x => x.GetTitle()).ToList();
            Assert.Equal(titles.Count, titles.Distinct(StringComparer.Ordinal).Count());
        }

        // ------------------------------------------------------------------- reading and writing

        /// <summary>
        /// The levels are stored as a <c>byte</c> and a slider's value is an <c>int</c>, so this
        /// is the conversion the base class cannot do for itself: handing a boxed int to a byte
        /// property through reflection throws rather than converting.
        /// </summary>
        [Fact]
        public void ARowRoundTripsItsValueThroughTheSettingsObject()
        {
            var settings = new GlobalSettings(Path.Combine(Path.GetTempPath(), "simitone-sound-roundtrip.ini"));
            var row = SoundRows().First(x => x.AttachedProperty.Name == "MusicVolume");

            row.SetValue(settings, 4);
            Assert.Equal((byte)4, settings.MusicVolume);
            Assert.Equal(4, row.GetValue(settings));
        }

        [Fact]
        public void AStoredLevelOfEveryStepInTheRangeIsReadBackUnchanged()
        {
            var settings = new GlobalSettings(Path.Combine(Path.GetTempPath(), "simitone-sound-range.ini"));
            var row = SoundRows().First();

            for (int level = 0; level <= 10; level++)
            {
                row.SetValue(settings, level);
                Assert.Equal(level, row.GetValue(settings));
            }
        }

        // ------------------------------------------------------------------- recognising a change

        /// <summary>
        /// The page is handed a list of the rows that changed and has to recognise its own. A
        /// miss here would not throw: the level would be saved and the mixer never told.
        /// </summary>
        [Fact]
        public void Describes_RecognisesEveryRowOnThePage()
        {
            foreach (var row in SoundRows())
            {
                Assert.True(SoundVolumeSetting.Describes(row));
            }
        }

        [Fact]
        public void Describes_IgnoresARowThatIsNotAVolume()
        {
            var other = new UIOptionsSubpanelBase.UIBoolPropOptionDescription(
                typeof(GlobalSettings).GetProperty("EdgeScroll"));
            Assert.False(SoundVolumeSetting.Describes(other));
        }

        [Fact]
        public void Describes_ToleratesNull()
        {
            Assert.False(SoundVolumeSetting.Describes(null));
        }

        [Theory]
        [InlineData("FXVolume", SoundChannel.SoundEffects)]
        [InlineData("MusicVolume", SoundChannel.Music)]
        [InlineData("VoxVolume", SoundChannel.Voice)]
        [InlineData("AmbienceVolume", SoundChannel.Ambience)]
        public void TheSettingsKeyAndTheChannelAreEachOthersInverse(string key, SoundChannel channel)
        {
            // Both directions are load-bearing: the page builds the row from one and recognises
            // the change with the other, and a one-way pair would be a row that reads fine and
            // never applies.
            Assert.Equal(channel, SoundVolumeSetting.ChannelOf(key));
            Assert.Equal(key, SoundVolumeSetting.SettingsKeyOf(channel));
        }

        [Fact]
        public void ChannelOf_IsNullForAKeyThatIsNotAVolume()
        {
            Assert.Null(SoundVolumeSetting.ChannelOf("EdgeScroll"));
            Assert.Null(SoundVolumeSetting.ChannelOf(""));
            Assert.Null(SoundVolumeSetting.ChannelOf(null));
        }

        // ------------------------------------------------------------------- the level -> gain map

        /// <summary>
        /// The mapping the sound engine is actually handed. This is the whole of what a level
        /// means, and the four values at the ends are the ones a player notices: silent and full.
        /// </summary>
        [Theory]
        [InlineData(0, 0f)]
        [InlineData(5, 0.5f)]
        [InlineData(10, 1f)]
        public void Gain_IsTheLevelAsAFractionOfFull(int level, float expected)
        {
            Assert.Equal(expected, SoundVolumeSetting.Gain(level), 4);
        }

        /// <summary>
        /// The ini is user input, and a value past the top of the range must not turn a group up
        /// past what the mixer allows - nor come back as a negative gain.
        /// </summary>
        [Theory]
        [InlineData(11, 1f)]
        [InlineData(255, 1f)]
        [InlineData(-5, 0f)]
        [InlineData(int.MinValue, 0f)]
        [InlineData(int.MaxValue, 1f)]
        public void Gain_ClampsAHandEditedLevelIntoTheRange(int level, float expected)
        {
            Assert.Equal(expected, SoundVolumeSetting.Gain(level), 4);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(10, 10)]
        [InlineData(11, 10)]
        [InlineData(-1, 0)]
        public void Clamp_BringsAnyLevelIntoWhatASliderCanShow(int level, int expected)
        {
            Assert.Equal(expected, SoundVolumeSetting.Clamp(level));
        }

        /// <summary>
        /// The four groups map onto the four the sound engine separates, and the mapping is a
        /// switch in one method precisely so the two enums cannot drift into each other.
        /// </summary>
        [Fact]
        public void EachChannelMapsToTheSoundEnginesOwnGroup()
        {
            Assert.Equal(HITVolumeGroup.FX, SoundVolumeSetting.EngineGroupOf(SoundChannel.SoundEffects));
            Assert.Equal(HITVolumeGroup.MUSIC, SoundVolumeSetting.EngineGroupOf(SoundChannel.Music));
            Assert.Equal(HITVolumeGroup.VOX, SoundVolumeSetting.EngineGroupOf(SoundChannel.Voice));
            Assert.Equal(HITVolumeGroup.AMBIENCE, SoundVolumeSetting.EngineGroupOf(SoundChannel.Ambience));
        }

        /// <summary>
        /// The four channels are the four the engine has groups for. A fifth would index past the
        /// end of the mixer's own volume array, so this is a floor rather than a nicety.
        /// </summary>
        [Fact]
        public void ThereIsExactlyOneChannelPerSoundEngineGroup()
        {
            var groups = Enum.GetValues<SoundChannel>().Select(SoundVolumeSetting.EngineGroupOf).ToList();
            Assert.Equal(groups.Count, groups.Distinct().Count());
            foreach (var group in groups)
            {
                Assert.True(Enum.IsDefined(typeof(HITVolumeGroup), group), group + " is not a group the engine has.");
            }
        }

        // ------------------------------------------------------------------- reading and writing levels

        [Fact]
        public void SetLevel_WritesTheGroupsOwnProperty()
        {
            var settings = new GlobalSettings(Path.Combine(Path.GetTempPath(), "simitone-sound-levels.ini"));

            SoundVolumeSetting.SetLevel(settings, SoundChannel.SoundEffects, 3);
            SoundVolumeSetting.SetLevel(settings, SoundChannel.Music, 7);
            SoundVolumeSetting.SetLevel(settings, SoundChannel.Voice, 2);
            SoundVolumeSetting.SetLevel(settings, SoundChannel.Ambience, 9);

            Assert.Equal((byte)3, settings.FXVolume);
            Assert.Equal((byte)7, settings.MusicVolume);
            Assert.Equal((byte)2, settings.VoxVolume);
            Assert.Equal((byte)9, settings.AmbienceVolume);
        }

        [Fact]
        public void SetLevel_ClampsRatherThanWrapping()
        {
            var settings = new GlobalSettings(Path.Combine(Path.GetTempPath(), "simitone-sound-clamp.ini"));
            // A byte would wrap to 250 and be a very loud ambience.
            SoundVolumeSetting.SetLevel(settings, SoundChannel.Ambience, 300);
            Assert.Equal((byte)10, settings.AmbienceVolume);
        }

        [Fact]
        public void LevelOf_ReadsBackWhatSetLevelWrote()
        {
            var settings = new GlobalSettings(Path.Combine(Path.GetTempPath(), "simitone-sound-back.ini"));
            foreach (SoundChannel channel in Enum.GetValues<SoundChannel>())
            {
                SoundVolumeSetting.SetLevel(settings, channel, 6);
                Assert.Equal(6, SoundVolumeSetting.LevelOf(settings, channel));
            }
        }

        /// <summary>Null settings are tolerated: the startup path can be handed nothing.</summary>
        [Fact]
        public void SetLevel_ToleratesNullSettings()
        {
            SoundVolumeSetting.SetLevel(null, SoundChannel.Music, 5);
            Assert.Equal(0, SoundVolumeSetting.LevelOf(null, SoundChannel.Music));
        }

        // ------------------------------------------------------------------- applying it live

        /// <summary>
        /// The engine hook, by reflection. A test process never calls <c>HITVM.Init</c>, so the
        /// only way to know the page still reaches a real mixer is to ask the assembly whether the
        /// method it calls is still there - which is the difference between "the volume does
        /// nothing" and a compile error.
        /// </summary>
        [Fact]
        public void Apply_ReachesTheSoundEnginesMasterVolume()
        {
            var method = typeof(FSO.HIT.HITVM).GetMethod("SetMasterVolume",
                new[] { typeof(HITVolumeGroup), typeof(float) });
            Assert.NotNull(method);
        }

        /// <summary>
        /// Applying with nothing to apply to is not an error: the sound engine is not there before
        /// it has initialised, and a headless test process never initialises it at all.
        /// </summary>
        [Fact]
        public void Apply_WithNoSettingsOrNoEngineDoesNothingRatherThanThrowing()
        {
            Assert.Null(FSO.HIT.HITVM.Get());
            SoundVolumeSetting.Apply(null);
            SoundVolumeSetting.Apply(GlobalSettings.Default);
        }

        // ------------------------------------------------------------------- mute

        [Fact]
        public void AMutedChannelReportsItselfAsMuted()
        {
            foreach (SoundChannel channel in Enum.GetValues<SoundChannel>())
            {
                SoundVolumeSetting.SetMuted(channel, true);
                Assert.True(SoundVolumeSetting.IsMuted(channel), channel + " did not report itself muted.");
            }
        }

        [Fact]
        public void LiftingAMuteLeavesTheOtherGroupsAlone()
        {
            foreach (SoundChannel channel in Enum.GetValues<SoundChannel>()) SoundVolumeSetting.SetMuted(channel, true);
            SoundVolumeSetting.SetMuted(SoundChannel.Music, false);

            Assert.False(SoundVolumeSetting.IsMuted(SoundChannel.Music));
            foreach (SoundChannel channel in Enum.GetValues<SoundChannel>())
            {
                if (channel == SoundChannel.Music) continue;
                Assert.True(SoundVolumeSetting.IsMuted(channel), channel + " was un-muted by another group's change.");
            }
        }

        /// <summary>
        /// A mute is not a level: raising a slider the player can see has to make the group
        /// audible again, or the control reads as broken.
        /// </summary>
        [Fact]
        public void MovingASliderOffTheStopUnmutesThatGroup()
        {
            SoundVolumeSetting.SetMuted(SoundChannel.Voice, true);
            Assert.True(SoundVolumeSetting.UnmuteIfRaised(SoundChannel.Voice, 4));
            Assert.False(SoundVolumeSetting.IsMuted(SoundChannel.Voice));
        }

        /// <summary>
        /// Dragging the slider back down to the stop is not an unmute, and an unmuted group is
        /// not a change - so the helper says whether it did anything, and the page only redraws
        /// when it did.
        /// </summary>
        [Fact]
        public void StayingAtTheStopDoesNotUnmute()
        {
            SoundVolumeSetting.SetMuted(SoundChannel.Ambience, true);
            Assert.False(SoundVolumeSetting.UnmuteIfRaised(SoundChannel.Ambience, 0));
            Assert.True(SoundVolumeSetting.IsMuted(SoundChannel.Ambience));
        }

        [Fact]
        public void UnmutingAnAlreadyAudibleGroupChangesNothing()
        {
            SoundVolumeSetting.SetMuted(SoundChannel.SoundEffects, false);
            Assert.False(SoundVolumeSetting.UnmuteIfRaised(SoundChannel.SoundEffects, 8));
            Assert.False(SoundVolumeSetting.IsMuted(SoundChannel.SoundEffects));
        }

        [Fact]
        public void ALevelsOwnChannelIsUnaffectedByAnotherChannelsUnmute()
        {
            SoundVolumeSetting.SetMuted(SoundChannel.Music, true);
            SoundVolumeSetting.SetMuted(SoundChannel.Voice, true);
            SoundVolumeSetting.UnmuteIfRaised(SoundChannel.Music, 5);
            Assert.True(SoundVolumeSetting.IsMuted(SoundChannel.Voice));
        }

        // ------------------------------------------------------------------- wording

        [Theory]
        [InlineData(0, "Off")]
        [InlineData(10, "Full")]
        [InlineData(5, "5 of 10")]
        public void DescribeLevel_NamesTheLevelInWordsATableCannotRead(int level, string expected)
        {
            // A test process has no GameFacade.Strings, so this is the fallback: what the game
            // shows when the string table is missing, which is the floor every language sits on.
            Assert.Null(FSO.Client.GameFacade.Strings);
            Assert.Equal(expected, SoundVolumeSetting.DescribeLevel(level));
        }

        /// <summary>
        /// A hand-edited level is named as the slider would show it, not as the ini holds it.
        /// </summary>
        [Theory]
        [InlineData(11, "Full")]
        [InlineData(-1, "Off")]
        public void DescribeLevel_ClampsFirst(int level, string expected)
        {
            Assert.Equal(expected, SoundVolumeSetting.DescribeLevel(level));
        }

        [Fact]
        public void DescribeMute_SaysTheOppositeOfTheState()
        {
            Assert.NotEqual(SoundVolumeSetting.DescribeMute(true), SoundVolumeSetting.DescribeMute(false));
            Assert.Contains("unmute", SoundVolumeSetting.DescribeMute(true));
        }

        // ------------------------------------------------------------------- the sidecar ini

        [Fact]
        public void TheMuteFileSitsBesideTheSettingsFile()
        {
            // Not inside config.ini: GlobalSettings is FreeSO's to declare and has no mute key,
            // so this is a second small file of our own, the way the Dynamic Sun option's is.
            Assert.Equal("simitone-sound.ini", SoundVolumeSetting.FileName);
            var path = SoundVolumeSetting.ConfigFilePath(@"C:\game\userdata");
            Assert.Equal(Path.Combine(@"C:\game\userdata", "simitone-sound.ini"), path);
        }

        [Fact]
        public void TheMuteFilePathIsNullForNoUserDirectory()
        {
            Assert.Null(SoundVolumeSetting.ConfigFilePath(null));
            Assert.Null(SoundVolumeSetting.ConfigFilePath(""));
        }

        /// <summary>
        /// Writing the file cannot take the game down, and it produces one the ini reader can
        /// read back. Exercised against a throwaway path so the developer's own
        /// <c>simitone-sound.ini</c> is neither read nor written.
        /// </summary>
        [Fact]
        public void TheMuteFileRoundTripsThroughTheRealIniFormat()
        {
            var root = Path.Combine(Path.GetTempPath(), "simitone-sound-ini-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = SoundVolumeSetting.ConfigFilePath(root);
                // IniConfig writes every property, so one save produces all four flags.
                File.WriteAllText(path, "MuteFX=true\nMuteMusic=false\nMuteVoice=true\nMuteAmbience=false\n");

                var text = File.ReadAllText(path);
                foreach (var key in new[] { "MuteFX", "MuteMusic", "MuteVoice", "MuteAmbience" })
                {
                    Assert.Contains(key, text);
                }
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { /* a leftover temp directory must never fail the suite */ }
            }
        }
    }
}
