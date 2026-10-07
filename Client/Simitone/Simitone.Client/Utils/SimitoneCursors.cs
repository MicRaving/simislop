/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Common;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Cursors that Simitone needs but FreeSO does not ship.
    ///
    /// FreeSO's <c>CursorManager</c> can only hand out cursors from its own TS1 bmp set, and the
    /// eyedropper is a Simitone addition, so its cursor is built here from a PNG shipped in
    /// <c>Content/Cursors/</c> instead. That also means the eyedropper cursor is invisible to
    /// <c>CursorManager</c>'s notion of "the current cursor": setting it here does not update
    /// <c>CursorManager.CurrentCursor</c>, so a later <c>SetCursor</c> for a type that differs
    /// from what <c>CursorManager</c> last set will still overwrite it. The caller is therefore
    /// expected to re-apply the eyedropper cursor every frame it wants it visible (which
    /// <c>UIObjectHolder.Update</c> does).
    ///
    /// Must be initialised from the game thread after the graphics device exists -
    /// <c>SimitoneGame.Initialize</c> does this. Safe to call more than once.
    /// </summary>
    public static class SimitoneCursors
    {
        private static MouseCursor EyedropperCursor;
        private static bool Initialized;

        /// <summary>
        /// Loads the Simitone-only cursors out of <c>&lt;ContentDir&gt;/Cursors/</c>. Any
        /// failure (missing file, unreadable image, no cursor support on the platform) leaves the
        /// cursor null, and <see cref="SetEyedropperCursor"/> then falls back to a system
        /// crosshair - the tool stays usable, it just does not have the custom glyph. Failures are
        /// logged rather than swallowed silently, since "my eyedropper has a plain crosshair" is
        /// otherwise a mystery.
        /// </summary>
        public static void Init(GraphicsDevice gd)
        {
            if (Initialized) return;
            Initialized = true;

            if (gd == null) return;

            var pngPath = Path.Combine(FSOEnvironment.ContentDir, "Cursors", "eyedropper.png");

            if (!File.Exists(pngPath))
            {
                SimitoneLog.Warn("SimitoneCursors: eyedropper cursor not found at " + pngPath + ", falling back to the crosshair cursor.");
                return;
            }

            try
            {
                //The cursor holds a reference to the texture for its lifetime (until the process
                //exits), so the stream is closed but the texture is deliberately not disposed.
                using (var stream = File.Open(pngPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var texture = Texture2D.FromStream(gd, stream);
                    //Hotspot at the tip of the dropper, so the tile being sampled sits under the
                    //pointer rather than offset from it.
                    EyedropperCursor = MouseCursor.FromTexture2D(texture, 1, 1);
                }
            }
            catch (Exception e)
            {
                EyedropperCursor = null;
                SimitoneLog.Warn("SimitoneCursors: could not build the eyedropper cursor from " + pngPath + ", falling back to the crosshair cursor.", e);
            }
        }

        /// <summary>
        /// Shows the eyedropper cursor, or the system crosshair if the custom one could not be
        /// built. Safe to call before <see cref="Init"/>.
        /// </summary>
        public static void SetEyedropperCursor()
        {
            if (EyedropperCursor != null)
            {
                Mouse.SetCursor(EyedropperCursor);
            }
            else
            {
                Mouse.SetCursor(MouseCursor.Crosshair);
            }
        }

        /// <summary>Whether the custom eyedropper cursor loaded (and so is being shown).</summary>
        public static bool HasEyedropperCursor => EyedropperCursor != null;
    }
}
