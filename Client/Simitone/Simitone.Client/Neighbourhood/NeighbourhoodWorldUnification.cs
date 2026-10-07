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
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// How a sim gets from a lot to another lot, as decided by
    /// <see cref="NeighbourhoodWorldUnification"/>.
    ///
    /// <para>
    /// §5.1's rule, as three values rather than a yes/no: the handoff keeps one generated map
    /// per mode and says cross-mode travel is "a transition, like the existing travel (a
    /// taxi/car between areas), not walking". This is the type that decision is expressed in, so
    /// that a caller routing a sim asks a question with an answer and not a question with a
    /// comment.
    /// </para>
    /// </summary>
    public enum NeighbourhoodTravelKind
    {
        /// <summary>
        /// The question could not be answered - a mode is not in this slot's space, or the space
        /// was refused. The caller falls back to whatever it did before any of this existed.
        /// </summary>
        None = 0,

        /// <summary>
        /// The sim walks. Either both lots are on one mode's road graph, or two regions share an
        /// edge in the slot space, which is the only thing that makes a cross-mode walk
        /// meaningful.
        /// </summary>
        Walk = 1,

        /// <summary>
        /// The sim is carried: a lot switch, a wipe, a taxi or a car - the same path a
        /// neighbourhood switch already takes. §5.1, and the default for every pair of modes
        /// whose regions do not touch.
        /// </summary>
        Transition = 2
    }

    /// <summary>
    /// One mode's half of a neighbourhood slot: the layout and the raster that were generated for
    /// it, and nothing else. The input to <see cref="NeighbourhoodWorldUnification"/>.
    ///
    /// <para>
    /// <b>Why a mode and a slot are separate here, when the rest of the code says otherwise.</b>
    /// Part A and everything above it stores one generated world per <em>slot</em> and puts the
    /// view id in the slot field - <see cref="NeighbourhoodLayout.Slot"/> holds
    /// <c>CurrentNeighborhoodMode</c>, and <c>NeighborhoodService</c> invalidates by that value.
    /// That is one world per view. This type is the other half of §13 open question 1: it says
    /// that a slot is the folder (all the <c>HouseNN.iff</c> files live in one
    /// <c>UserData</c>) and a mode is a view inside it, so a caller gathering one
    /// <see cref="NeighbourhoodModeWorld"/> per view passes the <em>same</em> slot to all of
    /// them. Nothing above is changed by this, which is the point: the unification is a new
    /// question asked of the existing data, not a new source of it.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodModeWorld
    {
        /// <summary>
        /// A mode's world. Either half may be <c>null</c> - a world whose raster failed to
        /// generate still has a layout, and a layout with no raster is what a cache hit before the
        /// worker lands looks like - but not both: a mode with neither has no size to place and
        /// the unification refuses it with a sentence saying so.
        /// </summary>
        /// <param name="mode">The view id: 1-8, per <see cref="NeighbourhoodModeMap"/>.</param>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="map">The generated raster, or <c>null</c>.</param>
        public NeighbourhoodModeWorld(int mode, NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            Mode = mode;
            Layout = layout;
            Map = map;
        }

        /// <summary>The view id this world was generated for. See the type remarks.</summary>
        public int Mode { get; }

        /// <summary>The packed layout, or <c>null</c>.</summary>
        public NeighbourhoodLayout Layout { get; }

        /// <summary>The generated raster, or <c>null</c>.</summary>
        public NeighbourhoodMap Map { get; }

        /// <summary>
        /// The side of the generated map, in tiles: the raster's own width where there is one, and
        /// otherwise the layout's declared map size. <c>0</c> when this world has neither, which
        /// is the case <see cref="NeighbourhoodWorldUnification"/> refuses.
        /// </summary>
        public int MapSize
        {
            get
            {
                if (Map != null) return Map.Width;
                return Layout == null ? 0 : Layout.MapSize;
            }
        }

        /// <summary>Whether there is anything at all to place. See the constructor.</summary>
        public bool HasWorld
        {
            get { return Map != null || Layout != null; }
        }

        /// <summary>
        /// The mode a lot number belongs to, as this type's mode. A straight
        /// <see cref="NeighbourhoodModeMap.ForHouse"/>, named here so a caller gathering a slot's
        /// worlds does not have to know that the mode of a lot is a lookup in the mode map.
        /// </summary>
        /// <param name="lotNumber">The lot the player is standing on.</param>
        public static int ModeForLot(int lotNumber)
        {
            return NeighbourhoodModeMap.ForHouse((short)lotNumber);
        }

        public override string ToString()
        {
            return "mode " + Mode + " " + MapSize + "x" + MapSize
                + " (" + (Layout?.LotCount ?? 0) + " lot(s)" + (Map == null ? ", no raster" : string.Empty) + ")";
        }
    }

    /// <summary>
    /// One mode's rectangle inside a slot's shared coordinate space, and the arithmetic that gets
    /// between that rectangle and the mode's own raster.
    ///
    /// <para>
    /// <b>The model this file settles on.</b> Per-mode rasters stay exactly as generated - nothing
    /// is regenerated, resized, resampled or copied - and each one is given a
    /// <em>window</em> in a larger, purely virtual slot space. A shared world therefore costs
    /// arithmetic rather than a 2560x512 raster, and the alternative (blitting five 512x512 maps
    /// into one bigger map) is refused for the reason §13 gives: it is a much larger change, it
    /// contradicts decision 4, and it buys nothing a query over five windows cannot.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodModeRegion
    {
        internal NeighbourhoodModeRegion(int mode, int offsetX, int offsetY, int size,
            NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            Mode = mode;
            OffsetX = offsetX;
            OffsetY = offsetY;
            Size = size;
            Layout = layout;
            Map = map;
            Region = new NeighbourhoodExtent(offsetX, offsetY, offsetX + size, offsetY + size);
        }

        /// <summary>The view id this region is.</summary>
        public int Mode { get; }

        /// <summary>Where this region's own tile (0, 0) sits in the shared slot space.</summary>
        public int OffsetX { get; }

        /// <summary>Where this region's own tile (0, 0) sits in the shared slot space.</summary>
        public int OffsetY { get; }

        /// <summary>Side of the region, in tiles. Equal for every region in one space.</summary>
        public int Size { get; }

        /// <summary>The region's box in the shared slot space. Half-open, like every extent here.</summary>
        public NeighbourhoodExtent Region { get; }

        /// <summary>The packed layout this window shows, or <c>null</c>.</summary>
        public NeighbourhoodLayout Layout { get; }

        /// <summary>The raster this window shows, or <c>null</c>.</summary>
        public NeighbourhoodMap Map { get; }

        /// <summary>Whether there is a raster behind the window.</summary>
        public bool HasRaster
        {
            get { return Map != null; }
        }

        /// <summary>How many lots this mode places.</summary>
        public int LotCount
        {
            get { return Layout == null ? 0 : Layout.LotCount; }
        }

        /// <summary>A tile of this mode's own raster, in the shared slot space.</summary>
        /// <param name="localX">Tile x inside the mode's own map.</param>
        public int SlotX(int localX)
        {
            return OffsetX + localX;
        }

        /// <summary>A tile of this mode's own raster, in the shared slot space.</summary>
        /// <param name="localY">Tile y inside the mode's own map.</param>
        public int SlotY(int localY)
        {
            return OffsetY + localY;
        }

        /// <summary>A tile of the shared slot space, in this mode's own raster. May be negative.</summary>
        /// <param name="slotX">Tile x in the shared space.</param>
        public int LocalX(int slotX)
        {
            return slotX - OffsetX;
        }

        /// <summary>A tile of the shared slot space, in this mode's own raster. May be negative.</summary>
        /// <param name="slotY">Tile y in the shared space.</param>
        public int LocalY(int slotY)
        {
            return slotY - OffsetY;
        }

        /// <summary>Whether a shared-space tile is inside this window.</summary>
        /// <param name="slotX">Tile x in the shared space.</param>
        /// <param name="slotY">Tile y in the shared space.</param>
        public bool Contains(int slotX, int slotY)
        {
            return Region.Contains(slotX, slotY);
        }

        /// <summary>
        /// A box in this mode's own raster, moved into the shared slot space. The one direction
        /// everything spatial goes: a lot's rectangle, an extent, a camera focus.
        /// </summary>
        /// <param name="local">A box in mode-local tile space.</param>
        public NeighbourhoodExtent ToSlot(NeighbourhoodExtent local)
        {
            return new NeighbourhoodExtent(
                local.MinX + OffsetX, local.MinY + OffsetY,
                local.MaxX + OffsetX, local.MaxY + OffsetY);
        }

        /// <summary>A box in the shared slot space, moved into this mode's own raster.</summary>
        /// <param name="slot">A box in shared slot space.</param>
        public NeighbourhoodExtent ToLocal(NeighbourhoodExtent slot)
        {
            return new NeighbourhoodExtent(
                slot.MinX - OffsetX, slot.MinY - OffsetY,
                slot.MaxX - OffsetX, slot.MaxY - OffsetY);
        }

        /// <summary>The lot with this number in this mode, or <c>null</c>.</summary>
        /// <param name="lotNumber">The lot number.</param>
        public NeighbourhoodLot FindLot(int lotNumber)
        {
            return Layout == null ? null : Layout.FindLot(lotNumber);
        }

        /// <summary>
        /// Which lot covers a shared-space tile, or <c>null</c> for connective ground and for a
        /// tile in another mode's window.
        /// </summary>
        /// <param name="slotX">Tile x in the shared space.</param>
        /// <param name="slotY">Tile y in the shared space.</param>
        public NeighbourhoodLot LotAt(int slotX, int slotY)
        {
            if (Layout == null || !Contains(slotX, slotY)) return null;
            return Layout.LotAt(LocalX(slotX), LocalY(slotY));
        }

        /// <summary>
        /// This mode's lot, placed in the shared slot space. <c>false</c> when the lot is not this
        /// mode's, which is different from a refusal: the lot belongs to some other window, and
        /// <see cref="NeighbourhoodWorldUnification.TryMapLot(int, out NeighbourhoodSharedLot)"/>
        /// is the thing that goes looking for it.
        /// </summary>
        /// <param name="lotNumber">The lot number.</param>
        /// <param name="shared">The lot in both spaces, on success.</param>
        public bool TryMapLot(int lotNumber, out NeighbourhoodSharedLot shared)
        {
            shared = NeighbourhoodSharedLot.None;
            var lot = FindLot(lotNumber);
            if (lot == null) return false;
            shared = new NeighbourhoodSharedLot(Mode, lot, OffsetX, OffsetY);
            return true;
        }

        /// <summary>One line for a log: which mode, where its window is, what is in it.</summary>
        public string Describe()
        {
            return "mode " + Mode + " at offset (" + OffsetX + "," + OffsetY + ") " + Size + "x" + Size
                + ", " + LotCount + " lot(s)" + (HasRaster ? string.Empty : ", no raster");
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// One lot, in both spaces at once: where the mode's own raster has it and where the shared
    /// slot space has it. The object the lot-to-shared-map mapping produces, and the one a camera
    /// or a <c>SubWorldComponent.GlobalPosition</c> needs rather than re-deriving.
    /// </summary>
    public readonly struct NeighbourhoodSharedLot
    {
        internal static readonly NeighbourhoodSharedLot None =
            new NeighbourhoodSharedLot(0, null, 0, 0);

        internal NeighbourhoodSharedLot(int mode, NeighbourhoodLot lot, int offsetX, int offsetY)
        {
            Mode = mode;
            Lot = lot;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        /// <summary>The view this lot is in.</summary>
        public int Mode { get; }

        /// <summary>The lot as the layout records it, or <c>null</c> on the <c>None</c> value.</summary>
        public NeighbourhoodLot Lot { get; }

        /// <summary>The window's offset, repeated here so a consumer holding only this needs nothing else.</summary>
        public int OffsetX { get; }

        /// <summary>The window's offset, repeated here so a consumer holding only this needs nothing else.</summary>
        public int OffsetY { get; }

        /// <summary>Whether this is a real lot rather than the <c>None</c> value.</summary>
        public bool IsValid
        {
            get { return Lot != null; }
        }

        /// <summary>The lot number, or 0 on the <c>None</c> value.</summary>
        public int Number
        {
            get { return Lot == null ? 0 : Lot.Number; }
        }

        /// <summary>The lot's side in tiles, or 0 on the <c>None</c> value.</summary>
        public int Size
        {
            get { return Lot == null ? 0 : Lot.Size; }
        }

        /// <summary>The lot's origin in its own mode's raster, unchanged from the layout.</summary>
        public int LocalX
        {
            get { return Lot == null ? 0 : Lot.WorldX; }
        }

        /// <summary>The lot's origin in its own mode's raster, unchanged from the layout.</summary>
        public int LocalY
        {
            get { return Lot == null ? 0 : Lot.WorldY; }
        }

        /// <summary>The lot's origin in the shared slot space.</summary>
        public int SlotX
        {
            get { return LocalX + OffsetX; }
        }

        /// <summary>The lot's origin in the shared slot space.</summary>
        public int SlotY
        {
            get { return LocalY + OffsetY; }
        }

        /// <summary>The lot's footprint in its own mode's raster.</summary>
        public NeighbourhoodExtent LocalExtent
        {
            get
            {
                if (Lot == null) return NeighbourhoodExtent.Empty;
                return new NeighbourhoodExtent(Lot.WorldX, Lot.WorldY,
                    Lot.WorldX + Lot.Size, Lot.WorldY + Lot.Size);
            }
        }

        /// <summary>The same footprint in the shared slot space.</summary>
        public NeighbourhoodExtent SlotExtent
        {
            get
            {
                if (Lot == null) return NeighbourhoodExtent.Empty;
                return new NeighbourhoodExtent(SlotX, SlotY, SlotX + Lot.Size, SlotY + Lot.Size);
            }
        }

        /// <summary>One line for a log.</summary>
        public string Describe()
        {
            if (Lot == null) return "no lot";
            return "lot " + Number + " of mode " + Mode
                + " at (" + LocalX + "," + LocalY + ") locally, (" + SlotX + "," + SlotY + ") in the slot";
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// The answer to "how does a sim get from here to there across this slot", with the reason it
    /// is that way attached. A value, because the decision is made once by
    /// <see cref="NeighbourhoodWorldUnification"/> and read by a lot of code - B3's travel path,
    /// B4's transit lot, a debug overlay - that must not each re-derive it.
    /// </summary>
    public readonly struct NeighbourhoodModeTransition
    {
        internal NeighbourhoodModeTransition(int fromMode, int toMode, NeighbourhoodTravelKind kind,
            bool crossMode, int gapTiles, NeighbourhoodExtent sharedEdge, string reason)
        {
            FromMode = fromMode;
            ToMode = toMode;
            Kind = kind;
            IsCrossMode = crossMode;
            GapTiles = gapTiles;
            SharedEdge = sharedEdge;
            Reason = reason;
        }

        /// <summary>The view the sim is leaving.</summary>
        public int FromMode { get; }

        /// <summary>The view the sim is arriving in.</summary>
        public int ToMode { get; }

        /// <summary>Walk, or carried.</summary>
        public NeighbourhoodTravelKind Kind { get; }

        /// <summary>Whether the two ends are in different views.</summary>
        public bool IsCrossMode { get; }

        /// <summary>
        /// How far apart the two regions are, in tiles. Zero when they touch, which is the only
        /// thing that turns a cross-mode hop into a walk.
        /// </summary>
        public int GapTiles { get; }

        /// <summary>
        /// Where the two regions meet: the zero-width or zero-height extent they share along an
        /// edge or a corner. <see cref="NeighbourhoodExtent.Empty"/> when they do not touch, which
        /// is the ordinary case and the reason cross-mode travel is a transition.
        /// </summary>
        public NeighbourhoodExtent SharedEdge { get; }

        /// <summary>The decision in one sentence, for a log line and for a reader of this file.</summary>
        public string Reason { get; }

        /// <summary>One line for a log.</summary>
        public string Describe()
        {
            return "mode " + FromMode + " -> mode " + ToMode + ": " + Kind.ToString().ToLowerInvariant()
                + (IsCrossMode ? " (cross-mode)" : string.Empty)
                + ", gap " + GapTiles + " tile(s)"
                + (string.IsNullOrEmpty(Reason) ? string.Empty : " - " + Reason);
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// Whether the modes of one neighbourhood slot can share a coordinate space, and if so where
    /// each of them sits in it. §13 open question 1, answered.
    ///
    /// <para><b>The model, stated once.</b> One generated world per <em>slot</em>, spanning every
    /// mode, built out of the per-mode rasters that already exist: each mode's 512x512 map becomes
    /// a window in a larger virtual slot space, and the whole of the unification is the offset
    /// that places it. Nothing is regenerated, nothing is resampled, nothing is copied, and
    /// nothing above this file changes. That is deliberately the cheapest of the two models §13
    /// offers - the alternative is a genuinely single 512x512 (or larger) map with all five
    /// modes' lots packed into it, which means re-running the packer and the generator over a
    /// merged lot list and invalidating every cached world - and it is the one that survives §0
    /// decision 4, which the handoff has not reopened.</para>
    ///
    /// <para><b>Why the residential region is at (0, 0).</b> It is the only arbitrary-looking
    /// decision in here, so it is worth naming: the view a player spends most of their time in
    /// (<see cref="NeighbourhoodModeMap.ResidentialDefault"/>) is pinned to the first cell, which
    /// makes its offset exactly zero and therefore makes today's per-mode coordinates - the ones
    /// <c>SimitoneSurroundings</c> and the facade cache already use - valid in the shared space
    /// unshifted. Unification is then additive: a consumer that has not heard of this file keeps
    /// working, and the other four modes are the ones that learn an offset. A pin that put, say,
    /// Downtown at the origin would have inverted that for no gain.</para>
    ///
    /// <para><b>The gap is one tile and it is not a landscape.</b> <see cref="DefaultRegionGap"/>
    /// separates the windows by exactly one tile, which is the smallest separation that makes
    /// "these two regions touch" false and therefore keeps §5.1's rule - cross-mode travel is a
    /// transition, not a walk - true by construction rather than by inspection. Nothing is drawn in
    /// it: the slot space is a coordinate space, not a raster, and a larger gap would only
    /// misdescribe it.</para>
    ///
    /// <para><b>Determinism.</b> The canonical order is the residential view first when it is
    /// present, then every other view in ascending mode id; windows are laid out left to right in
    /// that order on a fixed pitch. Two calls over the same modes produce the same space down to
    /// the offsets and the same <see cref="Describe"/> text, whatever order the caller listed its
    /// modes in. That is the assertion the tests make.</para>
    ///
    /// <para><b>Pure.</b> No disk, no VM, no graphics device, no game state, and no mutation of
    /// anything handed in. The layouts and rasters are read and referenced, never edited.</para>
    /// </summary>
    public sealed class NeighbourhoodWorldUnification
    {
        /// <summary>
        /// Tiles of separation between two adjacent windows in the shared space.
        ///
        /// <para>
        /// One, and only because one is enough. The pitch is
        /// <see cref="RegionSize"/> + this, so neighbouring windows in the auto-packed space are
        /// exactly this far apart, which is > <see cref="TouchTolerance"/> and therefore makes
        /// every cross-mode hop a <see cref="NeighbourhoodTravelKind.Transition"/>. No tile of it
        /// is ever drawn: the space is virtual, and a gap wide enough to be a road would imply a
        /// road that does not exist.
        /// </para>
        /// </summary>
        public const int DefaultRegionGap = 1;

        /// <summary>
        /// How close two regions must be, in tiles, for a sim to be able to walk from one to the
        /// other. Zero means edge-to-edge or corner-to-corner, and nothing looser.
        /// </summary>
        public const int TouchTolerance = 0;

        private readonly Dictionary<int, NeighbourhoodModeRegion> _byMode;

        private NeighbourhoodWorldUnification(int slot, bool autoPacked, int regionSize,
            int regionGap, List<NeighbourhoodModeRegion> regions, string failure)
        {
            Slot = slot;
            IsAutoPacked = autoPacked;
            RegionSize = regionSize;
            RegionGap = regionGap;
            Failure = failure;
            IsUnified = failure == null;

            var shared = NeighbourhoodExtent.Empty;
            for (int i = 0; i < regions.Count; i++)
            {
                shared = shared.Union(regions[i].Region);
            }

            //Every offset is stated relative to the shared space's minimum corner, and the shared
            //space always starts at (0, 0). For the auto pack that is a no-op (the first window is
            //already at the origin); for caller-supplied windows it is what makes a placement given
            //in arbitrary coordinates comparable with one given in slot coordinates.
            var origin = new NeighbourhoodExtent(0, 0, shared.Width, shared.Height);

            _byMode = new Dictionary<int, NeighbourhoodModeRegion>(regions.Count);
            var normalised = new List<NeighbourhoodModeRegion>(regions.Count);
            for (int i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                //always shifted, and not "shifted when it is not already there": the window whose
                //origin is the shared space's minimum corner is exactly the one whose offset has to
                //become zero, and a guard that skipped it would leave it where the caller put it
                region = new NeighbourhoodModeRegion(region.Mode,
                    region.OffsetX - shared.MinX, region.OffsetY - shared.MinY,
                    region.Size, region.Layout, region.Map);
                normalised.Add(region);
                _byMode[region.Mode] = region;
            }

            SharedOrigin = origin;
            Regions = new ReadOnlyCollection<NeighbourhoodModeRegion>(normalised);
        }

        /// <summary>
        /// The neighbourhood slot this space belongs to - the folder, not the view. See
        /// <see cref="NeighbourhoodModeWorld"/> for why the two are separate here when they are
        /// the same number everywhere above this file.
        /// </summary>
        public int Slot { get; }

        /// <summary>
        /// Whether these modes actually share a space. <c>false</c> on a refusal, which is the
        /// object <see cref="TryUnify(int, IReadOnlyList{NeighbourhoodModeWorld}, out NeighbourhoodWorldUnification)"/>
        /// hands back with a <see cref="Failure"/> instead of null.
        /// </summary>
        public bool IsUnified { get; }

        /// <summary>
        /// Whether the windows were placed by this file's own rule, or supplied by the caller and
        /// merely checked. Only a caller who wants a particular layout passes one, and
        /// <see cref="Describe"/> says which happened.
        /// </summary>
        public bool IsAutoPacked { get; }

        /// <summary>Side of one window, in tiles. Equal for every window in the space.</summary>
        public int RegionSize { get; }

        /// <summary>Tiles between two adjacent windows. See <see cref="DefaultRegionGap"/>.</summary>
        public int RegionGap { get; }

        /// <summary>How far apart two adjacent windows' origins are, in tiles.</summary>
        public int RegionPitch
        {
            get { return RegionSize + RegionGap; }
        }

        /// <summary>
        /// The slot-level origin: the one box in slot space that replaces "the generated map", and
        /// whose minimum corner is the tile every window's offset is measured from. It is always
        /// <c>[0, 0, width, height)</c> - the shared space is defined to start at (0, 0), which is
        /// what makes <see cref="NeighbourhoodModeRegion.LocalX"/> a subtraction rather than a
        /// lookup and what pins the residential window's offset to zero.
        /// </summary>
        public NeighbourhoodExtent SharedOrigin { get; }

        /// <summary>
        /// Every window, in the canonical order they were placed: the residential view first when
        /// it is present, then ascending mode id. Immutable.
        /// </summary>
        public IReadOnlyList<NeighbourhoodModeRegion> Regions { get; }

        /// <summary>How many windows the shared space holds.</summary>
        public int RegionCount
        {
            get { return Regions.Count; }
        }

        /// <summary>Why the modes do not share a space, or <c>null</c> when they do.</summary>
        public string Failure { get; }

        /// <summary>
        /// The window for a view, or <c>null</c>. The default value on a view this space does not
        /// hold, which is the answer for a lot number whose mode was never gathered.
        /// </summary>
        /// <param name="mode">A view id.</param>
        public NeighbourhoodModeRegion RegionFor(int mode)
        {
            NeighbourhoodModeRegion region;
            return _byMode.TryGetValue(mode, out region) ? region : default(NeighbourhoodModeRegion);
        }

        /// <summary>Whether this space holds a window for a view.</summary>
        /// <param name="mode">A view id.</param>
        public bool TryGetRegion(int mode, out NeighbourhoodModeRegion region)
        {
            return _byMode.TryGetValue(mode, out region);
        }

        /// <summary>
        /// The window's x offset in the shared space, or 0 for a view this space does not hold.
        /// </summary>
        /// <param name="mode">A view id.</param>
        public int OffsetXFor(int mode)
        {
            return RegionFor(mode).OffsetX;
        }

        /// <summary>
        /// The window's y offset in the shared space, or 0 for a view this space does not hold.
        /// </summary>
        /// <param name="mode">A view id.</param>
        public int OffsetYFor(int mode)
        {
            return RegionFor(mode).OffsetY;
        }

        /// <summary>
        /// A lot, in both spaces, found through the view it belongs to
        /// (<see cref="NeighbourhoodModeMap.ForHouse"/>).
        /// </summary>
        /// <param name="lotNumber">The lot number.</param>
        /// <param name="shared">The lot placed in the shared space, on success.</param>
        public bool TryMapLot(int lotNumber, out NeighbourhoodSharedLot shared)
        {
            return TryMapLot(NeighbourhoodModeWorld.ModeForLot(lotNumber), lotNumber, out shared);
        }

        /// <summary>
        /// A lot, in both spaces, from a named view. <c>false</c> when the view has no window here
        /// or the view's layout does not place that lot - the two are different problems and the
        /// caller can tell them apart by asking <see cref="TryGetRegion"/>.
        /// </summary>
        /// <param name="mode">A view id.</param>
        /// <param name="lotNumber">The lot number.</param>
        /// <param name="shared">The lot placed in the shared space, on success.</param>
        public bool TryMapLot(int mode, int lotNumber, out NeighbourhoodSharedLot shared)
        {
            shared = NeighbourhoodSharedLot.None;
            NeighbourhoodModeRegion region;
            if (!IsUnified || !_byMode.TryGetValue(mode, out region)) return false;
            return region.TryMapLot(lotNumber, out shared);
        }

        /// <summary>
        /// Which view owns a shared-space tile, and where that tile is in that view's own raster.
        /// The inverse of the offset, and the query a minimap or a pick does.
        ///
        /// <para>
        /// A tile in the gap between two windows belongs to no view, which is deliberate: the gap
        /// is not ground, it is the absence of an edge, and answering "the last window" for it
        /// would invent a coastline.
        /// </para>
        /// </summary>
        /// <param name="slotX">Tile x in the shared space.</param>
        /// <param name="slotY">Tile y in the shared space.</param>
        /// <param name="mode">The owning view, on success.</param>
        /// <param name="localX">Tile x in that view's own raster, on success.</param>
        /// <param name="localY">Tile y in that view's own raster, on success.</param>
        public bool TryMapTile(int slotX, int slotY, out int mode, out int localX, out int localY)
        {
            mode = 0;
            localX = 0;
            localY = 0;
            if (!IsUnified || !SharedOrigin.Contains(slotX, slotY)) return false;
            for (int i = 0; i < Regions.Count; i++)
            {
                var region = Regions[i];
                if (!region.Contains(slotX, slotY)) continue;
                mode = region.Mode;
                localX = region.LocalX(slotX);
                localY = region.LocalY(slotY);
                return true;
            }
            return false;
        }

        /// <summary>
        /// How a sim crosses from one view to another. §5.1's rule, as code:
        ///
        /// <para>
        /// <b>Same view: a walk.</b> Both lots are on one road graph, which is a
        /// <see cref="RoadGraph"/> away, and this type has no opinion about it beyond saying that
        /// no transition is involved.
        /// </para>
        ///
        /// <para>
        /// <b>Different views, windows sharing an edge or a corner: a walk.</b> The two rasters are
        /// physically continuous in the slot space, so a sim could cross. Nothing generates that
        /// edge today - the auto-packed space never produces one - but the rule is stated so that
        /// the day somebody does place two windows against each other, the answer changes to a walk
        /// on purpose rather than by accident.
        /// </para>
        ///
        /// <para>
        /// <b>Different views, windows apart: a transition.</b> This is the case the auto pack
        /// produces for every pair of modes, and it is §5.1 unchanged: the sim is carried, by the
        /// same lot switch a neighbourhood switch already uses, arriving at the destination lot's
        /// entrance. A shared coordinate space does not by itself make a world walkable - a
        /// coordinate is not a road - and treating it as though it did is the failure this type
        /// exists to prevent.
        /// </para>
        /// </summary>
        /// <param name="fromMode">The view the sim is leaving.</param>
        /// <param name="toMode">The view the sim is arriving in.</param>
        /// <param name="transition">The decision, on success.</param>
        public bool TransitionBetweenModes(int fromMode, int toMode,
            out NeighbourhoodModeTransition transition)
        {
            transition = default(NeighbourhoodModeTransition);
            NeighbourhoodModeRegion from, to;
            if (!IsUnified) return false;
            if (!_byMode.TryGetValue(fromMode, out from)) return false;
            if (!_byMode.TryGetValue(toMode, out to)) return false;

            if (fromMode == toMode)
            {
                transition = new NeighbourhoodModeTransition(fromMode, toMode,
                    NeighbourhoodTravelKind.Walk, false, 0, NeighbourhoodExtent.Empty,
                    "both lots are in mode " + fromMode
                    + ", so the road graph routes them inside one generated map");
                return true;
            }

            var separation = Separation(from.Region, to.Region);
            if (separation <= TouchTolerance)
            {
                transition = new NeighbourhoodModeTransition(fromMode, toMode,
                    NeighbourhoodTravelKind.Walk, true, separation, SharedEdge(from.Region, to.Region),
                    "the mode " + fromMode + " and mode " + toMode + " regions share an edge in the"
                    + " shared slot space, so a sim can walk between them");
                return true;
            }

            transition = new NeighbourhoodModeTransition(fromMode, toMode,
                NeighbourhoodTravelKind.Transition, true, separation, NeighbourhoodExtent.Empty,
                "the mode " + fromMode + " and mode " + toMode + " regions are " + separation
                + " tile(s) apart in the shared slot space; cross-mode travel is a transition"
                + " (a lot switch, a taxi), not a walk");
            return true;
        }

        /// <summary>
        /// The same decision for two lot numbers rather than two views, which is the form B3's
        /// travel path wants: it knows the two houses, not the two modes.
        /// </summary>
        /// <param name="fromLot">The lot the sim is leaving.</param>
        /// <param name="toLot">The lot the sim is arriving in.</param>
        /// <param name="transition">The decision, on success.</param>
        public bool TryTransitionForLots(int fromLot, int toLot,
            out NeighbourhoodModeTransition transition)
        {
            transition = default(NeighbourhoodModeTransition);
            if (!IsUnified) return false;
            return TransitionBetweenModes(
                NeighbourhoodModeWorld.ModeForLot(fromLot),
                NeighbourhoodModeWorld.ModeForLot(toLot), out transition);
        }

        /// <summary>
        /// Whether two boxes in the shared space are close enough for a sim to walk from one to
        /// the other: overlapping, edge-to-edge or corner-to-corner within
        /// <paramref name="tolerance"/>.
        ///
        /// <para>
        /// The all-four-signed-distances form rather than "expand one and test overlap", because an
        /// expand-and-overlap test cannot tell an edge-sharing pair from a pair one tile apart - it
        /// reports neither as touching, which is right for a walk and useless for explaining why
        /// one walk was refused and another allowed.
        /// </para>
        /// </summary>
        /// <param name="a">One box.</param>
        /// <param name="b">The other.</param>
        /// <param name="tolerance">How close counts as touching, in tiles.</param>
        public static bool RegionsTouch(NeighbourhoodExtent a, NeighbourhoodExtent b, int tolerance)
        {
            if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0) return false;
            return a.MinX - b.MaxX <= tolerance
                && b.MinX - a.MaxX <= tolerance
                && a.MinY - b.MaxY <= tolerance
                && b.MinY - a.MaxY <= tolerance;
        }

        /// <summary>
        /// Whether two boxes share a tile. The no-overlap rule, in one place: two windows in one
        /// shared space may not claim the same ground, because a slot tile has to resolve to
        /// exactly one mode's raster.
        /// </summary>
        /// <param name="a">One box.</param>
        /// <param name="b">The other.</param>
        public static bool RegionsOverlap(NeighbourhoodExtent a, NeighbourhoodExtent b)
        {
            if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0) return false;
            return a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinY < b.MaxY && b.MinY < a.MaxY;
        }

        /// <summary>
        /// How far apart two boxes are, in tiles, on the worst of their two axes. Zero when they
        /// touch or overlap; positive otherwise. Never negative, so it is safe to print.
        /// </summary>
        /// <param name="a">One box.</param>
        /// <param name="b">The other.</param>
        public static int Separation(NeighbourhoodExtent a, NeighbourhoodExtent b)
        {
            var dx = Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX);
            var dy = Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY);
            return Math.Max(0, Math.Max(dx, dy));
        }

        /// <summary>
        /// Places a slot's modes in one coordinate space, with the windows laid out by this file's
        /// own rule: the residential view first, then every other view in ascending mode id, left
        /// to right on a pitch of <see cref="RegionPitch"/>.
        /// </summary>
        /// <param name="slot">The neighbourhood slot - the folder, not the view.</param>
        /// <param name="modes">One entry per view. Order does not matter.</param>
        /// <param name="unification">
        /// The space, or a refusal carrying the reason. Never null, so a caller that only wants the
        /// reason can ignore the return value.
        /// </param>
        /// <returns>Whether the modes share a space.</returns>
        public static bool TryUnify(int slot, IReadOnlyList<NeighbourhoodModeWorld> modes,
            out NeighbourhoodWorldUnification unification)
        {
            return TryUnify(slot, modes, null, out unification);
        }

        /// <summary>
        /// Places a slot's modes in a coordinate space the caller has already chosen, and checks
        /// that the choice is a space: every window one region-size square, no two of them
        /// sharing a tile, and every one of them named in <paramref name="regions"/>.
        ///
        /// <para>
        /// This is where the overlap decision is made rather than merely being impossible: the
        /// auto pack cannot overlap by construction, so a test that wants to see the refusal has to
        /// hand in two windows on the same ground. <see cref="RegionsOverlap"/> is the rule and
        /// <see cref="Failure"/> names the two views.
        /// </para>
        /// </summary>
        /// <param name="slot">The neighbourhood slot.</param>
        /// <param name="modes">One entry per view.</param>
        /// <param name="regions">The window for each view, keyed by mode id.</param>
        /// <param name="unification">The space, or a refusal carrying the reason.</param>
        /// <returns>Whether the modes share a space.</returns>
        public static bool TryUnify(int slot, IReadOnlyList<NeighbourhoodModeWorld> modes,
            IReadOnlyDictionary<int, NeighbourhoodExtent> regions,
            out NeighbourhoodWorldUnification unification)
        {
            if (!TryOrder(modes, out var ordered, out var reason))
            {
                unification = new NeighbourhoodWorldUnification(slot, regions == null, 0,
                    DefaultRegionGap, new List<NeighbourhoodModeRegion>(), reason);
                return false;
            }

            if (!TryResolveRegionSize(ordered, out var size, out reason))
            {
                unification = new NeighbourhoodWorldUnification(slot, regions == null, size,
                    DefaultRegionGap, new List<NeighbourhoodModeRegion>(), reason);
                return false;
            }

            var placed = new List<NeighbourhoodModeRegion>(ordered.Count);
            if (regions == null)
            {
                var pitch = size + DefaultRegionGap;
                for (int i = 0; i < ordered.Count; i++)
                {
                    placed.Add(new NeighbourhoodModeRegion(ordered[i].Mode, i * pitch, 0, size,
                        ordered[i].Layout, ordered[i].Map));
                }
            }
            else
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    var mode = ordered[i].Mode;
                    NeighbourhoodExtent region;
                    if (!regions.TryGetValue(mode, out region))
                    {
                        unification = new NeighbourhoodWorldUnification(slot, false, size,
                            DefaultRegionGap, new List<NeighbourhoodModeRegion>(),
                            "no region was given for mode " + mode);
                        return false;
                    }
                    if (region.Width <= 0 || region.Height <= 0)
                    {
                        unification = new NeighbourhoodWorldUnification(slot, false, size,
                            DefaultRegionGap, new List<NeighbourhoodModeRegion>(),
                            "the region given for mode " + mode + " is empty");
                        return false;
                    }
                    if (region.Width != size || region.Height != size)
                    {
                        unification = new NeighbourhoodWorldUnification(slot, false, size,
                            DefaultRegionGap, new List<NeighbourhoodModeRegion>(),
                            "the region given for mode " + mode + " is " + region.Width + "x"
                            + region.Height + " but this slot's generated maps are " + size + "x" + size);
                        return false;
                    }
                    placed.Add(new NeighbourhoodModeRegion(mode, region.MinX, region.MinY, size,
                        ordered[i].Layout, ordered[i].Map));
                }

                foreach (var pair in regions)
                {
                    if (!ContainsMode(ordered, pair.Key))
                    {
                        unification = new NeighbourhoodWorldUnification(slot, false, size,
                            DefaultRegionGap, new List<NeighbourhoodModeRegion>(),
                            "a region was given for mode " + pair.Key
                            + ", which is not one of this slot's modes");
                        return false;
                    }
                }

                for (int a = 0; a < placed.Count; a++)
                {
                    for (int b = a + 1; b < placed.Count; b++)
                    {
                        if (!RegionsOverlap(placed[a].Region, placed[b].Region)) continue;
                        unification = new NeighbourhoodWorldUnification(slot, false, size,
                            DefaultRegionGap, new List<NeighbourhoodModeRegion>(),
                            "the regions for mode " + placed[a].Mode + " and mode " + placed[b].Mode
                            + " overlap at " + placed[a].Region + " and " + placed[b].Region
                            + "; a shared-space tile has to resolve to one mode");
                        return false;
                    }
                }
            }

            unification = new NeighbourhoodWorldUnification(slot, regions == null, size,
                DefaultRegionGap, placed, null);
            return true;
        }

        /// <summary>
        /// The same question asked of the worlds the lifecycle already produced, folding each
        /// world in as the view it was generated for.
        ///
        /// <para>
        /// <b>The one thing a caller has to get right.</b> A
        /// <see cref="NeighbourhoodGeneratedWorld"/>'s <c>Slot</c> is a <em>view</em> id - it is
        /// <c>CurrentNeighborhoodMode</c>, see <see cref="NeighbourhoodModeWorld"/> - while this
        /// method's <paramref name="slot"/> is the folder. So a caller gathering the five worlds
        /// of one neighbourhood passes the same folder to every one of them and lets each world
        /// name its own view. Getting that backwards is the single wiring mistake this overload
        /// can make, which is why it is spelled out here rather than left to the call site.
        /// </para>
        /// </summary>
        /// <param name="slot">The neighbourhood slot - the folder.</param>
        /// <param name="worlds">The per-view worlds, each already generated.</param>
        /// <param name="unification">The space, or a refusal carrying the reason.</param>
        /// <returns>Whether the modes share a space.</returns>
        public static bool TryUnifyFromWorlds(int slot, IReadOnlyList<NeighbourhoodGeneratedWorld> worlds,
            out NeighbourhoodWorldUnification unification)
        {
            if (worlds == null || worlds.Count == 0)
            {
                unification = new NeighbourhoodWorldUnification(slot, true, 0, DefaultRegionGap,
                    new List<NeighbourhoodModeRegion>(), "the slot has no generated worlds");
                return false;
            }

            var modes = new List<NeighbourhoodModeWorld>(worlds.Count);
            for (int i = 0; i < worlds.Count; i++)
            {
                var world = worlds[i];
                if (world == null) continue;
                modes.Add(new NeighbourhoodModeWorld(world.Slot, world.Layout, world.Map));
            }
            return TryUnify(slot, modes, out unification);
        }

        /// <summary>
        /// The canonical order, and the validation that has to happen before it: at least one
        /// mode, no mode named twice, and every mode a real view id.
        /// </summary>
        private static bool TryOrder(IReadOnlyList<NeighbourhoodModeWorld> modes,
            out List<NeighbourhoodModeWorld> ordered, out string reason)
        {
            ordered = new List<NeighbourhoodModeWorld>();
            reason = null;
            if (modes == null || modes.Count == 0)
            {
                reason = "the slot has no modes, so there is nothing to unify";
                return false;
            }

            var seen = new Dictionary<int, bool>();
            for (int i = 0; i < modes.Count; i++)
            {
                var mode = modes[i].Mode;
                if (mode < NeighbourhoodModeMap.FirstMode || mode > NeighbourhoodModeMap.LastMode)
                {
                    reason = "mode " + mode + " is not a neighbourhood view ("
                        + NeighbourhoodModeMap.FirstMode + "-" + NeighbourhoodModeMap.LastMode + ")";
                    return false;
                }
                if (seen.ContainsKey(mode))
                {
                    reason = "mode " + mode + " was given twice";
                    return false;
                }
                seen.Add(mode, true);
                ordered.Add(modes[i]);
            }

            //The residential view is pinned to the first window - see the class remarks. The rest
            //are ascending by mode id, which is a total order that does not depend on the order
            //the caller listed its modes in, and that is what makes the offsets deterministic.
            ordered.Sort((a, b) =>
            {
                var aAnchor = a.Mode == NeighbourhoodModeMap.ResidentialDefault ? 0 : 1;
                var bAnchor = b.Mode == NeighbourhoodModeMap.ResidentialDefault ? 0 : 1;
                if (aAnchor != bAnchor) return aAnchor - bAnchor;
                return a.Mode.CompareTo(b.Mode);
            });
            return true;
        }

        private static bool ContainsMode(List<NeighbourhoodModeWorld> modes, int mode)
        {
            for (int i = 0; i < modes.Count; i++)
            {
                if (modes[i].Mode == mode) return true;
            }
            return false;
        }

        /// <summary>
        /// The one region size for the whole space, and the refusal when the modes disagree about
        /// it. A 512-tile map beside a 256-tile one cannot be two windows of a square space
        /// without either a resize - which would be changing generation - or a rectangular
        /// lattice, which is a second packing rule for no gain in a build where every mode is
        /// 512.
        /// </summary>
        private static bool TryResolveRegionSize(List<NeighbourhoodModeWorld> modes,
            out int size, out string reason)
        {
            size = 0;
            reason = null;
            for (int i = 0; i < modes.Count; i++)
            {
                var world = modes[i];
                if (!world.HasWorld)
                {
                    reason = "mode " + world.Mode + " has neither a layout nor a raster, so it"
                        + " has no size to place";
                    return false;
                }

                var side = world.MapSize;
                if (world.Map != null && world.Layout != null
                    && world.Map.Width != world.Layout.MapSize)
                {
                    reason = "mode " + world.Mode + "'s raster is " + world.Map.Width + " tiles wide"
                        + " and its layout says " + world.Layout.MapSize;
                    return false;
                }
                if (side <= 0)
                {
                    reason = "mode " + world.Mode + " has a map size of " + side;
                    return false;
                }
                if (size == 0)
                {
                    size = side;
                    continue;
                }
                if (size != side)
                {
                    reason = "mode " + world.Mode + " is " + side + " tiles square and an earlier"
                        + " mode is " + size + "; one shared space needs one region size";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Where two touching windows meet: the box they share, which has zero width or zero height
        /// for an edge and is a single tile for a corner. <see cref="NeighbourhoodExtent.Empty"/>
        /// when they do not touch at all, which is a different statement from a degenerate box and
        /// is why the value is checked rather than tested for emptiness.
        /// </summary>
        private static NeighbourhoodExtent SharedEdge(NeighbourhoodExtent a, NeighbourhoodExtent b)
        {
            return new NeighbourhoodExtent(
                Math.Max(a.MinX, b.MinX), Math.Max(a.MinY, b.MinY),
                Math.Min(a.MaxX, b.MaxX), Math.Min(a.MaxY, b.MaxY));
        }

        /// <summary>
        /// One line for a log: what the space is, where each view sits in it, and what the
        /// cross-mode rule is. The sentence a log reader wants is the last one, so it is last.
        /// </summary>
        public string Describe()
        {
            if (!IsUnified)
            {
                return "slot " + Slot + ": not unified - " + Failure;
            }

            var sb = new StringBuilder();
            sb.Append("slot ").Append(Slot.ToString(CultureInfo.InvariantCulture))
              .Append(": one shared space ").Append(SharedOrigin.ToString())
              .Append(" of ").Append(RegionCount.ToString(CultureInfo.InvariantCulture))
              .Append(" mode region(s) of ").Append(RegionSize.ToString(CultureInfo.InvariantCulture))
              .Append("x").Append(RegionSize.ToString(CultureInfo.InvariantCulture))
              .Append(" (pitch ").Append(RegionPitch.ToString(CultureInfo.InvariantCulture))
              .Append(", ").Append(RegionGap.ToString(CultureInfo.InvariantCulture))
              .Append(" tile(s) apart")
              .Append(IsAutoPacked ? ", packed" : ", caller-placed");

            for (int i = 0; i < Regions.Count; i++)
            {
                var region = Regions[i];
                sb.Append("; mode ").Append(region.Mode.ToString(CultureInfo.InvariantCulture))
                  .Append(" +(").Append(region.OffsetX.ToString(CultureInfo.InvariantCulture))
                  .Append(',').Append(region.OffsetY.ToString(CultureInfo.InvariantCulture))
                  .Append(')').Append(region.Region.ToString())
                  .Append(' ').Append(region.LotCount.ToString(CultureInfo.InvariantCulture))
                  .Append(region.HasRaster ? " lots" : " lots, no raster");
            }

            sb.Append("; cross-mode travel is ").Append(AllRegionsApart() ? "always a transition" : "a transition unless two regions touch")
              .Append(" (a shared coordinate space is not a road)");
            return sb.ToString();
        }

        /// <summary>
        /// Whether every pair of windows in this space is more than <see cref="TouchTolerance"/>
        /// apart - which is what the auto pack guarantees, and which is why the documented default
        /// needs no per-pair inspection.
        /// </summary>
        public bool AllRegionsApart()
        {
            for (int a = 0; a < Regions.Count; a++)
            {
                for (int b = a + 1; b < Regions.Count; b++)
                {
                    if (RegionsTouch(Regions[a].Region, Regions[b].Region, TouchTolerance)) return false;
                }
            }
            return true;
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// The one generated world a neighbourhood has, and the single authority every view of that
    /// neighbourhood reads it through.
    ///
    /// <para><b>Why this exists, and what it replaces.</b> §8.1 asks for "one connected map, not two
    /// views", and §8.3 settles it as "one generated world, two camera states". Before this, the
    /// neighbourhood screen and the in-lot surround each built their own
    /// <see cref="NeighbourhoodSceneSource"/> over the same
    /// <see cref="NeighbourhoodGeneratedWorld"/> - and <see cref="NeighbourhoodSceneSource"/>
    /// clones the raster in its constructor, so "each built its own" was not a figure of speech:
    /// a 512x512 map of terrain, elevation and road bytes was duplicated per view, and the two
    /// views' answers to "where is lot 21" were two objects rather than one.</para>
    ///
    /// <para><b>What it hands out.</b> One <see cref="Source"/> per slot, holding one clone of the
    /// map and one <see cref="NeighbourhoodWorldMapping"/>, plus the
    /// <see cref="Unification"/> that places this slot's view inside the shared coordinate space.
    /// Both views read that same object, so "the same map" is reference identity rather than a
    /// promise.</para>
    ///
    /// <para><b>Why it is safe to share the raster.</b> Nothing outside generation writes to a
    /// <see cref="NeighbourhoodMap"/>: the only writers in the tree are
    /// <see cref="RoadGraph"/> and <see cref="TerrainFill"/>, which run inside the generator before
    /// anything holds a map. Every view - the terrain renderer, the foliage, the facade cache and the
    /// surround crop - only reads. And a single view already shared one clone between all three of
    /// its passes, so sharing across views introduces no new class of hazard.</para>
    ///
    /// <para><b>Identity, not contents.</b> An entry is reused when the caller hands back the very
    /// same <see cref="NeighbourhoodLayout"/> and <see cref="NeighbourhoodMap"/> instances. That is
    /// the honest test rather than a field-for-field comparison, because a regenerated world is a new
    /// pair of objects by construction: a bulldoze that re-placed a lot produced a new layout, and
    /// reusing the old source would show the old street. A cache hit hands back the cached instance
    /// itself, which is what makes the common case - walking into a lot and coming straight back -
    /// reuse rather than rebuild.</para>
    ///
    /// <para><b>The one piece of mutable state on the source is now shared too.</b>
    /// <see cref="NeighbourhoodSceneSource.ActiveLot"/> is a settable property, and the facade cache
    /// reads it to skip the lot the player is standing in. That is the right answer for a shared world
    /// - "which lot is active" is a fact about the neighbourhood, not about a view - but it does mean
    /// a second writer would now be visible to both views. Nothing writes it in production yet
    /// (<see cref="Neighbourhood3DView.SetActiveLot"/> has no caller), and this is noted here rather
    /// than solved because solving it means moving the property off the source, which is not this
    /// workstream's file.</para>
    ///
    /// <para><b>What is still duplicated, honestly.</b> One map, yes; one set of vertex buffers, no.
    /// The map view's terrain renderer and the in-lot surround's crop each call
    /// <see cref="NeighbourhoodCityMeshBuilder.TryBuild"/> on this shared raster and each upload the
    /// result to the device, so the geometry is derived twice and the GPU holds two buffers' worth.
    /// That is a cost, not a divergence - both are pure functions of the same map, so they cannot
    /// disagree about what the neighbourhood looks like - and collapsing it needs a change in
    /// <c>NeighbourhoodTerrainRenderer</c> and <c>SimitoneSurroundings</c>, neither of which belongs
    /// to this workstream. It is the seam §8.3 leaves for the single-scene half.</para>
    ///
    /// <para><b>Pure enough to assert.</b> No disk, no provider, no graphics device and no clock:
    /// <see cref="Acquire"/> takes a layout and a map and returns a source. Everything that needs the
    /// game to produce those two lives above this class, which is what lets a test drive the whole
    /// sharing rule with two hand-made layouts.</para>
    /// </summary>
    public sealed class NeighbourhoodSharedWorld
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, NeighbourhoodSharedWorld> BySlot =
            new Dictionary<int, NeighbourhoodSharedWorld>();
        private static int HandoutCount;

        private NeighbourhoodSharedWorld(int slot, NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            Slot = slot;
            Layout = layout;
            Map = map;
            //One clone, made once. Both views draw this object, so the raster exists exactly once per
            //neighbourhood for as long as this entry lives.
            Source = new NeighbourhoodSceneSource(layout, map);
            Unification = PlaceInSharedSpace(slot, layout, map);
        }

        /// <summary>The neighbourhood slot this world belongs to.</summary>
        public int Slot { get; }

        /// <summary>
        /// The packed layout, which is also the raster's own coordinate space for this slot: a
        /// one-window <see cref="NeighbourhoodWorldUnification"/> puts its window at offset (0, 0),
        /// so shared-space tiles and this layout's tiles are the same numbers today.
        /// </summary>
        public NeighbourhoodLayout Layout { get; }

        /// <summary>
        /// The generated raster as the pipeline produced it. Views read
        /// <see cref="Source"/>'s map, not this one; this is the instance the entry is keyed on.
        /// </summary>
        public NeighbourhoodMap Map { get; }

        /// <summary>
        /// The one scene source both views hold. Its <see cref="NeighbourhoodSceneSource.Map"/> is
        /// the single raster copy, and its <see cref="NeighbourhoodSceneSource.Mapping"/> is the
        /// single bitmap/world correspondence.
        /// </summary>
        public NeighbourhoodSceneSource Source { get; }

        /// <summary>
        /// This slot's view placed in the shared slot space. With one window in the space the window
        /// is at the origin, so every accessor here answers in the layout's own tiles - which is the
        /// point: it is now written down in one place rather than assumed by each caller.
        /// </summary>
        public NeighbourhoodWorldUnification Unification { get; }

        /// <summary>Whether there is a shared space at all. See <see cref="Failure"/>.</summary>
        public bool IsUnified
        {
            get { return Unification != null && Unification.IsUnified; }
        }

        /// <summary>Why the modes do not share a space, or <c>null</c> when they do.</summary>
        public string Failure
        {
            get { return Unification == null ? "no shared space was built" : Unification.Failure; }
        }

        /// <summary>
        /// A lot in the shared space, in both coordinates at once.
        /// </summary>
        /// <param name="lotNumber">The lot number.</param>
        /// <param name="shared">The lot placed, on success.</param>
        public bool TryMapLot(int lotNumber, out NeighbourhoodSharedLot shared)
        {
            shared = NeighbourhoodSharedLot.None;
            if (!IsUnified) return false;
            return Unification.TryMapLot(Slot, lotNumber, out shared);
        }

        /// <summary>
        /// The shared source for a layout and a map, creating the entry if this is the first view to
        /// ask for this exact pair. Never null: a pair that cannot produce a map is not a shared
        /// world, and the caller gets a source carrying the reason.
        /// </summary>
        /// <remarks>
        /// <b>Not thread safe by accident.</b> Everything is taken under one lock and the only work
        /// done inside it is a dictionary lookup, a clone and a unification - which is exactly the
        /// work that must not happen twice, and which is fast enough to do under the lock. The
        /// callers are the game thread (the panel) and the screen's own attach, so there is no
        /// contention to speak of either way.
        /// </remarks>
        /// <param name="slot">The neighbourhood slot the world belongs to.</param>
        /// <param name="layout">The packed layout.</param>
        /// <param name="map">The generated raster.</param>
        /// <returns>The shared source, or a source carrying the reason there is not one.</returns>
        public static NeighbourhoodSceneSource Acquire(int slot, NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            if (layout == null || map == null)
            {
                return new NeighbourhoodSceneSource(layout,
                    "there is no generated map to share");
            }

            lock (Gate)
            {
                NeighbourhoodSharedWorld entry;
                if (BySlot.TryGetValue(slot, out entry) && IsSameWorld(entry, layout, map))
                {
                    HandoutCount++;
                    return entry.Source;
                }

                //A different world for the same slot: the old entry is replaced rather than merged.
                //Two worlds for one neighbourhood is the state §8.1 exists to end, and keeping the
                //older one would mean a later lookup could find it again.
                BySlot[slot] = new NeighbourhoodSharedWorld(slot, layout, map);
                HandoutCount++;
                return BySlot[slot].Source;
            }
        }

        /// <summary>
        /// <see cref="Acquire(int, NeighbourhoodLayout, NeighbourhoodMap)"/> from a lifecycle world,
        /// with the same refusals <see cref="UINeighborhoodSelectionPanel.SceneSourceFor"/> states.
        /// </summary>
        /// <param name="world">What the lifecycle handed back, or <c>null</c>.</param>
        /// <param name="failure">Why there is no map to draw, or <c>null</c> when there is one.</param>
        public static NeighbourhoodSceneSource AcquireFor(
            NeighbourhoodGeneratedWorld world, out string failure)
        {
            if (world == null)
            {
                failure = "the generated world refresh produced nothing";
                return new NeighbourhoodSceneSource(null, failure);
            }

            if (!world.HasWorld)
            {
                //The specific reason first - the road graph, the generation input, the allocation -
                //because that is the one worth chasing; the plan's own selection reason is the
                //fallback for a world that failed without saying why.
                failure = string.IsNullOrEmpty(world.Failure)
                    ? world.Plan.Selection.Reason
                    : world.Failure;
                return new NeighbourhoodSceneSource(world.Layout, failure);
            }

            failure = null;
            return Acquire(world.Slot, world.Layout, world.Map);
        }

        /// <summary>
        /// Whether an entry is the world a caller is holding. The rule the reuse decision is made
        /// by, exposed so it can be asserted rather than inferred from a log line.
        /// </summary>
        /// <param name="entry">The remembered entry, or <c>null</c>.</param>
        /// <param name="layout">The layout the caller has.</param>
        /// <param name="map">The map the caller has.</param>
        public static bool IsSameWorld(
            NeighbourhoodSharedWorld entry, NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            if (entry == null || layout == null || map == null) return false;
            return ReferenceEquals(entry.Layout, layout) && ReferenceEquals(entry.Map, map);
        }

        /// <summary>The shared world a slot currently has, or <c>null</c>.</summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        public static NeighbourhoodSharedWorld Current(int slot)
        {
            lock (Gate)
            {
                NeighbourhoodSharedWorld entry;
                return BySlot.TryGetValue(slot, out entry) ? entry : null;
            }
        }

        /// <summary>
        /// Whether a slot's shared world is the one a caller is holding. What the neighbourhood
        /// screen asks before it decides whether the view it parked for a lot can come back as it
        /// was, or has to be rebuilt over a world that moved underneath it.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <param name="layout">The layout the caller holds.</param>
        /// <param name="map">The map the caller holds.</param>
        public static bool Holds(int slot, NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            return IsSameWorld(Current(slot), layout, map);
        }

        /// <summary>How many slots currently hold a shared world, for the log and for tests.</summary>
        public static int Count
        {
            get { lock (Gate) { return BySlot.Count; } }
        }

        /// <summary>
        /// How many times a shared source has been handed out. The number that says whether the
        /// sharing is doing anything: one clone per slot, however many views ask.
        /// </summary>
        public static int Handouts
        {
            get { lock (Gate) { return HandoutCount; } }
        }

        /// <summary>
        /// Forgets a slot's shared world, because the neighbourhood it described changed. Called from
        /// the same two places that drop the generated world, so the two can never disagree about
        /// what is current.
        /// </summary>
        /// <param name="slot">The neighbourhood slot id.</param>
        /// <returns>Whether there was one.</returns>
        public static bool Invalidate(int slot)
        {
            lock (Gate)
            {
                return BySlot.Remove(slot);
            }
        }

        /// <summary>Forgets every shared world. For a content change and for tests.</summary>
        public static void InvalidateAll()
        {
            lock (Gate)
            {
                BySlot.Clear();
                HandoutCount = 0;
            }
        }

        /// <summary>One line for a log.</summary>
        public static string Describe()
        {
            lock (Gate)
            {
                var sb = new StringBuilder();
                sb.Append(BySlot.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(" shared neighbourhood world(s), handed out ")
                                    .Append(HandoutCount.ToString(CultureInfo.InvariantCulture))
                  .Append(" time(s)");
                return sb.ToString();
            }
        }

        /// <summary>One line for a log about this entry.</summary>
        public string DescribeSelf()
        {
            return "slot " + Slot.ToString(CultureInfo.InvariantCulture) + ": "
                + (Layout?.LotCount ?? 0).ToString(CultureInfo.InvariantCulture) + " lot(s), "
                + (Source?.Map?.Width ?? 0).ToString(CultureInfo.InvariantCulture) + "x"
                + (Source?.Map?.Height ?? 0).ToString(CultureInfo.InvariantCulture)
                + " tiles, " + (IsUnified ? "one shared space" : "no shared space (" + Failure + ")");
        }

        public override string ToString()
        {
            return DescribeSelf();
        }

        /// <summary>
        /// Places one view's window in the shared slot space. With a single window the auto pack puts
        /// it at (0, 0) - see the class remarks - so this is the identity placement, and it is asked
        /// for rather than assumed so that "the shared space is where this view's tiles are" is a
        /// fact the tree can answer.
        /// </summary>
        private static NeighbourhoodWorldUnification PlaceInSharedSpace(
            int slot, NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            try
            {
                var modes = new List<NeighbourhoodModeWorld>(1)
                {
                    new NeighbourhoodModeWorld(slot, layout, map)
                };
                NeighbourhoodWorldUnification.TryUnify(slot, modes, out var unified);
                return unified;
            }
            catch (Exception)
            {
                //A shared space is bookkeeping over data that already exists. It cannot be worth
                //taking a neighbourhood down for, and the entry is still usable without one - the
                //views read Layout and Source either way.
                return null;
            }
        }
    }
}
