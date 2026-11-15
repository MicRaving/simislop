using FSO.Client;
using FSO.SimAntics;
using Simitone.Client.UI.Panels.Options;
using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="FreeWillSetting"/> - the Simitone half of the "Accurate Free Will"
    /// option.
    ///
    /// <para>
    /// The setting is unusual for Simitone in that the value the player chooses is not what the
    /// game reads: <c>VM.FreeWillEnabled</c> (FreeSO PR #290) is a process-global static that
    /// nothing persists, and the ini property <c>TS1FreeWill</c> is a parking space FreeSO
    /// declares but never reads. These tests pin the ways that can quietly break as a result:
    /// that the engine hook is still the static bool we assign to, that the reflection key still
    /// names a real bool property (the Options page finds the setting by name), that the live
    /// flag follows the setting, and that the setting follows the live flag and reaches the
    /// file.
    /// </para>
    ///
    /// <para>
    /// Tests that touch the engine flag save and restore the original in
    /// <see cref="Dispose"/>, and settings objects are built against a throwaway temp file
    /// rather than the developer's real <c>config.ini</c>.
    /// </para>
    /// </summary>
    public sealed class FreeWillSettingTests : IDisposable
    {
        private readonly bool _originalFlag;
        private readonly string _tempRoot;

        public FreeWillSettingTests()
        {
            _originalFlag = VM.FreeWillEnabled;
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-freewill-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            VM.FreeWillEnabled = _originalFlag;
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // best effort - a leftover temp directory must never fail the suite
            }
        }

        /// <summary>A settings object backed by its own throwaway ini, never the real one.</summary>
        private GlobalSettings NewSettings(string name = "config.ini")
        {
            return new GlobalSettings(Path.Combine(_tempRoot, name));
        }

        // ---- The engine hook -----------------------------------------------------------------

        /// <summary>
        /// Everything above depends on <c>VM.FreeWillEnabled</c> being the static bool that
        /// <c>VMFindBestAction</c> reads on every autonomy decision. If FreeSO ever renames it,
        /// changes its type or makes it read-only, the setting would still save and still redraw
        /// itself and would silently do nothing - a compile error here is a much cheaper
        /// outcome, which is why this reaches for the field by name.
        /// </summary>
        [Fact]
        public void EngineHook_IsAPublicSettableStaticBoolOnVM()
        {
            var field = typeof(VM).GetField("FreeWillEnabled", BindingFlags.Public | BindingFlags.Static);

            Assert.NotNull(field);
            Assert.Equal(typeof(bool), field.FieldType);
            Assert.False(field.IsInitOnly, "the setting has to be able to assign it");
            Assert.True(field.IsStatic);
        }

        // ---- The reflection key the Options page descriptor resolves ----------------------------

        /// <summary>
        /// The Gameplay page finds the setting by looking <c>SettingsKey</c> up on
        /// <see cref="GlobalSettings"/>, so a rename on either side would turn the row into a
        /// silent <c>NullReferenceException</c> when the page was built.
        /// </summary>
        [Fact]
        public void SettingsKey_ResolvesToABoolPropertyOnGlobalSettings()
        {
            var property = typeof(GlobalSettings).GetProperty(FreeWillSetting.SettingsKey);
            Assert.NotNull(property);
            Assert.Equal(typeof(bool), property.PropertyType);
            Assert.True(property.CanRead);
            Assert.True(property.CanWrite);
        }

        /// <summary>
        /// The key is also how a change notification recognises the setting, so it has to be the
        /// property's own name rather than a friendlier label.
        /// </summary>
        [Fact]
        public void SettingsKey_StartsWithTheSameLetterAsThePropertyItNames()
        {
            // nameof() would make an exact-equality assertion here a tautology, so what is
            // actually worth pinning is that the two are describing the same thing: a key that
            // has drifted off the property name still finds *a* bool and would silently wire the
            // row to the wrong setting.
            Assert.StartsWith(nameof(GlobalSettings.TS1FreeWill).Substring(0, 4), FreeWillSetting.SettingsKey);
        }

        /// <summary>
        /// FreeSO ships <c>TS1FreeWill</c> with an ini default of "true", and that default is
        /// what every existing player's config.ini now holds. If the engine ever flips it, a
        /// player who has never opened the menu would silently change behaviour, so it is worth
        /// a test that says out loud that "on" is the intended out-of-the-box state.
        /// </summary>
        [Fact]
        public void AFreshConfigStartsWithFreeWillOn()
        {
            var settings = NewSettings("fresh.ini");
            Assert.True(settings.TS1FreeWill);

            VM.FreeWillEnabled = false;
            FreeWillSetting.InitializeFromSettings(settings);
            Assert.True(FreeWillSetting.Enabled);
        }

        // ---- The engine flag -----------------------------------------------------------------

        [Fact]
        public void SetEnabled_WritesTheEngineFlag()
        {
            FreeWillSetting.SetEnabled(false, null);
            Assert.False(VM.FreeWillEnabled);
            Assert.False(FreeWillSetting.Enabled);

            FreeWillSetting.SetEnabled(true, null);
            Assert.True(VM.FreeWillEnabled);
            Assert.True(FreeWillSetting.Enabled);
        }

        /// <summary>
        /// Setting the flag alone would leave the ini holding the old value, so the next launch
        /// would come up in the state the player last un-set rather than the one they are
        /// looking at.
        /// </summary>
        [Fact]
        public void SetEnabled_RecordsTheChoiceOnTheSettings()
        {
            var settings = NewSettings();

            FreeWillSetting.SetEnabled(false, settings);
            Assert.False(settings.TS1FreeWill);

            FreeWillSetting.SetEnabled(true, settings);
            Assert.True(settings.TS1FreeWill);
        }

        /// <summary>Null settings are tolerated: the startup path can be handed nothing.</summary>
        [Fact]
        public void SetEnabled_ToleratesNullSettings()
        {
            FreeWillSetting.SetEnabled(false, null);
            Assert.False(VM.FreeWillEnabled);
        }

        [Fact]
        public void InitializeFromSettings_SeedsTheFlagFromTheSavedChoice()
        {
            var settings = NewSettings();
            settings.TS1FreeWill = false;
            VM.FreeWillEnabled = true;

            FreeWillSetting.InitializeFromSettings(settings);

            Assert.False(VM.FreeWillEnabled);
        }

        [Fact]
        public void InitializeFromSettings_LeavesTheFlagAloneWhenThereAreNoSettings()
        {
            VM.FreeWillEnabled = false;

            FreeWillSetting.InitializeFromSettings(null);

            Assert.False(VM.FreeWillEnabled);
        }

        // ---- Recognising the Options row ------------------------------------------------------

        /// <summary>
        /// The Gameplay page is handed a list of changed descriptors and picks the free will one
        /// out by property name. A miss here would not throw: the checkbox would go on saving a
        /// preference the VM never reads, and the row would look like it worked.
        /// </summary>
        [Fact]
        public void Describes_RecognisesTheFreeWillRow()
        {
            var descriptor = new UIOptionsSubpanelBase.UIBoolPropOptionDescription(
                typeof(GlobalSettings).GetProperty(FreeWillSetting.SettingsKey));

            Assert.True(FreeWillSetting.Describes(descriptor));
        }

        [Fact]
        public void Describes_IgnoresTheOtherRowsOnTheSamePage()
        {
            var edgeScroll = new UIOptionsSubpanelBase.UIBoolPropOptionDescription(
                typeof(GlobalSettings).GetProperty("EdgeScroll"));

            Assert.False(FreeWillSetting.Describes(edgeScroll));
        }

        /// <summary>Same page, one change at a time - a null must not be mistaken for a match.</summary>
        [Fact]
        public void Describes_ToleratesNull()
        {
            Assert.False(FreeWillSetting.Describes(null));
        }

    }
}
