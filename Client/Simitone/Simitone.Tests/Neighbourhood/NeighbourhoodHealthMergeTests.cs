/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The two-witness rule for the facts only the wrapper can see: how many draws threw, and
    /// whether the device has gone.
    ///
    /// <para>
    /// <b>The bug these pin.</b> The scene implemented <c>INeighbourhoodRenderSignal</c> with a
    /// hard-coded zero for <c>DrawFailures</c> and <c>false</c> for <c>DeviceLost</c>, and the panel
    /// preferred the scene's signal over <c>NeighbourhoodWorldProxy</c>'s real counters. So the
    /// proxy caught the exception, incremented its count, and the backdrop rule - which has a whole
    /// branch for exactly that - never saw either one. The merge below is the fix, and it is a pure
    /// function so "the proxy's count reaches the rule" is an assertion rather than an observation.
    /// </para>
    /// </summary>
    public class NeighbourhoodHealthMergeTests
    {
        [Fact]
        public void TheDrawFailureCountIsTheLargerOfTheTwoWitnesses()
        {
            //The proxy counted and the scene did not is the whole case; the other direction is
            //kept so a scene that swallows its own failures cannot be under-counted either.
            Assert.Equal(2, NeighbourhoodWorldHealth.MergeDrawFailures(0, 2));
            Assert.Equal(3, NeighbourhoodWorldHealth.MergeDrawFailures(3, 1));
            Assert.Equal(3, NeighbourhoodWorldHealth.MergeDrawFailures(3, 3));
            Assert.Equal(0, NeighbourhoodWorldHealth.MergeDrawFailures(0, 0));
        }

        [Fact]
        public void AProxysDrawFailuresReachTheBackdropRule()
        {
            //The regression, end to end. Two consecutive failures were caught by the proxy, not by
            //the scene, so the merged count is what has to demote the world to the painted map.
            var health = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 24576,
                groundQuads: 65536, roadQuads: 4096, lotCount: 12,
                drawFailures: NeighbourhoodWorldHealth.MergeDrawFailures(
                    0, NeighbourhoodBackdropRule.DrawFailureLimit),
                secondsSinceAttach: 30d);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, health);

            Assert.Equal(NeighbourhoodWorldFault.DrawFailed, decision.Fault);
            Assert.True(decision.UseBitmap);
            Assert.True(decision.FellBack);
        }

        [Fact]
        public void EitherWitnessCanSayTheDeviceIsGone()
        {
            //A lost device is not always an exception, so neither side is authoritative and either
            //one seeing it is enough.
            Assert.True(NeighbourhoodWorldHealth.MergeDeviceLost(false, true));
            Assert.True(NeighbourhoodWorldHealth.MergeDeviceLost(true, false));
            Assert.False(NeighbourhoodWorldHealth.MergeDeviceLost(false, false));
        }

        [Fact]
        public void TheFailureMessagePrefersTheOneThatNamedAnException()
        {
            //The proxy's message is the exception a draw threw; the scene's is usually its
            //construction failure. Naming the exception is the useful half.
            Assert.Equal("proxy", NeighbourhoodWorldHealth.MergeDrawFailure("scene", "proxy", null));
            Assert.Equal("scene", NeighbourhoodWorldHealth.MergeDrawFailure("scene", null, null));
            Assert.Equal("fallback", NeighbourhoodWorldHealth.MergeDrawFailure(null, null, "fallback"));
            //an empty message is not a message
            Assert.Equal("fallback", NeighbourhoodWorldHealth.MergeDrawFailure("", "", "fallback"));
        }
    }
}
