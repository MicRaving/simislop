/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

namespace Simitone.Client.UI.Services
{
    /// <summary>
    /// The latch behind the "ask once, act on re-entry" confirm idiom the UI screens use.
    ///
    /// <para>
    /// Several confirmations in the game are written as a re-entrant method: the first call finds
    /// nothing latched, builds a dialog and returns; the dialog's <em>yes</em> handler calls the
    /// same method again, which now finds the dialog still latched and therefore falls through to
    /// the real work. Every copy of that shape used a nullable <c>UIMobileAlert</c> field as its
    /// latch, which made the rule ("one dialog at a time, and the action runs before the latch is
    /// cleared") invisible at the call site and impossible to test on its own.
    /// </para>
    ///
    /// <para>
    /// This is that latch, with no UI in it: <see cref="TryBegin"/> is the
    /// <c>if (Latch == null)</c> test and takes ownership of the slot if it is free, and
    /// <see cref="Reset"/> is the <c>Latch = null</c> that the dialog's handlers do. The state
    /// machine is exactly two states - free and latched - and a second <see cref="TryBegin"/>
    /// without an intervening <see cref="Reset"/> returns <c>false</c> and changes nothing, which
    /// is the whole contract the callers rely on.
    /// </para>
    ///
    /// <para>
    /// Not thread safe, exactly like the nullable field it replaces: every use is on the UI thread.
    /// </para>
    /// </summary>
    public sealed class ConfirmOnceGate
    {
        /// <summary>
        /// True while a dialog owns the gate. The screen uses this to answer "is a confirmation
        /// already up?" without touching the dialog element itself.
        /// </summary>
        public bool IsLatched { get; private set; }

        /// <summary>
        /// Attempts to latch the gate, i.e. claims the right to show a dialog.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the gate was free and the caller should build and show its dialog;
        /// <c>false</c> when a dialog already owns the gate, in which case nothing changed and the
        /// caller should run its real work instead (the dialog's <em>yes</em> handler re-entering
        /// the method is how that happens).
        /// </returns>
        public bool TryBegin()
        {
            if (IsLatched) return false;
            IsLatched = true;
            return true;
        }

        /// <summary>
        /// Releases the gate, letting the next <see cref="TryBegin"/> succeed again. Called by the
        /// dialog's button handlers once the dialog has been closed.
        /// </summary>
        public void Reset()
        {
            IsLatched = false;
        }
    }
}
