using FSO.Client;
using FSO.Common;
using FSO.LotView;
using System;
using System.IO;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// The stored shape of the Dynamic Sun choice, as a property.
    /// </summary>
    /// <remarks>
    /// Exists only so the Options page's descriptor has a <see cref="System.Reflection.PropertyInfo"/>
    /// to name the setting with - the page's reflection is built around
    /// <c>Type.GetProperty</c>, and a setting with no property has no name to be found by. No
    /// instance of this is ever created and no value is ever read from or written to one; the
    /// row's reads and writes go through <c>SunCycleSetting</c>. See
    /// <c>UIOptionsSubpanelBase.UIExternalBoolPropOptionDescription</c>.
    /// </remarks>
    public sealed class SunCycleOption
    {
        public bool SunCycle { get; set; }
    }

    /// <summary>
    /// Simitone's own half of the "Dynamic Sun" graphics option: the one place that knows how the
    /// choice is stored and how it reaches the renderer.
    ///
    /// <para>
    /// The engine half is <c>FSO.LotView.WorldConfig.SunCycle</c>, which
    /// <c>SunLightingModel</c> reads on every clock tick. Off, the lot is lit by the fixed-elevation
    /// sweep the game has always used; on, the sun gets an elevation cycle and is coupled to the
    /// weather. <see cref="GraphicsOptions.Apply"/> pushes the flag across, and the Options page
    /// row is the only thing that writes it.
    /// </para>
    ///
    /// <para>
    /// <b>Why it is not a <c>GlobalSettings</c> property.</b> The Options pages reflect over the
    /// settings object, so a row normally names a <c>GlobalSettings</c> property - and
    /// <c>FSO.UI.GlobalSettings</c> is not this file's to edit, so a new key cannot be added to it.
    /// The alternatives were all worse than a second ini: every existing key either already means
    /// something (<c>Weather</c>, <c>LightingMode</c>, <c>DirectionalLight3D</c>), belongs to the
    /// city renderer rather than the lot view (<c>CityShadows</c>, <c>ShadowQuality</c>), or is the
    /// engine flag this feature is not (<c>Lighting</c>). Borrowed keys do not stay borrowed - the
    /// next person to need <c>Lighting</c> finds a boolean that turns the sun on.
    /// </para>
    ///
    /// <para>
    /// So the choice lives in a small ini beside <c>config.ini</c>, holding one key, written
    /// through <see cref="SimitoneIniFile"/> - the one writer for the whole sidecar, which changes
    /// only this key and leaves every other line of the file exactly as it was. That matters
    /// because <see cref="Neighbourhood.NeighbourhoodViewSetting"/> is the other half of the same
    /// file: the two used to each own an <c>IniConfig</c> subclass, and <c>IniConfig.Save</c>
    /// truncates the file and writes only the subclass's own properties, so turning Dynamic Sun off
    /// erased the neighbourhood view and vice versa. Nothing here can stop the game starting: a
    /// missing, unreadable or corrupt file is the same as the default, which is on.
    /// </para>
    /// </summary>
    public static class SunCycleSetting
    {
        /// <summary>The ini key the choice is stored under.</summary>
        public const string Key = "SunCycle";

        /// <summary>
        /// The file it is stored in, beside FreeSO's <c>config.ini</c> rather than inside it.
        /// Shared with <see cref="Neighbourhood.NeighbourhoodViewSetting"/>, which is why every
        /// read and write goes through <see cref="SimitoneIniFile"/>.
        /// </summary>
        public const string FileName = "simitone.ini";

        /// <summary>
        /// Whether the choice is on when nothing has chosen. On, because in 3D this is what makes
        /// the sun move: <c>WorldConfig.SunCycle</c> is the flag
        /// <c>SunLightingModel</c> reads on every clock tick, so with it off a 3D lot is lit by a
        /// fixed-elevation sweep and the day never progresses. It used to be off, back when the
        /// feature was something a player had to ask for; the row is still there to turn it off.
        /// </summary>
        public const bool DefaultEnabled = true;

        /// <summary>The comment written on the first line of a <c>simitone.ini</c> this build creates.</summary>
        internal const string HeadingComment =
            "Simitone Settings File. Simitone's own keys; FreeSO's are in config.ini.";

        private static readonly object Gate = new object();
        private static bool? cached;

        /// <summary>
        /// The live choice, read from disk the first time it is asked for.
        /// </summary>
        /// <remarks>
        /// Loaded lazily rather than at startup on purpose: the engine flag is read once per clock
        /// tick by the lot view, and the Options page reads it when it is built, so there is no
        /// point where an eager load would be more correct - and a lazy load means this class
        /// works in a test with no user directory and no game around it.
        /// </remarks>
        public static bool Enabled
        {
            get
            {
                lock (Gate)
                {
                    if (!cached.HasValue) cached = Load();
                    return cached.Value;
                }
            }
        }

        /// <summary>
        /// Records <paramref name="enabled"/> and pushes it into the live renderer config.
        /// </summary>
        /// <remarks>
        /// This writes the one flag, deliberately: rebuilding a whole
        /// <see cref="GraphicsPreferences"/> from defaults here would reset the player's other
        /// graphics settings, and the Options page's own
        /// <c>UIOptionsSubpanelBase.OnSettingsChanged</c> already re-applies the full snapshot
        /// through <see cref="GraphicsOptions.Apply"/> a moment later. Saving is the caller's job
        /// unless <paramref name="save"/> says otherwise, because that same method saves
        /// everything else the page touches.
        /// </remarks>
        public static void SetEnabled(bool enabled, bool save = false)
        {
            lock (Gate)
            {
                cached = enabled;
            }
            WorldConfig.Current.SunCycle = enabled;
            if (save) Save();
        }

        /// <summary>
        /// The stored raw value turned into a decision.
        /// </summary>
        /// <remarks>
        /// A hand-edited or corrupt file is the same as "never configured", which is now on. The
        /// same garbage-in-known-good-out contract as
        /// <c>StartupSettings.ResolveLightingMode</c>, and for the same reason: this is user input
        /// and the alternative is a boolean nobody can predict.
        /// </remarks>
        public static bool Resolve(string stored)
        {
            if (stored == null) return DefaultEnabled;
            var trimmed = stored.Trim();
            if (trimmed.Length == 0) return DefaultEnabled;
            if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return DefaultEnabled;
        }

        /// <summary>
        /// Full path of the sidecar ini for a user directory, or null for a null/empty one so the
        /// result can go straight into <see cref="File.Exists"/> without a second guard.
        /// </summary>
        public static string ConfigFilePath(string userDir)
        {
            if (string.IsNullOrEmpty(userDir)) return null;
            return Path.Combine(userDir, FileName);
        }

        /// <summary>
        /// The sidecar, opened for the one key this setting owns. Every read and every write of
        /// <c>simitone.ini</c> goes through here, so the two settings that share the file cannot
        /// each bring their own <c>IniConfig</c> subclass and truncate the other's half.
        /// </summary>
        internal static SimitoneIniFile Ini(string path)
        {
            return new SimitoneIniFile(path, HeadingComment);
        }

        private static bool Load()
        {
            try
            {
                var path = ConfigFilePath(FSOEnvironment.UserDir);
                if (path == null) return DefaultEnabled;
                return Resolve(Ini(path).Get(Key));
            }
            catch (Exception)
            {
                // A settings file that cannot be read is not worth refusing to start over.
                return DefaultEnabled;
            }
        }

        private static void Save()
        {
            string path = null;
            try
            {
                path = ConfigFilePath(FSOEnvironment.UserDir);
                if (path == null) return;
                Ini(path).Set(Key, SimitoneIniFile.FormatBool(Enabled));
            }
            catch (Exception e)
            {
                // The choice is already live in memory, so a sidecar that cannot be written is
                // not worth refusing to run over - but it must not be silent either, or the
                // player watches a checkbox that appears to do nothing and finds the old value
                // back on the next launch.
                SimitoneLog.Warn("SunCycleSetting: could not save the Dynamic Sun choice to '"
                    + (path ?? "(no user directory)") + "'. It will reset next launch.", e);
            }
        }

        /// <summary>
        /// Whether <paramref name="descriptor"/> is the Options row for this setting.
        /// </summary>
        /// <remarks>
        /// Matching on the property name rather than on instance identity, for the reason
        /// <c>FreeWillSetting.Describes</c> gives: the descriptor list is rebuilt every time the
        /// page re-lays itself out, so there is no stable object to compare against, and a silent
        /// mismatch would not throw - it would leave a checkbox that saves and does nothing.
        /// </remarks>
        public static bool Describes(UIOptionsSubpanelBase.UIOptionDescriprion descriptor)
        {
            return descriptor?.AttachedProperty?.Name == Key;
        }

        /// <summary>Explanation of what the setting does, for the row's tooltip.</summary>
        public static string DescribeEffect()
        {
            return "Lets the sun rise, cross the sky and set, instead of staying at one height all " +
                "day, and makes rain and snow flatten, dim and grey it. Outdoor shadows change " +
                "length through the day and soften under cloud. Off, the lot is lit exactly as it has " +
                "always been.";
        }
    }
}
