/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using FSO.Content.Model;
using FSO.Content.TS1;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The one place that reads a TS1 neighbourhood: the lot-location STR, each lot's
    /// <c>HouseNN.iff</c>, the zoning dictionary and the family dictionary.
    ///
    /// <para>
    /// §15's <see cref="ILotContentSource"/> implemented over
    /// <see cref="TS1NeighborhoodProvider"/>, which is the impure half of
    /// <see cref="NeighbourhoodLayoutBuilder"/>. Everything it returns is copied into a
    /// <see cref="NeighbourhoodLotRead"/> and then the provider can be forgotten, so nothing
    /// downstream of the builder depends on a live content manager - which is what lets the
    /// packing be tested at all.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing here writes.</b> The provider is opened for reading, house files are opened
    /// for reading, and a house that will not open is a lot with no house rather than an
    /// exception. §11 asks for exactly this: a neighbourhood with a broken <c>HouseNN.iff</c>
    /// still opens, the bad lot's facade is skipped, and the rest of the map is generated.
    /// </para>
    /// </summary>
    public sealed class TS1LotContentSource : ILotContentSource
    {
        private readonly TS1NeighborhoodProvider _provider;
        private readonly int _mode;

        /// <summary>House files already read this session, keyed by lot number.</summary>
        private readonly Dictionary<short, NeighbourhoodLotRead> _cache = new Dictionary<short, NeighbourhoodLotRead>();

        /// <summary>The lot-location table for <paramref name="mode"/>, read once.</summary>
        private IReadOnlyList<string> _lines;

        /// <summary>
        /// Lot number to its <c>LotLocations.iff</c> x/y, parsed once from the same table
        /// <see cref="LotLocationLines"/> serves. Used to stamp <see cref="NeighbourhoodLotRead.MapX"/>
        /// and <see cref="NeighbourhoodLotRead.MapY"/> on every record this source hands out.
        /// </summary>
        private Dictionary<int, int[]> _positions;

        private ITextureRef _backdrop;
        private bool _backdropTried;
        private TexBitmap _bitmap;
        private bool _bitmapTried;

        /// <summary>
        /// Read a neighbourhood mode out of an already-open provider.
        /// </summary>
        /// <param name="provider">The neighbourhood provider. Required.</param>
        /// <param name="mode">
        /// The neighbourhood mode: the 1-based STR id in <c>LotLocations.iff</c>, which is what
        /// <c>UINeighborhoodSelectionPanel.PopulateScreen</c> passes to <c>STR.Get</c>.
        /// </param>
        public TS1LotContentSource(TS1NeighborhoodProvider provider, int mode)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _mode = mode;
        }

        /// <summary>
        /// The neighbourhood mode this source reads: the 1-based STR id in
        /// <c>LotLocations.iff</c>. Needed by the connector inference, which is keyed per mode and
        /// which this is the only object in the read pipeline that knows.
        /// </summary>
        public int Mode
        {
            get { return _mode; }
        }

        /// <summary>
        /// The mode's 800x600 backdrop bitmap as a texture reference, or <c>null</c> when this
        /// install has no such file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked for once and remembered, including the answer "there is none", because a missing
        /// file is a normal thing for an incomplete install and re-resolving a path that does not
        /// exist on every generation is a cost with no return.
        /// </para>
        /// <para>
        /// <b>Prefer <see cref="BackdropBitmap"/>.</b> This is the reference form
        /// <see cref="NeighbourhoodRoadInference.Build"/> takes, and it exists because that
        /// signature is frozen and because a caller that already has an <see cref="ITextureRef"/> -
        /// a provider lookup, say - can hand it straight over. It does <em>not</em> decode: reading
        /// it goes through <c>ITextureRef.GetImage()</c>, which in this build
        /// (<c>AbstractTextureRef.ImageFetchWithDevice</c>) decodes the file and then uploads it to a
        /// <c>GraphicsDevice</c> to read it back, so it is not usable from a generation worker.
        /// <see cref="BackdropBitmap"/> is the headless route and the one the builder takes.
        /// </para>
        /// </remarks>
        public ITextureRef BackdropTexture
        {
            get
            {
                if (_backdropTried) return _backdrop;
                _backdropTried = true;
                var path = BackdropPath();
                if (path != null)
                {
                    try
                    {
                        _backdrop = new FileTextureRef(path);
                    }
                    catch (Exception e)
                    {
                        SimitoneLog.Warn("NeighbourhoodLayoutBuilder: '" + path + "' could not be named"
                            + " as a texture reference.", e);
                        _backdrop = null;
                    }
                }
                return _backdrop;
            }
        }

        /// <summary>
        /// The mode's backdrop bitmap decoded headlessly, or <c>null</c> when it is not there or
        /// will not decode. This is what <see cref="NeighbourhoodRoadInference"/> is given.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Decoded through <see cref="BitmapDecoder"/>, Simitone's own ImageSharp reader, which is
        /// what the game already installs as FreeSO's bitmap hook - so an 8-bit indexed BMP is read
        /// by the same code the 2D panel's texture goes through, on a worker thread, with no
        /// graphics device anywhere. That last part is the reason this exists rather than
        /// <c>ITextureRef.GetImage()</c>: generation runs off the game thread and
        /// <c>GetImage()</c> needs a <c>GraphicsDevice</c>.
        /// </para>
        /// <para>
        /// Decoded once and remembered for the session, per mode. The decode is the expensive part
        /// of <see cref="NeighbourhoodRoadInference"/> and it depends on nothing but the file.
        /// </para>
        /// </remarks>
        public TexBitmap BackdropBitmap
        {
            get
            {
                if (_bitmapTried) return _bitmap;
                _bitmapTried = true;

                var path = BackdropPath();
                if (path == null) return null;

                try
                {
                    var decoded = BitmapDecoder.Read(File.OpenRead(path));
                    if (decoded == null)
                    {
                        SimitoneLog.Warn("NeighbourhoodLayoutBuilder: the backdrop for mode " + _mode
                            + ", '" + path + "', did not decode; the connectors will be inferred from"
                            + " the lot lattice instead.");
                        return null;
                    }

                    //TexBitmap is documented as BGRA; BitmapDecoder returns RGBA, and the road
                    //classifier is symmetric in the two ends (it only ever asks for max, min and a
                    //luma), so the buffer is handed over as-is rather than swizzled. That is stated
                    //here because it is the one place a reader would otherwise assume the channels
                    //have been put in order.
                    _bitmap = new TexBitmap
                    {
                        Data = decoded.Item1,
                        Width = decoded.Item2,
                        Height = decoded.Item3,
                        PixelSize = 4,
                    };
                }
                catch (Exception e)
                {
                    SimitoneLog.Warn("NeighbourhoodLayoutBuilder: the backdrop for mode " + _mode
                        + ", '" + path + "', could not be read; the connectors will be inferred from"
                        + " the lot lattice instead.", e);
                    _bitmap = null;
                }

                return _bitmap;
            }
        }

        /// <summary>
        /// Where this mode's backdrop bitmap is, or <c>null</c> when it cannot be worked out.
        /// </summary>
        /// <remarks>
        /// The 2D panel's own table supplies the file name; the neighbourhood's own
        /// <c>UserData</c> folder supplies the directory, because that is where a TS1
        /// neighbourhood keeps its picture - <c>UserData\Nbhd\NScreen.BMP</c> and its five
        /// expansion siblings. The two are composed here rather than in either of them because
        /// neither owns both halves, and this is the only place that needs the whole path.
        /// </remarks>
        private string BackdropPath()
        {
            var file = Simitone.Client.UI.Panels.UINeighborhoodSelectionPanel.BitmapOf((ushort)_mode);
            if (string.IsNullOrEmpty(file)) return null;
            var root = _provider?.UserPath;
            if (string.IsNullOrEmpty(root)) return null;
            try
            {
                var path = Path.Combine(root, file.Replace('\\', Path.DirectorySeparatorChar));
                return File.Exists(path) ? path : null;
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodLayoutBuilder: the backdrop path for mode " + _mode
                    + " could not be built.", e);
                return null;
            }
        }

        public IReadOnlyList<string> LotLocationLines(int mode)
        {
            //the source is built for one mode and that is what it caches. A caller asking for
            //another mode gets a fresh read of that mode's table rather than the wrong table,
            //which is the failure this guard exists to prevent.
            if (mode == _mode)
            {
                if (_lines == null) _lines = ReadLocationLines(mode);
                return _lines;
            }
            return ReadLocationLines(mode);
        }

        /// <summary>
        /// The parsed <c>number,x,y</c> table for this source's own mode, keyed by lot number, first
        /// entry winning.
        /// </summary>
        /// <remarks>
        /// Parsed with <see cref="LotLocationParser"/> rather than with a second hand-rolled parse,
        /// so the skip rules - lot 99, a short entry, a malformed field - are one rule in the tree
        /// rather than two that can disagree. A lot the parser rejects is simply absent here, and
        /// <see cref="ReadLot"/> leaves its position at zero, which is the same answer as "this
        /// lot is not on the map".
        /// </remarks>
        private Dictionary<int, int[]> Positions()
        {
            if (_positions != null) return _positions;

            _positions = new Dictionary<int, int[]>();
            var lines = LotLocationLines(_mode);
            if (lines == null) return _positions;

            for (int i = 0; i < lines.Count; i++)
            {
                int number, x, y;
                if (!LotLocationParser.TryParseLotLocation(lines[i], out number, out x, out y)) continue;
                if (_positions.ContainsKey(number)) continue;
                _positions.Add(number, new[] { x, y });
            }
            return _positions;
        }

        private IReadOnlyList<string> ReadLocationLines(int mode)
        {
            var result = new List<string>();
            try
            {
                var locations = _provider.LotLocations?.Get<STR>((ushort)mode);
                if (locations == null) return result;
                for (int i = 0; i < locations.Length; i++)
                {
                    result.Add(locations.GetString(i));
                }
            }
            catch (Exception e)
            {
                //a neighbourhood with an unreadable LotLocations.iff has no selectable lots in
                //the generated world; the 2D panel has its own handling and is not this's job
                SimitoneLog.Warn("NeighbourhoodLayoutBuilder: could not read the lot locations for mode "
                    + mode + " at '" + _provider.UserPath + "'.", e);
            }
            return result;
        }

        public bool TryReadLot(int number, out NeighbourhoodLotRead lot)
        {
            lot = null;
            if (number < 0 || number > short.MaxValue) return false;

            var id = (short)number;
            NeighbourhoodLotRead cached;
            if (_cache.TryGetValue(id, out cached))
            {
                lot = cached;
                return true;
            }

            lot = ReadLot(id);
            _cache[id] = lot;
            return true;
        }

        private NeighbourhoodLotRead ReadLot(short id)
        {
            var read = new NeighbourhoodLotRead
            {
                Number = id,
                //an empty plot still has to be surrounded by the right ground, so the terrain is
                //resolved whether or not there is a house to ask it from
                TerrainType = NeighbourhoodLayoutBuilder.TerrainTypeFor(id),
                Zone = ReadZone(id),
                Occupied = IsOccupied(id)
            };

            //The lot's real position on the 2D map. This used to be left at (0, 0) - the
            //"load-bearing bug" of Part 8 §8.2 - because nothing downstream could make a use of it:
            //the packer seeded its arrangement from the table it had already read, so it had no
            //reason to ask the source. Now that a lot is placed at exactly this position, the
            //source has to answer it, and it answers from the same table
            //ReadLots parses, so there is one reading of LotLocations.iff in this pipeline.
            //
            //First entry wins, matching ReadLots' duplicate rule, so a table that names a lot twice
            //gives both the source and the builder the same answer.
            int[] position;
            if (Positions().TryGetValue(id, out position))
            {
                read.MapX = position[0];
                read.MapY = position[1];
            }

            var path = HousePath(id);
            if (path != null)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        read.HouseHash = NeighbourhoodLayoutBuilder.HouseHashFor(info.Length, info.LastWriteTimeUtc);
                        read.HasHouse = true;
                    }
                }
                catch (Exception e)
                {
                    SimitoneLog.Warn("NeighbourhoodLayoutBuilder: could not stat '" + path + "'.", e);
                }
            }

            if (!read.HasHouse) return read;

            try
            {
                var iff = _provider.GetHouse(id);
                if (iff == null) return read;

                var simi = iff.Get<SIMI>(1);
                if (simi != null && simi.GlobalData != null && simi.GlobalData.Length > 23)
                {
                    read.Size = simi.GlobalData[23];
                }

                var hous = iff.Get<HOUS>(0);
                if (hous != null)
                {
                    //CameraDir is a short; only its low two bits are a facing, and masking here
                    //keeps the sidecar's 0..3 promise true whatever the file claims
                    read.Rotation = hous.CameraDir & 3;
                }

                read.BaseHeight = NeighbourhoodLayoutBuilder.MedianBaseHeight(iff.Get<ARRY>(0)?.TransposeData);
                read.HasGrassVariation = NeighbourhoodLayoutBuilder.HasGrassVariation(iff.Get<ARRY>(6)?.TransposeData);
            }
            catch (Exception e)
            {
                //§11's regression: a broken HouseNN.iff costs that lot its facade, not the
                //neighbourhood. Report it as an empty plot so the rest of the map is unaffected.
                SimitoneLog.Warn("NeighbourhoodLayoutBuilder: house " + id + " at '" + path
                    + "' could not be read; it will be treated as an empty plot.", e);
                read.HasHouse = false;
                read.HouseHash = 0u;
                read.Size = 0;
            }

            return read;
        }

        private short ReadZone(short id)
        {
            try
            {
                short zone;
                if (_provider.ZoningDictionary != null && _provider.ZoningDictionary.TryGetValue(id, out zone))
                {
                    return zone;
                }
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodLayoutBuilder: could not read the zone of lot " + id + ".", e);
            }
            //the panel's own fallback for a lot the zoning chunk says nothing about
            return LotZoning.DefaultZoneForLot(id);
        }

        private bool IsOccupied(short id)
        {
            try
            {
                return _provider.FamilyForHouse != null && _provider.FamilyForHouse.ContainsKey(id);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodLayoutBuilder: could not read occupancy for lot " + id + ".", e);
                return false;
            }
        }

        private string HousePath(short id)
        {
            try
            {
                return _provider.GetHousePath(id);
            }
            catch (Exception)
            {
                //no UserPath, or a provider that will not name a file: no house, no hash
                return null;
            }
        }
    }
}
