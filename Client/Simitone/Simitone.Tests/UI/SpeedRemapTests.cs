using System.Collections.Generic;
using System.Linq;
using Simitone.Client;
using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.Desktop;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Speed buttons are addressed two different ways in the UI:
    ///   - the VM stores the "real" speed multipliers (0 = pause, 1/3/10 = the three speeds);
    ///   - the panels address the four buttons by a 1..4 index, where 4 means pause.
    /// <see cref="UIDesktopUCP.RemapSpeed"/> and <see cref="ReverseRemap"/> are the translation
    /// layer between the two, and both panels build the button-highlight loop out of the result:
    /// <c>for (i = 0; i &lt; 4; i++) Btns[i].Selected = (i + 1 == speed);</c>
    ///
    /// Both tables are public static fields, so these are genuinely reachable without a graphics
    /// device. Nothing here constructs a panel or touches a GraphicsDevice.
    /// </summary>
    public sealed class SpeedRemapTests
    {
        /// <summary>
        /// The four panel-side button indices. 1..3 are the speeds, 4 is pause. Anything
        /// outside this set means the highlight loop in Update() can select no button.
        /// </summary>
        private static readonly int[] PanelButtonIndices = { 1, 2, 3, 4 };

        public static IEnumerable<object[]> SpeedTables()
        {
            yield return new object[] { "UIDesktopUCP" };
            yield return new object[] { "UIClockPanel" };
        }

        private static Dictionary<int, int> Forward(string table)
        {
            return table == "UIDesktopUCP" ? UIDesktopUCP.RemapSpeed : UIClockPanel.RemapSpeed;
        }

        private static Dictionary<int, int> Reverse(string table)
        {
            return table == "UIDesktopUCP" ? UIDesktopUCP.ReverseRemap : UIClockPanel.ReverseRemap;
        }

        [Theory]
        [MemberData(nameof(SpeedTables))]
        public void RemapSpeed_KeysAreTheVmSpeedMultipliers(string table)
        {
            // these are the values the VM reports (0 pause, 1/3/10 speeds). If the VM ever grows
            // a new multiplier this test should fail and the tables should grow with it.
            Assert.Equal(new[] { 0, 1, 3, 10 }, Forward(table).Keys.OrderBy(k => k).ToArray());
        }

        [Theory]
        [MemberData(nameof(SpeedTables))]
        public void RemapSpeed_ValuesCoverEveryPanelButtonIndexExactlyOnce(string table)
        {
            var values = Forward(table).Values.ToArray();

            // bijection, part 1: no two VM speeds may collapse onto the same button, otherwise
            // the highlight loop can never tell them apart.
            Assert.Equal(values.Length, values.Distinct().Count());

            // bijection, part 2: every button must be reachable, otherwise a speed the player
            // can select would light up no button at all.
            Assert.Equal(PanelButtonIndices, values.OrderBy(x => x).ToArray());
        }

        [Theory]
        [MemberData(nameof(SpeedTables))]
        public void ReverseRemap_InvertsRemapSpeed_ForEveryEntry(string table)
        {
            var forward = Forward(table);
            var reverse = Reverse(table);

            Assert.Equal(forward.Count, reverse.Count);
            foreach (var kvp in forward)
            {
                Assert.True(reverse.TryGetValue(kvp.Value, out int back), $"no reverse entry for {kvp.Value}");
                Assert.Equal(kvp.Key, back);
            }
        }

        [Theory]
        [MemberData(nameof(SpeedTables))]
        public void PauseIsTheHighestButtonIndex_AndTheOtherThreeAscendWithTheVmMultiplier(string table)
        {
            var forward = Forward(table);

            // the highlight loop is `Btns[i].Selected = (i + 1 == speed)` over four buttons
            // indexed 0..3, so 4 == pause has to be representable.
            Assert.Equal(4, forward[0]);
            Assert.True(forward[1] < forward[3], "speed 2 should sit between speed 1 and speed 3");
            Assert.True(forward[3] < forward[10], "speed 3 should be the fastest non-paused speed");
        }

        [Fact]
        public void BothPanelsDeclareTheIdenticalMapping()
        {
            // the table is duplicated between UIDesktopUCP and UIClockPanel; if they ever drift
            // the two panels would disagree about which speed button is lit.
            var desktop = UIDesktopUCP.RemapSpeed.OrderBy(kvp => kvp.Key).ToArray();
            var clock = UIClockPanel.RemapSpeed.OrderBy(kvp => kvp.Key).ToArray();
            Assert.Equal(desktop, clock);

            var desktopReverse = UIDesktopUCP.ReverseRemap.OrderBy(kvp => kvp.Key).ToArray();
            var clockReverse = UIClockPanel.ReverseRemap.OrderBy(kvp => kvp.Key).ToArray();
            Assert.Equal(desktopReverse, clockReverse);
        }

        [Fact]
        public void BothPanelTablesAreDerivedFromTheSharedGameplaySpeedMap()
        {
            // The real fix for the duplication: neither panel types the numbers out, so neither can
            // disagree with GameplaySpeed. GameplaySpeed is internal, which is why
            // InternalsVisibleTo exists on the client project - a copy of this table in the test
            // would just be a fourth hand-typed version of the thing this is guarding.
            var shared = GameplaySpeed.RemapSpeed;

            foreach (var table in new[] { UIDesktopUCP.RemapSpeed, UIClockPanel.RemapSpeed })
            {
                Assert.Equal(
                    shared.Where(kvp => kvp.Key != GameplaySpeed.Disabled)
                        .OrderBy(kvp => kvp.Key)
                        .ToArray(),
                    table.OrderBy(kvp => kvp.Key).ToArray());
            }
        }

        [Fact]
        public void TheSharedMapKnowsAboutDisabledAndNeitherPanelTableDoes()
        {
            // GameplaySpeed carries -1 so the VM's "gameplay may not advance" has a name. It is
            // not one of the four buttons, and both panels index their table straight from
            // Math.Max(0, multiplier) - so a -1 that reached the table would be a KeyNotFound
            // inside Update, once per frame. UIClockPanel used to carry a hand-typed copy that was
            // already missing it, which is the same latent crash in a different place.
            Assert.True(GameplaySpeed.RemapSpeed.ContainsKey(GameplaySpeed.Disabled));
            Assert.Equal(-1, GameplaySpeed.Disabled);

            Assert.False(UIDesktopUCP.RemapSpeed.ContainsKey(GameplaySpeed.Disabled));
            Assert.False(UIClockPanel.RemapSpeed.ContainsKey(GameplaySpeed.Disabled));
            Assert.False(UIDesktopUCP.ReverseRemap.ContainsKey(GameplaySpeed.Disabled));
            Assert.False(UIClockPanel.ReverseRemap.ContainsKey(GameplaySpeed.Disabled));
        }

        [Fact]
        public void EverySpeedMultiplierTheVMIsDocumentedToReportIsInThePanelTables()
        {
            // The unguarded-index test, stated as data. Both panels index with
            // RemapSpeed[Math.Max(0, VM.SpeedMultiplier)] and neither guards the result, so an
            // unknown multiplier is a per-frame KeyNotFoundException rather than a wrong button.
            // Any speed the VM grows has to appear in GameplaySpeed.RemapSpeed.
            int[] vmSpeeds = { 0, 1, 3, 10, -1 };
            var shared = GameplaySpeed.RemapSpeed;

            foreach (var speed in vmSpeeds)
            {
                Assert.True(shared.ContainsKey(speed), $"the shared map has no entry for multiplier {speed}");
            }
            foreach (var table in new[] { UIDesktopUCP.RemapSpeed, UIClockPanel.RemapSpeed })
            {
                foreach (var speed in vmSpeeds.Where(s => s != GameplaySpeed.Disabled))
                {
                    Assert.True(table.ContainsKey(speed), $"a panel table has no entry for multiplier {speed}");
                }
            }
        }

        [Fact]
        public void BothPanelTablesAreReadOnly()
        {
            // They used to be plain `public static Dictionary` fields, so any caller anywhere could
            // add a key, change one, or clear the lot. A shared lookup table is not per-instance
            // state and nothing has a reason to write to it.
            var type = typeof(UIDesktopUCP);
            foreach (var name in new[] { "RemapSpeed", "ReverseRemap" })
            {
                var field = type.GetField(name);
                Assert.NotNull(field);
                Assert.True(field.IsInitOnly, name + " must not be assignable");
            }
            var clock = typeof(UIClockPanel);
            foreach (var name in new[] { "RemapSpeed", "ReverseRemap" })
            {
                var field = clock.GetField(name);
                Assert.NotNull(field);
                Assert.True(field.IsInitOnly, "UIClockPanel." + name + " must not be assignable");
            }
        }

        [Fact]
        public void BothPanelsShareOneFloorNameArray()
        {
            // UIMainPanel and UIDesktopUCP used to initialise two `public static string[]` from the
            // same five string ids, which agreed until one of them was edited. One array, and it
            // is readonly, is what makes "the desktop clock and the panel beside it can never be
            // given different words for the same floor" true rather than aspirational.
            Assert.Same(UIMainPanel.FloorNames, UIDesktopUCP.FloorNames);

            foreach (var type in new[] { typeof(UIMainPanel), typeof(UIDesktopUCP) })
            {
                var field = type.GetField("FloorNames");
                Assert.NotNull(field);
                Assert.True(field.IsInitOnly, type.Name + ".FloorNames must not be assignable");
            }
        }

        [Fact]
        public void FloorNames_HasOneEntryPerSelectableFloor()
        {
            // UIDesktopUCP.Update() indexes FloorNames with `LastFloor - 1` where LastFloor is
            // the lot level, and disables the up/down buttons at exactly levels 1 and 5.
            Assert.Equal(5, UIDesktopUCP.FloorNames.Length);
            Assert.Equal(new[] { "1st", "2nd", "3rd", "4th", "5th" }, UIDesktopUCP.FloorNames);
        }
    }
}
