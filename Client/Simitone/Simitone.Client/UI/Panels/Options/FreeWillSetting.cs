using FSO.Client;
using FSO.SimAntics;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// Simitone's half of the "Accurate Free Will" option: the one place that knows how the
    /// choice is stored and how it reaches the engine.
    ///
    /// <para>
    /// The engine half is FreeSO PR #290 and it is deliberately tiny: a single
    /// <see cref="VM.FreeWillEnabled"/> static flag, consulted by
    /// <c>FSO.SimAntics.VMFindBestAction</c> before a Sim is allowed to pick an action for
    /// itself. With it off, a Sim in the player's family gets no autonomous action at all - it
    /// stands there until the player directs it. Visitors (<c>PersonType == 1</c>) and pets are
    /// explicitly excluded from that gate and keep acting on their own either way, so "off" is a
    /// pause button for your own household, not a freeze on the lot.
    /// </para>
    ///
    /// <para>
    /// That flag is process-global rather than per-VM, and FreeSO does not persist it. FreeSO
    /// does ship the matching <see cref="GlobalSettings.TS1FreeWill"/> property (ini key
    /// <c>TS1FreeWill</c>, default on) and nothing in FreeSO reads it, so Simitone owns it: this
    /// type seeds the flag from the setting at startup and writes back to it when the player
    /// changes the setting. Every path goes through here rather than assigning
    /// <see cref="VM.FreeWillEnabled"/> directly, so the live flag and the ini cannot drift
    /// apart - a change saved to config but not to the flag (or the reverse) would be a setting
    /// that silently reverts or silently does nothing.
    /// </para>
    /// </summary>
    public static class FreeWillSetting
    {
        /// <summary>
        /// Name of the <see cref="GlobalSettings"/> property that persists the choice. Used as
        /// the reflection key for the Options page descriptor, and to recognise the setting
        /// when it comes back out of a change notification.
        /// </summary>
        public const string SettingsKey = "TS1FreeWill";

        /// <summary>
        /// Whether Sims in the player's family are currently choosing their own actions. Read
        /// live off the engine flag rather than off the settings object, so the UI reflects
        /// what the lot is actually doing.
        /// </summary>
        public static bool Enabled => VM.FreeWillEnabled;

        /// <summary>
        /// Seeds the engine flag from the saved setting. Called once at startup, before any lot
        /// is loaded, because <see cref="VM.FreeWillEnabled"/> is a process-global that would
        /// otherwise sit at its own default for the whole session regardless of the ini.
        /// </summary>
        /// <param name="settings">The settings to read; ignored when null.</param>
        public static void InitializeFromSettings(GlobalSettings settings)
        {
            if (settings == null) return;
            SetEnabled(settings.TS1FreeWill, settings);
        }

        /// <summary>
        /// Applies <paramref name="enabled"/> to the engine flag and records it on
        /// <paramref name="settings"/>, without saving.
        /// </summary>
        /// <remarks>
        /// Saving is left to the caller because the Options page already saves the whole
        /// settings object on every change (see
        /// <c>UIOptionsSubpanelBase.OnSettingsChanged</c>).
        /// </remarks>
        /// <param name="enabled">The state to leave the game in.</param>
        /// <param name="settings">Settings to record the choice on; ignored when null.</param>
        public static void SetEnabled(bool enabled, GlobalSettings settings)
        {
            if (VM.FreeWillEnabled != enabled)
                SimitoneLog.Info(enabled
                    ? "Free will enabled: family Sims choose their own actions."
                    : "Free will disabled: family Sims wait to be directed (visitors and pets are unaffected).");

            VM.FreeWillEnabled = enabled;
            if (settings != null) settings.TS1FreeWill = enabled;
        }

        /// <summary>
        /// Whether <paramref name="descriptor"/> is the Options row for this setting.
        /// </summary>
        /// <remarks>
        /// The Gameplay page is handed a list of the descriptors that changed and has to pick
        /// out the one that needs the extra engine step. Matching on the property name rather
        /// than on instance identity is what the page's reflection is built on - the descriptor
        /// is rebuilt every time the page re-lays itself out, so there is no stable object to
        /// compare against. Kept here rather than inlined so the match is testable: a mismatch
        /// would not throw, it would leave the switch saving a preference the VM never reads.
        /// </remarks>
        public static bool Describes(UIOptionsSubpanelBase.UIOptionDescriprion descriptor)
        {
            return descriptor?.AttachedProperty?.Name == SettingsKey;
        }
    }
}
