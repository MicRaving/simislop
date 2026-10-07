/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Common.Rendering.Framework;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.NetPlay.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Globalization;
using System.IO;

namespace Simitone.Client.UI.Services
{
    /// <summary>
    /// Loading a lot's state into a live VM: the "resume a marshalled lot" path and the
    /// "reset this lot to its blueprint" path.
    ///
    /// <para>
    /// This service deliberately does <b>not</b> own the lot. The VM, the world, the driver, the
    /// lot control, the frontend and the in-memory lot store are read through the screen by about
    /// thirty sites elsewhere in the UI, and moving them behind a service would change the public
    /// shape of <see cref="TS1GameScreen"/> for no testable gain. What is moved is the two
    /// sequences that consume them:
    /// <list type="bullet">
    /// <item><see cref="ResumeFromMarshal"/> - bring up a world and a VM, load a saved lot into it,
    /// re-select the family, attach the local player and put the UI frontend up. This is the path
    /// both a lot held in the in-session store and a cross-session travel slot resume through.</item>
    /// <item><see cref="BlueprintReset"/> - put a lot back to its shipped blueprint, preferring a
    /// cached <c>LocalHouse/*.fsov</c> rebuild when one exists.</item>
    /// </list>
    /// Both write through the screen's own members, so their effects on the screen are the same
    /// as when the bodies were inline.
    /// </para>
    /// </summary>
    public sealed class LotLifecycleService
    {
        /// <summary>
        /// The screen whose lot is driven.
        /// </summary>
        private readonly TS1GameScreen _screen;

        /// <summary>
        /// Creates a lot lifecycle service driving <paramref name="screen"/>.
        /// </summary>
        public LotLifecycleService(TS1GameScreen screen)
        {
            _screen = screen;
        }

        /// <summary>
        /// Whether a lot load should leave the VM simless.
        /// </summary>
        /// <remarks>
        /// Pure. A lot loaded with no family in play (a town lot, or a surrounding-lot load) runs
        /// with the speed pinned off and global 32 set to 1, which is what stops the empty lot from
        /// ticking while nothing is happening in it.
        /// </remarks>
        /// <param name="hasActiveFamily">Whether a family is being played.</param>
        /// <param name="isSurrounding">Whether this load is a surrounding lot rather than the
        /// lot the player is standing on.</param>
        public static bool IsSimless(bool hasActiveFamily, bool isSurrounding)
        {
            return !hasActiveFamily && !isSurrounding;
        }

        /// <summary>
        /// The call-site form of <see cref="IsSimless"/>, taking the family itself rather than a
        /// flag saying whether there is one.
        ///
        /// <para>
        /// This exists because of the bug it is named after. The single production caller used to
        /// write <c>IsSimless(_screen.ActiveFamily == null, isSurrounding)</c> - passing the
        /// <em>negation</em> of the flag <see cref="IsSimless"/> asks for - and the truth table
        /// above is perfectly happy with that, so every lot in the game came up simless:
        /// <c>SpeedMultiplier</c> pinned to <see cref="GameplaySpeed.Disabled"/>, global 32 set to
        /// 1, the frontend coming up in Buy mode, the Live button greyed out
        /// (<c>UIDesktopUCP.UpdateBuildBuy</c>) and the money label hidden
        /// (<c>UIDesktopUCP.UpdateMoneyDisplay</c>). A family that had just moved in could not play
        /// and looked like they had no money at all.
        /// </para>
        ///
        /// <para>
        /// Handing the question the family <em>object</em> is what makes that mistake impossible to
        /// express: there is no boolean left to invert, and a null family is the only thing that
        /// means "nobody in play".
        /// </para>
        /// </remarks>
        /// <param name="activeFamily">The family being played on the lot, or <c>null</c> when the
        /// lot is being loaded with nobody in it.</param>
        /// <param name="isSurrounding">Whether this load is a surrounding lot rather than the lot
        /// the player is standing on.</param>
        public static bool SimlessForLoad(FAMI activeFamily, bool isSurrounding)
        {
            return IsSimless(activeFamily != null, isSurrounding);
        }

        /// <summary>
        /// Whether a blueprint path names an <c>.iff</c> lot file, which is what tells the VM to
        /// read the payload as IFF rather than as XML.
        /// </summary>
        /// <param name="path">The lot's house path.</param>
        public static bool IsIffPath(string path)
        {
            return path.EndsWith(".iff");
        }

        /// <summary>
        /// The job level a blueprint path declares, taken from the two digits before the
        /// extension.
        /// </summary>
        /// <remarks>
        /// Total, and total on purpose: -1 means "no level was declared", which is both what a
        /// non-IFF path has always answered and what a malformed one now answers. This is called
        /// from the blueprint-reset <em>recovery</em> path, and a path too short to carry the
        /// digits used to throw from inside a catch block - turning a lot reset that could have
        /// been logged and recovered from into an unhandled failure on the game thread. The
        /// caller logs, so the shape the lot is restored at is still visible; what is no longer
        /// possible is a formatting detail killing the reset.
        /// </remarks>
        /// <param name="path">The lot's house path.</param>
        /// <returns>
        /// The parsed level, or -1 for a path that is not an <c>.iff</c> file or whose two digits
        /// before the extension are not a number.
        /// </returns>
        public static short JobLevelFromPath(string path)
        {
            if (!IsIffPath(path)) return -1;
            if (path.Length < 6) return -1;
            return short.TryParse(path.Substring(path.Length - 6, 2),
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var level)
                ? level
                : (short)-1;
        }

        /// <summary>
        /// The path a cached rebuild of this lot would live at, or <c>null</c> when the name is
        /// too short to have one.
        /// </summary>
        /// <remarks>
        /// The cached file is named after the lot file with the <c>.iff</c> swapped for
        /// <c>.fsov</c>, which is a <c>Substring</c> of the name and therefore throws on a name
        /// shorter than the four characters being removed. Pure, so the guard is here rather than
        /// at each of the two call sites.
        /// </remarks>
        /// <param name="path">The lot's house path.</param>
        /// <returns>The full path of the cached rebuild, or <c>null</c>.</returns>
        public static string CachedRebuildPath(string path)
        {
            if (path == null) return null;
            var filename = Path.GetFileName(path);
            if (string.IsNullOrEmpty(filename) || filename.Length < 4) return null;
            return Path.Combine(FSOEnvironment.UserDir ?? string.Empty, "LocalHouse")
                + Path.DirectorySeparatorChar
                + filename.Substring(0, filename.Length - 4) + ".fsov";
        }

        /// <summary>
        /// Builds a fresh world and VM, loads <paramref name="marshal"/> into it, re-selects the
        /// family, attaches the local player and puts the UI frontend up.
        /// </summary>
        /// <remarks>
        /// This is the same path a lot held in the in-session store and a cross-session travel
        /// slot both take; it exists so neither of them can drift from the other. Note it does
        /// <em>not</em> re-select the family - the caller does that, under the same
        /// <c>!Downtown &amp;&amp; ActiveFamily != null</c> condition the template load uses,
        /// because the family subset is transient state that is deliberately not saved into a
        /// marshal.
        ///
        /// <para>
        /// The one other thing this path does is offer the lot to
        /// <see cref="FSO.SimAntics.Utils.VMTS1TutorialTrigger"/>, which is where the TS1 tutorial
        /// controller can be spawned. It sits here because this is the moment a household is
        /// actually in play, and because both the "resume a saved lot" and the "load a blueprint"
        /// paths arrive through this method. It is a no-op unless
        /// <see cref="FSO.SimAntics.Utils.VMTS1TutorialTrigger.HelpEnabled"/> has been set, and
        /// that defaults to off - deliberately, because the tutorial controller is not a passive
        /// tour: <c>Tutorial.iff</c> 4098 <c>"setup"</c> tears the lot down and rebuilds it. See
        /// <see cref="FSO.SimAntics.Utils.VMTS1TutorialTrigger"/> for the whole of it.
        /// </para>
        /// </remarks>
        /// <param name="marshal">The lot state to resume.</param>
        public void ResumeFromMarshal(VMMarshal marshal)
        {
            _screen.InitializeLot();
            _screen.vm.MyUID = 65537;
            _screen.vm.Load(marshal);

            _screen.vm.TS1State.ActivateFamily(_screen.vm, _screen.ActiveFamily);

            // Opt-in, and off by default, so a lot load costs nothing until someone turns the
            // tutorial on. Deliberately not folded into the sequence above: it is a feature
            // switch, not a step in bringing a lot up, and it has to stay removable as one line.
            FSO.SimAntics.Utils.VMTS1TutorialTrigger.TryStart(
                _screen.vm.Context, _screen.ActiveFamily != null);

            var settings = GlobalSettings.Default;
            var myClient = new VMNetClient
            {
                PersistID = 1,
                RemoteIP = "local",
                AvatarState = new VMNetAvatarPersistState()
                {
                    Name = settings.LastUser ?? "",
                    DefaultSuits = new VMAvatarDefaultSuits(settings.DebugGender),
                    BodyOutfit = settings.DebugBody,
                    HeadOutfit = settings.DebugHead,
                    PersistID = 1,
                    SkinTone = (byte)settings.DebugSkin,
                    Gender = (short)(settings.DebugGender ? 1 : 0),
                    Permissions = FSO.SimAntics.Model.TSOPlatform.VMTSOAvatarPermissions.Admin,
                    Budget = 1000000
                }

            };

            var server = (VMServerDriver)_screen.Driver;
            server.ConnectClient(myClient);

            GameFacade.Cursor.SetCursor(CursorType.Normal);
            _screen.ZoomLevel = 1;

            _screen.Frontend = new UISimitoneFrontend(_screen);
            _screen.Add(_screen.Frontend);
        }

        /// <summary>
        /// Puts a lot back to its shipped blueprint.
        /// </summary>
        /// <remarks>
        /// A cached <c>LocalHouse/{name}.fsov</c> rebuild is used when one exists, because it
        /// skips the VM's own blueprint parse; anything else is handed to the VM as a
        /// <see cref="VMBlueprintRestoreCmd"/>. Either way the lot is then ticked once at speed 1
        /// to settle, and a lot with no family in play is left simless.
        /// </remarks>
        /// <param name="path">The lot's house path.</param>
        /// <param name="vm">The VM to reset, or <c>null</c> to use the screen's own.</param>
        public void BlueprintReset(string path, VM vm)
        {
            bool isSurrounding = true;
            if (vm == null)
            {
                isSurrounding = false;
                vm = _screen.vm;
            }

            //Only the *read* of the cached rebuild is guarded, and that is the whole point of
            //splitting this in two. The old shape wrapped the read, the deserialise, the
            //vm.Load and the vm.Reset in one bare catch, so a failure half way through the load
            //fell into the "there is no cached rebuild" branch and pushed a second load on top
            //of a VM already in an unknown state. Now a VM that will not take the cached state
            //reports that instead, and the fallback is only ever the answer to "no usable cached
            //rebuild", which is what it was written for.
            var cached = TryReadCachedRebuild(path, out var failure);
            if (cached == null)
            {
                SimitoneLog.Info("BlueprintReset: no usable cached rebuild for '" + path
                    + "' (" + failure + "); restoring the blueprint itself.");
                RestoreBlueprint(path, vm);
            }
            else
            {
                //Outside the guarded read on purpose - see above.
                vm.Load(cached);
                vm.Reset();
            }

            var isSimless = SimlessForLoad(_screen.ActiveFamily, isSurrounding);
            vm.SpeedMultiplier = GameplaySpeed.Disabled;
            vm.Tick();
            vm.SpeedMultiplier = 1;

            if (isSimless)
            {
                vm.SpeedMultiplier = GameplaySpeed.Disabled;
            }
            vm.SetGlobalValue(32, (short)(isSimless ? 1 : 0));
        }

        /// <summary>
        /// The cached <c>LocalHouse/{name}.fsov</c> rebuild for this lot, deserialised, or
        /// <c>null</c> when there is not one to use.
        /// </summary>
        /// <remarks>
        /// The preamble is checked before deserialising because
        /// <c>VMMarshal.Deserialize</c> answers a file without the <c>FSOv</c> magic with a
        /// hollow marshal (null <c>Context</c>, null <c>Entities</c>) rather than by throwing -
        /// the exact trap <c>TravelSave.HasMarshalPreamble</c> and
        /// <see cref="SaveService.TryRestoreTravelSlot"/> already guard against on the travel
        /// slot. Every failure is logged with what it was, so a missing cache, a truncated one
        /// and a corrupt one are no longer indistinguishable afterwards.
        /// </remarks>
        /// <param name="path">The lot's house path.</param>
        /// <param name="failure">Why there is no usable cached rebuild.</param>
        private static VMMarshal TryReadCachedRebuild(string path, out string failure)
        {
            failure = "no cache file";
            var cachedPath = CachedRebuildPath(path);
            if (cachedPath == null)
            {
                failure = "the lot name is too short to have a cache file";
                return null;
            }

            byte[] bytes;
            try
            {
                if (!File.Exists(cachedPath))
                {
                    return null;
                }
                bytes = File.ReadAllBytes(cachedPath);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("BlueprintReset: could not read the cached rebuild at '"
                    + cachedPath + "'.", e);
                failure = "the cache file could not be read";
                return null;
            }

            if (!TravelSave.HasMarshalPreamble(bytes, 0))
            {
                failure = "the cache file has no FSOv preamble";
                return null;
            }

            try
            {
                var marshal = new VMMarshal();
                using (var stream = new MemoryStream(bytes))
                using (var reader = new BinaryReader(stream))
                {
                    marshal.Deserialize(reader);
                }
                if (marshal.Context == null || marshal.Entities == null)
                {
                    failure = "the cache file deserialised to nothing";
                    return null;
                }
                return marshal;
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("BlueprintReset: the cached rebuild at '" + cachedPath
                    + "' is damaged.", e);
                failure = "the cache file is damaged";
                return null;
            }
        }

        /// <summary>
        /// The fallback the reset takes when there is no cached rebuild: hand the blueprint itself
        /// to the VM as a <see cref="VMBlueprintRestoreCmd"/>.
        /// </summary>
        /// <remarks>
        /// Guarded in its own right, which it never was: this used to run inside the catch that
        /// led to it, so a <c>File.ReadAllBytes</c> of a missing or unreadable lot file threw
        /// straight out of <see cref="BlueprintReset"/> and onto the game thread. A lot that
        /// cannot be restored at all is now reported and nothing is sent, which leaves the lot
        /// exactly as it was rather than half-reset.
        /// </remarks>
        private static void RestoreBlueprint(string path, VM vm)
        {
            var floorClip = Rectangle.Empty;
            var offset = new Point();
            const int targetSize = 0;

            var jobLevel = JobLevelFromPath(path);
            byte[] payload;
            try
            {
                payload = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                SimitoneLog.Error("BlueprintReset: could not read the blueprint at '" + path
                    + "', so the lot was not reset. Restore it from the install, or delete the lot"
                    + " and let the game seed a new one.", e);
                return;
            }

            vm.SendCommand(new VMBlueprintRestoreCmd
            {
                JobLevel = jobLevel,
                XMLData = payload,
                IffData = IsIffPath(path),

                FloorClipX = floorClip.X,
                FloorClipY = floorClip.Y,
                FloorClipWidth = floorClip.Width,
                FloorClipHeight = floorClip.Height,
                OffsetX = offset.X,
                OffsetY = offset.Y,
                TargetSize = targetSize
            });
        }
    }
}
