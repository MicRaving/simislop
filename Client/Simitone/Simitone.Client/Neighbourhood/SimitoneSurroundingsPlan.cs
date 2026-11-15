/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using FSO.SimAntics.Marshals.Hollow;
using Microsoft.Xna.Framework;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Where one lot's own <c>ARRY(0)</c> height plane comes from, if it is anywhere.
    /// </summary>
    /// <remarks>
    /// A delegate rather than an interface because there is exactly one production implementation
    /// (a house file, through <see cref="NeighbourHollowLoader"/>) and exactly one reason to want a
    /// different one: a test that has a known plane and no Sims install. A test that cannot hand
    /// the plan a plane cannot say that a plane grades anything, which is the one property this
    /// whole seam exists for.
    /// </remarks>
    /// <param name="lot">The lot whose plane is wanted.</param>
    /// <param name="arry">The lot's own plane, or <see cref="SimitoneArryAltitude.None"/>.</param>
    /// <param name="reason">Why there is none, when there is not one.</param>
    public delegate bool SimitoneArryPlaneReader(
        NeighbourhoodLot lot, out SimitoneArryAltitude arry, out string reason);

    /// <summary>
    /// Where <see cref="SimitoneSurroundingsPlan"/> gets each neighbour's own <c>ARRY(0)</c> plane,
    /// and what it did with the ones it could not get.
    ///
    /// <para><b>Why the plan asks for this at all.</b> A generated neighbourhood pins every tile of
    /// a lot's footprint to that lot's median altitude
    /// (<see cref="TerrainSeamSolver"/>'s Dirichlet stage), so the map's answer for a lot's own
    /// ground is <em>flat</em> - a correct boundary condition and the wrong answer to "what shape
    /// is this lot". The player's own lot is drawn by the lot view from its own <c>ARRY(0)</c>, so
    /// until this landed the street was a graded lot in a field of planes, which is the owner's
    /// "height maps of surrounding lots are incorrect". The real plane is in the lot's own
    /// <c>HouseNN.iff</c> and nowhere else, so a plan that wants it has to ask for it.</para>
    ///
    /// <para><b>It is threaded, never global.</b> It is a parameter of
    /// <see cref="SimitoneSurroundingsPlan.For"/> and
    /// <see cref="SimitoneSurroundingsPlan.ForQuery"/>, handed to
    /// <see cref="SimitoneSurroundingsPlan.For"/>'s per-lot step, and it ends up on each
    /// <see cref="SimitoneSurroundingsNeighbour"/> as
    /// <see cref="SimitoneSurroundingsNeighbour.ArryAltitude"/>. There is no static reader and no
    /// ambient house-file cache: the thing that reads files is a value a caller owns and passes in,
    /// which is what lets a test assert the graded path with no install and what keeps the plan's
    /// inputs visible at the one place the decision is made.</para>
    ///
    /// <para><b>Unavailable is a value, not a failure.</b> A lot with no house file, a lot whose
    /// file cannot be fingerprinted, an install with no neighbourhood content loaded, a truncated
    /// <c>ARRY(0)</c> - each of those is a sentence and
    /// <see cref="SimitoneArryAltitude.None"/>, which every reader of a lot's ground already treats
    /// as "the generated map's flat median for this lot". The neighbourhood keeps working and the
    /// log says which lots are on the plane and why the others are not; see
    /// <see cref="Describe"/>.</para>
    /// </summary>
    public sealed class SimitoneArryPlanes
    {
        private readonly SimitoneArryPlaneReader _reader;

        // The cache is keyed on the lot and validated against the house file's own fingerprint, the
        // same rule `NeighbourHollowLoader` applies to its marshals: a lot whose house changed must
        // not be answered with the plane of the file it had, and a lot with no fingerprint is never
        // cached at all (NeighbourHollowLoader.ArryRefusalFor refuses it before this is reached).
        private readonly Dictionary<short, uint> _hashes = new Dictionary<short, uint>();
        private readonly Dictionary<short, SimitoneArryAltitude> _planes
            = new Dictionary<short, SimitoneArryAltitude>();

        private readonly List<string> _pins = new List<string>();
        private int _asked;
        private int _reads;
        private int _hits;
        private int _graded;

        /// <summary>
        /// Planes from <paramref name="reader"/>, or from each lot's own house file when it is null.
        /// </summary>
        /// <param name="reader">
        /// Where a plane comes from. Null is the shipped answer - a house file, through
        /// <see cref="NeighbourHollowLoader.TryReadArryAltitude"/> - rather than "no planes", so
        /// that a caller that has not heard of this type still gets its neighbours graded. A reader
        /// that throws is a refusal, not an exception: see <see cref="Read"/>.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>Not thread-safe, and deliberately not made so.</b> It counts what it was asked, and a
        /// lock over a counter a single lot-load thread owns would cost more than the counter. One
        /// instance per lot load is the intended shape; a caller that plans from more than one thread
        /// passes one per thread.
        /// </para>
        /// <para>
        /// <b>What one ask costs.</b> The shipped reader opens one <c>HouseNN.iff</c> and reads one
        /// chunk out of it - no <c>World</c>, no <c>VM</c>, no device, no light map - so a plan over
        /// the ordinary radius is a handful of file reads on the lot-load thread, next to the four
        /// whole throwaway lots <c>NeighbourHollowLoader</c> stands up in the same load for contents.
        /// </para>
        /// </remarks>
        public SimitoneArryPlanes(SimitoneArryPlaneReader reader = null)
        {
            _reader = reader ?? HouseFileReader;
        }

        /// <summary>
        /// The shipped reader: each lot's own <c>HouseNN.iff</c>, through
        /// <see cref="NeighbourHollowLoader.TryReadArryAltitude"/>.
        /// </summary>
        public static SimitoneArryPlaneReader HouseFileReader
        {
            get { return ReadHouseFile; }
        }

        /// <summary>A fresh source over the house files. What a plan builds when it is given none.</summary>
        public static SimitoneArryPlanes HouseFiles()
        {
            return new SimitoneArryPlanes();
        }

        /// <summary>
        /// A source that never has a plane, so every neighbour is built on the generated map's flat
        /// median. A mode rather than a null: "the caller asked for the pinned path" and "the
        /// caller has not heard of this" are different intentions and a test wants the first.
        /// </summary>
        /// <remarks>
        /// A fresh instance per call and not a shared one, because the counts on it are about the
        /// lots it was asked about and a singleton would report every lot any caller ever pinned.
        /// </remarks>
        public static SimitoneArryPlanes Pinned()
        {
            return new SimitoneArryPlanes(RefuseEveryLot);
        }

        /// <summary>How many lots were asked about.</summary>
        public int Asked
        {
            get { return _asked; }
        }

        /// <summary>How many of those were answered with a real <c>ARRY(0)</c> plane.</summary>
        public int GradedCount
        {
            get { return _graded; }
        }

        /// <summary>How many of those fell back to the generated map's flat median.</summary>
        public int PinnedCount
        {
            get { return _pins.Count; }
        }

        /// <summary>How many house files this source opened to answer a lot.</summary>
        public int ReadCount
        {
            get { return _reads; }
        }

        /// <summary>How many lots were answered from its own cache rather than from a file.</summary>
        public int CacheHitCount
        {
            get { return _hits; }
        }

        /// <summary>One line per lot that fell back to the pinned median, and why.</summary>
        public IReadOnlyList<string> PinReasons
        {
            get { return _pins; }
        }

        /// <summary>
        /// One lot's own plane, out of the cache where it can be and out of the reader where it
        /// cannot, or <c>false</c> with <see cref="SimitoneArryAltitude.None"/> and the reason.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The cache is asked first and the reader second, and a refusal is recorded rather than
        /// thrown, because this runs once per candidate on the lot-load thread and a lot the reader
        /// cannot answer for must cost that lot and nothing else. The recorded reason is what makes
        /// the fallback visible rather than silent: "the surrounding lots are flat" and "the
        /// surrounding lots are flat because nothing could read their house files" are different
        /// defects and only one of them is a bug.
        /// </para>
        /// <para>
        /// <b>Only a plane is cached.</b> A refusal is a fact about the reader, the install or the
        /// file at this moment, and remembering it would make a lot that has since gained a house
        /// file stay flat for the life of this source - which is precisely the failure the fingerprint
        /// exists to prevent on the other side.
        /// </para>
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="arry">The lot's own plane, or <see cref="SimitoneArryAltitude.None"/>.</param>
        /// <param name="reason">Why there is none, when there is not one.</param>
        public bool TryGet(
            NeighbourhoodLot lot, out SimitoneArryAltitude arry, out string reason)
        {
            arry = SimitoneArryAltitude.None;
            reason = null;
            _asked++;
            if (lot == null)
            {
                reason = "there is no lot to read an ARRY(0) plane out of, so its ground is the"
                    + " generated map's flat median";
                _pins.Add(reason);
                return false;
            }

            //The fingerprint, not the lot number: a lot whose house file changed must be re-read.
            //A lot the layout cannot fingerprint is never cached at all, because there is nothing
            //here that could tell a changed file from an unchanged one.
            uint built;
            if (lot.HouseHash != 0u
                && _hashes.TryGetValue(lot.Number, out built)
                && built == lot.HouseHash
                && _planes.TryGetValue(lot.Number, out arry)
                && arry.IsValid)
            {
                _hits++;
                _graded++;
                return true;
            }

            if (Read(lot, out arry, out reason) && arry.IsValid)
            {
                _reads++;
                if (lot.HouseHash != 0u)
                {
                    _hashes[lot.Number] = lot.HouseHash;
                    _planes[lot.Number] = arry;
                }
                _graded++;
                return true;
            }

            arry = SimitoneArryAltitude.None;
            if (string.IsNullOrEmpty(reason))
            {
                reason = "lot " + lot.Number + ": no ARRY(0) plane could be read, so its ground is"
                    + " the generated map's flat median";
            }
            _pins.Add(reason);
            return false;
        }

        /// <summary>
        /// One line for a log: how many neighbours got their real graded ground and how many are on
        /// the generated map's pinned median, with the reason for each of those.
        /// </summary>
        public string Describe()
        {
            if (Asked == 0) return "no lot asked for an ARRY(0) plane";
            var text = GradedCount + " of " + Asked + " neighbouring lot"
                + (Asked == 1 ? "" : "s") + " graded from their own ARRY(0) plane, "
                + PinnedCount + " on the generated map's pinned median ("
                + ReadCount + " house file" + (ReadCount == 1 ? "" : "s") + " read, "
                + CacheHitCount + " cache hit" + (CacheHitCount == 1 ? "" : "s") + ")";
            for (int i = 0; i < _pins.Count; i++)
            {
                text += "\n  " + _pins[i];
            }
            return text;
        }

        public override string ToString()
        {
            return Describe();
        }

        private static bool ReadHouseFile(
            NeighbourhoodLot lot, out SimitoneArryAltitude arry, out string reason)
        {
            return NeighbourHollowLoader.TryReadArryAltitude(lot, out arry, out reason);
        }

        /// <summary>
        /// The reader, with its own throw turned into a refusal.
        /// </summary>
        /// <remarks>
        /// The shipped reader already never throws, and this exists for a reader a caller supplied -
        /// a plane source is a delegate, so anything can stand behind it, and the plan is built on
        /// the lot-load thread with the whole street waiting behind it. A reader that throws costs
        /// that lot its graded ground and says so, which is the same shape as any other refusal.
        /// </remarks>
        private bool Read(
            NeighbourhoodLot lot, out SimitoneArryAltitude arry, out string reason)
        {
            try
            {
                return _reader(lot, out arry, out reason);
            }
            catch (Exception ex)
            {
                arry = SimitoneArryAltitude.None;
                reason = "lot " + lot.Number + ": reading its ARRY(0) plane threw (" + ex.Message
                    + "), so its ground is the generated map's flat median";
                return false;
            }
        }

        private static bool RefuseEveryLot(
            NeighbourhoodLot lot, out SimitoneArryAltitude arry, out string reason)
        {
            arry = SimitoneArryAltitude.None;
            reason = "lot " + (lot?.Number ?? 0) + ": no ARRY(0) plane source was asked for, so its"
                + " ground is the generated map's flat median";
            return false;
        }
    }

    /// <summary>
    /// One lot that B2 is going to build as a sub-world of the lot the player is standing in, and
    /// everything the bridge needs to build it: which lot, the terrain to build it out of, the
    /// contents to build it with if there are any, where in the world it stands, which way it
    /// faces and how big it is.
    ///
    /// <para><b>It is a value and it is immutable</b> because a plan is a decision that has already
    /// been made: the bridge consumes it and nothing revises it, so there is no way for the terrain
    /// and the position to disagree. The only mutable thing any of it refers to is the layout's own
    /// <see cref="NeighbourhoodLot"/>, which is what every other consumer of a layout reads.</para>
    /// </summary>
    public readonly struct SimitoneSurroundingsNeighbour
    {
        private SimitoneSurroundingsNeighbour(
            NeighbourhoodLot lot, SimitoneNeighbourTerrain terrain, VMHollowMarshal hollow,
            Vector2 subWorldPosition, ushort lotDirection, int subWorldSize,
            short[] subWorldAltitudes, SimitoneArryAltitude arry)
        {
            Lot = lot;
            Terrain = terrain;
            Hollow = hollow;
            SubWorldPosition = subWorldPosition;
            LotDirection = lotDirection;
            SubWorldSize = subWorldSize;
            SubWorldAltitudes = subWorldAltitudes;
            ArryAltitude = arry;
        }

        /// <summary>The lot, as the layout records it. Never null on a planned neighbour.</summary>
        public NeighbourhoodLot Lot { get; }

        /// <summary>
        /// The terrain this lot is built out of, read out of the generated map by
        /// <see cref="SimitoneNeighbourTerrain.TryBuild"/>.
        /// </summary>
        public SimitoneNeighbourTerrain Terrain { get; }

        /// <summary>
        /// The lot's stored contents, or <c>null</c> when it has none - in which case the bridge
        /// builds a blank lot instead, exactly as <c>RestoreSurroundings</c> does for
        /// <c>hollowAdj[..] == null</c>.
        /// </summary>
        /// <remarks>
        /// Read out of a <see cref="LotStore"/> at plan time rather than at build time, so the
        /// decision of <em>which</em> lots are hollow is part of the plan and a test can assert it
        /// without a VM. §5.6 has a lot's contents committed to its own <c>HouseNN.iff</c> before
        /// the store is asked to hold them, so this cannot be looking at a half-saved lot.
        /// <para>
        /// <b>A null here is not necessarily a blank lot.</b> The store only holds a lot somebody
        /// has visited, and in a generated neighbourhood that is none of them, so every neighbour
        /// of a lot the player has never entered arrives here null. The bridge therefore reads the
        /// missing contents out of each neighbour's own house file - see
        /// <see cref="NeighbourHollowLoader"/> and
        /// <see cref="SimitoneSurroundingsNeighbour.WithHollow"/> - and only the lots that cannot
        /// be read that way are built blank.
        /// </para>
        /// </remarks>
        public VMHollowMarshal Hollow { get; }

        /// <summary>
        /// Where the lot stands, in the parent's tile space: the difference between the two
        /// rectangles' minimum corners.
        ///
        /// <para>
        /// This is the number B2 exists to get right. <c>RestoreSurroundings</c> derives its
        /// sub-world offsets from <c>(size - 2)</c> spacing on a uniform 3x3 grid keyed on a packed
        /// <c>vm.TSOState.LotID</c>, which assumes lots are packed edge to edge in a square. A
        /// generated layout is a packer output on a pitch with a margin, so the tile delta is the
        /// only distance between two lots that means anything - and it is the same delta the facade
        /// transform and the surround placement already use, so a neighbour standing here as a real
        /// lot is standing where its facade was.
        /// </para>
        ///
        /// <para><b>The sign, and why this is the raw delta and not its negation.</b>
        /// <c>SubWorldComponent.GlobalPosition</c> is read by the engine in three places, and two
        /// of them take it positive, so it is the sub-lot's minimum corner in the parent's tile
        /// space and nothing else:</para>
        /// <list type="bullet">
        ///   <item><description>
        ///     <c>SubDraw</c>, which is the only place a sub-lot is actually drawn -
        ///     <c>WorldArchitecture.DrawSub</c> calls it, and the alternatives are commented out in
        ///     <c>FSO.LotView</c>. In 2D it does <c>parentState.CenterTile += GlobalPosition</c>;
        ///     in 3D it does <c>Cameras.ModelTranslation = new Vector3(GlobalPosition.X * 3, 0,
        ///     GlobalPosition.Y * 3)</c>. Both put the sub-lot's own tile (0, 0) at the parent's tile
        ///     <c>GlobalPosition</c>, unswapped and unsigned.
        ///   </description></item>
        ///   <item><description>
        ///     <c>PreDraw</c>: <c>Blueprint.Terrain.SubworldOff = GlobalPosition * 3</c>, the fade
        ///     rectangle the sub-lot's own ground is cut against. Positive as well.
        ///   </description></item>
        ///   <item><description>
        ///     <c>UpdateBounds</c> is the one that disagrees: it puts its box at
        ///     <c>-GlobalPosition * 3</c> in x and z. That value is not a placement. It is merged
        ///     into <c>World.SkyBounds</c> by <c>InitSubWorlds</c> and read by nothing else but the
        ///     3D sky box, and the parent's own box in that same method is
        ///     <c>[0, Width * 3] x [0, Height * 3]</c> - which is to say the parent's own lot is at
        ///     <c>GlobalPosition == 0</c> under the same positive convention <c>SubDraw</c> uses.
        ///     It is FSO's inconsistency on a bounds-only path, it is not a mirroring instruction,
        ///     and it is not fixable from this codebase. Negating the delta to satisfy it would put
        ///     every neighbour on the diagonally opposite plot.
        ///   </description></item>
        /// </list>
        /// <para>
        /// So: <b>the raw tile delta, unsigned.</b> TSO's own assignment -
        /// <c>new Vector2((1 - y) * (size - 2), (x - 1) * (size - 2))</c>, where <c>x</c> is its
        /// grid column and <c>y</c> its grid row - is negated and transposed relative to that same
        /// grid, which is a second reason not to copy it: its <c>X</c> is the grid <em>row</em> and
        /// its <c>Y</c> the grid <em>column</em>. It happens to read correctly there only because
        /// of how TSO packs <c>vm.TSOState.LotID</c>. <see cref="SimitoneSurroundingsPlan.SubWorldPositionFor"/>
        /// states this file's rule in one line, and it is the only place the delta is computed.
        /// </para>
        /// </summary>
        public Vector2 SubWorldPosition { get; }

        /// <summary>
        /// Which way the lot faces, 0..3, as the high half of <c>vm.TSOState.Size</c> - taken from
        /// the layout's <see cref="NeighbourhoodLot.DoorSide"/> rather than from
        /// <c>VMLotTerrainRestoreTools.PickRoadDir</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The packer chose <c>DoorSide</c> from the lot's distance to the map border and the road
        /// graph grew a driveway on that side, so it is where this lot's road actually is.
        /// <c>PickRoadDir</c> reads TSO's road byte instead, which is a four-corner fan about a
        /// lot in a uniform grid this neighbourhood does not have; a lot facing the wrong way puts
        /// its mailbox, bin and car portal on the wrong pavement, which a player standing next door
        /// can see.
        /// </para>
        /// <para>
        /// <b>It is not the same integer.</b> TSO's lot direction names a <em>corner</em> of the
        /// lot and the layout's door side names an <em>edge</em>, so this is
        /// <see cref="SimitoneSurroundingsPlan.LotDirectionFor"/> and not a cast. Casting is what
        /// this used to do, and it turned every lot a quarter turn: TSO's 0 is the buildable area
        /// at the lot's <em>bottom</em>-left, not at its north edge. See that method for both
        /// tables.
        /// </para>
        /// </remarks>
        public ushort LotDirection { get; }

        /// <summary>
        /// The side of the blueprint to build this lot at, in tiles: the lot's own
        /// <see cref="NeighbourhoodLot.Size"/>, floored at
        /// <see cref="SimitoneSurroundingsPlan.MinSubWorldSize"/>.
        /// </summary>
        public int SubWorldSize { get; }

        /// <summary>
        /// This lot's ground, as the single <c>size * size</c> altitude field the sub-world is to
        /// be built on - the surround's own heights, corner for corner, in the lot view's altitude
        /// units and its drawn-corner order (<c>z * size + x</c>). Null when the map could not say,
        /// which the bridge reads as "leave the helpers' own field alone".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why the plan carries it rather than the bridge reading it.</b> The bridge
        /// (<see cref="SimitoneSurroundingsRestore"/>) is handed this plan and has no generated map,
        /// and the plan is where the map, the active lot's placement and both lots' base levels are
        /// all in scope at once. So the field is decided here, once, by
        /// <see cref="SimitoneSurroundingsPlacement.SubWorldAltitudesFor"/> - the same function the
        /// surround's own <see cref="SimitoneSurroundingsPlacement.ToLotView"/> defines the heights
        /// with - and the bridge only copies it. Two readings of the same map is precisely the bug
        /// this field exists to remove, so the second reading is not allowed to exist anywhere.
        /// </para>
        /// <para>
        /// It is an array, so it is shared rather than copied between the plan that read the
        /// contents and the plan that has them (<see cref="WithHollow"/>). Nothing mutates it: the
        /// bridge copies it into the sub-world's own <c>Terrain.Heights</c>, and the plan hands out
        /// the same array for every read. <see cref="AltitudeAt"/> is the accessor to use; it clamps,
        /// so a caller cannot walk off the field.
        /// </para>
        /// </remarks>
        public short[] SubWorldAltitudes { get; }

        /// <summary>
        /// The merged ground's altitude at one of the sub-world's own drawn corners, clamped to the
        /// field. 0 for no field, which is the answer the helpers' own field also gives at a lot's
        /// centre.
        /// </summary>
        /// <param name="x">The sub-world's own tile x, from its minimum corner.</param>
        /// <param name="z">The sub-world's own tile z, from its minimum corner.</param>
        public short AltitudeAt(int x, int z)
        {
            var field = SubWorldAltitudes;
            if (field == null || SubWorldSize <= 0) return 0;
            var cx = Math.Max(0, Math.Min(SubWorldSize - 1, x));
            var cz = Math.Max(0, Math.Min(SubWorldSize - 1, z));
            return field[cz * SubWorldSize + cx];
        }

        /// <summary>Whether this neighbour carries a merged ground to be built on.</summary>
        public bool HasMergedGround
        {
            get { return SubWorldAltitudes != null && SubWorldSize > 0; }
        }

        /// <summary>
        /// This lot's own decoded <c>ARRY(0)</c> plane - the graded, per-tile height field the lot
        /// view would have loaded out of its own <c>HouseNN.iff</c> - or
        /// <see cref="SimitoneArryAltitude.None"/> where there was none to read.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Carried rather than consulted. By the time a plan exists both of its consumers have been
        /// computed from it - <see cref="Terrain"/>'s sixteen samples by
        /// <see cref="SimitoneNeighbourTerrain.TryBuild"/> and <see cref="SubWorldAltitudes"/> by
        /// <see cref="SimitoneSurroundingsPlacement.SubWorldAltitudesFor"/> - and the bridge, which
        /// has no generated map and no content manager, copies those two. So the plane itself is the
        /// provenance of both: it is what lets the log say <em>why</em> a neighbour is graded or
        /// pinned rather than only which, and it is what makes
        /// <see cref="SimitoneSurroundingsPlan.ArryPlanes"/> a plan the caller can interrogate.
        /// </para>
        /// <para>
        /// <b>The plane is never the active lot's.</b> The lot the player is standing in is drawn by
        /// the lot view from its own <c>ARRY(0)</c> with
        /// absolute altitude (the surround is lifted to meet it), and the plan reads the
        /// generated map's pinned value for
        /// <see cref="SimitoneSurroundingsPlan.ActiveTerrain"/> exactly as it did before this field
        /// existed. Grading the neighbours is what puts the street on one plane; re-deriving the
        /// player's own ground here would move the one surface everything else is measured against.
        /// </para>
        /// </remarks>
        public SimitoneArryAltitude ArryAltitude { get; }

        /// <summary>
        /// Whether this lot's ground is its own graded shape rather than the generated map's flat
        /// pin over its rectangle. The same answer as <see cref="SimitoneNeighbourTerrain.IsGraded"/>,
        /// named for the log line and for a test that wants to ask about a neighbour and not about
        /// a terrain record.
        /// </summary>
        public bool IsGradedGround
        {
            get { return Terrain.IsGraded; }
        }

        /// <summary>Whether the lot has stored contents to build from.</summary>
        public bool IsHollow
        {
            get { return Hollow != null; }
        }

        /// <summary>
        /// This neighbour with different contents: the same lot, terrain, position, direction and
        /// size, and a <paramref name="hollow"/> in place of this one's marshal.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one way a neighbour's contents can change after the plan has been made. A plan is a
        /// value and nothing revises it - that is what makes two plans over the same inputs equal
        /// element for element - but <b>reading the contents is not part of the decision</b>. Which
        /// lots are real neighbours is arithmetic over a layout and a map, and asking it must not
        /// cost a <c>World</c>, a <c>VM</c> and a whole house-file load; so a neighbour whose
        /// contents the store did not have is built blank, and
        /// <see cref="NeighbourHollowLoader"/> goes and reads them afterwards and this method is
        /// how they reach the plan the restore is about to consume.
        /// </para>
        /// <para>
        /// Every other field is copied rather than recomputed, deliberately.
        /// <see cref="Create"/> would re-derive <see cref="SubWorldSize"/> from the lot, and the
        /// terrain and the position are the plan's own: this is a replacement of one field and not
        /// a second decision about where the lot stands.
        /// </para>
        /// </remarks>
        /// <param name="hollow">The lot's stored contents, or <c>null</c> for a blank lot.</param>
        public SimitoneSurroundingsNeighbour WithHollow(VMHollowMarshal hollow)
        {
            return new SimitoneSurroundingsNeighbour(Lot, Terrain, hollow, SubWorldPosition,
                LotDirection, SubWorldSize, SubWorldAltitudes, ArryAltitude);
        }

        /// <summary>Whether this neighbour could be built at all.</summary>
        public bool IsValid
        {
            get { return Lot != null && Terrain.IsValid && SubWorldSize > 0; }
        }

        /// <summary>
        /// This lot's ground height in the helpers' units - see
        /// <see cref="SimitoneNeighbourTerrain.BaseLevel"/>. What the bridge subtracts from the
        /// active lot's to get the sub-world's <c>Blueprint.BaseAlt</c>.
        /// </summary>
        /// <remarks>
        /// The subtraction is the whole of how a sub-world is hung at a height, and the units are
        /// the whole of why it works. Both sides are hundredths of the FreeSO helpers' own
        /// <c>ARRY(0)</c> height byte, which the helpers multiply by a hundred to become a
        /// blueprint altitude unit, and <c>Blueprint.BaseAlt</c> is in blueprint altitude units, so
        /// the difference needs no conversion. Composed through the unified vertical contract
        /// (<see cref="NeighbourhoodCityMeshBuilder"/>), one height byte is
        /// <c>100 / LotAltitudeUnitsPerArryUnit</c> = 10 <c>ARRY(0)</c> units, and one
        /// <c>ARRY(0)</c> unit is <see cref="NeighbourhoodCityMeshBuilder.TilesPerArryUnit"/> of a
        /// tile and a tile is <see cref="NeighbourhoodCityMeshBuilder.ElevationScale"/> of them -
        /// so one height byte is <c>10 * (9/4)</c> = <b>22.5 elevation bytes</b>, or 1.875 tiles of
        /// world height, and one elevation byte is <c>1 / 22.5</c> of a height byte. That last
        /// number is <see cref="SimitoneNeighbourTerrain.ElevationBytesPerHeightByte"/>, and it is
        /// the one that has to be right: it is what makes the sub-world's own ground land on the
        /// surround's ground under it rather than 22.5 times above or below it. See
        /// <see cref="SimitoneSurroundingsRestore.BaseAltitudeFor"/>, which is the arithmetic
        /// rather than the prose.
        /// </remarks>
        public int BaseLevel
        {
            get { return Terrain.BaseLevel; }
        }

        /// <summary>One line for a log: the lot, where it stands, what it is built out of.</summary>
        public string Describe()
        {
            if (Lot == null) return "no neighbour";
            return "lot " + Lot.Number
                + " at tile (" + Lot.WorldX + "," + Lot.WorldY + ")"
                + " as sub-world " + SubWorldPosition.X.ToString(CultureInfo.InvariantCulture)
                + "," + SubWorldPosition.Y.ToString(CultureInfo.InvariantCulture)
                + ", " + SubWorldSize + "x" + SubWorldSize
                + ", facing " + LotDirection
                + (IsHollow ? ", hollow" : ", blank")
                + ", base level " + BaseLevel
                + (IsGradedGround
                    ? ", ground graded from its own ARRY(0)"
                    : ", ground pinned to the map's median");
        }

        public override string ToString()
        {
            return Describe();
        }

        /// <summary>
        /// Builds a neighbour. Internal because the only honest way to have one is through
        /// <see cref="SimitoneSurroundingsPlan"/>, which is where every decision it records is
        /// made.
        /// </summary>
        /// <param name="arry">
        /// The lot's own <c>ARRY(0)</c> plane, or the default value - which is
        /// <see cref="SimitoneArryAltitude.None"/>, so a caller that only cares about the road
        /// byte's seam keeps compiling and keeps saying "the map's flat median".
        /// </param>
        internal static SimitoneSurroundingsNeighbour Create(
            NeighbourhoodLot lot, SimitoneNeighbourTerrain terrain, VMHollowMarshal hollow,
            Vector2 subWorldPosition, ushort lotDirection, short[] subWorldAltitudes,
            SimitoneArryAltitude arry = default(SimitoneArryAltitude))
        {
            return new SimitoneSurroundingsNeighbour(lot, terrain, hollow, subWorldPosition,
                lotDirection, SimitoneSurroundingsPlan.SubWorldSizeFor(lot), subWorldAltitudes,
                arry);
        }
    }

    /// <summary>
    /// Which lots become real sub-worlds around the lot the player is standing in, and what each
    /// one is built out of. B2's decision, made once and purely.
    ///
    /// <para><b>Why a plan at all.</b> "Which neighbours" and "how they are built" are two
    /// different kinds of question, and only the second one needs a <c>VM</c>, a graphics device and
    /// game content. Splitting them here means the half that decides <em>which lots are real</em> is
    /// arithmetic over a layout, a map and a store and can be asserted in a test with no engine at
    /// all; and it means the bridge is left with nothing to decide, so the two halves cannot
    /// disagree about which lots are hollow or which way they face.</para>
    ///
    /// <para><b>And the one thing it does read: each neighbour's own <c>ARRY(0)</c> plane.</b> The
    /// plan asks <see cref="SimitoneArryPlanes"/> for it and threads the answer into
    /// <see cref="SimitoneNeighbourTerrain.TryBuild"/> and
    /// <see cref="SimitoneSurroundingsPlacement.SubWorldAltitudesFor"/>, because both of those are
    /// the lot's drawn ground and the generated map can only answer either of them with a plane at
    /// the lot's median. So "no engine at all" above means no <c>World</c>, no <c>VM</c> and no
    /// device: the height source is a parameter, and a caller with no Sims install - or a test with
    /// no files - hands in one that has no planes and gets exactly the answer it had before.</para>
    ///
    /// <para><b>What selects a neighbour.</b> Two sufficient reasons, and the radius bounds both.
    /// A lot is a candidate when the road graph reaches it from this lot's door
    /// (<see cref="NeighbourhoodQuery.NeighboursOf"/>), <em>or</em> when it sits inside the radius
    /// and has a house. The route is a preference, not a permission: a lot whose door cannot be
    /// walked to is still a real house if it is close enough to be in the neighbour region, because
    /// "which driveways the generator joined up" is not the same question as "may this lot be stood
    /// up as a sub-world" - tying the two together left the live street with one candidate out of
    /// eleven in-radius houses. Reachable lots are ordered ahead of the rest when the budget cuts,
    /// because a lot the player can walk to is the one worth spending the load on. A lot with no
    /// door and no house - a bulldozed plot - is still not a candidate, and the radius is still a
    /// budget rather than a description: a lot view standing up a <c>World</c> and a <c>VM</c> per
    /// neighbour is a cost, so the radius is a <em>gap</em> in tiles between the two rectangles - the
    /// same reading <see cref="SimitoneSurroundingsCull"/> gives "forty tiles past the property" -
    /// and a radius of zero means touching or overlapping, never "centres within n tiles". By
    /// default it is the neighbour region's own radius
    /// (<see cref="DefaultSubWorldRadiusTiles"/>), so a lot close enough to stand in is a lot that
    /// gets a real house rather than a facade.</para>
    ///
    /// <para><b>What a radius alone cannot express, and the budget that does.</b> The radius is a
    /// description, so the candidate set can still ask for more lots than a lot view should stand
    /// up: <c>SubWorldComponent</c> is a whole <c>World</c> with its own world state, architecture,
    /// room map and light map, plus a throwaway <c>World</c> and <c>VM</c> in the loader for a
    /// hollow neighbour. So the candidates the radius and the route produce are ordered
    /// reachable-first, nearest-first within each half, by <see cref="SubWorldGapFor"/>, and cut at
    /// <see cref="MaxSubWorlds"/>. What falls off the end is <see cref="DeferredNumbers"/> and is
    /// not dropped - it is inside the neighbour region, so
    /// <see cref="NeighbourhoodRenderDecision"/> still draws it, as a facade.</para>
    ///
    /// <para><b>What the plan is not responsible for.</b> Whether a neighbour ends up a real
    /// sub-world or a facade is a rendering decision B2 task 5 makes per lot, and this plan is its
    /// input: it says what could be built, the renderer says what will be. That is why a plan with
    /// no neighbours is <em>valid</em> rather than a failure - a lot on the edge of the world with a
    /// radius of one genuinely has none, and the caller's answer to that is to draw ground and no
    /// houses rather than to log a problem.</para>
    ///
    /// <para><b>Determinism, and the two orders.</b> There are two, and neither is an accident.
    /// <see cref="SelectionOrder"/> is <em>reachable first, then nearest first</em> - the lots the
    /// road graph reaches sort ahead of the in-radius houses it does not, and within each half the
    /// order is by <see cref="SubWorldGapFor"/>, with the lot number as the tie-break - and it is
    /// the order the budget consumes, so which lot survives a full budget is a fact about the layout
    /// and the route and not about the iteration order of whatever produced the candidates.
    /// <see cref="Neighbours"/> is <em>ascending lot number</em>, which is the layout's own order,
    /// because that is what everything downstream of the plan wants to read. Two plans over the
    /// same inputs are element for element equal in both.</para>
    /// </summary>
    public sealed class SimitoneSurroundingsPlan
    {
        /// <summary>
        /// The smallest side a neighbour's blueprint is built at, in tiles.
        /// </summary>
        /// <remarks>
        /// <c>RestoreRoad</c> and <c>PositionLandmarkObjects</c> both work in offsets from the far
        /// edge of the architecture - <c>arch.Width - 9</c>, <c>arch.Height - 7</c> - so a lot
        /// narrower than about sixteen tiles has no room for a road template or a mailbox row, and
        /// the helpers write past the end of what their rectangles meant rather than throwing. A
        /// TS1 lot is 64 tiles, so this only fires on a corrupt or hand-written layout, and the
        /// alternative - building a nine-tile lot and letting the templates clip - produces a
        /// neighbour with a road across the middle of it.
        /// </remarks>
        public const int MinSubWorldSize = 16;

        /// <summary>
        /// How many tiles of gap from the active lot a neighbour may sit, and the radius the facade
        /// side of the render decision is measured against. B2 task 5's shared cut - and it is
        /// <b>the same number as the neighbour region's own radius</b>, so there is no band of
        /// facades between the real lots and the flat ones.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One number for both halves of the decision, and it is the region's.</b> A neighbour is
        /// a real sub-world when <see cref="WithinRadius"/> says so; every other lot inside the
        /// neighbour region is a facade, and "inside the neighbour region" is
        /// <see cref="SimitoneSurroundingsCull.CoversLot"/> at
        /// <see cref="SimitoneSurroundingsPlacement.CullRadiusTiles"/>. Those are two questions -
        /// which lots may be <em>stood up</em>, and how much of the neighbourhood is <em>built
        /// up</em> - and they used to have two answers, eight and forty, which is what left the
        /// middle distance as a ring of facades between the real lots at the centre and the drawn
        /// ground at the edge. The user-facing complaint that produced that ring was "some houses
        /// are real and some are cardboard", so the two radii are now the same radius and the only
        /// thing that decides whether a drawn lot is real is <see cref="MaxSubWorlds"/>.
        /// </para>
        /// <para>
        /// <b>CHANGED: this is no longer the radius of the drawn ground.</b> The ground and the
        /// streets are drawn for the whole connected map
        /// (<see cref="SimitoneSurroundingsPlacement.DrawnRegion"/>), and this is the radius of the
        /// <em>neighbour</em> region - which lots are close enough to be stood up as real sub-worlds
        /// or built as facades rather than left as the far houses the map view draws. Both are still
        /// the same number, so every lot inside the neighbour region is real, and the lots between
        /// that region and the edge of the map are facades - which is what the neighbourhood view
        /// shows for them too.
        /// </para>
        /// <para>
        /// <b>What makes this affordable is that the region is small.</b> The neighbour region is
        /// the active lot's own rectangle plus this margin on every side, so it spans
        /// <c>size + 2 * CullRadiusTiles</c> tiles - 144 for a 64-tile TS1 lot. At the packer's
        /// pitch (<c>NeighbourhoodLayoutBuilder.StreetCorridorTiles</c> of corridor, so
        /// <c>Size + corridor</c> = 72) that is two lots across and two deep, and the four diagonal
        /// plots at one pitch out are inside it as well - which is eight neighbours, not four,
        /// because a radius is a gap between rectangles and a diagonal plot's gap is the corridor
        /// and not the diagonal. See <see cref="MaxSubWorlds"/> for what the ceiling is there for.
        /// </para>
        /// <para>
        /// <b>What the two radii may still not do is disagree.</b> Because they are literally the
        /// same constant the disagreement is gone rather than checked, but the relation remains a
        /// named rule - a caller may still build a plan at any radius - and it is still asserted:
        /// <see cref="RadiusFitsCut"/> is the check on the two numbers themselves and
        /// <see cref="NeighbourhoodRenderDecision.SubWorldsAreInsideCut"/> is the check on a plan
        /// and a region together.
        /// </para>
        /// <para>
        /// The screen used to carry this as its own literal. It does not have to be this constant
        /// to be correct - any radius that <see cref="RadiusFitsCut"/> accepts is correct, because
        /// the decision reads the plan's own <see cref="RadiusTiles"/> rather than a number of its
        /// own - but it should be, so that there is one thing to widen.
        /// </para>
        /// </remarks>
        public const int DefaultSubWorldRadiusTiles = SimitoneSurroundingsPlacement.CullRadiusTiles;

        /// <summary>
        /// The most neighbouring lots one lot view will stand up as real sub-worlds, in lots. The
        /// hard budget, and the only thing standing between "the whole neighbour region is real" and
        /// "the whole neighbour region is real on a machine that can afford it".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What one sub-world is, priced.</b> <c>SubWorldComponent</c> derives from
        /// <c>FSO.LotView.World</c>, so each one is not a mesh but a lot: its own
        /// <c>WorldState</c> and viewport, its own <c>WorldArchitecture</c> and
        /// <c>WorldEntities</c> over its own blueprint, its own room map, and - with advanced
        /// lighting on - its own <c>LMapBatch(gd, 6)</c> light map, held for the whole time the
        /// player is on the lot. Every frame it is walked twice by the lot view:
        /// <c>PreDraw</c> rebuilds its terrain offset and re-prepares its outdoor lighting, and
        /// <c>SubDraw</c> translates the parent's camera, re-prepares lighting again and restores
        /// it afterwards. A neighbour whose contents are hollow costs a second <c>World</c> and a
        /// second <c>VM</c> besides, in the loader, to read the house file.
        /// </para>
        /// <para>
        /// <b>What governs the number, then.</b> Two costs, and they pull in opposite directions.
        /// The steady-state one is linear and cheap: <c>N</c> pairs of
        /// <c>PreDraw</c>/<c>SubDraw</c> and <c>N</c> light maps resident. The one that actually
        /// bites is the load: <c>SimitoneSurroundingsRestore.TryRestore</c> builds every neighbour
        /// in one lot load, so <c>N</c> is <c>N</c> sequential world stands on the path between a
        /// player pressing travel and seeing a house. That is a per-load stall and it is why this
        /// is a number rather than "all of them".
        /// </para>
        /// <para>
        /// <b>Why thirty-two.</b> <see cref="DefaultSubWorldRadiusTiles"/> puts the realistic
        /// demand at <b>eight</b>: the eight plots one pitch from the player, at the
        /// <see cref="NeighbourhoodLayoutBuilder.StreetCorridorTiles"/>-corridor pitch the packer
        /// produces, minus the one the player is standing in. Thirty-two is therefore about <b>3.5x
        /// the realistic worst case</b>, and it is chosen to be unreachable rather than tight - a
        /// budget that binds on the layout the packer actually produces would be a second radius in
        /// disguise, and the facades it let through would be back with a different name. It binds on
        /// three things only: a hand-written layout packing lots far smaller than
        /// <see cref="NeighbourhoodLayout.MinLotSize"/>'s neighbours allow (at the smallest legal
        /// pitch of <c>Size + 1</c> the same region holds several hundred plots), a generated map
        /// whose lots are laid out far tighter than the corridor the packer reserves, or a machine
        /// slow enough that the per-load stall is felt at eight.
        /// </para>
        /// <para>
        /// <b>What happens when it does bind.</b> The plan takes the <see cref="SelectionOrder"/>
        /// prefix - nearest the player first, lot number as the tie-break, so the choice is
        /// deterministic and the same lot is the one dropped on every visit - and the rest are
        /// <see cref="DeferredNumbers"/>. A deferred lot is not dropped: it is inside the drawn
        /// region, so <see cref="NeighbourhoodRenderDecision"/> still draws it, as a facade. The
        /// budget therefore costs flatness at the far edge and never costs a hole.
        /// </para>
        /// </remarks>
        public const int MaxSubWorlds = 32;

        private static readonly SimitoneSurroundingsNeighbour[] NoNeighbours
            = new SimitoneSurroundingsNeighbour[0];

        private static readonly short[] NoLotNumbers = new short[0];

        private static readonly string[] NoWarnings = new string[0];

        private SimitoneSurroundingsPlan(
            NeighbourhoodLot activeLot, SimitoneNeighbourTerrain activeTerrain, int radiusTiles,
            bool roadReachability, IReadOnlyList<SimitoneSurroundingsNeighbour> neighbours,
            IReadOnlyList<string> warnings, string failure, LotStore store,
            IReadOnlyList<short> selectionOrder = null,
            IReadOnlyList<short> deferred = null,
            int maxSubWorlds = MaxSubWorlds,
            SimitoneArryPlanes arryPlanes = null)
        {
            ActiveLot = activeLot;
            ActiveTerrain = activeTerrain;
            RadiusTiles = radiusTiles;
            RoadReachability = roadReachability;
            Neighbours = neighbours ?? NoNeighbours;
            Warnings = warnings ?? NoWarnings;
            Failure = failure;
            Store = store;
            SelectionOrder = selectionOrder ?? NoLotNumbers;
            DeferredNumbers = deferred ?? NoLotNumbers;
            SubWorldBudget = maxSubWorlds < 0 ? 0 : maxSubWorlds;
            ArryPlanes = arryPlanes;
        }

        /// <summary>
        /// The lot the player is standing in, as the placement records it. Null on an invalid plan.
        /// </summary>
        public NeighbourhoodLot ActiveLot { get; }

        /// <summary>
        /// The active lot's own terrain, on the same scale as its neighbours' - which
        /// <c>vm.TSOState.Terrain</c> cannot supply, because a TS1 lot's state has none (Part B
        /// §11, landmine 2). Every sub-world's altitude offset is measured from this.
        /// </summary>
        public SimitoneNeighbourTerrain ActiveTerrain { get; }

        /// <summary>
        /// The active lot's ground height in the helpers' units. A neighbour's
        /// <c>Blueprint.BaseAlt</c> is this minus the neighbour's own.
        /// </summary>
        public int ActiveBaseLevel
        {
            get { return ActiveTerrain.BaseLevel; }
        }

        /// <summary>How many tiles of gap from the active lot a neighbour may sit.</summary>
        public int RadiusTiles { get; }

        /// <summary>
        /// Whether road reachability was available to this plan. False means the
        /// <see cref="NeighbourhoodQuery"/> could not be built and selection fell back to the
        /// radius alone, which is a wider net than intended and is said out loud in
        /// <see cref="Describe"/> rather than being silent about it.
        /// </summary>
        public bool RoadReachability { get; }

        /// <summary>
        /// The neighbours, in ascending lot number. Empty, and not an error, when there are none.
        /// </summary>
        public IReadOnlyList<SimitoneSurroundingsNeighbour> Neighbours { get; }

        /// <summary>
        /// The neighbours this plan considered, nearest the player first - the order
        /// <see cref="MaxSubWorlds"/> was applied in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Ascending gap from <see cref="SubWorldGapFor"/> (which is the same measure
        /// <see cref="WithinRadius"/> thresholds, so the first
        /// <c>Neighbours.Count</c> entries of this list are exactly the lots that survived), then
        /// ascending lot number as the tie-break. Two neighbours at the same gap on opposite sides
        /// of the same street are otherwise interchangeable and the lot number is what makes the
        /// choice the same on every visit and on every machine.
        /// </para>
        /// <para>
        /// It is the whole of the budget's determinism: <c>Neighbours</c> is sorted back into
        /// ascending lot number for the callers downstream, so without this list there would be no
        /// way to tell which lot won a full budget from the plan alone.
        /// </para>
        /// </remarks>
        public IReadOnlyList<short> SelectionOrder { get; }

        /// <summary>
        /// The lots the budget did not take: inside the radius, buildable, and further from the
        /// player than <see cref="MaxSubWorlds"/> others. Nearest-first tail of
        /// <see cref="SelectionOrder"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>These are drawn, not dropped.</b> Each one is inside the neighbour region, so
        /// <see cref="NeighbourhoodRenderDecision"/> gives it a facade - which is the whole of the
        /// budget's cost. A lot that is neither a sub-world nor a facade is a hole in the street,
        /// and there is no path from here to one.
        /// </para>
        /// <para>
        /// Empty on any plan whose candidate count fits inside the budget, which at
        /// <see cref="DefaultSubWorldRadiusTiles"/> and the packer's own pitch is every lot a
        /// real generated layout produces. The plan logs it either way, because "some of the
        /// street is flat" is a thing a player reports and the log is the only place that can say
        /// whether the budget or the layout caused it.
        /// </para>
        /// </remarks>
        public IReadOnlyList<short> DeferredNumbers { get; }

        /// <summary>
        /// How many sub-worlds this plan was allowed. Equal to <see cref="MaxSubWorlds"/> unless
        /// the caller named a different budget.
        /// </summary>
        /// <remarks>
        /// Named for what it is rather than for the constant it usually is: a per-plan budget that
        /// a caller can lower is how the over-budget behaviour is reached at all, and a test cannot
        /// observe the shipped path without one.
        /// </remarks>
        public int SubWorldBudget { get; }

        /// <summary>How many lots the budget left to the facade pass. Zero on a full plan.</summary>
        public int DeferredCount
        {
            get { return DeferredNumbers.Count; }
        }

        /// <summary>
        /// Whether this plan stood up every lot inside the neighbour region, so that no lot in view
        /// is a facade for any reason. The answer the whole coverage change exists to be true.
        /// </summary>
        public bool IsFullyCovered
        {
            get { return DeferredNumbers.Count == 0; }
        }

        /// <summary>
        /// Everything that was skipped and why - a lot the map could not answer about, a lot a
        /// caller named that the layout does not have. One line each, and the bridge hands them to
        /// the log as they are written: a neighbour that silently fails to build is a player looking
        /// at a gap in the street with nothing in the log to explain it.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Why there is no plan, or <c>null</c> when there is one.</summary>
        public string Failure { get; }

        /// <summary>
        /// The store this plan read its neighbours' contents out of, or <c>null</c> when there was
        /// none. Never written to by the plan.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Held rather than taken, so that the bridge can put a neighbour's contents back where
        /// they belong. A lot whose house file has just been read into a hollow marshal is now a
        /// <see cref="NeighbourhoodLotState.Hollow"/> lot, and <see cref="LotStore"/> is where the
        /// next lot load looks to find out - so a marshal the loader built and the restore threw
        /// away would be paid for again on the next switch. <see cref="WithHollows"/> keeps this
        /// the same store, and the plan still never writes to it itself.</para>
        /// <para>
        /// Null on the overloads that take no store, which is the first cut's shape and the reason
        /// a lot with no world attached still gets its neighbours, blank.
        /// </para>
        /// </remarks>
        public LotStore Store { get; }

        /// <summary>
        /// Where each neighbour's own <c>ARRY(0)</c> plane came from, and what it did with the ones
        /// it could not get. The provenance of every
        /// <see cref="SimitoneSurroundingsNeighbour.IsGradedGround"/> answer on this plan, and the
        /// sentences naming the lots that fell back to the generated map's flat median.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Held rather than taken, so that the bridge can ask why a street is flat without holding a
        /// content manager of its own - which it must not, because it is handed a plan and a
        /// <c>VM</c> and nothing else. Never null: a plan built with no source is built with
        /// <see cref="SimitoneArryPlanes.HouseFiles"/>, and an invalid plan has none because it has
        /// no neighbours to ask about.
        /// </para>
        /// <para>
        /// <b>A caller that plans repeatedly over one street should hold one source and pass it
        /// in.</b> The cache inside it is keyed on each lot's house-file fingerprint, so the second
        /// lot load over the same houses is answered out of memory. A source built per plan - which is
        /// what a caller that does not pass one gets - re-reads those files, which is cheap (one
        /// chunk, no world, no VM) but is not free.
        /// </para>
        /// </remarks>
        public SimitoneArryPlanes ArryPlanes { get; }

        /// <summary>
        /// How many of this plan's neighbours are built on their own graded <c>ARRY(0)</c> ground
        /// rather than on the generated map's pinned median.
        /// </summary>
        public int GradedGroundCount
        {
            get
            {
                var count = 0;
                for (int i = 0; i < Neighbours.Count; i++)
                {
                    if (Neighbours[i].IsGradedGround) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// Whether the bridge has something to work from. A plan with no neighbours is valid - see
        /// the type remarks - so this is about the plan existing, not about it being worth running.
        /// </summary>
        public bool IsValid
        {
            get { return Failure == null && ActiveLot != null && ActiveTerrain.IsValid; }
        }

        /// <summary>How many neighbours are built out of stored contents rather than blank.</summary>
        public int HollowCount
        {
            get
            {
                var count = 0;
                for (int i = 0; i < Neighbours.Count; i++)
                {
                    if (Neighbours[i].IsHollow) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// Whether this plan has a neighbour the bridge will have to go and find contents for: one
        /// that could be built and has none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one question <see cref="SimitoneSurroundingsRestore"/> asks before it decides to
        /// reach for a <see cref="NeighbourHollowLoader"/>, and the whole of why that costs nothing
        /// in the ordinary case: a plan whose neighbours the store already holds marshals for - or
        /// whose neighbours cannot be built at all - answers <c>false</c> and the loader is never
        /// constructed, so the whole pass stays off the game thread's critical path.</para>
        /// <para>
        /// Only <see cref="SimitoneSurroundingsNeighbour.IsValid"/> neighbours count. An invalid one
        /// is skipped by the restore before anything is built for it, and standing a whole
        /// <c>World</c> up to find contents for a lot that will not be built is the one way this
        /// could be made to cost something on a plan that has nothing to gain from it.
        /// </para>
        /// </remarks>
        public bool NeedsHollows()
        {
            for (int i = 0; i < Neighbours.Count; i++)
            {
                if (Neighbours[i].IsValid && !Neighbours[i].IsHollow) return true;
            }
            return false;
        }

        /// <summary>
        /// The same plan holding only the neighbours that carry contents. Blank neighbours - the
        /// ones past the per-load hollow-build budget, or whose house file could not be read - are
        /// empty plots, so withholding their facades leaves a hole where a house should be.
        /// </summary>
        public SimitoneSurroundingsPlan WithoutBlankNeighbours()
        {
            var kept = new List<SimitoneSurroundingsNeighbour>(Neighbours.Count);
            for (int i = 0; i < Neighbours.Count; i++)
            {
                if (Neighbours[i].IsHollow) kept.Add(Neighbours[i]);
            }
            if (kept.Count == Neighbours.Count) return this;
            return new SimitoneSurroundingsPlan(ActiveLot, ActiveTerrain, RadiusTiles,
                RoadReachability, new ReadOnlyCollection<SimitoneSurroundingsNeighbour>(kept),
                Warnings, Failure, Store, SelectionOrder, DeferredNumbers, SubWorldBudget,
                ArryPlanes);
        }

        /// <summary>
        /// This plan with the given lots' contents filled in, and the reasons the rest stayed blank
        /// appended to its warnings.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bridge between the loader and the restore, and it is pure: it copies the plan, hands
        /// each named lot its marshal through
        /// <see cref="SimitoneSurroundingsNeighbour.WithHollow"/>, and copies everything else -
        /// the active lot, its terrain, the radius, whether road reachability was available, the
        /// lot's order, the sub-world budget and its deferred list, and every warning already on
        /// the plan. Nothing is re-derived, so a plan that went through this cannot have moved a
        /// single neighbour.</para>
        /// <para>
        /// <b>It returns a new plan rather than editing this one</b>, and that is the plan's own
        /// rule - a plan is a value and nothing revises it - rather than an accident of the
        /// implementation. The caller keeps the plan it was given; what it hands to
        /// <c>HollowLoad</c> is this one. A lot the marshals do not name keeps the contents it had,
        /// including none, which is how a blank lot stays a blank lot.</para>
        /// <para>
        /// The warnings are appended rather than replacing, so the plan's own reasons are still on
        /// it: a caller logging <see cref="Warnings"/> gets "this lot was skipped by the map" and
        /// "this lot has no house file" in the order they happened, which is the order they were
        /// discovered in.</para>
        /// </remarks>
        /// <param name="marshals">
        /// The contents to fill in, by lot number. A lot number that is not a neighbour of this plan
        /// is ignored - a marshal for a lot this plan does not build cannot be put anywhere.
        /// </param>
        /// <param name="warnings">
        /// One line per lot that stayed blank and why, or <c>null</c> for none.
        /// </param>
        public SimitoneSurroundingsPlan WithHollows(
            IReadOnlyDictionary<short, VMHollowMarshal> marshals,
            IReadOnlyList<string> warnings = null)
        {
            if (marshals == null || marshals.Count == 0)
            {
                //nothing to fill in. The plan is returned as it is rather than as a copy, because
                //a copy would be a second object describing the same decision for no reason at all
                if (warnings == null || warnings.Count == 0) return this;
            }
            var neighbours = new List<SimitoneSurroundingsNeighbour>(Neighbours.Count);
            for (int i = 0; i < Neighbours.Count; i++)
            {
                var neighbour = Neighbours[i];
                VMHollowMarshal marshal;
                if (marshals != null && neighbour.Lot != null
                    && marshals.TryGetValue(neighbour.Lot.Number, out marshal) && marshal != null)
                {
                    neighbour = neighbour.WithHollow(marshal);
                }
                neighbours.Add(neighbour);
            }

            var lines = new List<string>(Warnings.Count + (warnings?.Count ?? 0));
            lines.AddRange(Warnings);
            if (warnings != null)
            {
                for (int i = 0; i < warnings.Count; i++)
                {
                    if (!string.IsNullOrEmpty(warnings[i])) lines.Add(warnings[i]);
                }
            }

            return new SimitoneSurroundingsPlan(ActiveLot, ActiveTerrain, RadiusTiles,
                RoadReachability, new ReadOnlyCollection<SimitoneSurroundingsNeighbour>(neighbours),
                new ReadOnlyCollection<string>(lines), Failure, Store,
                //the budget's own three fields carry over untouched. Reading a lot's contents does
                //not change how many lots may be stood up, which lots were deferred, or the order
                //they were chosen in - and dropping the deferred list here would silently promote
                //the lots the budget held back back into neighbours.
                SelectionOrder, DeferredNumbers, SubWorldBudget,
                //and so is the source the planes came from: filling in contents does not re-read a
                //height field, and the graded/pinned answers on the neighbours it copies are the
                //ones the plan that asked for them made.
                ArryPlanes);
        }

        /// <summary>The lot numbers of the neighbours, ascending. For a log line and a test.</summary>
        public IReadOnlyList<short> NeighbourNumbers
        {
            get
            {
                var numbers = new short[Neighbours.Count];
                for (int i = 0; i < Neighbours.Count; i++) numbers[i] = Neighbours[i].Lot.Number;
                return numbers;
            }
        }

        /// <summary>Whether this lot is one of the plan's neighbours.</summary>
        /// <param name="lotNumber">The lot number.</param>
        public bool Contains(int lotNumber)
        {
            for (int i = 0; i < Neighbours.Count; i++)
            {
                if (Neighbours[i].Lot.Number == lotNumber) return true;
            }
            return false;
        }

        /// <summary>The plan's entry for a lot, or the default value when it is not a neighbour.</summary>
        /// <param name="lotNumber">The lot number.</param>
        public SimitoneSurroundingsNeighbour NeighbourFor(int lotNumber)
        {
            for (int i = 0; i < Neighbours.Count; i++)
            {
                if (Neighbours[i].Lot.Number == lotNumber) return Neighbours[i];
            }
            return default(SimitoneSurroundingsNeighbour);
        }

        /// <summary>
        /// The side of the blueprint to build a lot at: its own size, floored at
        /// <see cref="MinSubWorldSize"/>. Zero for no lot, which the bridge reads as "skip this
        /// one".
        /// </summary>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static int SubWorldSizeFor(NeighbourhoodLot lot)
        {
            if (lot == null) return 0;
            return Math.Max(MinSubWorldSize, Math.Max(0, lot.Size));
        }

        /// <summary>
        /// The <c>Blueprint.BaseAlt</c> a sub-world is hung at, from the two lots' base levels.
        /// <see cref="SimitoneSurroundingsRestore.BaseAltitudeFor"/>'s own arithmetic, reached from
        /// here so that the plan and the bridge cannot read the two base levels in a different
        /// order.
        /// </summary>
        /// <param name="activeBaseLevel">The active lot's base level.</param>
        /// <param name="neighbourBaseLevel">The neighbour's base level.</param>
        public static int BaseAltitudeFor(int activeBaseLevel, int neighbourBaseLevel)
        {
            return SimitoneSurroundingsRestore.BaseAltitudeFor(activeBaseLevel, neighbourBaseLevel);
        }

        /// <summary>
        /// Whether a lot's own rectangle can hold a sub-world without standing on the road the
        /// surround draws beside it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the check that the sub-world placement and the road width have to agree
        /// on.</b> A sub-world is the real lot, at the engine's own scale, standing in a surround
        /// whose street is one corridor - the lot view's own <c>RestoreRoad</c> band reaching
        /// <see cref="SimitoneLotViewRoad.HalfStreetDepth"/> tiles into the lot on each side, and
        /// <see cref="NeighbourhoodLayoutBuilder.StreetCorridorTiles"/> of map between two lots - so
        /// a lot whose rectangle is narrower than its own street corridor would have road running
        /// through the middle of the house the engine is standing up, and the two would be drawn
        /// from two different ideas about where a street is.
        /// </para>
        /// <para>
        /// It is a range rather than an equality because the width is two numbers. The lot has to
        /// be at least <see cref="MinSubWorldSize"/> for <c>RestoreRoad</c>'s templates to fit
        /// inside it at all, and at least <see cref="RoadGraph.MainStreetCorridorWidth"/> for a
        /// full-width street plus its pavements to fit beside it. A TS1 lot is 64 tiles, so the
        /// second is the binding one by a wide margin and this only fires on a hand-written layout.
        /// </para>
        /// </remarks>
        /// <param name="lotSize">The lot's <see cref="NeighbourhoodLot.Size"/>, in lot tiles.</param>
        public static bool SubWorldFitsBesideAStreet(int lotSize)
        {
            if (lotSize < RoadGraph.MainStreetCorridorWidth) return false;
            return lotSize >= MinSubWorldSize;
        }

        /// <summary>
        /// Where a lot stands relative to another, in the parent's tile space: the difference
        /// between the two rectangles' minimum corners, as a
        /// <c>SubWorldComponent.GlobalPosition</c>.
        ///
        /// <para>
        /// The whole rule, in one line, because it is the one this file exists to get right:
        /// <c>GlobalPosition = neighbour.Min - active.Min</c>, unsigned and untransposed. See
        /// <see cref="SimitoneSurroundingsNeighbour.SubWorldPosition"/> for the three engine reads
        /// that establish the sign, and for why TSO's own <c>(1 - y), (x - 1)</c> is not the same
        /// shape.
        /// </para>
        /// <para>
        /// Exposed as its own method so a test can write the expected tile delta down against
        /// <c>SubDraw</c>'s rule - "the sub-lot's own origin lands at the parent's
        /// <c>CentreTile + GlobalPosition</c>" - instead of only reading that a number came out of
        /// the plan.
        /// </para>
        /// </summary>
        /// <param name="active">The lot the player is standing in.</param>
        /// <param name="other">The lot being placed as a sub-world, or <c>null</c>.</param>
        public static Vector2 SubWorldPositionFor(NeighbourhoodLot active, NeighbourhoodLot other)
        {
            if (active == null || other == null) return Vector2.Zero;
            return new Vector2(other.WorldX - active.WorldX, other.WorldY - active.WorldY);
        }

        /// <summary>
        /// The layout's door side as TSO's lot direction, for the high half of
        /// <c>vm.TSOState.Size</c>.
        ///
        /// <para>
        /// <b>Two different four-valued vocabularies, and the offset between them is two.</b>
        /// The layout's <see cref="NeighbourhoodLotSide"/> names an edge of the lot's rectangle -
        /// north is the low-y edge, then east, south and west, clockwise. TSO's lot direction is
        /// read by both <c>VMContext.GetTSOBuildableArea</c> and
        /// <c>VMLotTerrainRestoreTools.PositionLandmarkObjects</c> as
        /// <c>pt1 = corners[(lotDir + 2) % 4], pt2 = corners[(lotDir + 3) % 4]</c> over
        /// <c>corners = { (5,5) top-left, (w-7,5) top-right, (w-7,h-7) bottom-right, (5,h-7)
        /// bottom-left }</c>. Each consecutive pair is the two corners of one edge, and the
        /// midpoint of that pair is where the buildable area and the landmark row go, so:
        /// </para>
        /// <list type="table">
        ///   <listheader><term>TSO lotDir</term><description>edge the pair spans</description></listheader>
        ///   <item><term>0</term><description>the south (bottom) edge</description></item>
        ///   <item><term>1</term><description>the west (left) edge</description></item>
        ///   <item><term>2</term><description>the north (top) edge</description></item>
        ///   <item><term>3</term><description>the east (right) edge</description></item>
        /// </list>
        /// <para>
        /// Both cycles run clockwise, so they are parallel and the only question is the offset.
        /// North is lot direction 2, therefore <c>(side + 2) &amp; 3</c>: north maps to 2, east to
        /// 3, south to 0 and west to 1. <c>SimitoneSurroundingsPlanTests</c> asserts the helpers'
        /// own corner arithmetic rather than these four integers, because the midpoint is the thing
        /// that actually places the mailbox, the bin and the car portal - four integers would pin a
        /// wrong answer just as firmly as a right one.
        /// </para>
        /// <para>
        /// <b>What the cast this replaces did.</b> <c>LotEntrance.ClampFacing((int)DoorSide)</c> is
        /// the identity on 0..3, and the offset is two, so it was a <em>half</em> turn, not a
        /// quarter: every generated lot got its mailbox row and its buildable area on the pavement
        /// and the half of the lot opposite its driveway. A lot facing the wrong way is invisible in
        /// a log line and obvious from the next lot over, which is why this is a named function
        /// and not a cast.
        /// </para>
        /// <para>
        /// A side outside 0..3 - a hand-edited sidecar - folds to north, which is what
        /// <see cref="LotEntrance.ClampFacing"/> does with a facing it cannot read, and is done
        /// here rather than delegated so that this function is the whole of the mapping.
        /// </para>
        /// </summary>
        /// <param name="doorSide">Which edge of the lot's rectangle the driveway is on.</param>
        public static ushort LotDirectionFor(NeighbourhoodLotSide doorSide)
        {
            var side = (int)doorSide;
            if (side < (int)NeighbourhoodLotSide.North || side > (int)NeighbourhoodLotSide.West)
            {
                side = (int)NeighbourhoodLotSide.North;
            }
            return (ushort)((side + 2) & 3);
        }

        /// <summary>
        /// The door side a TSO lot direction puts the buildable area and the landmark row on: the
        /// inverse of <see cref="LotDirectionFor"/>, and the form the test asserts against.
        /// </summary>
        /// <param name="lotDirection">A lot direction, 0..3.</param>
        public static NeighbourhoodLotSide DoorSideForLotDirection(int lotDirection)
        {
            switch (((lotDirection % 4) + 4) % 4)
            {
                case 0: return NeighbourhoodLotSide.South;
                case 1: return NeighbourhoodLotSide.West;
                case 2: return NeighbourhoodLotSide.North;
                default: return NeighbourhoodLotSide.East;
            }
        }

        /// <summary>
        /// Whether a neighbour radius may be used against a drawn region of another radius without the
        /// two cuts disagreeing: the sub-world cut may be tighter than the facade cut, never wider.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The relation between the two numbers that stand in a render decision, written as a rule
        /// so it can be asked rather than assumed. A sub-world radius wider than the cull's would
        /// put real lots outside the surround's own crop - built, ticked and drawn by the lot view,
        /// standing on ground the surround never drew - which is the failure this pair of radii
        /// exists to make impossible.
        /// </para>
        /// <para>
        /// A sub-world radius <em>tighter</em> than the cull's is legal and is what an explicit
        /// narrower radius buys: the ring of lots between the two is then too far to be a real lot
        /// and close enough to be a facade. It is no longer what any production caller asks for -
        /// <see cref="DefaultSubWorldRadiusTiles"/> <b>is</b> the cull radius, so the ring is empty
        /// and every drawn lot is a real one - but the rule stays because the ring is exactly what
        /// the user reported as "some houses are real and some are cardboard", and a narrower radius
        /// being legal is how that comes back if somebody wants it.
        /// </para>
        /// </remarks>
        /// <param name="planRadiusTiles">The radius the neighbour plan selects at.</param>
        /// <param name="cullRadiusTiles">The radius the drawn region is built at.</param>
        public static bool RadiusFitsCut(int planRadiusTiles, int cullRadiusTiles)
        {
            //The one relation between the two radii, as a rule rather than as a fact about two
            //constants that happen to satisfy it today. A sub-world radius wider than the drawn
            //region would put real lots outside the surround's own crop: the neighbour is built,
            //ticked and drawn by the lot view while the ground it stands on was never drawn. So
            //the sub-world cut may be tighter than the facade cut, but never wider - and the shipped
            //pair is the same number twice, so the ring of facades between them is empty.
            return Math.Max(0, planRadiusTiles) <= Math.Max(0, cullRadiusTiles);
        }

        /// <summary>
        /// Whether the plan's radius and the neighbour region's radius are the same number, so that
        /// no lot inside the region is outside the plan's reach.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The shared-radius invariant, as a question. <see cref="RadiusFitsCut"/> is a one-sided
        /// relation and is satisfied by any narrower radius, which is legal but leaves the ring of
        /// facades that made the neighbourhood look like a mix. This asks for the stronger thing the
        /// lot view now ships: the two radii are equal, so the neighbour region <em>is</em> the
        /// sub-world region and the only flat lots a player can see inside it are ones the budget
        /// deferred.
        /// </para>
        /// <para>
        /// Equality rather than "no lot between them" is deliberate. Radius arithmetic is a gap
        /// between rectangles on one side and a rectangle overlap on the other, so two radii can
        /// differ by a tile and still cover the same lots - but the property that has to hold for
        /// the coverage claim to be readable off a log line is that there is only one number, and
        /// that is what this asks.
        /// </para>
        /// </remarks>
        /// <param name="planRadiusTiles">The radius the neighbour plan selects at.</param>
        /// <param name="cullRadiusTiles">The radius the drawn region is built at.</param>
        public static bool RadiusIsShared(int planRadiusTiles, int cullRadiusTiles)
        {
            return Math.Max(0, planRadiusTiles) == Math.Max(0, cullRadiusTiles);
        }

        /// <summary>
        /// Whether every lot this plan will build is inside a region, so that withholding a
        /// facade from it cannot open a hole in the street. See
        /// <see cref="NeighbourhoodRenderDecision.SubWorldsAreInsideCut"/>, which is the same
        /// question asked of the plan and a region together.
        /// </summary>
        /// <param name="cull">The neighbour region, or <c>null</c> for "no cut".</param>
        public bool SubWorldsInside(SimitoneSurroundingsCull? cull)
        {
            return NeighbourhoodRenderDecision.SubWorldsAreInsideCut(this, cull);
        }

        /// <summary>
        /// Whether this plan's radius is the region's own radius, so no lot inside the region
        /// is out of the plan's reach. See <see cref="RadiusIsShared"/>.
        /// </summary>
        /// <param name="cull">The neighbour region, or <c>null</c> for "no cut".</param>
        public bool RadiusSharedWith(SimitoneSurroundingsCull? cull)
        {
            if (!cull.HasValue) return true;
            return RadiusIsShared(RadiusTiles, cull.Value.RadiusTiles);
        }

        /// <summary>
        /// Whether two lots are within <paramref name="radiusTiles"/> of each other, measured as the
        /// gap between their rectangles.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A gap, not a centre distance, for the reason <see cref="SimitoneSurroundingsCull"/> gives:
        /// a cull radius is "so much neighbourhood past the property", and a radius that meant
        /// "centres within n tiles" would select a different set of lots for every lot size. A
        /// negative radius is treated as zero rather than as an error, because the only thing a
        /// caller can mean by one is "no neighbourhood at all", and that is a plan with no
        /// neighbours rather than a crash.
        /// </para>
        /// <para>
        /// <b>It is the facade cut's reading of the same sentence.</b>
        /// <see cref="SimitoneSurroundingsCull.CoversLot"/> asks the same question against a region
        /// built at a radius and clips the answer to the map, so a lot at exactly this radius is in
        /// one test and out of the other by a single tile of boundary. That difference is harmless -
        /// it can only add a lot to the sub-world side, which is the direction that does not draw
        /// anything twice - and it is asserted rather than left to a comment:
        /// <see cref="NeighbourhoodRenderDecision.SubWorldsAreInsideCut"/>.
        /// </para>
        /// </remarks>
        /// <param name="active">The lot the player is standing in.</param>
        /// <param name="other">The lot being considered, or <c>null</c>.</param>
        /// <param name="radiusTiles">How many tiles of gap are allowed.</param>
        public static bool WithinRadius(
            NeighbourhoodLot active, NeighbourhoodLot other, int radiusTiles)
        {
            if (active == null || other == null) return false;
            return SubWorldGapFor(active, other) <= Math.Max(0, radiusTiles);
        }

        /// <summary>
        /// The gap between two lots' rectangles in tiles - zero when they touch or overlap - and the
        /// one number the radius thresholds and the budget's nearest-first order are both measured
        /// against.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The Chebyshev gap, and why not the Euclidean one.</b> A radius in this file has always
        /// been a square: <see cref="WithinRadius"/> is four axis comparisons, so a lot diagonally
        /// placed is in at the same gap as one directly across the street. Making the budget's order
        /// use the same square measure is what lets <see cref="SelectionOrder"/> and
        /// <see cref="WithinRadius"/> be about one distance rather than two - a lot cannot be
        /// "closer" than the budget's ordering says and still be outside the radius, which would be
        /// a very confusing way for the two to disagree.
        /// </para>
        /// <para>
        /// A Euclidean distance would in any case be the wrong thing to order by, because a
        /// neighbourhood's lots are on a grid with streets between them: the diagonally-adjacent
        /// plot is not nearer to walk to than the one opposite, it is just further away in a
        /// straight line.
        /// </para>
        /// </remarks>
        /// <param name="active">The lot the player is standing in.</param>
        /// <param name="other">The lot being measured, or <c>null</c>.</param>
        public static int SubWorldGapFor(NeighbourhoodLot active, NeighbourhoodLot other)
        {
            if (active == null || other == null) return int.MaxValue;
            var aSize = Math.Max(0, active.Size);
            var oSize = Math.Max(0, other.Size);
            // The gap on one axis is how far apart the two rectangles are along it, zero when they
            // overlap, and the larger of the two axes is the square distance the radius speaks in.
            var dx = GapOnAxis(active.WorldX, aSize, other.WorldX, oSize);
            var dy = GapOnAxis(active.WorldY, aSize, other.WorldY, oSize);
            return Math.Max(dx, dy);
        }

        private static int GapOnAxis(int aMin, int aSize, int bMin, int bSize)
        {
            var gap = bMin - (aMin + aSize);
            var other = aMin - (bMin + bSize);
            if (other > gap) gap = other;
            return gap < 0 ? 0 : gap;
        }

        /// <summary>
        /// The nearest-first ordering the budget consumes: ascending
        /// <see cref="SubWorldGapFor"/>, then ascending lot number.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why nearest first.</b> Everything about a sub-world costs the player twice - once at
        /// the lot load, and every frame afterwards - and the player is standing at the centre of
        /// the region. The lots they can walk to are the ones they will look at; the ones across the
        /// map corner of the neighbour region are the ones they will not. Spending a full budget on the
        /// far ring first would be spending it on the part of the view nobody is looking at.
        /// </para>
        /// <para>
        /// <b>Why the lot number breaks ties.</b> Two plots either side of the same street have the
        /// same gap and neither is nearer. Sorting on the number makes the choice a function of the
        /// layout alone, so the same lot is deferred on every visit, on every machine, and after any
        /// re-entry to the neighbourhood - rather than whichever one the reachability flood-fill
        /// happened to reach first this time.
        /// </para>
        /// </remarks>
        /// <param name="active">The lot the player is standing in.</param>
        /// <param name="candidates">The neighbours to order. Sorted in place.</param>
        /// <param name="reachable">
        /// The lots the road graph reaches from <paramref name="active"/>, or <c>null</c>/empty for
        /// "no preference". Lots in this set sort ahead of lots outside it, before the gap is
        /// consulted, so a full budget spends itself on the lots the player can walk to first and
        /// only then on the in-radius houses the route could not reach. See
        /// <see cref="SimitoneSurroundingsPlan"/>'s selection rule for why both are candidates.
        /// </param>
        public static void OrderNearestFirst(
            NeighbourhoodLot active, List<SimitoneSurroundingsNeighbour> candidates,
            ISet<short> reachable = null)
        {
            if (candidates == null || candidates.Count < 2) return;
            candidates.Sort((a, b) =>
            {
                if (reachable != null)
                {
                    var ra = reachable.Contains(a.Lot.Number);
                    var rb = reachable.Contains(b.Lot.Number);
                    if (ra != rb) return ra ? -1 : 1;
                }
                var ga = SubWorldGapFor(active, a.Lot);
                var gb = SubWorldGapFor(active, b.Lot);
                if (ga != gb) return ga.CompareTo(gb);
                return a.Lot.Number.CompareTo(b.Lot.Number);
            });
        }

        /// <summary>
        /// Cuts an already-ordered candidate list to the budget, keeping the nearest-first prefix,
        /// and says which lots fell off the end.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nearest-first, and nothing else.</b> The order is
        /// <see cref="OrderNearestFirst"/>'s and the cut is a count, so the selected set is a
        /// prefix of one total order - never a per-lot fudge like "keep the ones with houses" or
        /// "keep every third". Two plans over the same layout therefore select the same lots, and a
        /// lot's fate depends on where the player is standing and not on the order a neighbour
        /// list was walked in.
        /// </para>
        /// <para>
        /// <b>What comes off the end is facades, not holes.</b> Every candidate is inside the
        /// drawn region by construction - that is what the radius is now - so the surplus is drawn
        /// by <see cref="NeighbourhoodRenderDecision"/> as a facade. The caller is told what was cut
        /// through <paramref name="deferred"/> so it can say so in the log; a plan that hid the
        /// surplus would leave a player looking at a flat house with no line saying why.
        /// </para>
        /// <para>
        /// A budget of zero or less stands nothing up at all, which is the honest answer for "build
        /// no neighbours" and is what a test wants when it is asking about the facade side of the
        /// decision. A budget of <see cref="MaxSubWorlds"/> or more takes everything.
        /// </para>
        /// </remarks>
        /// <param name="candidates">The ordered candidates. Truncated in place to the budget.</param>
        /// <param name="maxSubWorlds">How many may be kept.</param>
        /// <param name="deferred">
        /// Filled with the lots cut, nearest first, or <c>null</c> to discard them - in which case
        /// the caller has no record of them, which is why every production caller passes a list.
        /// </param>
        /// <returns>How many lots were cut.</returns>
        public static int ApplyBudget(
            List<SimitoneSurroundingsNeighbour> candidates, int maxSubWorlds,
            List<SimitoneSurroundingsNeighbour> deferred)
        {
            if (candidates == null) return 0;
            var keep = maxSubWorlds < 0 ? 0 : maxSubWorlds;
            if (candidates.Count <= keep)
            {
                if (deferred != null) deferred.Clear();
                return 0;
            }

            //The cut is taken from the tail of a nearest-first list, so what goes is always the
            //furthest ring rather than whichever entries happened to be last in the input.
            var cut = candidates.Count - keep;
            if (deferred != null)
            {
                deferred.Clear();
                deferred.AddRange(candidates.GetRange(keep, cut));
            }
            candidates.RemoveRange(keep, cut);
            return cut;
        }

        /// <summary>
        /// The plan for a lot, with no store behind it: every selected neighbour is built blank.
        /// The shape B2's first cut wants, and the one a test can call without a lot store.
        /// </summary>
        /// <param name="placement">The active lot's placement.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="radiusTiles">How many tiles of gap a neighbour may sit away.</param>
        public static SimitoneSurroundingsPlan For(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map,
            NeighbourhoodLayout layout, int radiusTiles)
        {
            return For(placement, map, layout, null, radiusTiles, null);
        }

        /// <summary>
        /// The plan for a lot, with a store behind it: a neighbour the store holds a hollow marshal
        /// for is built from that marshal's contents, and one it does not is built blank.
        /// </summary>
        /// <param name="placement">The active lot's placement.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="store">The neighbourhood's lot store, or <c>null</c>.</param>
        /// <param name="radiusTiles">How many tiles of gap a neighbour may sit away.</param>
        public static SimitoneSurroundingsPlan For(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map,
            NeighbourhoodLayout layout, LotStore store, int radiusTiles)
        {
            return For(placement, map, layout, store, radiusTiles, null);
        }

        /// <summary>
        /// The plan for a lot, with a store behind it and an explicit budget on how many
        /// sub-worlds it may stand up.
        /// </summary>
        /// <param name="placement">The active lot's placement.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="store">The neighbourhood's lot store, or <c>null</c>.</param>
        /// <param name="radiusTiles">How many tiles of gap a neighbour may sit away.</param>
        /// <param name="maxSubWorlds">
        /// How many real sub-worlds the plan may name; zero or less stands none up. The surplus is
        /// drawn as facades and recorded in <see cref="DeferredNumbers"/>.
        /// </param>
        /// <param name="arryPlanes">
        /// Where each neighbour's own <c>ARRY(0)</c> plane comes from, or <c>null</c> for
        /// <see cref="SimitoneArryPlanes.HouseFiles"/> - which is the shipped answer, because a
        /// caller that has not heard of height planes must still get its neighbours graded rather
        /// than flat. Pass <see cref="SimitoneArryPlanes.Pinned"/> to ask for the pinned path
        /// outright.
        /// </param>
        public static SimitoneSurroundingsPlan For(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map,
            NeighbourhoodLayout layout, LotStore store, int radiusTiles, int maxSubWorlds,
            SimitoneArryPlanes arryPlanes = null)
        {
            return For(placement, map, layout, store, radiusTiles, null, maxSubWorlds, arryPlanes);
        }

        /// <summary>
        /// The plan for a lot, asked of a query the caller already has.
        /// </summary>
        /// <remarks>
        /// Building a <see cref="NeighbourhoodQuery"/> rasterises the layout and flood-fills its
        /// road network, so a caller that has one - the world does, once, at load - should not pay
        /// for a second. The map is still passed in rather than taken out of the query, because a
        /// query does not hand its map back and the height field has to be read from the same
        /// instance the surround is drawn from.
        /// </remarks>
        /// <param name="placement">The active lot's placement.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="query">The query, or <c>null</c> to build one.</param>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="store">The neighbourhood's lot store, or <c>null</c>.</param>
        /// <param name="radiusTiles">How many tiles of gap a neighbour may sit away.</param>
        public static SimitoneSurroundingsPlan ForQuery(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map, NeighbourhoodQuery query,
            NeighbourhoodLayout layout, LotStore store, int radiusTiles)
        {
            return For(placement, map, layout, store, radiusTiles, query);
        }

        /// <summary>
        /// The plan for a lot, asked of a query the caller already has, with an explicit budget.
        /// This is the overload the lot view uses.
        /// </summary>
        /// <remarks>
        /// <b>And it grades the neighbours.</b> The lot view calls this with no height source, and
        /// the default is <see cref="SimitoneArryPlanes.HouseFiles"/> - each neighbour's own
        /// <c>HouseNN.iff</c> is opened and its <c>ARRY(0)</c> plane read, so a surrounding lot's
        /// ground is that lot's own graded field rather than the generated map's flat pin over its
        /// footprint. Pass <paramref name="arryPlanes"/> to say where the planes come from, or
        /// <see cref="SimitoneArryPlanes.Pinned"/> to ask for the old behaviour outright.
        /// </remarks>
        /// <param name="placement">The active lot's placement.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="query">The query, or <c>null</c> to build one.</param>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="store">The neighbourhood's lot store, or <c>null</c>.</param>
        /// <param name="radiusTiles">How many tiles of gap a neighbour may sit away.</param>
        /// <param name="maxSubWorlds">
        /// How many real sub-worlds the plan may name; zero or less stands none up. The surplus is
        /// drawn as facades and recorded in <see cref="DeferredNumbers"/>.
        /// </param>
        /// <param name="arryPlanes">
        /// Where each neighbour's own <c>ARRY(0)</c> plane comes from, or <c>null</c> for
        /// <see cref="SimitoneArryPlanes.HouseFiles"/>. See
        /// <see cref="For(SimitoneSurroundingsPlacement,NeighbourhoodMap,NeighbourhoodLayout,LotStore,int,int,SimitoneArryPlanes)"/>.
        /// </param>
        public static SimitoneSurroundingsPlan ForQuery(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map, NeighbourhoodQuery query,
            NeighbourhoodLayout layout, LotStore store, int radiusTiles, int maxSubWorlds,
            SimitoneArryPlanes arryPlanes = null)
        {
            return For(placement, map, layout, store, radiusTiles, query, maxSubWorlds, arryPlanes);
        }

        private static SimitoneSurroundingsPlan For(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map,
            NeighbourhoodLayout layout, LotStore store, int radiusTiles, NeighbourhoodQuery query,
            int maxSubWorlds = MaxSubWorlds, SimitoneArryPlanes arryPlanes = null)
        {
            //Resolved here rather than at the overloads, so every route into the plan gets the same
            //source and "no source" can only ever mean the shipped one. The plan reads files now -
            //one chunk per candidate, no world and no device - which is a change to what "pure" is
            //allowed to mean here, and it is a change the owner's "height maps of surrounding lots
            //are incorrect" required: the alternative is a parameter every existing caller has to
            //learn about before its neighbours stop being flat.
            var planes = arryPlanes ?? SimitoneArryPlanes.HouseFiles();
            var radius = Math.Max(0, radiusTiles);
            var budget = maxSubWorlds < 0 ? 0 : maxSubWorlds;
            if (placement.Lot == null)
            {
                return Invalid(radius, "the active lot has no placement, so it has no surroundings"
                    + " to plan");
            }
            if (layout == null)
            {
                return Invalid(radius, "there is no generated neighbourhood layout");
            }
            if (map == null)
            {
                return Invalid(radius, "there is no generated map");
            }

            var active = placement.Lot;

            // The active lot has to be a lot of this layout. Every offset in the plan is relative
            // to its rectangle and every reachability answer is keyed on its number, so a plan
            // built around a lot the layout does not contain would be a set of numbers about
            // nowhere. The failure names the lot, because it is a caller bug and not a state worth
            // degrading from.
            if (layout.FindLot(active.Number) == null)
            {
                return Invalid(radius, "lot " + active.Number
                    + " is not in this neighbourhood's generated map");
            }

            SimitoneNeighbourTerrain activeTerrain;
            string terrainReason;
            if (!SimitoneNeighbourTerrain.TryBuild(map, active, out activeTerrain, out terrainReason))
            {
                return Invalid(radius, "the active lot has no terrain: " + terrainReason);
            }

            // Reachability. A query that cannot be built is not fatal - the radius still answers
            // the question that matters, just more widely - and the plan records that it happened
            // rather than pretending the narrower answer is the one it used.
            var reachability = query;
            if (reachability == null)
            {
                try
                {
                    reachability = new NeighbourhoodQuery(layout, map);
                }
                catch (Exception)
                {
                    reachability = null;
                }
            }

            var warnings = new List<string>();
            var selected = new List<SimitoneSurroundingsNeighbour>();

            // The route is a preference, not a permission. `NeighboursOf` answers "whose door can be
            // walked to from this one's", which is not the same question as "may this lot be stood
            // up as a sub-world". In a generated neighbourhood the road graph reaches only the lots
            // whose driveways the generator joined to this lot's, and in the live case that was one
            // lot out of the eleven inside the radius - so tying the candidates to the route left ten
            // real houses as flat facades by accident. The rule below is the union of the two
            // sufficient reasons to build a lot: the route reaches it, or it is inside the radius
            // and has a house. The radius, the budget, the ground-drawn test and the terrain test are
            // all unchanged, so this widens the candidates by the in-radius houses the route missed
            // and by nothing else. Reachable lots are ordered first when the budget cuts (see below),
            // because a lot the player can walk to is the one worth spending the load on.
            var reachable = new HashSet<short>();
            if (reachability != null)
            {
                var route = reachability.NeighboursOf(active.Number);
                for (int i = 0; i < route.Count; i++)
                {
                    var candidate = layout.FindLot(route[i]);
                    if (candidate == null)
                    {
                        warnings.Add("lot " + route[i] + " is reachable but is not in the"
                            + " layout, so it was skipped");
                        continue;
                    }
                    reachable.Add(candidate.Number);
                    Consider(map, placement, store, active, candidate, radius, selected, warnings,
                        planes);
                }

                // Everything else inside the radius that has a house: the route did not reach it,
                // which is a fact about driveways rather than about the lot, so it is still a
                // neighbour. A bulldozed plot is not added here - it has no house, and the facade
                // side already draws nothing for it - and the active lot, the out-of-radius lots and
                // the off-ground lots are all refused by Consider, exactly as they are above.
                for (int i = 0; i < layout.Lots.Count; i++)
                {
                    var candidate = layout.Lots[i];
                    if (candidate == null || reachable.Contains(candidate.Number)) continue;
                    if (!candidate.HasHouse) continue;
                    Consider(map, placement, store, active, candidate, radius, selected, warnings,
                        planes);
                }
            }
            else
            {
                for (int i = 0; i < layout.Lots.Count; i++)
                {
                    Consider(map, placement, store, active, layout.Lots[i], radius, selected,
                        warnings, planes);
                }
            }

            // Nearest-first within each half, reachable half first, then the budget, then ascending
            // lot number. The order matters and is the whole of the coverage rule: the radius is the
            // neighbour region's own radius, so everything in `selected` is a lot the surround stands
            // up or builds a house for, and the only reason any of them does not become a real lot is
            // the count. A reachable lot is preferred because it is the one the player can walk to;
            // the route-blind tail follows the whole reachable set.
            OrderNearestFirst(active, selected, reachable);

            //The order is recorded *before* the cut, because it records the candidates rather than
            //the outcome: it is what lets a caller see that a lot was considered and lost, and it is
            //the same over the same inputs at any budget.
            var order = NumbersOf(selected);

            var deferred = new List<SimitoneSurroundingsNeighbour>();
            ApplyBudget(selected, budget, deferred);

            // Ascending lot number, whatever the two sources above were in. The layout is already
            // in this order so the sort is a second assertion of something true rather than
            // anything expensive, and it is the order B2's test list asks to be deterministic.
            selected.Sort(CompareByNumber);

            //The cut is said out loud. A plan that quietly stood up fewer lots than the region
            //holds is a street with a flat house in it and nothing in the log to say why.
            if (deferred.Count > 0)
            {
                warnings.Add(deferred.Count + " lot(s) inside the neighbour region are past the budget"
                    + " of " + budget + " sub-worlds and will be drawn as facades instead:"
                    + " " + DescribeNumbers(deferred) + ".");
            }

            return new SimitoneSurroundingsPlan(active, activeTerrain, radius,
                reachability != null,
                new ReadOnlyCollection<SimitoneSurroundingsNeighbour>(selected),
                new ReadOnlyCollection<string>(warnings), null, store,
                order,
                NumbersOf(deferred),
                budget,
                planes);
        }

        /// <summary>The lot numbers of a neighbour list, in the list's own order.</summary>
        private static short[] NumbersOf(List<SimitoneSurroundingsNeighbour> neighbours)
        {
            if (neighbours == null || neighbours.Count == 0) return NoLotNumbers;
            var numbers = new short[neighbours.Count];
            for (int i = 0; i < neighbours.Count; i++) numbers[i] = neighbours[i].Lot.Number;
            return numbers;
        }

        /// <summary>One line naming a list of lots, for a warning.</summary>
        private static string DescribeNumbers(List<SimitoneSurroundingsNeighbour> neighbours)
        {
            if (neighbours == null || neighbours.Count == 0) return "none";
            var sb = new StringBuilder();
            for (int i = 0; i < neighbours.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(neighbours[i].Lot.Number.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static int CompareByNumber(
            SimitoneSurroundingsNeighbour a, SimitoneSurroundingsNeighbour b)
        {
            return a.Lot.Number.CompareTo(b.Lot.Number);
        }

        /// <summary>
        /// Whether this lot belongs in the plan, and if so what its entry is. One place, because
        /// "is it the lot the player is in", "is it inside the radius" and "can the map answer for
        /// it" are three separate refusals that each want their own sentence.
        /// </summary>
        /// <param name="arryPlanes">
        /// Where this lot's own <c>ARRY(0)</c> plane comes from, or <c>null</c> for none - which is
        /// the pinned median, and <em>not</em> a fourth refusal: a lot the map describes perfectly
        /// well is still a lot, flat or not.
        /// </param>
        private static void Consider(
            NeighbourhoodMap map, SimitoneSurroundingsPlacement placement, LotStore store,
            NeighbourhoodLot active, NeighbourhoodLot candidate, int radius,
            List<SimitoneSurroundingsNeighbour> selected, List<string> warnings,
            SimitoneArryPlanes arryPlanes)
        {
            if (candidate == null) return;
            // The lot the player is standing in is not its own neighbour. The lot view is already
            // drawing it, in full, from its own house file - the same exclusion
            // SimitoneSurroundingsCull.LotsInRange makes for facades, and for the same reason.
            if (candidate.Number == active.Number) return;
            if (!WithinRadius(active, candidate, radius)) return;

            //And the fourth refusal, which is the newest: a lot whose base tile carries no drawn
            //ground. A sub-world is a whole lot standing at a map tile, and that tile is where its
            //own origin and its own base height come from, so a lot the mesh leaves empty would be
            //a real lot standing in the void at the island's edge (§9.1). It is refused here as
            //well as in CoversLot rather than only there, because this list is the one that decides
            //what is stood up: CoversLot is the facade side's question and this is the sub-world
            //side's, and both have to answer it or a lot can be refused as a facade and built as a
            //sub-world. The wording is the same refusal a player would need to hear.
            if (!SimitoneSurroundingsPlacement.GroundIsDrawnUnder(map, candidate))
            {
                warnings.Add("lot " + candidate.Number + " was skipped: the map draws no ground"
                    + " under it (its base tile x[" + candidate.WorldX + "," + (candidate.WorldX + 1)
                    + ") z[" + candidate.WorldY + "," + (candidate.WorldY + 1)
                    + ") is outside the ground the mesh draws, and "
                    + SimitoneSurroundingsPlacement.GroundlessTilesOf(map, candidate)
                    + " of its " + (Math.Max(0, candidate.Size) * Math.Max(0, candidate.Size))
                    + " footprint tiles are outside it too");
                return;
            }

            // The lot's own graded ground, or the generated map's flat pin over its rectangle. Asked
            // for before the terrain is built because the terrain and the merged field are the two
            // things the plane decides, and both of them are wanted here: the sixteen samples the
            // helpers hang the lot at, and the size-squared field the bridge copies over its
            // Terrain.Heights. `SimitoneArryAltitude.None` is the honest "no plane" answer and every
            // reader of it already means "the map's pinned median", so nothing downstream has to
            // know this was ever a question.
            var arry = SimitoneArryAltitude.None;
            if (arryPlanes != null)
            {
                SimitoneArryAltitude read;
                string arryReason;
                arryPlanes.TryGet(candidate, out read, out arryReason);
                arry = read;
            }

            SimitoneNeighbourTerrain terrain;
            string reason;
            if (!SimitoneNeighbourTerrain.TryBuild(map, candidate, arry, out terrain, out reason))
            {
                // A lot the map cannot answer about is skipped, not fatal. B2's acceptance
                // criterion is that a neighbour which will not build costs that neighbour and
                // nothing else, and this is the first place it can go wrong.
                warnings.Add("lot " + candidate.Number + " was skipped: " + reason);
                return;
            }

            VMHollowMarshal hollow = null;
            if (store != null) store.TryGetHollow(candidate.Number, out hollow);

            // The two placement decisions, each through the one function that owns it, so a test can
            // assert the rule rather than a number that came out of a plan. The position is the raw
            // tile delta SubDraw wants; the direction is the layout's door side put into TSO's
            // corner vocabulary, which is not the same integer.
            //
            // And the third decision, which is the one that makes the shared map one ground: this
            // lot's own altitude field, read out of the surround's own height function rather than
            // out of the helpers' 4x4 interpolation of the same map. It is computed here, where the
            // map, the active lot's placement and both base levels are all in scope, because the
            // bridge that consumes it has none of them - and with the lot's own plane, so the field
            // is the lot's real grading measured from its own median rather than a plane pinned at
            // it. See SimitoneSurroundingsPlacement.SubWorldAltitudesFor for that subtraction.
            var baseAlt = BaseAltitudeFor(placement.ActiveBaseLevel, terrain.BaseLevel);
            selected.Add(SimitoneSurroundingsNeighbour.Create(candidate, terrain, hollow,
                SubWorldPositionFor(active, candidate),
                LotDirectionFor(candidate.DoorSide),
                SimitoneSurroundingsPlacement.SubWorldAltitudesFor(map, placement, candidate,
                    SubWorldSizeFor(candidate), baseAlt, arry),
                arry));
        }

        private static SimitoneSurroundingsPlan Invalid(int radiusTiles, string reason,
            int maxSubWorlds = MaxSubWorlds)
        {
            return new SimitoneSurroundingsPlan(null, default(SimitoneNeighbourTerrain),
                Math.Max(0, radiusTiles), false, NoNeighbours, NoWarnings, reason, null,
                NoLotNumbers, NoLotNumbers, maxSubWorlds);
        }

        /// <summary>
        /// One line for the log: the active lot, how far the radius reaches, how many neighbours
        /// that produced, how many of them have contents, and anything that was skipped. Written to
        /// be read beside the surround's own <c>Describe()</c>, because the two together are what a
        /// player reporting "the house next door is missing" needs.
        /// </summary>
        public string Describe()
        {
            if (!IsValid) return "no neighbour sub-worlds: " + Failure;
            var sb = new StringBuilder();
            var merged = 0;
            var graded = 0;
            for (int i = 0; i < Neighbours.Count; i++)
            {
                if (Neighbours[i].HasMergedGround) merged++;
                if (Neighbours[i].IsGradedGround) graded++;
            }
            sb.Append(Neighbours.Count)
              .Append(Neighbours.Count == 1 ? " neighbour sub-world" : " neighbour sub-worlds")
              .Append(" around lot ").Append(ActiveLot.Number)
              .Append(" within ").Append(RadiusTiles).Append(" tile(s)")
              .Append(", base level ").Append(ActiveBaseLevel)
              .Append(", ").Append(merged).Append(" of them on the surround's own ground")
              .Append(", ").Append(graded).Append(" graded from their own ARRY(0)")
              .Append(", ").Append(HollowCount).Append(" hollow, ")
              .Append(Neighbours.Count - HollowCount).Append(" blank")
              .Append(", budget ").Append(SubWorldBudget)
              .Append(DeferredCount > 0
                  ? ", " + DeferredCount + " left to the facade pass"
                  : string.Empty)
              .Append(RoadReachability ? string.Empty : " (no road reachability: radius only)");
            for (int i = 0; i < Warnings.Count; i++)
            {
                sb.Append("\n  ").Append(Warnings[i]);
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            return Describe();
        }
    }
}
