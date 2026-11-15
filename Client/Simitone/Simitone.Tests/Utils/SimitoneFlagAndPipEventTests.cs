using Simitone.Client.UI.Panels.EODs;
using Simitone.Client.Utils;
using System;
using System.IO;
using Xunit;

namespace Simitone.Tests.Utils
{
    public class SimitoneIniFlagTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "simitone-flag-" + Guid.NewGuid().ToString("N"));

        public SimitoneIniFlagTests() { Directory.CreateDirectory(dir); }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        private string IniPath => Path.Combine(dir, "simitone.ini");

        [Fact]
        public void ANewFlagIsItsDefaultWhenNothingIsStored()
        {
            Assert.False(new SimitoneIniFlag("DebugKeys", false, () => IniPath).Enabled);
            Assert.True(new SimitoneIniFlag("PictureInPicture", true, () => IniPath).Enabled);
        }

        [Fact]
        public void TheDebugKeysDefaultOffAndPictureInPictureDefaultsOn()
        {
            Assert.False(SimitoneFlags.DebugKeys.DefaultValue);
            Assert.True(SimitoneFlags.PictureInPicture.DefaultValue);
        }

        [Theory]
        [InlineData("true", false, true)]
        [InlineData(" TRUE ", false, true)]
        [InlineData("false", true, false)]
        [InlineData("garbage", true, true)]
        [InlineData("garbage", false, false)]
        [InlineData("", true, true)]
        [InlineData(null, false, false)]
        public void ResolveUnderstandsOnlyTrueAndFalse(string stored, bool defaultValue, bool expected)
        {
            Assert.Equal(expected, SimitoneIniFlag.Resolve(stored, defaultValue));
        }

        [Fact]
        public void ASavedValueSurvivesAReloadAndLeavesOtherKeysAlone()
        {
            File.WriteAllText(IniPath, "Other=keep\r\n");
            var flag = new SimitoneIniFlag("DebugKeys", false, () => IniPath);
            flag.SetEnabled(true, save: true);

            var fresh = new SimitoneIniFlag("DebugKeys", false, () => IniPath);
            Assert.True(fresh.Enabled);
            Assert.Contains("Other=keep", File.ReadAllText(IniPath));
        }

        [Fact]
        public void AFlagEditedOnDiskIsSeenAfterReload()
        {
            var flag = new SimitoneIniFlag("DebugKeys", false, () => IniPath);
            Assert.False(flag.Enabled);
            File.WriteAllText(IniPath, "DebugKeys=true\r\n");
            Assert.False(flag.Enabled); //cached
            flag.Reload();
            Assert.True(flag.Enabled);
        }

        [Fact]
        public void NoUserDirectoryMeansTheDefaultAndSavingIsHarmless()
        {
            var flag = new SimitoneIniFlag("PictureInPicture", true, () => null);
            Assert.True(flag.Enabled);
            flag.SetEnabled(false, save: true);
            Assert.False(flag.Enabled);
        }
    }

    public class PictureInPictureEventFilterTests
    {
        private const uint Fire = PictureInPictureEventFilter.FireGuid;

        [Theory]
        [InlineData(7, 1, -1, true, Fire, true)]                 //Fire.iff Panic
        [InlineData(7, 1, 150, true, PictureInPictureEventFilter.SnakeGuid, true)] //Snake Panic
        [InlineData(7, 1, 0, true, Fire, false)]                 //clearing the balloon
        [InlineData(7, 1, -1, false, Fire, false)]               //not a sim
        [InlineData(7, 0, 100, true, Fire, false)]               //thought balloon, not an alarm
        [InlineData(5, 1, -1, true, Fire, false)]                //Headline group = object status icons
        [InlineData(3, 1, 500, true, Fire, false)]               //motive balloon (visitors)
        [InlineData(8, 1, 100, true, Fire, false)]               //route failure
        public void OnlyAnAlarmBalloonOverAFireOrSnakeQualifies(int group, int type, int duration, bool sim, uint icon, bool expected)
        {
            Assert.Equal(expected, PictureInPictureEventFilter.IsNotable(group, type, duration, sim, icon));
        }

        [Fact]
        public void AnExclamationWithAnotherIconOrNoIconIsNotNotable()
        {
            Assert.False(PictureInPictureEventFilter.IsNotable(7, 1, 100, true, 0x1234)); //wake-up balloon
            Assert.False(PictureInPictureEventFilter.IsNotable(7, 1, 100, true, null));
        }
    }

    public class PictureInPictureEventGateTests
    {
        private const double Aged = PictureInPictureEventGate.WarmUpSeconds + 1;

        [Fact]
        public void AnEventAfterWarmUpIsAccepted()
        {
            var gate = new PictureInPictureEventGate();
            Assert.True(gate.TryAccept(100, 1, 100, Aged, false, false));
        }

        [Fact]
        public void EventsDuringWarmUpAreIgnored()
        {
            var gate = new PictureInPictureEventGate();
            Assert.False(gate.TryAccept(100, 1, 100, PictureInPictureEventGate.WarmUpSeconds - 0.1, false, false));
        }

        [Fact]
        public void AStaleEventIsDropped()
        {
            var gate = new PictureInPictureEventGate();
            Assert.False(gate.TryAccept(100, 1, 100 - PictureInPictureEventGate.StaleSeconds - 0.1, Aged, false, false));
        }

        [Fact]
        public void TheControlledSimAndABusyWindowNeverQualify()
        {
            var gate = new PictureInPictureEventGate();
            Assert.False(gate.TryAccept(100, 1, 100, Aged, true, false));
            Assert.False(gate.TryAccept(100, 1, 100, Aged, false, true));
            Assert.True(gate.TryAccept(100, 1, 100, Aged, false, false)); //refusals recorded nothing
        }

        [Fact]
        public void TheGlobalCooldownSpacesDifferentSims()
        {
            var gate = new PictureInPictureEventGate();
            Assert.True(gate.TryAccept(100, 1, 100, Aged, false, false));
            Assert.False(gate.TryAccept(105, 2, 105, Aged, false, false));
            Assert.True(gate.TryAccept(100 + PictureInPictureEventGate.GlobalCooldownSeconds, 2,
                100 + PictureInPictureEventGate.GlobalCooldownSeconds, Aged, false, false));
        }

        [Fact]
        public void TheSameSimWaitsLongerThanAnotherSim()
        {
            var gate = new PictureInPictureEventGate();
            Assert.True(gate.TryAccept(100, 1, 100, Aged, false, false));
            var t = 100 + PictureInPictureEventGate.GlobalCooldownSeconds + 1;
            Assert.False(gate.TryAccept(t, 1, t, Aged, false, false));
            Assert.True(gate.TryAccept(t, 2, t, Aged, false, false));
            t = 100 + PictureInPictureEventGate.SubjectCooldownSeconds + PictureInPictureEventGate.GlobalCooldownSeconds + 2;
            Assert.True(gate.TryAccept(t, 1, t, Aged, false, false));
        }

        [Fact]
        public void ManySubjectsDoNotGrowTheHistoryWithoutBound()
        {
            var gate = new PictureInPictureEventGate();
            double t = 1000;
            for (int i = 0; i < 500; i++)
            {
                t += PictureInPictureEventGate.SubjectCooldownSeconds + 1;
                Assert.True(gate.TryAccept(t, i, t, Aged, false, false));
            }
        }

        [Fact]
        public void ResetForgetsEverything()
        {
            var gate = new PictureInPictureEventGate();
            Assert.True(gate.TryAccept(100, 1, 100, Aged, false, false));
            gate.Reset();
            Assert.True(gate.TryAccept(101, 1, 101, Aged, false, false));
        }
    }

    public class PictureInPictureEventQueueTests
    {
        [Fact]
        public void PostedEventsComeBackOldestFirstAndTheQueueEmpties()
        {
            PictureInPictureEventQueue.Clear();
            PictureInPictureEventQueue.Post("a", 1, "A");
            PictureInPictureEventQueue.Post("b", 2, "B");
            var events = PictureInPictureEventQueue.Drain();
            Assert.Equal(new[] { 1, 2 }, new[] { events[0].SubjectId, events[1].SubjectId });
            Assert.Empty(PictureInPictureEventQueue.Drain());
        }

        [Fact]
        public void TheQueueIsBounded()
        {
            PictureInPictureEventQueue.Clear();
            for (int i = 0; i < PictureInPictureEventQueue.Capacity + 5; i++)
                PictureInPictureEventQueue.Post(null, i, "x");
            var events = PictureInPictureEventQueue.Drain();
            Assert.Equal(PictureInPictureEventQueue.Capacity, events.Count);
            Assert.Equal(5, events[0].SubjectId);
        }
    }

    public class EODLayoutToolTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "simitone-eod-" + Guid.NewGuid().ToString("N"));

        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        [Fact]
        public void OnlyTheFirstArgumentSelectsTheTool()
        {
            Assert.True(EODLayoutTool.IsRequested(new[] { "-regen-eod-layouts", "x" }));
            Assert.True(EODLayoutTool.IsRequested(new[] { "-REGEN-EOD-LAYOUTS" }));
            Assert.False(EODLayoutTool.IsRequested(new[] { "-ide" }));
            Assert.False(EODLayoutTool.IsRequested(new string[0]));
            Assert.False(EODLayoutTool.IsRequested(null));
        }

        [Fact]
        public void MissingDirectoryIsAUsageError()
        {
            var output = new StringWriter();
            Assert.Equal(2, EODLayoutTool.Run(new[] { "-regen-eod-layouts" }, output));
            Assert.Contains("usage", output.ToString());
        }

        [Fact]
        public void WritesEveryLayoutAndPrintsTheCaptionBlock()
        {
            var output = new StringWriter();
            Assert.Equal(0, EODLayoutTool.Run(new[] { "-regen-eod-layouts", dir }, output));
            foreach (var spec in EODLayouts.All)
                Assert.True(File.Exists(Path.Combine(dir, "UI", "uiscripts", spec.FileName)), spec.FileName);
            Assert.Contains("_f900_simitone.cst", output.ToString());
        }
    }
}
