/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The live neighbourhood world as a session object: one world, keyed by the neighbourhood
    /// slot the player is standing in, built from the neighbourhood lifecycle's layout and map and
    /// from nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The world used to be built lazily out of the 3D surround's source
    /// object, so it existed only while the 3D view setting was on, a lot number was known and the
    /// surround's generation had finished - and it was thrown away whenever that source object was
    /// replaced, which is every late landing and every change of neighbourhood. None of those has
    /// anything to do with whether the player has asked for a neighbourhood world.
    /// </para>
    /// <para>
    /// <b>Compared by slot.</b> A world is the same world while the slot is the same, whatever
    /// layout object the lifecycle handed over for it: <see cref="Adopt"/> for a slot already held
    /// returns the world it has. A different slot ends the held world first, and the caller gets it
    /// back from <see cref="Retarget"/> or <see cref="End"/> so it can be saved before it goes.
    /// </para>
    /// <para>
    /// <b>Says why it is missing.</b> <see cref="NoteUnavailable"/> returns true only the first
    /// time a reason is given, so the caller can log every reason once instead of every frame.
    /// </para>
    /// <para>
    /// Holds no device resources, so ending a world is dropping it. The lot store the world was
    /// built over is not the world's to clear: the screen owns it, because the lots outlive any
    /// one neighbourhood.
    /// </para>
    /// </remarks>
    public sealed class NeighbourhoodWorldSession
    {
        /// <summary>The slot value meaning "none".</summary>
        public const int NoSlot = -1;

        private SimitoneNeighbourhoodWorld _world;
        private int _slot = NoSlot;
        private int _requestedSlot = NoSlot;
        private int _epoch;
        private string _reason;

        /// <summary>The world, or null when there is none for the slot being played.</summary>
        public SimitoneNeighbourhoodWorld World
        {
            get { return _world; }
        }

        /// <summary>The slot <see cref="World"/> belongs to, or <see cref="NoSlot"/>.</summary>
        public int Slot
        {
            get { return _world == null ? NoSlot : _slot; }
        }

        /// <summary>The slot a world was last asked for, or <see cref="NoSlot"/>.</summary>
        public int RequestedSlot
        {
            get { return _requestedSlot; }
        }

        /// <summary>
        /// Changes every time the session is ended or retargeted, so an answer that arrives later
        /// (a generation landing on a worker) can tell that it was asked for something that is gone.
        /// </summary>
        public int Epoch
        {
            get { return _epoch; }
        }

        /// <summary>The last reason given for there being no world, or null.</summary>
        public string UnavailableReason
        {
            get { return _world == null ? _reason : null; }
        }

        /// <summary>Whether the held world is the one for <paramref name="slot"/>.</summary>
        public bool IsFor(int slot)
        {
            return _world != null && _slot == slot;
        }

        /// <summary>
        /// Whether a world still has to be asked for to serve <paramref name="slot"/>: there is a
        /// slot, the held world is not for it, and it has not been asked for already - a request
        /// that is pending and one that was refused are both left alone until the session ends.
        /// </summary>
        public bool NeedsRequest(int slot)
        {
            return slot != NoSlot && !IsFor(slot) && _requestedSlot != slot;
        }

        /// <summary>Records that a world was asked for, so it is not asked for again.</summary>
        public void MarkRequested(int slot)
        {
            _requestedSlot = slot;
        }

        /// <summary>
        /// The player is now in <paramref name="slot"/>. A held world for another slot is ended and
        /// returned so the caller can save it; a request made for another slot is forgotten.
        /// </summary>
        /// <returns>The world that was ended, or null.</returns>
        public SimitoneNeighbourhoodWorld Retarget(int slot)
        {
            SimitoneNeighbourhoodWorld ended = null;
            if (_world != null && _slot != slot) ended = End();
            else if (_requestedSlot != slot && _requestedSlot != NoSlot)
            {
                _requestedSlot = NoSlot;
                _epoch++;
            }
            return ended;
        }

        /// <summary>
        /// Takes the lifecycle's layout and map for <paramref name="slot"/> and makes the world for
        /// it, or returns the one already held for that slot whatever layout instance this is.
        /// </summary>
        /// <param name="slot">The neighbourhood slot.</param>
        /// <param name="layout">The generated layout. Required.</param>
        /// <param name="map">The generated map. Required.</param>
        /// <param name="lots">The session's lot store, or null for the world's own.</param>
        /// <param name="replaced">The world of another slot this ended, or null.</param>
        /// <param name="created">Whether a new world was made.</param>
        public SimitoneNeighbourhoodWorld Adopt(int slot, NeighbourhoodLayout layout, NeighbourhoodMap map,
            LotStore lots, out SimitoneNeighbourhoodWorld replaced, out bool created)
        {
            replaced = null;
            created = false;
            if (IsFor(slot)) return _world;

            if (_world != null) replaced = End();

            _world = new SimitoneNeighbourhoodWorld(layout, map, true, lots);
            _slot = slot;
            _requestedSlot = slot;
            _reason = null;
            created = true;
            return _world;
        }

        /// <summary>
        /// Gives <paramref name="slot"/> up as refused: a generation that failed, or a layout that
        /// cannot back a world. The request latch stays on, so it is not retried until the session
        /// ends. Returns true when the reason is new, which is the caller's cue to log it.
        /// </summary>
        public bool Refuse(int slot, string reason)
        {
            _requestedSlot = slot;
            return NoteUnavailable(reason);
        }

        /// <summary>
        /// Records why there is no world. Returns true the first time a reason is given since the
        /// last time the session ended or a world was adopted, and false for a repeat.
        /// </summary>
        public bool NoteUnavailable(string reason)
        {
            if (reason == null) reason = "no reason was given";
            if (reason == _reason) return false;
            _reason = reason;
            return true;
        }

        /// <summary>
        /// Ends the session: the held world is dropped and returned (so it can be saved), and
        /// everything remembered about requests and reasons is forgotten.
        /// </summary>
        /// <returns>The world that was held, or null.</returns>
        public SimitoneNeighbourhoodWorld End()
        {
            var ended = _world;
            _world = null;
            _slot = NoSlot;
            _requestedSlot = NoSlot;
            _reason = null;
            _epoch++;
            return ended;
        }
    }
}
