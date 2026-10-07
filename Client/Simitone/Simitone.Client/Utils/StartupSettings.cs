using FSO.Client;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Pure decision helpers for Simitone's startup settings bootstrap.
    ///
    /// Simitone used to unconditionally stamp a fixed set of graphics values into
    /// <c>GlobalSettings.Default</c> on every launch. That made the in-game settings
    /// persistence useless: anything the player changed was silently reset on the next
    /// start. The hardcoded values are still the right choice for a *first* run (there is
    /// no saved config to honour yet, and FreeSO's own ini defaults are tuned for TSO, not
    /// for a TS1 lot), so they are now applied only when no config file existed before we
    /// started. Everything else in the startup path is untouched.
    ///
    /// This type deliberately performs no IO and reads no globals so it can be unit
    /// tested headlessly; the caller does the <see cref="File.Exists"/> probe.
    /// </summary>
    public static class StartupSettings
    {
        /// <summary>
        /// Name of the settings file that <c>GlobalSettings.Default</c> reads and writes.
        /// Mirrors the literal used inside FreeSO's <c>GlobalSettings.Default</c> accessor,
        /// which is built as <c>Path.Combine(FSOEnvironment.UserDir, "config.ini")</c>.
        /// </summary>
        public const string ConfigFileName = "config.ini";

        /// <summary>
        /// The lighting level Simitone has always started with: "ultra" in
        /// <c>FSO.LotView.WorldConfig</c> terms (off, advanced, +3d wall, ultra).
        /// </summary>
        public const int DefaultLightingMode = 3;

        /// <summary>Graphics default: use the more expensive shader path.</summary>
        public const bool DefaultComplexShaders = true;

        /// <summary>Graphics default: allow camera/rotation transitions.</summary>
        public const bool DefaultEnableTransitions = true;

        /// <summary>
        /// What <c>-aa</c> switches antialiasing to: 4x MSAA. Not
        /// <see cref="MaxAntiAlias"/>, which is the other (2x SSAA) step - named here so the
        /// command-line override is a value the reader can look up rather than a bare 1.
        /// </summary>
        public const int ForcedAntiAlias = 1;

        /// <summary>Lowest and highest legal <c>LightingMode</c> values.</summary>
        public const int MinLightingMode = 0;

        /// <summary>Highest legal <c>LightingMode</c> value.</summary>
        public const int MaxLightingMode = 3;

        /// <summary>Lowest and highest legal <c>AntiAlias</c> values (off, 4x MSAA, 2x SSAA).</summary>
        public const int MinAntiAlias = 0;

        /// <summary>Highest legal <c>AntiAlias</c> value.</summary>
        public const int MaxAntiAlias = 2;

        /// <summary>
        /// Should the hardcoded first-run graphics defaults be written over the current
        /// <c>GlobalSettings.Default</c> values?
        /// </summary>
        /// <param name="configFileExistedBeforeStartup">
        /// Whether the player's <c>config.ini</c> was already on disk before the process
        /// touched <c>GlobalSettings.Default</c>. Note that FreeSO's <c>IniConfig.Load</c>
        /// *creates* the file when it is missing, so this must be probed before the first
        /// <c>GlobalSettings.Default</c> access of the run, not after.
        /// </param>
        /// <returns>
        /// True on a genuine first run (no saved config), false when a config already
        /// exists and its stored values must be left alone.
        /// </returns>
        public static bool ShouldApplyGraphicsDefaults(bool configFileExistedBeforeStartup)
        {
            return !configFileExistedBeforeStartup;
        }

        /// <summary>
        /// Full path of the settings file that <c>GlobalSettings.Default</c> will use for a
        /// given user directory. Returns null for a null/empty directory so callers can
        /// hand the result straight to <c>File.Exists</c> (which treats null as "missing")
        /// without a separate guard.
        /// </summary>
        public static string ConfigFilePath(string userDir)
        {
            if (string.IsNullOrEmpty(userDir)) return null;
            return Path.Combine(userDir, ConfigFileName);
        }

        /// <summary>
        /// Clamp a stored <c>LightingMode</c> into the range the renderer understands.
        ///
        /// FreeSO ships <c>LightingMode</c> with an ini default of <c>-1</c>, and
        /// <c>IniConfig.Load</c> writes that default out verbatim for anyone whose config
        /// predates the key. Honouring it would silently disable every lighting feature
        /// (<c>WorldConfig.AdvancedLighting</c> is <c>LightingMode &gt; 0</c>), which is a
        /// visible regression for existing players now that startup no longer overwrites the
        /// value. Out-of-range values are therefore treated as "never configured" and
        /// replaced with <see cref="DefaultLightingMode"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note the opposite policy in <see cref="ResolveAntiAlias"/>.</b> The two are
        /// siblings and are deliberately not interchangeable: an unreadable lighting mode is
        /// replaced with the <em>default</em> (ultra), an unreadable antialiasing level with the
        /// <em>cheapest</em> (off). Both are right for their own key - lighting has an ini default
        /// of -1 that means "off", which is the one answer that must not be taken at face value,
        /// while antialiasing has no such trap and turning a corrupt value into the most expensive
        /// setting would be a worse answer than turning it into the least. Same "garbage in,
        /// known-good out" contract, opposite idea of which end is safe. Do not unify them.
        /// </remarks>
        public static int ResolveLightingMode(int storedLightingMode)
        {
            if (storedLightingMode < MinLightingMode || storedLightingMode > MaxLightingMode)
                return DefaultLightingMode;
            return storedLightingMode;
        }

        /// <summary>
        /// Clamp a stored <c>AntiAlias</c> into the range the renderer understands
        /// (off / 4x MSAA / 2x SSAA). Hand-edited or corrupt values collapse to "off" rather
        /// than to an unexpected quality setting, on the "garbage in, known-good out" contract
        /// <see cref="ResolveLightingMode"/> also follows - but to the <em>other</em> end of the
        /// range, deliberately. See that method's remarks.
        /// </summary>
        public static int ResolveAntiAlias(int storedAntiAlias)
        {
            if (storedAntiAlias < MinAntiAlias || storedAntiAlias > MaxAntiAlias) return MinAntiAlias;
            return storedAntiAlias;
        }

        /// <summary>
        /// The first-run graphics stamp, in one place.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The values come from <see cref="GraphicsOptions.Defaults"/> - the snapshot documented as
        /// "what a player who has changed nothing ends up with" - rather than from literals
        /// repeated here, so "the defaults" has one answer. Only the four keys that differ from
        /// FreeSO's own ini defaults are written; for the rest the ini default already *is* the
        /// Simitone default, and rewriting them would be a claim the code cannot back up. That is
        /// pinned by <c>StartupSettingsTests</c>, which compares the untouched keys against a
        /// freshly-created <see cref="GlobalSettings"/> so the subset cannot quietly grow stale.
        /// </para>
        ///
        /// <para>
        /// <b>Call this before anything can save the settings object.</b>
        /// <c>GlobalSettings.Default.Save()</c> rewrites the whole file, so a stamp that ran after
        /// the first save would never reach disk - and because the "is this a first run" probe
        /// only answers honestly before the first <c>GlobalSettings.Default</c> touch, there is no
        /// second chance: the next launch would read a config that exists, skip the stamp, and
        /// keep whatever the ini default had written. That is not hypothetical; it is exactly what
        /// the previous shape of <c>Program.Main</c> did, because the install resolution that saves
        /// ran before the stamp.
        /// </para>
        /// </remarks>
        /// <param name="settings">The settings object to stamp. Null is ignored.</param>
        /// <param name="configFileExistedBeforeStartup">
        /// Whether the player's <c>config.ini</c> was already on disk before the process touched
        /// the settings object.
        /// </param>
        /// <param name="forceAntiAlias">Whether <c>-aa</c> was passed, which outranks a stored
        /// preference and is honoured on a first run and every launch after it.</param>
        public static void ApplyGraphicsDefaults(GlobalSettings settings,
            bool configFileExistedBeforeStartup, bool forceAntiAlias)
        {
            if (settings == null) return;

            var defaults = GraphicsOptions.Defaults;
            if (ShouldApplyGraphicsDefaults(configFileExistedBeforeStartup))
            {
                settings.LightingMode = defaults.LightingMode;
                settings.ComplexShaders = defaults.ComplexShaders;
                settings.EnableTransitions = defaults.EnableTransitions;
                settings.AntiAlias = forceAntiAlias ? ForcedAntiAlias : defaults.AntiAlias;
                return;
            }

            // FreeSO's ini default for LightingMode is -1 and that value is written verbatim into
            // any config that predates the key. Copying it straight into WorldConfig would turn
            // every lighting feature off, so substitute the startup default when the stored value
            // is out of the renderer's range. Everything else is the player's.
            settings.LightingMode = ResolveLightingMode(settings.LightingMode);
            if (forceAntiAlias) settings.AntiAlias = ForcedAntiAlias;
        }
    }
}
