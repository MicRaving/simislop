/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The generated map on disk: <c>&lt;userDir&gt;Neighbourhoods/generated/&lt;slot&gt;.raster</c>,
    /// the six 512x512 arrays a <see cref="NeighbourhoodMapGenerator"/> pass produced, beside the
    /// layout sidecar that describes the world they belong to.
    ///
    /// <para>
    /// <b>Why it exists, and what it changes.</b> §4.2 of the plan says the sidecar holds the
    /// layout and not the raster, because the raster is a pure function of the layout and the seed
    /// and "a few hundred KB of JSON per map would be unnecessary". That is right about the
    /// <em>format</em> and wrong about the consequence: a pure function still has to be evaluated,
    /// and evaluating it is several seconds of road graph, terrain fill and seam solving. The
    /// in-memory <see cref="NeighbourhoodWorldCache"/> makes the second <em>visit</em> free, and
    /// this file makes the second <em>launch</em> free too, by keeping the answer next to the
    /// question. JSON was the wrong carrier for three megabytes of bytes; a flat binary dump with
    /// a stamp is the right one.
    /// </para>
    ///
    /// <para>
    /// <b>Never anything but a cache.</b> A file here is only used when its stamp matches the
    /// layout it is being asked about exactly - same slot, same seed, same generator version, same
    /// map size, same arrangement - and a file that is missing, truncated, corrupt, from a build
    /// this one does not understand, or simply older costs one regeneration and nothing else. There
    /// is exactly one safe reading of a file that does not check out, and it is "there is nothing
    /// here": a partly-read map would put some tiles from one world and some from another, and
    /// would look entirely plausible.
    /// </para>
    ///
    /// <para>
    /// <b>Where it goes.</b> Under the user's own Simitone directory, beside
    /// <see cref="NeighbourhoodLayoutStore"/>'s <c>&lt;slot&gt;.json</c>, and never inside a
    /// <c>UserData*</c> folder - <see cref="Save"/> re-derives the path from the slot id and then
    /// checks the resolved path against every slot folder, so a future change to the path shape
    /// cannot quietly start writing into a TS1 neighbourhood.
    /// </para>
    ///
    /// <para>
    /// <b>Written through <see cref="AtomicFileCommit"/>.</b> The same stage / backup / commit
    /// contract the house save, the names sidecar and the layout sidecar use. A crash mid-write
    /// leaves the previous raster readable and the new one staged, never a truncated map where a
    /// good one used to be - which for a three-megabyte file is a long enough window to matter.
    /// </para>
    ///
    /// <para>
    /// <b>The file.</b> Little-endian throughout, written through <see cref="BinaryWriter"/> and
    /// read back through <see cref="BinaryReader"/>:
    /// </para>
    /// <code>
    /// offset  size  meaning
    ///      0     4  magic "SNRS"                       - not a raster at all if it differs
    ///      4     4  formatVersion (1)                  - a build that does not know this refuses it
    ///      8     4  slot
    ///     12     4  seed
    ///     16     4  generatorVersion
    ///     20     4  mapSize
    ///     24     4  width   \
    ///     28     4  height  /  all three equal; a mismatch is a damaged file
    ///     32     8  layoutHash - FNV-1a 64 over the generator's own input
    ///     40     8  payloadBytes - 12 per tile, and the file's length must agree
    ///     48   ...  payload: terrainType, terrainTypeColor, elevation,
    ///                 road, forestDensity, forestType - RGBA, one byte each
    ///  end     8  FNV-1a 64 over every byte before it
    /// </code>
    /// <para>
    /// Twelve bytes per tile is deliberate: four of the six arrays are one byte each and the two
    /// colour arrays are four, so the whole thing is
    /// <c>Width * Height * 12</c> - 3 145 728 bytes for a real 512x512 map, and the size is
    /// computable rather than declared, which is what makes a truncated file detectable before a
    /// single array is allocated. The trailing checksum is the other half of that: a file that is
    /// the right length but has been scribbled on is refused rather than half-believed.
    /// </para>
    /// </summary>
    public static class NeighbourhoodRasterCache
    {
        /// <summary>
        /// The format this build writes. Bumped when the <em>shape</em> of the file changes, for
        /// any reason at all - a new array, a different colour order, a different hash. A file
        /// stamped with anything else is ignored and the map is generated again.
        /// </summary>
        /// <remarks>
        /// <b>Ninety-six, for the connected-mechanics revert.</b> The rework put the real connectors
        /// in <see cref="NeighbourhoodMap.RoadData"/>, so a raster written before it holds measured
        /// streets where the player's map shows painted ones and the file is a plausible-looking map
        /// of the wrong neighbourhood. And <see cref="LayoutHash"/> grew the mask's own fingerprint,
        /// which changes what the stamp means rather than what the bytes are - and a stamp whose
        /// meaning has changed cannot be checked by a reader carrying the old meaning of it, so it
        /// moves too. Both refusals cost one regeneration; neither is worth trying to avoid.
        /// </remarks>
        public const int FormatVersion = 96;

        /// <summary>Extension of a raster cache file.</summary>
        public const string FileExtension = ".raster";

        /// <summary>Extension of the folder a <see cref="SaveDebugPng"/> dump writes into.</summary>
        public const string DebugFolderExtension = ".png";

        /// <summary>The first four bytes of every file this build writes.</summary>
        public static readonly byte[] Magic = { (byte)'S', (byte)'N', (byte)'R', (byte)'S' };

        // ---- header layout ----------------------------------------------------------------------
        //
        //Public because they are part of the format rather than of this implementation: a file is
        //only readable against the offsets it was written at, so a test that wants to forge one -
        //to prove a build that does not understand a version refuses it, or that a header which
        //disagrees with itself is caught - has to be able to name them. The offsets are fixed by
        //FormatVersion 1 and may not be reused by a later version.

        /// <summary>Where the magic starts.</summary>
        public const int MagicOffset = 0;

        /// <summary>How long the magic is.</summary>
        public const int MagicLength = 4;

        /// <summary>Where <see cref="FormatVersion"/> is.</summary>
        public const int VersionOffset = 4;

        /// <summary>Where the neighbourhood slot id is.</summary>
        public const int SlotOffset = 8;

        /// <summary>Where the seed is.</summary>
        public const int SeedOffset = 12;

        /// <summary>Where the generator version is.</summary>
        public const int GeneratorOffset = 16;

        /// <summary>Where the map side length is.</summary>
        public const int MapSizeOffset = 20;

        /// <summary>Where the map width is.</summary>
        public const int WidthOffset = 24;

        /// <summary>Where the map height is.</summary>
        public const int HeightOffset = 28;

        /// <summary>Where the layout hash is.</summary>
        public const int LayoutHashOffset = 32;

        /// <summary>Where the payload byte count is.</summary>
        public const int PayloadOffset = 40;

        /// <summary>Bytes before the payload: the magic and the nine stamped fields.</summary>
        public const int HeaderSize = 48;

        /// <summary>Bytes after the payload: the trailing checksum.</summary>
        public const int ChecksumSize = 8;

        /// <summary>
        /// Bytes of payload per tile: one each for terrain, elevation, road and forest density, and
        /// four each for the two colour arrays.
        /// </summary>
        public const int BytesPerTile = 12;

        // ---- where the file goes -----------------------------------------------------------------

        /// <summary>
        /// The raster path for a slot, <c>&lt;userDir&gt;Neighbourhoods/generated/&lt;slot&gt;.raster</c>,
        /// or <c>null</c> for an unknown slot or a missing user directory.
        /// </summary>
        /// <remarks>
        /// The same folder and the same slot-id convention as
        /// <see cref="NeighbourhoodLayoutStore.PathFor"/>, so the two files for a neighbourhood
        /// sit next to each other and a future change to one path is obviously a change to both.
        /// </remarks>
        /// <param name="userDir">The user directory the sidecars live in.</param>
        /// <param name="slot">The neighbourhood slot id, 0..8.</param>
        public static string PathFor(string userDir, int slot)
        {
            if (!NeighbourhoodLayoutStore.IsKnownSlot(slot)) return null;
            var folder = NeighbourhoodLayoutStore.GeneratedFolder(userDir);
            if (folder == null) return null;
            return Path.Combine(folder, slot.ToString(CultureInfo.InvariantCulture) + FileExtension);
        }

        /// <summary>
        /// The folder a <see cref="SaveDebugPng"/> dump for a slot goes in,
        /// <c>&lt;userDir&gt;Neighbourhoods/generated/&lt;slot&gt;.png</c>, or <c>null</c>.
        /// </summary>
        /// <param name="userDir">The user directory the sidecars live in.</param>
        /// <param name="slot">The neighbourhood slot id, 0..8.</param>
        public static string DebugFolderFor(string userDir, int slot)
        {
            if (!NeighbourhoodLayoutStore.IsKnownSlot(slot)) return null;
            var folder = NeighbourhoodLayoutStore.GeneratedFolder(userDir);
            if (folder == null) return null;
            return Path.Combine(folder, slot.ToString(CultureInfo.InvariantCulture)
                + DebugFolderExtension);
        }

        // ---- writing ------------------------------------------------------------------------------

        /// <summary>
        /// Write a generated map beside its slot's layout sidecar, stamped with everything a
        /// later session has to check before it may reuse it.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the file was committed. <c>false</c> - with the reason logged - for a
        /// missing layout or map, an unknown slot, a map that is not the layout's size, a path
        /// that resolves inside a TS1 neighbourhood folder, or any IO failure.
        /// </returns>
        /// <remarks>
        /// Best effort by design, and for the same reason the layout sidecar's write is: a cache
        /// file is not the world. A neighbourhood that cannot be written to disk still generates
        /// identically every time, so a failure here is a log line and nothing more - never a
        /// fallback to the 2D map and never an exception.
        /// </remarks>
        /// <param name="userDir">The user directory the sidecars live in; null means "no cache".</param>
        /// <param name="layout">The layout the map was generated from.</param>
        /// <param name="map">The generated map.</param>
        public static bool Save(string userDir, NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            if (layout == null)
            {
                SimitoneLog.Warn("NeighbourhoodRasterCache: nothing to write; there is no layout.");
                return false;
            }
            if (map == null)
            {
                SimitoneLog.Warn("NeighbourhoodRasterCache: nothing to write; there is no map.");
                return false;
            }

            var path = PathFor(userDir, layout.Slot);
            if (path == null)
            {
                SimitoneLog.Warn("NeighbourhoodRasterCache: slot " + layout.Slot
                    + " is not one of the " + NeighbourhoodManager.SlotCount + " neighbourhood slots.");
                return false;
            }

            //A raster stamped with a size the layout does not have would be refused by every later
            //read, so it is not written at all. Reaching this means the caller generated a map for
            //one layout and is storing it against another.
            if (map.Width != layout.MapSize || map.Height != layout.MapSize)
            {
                SimitoneLog.Warn("NeighbourhoodRasterCache: not writing '" + path + "'; the map is "
                    + map.Width + "x" + map.Height + " and the layout is " + layout.MapSize + ".");
                return false;
            }

            //the last line of defence, and the only one that survives someone changing the path
            //constants above
            if (NeighbourhoodLayoutStore.IsInsideUserData(userDir, path))
            {
                SimitoneLog.Error("NeighbourhoodRasterCache: refusing to write '" + path
                    + "'; it resolves inside a TS1 neighbourhood folder.");
                return false;
            }

            try
            {
                AtomicFileCommit.Write(path, Serialize(layout, map));
                return true;
            }
            catch (Exception e)
            {
                SimitoneLog.Error("NeighbourhoodRasterCache: could not write '" + path + "'.", e);
                return false;
            }
        }

        /// <summary>Delete a slot's raster, so the next refresh generates again.</summary>
        /// <returns><c>true</c> when the file is gone, including when it never existed.</returns>
        /// <param name="userDir">The user directory the sidecars live in.</param>
        /// <param name="slot">The neighbourhood slot id.</param>
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
                SimitoneLog.Warn("NeighbourhoodRasterCache: could not delete '" + path + "'.", e);
                return false;
            }
        }

        /// <summary>
        /// Write the five PNGs <c>CityMapData.Save</c> would, for looking at a generated map
        /// without a graphics device. Not called by the refresh: encoding five 512x512 images is
        /// not free, and nobody wants it on every generation. A tool, or a failing test, asks.
        /// </summary>
        /// <returns>Whether the images were written. Never throws.</returns>
        /// <param name="userDir">The user directory the sidecars live in.</param>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="map">The map to dump.</param>
        public static bool SaveDebugPng(string userDir, int slot, NeighbourhoodMap map)
        {
            if (map == null) return false;
            var folder = DebugFolderFor(userDir, slot);
            if (folder == null) return false;
            if (NeighbourhoodLayoutStore.IsInsideUserData(userDir, folder))
            {
                SimitoneLog.Error("NeighbourhoodRasterCache: refusing to write the debug images to '"
                    + folder + "'; it resolves inside a TS1 neighbourhood folder.");
                return false;
            }
            try
            {
                map.SavePng(folder);
                return true;
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodRasterCache: could not write the debug images to '"
                    + folder + "'.", e);
                return false;
            }
        }

        // ---- serialisation ------------------------------------------------------------------------

        /// <summary>
        /// Render a generated map as the raster cache file's bytes. Deterministic: the same map
        /// from the same layout produces the same file, byte for byte, which is what lets a test
        /// compare a round-trip rather than a set of values.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">One of the map's six arrays is not
        /// <c>Width * Height</c> long.</exception>
        /// <param name="layout">The layout the map was generated from; supplies the stamp.</param>
        /// <param name="map">The generated map.</param>
        public static byte[] Serialize(NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (map.Width <= 0 || map.Height <= 0)
            {
                throw new ArgumentException("A map with no tiles cannot be written.", nameof(map));
            }

            var count = map.Width * map.Height;
            RequireLength(map.TerrainType, count, nameof(NeighbourhoodMap.TerrainType));
            RequireLength(map.TerrainTypeColorData, count, nameof(NeighbourhoodMap.TerrainTypeColorData));
            RequireLength(map.ElevationData, count, nameof(NeighbourhoodMap.ElevationData));
            RequireLength(map.RoadData, count, nameof(NeighbourhoodMap.RoadData));
            RequireLength(map.ForestDensityData, count, nameof(NeighbourhoodMap.ForestDensityData));
            RequireLength(map.ForestTypeData, count, nameof(NeighbourhoodMap.ForestTypeData));

            //long arithmetic throughout, and the one place the result is narrowed to int is guarded
            //by the very check that makes it safe
            var payload = (long)count * BytesPerTile;
            var total = HeaderSize + payload + ChecksumSize;
            if (total > int.MaxValue)
            {
                throw new ArgumentException("A " + map.Width + "x" + map.Height + " map is too large to"
                    + " write as a raster cache file.", nameof(map));
            }

            var bytes = new byte[(int)total];
            using (var stream = new MemoryStream(bytes, writable: true))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(layout.Slot);
                writer.Write(layout.Seed);
                writer.Write(layout.GeneratorVersion);
                writer.Write(map.Width);
                writer.Write(map.Width);
                writer.Write(map.Height);
                writer.Write(unchecked((long)LayoutHash(layout)));
                writer.Write(payload);

                //the order is the contract, and it is the order NeighbourhoodMap declares its
                //arrays in: the four byte arrays, then the two colour ones
                writer.Write(map.TerrainType);
                WriteColors(writer, map.TerrainTypeColorData);
                writer.Write(map.ElevationData);
                writer.Write(map.RoadData);
                writer.Write(map.ForestDensityData);
                WriteColors(writer, map.ForestTypeData);

                writer.Flush();
            }

            var payloadEnd = HeaderSize + (int)payload;
            WriteUInt64(bytes, payloadEnd, Fnv1a64(bytes, 0, payloadEnd));
            return bytes;
        }

        private static void RequireLength(Array array, int count, string name)
        {
            if (array == null || array.Length != count)
            {
                throw new ArgumentException("The map's " + name + " is "
                    + (array == null ? "missing" : array.Length.ToString(CultureInfo.InvariantCulture)
                        + " tiles")
                    + " but the map is " + count + " tiles.", "map");
            }
        }

        private static void WriteColors(BinaryWriter writer, Color[] colors)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                var c = colors[i];
                writer.Write(c.R);
                writer.Write(c.G);
                writer.Write(c.B);
                writer.Write(c.A);
            }
        }

        /// <summary>
        /// Read RGBA quadruples into an existing array. The map's colour arrays are get-only, so
        /// the whole payload is read over the arrays <see cref="NeighbourhoodMap.Create"/> already
        /// allocated rather than into fresh ones and assigned - which also means a partial read
        /// would leave a partly-filled map that nothing can get at, because the method only returns
        /// after the last byte is in.
        /// </summary>
        private static void ReadColors(BinaryReader reader, Color[] colors)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = new Color(reader.ReadByte(), reader.ReadByte(),
                    reader.ReadByte(), reader.ReadByte());
            }
        }

        // ---- reading ------------------------------------------------------------------------------

        /// <summary>
        /// Parse a raster cache file and hand back the map in it, if and only if it is stamped for
        /// <paramref name="layout"/>.
        /// </summary>
        /// <param name="bytes">The file's bytes.</param>
        /// <param name="layout">
        /// The layout the map is wanted for, or <c>null</c> - which is itself a refusal, because a
        /// raster with nothing to check its stamp against is a raster nobody may use.
        /// </param>
        /// <param name="map">The map, when it could be read and is one this build understands.</param>
        /// <param name="reason">Why not, when it could not. Suitable for the log.</param>
        /// <returns><c>true</c> when <paramref name="map"/> is set.</returns>
        /// <remarks>
        /// <para>
        /// The checks run cheapest and most damning first, and the stamp is verified
        /// <em>before</em> a single array is allocated: an unanswered question about which world
        /// this is must not cost three megabytes of heap to reach.
        /// </para>
        /// <para>
        /// Every failure is a refusal with a reason, never an exception. A cache file is the one
        /// place in this pipeline where a bad byte must be indistinguishable from no file at all,
        /// because both mean exactly the same thing - generate again.
        /// </para>
        /// </remarks>
        public static bool TryDeserialize(byte[] bytes, NeighbourhoodLayout layout,
            out NeighbourhoodMap map, out string reason)
        {
            map = null;
            reason = null;

            if (bytes == null || bytes.Length < HeaderSize + ChecksumSize)
            {
                reason = "it is empty, or truncated to " + (bytes == null ? 0 : bytes.Length)
                    + " bytes";
                return false;
            }

            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                {
                    if (!StartsWithMagic(bytes)) return Refuse(out map, out reason,
                        "it is not a generated-map raster");

                    //the version comes before anything is believed, because a build that does not
                    //know the shape must not go on to interpret the shape
                    var version = ReadInt32(bytes, VersionOffset);
                    if (version != FormatVersion)
                    {
                        return Refuse(out map, out reason, "its format version is " + version
                            + ", which this build does not understand");
                    }

                    var slot = ReadInt32(bytes, SlotOffset);
                    var seed = ReadInt32(bytes, SeedOffset);
                    var generator = ReadInt32(bytes, GeneratorOffset);
                    var mapSize = ReadInt32(bytes, MapSizeOffset);
                    var width = ReadInt32(bytes, WidthOffset);
                    var height = ReadInt32(bytes, HeightOffset);
                    var stampedHash = ReadInt64(bytes, LayoutHashOffset);
                    var payload = ReadInt64(bytes, PayloadOffset);

                    //structural, so a damaged file is refused as damaged rather than as stale -
                    //and, more importantly, so nothing below can read past the end of the buffer
                    if (mapSize <= 0 || width <= 0 || height <= 0)
                    {
                        return Refuse(out map, out reason,
                            "it describes a " + width + "x" + height + " map of " + mapSize + " tiles");
                    }
                    if (width != height || width != mapSize)
                    {
                        return Refuse(out map, out reason, "it describes a " + width + "x" + height
                            + " map but stamps its size as " + mapSize);
                    }

                    long count = (long)width * (long)height;
                    long expectedPayload = count * BytesPerTile;
                    if (payload != expectedPayload)
                    {
                        return Refuse(out map, out reason, "it says it holds " + payload
                            + " bytes of map but a " + width + "x" + height + " one is "
                            + expectedPayload);
                    }
                    if (HeaderSize + payload + ChecksumSize != bytes.Length)
                    {
                        return Refuse(out map, out reason, "it is " + bytes.Length
                            + " bytes but its own header describes " + (HeaderSize + payload + ChecksumSize));
                    }

                    //a file that is the right length and the wrong content is the case a length
                    //check cannot see, and the one a half-trusted map would come from
                    var payloadEnd = HeaderSize + (int)payload;
                    if (Fnv1a64(bytes, 0, payloadEnd) != ReadUInt64(bytes, payloadEnd))
                    {
                        return Refuse(out map, out reason, "its contents do not match its checksum");
                    }

                    //the stamp, which is the whole question: is this the map for *this* world?
                    if (layout == null)
                    {
                        return Refuse(out map, out reason,
                            "there is no layout to check it against");
                    }
                    if (slot != layout.Slot)
                    {
                        return Refuse(out map, out reason, "it describes slot " + slot
                            + ", not slot " + layout.Slot);
                    }
                    if (seed != layout.Seed)
                    {
                        return Refuse(out map, out reason, "it was generated with seed " + seed
                            + " and this world is seed " + layout.Seed);
                    }
                    if (generator != layout.GeneratorVersion)
                    {
                        return Refuse(out map, out reason, "it was generated by map generator "
                            + generator + " and this build is " + layout.GeneratorVersion);
                    }
                    if (mapSize != layout.MapSize)
                    {
                        return Refuse(out map, out reason, "it is a " + mapSize
                            + " tile map and this layout is " + layout.MapSize);
                    }
                    var wanted = unchecked((long)LayoutHash(layout));
                    if (stampedHash != wanted)
                    {
                        return Refuse(out map, out reason,
                            "it was generated from a different arrangement of this neighbourhood");
                    }

                    //everything above has already proved the buffer is exactly the right length,
                    //so these reads cannot fail and cannot half-occur
                    stream.Position = HeaderSize;
                    var loaded = NeighbourhoodMap.Create(mapSize);
                    reader.Read(loaded.TerrainType, 0, loaded.TerrainType.Length);
                    ReadColors(reader, loaded.TerrainTypeColorData);
                    reader.Read(loaded.ElevationData, 0, loaded.ElevationData.Length);
                    reader.Read(loaded.RoadData, 0, loaded.RoadData.Length);
                    reader.Read(loaded.ForestDensityData, 0, loaded.ForestDensityData.Length);
                    ReadColors(reader, loaded.ForestTypeData);

                    map = loaded;
                    return true;
                }
            }
            catch (Exception e)
            {
                //unreachable for a buffer that passed the length check, and the reason to keep the
                //try: a cache file must never be the thing that fails a neighbourhood
                return Refuse(out map, out reason, "it could not be read (" + e.Message + ")");
            }
        }

        private static bool Refuse(out NeighbourhoodMap map, out string reason, string why)
        {
            map = null;
            reason = why;
            return false;
        }

        /// <summary>
        /// Read a slot's raster and hand back the map in it, if it is stamped for
        /// <paramref name="layout"/>.
        /// </summary>
        /// <remarks>
        /// The file-level half of the cache, and the same contract as
        /// <see cref="NeighbourhoodLayoutStore.Load"/>: a slot with no file is not a warning, a
        /// file this build will not stand behind is a warning and a <c>null</c>, and neither is an
        /// exception. A <c>null</c> here always means the same thing - generate the map again.
        /// </remarks>
        /// <returns>The map, or <c>null</c> when there is nothing reusable.</returns>
        /// <param name="userDir">The user directory the sidecars live in; null means "no cache".</param>
        /// <param name="slot">The neighbourhood slot id, which is also the file's name.</param>
        /// <param name="layout">The layout the map is wanted for.</param>
        public static NeighbourhoodMap Load(string userDir, int slot, NeighbourhoodLayout layout)
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
                SimitoneLog.Warn("NeighbourhoodRasterCache: could not read '" + path + "'.", e);
                return null;
            }

            NeighbourhoodMap map;
            string reason;
            if (!TryDeserialize(bytes, layout, out map, out reason))
            {
                SimitoneLog.Warn("NeighbourhoodRasterCache: ignoring '" + path + "': " + reason
                    + ", so the map will be generated again.");
                return null;
            }

            return map;
        }

        // ---- the stamp ---------------------------------------------------------------------------

        /// <summary>
        /// A fingerprint of everything the generated map is a function of, other than the seed and
        /// the generator version: the generator's own input, exactly as
        /// <see cref="NeighbourhoodMapAdapter.InputFor"/> projects it.
        ///
        /// <para>
        /// <b>Why the input and not the layout.</b> Because the input is what the raster depends
        /// on. Hashing the whole layout would invalidate the file for a field the generator never
        /// reads - a lot's <c>rotation</c>, the session a save stamped on it, and - the one that
        /// surprises people - a lot's <c>houseHash</c> - and each of those would cost a regeneration
        /// to produce byte-identical tiles. Hashing the input means the stamp moves if and only if
        /// the map would. A bulldozed lot therefore keeps the file <em>if</em> its rectangle stayed
        /// where it was, which is the right answer: the generator would put the same tiles back.
        /// </para>
        ///
        /// <para>
        /// <b>It is no longer the same list of fields it was.</b> A lot's <c>MapX</c>/<c>MapY</c>
        /// were explicitly excluded before, on the grounds that the generator only used them to seed
        /// a packer whose output it then received as <c>WorldX</c>/<c>WorldY</c> - so a bitmap
        /// position could move without changing a tile. That stopped being true in Part 8: the
        /// generator now consults the mask it was handed, and the mask is a function of the 2D map
        /// rather than of the packing, so the same rectangles with a different mask are different
        /// maps. The mask's own fingerprint is therefore hashed, and so is a byte per lot recording
        /// whether one was present - because "no mask" and "a mask with nothing set in it" are the
        /// same array but not the same answer.
        /// </para>
        ///
        /// <para>
        /// <b>The mask's source string is deliberately not hashed.</b> Two runs that produce the
        /// same mask produce the same map, and a reason that differs between them - a cache hit
        /// versus a decode, say - is a difference in how the mask was obtained rather than in what
        /// it says. Hashing it would regenerate a correct raster once per process for no reason.
        /// </para>
        /// </summary>
        /// <param name="layout">The layout to fingerprint, or <c>null</c> for 0.</param>
        public static ulong LayoutHash(NeighbourhoodLayout layout)
        {
            if (layout == null) return 0UL;

            var input = NeighbourhoodMapAdapter.InputFor(layout);
            var hash = FnvOffsetBasis;
            hash = Hash(hash, input.Seed);
            hash = Hash(hash, input.MapSize);
            hash = Hash(hash, input.Lots.Count);
            hash = Hash(hash, input.HasRoadMask);
            hash = HashMask(hash, input.RoadMask);
            for (int i = 0; i < input.Lots.Count; i++)
            {
                var lot = input.Lots[i];
                hash = Hash(hash, lot.Number);
                hash = Hash(hash, lot.WorldX);
                hash = Hash(hash, lot.WorldY);
                hash = Hash(hash, lot.Size);
                hash = Hash(hash, lot.Zone);
                hash = Hash(hash, lot.TerrainType);
                hash = Hash(hash, lot.BaseHeight);
                hash = Hash(hash, lot.DoorSide);
                hash = Hash(hash, lot.HasHouse);
            }
            return hash;
        }

        /// <summary>
        /// Folds a connector mask into a stamp: one byte per tile, in raster order.
        /// </summary>
        /// <remarks>
        /// Run-length rather than per-tile in practice - a 512x512 mask that was mostly road
        /// alternates a lot - but written as the obvious loop because that is the definition, and
        /// <see cref="FormatVersion"/> is what would move if it were ever worth making faster.
        /// A <c>null</c> mask and an all-false one hash differently by design: see
        /// <see cref="LayoutHash"/>.
        /// </remarks>
        private static ulong HashMask(ulong hash, bool[] mask)
        {
            if (mask == null) return Hash(hash, -1);
            for (int i = 0; i < mask.Length; i++)
            {
                hash = (hash ^ (mask[i] ? (byte)1 : (byte)0)) * FnvPrime;
            }
            return hash;
        }

        // ---- hashing -----------------------------------------------------------------------------

        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static ulong Fnv1a64(byte[] bytes, int offset, int count)
        {
            var hash = FnvOffsetBasis;
            for (int i = 0; i < count; i++)
            {
                hash ^= bytes[offset + i];
                hash *= FnvPrime;
            }
            return hash;
        }

        private static ulong Hash(ulong hash, int value)
        {
            unchecked
            {
                hash = (hash ^ (byte)value) * FnvPrime;
                hash = (hash ^ (byte)(value >> 8)) * FnvPrime;
                hash = (hash ^ (byte)(value >> 16)) * FnvPrime;
                hash = (hash ^ (byte)(value >> 24)) * FnvPrime;
                return hash;
            }
        }

        private static ulong Hash(ulong hash, long value)
        {
            unchecked
            {
                hash = (hash ^ (ulong)value) * FnvPrime;
                return hash;
            }
        }

        private static ulong Hash(ulong hash, bool value)
        {
            return Hash(hash, value ? 1 : 0);
        }

        private static bool StartsWithMagic(byte[] bytes)
        {
            for (int i = 0; i < MagicLength; i++)
            {
                if (bytes[MagicOffset + i] != Magic[i]) return false;
            }
            return true;
        }

        private static int ReadInt32(byte[] bytes, int offset)
        {
            return bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
        }

        private static long ReadInt64(byte[] bytes, int offset)
        {
            return (uint)ReadInt32(bytes, offset) | ((long)ReadInt32(bytes, offset + 4) << 32);
        }

        private static ulong ReadUInt64(byte[] bytes, int offset)
        {
            return (ulong)ReadInt64(bytes, offset);
        }

        private static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++)
                {
                    bytes[offset + i] = (byte)(value >> (8 * i));
                }
            }
        }
    }
}
