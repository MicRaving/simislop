using FSO.Client;
using FSO.Common;
using FSO.HIT;
using FSO.HIT.Model;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// The one group of sounds the Options ▸ Sound page drives, and where its level is kept.
    ///
    /// <para>
    /// There are four of them, and they are the four the sound engine already separates:
    /// <see cref="HITVolumeGroup.FX"/> (sound effects), <c>MUSIC</c>, <c>VOX</c> and
    /// <c>AMBIENCE</c>. Each is a <c>byte</c> 0-10 on <see cref="GlobalSettings"/>
    /// (<c>FXVolume</c>, <c>MusicVolume</c>, <c>VoxVolume</c>, <c>AmbienceVolume</c>), and each
    /// is handed to <see cref="HITVM.SetMasterVolume"/> as a 0-1 gain. Those four properties have
    /// been on the settings object since FreeSO declared them, and the game applied them once at
    /// startup - which is what the empty Sound page left the player with: a preference set in the
    /// ini that no control in the running game could reach.
    /// </para>
    ///
    /// <para>
    /// Everything about the mapping from a stored level to what the sound engine hears lives
    /// here rather than at the call site, so the startup path, the page and the tests cannot
    /// disagree about it. <see cref="Gain"/> is the whole of it: a tenth of the range is a tenth
    /// of the gain, clamped, so a hand-edited ini cannot turn a group up past full.
    /// </para>
    ///
    /// <para>
    /// <b>Mute is Simitone's own, and it is not a level.</b> Dragging a slider to zero mutes a
    /// group, but that is also how a player turns the music off for an evening and back on
    /// tomorrow without hunting for the slider again. So the mute is a separate flag kept beside
    /// the level, in a second small ini of our own (the same reasoning, and the same shape, as
    /// <see cref="SunCycleSetting"/>'s): <c>GlobalSettings</c> is FreeSO's to declare and has no
    /// mute key, and borrowing an unrelated one would quietly change what that key means for
    /// whoever wants it next. <see cref="FSO.UI.GlobalSettings.FXVolume"/> and friends are left
    /// exactly as they are, and only the two files are written.
    /// </para>
    /// </summary>
    public static class SoundVolumeSetting
    {
        /// <summary>
        /// The quietest a slider goes, and the value that means silence: a level of zero is
        /// zero gain, so it is already a mute. Used by the row to decide whether moving a slider
        /// off the stop is an unmute.
        /// </summary>
        public const int MinVolume = 0;

        /// <summary>
        /// The loudest a slider goes, and the level the engine takes as full volume. Matches the
        /// ini's own default of 10 for three of the four groups.
        /// </summary>
        public const int MaxVolume = 10;

        /// <summary>The ini key prefix for one channel's mute flag.</summary>
        public const string MuteKeyPrefix = "Mute";

        /// <summary>The file the mute flags are stored in, beside FreeSO's <c>config.ini</c>.</summary>
        public const string FileName = "simitone-sound.ini";

        /// <summary>
        /// The comment written on the first line of a <c>simitone-sound.ini</c> this build creates.
        /// </summary>
        internal const string HeadingComment =
            "Simitone Sound Settings. The volume levels are in config.ini.";

        /// <summary>Stored key for the sound-effects mute flag.</summary>
        internal const string MuteFXKey = MuteKeyPrefix + "FX";

        /// <summary>Stored key for the music mute flag.</summary>
        internal const string MuteMusicKey = MuteKeyPrefix + "Music";

        /// <summary>Stored key for the voice mute flag.</summary>
        internal const string MuteVoiceKey = MuteKeyPrefix + "Voice";

        /// <summary>Stored key for the ambience mute flag.</summary>
        internal const string MuteAmbienceKey = MuteKeyPrefix + "Ambience";

        /// <summary>
        /// A level, as the sound engine wants it: 0-1. Clamped, so an ini that has been
        /// hand-edited past 10 cannot make a group louder than the mixer allows.
        /// </summary>
        /// <param name="volume">A stored level, 0-<see cref="MaxVolume"/>.</param>
        public static float Gain(int volume)
        {
            return Math.Clamp(volume, MinVolume, MaxVolume) / (float)MaxVolume;
        }

        /// <summary>
        /// A level brought into the range a slider can actually show, for the same reason
        /// <see cref="Gain"/> clamps: the ini is user input.
        /// </summary>
        /// <param name="volume">A stored level, possibly out of range.</param>
        public static int Clamp(int volume)
        {
            return Math.Clamp(volume, MinVolume, MaxVolume);
        }

        private static readonly object Gate = new object();
        private static Dictionary<SoundChannel, bool> cachedMutes;

        /// <summary>
        /// The mute flags, read from disk the first time they are asked for and cached after
        /// that.
        ///
        /// <remarks>
        /// Lazy for the same reason <see cref="SunCycleSetting"/> loads lazily: the page reads
        /// them when it is built and <see cref="Apply"/> at startup, and neither is a moment
        /// where an eager load would be more correct. A test process has no user directory and
        /// no running game, and this is the shape that survives one.
        /// </remarks>
        /// </summary>
        private static Dictionary<SoundChannel, bool> Mutes
        {
            get
            {
                lock (Gate)
                {
                    if (cachedMutes == null) cachedMutes = Load();
                    return cachedMutes;
                }
            }
        }

        /// <summary>Whether a group is currently silenced.</summary>
        /// <param name="channel">The group.</param>
        public static bool IsMuted(SoundChannel channel)
        {
            return Mutes.TryGetValue(channel, out bool muted) && muted;
        }

        /// <summary>
        /// Records a mute choice.
        ///
        /// <remarks>
        /// Neither saving nor the live push is done here, and both are the caller's job: the
        /// Options page saves the settings object on every change, applies all four groups in one
        /// go through <see cref="Apply"/>, and writes this file through <see cref="Save"/>. Doing
        /// the push here as well would mean a second, redundant pass - and, worse, a second
        /// answer to "which settings object is the live one", which is the question the single
        /// apply exists to make impossible.
        /// </remarks>
        /// <param name="channel">The group to mute or unmute.</param>
        /// <param name="muted">Whether it should be silent.</param>
        public static void SetMuted(SoundChannel channel, bool muted)
        {
            lock (Gate)
            {
                var next = new Dictionary<SoundChannel, bool>(Mutes);
                next[channel] = muted;
                cachedMutes = next;
            }
            SimitoneLog.Info("Sound: " + channel + (muted ? " muted." : " unmuted."));
        }

        /// <summary>Writes the mute flags to the sidecar ini. Never throws.</summary>
        /// <remarks>
        /// A save that fails is reported, not swallowed: the mute flags stay right for the rest
        /// of the session because the in-memory copy is what <see cref="IsMuted"/> reads, so a
        /// silent failure looks exactly like a checkbox that does nothing until the next launch
        /// quietly reverts it. <see cref="SimitoneIniFile"/> is used rather than
        /// <c>IniConfig</c> precisely because the latter swallows the write failure itself and
        /// leaves nothing for the wrapper here to log.
        /// </remarks>
        public static void Save()
        {
            string path = null;
            try
            {
                path = ConfigFilePath(FSOEnvironment.UserDir);
                if (path == null) return;
                Ini(path).SetAll(MuteFlags());
            }
            catch (Exception e)
            {
                // A settings file that cannot be written is not worth refusing to run over.
                SimitoneLog.Warn("SoundVolumeSetting: could not save the sound mute flags to '"
                    + (path ?? "(no user directory)") + "'. They will reset next launch.", e);
            }
        }

        /// <summary>
        /// The sidecar, opened for the four mute keys this setting owns. The file is written as a
        /// unit, so one read-merge-write covers all four flags rather than four rewrites.
        /// </summary>
        private static SimitoneIniFile Ini(string path)
        {
            return new SimitoneIniFile(path, HeadingComment);
        }

        /// <summary>The four stored keys, in the order the ini lists them.</summary>
        private static readonly (string Key, SoundChannel Channel)[] MuteKeys =
        {
            (MuteFXKey, SoundChannel.SoundEffects),
            (MuteMusicKey, SoundChannel.Music),
            (MuteVoiceKey, SoundChannel.Voice),
            (MuteAmbienceKey, SoundChannel.Ambience)
        };

        /// <summary>Every channel's flag as a key/value pair, for one read-merge-write.</summary>
        private static List<KeyValuePair<string, string>> MuteFlags()
        {
            var result = new List<KeyValuePair<string, string>>(MuteKeys.Length);
            foreach (var (key, channel) in MuteKeys)
            {
                result.Add(new KeyValuePair<string, string>(key, SimitoneIniFile.FormatBool(IsMuted(channel))));
            }
            return result;
        }

        /// <summary>
        /// Full path of the sidecar ini for a user directory, or null for a null/empty one, so
        /// the result can go straight into <see cref="File.Exists"/> without a second guard.
        /// </summary>
        public static string ConfigFilePath(string userDir)
        {
            if (string.IsNullOrEmpty(userDir)) return null;
            return Path.Combine(userDir, FileName);
        }

        private static Dictionary<SoundChannel, bool> Load()
        {
            var mutes = new Dictionary<SoundChannel, bool>();
            foreach (SoundChannel channel in Enum.GetValues<SoundChannel>()) mutes[channel] = false;
            try
            {
                var path = ConfigFilePath(FSOEnvironment.UserDir);
                if (path == null) return mutes;
                var ini = Ini(path);
                foreach (var (key, channel) in MuteKeys)
                {
                    // A key the file does not carry is "never configured", which is unmuted -
                    // the same answer a value it does not recognise gets, and the same answer
                    // the file's own defaults gave before.
                    mutes[channel] = ResolveMute(ini.Get(key));
                }
            }
            catch (Exception)
            {
                // An unreadable settings file is the same as one nobody has written yet.
            }
            return mutes;
        }

        /// <summary>
        /// A stored mute flag turned into a decision: anything that is not recognisably
        /// <c>true</c> is unmuted. "An unknown value is the default" is the same contract
        /// <c>SunCycleSetting.Resolve</c> uses, and for the same reason - this is a file a
        /// player can edit by hand.
        /// </summary>
        public static bool ResolveMute(string stored)
        {
            if (stored == null) return false;
            var trimmed = stored.Trim();
            if (trimmed.Length == 0) return false;
            if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return false;
        }

        /// <summary>
        /// The stored level for a group, read off the settings object.
        /// </summary>
        /// <param name="settings">The settings to read; null answers 0.</param>
        /// <param name="channel">The group.</param>
        public static int LevelOf(GlobalSettings settings, SoundChannel channel)
        {
            if (settings == null) return MinVolume;
            return Clamp(channel switch
            {
                SoundChannel.SoundEffects => settings.FXVolume,
                SoundChannel.Music => settings.MusicVolume,
                SoundChannel.Voice => settings.VoxVolume,
                _ => settings.AmbienceVolume
            });
        }

        /// <summary>
        /// Writes a level for a group, clamped into the range the page offers.
        /// </summary>
        /// <param name="settings">The settings to write; null is ignored.</param>
        /// <param name="channel">The group.</param>
        /// <param name="volume">The level to store.</param>
        public static void SetLevel(GlobalSettings settings, SoundChannel channel, int volume)
        {
            if (settings == null) return;
            var level = (byte)Clamp(volume);
            switch (channel)
            {
                case SoundChannel.SoundEffects: settings.FXVolume = level; break;
                case SoundChannel.Music: settings.MusicVolume = level; break;
                case SoundChannel.Voice: settings.VoxVolume = level; break;
                default: settings.AmbienceVolume = level; break;
            }
        }

        /// <summary>
        /// The <see cref="GlobalSettings"/> property a group's level is stored in, as the name
        /// the Options page's reflection and its change notification both key off.
        /// </summary>
        /// <param name="channel">The group.</param>
        public static string SettingsKeyOf(SoundChannel channel)
        {
            return channel switch
            {
                SoundChannel.SoundEffects => nameof(GlobalSettings.FXVolume),
                SoundChannel.Music => nameof(GlobalSettings.MusicVolume),
                SoundChannel.Voice => nameof(GlobalSettings.VoxVolume),
                _ => nameof(GlobalSettings.AmbienceVolume)
            };
        }

        /// <summary>
        /// The group a settings property belongs to, or null when the property is not one of the
        /// four. The mirror of <see cref="SettingsKeyOf"/>, and what
        /// <see cref="ChannelOf"/>'s callers use to recognise a changed row.
        /// </summary>
        /// <param name="settingsKey">A <c>GlobalSettings</c> property name.</param>
        public static SoundChannel? ChannelOf(string settingsKey)
        {
            if (string.IsNullOrEmpty(settingsKey)) return null;
            foreach (SoundChannel channel in Enum.GetValues<SoundChannel>())
            {
                if (SettingsKeyOf(channel) == settingsKey) return channel;
            }
            return null;
        }

        /// <summary>
        /// Whether <paramref name="descriptor"/> is the Options row for a volume group.
        /// </summary>
        /// <remarks>
        /// Matching on the property name rather than on instance identity, for the reason
        /// <see cref="FreeWillSetting.Describes"/> gives: the descriptor list is rebuilt every
        /// time the page re-lays itself out, so there is no stable object to compare against, and
        /// a silent mismatch here would not throw - it would leave a slider that saves a
        /// preference the mixer never hears.
        /// </remarks>
        public static bool Describes(UIOptionsSubpanelBase.UIOptionDescriprion descriptor)
        {
            return ChannelOf(descriptor?.AttachedProperty?.Name) != null;
        }

        /// <summary>
        /// Hands the four levels, and the four mute flags, to the sound engine.
        ///
        /// <remarks>
        /// This is the whole of the live path, and both callers go through it: the Options page
        /// when a row changes, and the game's startup, which used to open-code the same four
        /// calls. A muted group is applied as silence, whatever its level says - the level is
        /// the player's setting and is left alone.
        /// </remarks>
        /// <param name="settings">The settings to read the levels from; null is ignored.</param>
        public static void Apply(GlobalSettings settings)
        {
            if (settings == null) return;
            var hit = HITVM.Get();
            // The engine is not there before the sound system has initialised, and a headless
            // test process never initialises it at all. Silence is not an error.
            if (hit == null) return;
            foreach (SoundChannel channel in Enum.GetValues<SoundChannel>())
            {
                var gain = IsMuted(channel) ? 0f : Gain(LevelOf(settings, channel));
                hit.SetMasterVolume(EngineGroupOf(channel), gain);
            }
        }

        /// <summary>
        /// Unmutes a group when the player moves its slider off the stop, so that a level the
        /// player can see is a level they can hear.
        ///
        /// <remarks>
        /// Only ever unsets the flag: leaving the slider at zero while unmuted is a legitimate
        /// thing to do, and <see cref="Apply"/> already treats zero as silence, so nothing has
        /// to be forced.
        /// </remarks>
        /// <param name="channel">The group whose slider moved.</param>
        /// <param name="volume">The level it moved to.</param>
        /// <returns>Whether this call changed the mute state.</returns>
        public static bool UnmuteIfRaised(SoundChannel channel, int volume)
        {
            if (Clamp(volume) <= MinVolume) return false;
            if (!IsMuted(channel)) return false;
            SetMuted(channel, false);
            return true;
        }

        /// <summary>
        /// Wording for a level, as the page's tooltip reads it out. Localised, because the
        /// number alone ("3") says nothing to a player who cannot see the track.
        /// </summary>
        /// <param name="volume">A stored level.</param>
        public static string DescribeLevel(int volume)
        {
            var level = Clamp(volume);
            if (level == MinVolume) return SimitoneStrings.Get("973", "Off");
            if (level == MaxVolume) return SimitoneStrings.Get("974", "Full");
            return SimitoneStrings.Format("975", "%d of %d", level.ToString(), MaxVolume.ToString());
        }

        /// <summary>Wording for a group's mute checkbox.</summary>
        /// <param name="muted">Whether the group is silenced.</param>
        public static string DescribeMute(bool muted)
        {
            return muted
                ? SimitoneStrings.Get("977", "Muted - click to unmute")
                : SimitoneStrings.Get("976", "Mute this group");
        }

        /// <summary>
        /// The sound engine's own group for one of ours. The two enums happen to be in the same
        /// order, and this is the one place that says so - named differently from
        /// <see cref="ChannelOf(string)"/> rather than overloaded, because a null argument would
        /// then be ambiguous and the two return types have nothing to do with each other.
        /// </summary>
        /// <param name="channel">One of the four groups.</param>
        public static HITVolumeGroup EngineGroupOf(SoundChannel channel)
        {
            return channel switch
            {
                SoundChannel.SoundEffects => HITVolumeGroup.FX,
                SoundChannel.Music => HITVolumeGroup.MUSIC,
                SoundChannel.Voice => HITVolumeGroup.VOX,
                _ => HITVolumeGroup.AMBIENCE
            };
        }
    }

    /// <summary>
    /// The four groups of sound the Options ▸ Sound page offers, in the order the page lists
    /// them and the order the sound engine numbers them in.
    /// </summary>
    public enum SoundChannel
    {
        /// <summary>Sound effects: the lot's own noises, clicks, the sounds objects make.</summary>
        SoundEffects = 0,

        /// <summary>Background music.</summary>
        Music = 1,

        /// <summary>Voice: the Sims' own voices and narration.</summary>
        Voice = 2,

        /// <summary>Ambience: weather and the sounds the lot plays at you.</summary>
        Ambience = 3
    }
}
