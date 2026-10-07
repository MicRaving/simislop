using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The custom names of every neighbourhood slot, read once and served from memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this type exists.</b> <see cref="NeighbourhoodManager.GetName(string, int)"/> is
    /// correct but not cheap: it is a <c>File.Exists</c>, a <c>File.ReadAllText</c> and a full
    /// <see cref="System.Text.Json.JsonDocument"/> parse, per call. The neighbourhood management
    /// page used to call it once per row for the row's title, once more for its detail line, and
    /// twice more in the button refresh - about twenty reads and parses of the same small file
    /// every time the list was rebuilt, which is on construction, on every window resize, and
    /// after every rename, create and delete. On a UI-thread path that runs while the player is
    /// dragging a window edge, that is a latency bug rather than a correctness one.
    /// </para>
    ///
    /// <para>
    /// A caller that shows more than one name at a time takes one of these instead, and takes a
    /// fresh one whenever it rebuilds. Freshness is not weakened: the sidecar is "otherwise
    /// independent of the folders" (see <see cref="NeighbourhoodManager.Rename"/>), so one read
    /// per rebuild is already as fresh as the disk can be - re-reading part way through the same
    /// rebuild could only return what the read already returned, unless something outside the
    /// game edited the file in the microseconds between two rows, in which case a list showing
    /// two different states is worse than one showing the older one.
    /// </para>
    /// </remarks>
    public sealed class NeighbourhoodNames
    {
        private readonly Dictionary<int, string> _names;

        private NeighbourhoodNames(Dictionary<int, string> names)
        {
            _names = names;
        }

        /// <summary>How many slots carry a custom name.</summary>
        public int Count => _names.Count;

        /// <summary>
        /// Reads every stored custom name for a user directory. A missing, unreadable, malformed
        /// or future-versioned sidecar yields an empty snapshot rather than an exception, exactly
        /// as <see cref="NeighbourhoodManager.GetNames"/> does.
        /// </summary>
        /// <param name="userDir">The game's user directory.</param>
        public static NeighbourhoodNames Read(string userDir)
        {
            return new NeighbourhoodNames(new Dictionary<int, string>(NeighbourhoodManager.GetNames(userDir)));
        }

        /// <summary>
        /// The stored custom name for one slot, or <c>null</c> when it has none. The one call a
        /// list control needs per row - and, unlike the equivalent on
        /// <see cref="NeighbourhoodManager"/>, it does not touch the disk.
        /// </summary>
        /// <param name="id">Slot id.</param>
        public string NameOf(int id)
        {
            return _names.TryGetValue(id, out var name) ? name : null;
        }

        /// <summary>
        /// The label a slot should be shown with: the player's name if they set one, otherwise
        /// the default. The same answer <see cref="NeighbourhoodManager.GetDisplayName"/> gives,
        /// from the snapshot rather than from a fresh parse.
        /// </summary>
        /// <param name="id">Slot id.</param>
        public string DisplayNameOf(int id)
        {
            if (!NeighbourhoodManager.IsKnownSlot(id)) return null;
            return NeighbourhoodManager.SlotName(id, NameOf(id));
        }

        /// <summary>Every stored name, for a caller that wants the whole map.</summary>
        public IReadOnlyDictionary<int, string> All => _names;
    }
}
