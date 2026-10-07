/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Common;
using FSO.Common.Utils;
using FSO.Content;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView;
using FSO.SimAntics;
using FSO.SimAntics.Marshals;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Utils;
using System;
using System.Globalization;
using System.IO;

namespace Simitone.Client.UI.Services
{
    /// <summary>
    /// Everything <c>TS1GameScreen</c> does to write a lot to disk, moved out of the screen.
    ///
    /// <para>
    /// The screen is now a thin delegate: it supplies the live collaborators (the VM, the world and
    /// the active family) and this service owns the order of operations, the file formats and the
    /// failure handling. The screen keeps ownership of its own state - the service never caches
    /// the VM or the world - so nothing here changes when a lot is loaded, travelled away from or
    /// torn down.
    /// </para>
    ///
    /// <para>
    /// Two save shapes exist and both are preserved byte for byte:
    /// <list type="bullet">
    /// <item><b>home lot</b> - the canonical <c>House{NN}.iff</c> (FSOV + SIMI + both thumbnails),
    /// written through <see cref="AtomicFileCommit"/> so the previous save is staged as
    /// <c>.tmp</c>, copied to <c>.bak</c> and only then replaced.</item>
    /// <item><b>travel lot</b> - the shipped <c>House{NN}.iff</c> is a shared template and is never
    /// written; the marshal goes to the family's private
    /// <c>&lt;UserDir&gt;/TravelLots/House{NN}.fsov</c> slot with the same atomic contract, via
    /// <see cref="TravelSave"/>.</item>
    /// </list>
    /// The order within a save is unchanged: SIMI stamped and the marshal taken once, then the lot
    /// file committed, and only then the neighbourhood - and on the travel path the neighbourhood
    /// is saved even when the slot write failed, because budget, family and lot ownership live
    /// there and not in the slot.
    /// </para>
    /// </summary>
    public sealed class SaveService
    {
        /// <summary>
        /// The screen's dialog service, used for the one alert a failed save produces. Passed in
        /// rather than created here so the save error goes through the same dialog path as every
        /// other alert in the game.
        /// </summary>
        private readonly DialogService _dialogs;

        /// <summary>
        /// Creates a save service that reports failures through <paramref name="dialogs"/>.
        /// </summary>
        /// <param name="dialogs">The screen's <see cref="DialogService"/>.</param>
        public SaveService(DialogService dialogs)
        {
            _dialogs = dialogs;
        }

        /// <summary>
        /// Whether there is a lot to save at all. A save with no VM, no world or no family would
        /// have nothing to write, so the call is logged and abandoned rather than half-attempted.
        /// </summary>
        /// <param name="vm">The live VM, or <c>null</c> when no lot is loaded.</param>
        /// <param name="world">The live world, or <c>null</c>.</param>
        /// <param name="family">The family being played, or <c>null</c>.</param>
        public static bool HasSaveState(VM vm, World world, FAMI family)
        {
            return vm != null && world != null && family != null;
        }

        /// <summary>
        /// A lot or family number as it appears in a log line.
        /// </summary>
        /// <remarks>
        /// Every save log in this class pairs a number with a path - "saved house 5 to
        /// .../House05.iff", "saved for family 7, not for the family now playing at 9" - and the
        /// paths are built invariantly (<see cref="TravelSave.SlotPathFor"/>,
        /// <c>GetHousePath</c>). A bare <c>int.ToString()</c> is the current culture, so on a
        /// machine that substitutes digits the line would name a file that does not exist and
        /// contradict itself. One renderer, used by every number in these lines, is what keeps the
        /// sentence coherent; the alternative is a per-call-site culture argument nobody remembers
        /// to pass.
        ///
        /// <para>
        /// Pure and total: formatting an int cannot throw, and this deliberately does not clamp
        /// or reject an out-of-range number - a log line that quietly replaced -1 with something
        /// plausible would be worse than one that prints it.
        /// </para>
        /// </remarks>
        /// <param name="value">The lot or family number.</param>
        internal static string Lot(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Saves the current lot.
        ///
        /// <para>
        /// Never throws: every failure is logged and, where it matters to the player, turned into
        /// the localised save-error dialog, because this is called from UI event handlers that must
        /// not bring the game down.
        /// </para>
        /// </summary>
        /// <param name="vm">The live VM.</param>
        /// <param name="world">The live world (needed for the lot thumbnail).</param>
        /// <param name="family">The family being played.</param>
        public void Save(VM vm, World world, FAMI family)
        {
            if (!HasSaveState(vm, world, family))
            {
                SimitoneLog.Warn("Save() called with no active lot (vm=" + (vm == null ? "null" : "ok")
                    + ", World=" + (world == null ? "null" : "ok")
                    + ", ActiveFamily=" + (family == null ? "null" : "ok") + "). Nothing to save.");
                return;
            }

            try
            {
                var houseID = vm.GetGlobalValue(10);
                var familyHouseNumber = family?.HouseNumber;

                //stamp the lot's SIMI globals, then take the one marshal both save shapes need.
                //a travel slot carries the marshal only - no SIMI chunk, no thumbnails - so this
                //runs before the home/travel split rather than inside either branch.
                vm.TS1State.UpdateSIMI(vm);
                var marshal = vm.Save();
                byte[] marshalBytes;
                using (var stream = new MemoryStream())
                {
                    marshal.SerializeInto(new BinaryWriter(stream));
                    marshalBytes = stream.ToArray();
                }

                if (TravelSave.IsTravelLot(houseID, familyHouseNumber))
                {
                    //not the family's own home. the shipped template must stay pristine, so the
                    //lot's state goes to the family's private slot. this is a normal, successful
                    //save - it used to be an explicit refusal, which is why "saving on vacation"
                    //did not work.
                    var slotFailure = WriteTravelSlot(houseID, familyHouseNumber, marshalBytes);

                    //the neighbourhood is saved on this path too: budget, family and lot
                    //ownership do not live in the slot file and must not be lost because a lot
                    //write failed.
                    Content.Get().Neighborhood.SaveNeighbourhood(true);

                    //only now that the neighbourhood is safely down is it worth telling the
                    //player that the lot itself was not.
                    if (slotFailure != null) ShowSaveError(slotFailure);
                    return;
                }

                //home lot: the canonical House{NN}.iff, exactly as before.
                //one GetLotThumb call produces both pictures: the returned texture is the lot with
                //its roof, and the callback hands back the same picture with the roof cut away.
                Texture2D roofless = null;
                var thumb = world.GetLotThumb(GameFacade.GraphicsDevice, (tex) => roofless = FSO.Common.Utils.TextureUtils.Decimate(tex, GameFacade.GraphicsDevice, 2, false));
                thumb = FSO.Common.Utils.TextureUtils.Decimate(thumb, GameFacade.GraphicsDevice, 2, false);

                var tPNG = GeneratePNG(thumb);
                var rPNG = GeneratePNG(roofless);

                //serialise the whole IFF up front so the file write below is a single
                //contiguous copy that cannot fail halfway through encoding.
                var houseBytes = BuildHouseFileBytes(marshalBytes, vm.TS1State.SimulationInfo, tPNG, rPNG);

                var path = Content.Get().Neighborhood.GetHousePath(houseID);

                //stage, back up and commit the house file. this creates the house directory when
                //it is missing, keeps a .bak of the previous save, and leaves the original intact
                //if any step fails.
                var bakPath = AtomicFileCommit.Write(path, houseBytes);

                //house is safely on disk - now persist the rest of the neighbourhood
                Content.Get().Neighborhood.SaveNeighbourhood(true);

                SimitoneLog.Info("Saved house " + Lot(houseID) + " to " + path
                    + (bakPath == null ? " (no previous save to back up)" : " (previous save kept at " + bakPath + ")"));
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Save failed", ex);
                ShowSaveError(ex);
            }
        }

        /// <summary>
        /// Builds the complete <c>House{NN}.iff</c> payload in memory: FSOV (the marshal), SIMI (the
        /// lot's values and globals), the with-roof thumbnail (id 513) and the roofless thumbnail
        /// (id 512), in that order.
        /// </summary>
        /// <remarks>
        /// Pure, and the order is load-bearing - the file has always been written in this order.
        /// The supplied <paramref name="simi"/> is stamped as patch-added here, exactly as the
        /// inline save did, because the SIMI chunk comes out of the VM rather than being built
        /// here. The two thumbnails have their chunk ids assigned here, which is why
        /// <see cref="GeneratePNG"/> deliberately does not set one.
        /// </remarks>
        /// <param name="marshalBytes">The serialised <c>VMMarshal</c>.</param>
        /// <param name="simi">The lot's SIMI chunk, straight from the VM.</param>
        /// <param name="thumbnail">The with-roof thumbnail chunk.</param>
        /// <param name="roofless">The roofless thumbnail chunk.</param>
        /// <returns>The complete IFF file bytes.</returns>
        public static byte[] BuildHouseFileBytes(byte[] marshalBytes, SIMI simi, PNG thumbnail, PNG roofless)
        {
            var iff = new IffFile();

            var fsov = new FSOV();
            fsov.ChunkLabel = "Simitone Lot Data";
            fsov.ChunkID = 1;
            fsov.ChunkProcessed = true;
            fsov.ChunkType = "FSOV";
            fsov.AddedByPatch = true;
            fsov.Data = marshalBytes;

            iff.AddChunk(fsov);

            simi.ChunkProcessed = true;
            simi.AddedByPatch = true;
            iff.AddChunk(simi);

            thumbnail.ChunkID = 513;
            iff.AddChunk(thumbnail);

            roofless.ChunkID = 512;
            iff.AddChunk(roofless);

            using (var houseStream = new MemoryStream())
            {
                iff.Write(houseStream);
                return houseStream.ToArray();
            }
        }

        /// <summary>
        /// Encodes a lot thumbnail texture into its <c>PNG_</c> chunk.
        /// </summary>
        /// <remarks>
        /// A <c>null</c> texture produces a zero-length chunk rather than an exception, and says
        /// so in the log: a missing thumbnail must never cost the player their save. The chunk id
        /// is not set here - <see cref="BuildHouseFileBytes"/> decides which of the two thumbnails
        /// this is.
        /// </remarks>
        /// <param name="data">The thumbnail texture, or <c>null</c> when there is none.</param>
        public PNG GeneratePNG(Texture2D data)
        {
            if (data == null)
            {
                SimitoneLog.Warn("GeneratePNG called with no texture; writing an empty PNG chunk.");
                return BuildPngChunk(null);
            }

            using (var stream = new MemoryStream())
            {
                data.SaveAsPng(stream, data.Width, data.Height);
                return BuildPngChunk(stream.ToArray());
            }
        }

        /// <summary>
        /// Wraps PNG bytes in the <c>PNG_</c> lot-thumbnail chunk.
        /// </summary>
        /// <remarks>
        /// A <c>null</c> or empty payload becomes a zero-length chunk rather than an exception: a
        /// missing thumbnail must never cost the player their save. The chunk id is not set here -
        /// the caller decides which of the two thumbnails this is.
        /// </remarks>
        /// <param name="pngBytes">The encoded PNG, or <c>null</c> when there is no texture.</param>
        public static PNG BuildPngChunk(byte[] pngBytes)
        {
            var png = new PNG();
            png.data = (pngBytes == null || pngBytes.Length == 0) ? new byte[0] : pngBytes;
            png.ChunkLabel = "Lot Thumbnail";
            png.ChunkProcessed = true;
            png.ChunkType = "PNG_";
            png.AddedByPatch = true;
            return png;
        }

        /// <summary>
        /// Commits a travel lot's <c>VMMarshal</c> to its private slot,
        /// <c>&lt;UserDir&gt;/TravelLots/House{NN}.fsov</c>, with the same stage / backup /
        /// commit contract the home-lot save uses.
        /// </summary>
        /// <remarks>
        /// Failure is returned rather than thrown so <see cref="Save"/> can still write the
        /// neighbourhood before reporting it: the lot blob is the only thing at risk here, and
        /// the neighbourhood holds the budget, the family and every lot purchase.
        /// </remarks>
        /// <returns><c>null</c> on success, or the exception that stopped the slot being
        /// written. A missing or empty user directory is not a failure worth a dialog - it just
        /// means there is nowhere to put a slot - and is logged and reported as success.</returns>
        /// <param name="houseID">The travel lot's number.</param>
        /// <param name="familyHouseNumber">The owning family's home lot, or <c>null</c>.</param>
        /// <param name="marshalBytes">The serialised <c>VMMarshal</c>.</param>
        public Exception WriteTravelSlot(int houseID, int? familyHouseNumber, byte[] marshalBytes)
        {
            try
            {
                var slotPath = TravelSave.SlotPathFor(FSOEnvironment.UserDir, houseID);
                if (slotPath == null)
                {
                    SimitoneLog.Warn("No user directory is configured, so travel lot " + Lot(houseID)
                        + " cannot be saved to a slot. Saving only the neighbourhood.");
                    return null;
                }

                var slotBytes = TravelSave.BuildSlot(familyHouseNumber, marshalBytes);
                var bakPath = AtomicFileCommit.Write(slotPath, slotBytes);

                SimitoneLog.Info("Saved travel lot " + Lot(houseID) + " to its own slot " + slotPath
                    + (bakPath == null ? " (no previous save to back up)" : " (previous save kept at " + bakPath + ")")
                    + ". The shipped " + TravelSave.HouseFileStem(houseID) + TravelSave.HouseFileExtension
                    + " template was left untouched.");
                return null;
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Could not save travel lot " + Lot(houseID) + " to its own slot", ex);
                return ex;
            }
        }

        /// <summary>
        /// Looks for a cross-session travel slot for the lot <paramref name="lotName"/> points at
        /// and deserialises it if - and only if - it is a travel lot whose slot belongs to the
        /// family now being played.
        /// </summary>
        /// <remarks>
        /// Every rejection is quiet apart from a log line, and every one of them means the same
        /// thing to the caller: no slot, load the template. That covers a null or empty
        /// <paramref name="lotName"/>, a house id that cannot be recovered, a home lot, a lot
        /// with no slot file, a slot from a different build or a different family, and a slot
        /// whose marshal payload is truncated or corrupt. Nothing here throws.
        /// </remarks>
        /// <param name="lotName">The house path the caller is about to load.</param>
        /// <param name="houseId">The lot number if the caller knows it, else null.</param>
        /// <param name="activeFamily">The family now being played, or <c>null</c>.</param>
        /// <param name="marshal">The deserialised lot state, or <c>null</c> when no slot is
        /// usable.</param>
        /// <returns><c>true</c> when a usable slot was loaded.</returns>
        public bool TryRestoreTravelSlot(string lotName, int? houseId, FAMI activeFamily, out VMMarshal marshal)
        {
            marshal = null;

            if (!houseId.HasValue)
            {
                int parsed;
                if (TravelSave.TryParseHouseIdFromPath(lotName, out parsed)) houseId = parsed;
            }
            if (!houseId.HasValue) return false;

            var familyHouseNumber = activeFamily?.HouseNumber;

            //a slot is the travel lot's counterpart to House{NN}.iff. the home lot keeps its own
            //file, so it must never be loaded from a slot - and with no family active there is
            //nobody a slot could legitimately belong to.
            if (!TravelSave.IsTravelLot(houseId.Value, familyHouseNumber)) return false;

            var slotPath = TravelSave.SlotPathFor(FSOEnvironment.UserDir, houseId.Value);
            if (slotPath == null || !File.Exists(slotPath)) return false;

            try
            {
                var bytes = File.ReadAllBytes(slotPath);

                int version, slotFamilyNumber;
                if (!TravelSave.IsValidHeader(bytes, out version, out slotFamilyNumber))
                {
                    SimitoneLog.Warn("Ignoring travel slot " + slotPath
                        + ": it is not a readable Simitone travel slot (bad magic, version or length). Loading the shipped template instead.");
                    return false;
                }

                if (!TravelSave.ShouldRestoreSlot(slotFamilyNumber, familyHouseNumber))
                {
                    //one slot per lot, but many families visit it. a slot saved by another family
                    //must not be loaded: it holds their Sims, their budget and their lot state.
                    SimitoneLog.Warn("Ignoring travel slot " + slotPath + ": it was saved for family "
                        + Lot(slotFamilyNumber) + ", not for the family now playing at "
                        + (familyHouseNumber.HasValue ? Lot(familyHouseNumber.Value) : "no house")
                        + ". Loading the shipped template instead.");
                    return false;
                }

                var restored = new VMMarshal();
                //check the marshal's own "FSOv" preamble first: VMMarshal.Deserialize returns
                //silently when it is missing, handing back a marshal with a null context and a
                //null entity array, which would blow up much later inside VM.Load.
                if (!TravelSave.HasMarshalPreamble(bytes, TravelSave.HeaderLength))
                {
                    SimitoneLog.Warn("Ignoring travel slot " + slotPath
                        + ": its lot data does not start with a VMMarshal. Loading the shipped template instead.");
                    return false;
                }

                using (var reader = new BinaryReader(new MemoryStream(bytes, TravelSave.HeaderLength, bytes.Length - TravelSave.HeaderLength)))
                {
                    restored.Deserialize(reader);
                }
                marshal = restored;
                return true;
            }
            catch (Exception ex)
            {
                //a corrupt slot is a lost save, never a failed lot load.
                SimitoneLog.Warn("Could not read travel slot " + slotPath + " (" + ex.Message
                    + "). Loading the shipped template instead.");
                return false;
            }
        }

        /// <summary>
        /// Reuses the game's own localised save-failure strings (table 153: "Save House" /
        /// "An error was encountered saving the house."). The exception detail is inherently
        /// untranslatable technical text, so it is appended after the translated sentence.
        /// </summary>
        /// <remarks>
        /// The dialog itself is never allowed to throw: a failure to report a failure would take
        /// the game down instead of the save.
        /// </remarks>
        /// <param name="ex">The failure to report, or <c>null</c> for the message alone.</param>
        public void ShowSaveError(Exception ex)
        {
            try
            {
                _dialogs.ShowAlert(
                    GameFacade.Strings.GetString("153", "15"),
                    GameFacade.Strings.GetString("153", "14"),
                    ex == null ? "" : ex.Message);
            }
            catch (Exception alertEx)
            {
                //never let the error dialog itself take down the game
                SimitoneLog.Error("Failed to show save error dialog", alertEx);
            }
        }
    }
}
