using Simitone.Client.Utils;
using System;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in the fallback policy for locating The Sims 1. This used to be an inline chain of
    /// <c>if</c>s in <c>WindowsLocator.FindTheSims1</c> with live
    /// <c>Microsoft.Win32.Registry</c> reads woven through it, which meant the branch that
    /// actually matters - the machine with no Sims installed at all - could only be reached on a
    /// machine with no Sims installed. <see cref="GameInstallLadder"/> takes the ordering as a
    /// pure function of three probes, so every rung and the dead end are reachable here.
    ///
    /// The probes are asserted to be short-circuiting, not merely ordered: a wrong answer on a
    /// machine that <i>does</i> have a portable install is the expensive failure, because it
    /// silently launches the game against the wrong GameData.
    /// </summary>
    public sealed class GameInstallLadderTests
    {
        /// <summary>
        /// Fails the test if a probe that should never have been consulted was called. Each test
        /// tracks its own invocations, so the flag has to be reset by the caller.
        /// </summary>
        private sealed class Probe
        {
            private readonly string result;
            private bool called;

            public Probe(string result)
            {
                this.result = result;
            }

            /// <summary>The value the probe answers with, whether or not it gets asked.</summary>
            public string Result => result;

            public bool WasCalled => called;

            public string Invoke()
            {
                called = true;
                return result;
            }
        }

        [Fact]
        public void ARelativeInstallWinsAndNoProbeIsConsulted()
        {
            var maxis = new Probe("C:/Program Files (x86)/Maxis/The Sims/");
            var steam = new Probe("D:/Steam/steamapps/common/The Sims Legacy Collection/");

            var found = GameInstallLadder.FirstHit("../The Sims/", maxis.Invoke, steam.Invoke);

            // Returned verbatim: this is the developer's own folder next to the executable, so the
            // ladder must not normalise or otherwise "fix" it.
            Assert.Equal("../The Sims/", found);
            Assert.False(maxis.WasCalled, "a portable install must short-circuit the registry probe");
            Assert.False(steam.WasCalled, "a portable install must short-circuit the Steam probe");
        }

        [Fact]
        public void TheMaxisRegistryBeatsSteamWhenThereIsNoRelativeInstall()
        {
            var maxis = new Probe("C:/Games/The Sims/");
            var steam = new Probe("D:/Steam/steamapps/common/The Sims Legacy Collection/");

            var found = GameInstallLadder.FirstHit(null, maxis.Invoke, steam.Invoke);

            Assert.Equal("C:/Games/The Sims/", found);
            Assert.True(maxis.WasCalled);
            Assert.False(steam.WasCalled, "Steam must not be probed once the installer entry hits");
        }

        [Fact]
        public void SteamIsUsedWhenNeitherTheRelativeDirNorTheRegistryHits()
        {
            var maxis = new Probe(null);
            var steam = new Probe("D:/Steam/steamapps/common/The Sims Legacy Collection/");

            var found = GameInstallLadder.FirstHit(null, maxis.Invoke, steam.Invoke);

            Assert.Equal("D:/Steam/steamapps/common/The Sims Legacy Collection/", found);
            Assert.True(maxis.WasCalled);
            Assert.True(steam.WasCalled);
        }

        [Fact]
        public void TheDefaultRetailPathIsReturnedWhenNothingIsFound()
        {
            var maxis = new Probe(null);
            var steam = new Probe(null);

            var found = GameInstallLadder.FirstHit(null, maxis.Invoke, steam.Invoke);

            // The caller boots on whatever comes back, so this rung cannot be null; it is the same
            // directory the old inline fallback named, in the forward-slash form every other rung
            // produces. Program.Main validates it against GameData\Behavior.iff and shows the
            // "could not find" dialog if it is not really an install.
            Assert.Equal("C:/Program Files (x86)/Maxis/The Sims/", found);
            Assert.Equal(GameInstallLadder.DefaultInstallDir, found);
            Assert.True(maxis.WasCalled);
            Assert.True(steam.WasCalled);
        }

        [Fact]
        public void AProbeThatAnswersNullIsTreatedAsNotFound()
        {
            // This is the contract the callers depend on: the registry reader reports a missing key
            // as null rather than throwing, and a Steam probe reports "not installed" as null. If
            // a null answer were treated as a hit, the game would try to boot from a null path
            // instead of falling through to the next rung.
            var found = GameInstallLadder.FirstHit(null, () => null, () => "D:/Steam/common/The Sims Legacy Collection/");

            Assert.Equal("D:/Steam/common/The Sims Legacy Collection/", found);
        }

        [Fact]
        public void AProbeResultIsNotSecondGuessed()
        {
            // Documented deliberately: the ladder orders the rungs and nothing else. It has no
            // opinion about what a valid path looks like, so it does not grow a second, untested
            // layer of validation. A probe that answers with an empty string has answered the
            // question it was asked, and the probes themselves are the place that decides whether
            // a value is usable.
            var found = GameInstallLadder.FirstHit(null, () => "", () => "D:/Steam/common/The Sims Legacy Collection/");

            Assert.Equal(string.Empty, found);
        }

        [Fact]
        public void TheFallbackIsUnchangedWhenBothProbesAreAbsent()
        {
            // A null delegate is treated the same as a delegate that answers null, so a platform
            // that has no Steam concept at all can pass nothing for the rungs it does not have.
            var found = GameInstallLadder.FirstHit(null, null, null);

            Assert.Equal(GameInstallLadder.DefaultInstallDir, found);
        }
    }
}
