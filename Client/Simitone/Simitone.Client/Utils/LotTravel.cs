using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The two decisions behind preserving a lot across a travel switch.
    ///
    /// A TS1 hybrid lot session holds one live VM. Travelling (home -&gt; downtown -&gt; vacation
    /// -&gt; home, or any hop in between) has to destroy the current VM and build a new one, so
    /// the state of the lot being left behind has to be marshalled out and kept alive in memory
    /// until the player comes back to that exact lot. The original code kept a
    /// <em>single</em> slot, which only ever worked for the family's home lot: any state built up
    /// on Downtown, Vacation, Studio or Magic was thrown away on the way out, so returning to
    /// that lot rebuilt it from the shipped template and lost anything the player did there.
    ///
    /// With one slot per lot number the round trip is symmetric, and both halves of it are
    /// small enough to state - and test - on their own:
    /// <list type="bullet">
    /// <item><see cref="ShouldStashCurrentLot"/>: save the current VM only when it is about to be
    /// replaced by a different lot. Stashing when the target <em>is</em> the current lot would
    /// overwrite the live state we are about to restore, so the two must be compared, not
    /// assumed.</item>
    /// <item><see cref="TryRestore{T}"/>: hand back the marshalled state for a lot number, if one
    /// is held, so the caller can resume the lot instead of reloading it from disk.</item>
    /// </list>
    /// </summary>
    public static class LotTravel
    {
        /// <summary>
        /// Returns true when the lot currently loaded in the VM must be marshalled out and kept
        /// before <paramref name="targetHouseId"/> is loaded in its place.
        /// </summary>
        /// <param name="currentHouseId">The lot the player is standing on right now, as reported
        /// by the VM's global 10 (house number).</param>
        /// <param name="targetHouseId">The lot the pending switch is about to load.</param>
        /// <returns>
        /// <c>true</c> when the two lots differ and the current lot's state has to be preserved;
        /// <c>false</c> when the switch targets the lot already loaded, in which case there is
        /// nothing to stash and the live VM is reused as-is.
        /// </returns>
        public static bool ShouldStashCurrentLot(int currentHouseId, int targetHouseId)
        {
            return currentHouseId != targetHouseId;
        }

        /// <summary>
        /// Looks up the preserved in-memory state for a lot number.
        /// </summary>
        /// <typeparam name="T">The preserved state type (in the game, <c>VMMarshal</c>).</typeparam>
        /// <param name="savedLots">The per-lot state store, keyed by lot number. May be
        /// <c>null</c>, which is treated as "nothing preserved".</param>
        /// <param name="targetHouseId">The lot whose preserved state is wanted.</param>
        /// <param name="marshal">The preserved state when the call succeeds, otherwise
        /// <c>default</c>.</param>
        /// <returns>
        /// <c>true</c> when usable state was found for <paramref name="targetHouseId"/>;
        /// <c>false</c> when the lot was never visited, was already restored, or its entry held
        /// no state - the caller must then load the lot from its <c>House{NN}.iff</c> instead.
        /// </returns>
        public static bool TryRestore<T>(IDictionary<short, T> savedLots, short targetHouseId, out T marshal)
        {
            T found;
            if (savedLots != null
                && savedLots.TryGetValue(targetHouseId, out found)
                && found != null)
            {
                marshal = found;
                return true;
            }

            marshal = default(T);
            return false;
        }
    }
}
