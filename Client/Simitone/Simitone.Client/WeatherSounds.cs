/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Common;
using FSO.LotView.Model;
using Microsoft.Xna.Framework.Audio;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Simitone.Client
{
    /// <summary>
    /// The cosmetic weather's audio: one looping rain bed and one thunder clip, played by the
    /// lot's weather state (see <see cref="LotWeatherState"/>).
    ///
    /// <para>
    /// Nothing here decides <em>when</em> to make a sound. The engine's
    /// <see cref="WeatherController"/> owns the weather state and Simitone's per-lot
    /// <see cref="LotWeatherState"/> turns that into a set of play/pause/stop actions, which are
    /// applied here. Keeping the two apart is what lets the decisions be tested without an audio
    /// device, and it means this class can be as boring as a wrapper around
    /// <see cref="SoundEffect"/> as it should be.
    /// </para>
    ///
    /// <para>
    /// <b>Assets.</b> Both clips are third-party and are copied next to the executable by
    /// <c>Simitone.Client.csproj</c>:
    /// <list type="bullet">
    /// <item><c>Content/Sounds/rain_loop.wav</c> - "rain loop" by <b>Rubaoliva</b>, released
    /// CC0 (public domain), from <a href="https://freesound.org/s/624645/">freesound.org/s/624645</a>.
    /// 15.5s, 44.1kHz stereo, 16-bit PCM.</item>
    /// <item><c>Content/Sounds/thunder.wav</c> - "thunder" by <b>Raclure</b>, released CC0
    /// (public domain), from <a href="https://freesound.org/s/458870/">freesound.org/s/458870</a>.
    /// 4.5s, 44.1kHz mono, 16-bit PCM.</item>
    /// </list>
    /// Both are CC0, so the only thing owed is the credit above - keep it with the files.
    /// </para>
    ///
    /// <para>
    /// A missing or unreadable clip is never fatal: the game runs silently rather than not at
    /// all, and the reason goes to <see cref="SimitoneLog"/> (never to the console, which is not
    /// where a player or a bug report will look).
    /// </para>
    /// </summary>
    public static class WeatherSounds
    {
        /// <summary>Folder under the content root holding the weather clips.</summary>
        public const string SoundsFolder = "Sounds";

        /// <summary>File name of the looping rain bed.</summary>
        public const string RainLoopFileName = "rain_loop.wav";

        /// <summary>File name of the thunder clip.</summary>
        public const string ThunderFileName = "thunder.wav";

        /// <summary>
        /// How loud the rain bed plays at full intensity. It is a bed under the lot's own sounds,
        /// not a foreground effect, so it never reaches the full 0..1 the mixer allows.
        /// </summary>
        public const float MaxRainVolume = 0.5f;

        private static SoundEffect RainLoopSound;
        private static SoundEffect ThunderSound;
        private static SoundEffectInstance RainLoopInstance;

        /// <summary>Whether the rain bed is loaded and can be played.</summary>
        public static bool IsLoaded { get; private set; }

        /// <summary>
        /// Whether a rain loop instance currently exists. It may be paused; that is still "playing"
        /// as far as the lot state is concerned, which is why this is not the same question as
        /// whether anything is audible.
        /// </summary>
        public static bool IsRainPlaying { get; private set; }

        /// <summary>
        /// The rain volume for a weather intensity in 0..1: clamped, then scaled by
        /// <see cref="MaxRainVolume"/>. Pure, and the one place that scaling is written down.
        /// </summary>
        /// <param name="intensity">The engine's 0..1 weather intensity.</param>
        public static float RainVolumeFor(float intensity)
        {
            return Math.Clamp(intensity, 0f, 1f) * MaxRainVolume;
        }

        /// <summary>
        /// Decodes the two files under <paramref name="contentPath"/>. Idempotent, and a no-op
        /// when the game was started with sound off.
        /// </summary>
        /// <remarks>
        /// Called once per process from <see cref="GameController.EnterLoading"/>, i.e. on the
        /// game thread with the audio device up (the engine's own sound bank is initialised
        /// during content load, and this decodes ahead of it). Every failure is caught: a
        /// cosmetic sound is not worth a crash, and the log line is enough to tell "the files did
        /// not get copied" apart from "the mixer rejected them".
        /// </remarks>
        /// <param name="contentPath">
        /// The game's content folder, or null to use the folder next to the executable (which is
        /// where the build copies it).
        /// </param>
        public static void Load(string contentPath = null)
        {
            if (IsLoaded) return;

            if (FSOEnvironment.NoSound)
            {
                SimitoneLog.Info("Weather sounds were not loaded: the game is running with -nosound.");
                return;
            }

            var root = string.IsNullOrEmpty(contentPath)
                ? Path.Combine(AppContext.BaseDirectory, "Content")
                : contentPath;

            try
            {
                RainLoopSound = LoadClip(Path.Combine(root, SoundsFolder, RainLoopFileName));
                ThunderSound = LoadClip(Path.Combine(root, SoundsFolder, ThunderFileName));
                IsLoaded = RainLoopSound != null;

                if (!IsLoaded)
                {
                    SimitoneLog.Warn("The rain loop (" + RainLoopFileName + ") could not be loaded from '" + root +
                        "'. Weather will be silent.");
                }
                else
                {
                    SimitoneLog.Info("Weather sounds loaded from '" + root + "'.");
                }
            }
            catch (Exception ex)
            {
                //the audio device itself can be the thing that throws (no output device, a
                //locked mixer). Clear whatever loaded so a later attempt starts from nothing.
                StopRain();
                SafeDispose(ref RainLoopSound);
                SafeDispose(ref ThunderSound);
                IsLoaded = false;
                SimitoneLog.Error("Failed to load the weather sounds.", ex);
            }
        }

        /// <summary>
        /// Reads one clip, or returns null when it is absent or unusable. Never throws.
        /// </summary>
        private static SoundEffect LoadClip(string path)
        {
            if (!File.Exists(path))
            {
                SimitoneLog.Warn("Weather sound file is missing: " + path);
                return null;
            }

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    return SoundEffect.FromStream(stream);
                }
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Weather sound file could not be decoded: " + path, ex);
                return null;
            }
        }

        /// <summary>
        /// Starts the rain bed at <paramref name="intensity"/>, or re-levels it if it is already
        /// running. Calling this every time the weather changes is expected and cheap.
        /// </summary>
        /// <param name="intensity">The engine's 0..1 weather intensity.</param>
        public static void PlayRain(float intensity)
        {
            if (!IsLoaded || RainLoopSound == null) return;

            var volume = RainVolumeFor(intensity);
            try
            {
                if (!IsRainPlaying || RainLoopInstance == null)
                {
                    //a stale instance (a lot teardown that raced the play) is disposed rather
                    //than adopted, so a lot can never leave its loop behind.
                    SafeDisposeInstance();
                    RainLoopInstance = RainLoopSound.CreateInstance();
                    RainLoopInstance.IsLooped = true;
                    IsRainPlaying = true;
                }

                RainLoopInstance.Volume = volume;
                //only a stopped instance is started. Re-playing a playing instance would restart
                //the loop from the top on every weather change, and re-playing a paused one would
                //be heard during a pause.
                if (RainLoopInstance.State == SoundState.Stopped) RainLoopInstance.Play();
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Failed to play the rain loop.", ex);
                SafeDisposeInstance();
            }
        }

        /// <summary>Pauses the rain bed, keeping it loaded and resumable.</summary>
        public static void PauseRain()
        {
            if (!IsRainPlaying || RainLoopInstance == null) return;
            try
            {
                if (RainLoopInstance.State == SoundState.Playing) RainLoopInstance.Pause();
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Failed to pause the rain loop.", ex);
            }
        }

        /// <summary>Resumes a paused rain bed.</summary>
        public static void ResumeRain()
        {
            if (!IsRainPlaying || RainLoopInstance == null) return;
            try
            {
                if (RainLoopInstance.State == SoundState.Paused) RainLoopInstance.Resume();
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Failed to resume the rain loop.", ex);
            }
        }

        /// <summary>
        /// Stops and disposes the rain bed. Safe to call when nothing is playing, and the call a
        /// lot teardown makes so the sound does not follow the player onto another lot.
        /// </summary>
        public static void StopRain()
        {
            SafeDisposeInstance();
        }

        /// <summary>
        /// Plays one thunder crack. Clamped, and silently ignored when the clip is not loaded.
        /// </summary>
        /// <param name="volume">Playback volume in 0..1.</param>
        public static void PlayThunder(float volume = 0.7f)
        {
            if (!IsLoaded || ThunderSound == null) return;
            try
            {
                ThunderSound.Play(Math.Clamp(volume, 0f, 1f), 0f, 0f);
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Failed to play thunder.", ex);
            }
        }

        /// <summary>
        /// Releases every sound this class owns. Safe to call when nothing is loaded.
        /// </summary>
        public static void Unload()
        {
            StopRain();
            SafeDispose(ref RainLoopSound);
            SafeDispose(ref ThunderSound);
            IsLoaded = false;
        }

        private static void SafeDisposeInstance()
        {
            var instance = RainLoopInstance;
            RainLoopInstance = null;
            IsRainPlaying = false;
            if (instance == null) return;
            try
            {
                instance.Stop();
                instance.Dispose();
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Failed to dispose the rain loop instance.", ex);
            }
        }

        private static void SafeDispose(ref SoundEffect sound)
        {
            var owned = sound;
            sound = null;
            if (owned == null) return;
            try
            {
                owned.Dispose();
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Failed to dispose a weather sound.", ex);
            }
        }
    }

    /// <summary>
    /// What the lot wants the weather audio to do, as a diff against what it is already doing.
    ///
    /// <para>
    /// This is the whole vocabulary of <see cref="LotWeatherState.Tick"/>, and it exists so the
    /// screen's per-frame code is a straight translation of the flags into calls, in a fixed
    /// order (stop before start, then pause/resume) that the type itself cannot get wrong.
    /// </para>
    /// </summary>
    [Flags]
    public enum WeatherAudioAction
    {
        /// <summary>Nothing to do.</summary>
        None = 0,

        /// <summary>Start the rain bed, or re-level a running one.</summary>
        PlayRain = 1,

        /// <summary>Stop and dispose the rain bed.</summary>
        StopRain = 2,

        /// <summary>Pause a running rain bed (the game is paused or in buy/build).</summary>
        PauseRain = 4,

        /// <summary>Resume a paused rain bed.</summary>
        ResumeRain = 8,

        /// <summary>Play one thunder crack.</summary>
        PlayThunder = 16,

        /// <summary>Force the terrain to winter, so snowing looks like snowing.</summary>
        ApplySnow = 32,

        /// <summary>Put the terrain back to summer grass.</summary>
        ClearSnow = 64,
    }

    /// <summary>
    /// The cosmetic weather's per-lot state: what the audio is currently doing, how long until
    /// the next thunder crack, whether snow has been forced onto the terrain, and the weather the
    /// lot is waiting to be handed once its blueprint exists.
    ///
    /// <para>
    /// This is deliberately pure - no VM, no world, no audio device - so the decisions that a
    /// wrong answer makes visible (a rain loop that never stops, thunder while the game is paused,
    /// grass left white in July) can be tested directly. The screen reads the engine's
    /// <see cref="WeatherController"/> into a <see cref="Sample"/>, calls <see cref="Tick"/>, and
    /// applies the returned <see cref="WeatherAudioAction"/> flags to <see cref="WeatherSounds"/>
    /// and the terrain.
    /// </para>
    ///
    /// <para>
    /// <b>Where the weather comes from.</b> The engine owns it: <c>weather &lt;type&gt;</c> in the
    /// cheat console writes straight into the lot's <see cref="WeatherController"/>, and the same
    /// value is kept in a small sidecar file next to the user's lot saves
    /// (<see cref="SidecarPath"/>) so it survives a restart. The sidecar is only read when it
    /// exists: a player who has never touched the weather keeps whatever the engine defaults to
    /// rather than being forced into a manual "clear" on every lot load.
    /// </para>
    /// </summary>
    public sealed class LotWeatherState
    {
        /// <summary>
        /// Below this intensity the weather is not worth hearing about: the engine's own particle
        /// swap treats small changes as nothing, and a rain loop at 5% is just noise.
        /// </summary>
        public const float AudibleThreshold = 0.1f;

        /// <summary>
        /// How far the intensity has to move before the rain bed is re-levelled. Without it, a
        /// slowly drifting intensity would re-issue a play every frame.
        /// </summary>
        public const float IntensityEpsilon = 0.05f;

        /// <summary>
        /// Thunder volume as a fraction of the weather intensity: a storm at 100% is a loud crack,
        /// a storm at 20% is a rumble under the rain.
        /// </summary>
        public const float ThunderVolumeScale = 0.8f;

        /// <summary>Shortest gap between two thunder cracks, in seconds.</summary>
        public const float MinThunderGap = 5f;

        /// <summary>Longest gap between two thunder cracks, in seconds.</summary>
        public const float MaxThunderGap = 15f;

        /// <summary>Name of the sidecar file holding the saved weather.</summary>
        public const string SidecarFileName = "cas.weather";

        /// <summary>Folder under the user directory holding Simitone's own lot files.</summary>
        public const string LocalHouseFolder = "LocalHouse";

        /// <summary>
        /// One frame's worth of weather, read out of the engine.
        /// </summary>
        /// <param name="Type">What is falling (or not).</param>
        /// <param name="IsThunder">Whether the weather is a storm.</param>
        /// <param name="Intensity">The engine's 0..1 intensity.</param>
        /// <param name="GameRunning">
        /// False when the game is paused or in buy/build, in which case the rain bed is paused
        /// rather than stopped - a stopped loop would have to be rebuilt on resume.
        /// </param>
        public readonly struct Sample
        {
            /// <summary>What is falling.</summary>
            public readonly WeatherType Type;

            /// <summary>Whether this is a storm.</summary>
            public readonly bool IsThunder;

            /// <summary>Intensity, 0..1.</summary>
            public readonly float Intensity;

            /// <summary>Whether gameplay is advancing.</summary>
            public readonly bool GameRunning;

            /// <summary>Captures one frame of engine weather state.</summary>
            public Sample(WeatherType type, bool isThunder, float intensity, bool gameRunning)
            {
                Type = type;
                IsThunder = isThunder;
                Intensity = intensity;
                GameRunning = gameRunning;
            }
        }

        private readonly Random _random;

        private bool _rainPlaying;
        private bool _rainPaused;
        private bool _thunderActive;
        private bool _snowApplied;
        private float _lastIntensity = -1f;
        private WeatherType _lastType = WeatherType.Rain;
        private bool _lastThunder;
        private float _thunderTimer;
        private short? _pendingWeatherData;

        /// <summary>
        /// Creates a weather state. The random source is injectable so thunder timing can be
        /// asserted rather than waited for.
        /// </summary>
        /// <param name="random">The source for thunder gaps, or null for a fresh one.</param>
        public LotWeatherState(Random random = null)
        {
            _random = random ?? new Random();
        }

        /// <summary>Whether the terrain is currently being held as snow.</summary>
        public bool SnowApplied { get { return _snowApplied; } }

        /// <summary>Whether a rain bed instance is believed to exist.</summary>
        public bool RainPlaying { get { return _rainPlaying; } }

        /// <summary>Whether a saved weather is waiting for the lot's blueprint.</summary>
        public bool HasPendingWeatherData { get { return _pendingWeatherData.HasValue; } }

        /// <summary>
        /// Starts a new lot: forgets everything the last lot was doing and remembers the weather
        /// to hand to the blueprint once it exists.
        ///
        /// <para>
        /// Called from the lot-load path rather than the teardown path, so a lot resumed from a
        /// marshal is treated exactly like one loaded from a template - the marshal carries its
        /// own terrain, and the blueprint is what decides the weather.
        /// </para>
        /// </summary>
        /// <param name="pendingWeatherData">
        /// The weather read out of the sidecar, or null to leave the lot's weather alone (no
        /// sidecar: the engine's own default stands).
        /// </param>
        public void BeginLot(short? pendingWeatherData)
        {
            _rainPlaying = false;
            _rainPaused = false;
            _thunderActive = false;
            _thunderTimer = 0f;
            //the previous lot's terrain went with its world, so there is nothing to un-snow here.
            _snowApplied = false;
            _lastType = WeatherType.Rain;
            _lastThunder = false;
            _lastIntensity = -1f;
            _pendingWeatherData = pendingWeatherData;
        }

        /// <summary>
        /// Ends the current lot, reporting whether the rain bed has to be stopped.
        ///
        /// <para>
        /// Deliberately does <em>not</em> report <see cref="WeatherAudioAction.ClearSnow"/>: the
        /// terrain that was forced white belongs to a world that is about to be disposed, and
        /// writing to it during teardown would only be work thrown away (or, if the world was
        /// already gone, a null reference).
        /// </para>
        /// </summary>
        public WeatherAudioAction EndLot()
        {
            var action = WeatherAudioAction.None;
            if (_rainPlaying)
            {
                _rainPlaying = false;
                _rainPaused = false;
                action |= WeatherAudioAction.StopRain;
            }
            _thunderActive = false;
            _thunderTimer = 0f;
            _snowApplied = false;
            _lastType = WeatherType.Rain;
            _lastThunder = false;
            _lastIntensity = -1f;
            return action;
        }

        /// <summary>
        /// Hands over the weather waiting for a blueprint, clearing it. The caller applies it with
        /// <c>WeatherController.SetWeather</c> once the lot's blueprint exists - a blueprint load
        /// is queued as a VM command, so it is still null on the frame the lot is created.
        /// </summary>
        /// <param name="weatherData">The saved weather, if there was one.</param>
        /// <returns>True when there was a saved weather to apply.</returns>
        public bool TryTakePendingWeatherData(out short weatherData)
        {
            weatherData = 0;
            if (!_pendingWeatherData.HasValue) return false;
            weatherData = _pendingWeatherData.Value;
            _pendingWeatherData = null;
            return true;
        }

        /// <summary>
        /// Decides what the weather audio and the terrain should do this frame, and records the
        /// state that produces the next decision.
        /// </summary>
        /// <remarks>
        /// The order the flags come back in does not matter to the caller, but what is in one
        /// result does: rain can be started and paused on the same frame, and the terrain can be
        /// put back to grass in the same frame that thunder stops.
        /// </remarks>
        /// <param name="sample">This frame's engine weather state.</param>
        /// <param name="deltaSeconds">
        /// Seconds since the last call, used for the thunder gap. A frame time that is
        /// implausible (a long stall, a debugger break) is clamped rather than allowed to
        /// swallow the next fifteen seconds of thunder.
        /// </param>
        public WeatherAudioAction Tick(in Sample sample, float deltaSeconds)
        {
            var action = WeatherAudioAction.None;

            var raining = sample.Type == WeatherType.Rain && sample.Intensity > AudibleThreshold;
            var snowing = sample.Type == WeatherType.Snow && sample.Intensity > AudibleThreshold;
            var thundering = raining && sample.IsThunder;

            //only react to a real change, so a drifting intensity does not re-issue a play every
            //frame (and so a pause/resume pair cannot fight each other).
            var changed = sample.Type != _lastType
                || sample.IsThunder != _lastThunder
                || Math.Abs(sample.Intensity - _lastIntensity) > IntensityEpsilon;

            if (changed)
            {
                if (raining)
                {
                    _rainPlaying = true;
                    action |= WeatherAudioAction.PlayRain;
                }
                else if (_rainPlaying)
                {
                    _rainPlaying = false;
                    _rainPaused = false;
                    action |= WeatherAudioAction.StopRain;
                }
            }

            //paused gameplay pauses the bed rather than stopping it, so resuming is instant
            //instead of re-decoding nothing but re-creating an instance.
            if (_rainPlaying)
            {
                if (!sample.GameRunning && !_rainPaused)
                {
                    _rainPaused = true;
                    action |= WeatherAudioAction.PauseRain;
                }
                else if (sample.GameRunning && _rainPaused)
                {
                    _rainPaused = false;
                    action |= WeatherAudioAction.ResumeRain;
                }
            }

            //thunder runs off a real clock rather than a frame count, so it keeps its rhythm
            //through a frame rate change.
            var thunderNow = thundering && sample.GameRunning;
            if (thunderNow)
            {
                if (!_thunderActive) _thunderTimer = 0f; //the storm arriving is its own crack
                _thunderTimer -= ClampDelta(deltaSeconds);
                if (_thunderTimer <= 0)
                {
                    action |= WeatherAudioAction.PlayThunder;
                    _thunderTimer = MinThunderGap + (float)_random.NextDouble() * (MaxThunderGap - MinThunderGap);
                }
            }
            else if (_thunderActive)
            {
                _thunderTimer = 0f;
            }
            _thunderActive = thunderNow;

            //snowing grass, the way a snowed-in vacation lot looks
            if (snowing)
            {
                if (!_snowApplied)
                {
                    _snowApplied = true;
                    action |= WeatherAudioAction.ApplySnow;
                }
            }
            else if (_snowApplied)
            {
                _snowApplied = false;
                action |= WeatherAudioAction.ClearSnow;
            }

            _lastType = sample.Type;
            _lastThunder = sample.IsThunder;
            _lastIntensity = sample.Intensity;
            return action;
        }

        /// <summary>
        /// Caps a frame time to something a game clock can plausibly produce, so one long stall
        /// does not consume the whole thunder gap at once.
        /// </summary>
        /// <param name="deltaSeconds">The raw frame time.</param>
        public static float ClampDelta(float deltaSeconds)
        {
            if (float.IsNaN(deltaSeconds) || deltaSeconds < 0f) return 0f;
            return Math.Min(deltaSeconds, 0.25f);
        }

        /// <summary>
        /// Where the saved weather lives: one file under the user's Simitone folder, beside the
        /// lot files Simitone caches there.
        /// </summary>
        /// <remarks>
        /// Null for a null/empty user directory, so the result can go straight into
        /// <c>File.Exists</c> and so nothing downstream can turn it into a path relative to the
        /// process working directory - which <c>Program.Main</c> deliberately sets to the install
        /// folder. <see cref="AtomicFileCommit"/> refuses a relative path for exactly that reason,
        /// and this is the upstream half of the same rule.
        /// </remarks>
        /// <param name="userDir">The user's Simitone directory.</param>
        public static string SidecarPath(string userDir)
        {
            if (string.IsNullOrEmpty(userDir)) return null;
            return Path.Combine(userDir, LocalHouseFolder, SidecarFileName);
        }

        /// <summary>
        /// Reads the saved weather, or null when there is nothing usable to read.
        ///
        /// <para>
        /// Null covers three cases on purpose: no sidecar at all (the player has never set the
        /// weather, and the engine's own default stands), a sidecar too short to hold a short, and
        /// one that cannot be read. None of them are worth an error dialog on a lot load, and the
        /// last two are logged so a genuine permissions problem is still visible.
        /// </para>
        /// </summary>
        /// <param name="path">The sidecar path, from <see cref="SidecarPath"/>.</param>
        public static short? ReadSidecar(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < sizeof(short))
                {
                    SimitoneLog.Warn("The saved weather file is too short to hold a value: " + path);
                    return null;
                }
                return BitConverter.ToInt16(bytes, 0);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("Could not read the saved weather.", ex);
                return null;
            }
        }

        /// <summary>
        /// Writes the saved weather, through the same staged/committed write the lot saves use, so
        /// a crash cannot leave a truncated file behind. Never throws: a cosmetic sidecar is not
        /// worth a failed save dialog.
        /// </summary>
        /// <param name="path">The sidecar path, from <see cref="SidecarPath"/>.</param>
        /// <param name="data">The weather data to store.</param>
        public static void WriteSidecar(string path, short data)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                AtomicFileCommit.Write(path, BitConverter.GetBytes(data));
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("Could not save the current weather to '" + path + "'.", ex);
            }
        }
    }

    /// <summary>
    /// Parses the cheat console's <c>weather</c> command into the packed value the engine's
    /// <see cref="WeatherController.SetWeather"/> takes.
    ///
    /// <para>
    /// The wire format is a short, and it is the engine's, not Simitone's:
    /// <list type="bullet">
    /// <item>bit 8 - the weather was set by hand rather than derived from the time of day;</item>
    /// <item>bits 9-10 - the <see cref="WeatherType"/> (0 rain, 1 snow, 2 hail);</item>
    /// <item>bit 11 - thunder;</item>
    /// <item>bits 0-7 - intensity as a percentage.</item>
    /// </list>
    /// <see cref="Encode"/> is the only place that layout is written down.
    /// </para>
    ///
    /// <para>
    /// Commands: <c>weather rain|storm|thunder|snow|hail [0-100]</c>, <c>weather clear</c> and
    /// <c>weather auto</c>. Pure, so the whole table is testable without a VM - and the table is
    /// the part worth testing, because every one of these bits means something the renderer acts
    /// on.
    /// </para>
    /// </summary>
    public static class WeatherCommandParser
    {
        /// <summary>The word that introduces a weather command.</summary>
        public const string CommandName = "weather";

        /// <summary>Intensity used when a command names a type but no strength.</summary>
        public const int DefaultIntensity = 50;

        /// <summary>
        /// Intensity used by <c>weather storm</c> with no strength given. A storm is the one type
        /// that wants to be heard as well as seen, so it defaults louder than the others.
        /// </summary>
        public const int DefaultStormIntensity = 75;

        /// <summary>Bit set when the weather was set by hand.</summary>
        public const int ManualBit = 1 << 8;

        /// <summary>Bit set when the weather is a storm.</summary>
        public const int ThunderBit = 1 << 11;

        /// <summary>Where the weather type lives.</summary>
        public const int TypeShift = 9;

        /// <summary>Where the intensity lives.</summary>
        public const int IntensityMask = 0xFF;

        /// <summary>
        /// Packs a weather into the value the engine reads. Intensity is clamped to 0..100
        /// because the engine reads the low byte as a percentage and would wrap.
        /// </summary>
        /// <param name="type">What is falling.</param>
        /// <param name="isThunder">Whether the weather is a storm.</param>
        /// <param name="intensity">Intensity as a percentage.</param>
        public static short Encode(WeatherType type, bool isThunder, int intensity)
        {
            var value = ManualBit | ((int)type << TypeShift) | (isThunder ? ThunderBit : 0)
                | (Math.Clamp(intensity, 0, 100) & IntensityMask);
            return (short)value;
        }

        /// <summary>
        /// The manual bit of a packed weather, i.e. whether it was set by hand.
        /// </summary>
        /// <param name="data">A packed weather.</param>
        public static bool IsManual(short data)
        {
            return (data & ManualBit) != 0;
        }

        /// <summary>
        /// The type of a packed weather, as the engine decodes it.
        /// </summary>
        /// <param name="data">A packed weather.</param>
        public static WeatherType TypeOf(short data)
        {
            return (WeatherType)((data >> TypeShift) & 3);
        }

        /// <summary>
        /// Whether a packed weather is a storm, as the engine decodes it.
        /// </summary>
        /// <param name="data">A packed weather.</param>
        public static bool IsThunder(short data)
        {
            return (data & ThunderBit) != 0;
        }

        /// <summary>
        /// The intensity of a packed weather, 0..1, as the engine decodes it.
        /// </summary>
        /// <param name="data">A packed weather.</param>
        public static float IntensityOf(short data)
        {
            return (data & IntensityMask) / 100f;
        }

        /// <summary>
        /// A human-readable rendering of a packed weather, for the log. Not shown to the player.
        /// </summary>
        /// <param name="data">A packed weather.</param>
        public static string Describe(short data)
        {
            if (!IsManual(data)) return "automatic";
            var text = TypeOf(data).ToString().ToLowerInvariant();
            if (IsThunder(data)) text += " storm";
            return text + " at " + (data & IntensityMask).ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// Whether a line typed into the cheat console is a weather command. The cheat box routes
        /// on this, so it has to recognise the command without also swallowing a cheat that merely
        /// starts with the same letters.
        /// </summary>
        /// <param name="command">The typed line, repetition marks and all.</param>
        public static bool IsWeatherCommand(string command)
        {
            var parts = Tokenize(command);
            return parts.Count > 0 && string.Equals(parts[0], CommandName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Turns a typed line into the packed weather the engine takes.
        /// </summary>
        /// <remarks>
        /// The repetition marks the cheat console understands (<c>!</c> and <c>;</c>) are
        /// stripped, because repeating a weather change is meaningless - but a line with them
        /// still works rather than being rejected as unrecognised.
        /// </remarks>
        /// <param name="command">The typed line.</param>
        /// <param name="weatherData">The packed weather, or 0 when the line is not a usable one.</param>
        /// <returns>
        /// False for a line that names no type, an unknown type, or is not a weather command at
        /// all - all three are answered with the same "that is not a command" error sound.
        /// </returns>
        public static bool TryParse(string command, out short weatherData)
        {
            weatherData = 0;
            var parts = Tokenize(command);
            if (parts.Count < 2) return false;
            if (!string.Equals(parts[0], CommandName, StringComparison.Ordinal)) return false;

            var type = parts[1];
            int intensity = DefaultIntensity;
            if (parts.Count >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                intensity = Math.Clamp(parsed, 0, 100);
            }

            switch (type)
            {
                case "rain":
                    weatherData = Encode(WeatherType.Rain, false, intensity);
                    return true;
                case "storm":
                case "thunder":
                    if (parts.Count < 3) intensity = DefaultStormIntensity;
                    weatherData = Encode(WeatherType.Rain, true, intensity);
                    return true;
                case "snow":
                    weatherData = Encode(WeatherType.Snow, false, intensity);
                    return true;
                case "hail":
                    weatherData = Encode(WeatherType.Hail, false, intensity);
                    return true;
                case "clear":
                    weatherData = Encode(WeatherType.Rain, false, 0);
                    return true;
                case "auto":
                    //no manual bit: the engine goes back to deriving the weather from the time
                    weatherData = 0;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Splits a typed line into lower-case words, dropping the cheat console's repetition
        /// marks. Whitespace of any kind separates, because a line typed on a soft keyboard
        /// carries tabs.
        /// </summary>
        /// <param name="command">The typed line.</param>
        private static List<string> Tokenize(string command)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(command)) return parts;

            foreach (var raw in command.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw.Replace("!", string.Empty).Replace(";", string.Empty).ToLowerInvariant();
                if (word.Length > 0) parts.Add(word);
            }
            return parts;
        }
    }
}
