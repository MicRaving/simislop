using System.Collections.Generic;
using System.Linq;

namespace Simitone.Client
{
    /// <summary>
    /// The gameplay speeds the UI talks about, and the translation layer between those
    /// named speeds and the raw <c>VM.SpeedMultiplier</c> values simantics actually stores.
    ///
    /// <para>
    /// The two numbering schemes are unrelated. Simantics stores pause as 0 and the three
    /// speeds as 1 / 3 / 10, because the multipliers are applied literally to the clock
    /// advance. The UI instead has four speed buttons indexed 1..4 where 4 is pause -
    /// <see cref="Play"/>..<see cref="Pause"/> - which is why the desktop clock and the
    /// live-mode speed buttons can both be driven by a plain <c>for (i = 0; i &lt; 4; i++)
    /// Btns[i].Selected = (i + 1 == speed)</c> loop.
    /// </para>
    ///
    /// <para>
    /// <see cref="Disabled"/> is the one value that is not a speed at all: -1 is what the VM
    /// reports when gameplay is not allowed to advance (buy/build mode, and the "this lot
    /// has no sims in it" case). It is deliberately absent from the forward map's
    /// 1..4 output range, so a caller that does <c>RemapSpeed[Math.Max(0, multiplier)]</c>
    /// can never accidentally light a button for it.
    /// </para>
    /// </summary>
    internal static class GameplaySpeed
    {
        /// <summary>Paused. The VM multiplier is 0.</summary>
        internal const int Pause = 4;

        /// <summary>Normal speed. The VM multiplier is 1.</summary>
        internal const int Play = 1;

        /// <summary>Speed 2. The VM multiplier is 3.</summary>
        internal const int FastForward = 2;

        /// <summary>Speed 3. The VM multiplier is 10.</summary>
        internal const int FastFastForward = 3;

        /// <summary>
        /// Gameplay may not advance. This is the VM's -1, not a speed: it is what buy/build
        /// mode holds the game at so the speed buttons cannot be used to bypass the pause.
        /// </summary>
        internal const int Disabled = -1;

        /// <summary>
        /// Maps a <c>VM.SpeedMultiplier</c> to the matching constant in
        /// <see cref="GameplaySpeed"/>. <see cref="Disabled"/> is included so the -1 the VM
        /// reports has a name, but it is not one of the four speed buttons.
        /// </summary>
        internal static readonly Dictionary<int, int> RemapSpeed = new Dictionary<int, int>()
        {
            {0, Pause},              //pause
            {1, Play},               //1 speed
            {3, FastForward},        //2 speed
            {10, FastFastForward},   //3 speed
            {-1, Disabled}           // disabled (buy/build,etc.)
        };

        /// <summary>
        /// The inverse of <see cref="RemapSpeed"/>: a <see cref="GameplaySpeed"/> constant
        /// back to the <c>VM.SpeedMultiplier</c> that produces it. Both maps are static
        /// lookup tables rather than per-instance state, so they are reachable - and
        /// testable - without a graphics device or a live VM.
        /// </summary>
        internal static readonly Dictionary<int, int> ReverseRemap = RemapSpeed.ToDictionary(x => x.Value, x => x.Key);
    }
}
