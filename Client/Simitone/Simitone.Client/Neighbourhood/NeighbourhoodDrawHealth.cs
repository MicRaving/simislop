using System;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The live fallback signals of the generated world's draw: how many draws in a row threw, and
    /// whether the graphics device has been lost or reset since the world last drew a frame.
    /// </summary>
    /// <remarks>
    /// Pure bookkeeping with no graphics device in it, so the rules are testable.
    /// <see cref="NeighbourhoodBackdropRule"/> reads <see cref="ConsecutiveFailures"/> through
    /// <see cref="INeighbourhoodRenderSignal.DrawFailures"/> and <see cref="DeviceLost"/> through
    /// <see cref="INeighbourhoodRenderSignal.DeviceLost"/>.
    /// </remarks>
    public sealed class NeighbourhoodDrawHealth
    {
        /// <summary>Draws that threw since the last one that completed.</summary>
        public int ConsecutiveFailures { get; private set; }

        /// <summary>Draws that threw over the whole life of the world.</summary>
        public int TotalFailures { get; private set; }

        /// <summary>The message of the most recent failure, or <c>null</c>.</summary>
        public string LastFailure { get; private set; }

        /// <summary>
        /// Whether the device was lost or went into a reset and no frame has completed since.
        /// </summary>
        public bool DeviceLost { get; private set; }

        /// <summary>A draw completed: the run of failures ends and the device is evidently back.</summary>
        public void Succeeded()
        {
            ConsecutiveFailures = 0;
            DeviceLost = false;
        }

        /// <summary>
        /// A draw threw. Returns whether the caller should log it: the first failure ever, and the
        /// one that takes the run to <see cref="NeighbourhoodBackdropRule.DrawFailureLimit"/>, so a
        /// world that throws every frame writes two lines rather than sixty a second.
        /// </summary>
        /// <param name="ex">The exception that was caught.</param>
        public bool Failed(Exception ex)
        {
            ConsecutiveFailures++;
            TotalFailures++;
            LastFailure = ex?.Message;
            return TotalFailures == 1
                || ConsecutiveFailures == NeighbourhoodBackdropRule.DrawFailureLimit;
        }

        /// <summary>The device raised <c>DeviceLost</c> or <c>DeviceResetting</c>.</summary>
        public void MarkDeviceLost()
        {
            DeviceLost = true;
        }
    }
}
