using System;
using System.IO;
using FSO.LotView.Model;
using Simitone.Client;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="LotWeatherState"/>: the per-lot decision of what the rain loop, the
    /// thunder and the terrain should be doing, and for the sidecar that carries the chosen
    /// weather between sessions.
    ///
    /// <para>
    /// <b>Why this is pure.</b> The screen's job is to read the engine's weather controller, ask
    /// this state what to do, and apply the answer. Everything that can be wrong - a rain loop
    /// that outlives its lot, thunder that keeps rolling while the game is paused, grass left
    /// white in July, a storm that re-plays its crack every frame - is decided here, with no VM,
    /// no world, no audio device and no graphics device anywhere near it. The tests below are
    /// therefore about decisions rather than about pixels, which is the only way they can run at
    /// all.
    /// </para>
    ///
    /// <para>
    /// The sidecar tests write to a throwaway directory and read the bytes back, because "the
    /// weather survives a restart" is a claim about a file, and a claim about a file is only
    /// worth anything if something reads the file.
    /// </para>
    /// </summary>
    public sealed class LotWeatherStateTests : IDisposable
    {
        private readonly string _tempRoot;

        public LotWeatherStateTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-weather-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
            }
            catch (IOException)
            {
                //a leftover temp directory is not worth failing a test over
            }
        }

        /// <summary>A random source that always returns the same value, so thunder gaps are exact.</summary>
        private sealed class FixedRandom : Random
        {
            private readonly double _value;
            public FixedRandom(double value) { _value = value; }
            public override double NextDouble() { return _value; }
        }

        private static LotWeatherState.Sample Sample(WeatherType type, bool thunder, float intensity, bool running = true)
        {
            return new LotWeatherState.Sample(type, thunder, intensity, running);
        }

        // ------------------------------------------------------------------------- rain: on/off

        [Fact]
        public void Rain_StartsWhenTheWeatherTurnsToIt()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);

            var action = state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);
            Assert.True((action & WeatherAudioAction.PlayRain) != 0);
        }

        [Fact]
        public void Rain_StopsWhenTheWeatherClears()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);

            var action = state.Tick(Sample(WeatherType.Rain, false, 0f), 1f / 60f);
            Assert.True((action & WeatherAudioAction.StopRain) != 0);
            Assert.False((action & WeatherAudioAction.PlayRain) != 0);
        }

        [Fact]
        public void Rain_IsSilentBelowTheAudibleThreshold()
        {
            //5% rain is not worth a loop instance: the engine's own particle swap treats it as
            //nothing, and an inaudible bed that is still running is a bug waiting to be noticed.
            var state = new LotWeatherState();
            state.BeginLot(null);

            var action = state.Tick(Sample(WeatherType.Rain, false, LotWeatherState.AudibleThreshold - 0.01f), 1f / 60f);
            Assert.Equal(WeatherAudioAction.None, action);
        }

        [Fact]
        public void Rain_StopsWhenTheWeatherBecomesSnow()
        {
            //a type change must both stop the old loop and start the new state, in one result:
            //the screen applies StopRain before PlayRain, so the two can never overlap.
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);

            var action = state.Tick(Sample(WeatherType.Snow, false, 0.5f), 1f / 60f);
            Assert.True((action & WeatherAudioAction.StopRain) != 0);
            Assert.True((action & WeatherAudioAction.ApplySnow) != 0);
        }

        [Fact]
        public void Rain_IsNotRestartedEveryFrame()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);

            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f));
        }

        [Fact]
        public void Rain_IsNotReLevelledForATinyChangeInStrength()
        {
            //without the hysteresis a slowly drifting intensity would re-issue a play every frame.
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);

            var action = state.Tick(Sample(WeatherType.Rain, false, 0.5f + LotWeatherState.IntensityEpsilon / 2f), 1f / 60f);
            Assert.Equal(WeatherAudioAction.None, action);
        }

        [Fact]
        public void Rain_IsReLevelledOnceTheStrengthMovesFarEnough()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);

            var action = state.Tick(Sample(WeatherType.Rain, false, 0.8f), 1f / 60f);
            Assert.True((action & WeatherAudioAction.PlayRain) != 0);
        }

        [Fact]
        public void Rain_VolumeIsClampedAndScaledRatherThanUsedRaw()
        {
            //the bed is scaled to MaxRainVolume, and a strength outside 0..1 must not push it past
            //that - the engine hands out a 0..1 intensity, but nothing guarantees it forever.
            Assert.Equal(0f, WeatherSounds.RainVolumeFor(0f));
            Assert.Equal(WeatherSounds.MaxRainVolume, WeatherSounds.RainVolumeFor(1f));
            Assert.Equal(WeatherSounds.MaxRainVolume, WeatherSounds.RainVolumeFor(4f));
            Assert.Equal(0f, WeatherSounds.RainVolumeFor(-1f));
            Assert.Equal(WeatherSounds.MaxRainVolume / 2f, WeatherSounds.RainVolumeFor(0.5f));
        }

        // ------------------------------------------------------------------- rain: pause/resume

        [Fact]
        public void Rain_BeginsPausedWhenTheGameIsNotRunning()
        {
            //one result carries both, and the screen applies PlayRain before PauseRain - a loop
            //that started during a pause must not be heard until the game resumes.
            var state = new LotWeatherState();
            state.BeginLot(null);

            var action = state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: false), 1f / 60f);
            Assert.True((action & WeatherAudioAction.PlayRain) != 0);
            Assert.True((action & WeatherAudioAction.PauseRain) != 0);
        }

        [Fact]
        public void Rain_ResumesWhenGameplayResumes()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: false), 1f / 60f);

            var action = state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: true), 1f / 60f);
            Assert.True((action & WeatherAudioAction.ResumeRain) != 0);
        }

        [Fact]
        public void Rain_IsPausedOnlyOnceAndResumedOnlyOnce()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: false), 1f / 60f);

            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: false), 1f / 60f));
            Assert.True((state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: true), 1f / 60f) & WeatherAudioAction.ResumeRain) != 0);
            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: true), 1f / 60f));
        }

        [Fact]
        public void Rain_IsNotResumedByAGameThatWasNeverPaused()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: true), 1f / 60f);

            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Rain, false, 0.5f, running: true), 1f / 60f));
        }

        // ------------------------------------------------------------------------------ thunder

        [Fact]
        public void Thunder_StrikesAsSoonAsAStormStarts()
        {
            var state = new LotWeatherState(new FixedRandom(0d));
            state.BeginLot(null);

            Assert.True((state.Tick(Sample(WeatherType.Rain, true, 0.5f), 0f) & WeatherAudioAction.PlayThunder) != 0);
        }

        [Fact]
        public void Thunder_WaitsOutItsGapBetweenCracks()
        {
            //the gap is the shortest one the class can pick (5s) and a frame is a quarter of a
            //second - the ceiling Tick's delta clamp allows - so the second crack is due on the
            //twentieth frame after the first.
            var state = new LotWeatherState(new FixedRandom(0d));
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, true, 0.5f), 0f);

            for (int i = 0; i < 19; i++)
            {
                Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Rain, true, 0.5f), 0.25f));
            }
            Assert.True((state.Tick(Sample(WeatherType.Rain, true, 0.5f), 0.25f) & WeatherAudioAction.PlayThunder) != 0);
        }

        [Fact]
        public void Thunder_StopsWhileTheGameIsPaused()
        {
            //a paused lot is silent: the gap is not spent, and no crack is queued up to fire the
            //moment the player unpauses.
            var state = new LotWeatherState(new FixedRandom(0d));
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, true, 0.5f, running: false), 1f);

            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Rain, true, 0.5f, running: false), 1f));
        }

        [Fact]
        public void Thunder_NeedsRainToThunderIn()
        {
            //thunder is a bit on the weather, not a separate state: snow with the bit set is
            //snow, and a lot of it must not rattle.
            var state = new LotWeatherState(new FixedRandom(0d));
            state.BeginLot(null);

            Assert.False((state.Tick(Sample(WeatherType.Snow, true, 0.5f), 0f) & WeatherAudioAction.PlayThunder) != 0);
        }

        [Fact]
        public void Thunder_IsSilentWhenTheRainIsTooLightToHear()
        {
            var state = new LotWeatherState(new FixedRandom(0d));
            state.BeginLot(null);

            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Rain, true, 0.05f), 0f));
        }

        [Fact]
        public void Thunder_StopsWhenTheStormEnds_AndEachNewStormIsItsOwnCrack()
        {
            var state = new LotWeatherState(new FixedRandom(0d));
            state.BeginLot(null);
            Assert.True((state.Tick(Sample(WeatherType.Rain, true, 0.5f), 0f) & WeatherAudioAction.PlayThunder) != 0);

            //rain continues, storm does not. (The rain bed is re-levelled, because the storm bit is
            //part of the weather the bed is answering to; what must not happen is a crack.)
            Assert.False((state.Tick(Sample(WeatherType.Rain, false, 0.5f), 0.25f) & WeatherAudioAction.PlayThunder) != 0);

            //and the storm coming back cracks on the frame it arrives rather than staying silent
            //for the rest of a gap it had already been counting down.
            Assert.True((state.Tick(Sample(WeatherType.Rain, true, 0.5f), 0.25f) & WeatherAudioAction.PlayThunder) != 0);
        }

        [Fact]
        public void Thunder_IsNotSwallowedByOneLongStall()
        {
            //a debugger break or a stalled frame must not consume the whole gap in one tick: the
            //delta is clamped, so the gap is still spent over the frames that follow.
            var state = new LotWeatherState(new FixedRandom(0d));
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, true, 0.5f), 0f);

            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Rain, true, 0.5f), 600f));
        }

        [Theory]
        [InlineData(-1f, 0f)]
        [InlineData(0f, 0f)]
        [InlineData(0.016f, 0.016f)]
        [InlineData(30f, 0.25f)]
        [InlineData(float.NaN, 0f)]
        public void ClampDelta_KeepsAFrameTimePlausible(float raw, float expected)
        {
            Assert.Equal(expected, LotWeatherState.ClampDelta(raw), 5);
        }

        // ------------------------------------------------------------------------------ terrain

        [Fact]
        public void Snow_IsAppliedToTheTerrainOnce()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);

            Assert.True((state.Tick(Sample(WeatherType.Snow, false, 0.5f), 1f / 60f) & WeatherAudioAction.ApplySnow) != 0);
            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Snow, false, 0.5f), 1f / 60f));
            Assert.True(state.SnowApplied);
        }

        [Fact]
        public void Snow_IsTakenOffTheTerrainWhenItStops()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Snow, false, 0.5f), 1f / 60f);

            Assert.True((state.Tick(Sample(WeatherType.Snow, false, 0f), 1f / 60f) & WeatherAudioAction.ClearSnow) != 0);
            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Snow, false, 0f), 1f / 60f));
            Assert.False(state.SnowApplied);
        }

        [Fact]
        public void Snow_IsNotAppliedForASnowfallTooLightToSee()
        {
            var state = new LotWeatherState();
            state.BeginLot(null);

            Assert.Equal(WeatherAudioAction.None, state.Tick(Sample(WeatherType.Snow, false, 0.05f), 1f / 60f));
            Assert.False(state.SnowApplied);
        }

        [Fact]
        public void Snow_AndRainAreNeverBothAppliedInOneResult()
        {
            //they are two halves of one weather; asking for both at once would mean the screen
            //had to decide an ordering the engine never intended.
            var state = new LotWeatherState();
            state.BeginLot(null);

            var action = state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);
            Assert.True((action & WeatherAudioAction.PlayRain) != 0);
            Assert.Equal(0, (int)(action & WeatherAudioAction.ApplySnow));
        }

        // ----------------------------------------------------------------- lot lifecycle + sidecar

        [Fact]
        public void EndLot_StopsTheRainExactlyOnce()
        {
            //this is what keeps a rain loop from following the player off the lot.
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);

            Assert.True((state.EndLot() & WeatherAudioAction.StopRain) != 0);
            Assert.Equal(WeatherAudioAction.None, state.EndLot());
            Assert.False(state.RainPlaying);
        }

        [Fact]
        public void EndLot_DoesNotTryToUnsnowATerrainThatIsAboutToBeThrownAway()
        {
            //the world the snow belongs to is being disposed; writing to it during teardown is
            //work thrown away at best and a null reference at worst.
            var state = new LotWeatherState();
            state.BeginLot(null);
            state.Tick(Sample(WeatherType.Snow, false, 0.5f), 1f / 60f);

            Assert.False((state.EndLot() & WeatherAudioAction.ClearSnow) != 0);
            Assert.False(state.SnowApplied);
        }

        [Fact]
        public void BeginLot_ForgetsTheLastLotSoTheNewOneIsJudgedFromScratch()
        {
            //two lots in a row both raining must still start the loop for the second one: the
            //audio object outlives the lot, but the state that decided it does not.
            var first = new LotWeatherState();
            first.BeginLot(null);
            first.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f);
            first.EndLot();

            first.BeginLot(null);
            Assert.True((first.Tick(Sample(WeatherType.Rain, false, 0.5f), 1f / 60f) & WeatherAudioAction.PlayRain) != 0);
        }

        [Fact]
        public void SavedWeather_IsHandedOverOnceAndThenForgotten()
        {
            //the blueprint is created by a queued VM command, so the weather is applied a frame or
            //two after the lot load - exactly once, because a second application would fight
            //whatever the player has done since.
            var state = new LotWeatherState();
            state.BeginLot(0x0132);

            Assert.True(state.HasPendingWeatherData);
            Assert.True(state.TryTakePendingWeatherData(out short data));
            Assert.Equal(0x0132, data);

            Assert.False(state.HasPendingWeatherData);
            Assert.False(state.TryTakePendingWeatherData(out _));
        }

        [Fact]
        public void ALotWithNoSavedWeatherIsLeftAlone()
        {
            //no sidecar means the player has never touched the weather, and the engine's own
            //default is what they had before this feature existed.
            var state = new LotWeatherState();
            state.BeginLot(null);

            Assert.False(state.HasPendingWeatherData);
            Assert.False(state.TryTakePendingWeatherData(out _));
        }

        [Fact]
        public void SidecarPath_SitsBesideTheOtherLotFiles()
        {
            var path = LotWeatherState.SidecarPath(_tempRoot);
            Assert.Equal(Path.Combine(_tempRoot, LotWeatherState.LocalHouseFolder, LotWeatherState.SidecarFileName), path);
        }

        [Fact]
        public void SidecarPath_IsNullRatherThanRelativeWhenThereIsNoUserDirectory()
        {
            // AtomicFileCommit refuses a relative path, and Program.Main sets the process working
            // directory to the install folder - so a sidecar path built from an empty user
            // directory would otherwise be a save written next to the executable. Null is the
            // answer, and the read and the write both stop on it.
            Assert.Null(LotWeatherState.SidecarPath(null));
            Assert.Null(LotWeatherState.SidecarPath(""));
            Assert.Null(LotWeatherState.ReadSidecar(LotWeatherState.SidecarPath(null)));
            LotWeatherState.WriteSidecar(LotWeatherState.SidecarPath(""), 0x094B);   //must not throw
        }

        [Fact]
        public void Sidecar_RoundTripsTheWeather()
        {
            var path = LotWeatherState.SidecarPath(_tempRoot);
            LotWeatherState.WriteSidecar(path, 0x094B);

            Assert.Equal((short?)0x094B, LotWeatherState.ReadSidecar(path));
        }

        [Fact]
        public void Sidecar_MissingFileReadsAsNothingRatherThanThrowing()
        {
            Assert.Null(LotWeatherState.ReadSidecar(LotWeatherState.SidecarPath(_tempRoot)));
        }

        [Fact]
        public void Sidecar_AFileTooShortToHoldAWeatherReadsAsNothing()
        {
            //a truncated sidecar is a bug, not a crash: the lot just opens with the engine default.
            var path = LotWeatherState.SidecarPath(_tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[] { 0x01 });

            Assert.Null(LotWeatherState.ReadSidecar(path));
        }

        [Fact]
        public void Sidecar_AFileThatCannotBeReadReadsAsNothing()
        {
            //a directory where the file should be is the awkward case (permissions, a stray
            //folder): it must still be nothing rather than an exception on a lot load.
            var path = Path.Combine(_tempRoot, "not-a-file");
            Directory.CreateDirectory(path);

            Assert.Null(LotWeatherState.ReadSidecar(path));
        }

        [Fact]
        public void Sidecar_AFailedWriteIsSwallowed()
        {
            //the sidecar is cosmetic. A write that cannot happen (here: the parent path is a file)
            //must not turn a save into a failure the player sees.
            var blocker = Path.Combine(_tempRoot, "blocker");
            File.WriteAllText(blocker, "not a directory");

            LotWeatherState.WriteSidecar(Path.Combine(blocker, LotWeatherState.LocalHouseFolder, LotWeatherState.SidecarFileName), 0x0132);
        }

        [Fact]
        public void Sidecar_StandsInForTheFileRatherThanTruncatingIt()
        {
            //the lot saves are committed atomically, and this is committed the same way: a failed
            //write leaves the previous weather readable.
            var path = LotWeatherState.SidecarPath(_tempRoot);
            LotWeatherState.WriteSidecar(path, 0x0132);
            var backup = AtomicFileCommit.Write(path, new byte[] { 0x4B, 0x09 });

            Assert.NotNull(backup);
            Assert.True(File.Exists(backup));
            Assert.Equal((short?)0x094B, LotWeatherState.ReadSidecar(path));
        }
    }
}
