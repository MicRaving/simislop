using System;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The live DrawFailures / DeviceLost signals of <see cref="Neighbourhood3DView"/>, and that
    /// the backdrop rule reacts to them.
    /// </summary>
    public class NeighbourhoodDrawHealthTests
    {
        private static NeighbourhoodWorldHealth HealthFor(NeighbourhoodDrawHealth draw)
        {
            return new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 24576,
                groundQuads: 65536, roadQuads: 4096, lotCount: 12,
                drawFailures: draw.ConsecutiveFailures,
                secondsSinceAttach: 30d,
                deviceLost: draw.DeviceLost);
        }

        [Fact]
        public void AFreshHealthReportsNothingWrong()
        {
            var draw = new NeighbourhoodDrawHealth();
            Assert.Equal(0, draw.ConsecutiveFailures);
            Assert.False(draw.DeviceLost);
            Assert.Null(draw.LastFailure);
        }

        [Fact]
        public void FailuresCountInARowAndASuccessEndsTheRun()
        {
            var draw = new NeighbourhoodDrawHealth();
            draw.Failed(new InvalidOperationException("one"));
            draw.Failed(new InvalidOperationException("two"));
            Assert.Equal(2, draw.ConsecutiveFailures);
            Assert.Equal("two", draw.LastFailure);

            draw.Succeeded();
            Assert.Equal(0, draw.ConsecutiveFailures);
            Assert.Equal(2, draw.TotalFailures);
        }

        [Fact]
        public void OnlyTheFirstAndTheLimitingFailureAreLogged()
        {
            var draw = new NeighbourhoodDrawHealth();
            var ex = new InvalidOperationException("x");
            var logged = 0;
            for (int i = 0; i < 20; i++) if (draw.Failed(ex)) logged++;
            // the first, and the one that reaches DrawFailureLimit (2)
            Assert.Equal(2, logged);
        }

        [Fact]
        public void ALostDeviceStaysLostUntilAFrameCompletes()
        {
            var draw = new NeighbourhoodDrawHealth();
            draw.MarkDeviceLost();
            Assert.True(draw.DeviceLost);
            draw.Failed(new Exception("still down"));
            Assert.True(draw.DeviceLost);
            draw.Succeeded();
            Assert.False(draw.DeviceLost);
        }

        [Fact]
        public void TheBackdropRuleFallsBackOnARunOfDrawFailures()
        {
            var draw = new NeighbourhoodDrawHealth();
            for (int i = 0; i < NeighbourhoodBackdropRule.DrawFailureLimit; i++)
                draw.Failed(new Exception("boom"));

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, HealthFor(draw));

            Assert.Equal(NeighbourhoodWorldFault.DrawFailed, decision.Fault);
            Assert.True(decision.UseBitmap);
        }

        [Fact]
        public void TheBackdropRuleFallsBackOnALostDevice()
        {
            var draw = new NeighbourhoodDrawHealth();
            draw.MarkDeviceLost();

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, HealthFor(draw));

            Assert.Equal(NeighbourhoodWorldFault.DeviceLost, decision.Fault);
            Assert.True(decision.UseBitmap);
        }

        [Fact]
        public void OneBadFrameIsAHiccupNotAVerdict()
        {
            var draw = new NeighbourhoodDrawHealth();
            draw.Failed(new Exception("boom"));

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, HealthFor(draw));

            Assert.NotEqual(NeighbourhoodWorldFault.DrawFailed, decision.Fault);
        }
    }
}
