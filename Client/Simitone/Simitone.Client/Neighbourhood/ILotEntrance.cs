/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Where one lot's door meets the generated world, as the five facts anything that places a
    /// sim at a lot or routes one there needs: which lot, which tile, and which way the lot faces.
    ///
    /// <para>
    /// <b>Every coordinate here is map-tile space</b> - the same integers
    /// <see cref="NeighbourhoodMap.Index"/> takes, so <c>X</c>/<c>Y</c> can be used against any
    /// generated array without a conversion. Not lot-local, not pixels: turning tiles into pixels
    /// is the camera's job, and doing it here would put one scale into a question that has none.
    /// </para>
    ///
    /// <para>
    /// <b>Why an interface at all.</b> Part A's answer already exists and is a plain struct -
    /// <see cref="NeighbourhoodEntrance"/>, which <see cref="RoadGraph"/> produces - and it is
    /// deliberately <em>not</em> named <c>ILotEntrance</c>, because Part A's types do not
    /// implement interfaces it invented for a later scope. This is that name, with the same
    /// fields, and it exists so Part B can ask for "a lot's door" without the generator or the
    /// road graph knowing that Part B exists: a persisted sidecar's entrance table, a
    /// hand-authored neighbourhood, and a saved game can each answer this differently without
    /// changing <see cref="NeighbourhoodQuery"/> or <see cref="RoadGraph"/>.
    /// </para>
    ///
    /// <para>
    /// <b>What the facing means.</b> <see cref="FacingX"/>/<see cref="FacingY"/> are an outward
    /// normal, exactly as on <see cref="NeighbourhoodEntrance"/>: one of them is 0 and the other
    /// is +1 or -1, and the pair is the direction a driveway leaves the lot in. Stepping
    /// <em>inward</em> from the door tile - <c>X - FacingX</c>, <c>Y - FacingY</c> - therefore
    /// lands on the lot's own border, which is the tile a sim standing at the door is on the
    /// inside of. See <see cref="LotEntrance.ClampFacing"/> for the stored-integer form.
    /// </para>
    /// </summary>
    public interface ILotEntrance
    {
        /// <summary>The lot this entrance belongs to, in the same numbering as
        /// <see cref="NeighbourhoodLot.Number"/>. Never 0, and never 99 - the lot-location parser
        /// drops 99 as its sentinel and <see cref="INeighbourhoodWorld.ActiveLotNumber"/> uses 0
        /// for "the player is in no lot".</summary>
        int LotNumber { get; }

        /// <summary>The tile the driveway starts on, in map-tile space.</summary>
        int X { get; }

        /// <summary>The tile's y, in map-tile space.</summary>
        int Y { get; }

        /// <summary>The outward normal's x component: +1, -1, or 0 for a horizontal edge.</summary>
        int FacingX { get; }

        /// <summary>The outward normal's y component: +1, -1, or 0.</summary>
        int FacingY { get; }
    }

    /// <summary>
    /// Part A's <see cref="ILotEntrance"/>: a value copy of
    /// <see cref="NeighbourhoodEntrance"/>, plus the promotions that get one out of the two
    /// places Part A records a door.
    ///
    /// <para>
    /// There is a <see cref="NeighbourhoodEntrance"/> in <see cref="RoadGraph.Entrances"/> and
    /// there may be a <see cref="NeighbourhoodLotEntrance"/> on a lot loaded from a sidecar. They
    /// are the same fact in two shapes, and the shape difference is a nuisance at every call
    /// site: the graph's carries the <em>outward normal</em> while the layout's carries the
    /// <em>side index</em> it was written from. <see cref="From(NeighbourhoodEntrance)"/> and
    /// <see cref="From(NeighbourhoodLot)"/> are the two adapters, and they are named
    /// <c>From</c> rather than being constructors because neither source type is this one - a
    /// constructor would read as if this type were the thing being converted from.
    /// </para>
    ///
    /// <para>
    /// It is a struct with value equality so it can go in a dictionary, sit in a
    /// <c>ReadOnlyCollection</c> without boxing on every read, and be compared in a test without
    /// a bespoke assertion. Nothing here allocates.
    /// </para>
    /// </summary>
    public readonly struct LotEntrance : ILotEntrance, IEquatable<LotEntrance>
    {
        /// <inheritdoc/>
        public int LotNumber { get; }

        /// <inheritdoc/>
        public int X { get; }

        /// <inheritdoc/>
        public int Y { get; }

        /// <inheritdoc/>
        public int FacingX { get; }

        /// <inheritdoc/>
        public int FacingY { get; }

        /// <summary>Creates an entrance.</summary>
        /// <param name="lotNumber">The lot this door belongs to.</param>
        /// <param name="x">The tile the driveway starts on, in map-tile space.</param>
        /// <param name="y">The tile's y, in map-tile space.</param>
        /// <param name="facingX">The outward normal's x component.</param>
        /// <param name="facingY">The outward normal's y component.</param>
        public LotEntrance(int lotNumber, int x, int y, int facingX, int facingY)
        {
            LotNumber = lotNumber;
            X = x;
            Y = y;
            FacingX = facingX;
            FacingY = facingY;
        }

        /// <summary>
        /// Does this name a lot? False for <c>default(LotEntrance)</c>, which is all zeros and
        /// therefore claims lot 0 - a number that is not a lot.
        /// </summary>
        /// <remarks>
        /// The check is on the lot number rather than on "any field is non-zero" so a door at
        /// tile (0,0) facing north is still recognised: (0,0) is a real tile on a real map.
        /// </remarks>
        public bool IsSet
        {
            get { return LotNumber != 0; }
        }

        /// <summary>
        /// The road graph's entrance, as this type. A field-for-field copy - the graph's
        /// <see cref="NeighbourhoodEntrance.Side"/> has no counterpart here and is dropped, which
        /// is deliberate: an interface that exposed it would be carrying a field for consumers
        /// that only have tiles.
        /// </summary>
        /// <param name="entrance">A <see cref="NeighbourhoodEntrance"/>.</param>
        public static LotEntrance From(NeighbourhoodEntrance entrance)
        {
            return new LotEntrance(
                entrance.LotNumber, entrance.X, entrance.Y, entrance.FacingX, entrance.FacingY);
        }

        /// <summary>
        /// A lot's own stored entrance, as this type.
        /// </summary>
        /// <remarks>
        /// The layout's <see cref="NeighbourhoodLotEntrance"/> records the side the door is on
        /// rather than a normal, so the conversion is the one
        /// <see cref="NeighbourhoodDoorSide"/> exists for. A side outside 0..3 - a hand-edited
        /// sidecar, a forward field written by a build that meant something else - folds to north
        /// through <see cref="ClampFacing"/> rather than being trusted, and rather than being
        /// cast to a <c>byte</c> first, which would turn 256 into "east".
        /// </remarks>
        /// <param name="lot">The lot, which must carry an <see cref="NeighbourhoodLot.Entrance"/>.</param>
        /// <exception cref="ArgumentNullException">The lot is null.</exception>
        /// <exception cref="InvalidOperationException">The lot carries no entrance.</exception>
        public static LotEntrance From(NeighbourhoodLot lot)
        {
            if (lot == null) throw new ArgumentNullException(nameof(lot));

            LotEntrance entrance;
            if (!TryFrom(lot, out entrance))
            {
                throw new InvalidOperationException(
                    "lot " + lot.Number + " carries no entrance, so there is nothing to promote;"
                    + " use TryFrom.");
            }
            return entrance;
        }

        /// <summary>
        /// <see cref="From(NeighbourhoodLot)"/> for the two ways it can decline: no lot, or a lot
        /// whose entrance has not been computed. A missing entrance is a normal state - it is
        /// <c>null</c> in every Part A sidecar - so a caller reading a lot record wants this
        /// rather than an exception.
        /// </summary>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="entrance">The promoted entrance, when there is one.</param>
        /// <returns>Whether the lot carries an entrance.</returns>
        public static bool TryFrom(NeighbourhoodLot lot, out LotEntrance entrance)
        {
            if (lot == null || !lot.Entrance.HasValue)
            {
                entrance = default(LotEntrance);
                return false;
            }

            var own = lot.Entrance.Value;
            var side = ClampFacing(own.Facing);
            entrance = new LotEntrance(
                lot.Number, own.WorldX, own.WorldY,
                NeighbourhoodDoorSide.DeltaX(side), NeighbourhoodDoorSide.DeltaY(side));
            return true;
        }

        /// <summary>
        /// Folds a stored facing into the four real sides, 0..3.
        /// </summary>
        /// <remarks>
        /// <see cref="NeighbourhoodDoorSide.Clamp"/> does this job for a <c>byte</c>, and a side
        /// read out of a sidecar arrives as an <c>int</c>. Casting that to a <c>byte</c> first
        /// would wrap rather than clamp - 256 would become 0 and 260 would become 4 - so the
        /// range test happens on the <c>int</c> and an out-of-range value becomes north, which is
        /// what <see cref="NeighbourhoodDoorSide.Clamp"/> also does with a byte it cannot read.
        /// </remarks>
        /// <param name="facing">A side index, possibly out of range or negative.</param>
        public static byte ClampFacing(int facing)
        {
            if (facing >= NeighbourhoodDoorSide.North && facing <= NeighbourhoodDoorSide.West)
            {
                return (byte)facing;
            }
            return NeighbourhoodDoorSide.North;
        }

        /// <summary>Two entrances are equal when all five fields are.</summary>
        /// <param name="other">The entrance to compare with.</param>
        public bool Equals(LotEntrance other)
        {
            return LotNumber == other.LotNumber
                && X == other.X
                && Y == other.Y
                && FacingX == other.FacingX
                && FacingY == other.FacingY;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is LotEntrance && Equals((LotEntrance)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            //Five ints folded together. Not the point of a contest for a handful of doors, but it
            //has to agree with Equals and it has to not be a constant, so a dictionary of these
            //behaves.
            unchecked
            {
                var hash = LotNumber;
                hash = (hash * 397) ^ X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ FacingX;
                hash = (hash * 397) ^ FacingY;
                return hash;
            }
        }

        /// <summary>Two entrances are equal when all five fields are.</summary>
        /// <param name="a">One entrance.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(LotEntrance a, LotEntrance b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One entrance.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(LotEntrance a, LotEntrance b)
        {
            return !a.Equals(b);
        }

        /// <summary>
        /// One line for a log or a failing assertion, in the same wording
        /// <see cref="NeighbourhoodEntrance"/> uses so the two can be read against each other.
        /// </summary>
        public override string ToString()
        {
            return "lot " + LotNumber + " door at (" + X + "," + Y + ") facing ("
                + FacingX + "," + FacingY + ")";
        }
    }
}
