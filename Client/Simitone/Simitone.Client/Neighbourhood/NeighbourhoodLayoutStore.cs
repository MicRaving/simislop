/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The generated-world sidecar: <c>&lt;userDir&gt;Neighbourhoods/generated/&lt;slot&gt;.json</c>,
    /// holding one neighbourhood's <see cref="NeighbourhoodLayout"/> and nothing else.
    ///
    /// <para>
    /// <b>Where it goes.</b> Under the user's own Simitone directory, beside the custom-name
    /// sidecar <see cref="NeighbourhoodManager"/> already keeps, and never inside a
    /// <c>UserData*</c> folder. That is a hard boundary, not a convention: a TS1 neighbourhood
    /// folder is the game's data, the game enumerates it strictly
    /// (<c>TS1NeighborhoodProvider.LoadCharacters</c> aborts on the first file in
    /// <c>Characters/</c> that is not an <c>.iff</c>), and the whole promise of Scope A is that
    /// nothing derived is ever written back into a save. <see cref="Save"/> re-derives the path
    /// from the slot id and then <em>checks</em> it against every slot folder before touching
    /// the disk, so a future change to the path shape cannot quietly start writing into a
    /// neighbourhood.
    /// </para>
    ///
    /// <para>
    /// <b>What it holds.</b> The layout and the seed, and - from schema 2 - the session world state
    /// (see <see cref="NeighbourhoodWorldState"/>). Not the 512x512 arrays: those are a few
    /// hundred kilobytes of JSON per neighbourhood and they are a pure function of the layout
    /// and the seed, so the generator rebuilds them in memory every time it is asked. A cached
    /// layout is therefore small, human-readable, and cheap to write. Nor any lot's contents:
    /// those stay in <c>HouseNN.iff</c>, written through the house save, because a sidecar that
    /// held a lot's objects would be a second and worse copy of the player's save.
    /// </para>
    ///
    /// <para>
    /// <b>How it is written.</b> Through <see cref="AtomicFileCommit"/>, the same stage /
    /// backup / commit contract the house save and the names sidecar use: a crash mid-write
    /// leaves the previous layout readable and the new one staged, never a truncated file where
    /// a good one used to be.
    /// </para>
    ///
    /// <para>
    /// <b>When it is believed.</b> Two stamps have to match: <c>schemaVersion</c> (the shape of
    /// the file) and <c>generatorVersion</c> (the layout algorithm), plus the seed the world is
    /// being generated with. Anything else is ignored and regenerated - never half-read. A file
    /// this build cannot parse is treated the same way: there is exactly one safe reading of a
    /// corrupt file, and it is "there is nothing here".
    /// </para>
    ///
    /// <para>
    /// <b>One deliberate exception to "never half-read".</b> A schema-2 file whose
    /// <c>worldState</c> member is unreadable - wrong shape, or stamped with a world version this
    /// build does not know - keeps its <em>layout</em>. The lots in it are a pure function of the
    /// seed and the generator version, both of which are still verified, so throwing the whole
    /// file away would cost a regeneration over a bad clock. Instead the world state is replaced
    /// with <see cref="NeighbourhoodWorldState.CreateDefault"/> and the reason is logged: the
    /// player loses a session, not a neighbourhood. A layout body that will not parse is still
    /// refused outright.
    /// </para>
    /// </summary>
    public static class NeighbourhoodLayoutStore
    {
        /// <summary>
        /// The folder under the user's Simitone directory. The same folder
        /// <see cref="NeighbourhoodManager"/> keeps <c>names.json</c> in, so all of a player's
        /// neighbourhood metadata lives in one place.
        /// </summary>
        public const string RootFolderName = NeighbourhoodManager.NamesFolderName;

        /// <summary>The sub-folder holding the generated-world sidecars.</summary>
        public const string GeneratedFolderName = "generated";

        /// <summary>Extension of a sidecar file.</summary>
        public const string FileExtension = ".json";

        /// <summary>
        /// Schema version this build writes. Bumped when the <em>shape</em> of the file changes,
        /// independently of the generator version.
        /// </summary>
        public const int SchemaVersion = NeighbourhoodLayout.CurrentSchemaVersion;

        /// <summary>
        /// The oldest schema this build still reads. A layout stamped with it is read and migrated
        /// in memory to <see cref="SchemaVersion"/>; see <see cref="TryDeserialize"/>.
        /// </summary>
        public const int MinSchemaVersion = NeighbourhoodLayout.MinSchemaVersion;

        // ---- paths ----------------------------------------------------------------------------

        /// <summary>
        /// <c>&lt;userDir&gt;Neighbourhoods/generated</c>, or <c>null</c> when there is no user
        /// directory.
        /// </summary>
        public static string GeneratedFolder(string userDir)
        {
            if (string.IsNullOrEmpty(userDir)) return null;
            return Path.Combine(Path.Combine(userDir, RootFolderName), GeneratedFolderName);
        }

        /// <summary>
        /// The sidecar path for a slot, <c>&lt;userDir&gt;Neighbourhoods/generated/&lt;slot&gt;.json</c>,
        /// or <c>null</c> for an unknown slot or a missing user directory.
        /// </summary>
        /// <remarks>
        /// Named by the slot <em>id</em>, so the file name is the key and the path is not. Slot
        /// ids are the same nine the provider resolves to <c>UserData</c> ..
        /// <c>UserData8</c> - the convention <see cref="NeighbourhoodManager.SlotFolderName"/>
        /// spells out, and <see cref="IsKnownSlot"/> is that convention's gate.
        /// </remarks>
        public static string PathFor(string userDir, int slot)
        {
            if (!IsKnownSlot(slot)) return null;
            var folder = GeneratedFolder(userDir);
            if (folder == null) return null;
            return Path.Combine(folder, slot.ToString(CultureInfo.InvariantCulture) + FileExtension);
        }

        /// <summary>
        /// Is this one of the nine neighbourhood slots? The same gate
        /// <see cref="NeighbourhoodManager"/> applies, and the reason a bad id cannot turn a
        /// sidecar path into an arbitrary path.
        /// </summary>
        public static bool IsKnownSlot(int slot)
        {
            return NeighbourhoodManager.IsKnownSlot(slot);
        }

        /// <summary>
        /// Would writing <paramref name="path"/> put anything inside a TS1 neighbourhood folder?
        ///
        /// <para>
        /// Compares the path against every slot folder in <paramref name="userDir"/>. This is the
        /// check that turns "the sidecar lives outside <c>UserData</c>" from a claim about where
        /// the constant says to put it into a property of the resolved path, which is the thing
        /// that actually matters.
        /// </para>
        /// </summary>
        public static bool IsInsideUserData(string userDir, string path)
        {
            if (string.IsNullOrEmpty(userDir) || string.IsNullOrEmpty(path)) return false;

            for (var slot = NeighbourhoodManager.MinSlotId; slot <= NeighbourhoodManager.MaxSlotId; slot++)
            {
                var folder = NeighbourhoodManager.SlotPath(userDir, slot);
                if (folder == null) continue;
                if (IsUnder(folder, path)) return true;
            }
            return false;
        }

        /// <summary>Is <paramref name="path"/> inside the folder <paramref name="folder"/>?</summary>
        private static bool IsUnder(string folder, string path)
        {
            var root = Normalize(folder);
            var target = Normalize(path);
            if (root == null || target == null) return false;
            //compare against root + separator so "C:\Sims\UserDataExtra" is not "inside
            //C:\Sims\UserData"
            return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, root, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                return Path.GetFullPath(path).TrimEnd('/', '\\');
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- writing --------------------------------------------------------------------------

        /// <summary>
        /// Write a layout to its slot's sidecar.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the file was committed. <c>false</c> - with the reason logged - for
        /// an unknown slot, a missing user directory, a path that resolves inside a TS1
        /// neighbourhood folder, or any IO failure.
        /// </returns>
        public static bool Save(string userDir, NeighbourhoodLayout layout)
        {
            if (layout == null)
            {
                SimitoneLog.Warn("NeighbourhoodLayoutStore: nothing to write; there is no layout.");
                return false;
            }

            var path = PathFor(userDir, layout.Slot);
            if (path == null)
            {
                SimitoneLog.Warn("NeighbourhoodLayoutStore: slot " + layout.Slot
                    + " is not one of the " + NeighbourhoodManager.SlotCount + " neighbourhood slots.");
                return false;
            }

            //the last line of defence, and the only one that survives someone changing the path
            //constants above
            if (IsInsideUserData(userDir, path))
            {
                SimitoneLog.Error("NeighbourhoodLayoutStore: refusing to write '" + path
                    + "'; it resolves inside a TS1 neighbourhood folder.");
                return false;
            }

            try
            {
                //same stage / backup / commit contract as the house save and names.json
                AtomicFileCommit.Write(path, Serialize(layout));
                return true;
            }
            catch (Exception e)
            {
                SimitoneLog.Error("NeighbourhoodLayoutStore: could not write '" + path + "'.", e);
                return false;
            }
        }

        /// <summary>Delete a slot's sidecar, so the next build regenerates from scratch.</summary>
        /// <returns><c>true</c> when the file is gone, including when it never existed.</returns>
        public static bool Delete(string userDir, int slot)
        {
            var path = PathFor(userDir, slot);
            if (path == null) return false;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodLayoutStore: could not delete '" + path + "'.", e);
                return false;
            }
        }

        // ---- reading --------------------------------------------------------------------------

        /// <summary>
        /// Read a slot's sidecar, with no opinion about whether it is still current.
        /// </summary>
        /// <returns>
        /// The layout, or <c>null</c> for anything this build will not stand behind: no file, an
        /// unreadable file, a file that is not JSON, a file whose schema or generator version is
        /// not one we understand, or a file whose contents do not describe a layout this build
        /// can hold. In every one of those cases the answer is "regenerate", never a partly
        /// filled layout.
        /// </returns>
        public static NeighbourhoodLayout Load(string userDir, int slot)
        {
            var path = PathFor(userDir, slot);
            if (path == null) return null;

            byte[] bytes;
            try
            {
                if (!File.Exists(path)) return null;
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodLayoutStore: could not read '" + path + "'.", e);
                return null;
            }

            NeighbourhoodLayout layout;
            string reason;
            if (!TryDeserialize(bytes, out layout, out reason))
            {
                SimitoneLog.Warn("NeighbourhoodLayoutStore: ignoring '" + path + "': " + reason + ".");
                return null;
            }

            //a file that is valid but describes a different slot is a hand-copy mistake; reading
            //it anyway would put one neighbourhood's lots in another's world
            if (layout.Slot != slot)
            {
                SimitoneLog.Warn("NeighbourhoodLayoutStore: ignoring '" + path + "': it describes slot "
                    + layout.Slot + ", not slot " + slot + ".");
                return null;
            }

            return layout;
        }

        /// <summary>
        /// Read a slot's sidecar and check it against the world about to be generated.
        /// </summary>
        /// <remarks>
        /// This is the whole of §4.2's invalidation rule: a sidecar is only reused when its
        /// schema, its generator version and its seed are the ones the caller is about to
        /// generate with. Everything else - including a lot whose <c>HouseHash</c> has moved,
        /// which is a per-lot question rather than a whole-file one - is the caller's to handle
        /// after it has the layout.
        /// </remarks>
        /// <returns>The layout, or <c>null</c> when there is nothing reusable.</returns>
        public static NeighbourhoodLayout LoadFor(string userDir, int slot, int seed, int generatorVersion)
        {
            var layout = Load(userDir, slot);
            if (layout == null) return null;
            if (!layout.IsCurrentFor(seed, generatorVersion)) return null;
            return layout;
        }

        /// <summary>
        /// Which of a layout's lots have a <c>HouseHash</c> that no longer matches the one the
        /// content source reports - i.e. which lots were edited since the layout was built.
        /// </summary>
        /// <remarks>
        /// §4.2's cheap invalidation: only these lots' placements, and the roads and terrain
        /// that touch them, are stale. A lot that is in the source but not in the layout, and a
        /// lot in the layout the source no longer knows, are reported as changed too - both mean
        /// the layout is out of date in a way a hash comparison alone would miss.
        /// </remarks>
        /// <param name="layout">The cached layout, or <c>null</c>.</param>
        /// <param name="source">The neighbourhood to compare against.</param>
        /// <param name="mode">The neighbourhood mode to read.</param>
        public static IReadOnlyList<short> ChangedLots(NeighbourhoodLayout layout, ILotContentSource source, int mode)
        {
            var changed = new List<short>();
            if (layout == null || source == null) return changed;

            var reads = NeighbourhoodLayoutBuilder.ReadLots(source, mode);
            var current = new Dictionary<short, uint>();
            for (int i = 0; i < reads.Count; i++)
            {
                current[reads[i].Number] = reads[i].HouseHash;
            }

            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var lot = layout.Lots[i];
                uint hash;
                if (!current.TryGetValue(lot.Number, out hash) || hash != lot.HouseHash)
                {
                    changed.Add(lot.Number);
                }
            }

            for (int i = 0; i < reads.Count; i++)
            {
                if (layout.FindLot(reads[i].Number) == null) changed.Add(reads[i].Number);
            }

            return changed;
        }

        // ---- serialisation --------------------------------------------------------------------

        /// <summary>
        /// Render a layout as the sidecar's bytes. Indented, and with the lots in the layout's
        /// own order (ascending lot number), so the same layout always produces the same file.
        /// </summary>
        /// <remarks>
        /// The extent is written for readability - it is what a reader of the file wants to see
        /// first, and it is what the renderer would use to frame a camera - but it is
        /// <em>recomputed</em> on load rather than trusted, so a hand-edited extent cannot put
        /// the generator's idea of the world's bounds out of step with the lots in the file.
        ///
        /// <para>
        /// The stamp written is always this build's <see cref="SchemaVersion"/>, never the one the
        /// layout was built with: a layout read from a schema-1 file and written back out is a
        /// schema-2 file, and that is the whole of the 1 -> 2 migration on disk.
        /// </para>
        ///
        /// <para>
        /// <b>Schema 1 is a byte-identical prefix of this.</b> Everything up to and including
        /// <c>lots</c> is written exactly as schema 1 wrote it, and <c>worldState</c> - the only
        /// member schema 2 added - is written last and only when the layout has one. A layout with
        /// no world state therefore serialises to the document schema 1 produced, down to the
        /// byte, which is what lets an old build ignore a new file's extra member and lets a new
        /// build keep every cached Scope A layout.
        /// </para>
        /// </remarks>
        public static byte[] Serialize(NeighbourhoodLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));

            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", NeighbourhoodLayout.CurrentSchemaVersion);
                    //"version" is the name the plan's example uses for the same stamp; written as
                    //well so a file is readable by eye against the documentation, and read back
                    //from either
                    writer.WriteNumber("version", NeighbourhoodLayout.CurrentSchemaVersion);
                    writer.WriteNumber("generatorVersion", layout.GeneratorVersion);
                    writer.WriteNumber("slot", layout.Slot);
                    writer.WriteNumber("seed", layout.Seed);
                    writer.WriteNumber("mapSize", layout.MapSize);

                    writer.WriteStartObject("extent");
                    writer.WriteNumber("minX", layout.Extent.MinX);
                    writer.WriteNumber("minY", layout.Extent.MinY);
                    writer.WriteNumber("maxX", layout.Extent.MaxX);
                    writer.WriteNumber("maxY", layout.Extent.MaxY);
                    writer.WriteEndObject();

                    writer.WriteStartArray("lots");
                    for (int i = 0; i < layout.Lots.Count; i++)
                    {
                        WriteLot(writer, layout.Lots[i]);
                    }
                    writer.WriteEndArray();

                    //Schema 2, and the only member it added. Written last and only when there is
                    //something to say, so a layout without a world state is byte-identical to the
                    //schema-1 document.
                    if (layout.WorldState != null)
                    {
                        WriteWorldState(writer, layout.WorldState);
                    }

                    writer.WriteEndObject();
                }

                return buffer.ToArray();
            }
        }

        private static void WriteLot(Utf8JsonWriter writer, NeighbourhoodLot lot)
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", lot.Number);
            writer.WriteNumber("zone", lot.Zone);
            writer.WriteNumber("size", lot.Size);
            writer.WriteNumber("mapX", lot.MapX);
            writer.WriteNumber("mapY", lot.MapY);
            writer.WriteNumber("worldX", lot.WorldX);
            writer.WriteNumber("worldY", lot.WorldY);
            writer.WriteNumber("rotation", lot.Rotation);
            writer.WriteNumber("terrain", (int)lot.TerrainType);
            writer.WriteNumber("baseHeight", lot.BaseHeight);
            writer.WriteNumber("doorSide", (int)lot.DoorSide);
            writer.WriteString("houseHash", lot.HouseHash.ToString("x8", CultureInfo.InvariantCulture));
            writer.WriteBoolean("hasHouse", lot.HasHouse);

            //§15 forward fields. Written only when they carry something, so a file with none of
            //them is a file a build that has never heard of them wrote.
            if (lot.LotState != NeighbourhoodLotState.Unloaded)
            {
                writer.WriteNumber("lotState", (int)lot.LotState);
            }
            if (lot.LastSimulated != 0L)
            {
                writer.WriteNumber("lastSimulated", lot.LastSimulated);
            }
            if (lot.Entrance.HasValue)
            {
                writer.WriteStartObject("entrance");
                writer.WriteNumber("worldX", lot.Entrance.Value.WorldX);
                writer.WriteNumber("worldY", lot.Entrance.Value.WorldY);
                writer.WriteNumber("facing", lot.Entrance.Value.Facing);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Write the session world state: the clock, the active lot and the hops in progress.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The shape, for a world that has been going for a while:
        /// </para>
        /// <code>
        /// "worldState": {
        ///   "worldVersion": 1,
        ///   "flags": 1,
        ///   "activeLot": 21,
        ///   "clock": {
        ///     "available": true, "hours": 13, "minutes": 45, "minuteFractions": 30,
        ///     "ticksPerMinute": 60, "dayOfMonth": 12, "month": 5, "year": 2030,
        ///     "ticks": 987654321
        ///   },
        ///   "transit": [
        ///     { "sim": 5, "origin": 21, "destination": 31, "mode": 1, "progress": 40 }
        ///   ]
        /// }
        /// </code>
        /// <para>
        /// Two omissions and no others. <c>worldVersion</c> and <c>flags</c> are left out at 0, and
        /// <c>transit</c> is left out when there is none; <c>activeLot</c> and the whole
        /// <c>clock</c> object are always written when there is a world state at all, because
        /// their <em>absence</em> is the fact - "no active lot" and "the clock was unavailable" are
        /// the two answers that are easy to lose and expensive to lose. A record that is not a hop
        /// (origin equal to destination, which the defaulted record is) is not written, so a hop
        /// that was created and abandoned does not come back as a lot travelling to itself.
        /// </para>
        /// </remarks>
        private static void WriteWorldState(Utf8JsonWriter writer, NeighbourhoodWorldState world)
        {
            writer.WriteStartObject("worldState");

            if (world.WorldVersion != 0)
            {
                writer.WriteNumber("worldVersion", world.WorldVersion);
            }
            if (world.Flags != 0)
            {
                writer.WriteNumber("flags", world.Flags);
            }
            writer.WriteNumber("activeLot", world.ActiveLot);

            writer.WriteStartObject("clock");
            writer.WriteBoolean("available", world.ClockAvailable);
            writer.WriteNumber("hours", world.Hours);
            writer.WriteNumber("minutes", world.Minutes);
            writer.WriteNumber("minuteFractions", world.MinuteFractions);
            writer.WriteNumber("ticksPerMinute", world.TicksPerMinute);
            writer.WriteNumber("dayOfMonth", world.DayOfMonth);
            writer.WriteNumber("month", world.Month);
            writer.WriteNumber("year", world.Year);
            writer.WriteNumber("ticks", world.Ticks);
            writer.WriteEndObject();

            if (world.Transit != null && world.Transit.Count > 0)
            {
                writer.WriteStartArray("transit");
                for (int i = 0; i < world.Transit.Count; i++)
                {
                    var hop = world.Transit[i];
                    if (hop == null || !hop.IsHop) continue;
                    writer.WriteStartObject();
                    writer.WriteNumber("sim", hop.Sim);
                    writer.WriteNumber("origin", hop.Origin);
                    writer.WriteNumber("destination", hop.Destination);
                    writer.WriteNumber("mode", hop.Mode);
                    writer.WriteNumber("progress", hop.Progress);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Parse a sidecar's bytes.
        /// </summary>
        /// <param name="bytes">The file's bytes.</param>
        /// <param name="layout">The layout, when it could be read and is one this build understands.</param>
        /// <param name="reason">Why not, when it could not. Suitable for the log.</param>
        /// <returns><c>true</c> when <paramref name="layout"/> is set.</returns>
        /// <remarks>
        /// <para>
        /// <b>Schema 1 is read, not refused.</b> A layout stamped below this build's schema is
        /// brought forward in memory: it is given a default <see cref="NeighbourhoodWorldState"/>
        /// and reports <see cref="NeighbourhoodLayout.CurrentSchemaVersion"/>, and nothing is
        /// written back. The migration is therefore free and lazy - the file on disk stays a
        /// schema-1 file until something saves the layout for a reason of its own, so a player who
        /// quits without ever simulating has paid nothing for the schema having moved.
        /// </para>
        ///
        /// <para>
        /// A schema this build does not know, in either direction, is refused outright, and so is a
        /// layout body that will not parse. A <em>world</em> body that will not parse is the one
        /// asymmetry: the lots around it are still a pure function of the seed and the generator
        /// version, both of which are checked below, so the layout survives with a fresh world
        /// state and the reason is logged.
        /// </para>
        /// </remarks>
        public static bool TryDeserialize(byte[] bytes, out NeighbourhoodLayout layout, out string reason)
        {
            layout = null;
            reason = null;

            if (bytes == null || bytes.Length == 0)
            {
                reason = "it is empty";
                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            }
            catch (JsonException e)
            {
                reason = "it is not valid JSON (" + e.Message + ")";
                return false;
            }
            catch (Exception e)
            {
                reason = "it could not be parsed (" + e.Message + ")";
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    reason = "it is not a layout object";
                    return false;
                }

                //a missing schema stamp is the first one, so a hand-written file works; a stamp
                //we do not know is refused outright rather than reinterpreted
                var schema = ReadInt(root, "schemaVersion", NeighbourhoodLayout.MinSchemaVersion);
                if (schema < NeighbourhoodLayout.MinSchemaVersion)
                {
                    reason = "its schema version is " + schema;
                    return false;
                }
                if (schema > NeighbourhoodLayout.CurrentSchemaVersion)
                {
                    reason = "its schema version is " + schema + ", which this build does not understand";
                    return false;
                }

                var generatorVersion = ReadInt(root, "generatorVersion", int.MinValue);
                if (generatorVersion != int.MinValue && generatorVersion < 1)
                {
                    reason = "its generator version is " + generatorVersion;
                    return false;
                }

                var slot = ReadInt(root, "slot", int.MinValue);
                if (slot == int.MinValue)
                {
                    reason = "it does not say which slot it is for";
                    return false;
                }
                if (!IsKnownSlot(slot))
                {
                    reason = "it is for slot " + slot + ", which is not a neighbourhood slot";
                    return false;
                }

                var seed = ReadInt(root, "seed", 0);
                var mapSize = ReadInt(root, "mapSize", NeighbourhoodLayout.DefaultMapSize);
                if (mapSize <= 0)
                {
                    reason = "its map size is " + mapSize;
                    return false;
                }

                var hasLots = root.TryGetProperty("lots", out var lotsElement)
                    && lotsElement.ValueKind == JsonValueKind.Array;

                var lots = new List<NeighbourhoodLot>();
                if (hasLots)
                {
                    foreach (var element in lotsElement.EnumerateArray())
                    {
                        NeighbourhoodLot lot;
                        string lotReason;
                        if (!TryReadLot(element, mapSize, out lot, out lotReason))
                        {
                            reason = "one of its lots is unreadable: " + lotReason;
                            return false;
                        }
                        lots.Add(lot);
                    }
                }

                //The world state, and the 1 -> 2 migration, in one place. A schema-1 file has no
                //world state by definition, so it gets the default one; a schema-2 file's world
                //body may be unreadable, and unlike the layout body that is survivable.
                NeighbourhoodWorldState world;
                if (schema > NeighbourhoodLayout.MinSchemaVersion)
                {
                    NeighbourhoodWorldState read;
                    string worldReason;
                    if (TryReadWorldState(root, out read, out worldReason))
                    {
                        world = read;
                    }
                    else
                    {
                        SimitoneLog.Warn("NeighbourhoodLayoutStore: this sidecar's world state is "
                            + worldReason + ", so the layout is used with a fresh one. The lots in it "
                            + "are still this seed and this generator, and they are still checked.");
                        world = NeighbourhoodWorldState.CreateDefault();
                    }
                }
                else
                {
                    //Migrated in memory only. No write, no reason: the file is not wrong, it is
                    //just older, and it will be rewritten as schema 2 the next time anything
                    //saves the layout anyway.
                    world = NeighbourhoodWorldState.CreateDefault();
                }

                try
                {
                    layout = new NeighbourhoodLayout(slot, seed,
                        generatorVersion == int.MinValue ? NeighbourhoodLayoutBuilder.CurrentGeneratorVersion : generatorVersion,
                        //whatever the file said: an older layout is read as the shape this build
                        //works in, and the next save stamps it that way
                        NeighbourhoodLayout.CurrentSchemaVersion, mapSize, lots, world);
                }
                catch (Exception e)
                {
                    reason = "it does not describe a usable layout (" + e.Message + ")";
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Read the <c>worldState</c> member of a schema-2 sidecar.
        /// </summary>
        /// <param name="root">The layout object.</param>
        /// <param name="world">The world state. A fresh default when the member is simply absent.</param>
        /// <param name="reason">Why not, when the member is present and unusable.</param>
        /// <returns><c>true</c> when <paramref name="world"/> is set.</returns>
        /// <remarks>
        /// <para>
        /// Two kinds of wrong are separated, because the callers treat them differently.
        /// A member of the wrong <em>shape</em> - a number where an object belongs, a world version
        /// from the future - is a failure, and the caller keeps the layout and logs. A member of
        /// the right shape holding an out-of-range or missing field is not: it is clamped or
        /// defaulted, exactly as the per-lot forward fields and <c>rotation</c> already are, so a
        /// hand-edited clock cannot divide by a zero it wrote itself.
        /// </para>
        /// </remarks>
        private static bool TryReadWorldState(JsonElement root, out NeighbourhoodWorldState world, out string reason)
        {
            world = null;
            reason = null;

            if (!root.TryGetProperty("worldState", out var element))
            {
                //legal: a schema-2 file written for a layout that had no session
                world = NeighbourhoodWorldState.CreateDefault();
                return true;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                reason = "it is " + Describe(element) + " rather than an object";
                return false;
            }

            var worldVersion = ReadInt(element, "worldVersion", 0);
            if (worldVersion > NeighbourhoodWorldState.CurrentWorldVersion)
            {
                reason = "it is world version " + worldVersion + ", which this build does not understand";
                return false;
            }

            world = new NeighbourhoodWorldState
            {
                WorldVersion = worldVersion,
                Flags = ReadInt(element, "flags", 0),
                ActiveLot = ReadInt(element, "activeLot", 0)
            };

            if (element.TryGetProperty("clock", out var clock))
            {
                if (clock.ValueKind != JsonValueKind.Object)
                {
                    reason = "its clock is " + Describe(clock) + " rather than an object";
                    world = null;
                    return false;
                }

                world.ClockAvailable = ReadBool(clock, "available", false);
                world.Hours = Clamp(ReadInt(clock, "hours", 0),
                    NeighbourhoodWorldState.MinHours, NeighbourhoodWorldState.MaxHours);
                world.Minutes = Clamp(ReadInt(clock, "minutes", 0),
                    NeighbourhoodWorldState.MinMinutes, NeighbourhoodWorldState.MaxMinutes);
                world.MinuteFractions = Math.Max(0, ReadInt(clock, "minuteFractions", 0));
                world.TicksPerMinute = Math.Max(0, ReadInt(clock, "ticksPerMinute", 0));
                world.DayOfMonth = Clamp(ReadInt(clock, "dayOfMonth", 0),
                    NeighbourhoodWorldState.MinDayOfMonth, NeighbourhoodWorldState.MaxDayOfMonth);
                world.Month = Clamp(ReadInt(clock, "month", 0),
                    NeighbourhoodWorldState.MinMonth, NeighbourhoodWorldState.MaxMonth);
                world.Year = ReadInt(clock, "year", 0);
                world.Ticks = 0L;
                if (clock.TryGetProperty("ticks", out var ticks) && ticks.ValueKind == JsonValueKind.Number
                    && ticks.TryGetInt64(out var parsedTicks))
                {
                    world.Ticks = parsedTicks;
                }
            }

            if (element.TryGetProperty("transit", out var transit))
            {
                if (transit.ValueKind != JsonValueKind.Array)
                {
                    reason = "its transit is " + Describe(transit) + " rather than an array";
                    world = null;
                    return false;
                }

                foreach (var hop in transit.EnumerateArray())
                {
                    if (hop.ValueKind != JsonValueKind.Object)
                    {
                        reason = "one of its transit records is " + Describe(hop) + " rather than an object";
                        world = null;
                        return false;
                    }

                    var record = new NeighbourhoodTransitRecord(
                        Math.Max(0, ReadInt(hop, "sim", 0)),
                        ReadInt(hop, "origin", 0),
                        ReadInt(hop, "destination", 0),
                        ReadInt(hop, "mode", 0),
                        Clamp(ReadInt(hop, "progress", NeighbourhoodTransitRecord.MinProgress),
                            NeighbourhoodTransitRecord.MinProgress, NeighbourhoodTransitRecord.MaxProgress));

                    //a lot travelling to itself is not a hop; a defaulted record is the degenerate
                    //case, and both are dropped rather than loaded and handed to B7's resume path
                    if (!record.IsHop) continue;
                    world.Transit.Add(record);
                }
            }

            return true;
        }

        private static string Describe(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array: return "an array";
                case JsonValueKind.String: return "a string";
                case JsonValueKind.Number: return "a number";
                case JsonValueKind.True:
                case JsonValueKind.False: return "a boolean";
                case JsonValueKind.Null: return "null";
                default: return "a " + element.ValueKind.ToString().ToLowerInvariant();
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static bool TryReadLot(JsonElement element, int mapSize, out NeighbourhoodLot lot, out string reason)
        {
            lot = null;
            reason = null;

            if (element.ValueKind != JsonValueKind.Object)
            {
                reason = "it is not an object";
                return false;
            }

            var number = ReadInt(element, "number", int.MinValue);
            if (number == int.MinValue || number < short.MinValue || number > short.MaxValue)
            {
                reason = "its number is " + number;
                return false;
            }

            var size = ReadInt(element, "size", 0);
            if (size < 1 || size > mapSize)
            {
                reason = "lot " + number + " has size " + size;
                return false;
            }

            var worldX = ReadInt(element, "worldX", 0);
            var worldY = ReadInt(element, "worldY", 0);
            if (worldX < 0 || worldY < 0 || worldX + size > mapSize || worldY + size > mapSize)
            {
                reason = "lot " + number + " is placed outside the map (" + worldX + "," + worldY + ")";
                return false;
            }

            uint houseHash;
            if (!TryReadHouseHash(element, out houseHash))
            {
                reason = "lot " + number + " has a house hash that is not eight hex digits";
                return false;
            }

            var terrain = ReadInt(element, "terrain", (int)FSO.Content.Model.TerrainType.GRASS);
            var doorSide = ReadInt(element, "doorSide", (int)NeighbourhoodLotSide.North);
            if (doorSide < 0 || doorSide > 3) doorSide = (int)NeighbourhoodLotSide.North;

            lot = new NeighbourhoodLot
            {
                Number = (short)number,
                Zone = (short)ReadInt(element, "zone", LotZoning.ZoneResidential),
                Size = size,
                MapX = ReadInt(element, "mapX", 0),
                MapY = ReadInt(element, "mapY", 0),
                WorldX = worldX,
                WorldY = worldY,
                Rotation = ReadInt(element, "rotation", 0) & 3,
                TerrainType = (FSO.Content.Model.TerrainType)terrain,
                BaseHeight = (short)ReadInt(element, "baseHeight", 0),
                DoorSide = (NeighbourhoodLotSide)doorSide,
                HouseHash = houseHash,
                HasHouse = ReadBool(element, "hasHouse", houseHash != 0u)
            };

            //§15 forward fields: absent means defaulted, which is what a Scope A file does
            var lotState = ReadInt(element, "lotState", (int)NeighbourhoodLotState.Unloaded);
            if (lotState < 0 || lotState > (int)NeighbourhoodLotState.Active) lotState = (int)NeighbourhoodLotState.Unloaded;

            var lastSimulated = 0L;
            if (element.TryGetProperty("lastSimulated", out var lastSimulatedElement)
                && lastSimulatedElement.ValueKind == JsonValueKind.Number
                && lastSimulatedElement.TryGetInt64(out var parsedLast))
            {
                lastSimulated = parsedLast;
            }

            lot.LotState = (NeighbourhoodLotState)lotState;
            lot.LastSimulated = lastSimulated;

            if (element.TryGetProperty("entrance", out var entrance) && entrance.ValueKind == JsonValueKind.Object)
            {
                lot.Entrance = new NeighbourhoodLotEntrance(
                    ReadInt(entrance, "worldX", 0),
                    ReadInt(entrance, "worldY", 0),
                    ReadInt(entrance, "facing", 0));
            }

            return true;
        }

        private static bool TryReadHouseHash(JsonElement element, out uint hash)
        {
            hash = 0u;
            if (!element.TryGetProperty("houseHash", out var value)) return true;   //absent is "no house"

            if (value.ValueKind == JsonValueKind.Number)
            {
                //tolerated so a hand-edited file that drops the hex formatting still loads
                if (value.TryGetUInt32(out hash)) return true;
                return false;
            }

            if (value.ValueKind != JsonValueKind.String) return false;
            var text = value.GetString();
            if (string.IsNullOrEmpty(text)) return true;
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
        }

        private static int ReadInt(JsonElement element, string name, int fallback)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt32(out var result)) return result;
            }
            return fallback;
        }

        private static bool ReadBool(JsonElement element, string name, bool fallback)
        {
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }
    }
}
