/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// One generated world per neighbourhood slot, spanning every mode: §13 open question 1.
    ///
    /// <para>
    /// The four things that have to be true of that planner, and which this file pins in that
    /// order because they are the four things a reader will otherwise have to take on trust:
    /// <list type="number">
    ///   <item><description>
    ///     <b>The offset arithmetic.</b> A mode's tile (x, y) is at <c>(offset + x, offset + y)</c>
    ///     in the shared space and comes back the same way, and a lot moves from where its own
    ///     layout put it to exactly the window's offset away. Pinned with literal numbers, because
    ///     an off-by-one here is invisible on a 512-tile map and is the whole class of bug the
    ///     sidecar's half-open rectangles are already written to prevent.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Determinism.</b> The offsets do not depend on the order the caller listed its modes
    ///     in, and two calls produce element-for-element equal spaces and the same
    ///     <c>Describe</c> text. A planner whose output depends on a <c>Dictionary</c>'s iteration
    ///     order is a planner that cannot be put in a sidecar.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The no-overlap / overlap decision.</b> The auto pack cannot overlap, and the
    ///     caller-placed path refuses a placement that does, naming both modes. Also pinned: a
    ///     refusal is an object with a reason, not a null, and a slot with no modes, a mode with no
    ///     world and two modes of different map sizes are all refusals.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The transition rule.</b> Cross-mode travel is a transition, not a walk - and stays
    ///     one when two regions are deliberately placed against each other only if they actually
    ///     touch. §5.1's decision, asserted rather than quoted.
    ///   </description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// No disk, no VM, no graphics device. The layouts are built by hand and the rasters are
    /// <see cref="NeighbourhoodMap.Create"/>, so nothing here depends on generation having
    /// succeeded on this machine.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldUnificationTests
    {
        private const int Slot = 0;
        private const int MapSize = 64;

        private static int Residential
        {
            get { return NeighbourhoodModeMap.ResidentialDefault; }
        }

        /// <summary>A layout with <paramref name="lotNumbers"/> packed from the top left.</summary>
        private static NeighbourhoodLayout LayoutFor(int mode, params short[] lotNumbers)
        {
            var lots = new List<NeighbourhoodLot>();
            for (int i = 0; i < lotNumbers.Length; i++)
            {
                lots.Add(new NeighbourhoodLot
                {
                    Number = lotNumbers[i],
                    WorldX = 8 + i * 16,
                    WorldY = 8,
                    Size = 8,
                    DoorSide = NeighbourhoodLotSide.South,
                    HasHouse = true
                });
            }
            return new NeighbourhoodLayout(mode, 1234, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                MapSize, lots);
        }

        private static NeighbourhoodModeWorld Mode(int mode, params short[] lotNumbers)
        {
            return new NeighbourhoodModeWorld(mode, LayoutFor(mode, lotNumbers), NeighbourhoodMap.Create(MapSize));
        }

        /// <summary>All five views, in the order the handoff names them.</summary>
        private static List<NeighbourhoodModeWorld> AllFiveModes()
        {
            return new List<NeighbourhoodModeWorld>
            {
                Mode(Residential, 1, 2),
                Mode(NeighbourhoodModeMap.Downtown, 21, 22),
                Mode(NeighbourhoodModeMap.Vacation, 40, 41),
                Mode(NeighbourhoodModeMap.Studiotown, 81),
                Mode(NeighbourhoodModeMap.Magictown, 90)
            };
        }

        // ---- the offset arithmetic -------------------------------------------------------------

        [Fact]
        public void ResidentialWindow_SitsAtTheOrigin_SoTodayPerModeCoordinatesAreUnchanged()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            var home = unified.RegionFor(Residential);
            Assert.Equal(0, home.OffsetX);
            Assert.Equal(0, home.OffsetY);
            Assert.Equal(new NeighbourhoodExtent(0, 0, MapSize, MapSize), home.Region);
        }

        [Fact]
        public void Windows_AreLaidOutLeftToRightOnAPitchOfRegionSizePlusTheGap()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            Assert.Equal(MapSize, unified.RegionSize);
            Assert.Equal(NeighbourhoodWorldUnification.DefaultRegionGap, unified.RegionGap);
            Assert.Equal(MapSize + 1, unified.RegionPitch);

            // residential, downtown, vacation, studio, magic - the canonical order, not the
            // order the caller listed them in above (which happens to match).
            Assert.Equal(0, unified.OffsetXFor(Residential));
            Assert.Equal(65, unified.OffsetXFor(NeighbourhoodModeMap.Downtown));
            Assert.Equal(130, unified.OffsetXFor(NeighbourhoodModeMap.Vacation));
            Assert.Equal(195, unified.OffsetXFor(NeighbourhoodModeMap.Studiotown));
            Assert.Equal(260, unified.OffsetXFor(NeighbourhoodModeMap.Magictown));

            foreach (var region in unified.Regions)
            {
                Assert.Equal(0, region.OffsetY);
            }
        }

        [Fact]
        public void SharedOrigin_IsTheWholeSpace_AndStartsAtZero()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            // five 64-tile windows on a 65 pitch, so the space is 5*65 - 1 wide and one tall.
            Assert.Equal(new NeighbourhoodExtent(0, 0, 324, 64), unified.SharedOrigin);
            Assert.True(unified.SharedOrigin.Contains(323, 63));
            Assert.False(unified.SharedOrigin.Contains(324, 63));
        }

        [Fact]
        public void ALocalTile_IsTheLocalTilePlusTheOffset_AndComesBackTheSame()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));
            var studio = unified.RegionFor(NeighbourhoodModeMap.Studiotown);

            Assert.Equal(195 + 7, studio.SlotX(7));
            Assert.Equal(0 + 41, studio.SlotY(41));
            Assert.Equal(7, studio.LocalX(studio.SlotX(7)));
            Assert.Equal(41, studio.LocalY(studio.SlotY(41)));

            Assert.True(studio.Contains(195, 0));
            Assert.True(studio.Contains(195 + MapSize - 1, MapSize - 1));
            Assert.False(studio.Contains(195 + MapSize, 0));
            Assert.False(studio.Contains(194, 0));
        }

        [Fact]
        public void Extents_MoveBetweenTheTwoSpacesByTheOffset()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));
            var vacation = unified.RegionFor(NeighbourhoodModeMap.Vacation);
            var local = new NeighbourhoodExtent(8, 8, 16, 16);

            Assert.Equal(new NeighbourhoodExtent(138, 8, 146, 16), vacation.ToSlot(local));
            Assert.Equal(local, vacation.ToSlot(vacation.ToLocal(local)));
        }

        [Fact]
        public void ALot_IsMappedToTheSharedSpaceAtItsLayoutPositionPlusTheOffset()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            Assert.True(unified.TryMapLot(81, out var studio));
            Assert.True(studio.IsValid);
            Assert.Equal(NeighbourhoodModeMap.Studiotown, studio.Mode);
            Assert.Equal(81, studio.Number);
            // lot 81 is the only studio lot, so it is the first one packed: (8, 8)
            Assert.Equal(8, studio.LocalX);
            Assert.Equal(8, studio.LocalY);
            Assert.Equal(8 + unified.OffsetXFor(NeighbourhoodModeMap.Studiotown), studio.SlotX);
            Assert.Equal(8 + unified.OffsetYFor(NeighbourhoodModeMap.Studiotown), studio.SlotY);
            Assert.Equal(studio.SlotX, studio.SlotExtent.MinX);
            Assert.Equal(studio.SlotX + studio.Size, studio.SlotExtent.MaxX);
            Assert.Equal(studio.LocalExtent.MaxX + unified.OffsetXFor(NeighbourhoodModeMap.Studiotown),
                studio.SlotExtent.MaxX);
        }

        [Fact]
        public void ALotIsFoundThroughTheViewItsNumberBelongsTo()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            // 21 is Downtown, 40 is Vacation, 90 is Magic, 1 is residential - all four resolved
            // by NeighbourhoodModeMap, not by the caller.
            Assert.True(unified.TryMapLot(21, out var downtown));
            Assert.True(unified.TryMapLot(40, out var vacation));
            Assert.True(unified.TryMapLot(90, out var magic));
            Assert.True(unified.TryMapLot(1, out var home));

            Assert.Equal(NeighbourhoodModeMap.Downtown, downtown.Mode);
            Assert.Equal(NeighbourhoodModeMap.Vacation, vacation.Mode);
            Assert.Equal(NeighbourhoodModeMap.Magictown, magic.Mode);
            Assert.Equal(Residential, home.Mode);
        }

        [Fact]
        public void ALotThatThisSlotDoesNotPlace_IsNotMapped()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            // 44 is a Vacation number, and this Vacation layout only places 40 and 41.
            Assert.False(unified.TryMapLot(44, out var missing));
            Assert.False(missing.IsValid);
            Assert.Equal(0, missing.Number);
            Assert.Equal(NeighbourhoodExtent.Empty, missing.SlotExtent);
        }

        [Fact]
        public void ASharedSpaceTile_MapsBackToExactlyOneMode()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            Assert.True(unified.TryMapTile(130 + 8, 8, out var mode, out var localX, out var localY));
            Assert.Equal(NeighbourhoodModeMap.Vacation, mode);
            Assert.Equal(8, localX);
            Assert.Equal(8, localY);

            // the gap between two windows belongs to no mode: it is not ground, it is the absence
            // of an edge, and answering "the window before it" would invent a coastline.
            Assert.False(unified.TryMapTile(64, 0, out mode, out localX, out localY));
            Assert.False(unified.TryMapTile(-1, 0, out mode, out localX, out localY));
            Assert.False(unified.TryMapTile(324, 0, out mode, out localX, out localY));
        }

        [Fact]
        public void ARoundTripThroughTheOffsetReturnsTheTileItStartedFrom()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            foreach (var region in unified.Regions)
            {
                for (int x = 0; x < MapSize; x += 17)
                {
                    for (int y = 0; y < MapSize; y += 17)
                    {
                        Assert.True(unified.TryMapTile(region.SlotX(x), region.SlotY(y),
                            out var mode, out var localX, out var localY));
                        Assert.Equal(region.Mode, mode);
                        Assert.Equal(x, localX);
                        Assert.Equal(y, localY);
                    }
                }
            }
        }

        [Fact]
        public void LotAtOnAWindow_IgnoresTilesOutsideIt_AndTheGap()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));
            var downtown = unified.RegionFor(NeighbourhoodModeMap.Downtown);

            // downtown's lot 22 is the second packed lot, at local (24, 8)
            Assert.Equal(22, downtown.LotAt(downtown.SlotX(25), downtown.SlotY(9)).Number);
            Assert.Null(downtown.LotAt(0, 0));
            Assert.Null(downtown.LotAt(downtown.SlotX(40), downtown.SlotY(9)));
        }

        // ---- determinism ------------------------------------------------------------------------

        [Fact]
        public void TheCanonicalOrderIsTheResidentialViewFirst_ThenAscendingModeId()
        {
            var shuffled = new List<NeighbourhoodModeWorld>
            {
                Mode(NeighbourhoodModeMap.Magictown, 90),
                Mode(NeighbourhoodModeMap.Studiotown, 81),
                Mode(NeighbourhoodModeMap.Downtown, 21),
                Mode(Residential, 1),
                Mode(NeighbourhoodModeMap.Vacation, 40)
            };

            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, shuffled, out var unified));

            Assert.Equal(
                new[]
                {
                    Residential,
                    NeighbourhoodModeMap.Downtown,
                    NeighbourhoodModeMap.Vacation,
                    NeighbourhoodModeMap.Studiotown,
                    NeighbourhoodModeMap.Magictown
                },
                unified.Regions.Select(r => r.Mode).ToArray());
        }

        [Fact]
        public void TheOrderTheCallerListedItsModesIn_DoesNotChangeTheSpace()
        {
            var forwards = AllFiveModes();
            var backwards = new List<NeighbourhoodModeWorld>(forwards);
            backwards.Reverse();

            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, forwards, out var a));
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, backwards, out var b));

            Assert.Equal(a.Describe(), b.Describe());
            Assert.Equal(a.Regions.Count, b.Regions.Count);
            for (int i = 0; i < a.Regions.Count; i++)
            {
                Assert.Equal(a.Regions[i].Mode, b.Regions[i].Mode);
                Assert.Equal(a.Regions[i].Region, b.Regions[i].Region);
            }
        }

        [Fact]
        public void TheSameInputsTwice_ProduceTheSameSpaceAndTheSameSentence()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var a));
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var b));

            Assert.Equal(a.Describe(), b.Describe());
            Assert.Equal(a.ToString(), b.ToString());
            Assert.Equal(a.SharedOrigin, b.SharedOrigin);
            Assert.Equal(a.RegionPitch, b.RegionPitch);
        }

        [Fact]
        public void ACallerPlacedSpaceIsNormalisedOntoTheSharedOrigin()
        {
            var modes = AllFiveModes();
            // deliberately not starting at the origin, to pin that the origin is the shared
            // space's own minimum corner rather than whatever the caller happened to use
            var regions = new Dictionary<int, NeighbourhoodExtent>();
            for (int i = 0; i < modes.Count; i++)
            {
                regions[modes[i].Mode] = new NeighbourhoodExtent(1000 + i * 100, 500, 1000 + i * 100 + MapSize, 500 + MapSize);
            }

            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, modes, regions, out var unified));

            Assert.Equal(0, unified.SharedOrigin.MinX);
            Assert.Equal(0, unified.SharedOrigin.MinY);
            Assert.Equal(0, unified.OffsetXFor(Residential));
            // the caller's own 100-tile pitch survives the shift; only the whole block moves
            Assert.Equal(100, unified.OffsetXFor(NeighbourhoodModeMap.Downtown));
            Assert.Equal(200, unified.OffsetXFor(NeighbourhoodModeMap.Vacation));
            Assert.Equal(400, unified.OffsetXFor(NeighbourhoodModeMap.Magictown));
            // and the shared space is as wide as the caller's layout, not the auto pack's
            Assert.Equal(new NeighbourhoodExtent(0, 0, 4 * 100 + MapSize, MapSize), unified.SharedOrigin);
            Assert.False(unified.IsAutoPacked);
            Assert.Contains("caller-placed", unified.Describe());
        }

        [Fact]
        public void TheSameModeSetAtADifferentSlot_IsADifferentSpaceOnlyInItsName()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(0, AllFiveModes(), out var a));
            Assert.True(NeighbourhoodWorldUnification.TryUnify(3, AllFiveModes(), out var b));

            Assert.Equal(0, a.Slot);
            Assert.Equal(3, b.Slot);
            Assert.Equal(a.SharedOrigin, b.SharedOrigin);
            Assert.Equal(a.OffsetXFor(NeighbourhoodModeMap.Downtown), b.OffsetXFor(NeighbourhoodModeMap.Downtown));
        }

        // ---- the overlap decision ---------------------------------------------------------------

        [Fact]
        public void TheAutoPack_PlacesNoTwoRegionsOnTheSameGround()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            Assert.True(unified.AllRegionsApart());
            for (int a = 0; a < unified.RegionCount; a++)
            {
                for (int b = a + 1; b < unified.RegionCount; b++)
                {
                    Assert.False(NeighbourhoodWorldUnification.RegionsOverlap(
                        unified.Regions[a].Region, unified.Regions[b].Region),
                        "mode " + unified.Regions[a].Mode + " overlaps mode " + unified.Regions[b].Mode);
                }
            }
        }

        [Fact]
        public void ACallerPlacedSpace_ThatOverlaps_IsRefusedByName()
        {
            var modes = AllFiveModes();
            var regions = new Dictionary<int, NeighbourhoodExtent>();
            for (int i = 0; i < modes.Count; i++)
            {
                regions[modes[i].Mode] = new NeighbourhoodExtent(i * (MapSize + 1), 0,
                    i * (MapSize + 1) + MapSize, MapSize);
            }
            // downtown placed on top of the residential window
            regions[NeighbourhoodModeMap.Downtown] = new NeighbourhoodExtent(4, 4, 4 + MapSize, 4 + MapSize);

            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, regions, out var unified));

            Assert.NotNull(unified);
            Assert.False(unified.IsUnified);
            Assert.Equal(0, unified.RegionCount);
            Assert.Contains("overlap", unified.Failure);
            Assert.Contains("mode " + Residential, unified.Failure);
            Assert.Contains("mode " + NeighbourhoodModeMap.Downtown, unified.Failure);
            Assert.Contains("not unified", unified.Describe());
        }

        [Fact]
        public void ACallerPlacedRegionOfTheWrongSize_IsRefused()
        {
            var modes = AllFiveModes();
            var regions = new Dictionary<int, NeighbourhoodExtent>();
            for (int i = 0; i < modes.Count; i++)
            {
                regions[modes[i].Mode] = new NeighbourhoodExtent(i * (MapSize + 1), 0,
                    i * (MapSize + 1) + MapSize, MapSize);
            }
            regions[NeighbourhoodModeMap.Magictown] = new NeighbourhoodExtent(400, 0, 400 + MapSize / 2, MapSize);

            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, regions, out var unified));
            Assert.Contains("32x64", unified.Failure);
        }

        [Fact]
        public void ACallerPlacedRegionWithNoModeBehindIt_IsRefused()
        {
            var modes = AllFiveModes();
            var regions = new Dictionary<int, NeighbourhoodExtent>();
            for (int i = 0; i < modes.Count; i++)
            {
                regions[modes[i].Mode] = new NeighbourhoodExtent(i * (MapSize + 1), 0,
                    i * (MapSize + 1) + MapSize, MapSize);
            }
            regions[NeighbourhoodModeMap.BaseResidential] = new NeighbourhoodExtent(900, 0, 900 + MapSize, MapSize);

            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, regions, out var unified));
            Assert.Contains("not one of this slot's modes", unified.Failure);
        }

        [Fact]
        public void AModeWithNoRegionIsRefusedAndAnEmptyRegionIsRefused()
        {
            var modes = AllFiveModes();
            var regions = new Dictionary<int, NeighbourhoodExtent>();
            for (int i = 0; i < modes.Count; i++)
            {
                regions[modes[i].Mode] = new NeighbourhoodExtent(i * (MapSize + 1), 0,
                    i * (MapSize + 1) + MapSize, MapSize);
            }
            regions.Remove(NeighbourhoodModeMap.Studiotown);

            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, regions, out var missing));
            Assert.Contains("no region was given for mode "
                + NeighbourhoodModeMap.Studiotown, missing.Failure);

            regions[NeighbourhoodModeMap.Studiotown] = NeighbourhoodExtent.Empty;
            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, regions, out var empty));
            Assert.Contains("is empty", empty.Failure);
        }

        [Fact]
        public void ASlotWithNoModes_IsRefused()
        {
            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot,
                new List<NeighbourhoodModeWorld>(), out var unified));
            Assert.Contains("no modes", unified.Failure);

            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, null, out var nothing));
            Assert.Contains("no modes", nothing.Failure);
        }

        [Fact]
        public void AModeWithNoLayoutAndNoRaster_IsRefused()
        {
            var modes = new List<NeighbourhoodModeWorld>
            {
                Mode(Residential, 1),
                new NeighbourhoodModeWorld(NeighbourhoodModeMap.Downtown, null, null)
            };

            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, out var unified));
            Assert.Contains("neither a layout nor a raster", unified.Failure);
        }

        [Fact]
        public void TwoModesOfDifferentMapSizes_CannotShareOneSquareSpace()
        {
            // both halves of each world agree with each other, so this is two honest worlds that
            // are different sizes rather than one corrupt one - the case the refusal is for
            var smallDowntown = new NeighbourhoodLayout(NeighbourhoodModeMap.Downtown, 1234, 1,
                NeighbourhoodLayout.CurrentSchemaVersion, 32, new[]
                {
                    new NeighbourhoodLot { Number = 21, WorldX = 4, WorldY = 4, Size = 8, HasHouse = true }
                });
            var modes = new List<NeighbourhoodModeWorld>
            {
                Mode(Residential, 1),
                new NeighbourhoodModeWorld(NeighbourhoodModeMap.Downtown,
                    smallDowntown, NeighbourhoodMap.Create(32))
            };

            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, out var unified));
            Assert.Contains("one shared space needs one region size", unified.Failure);
            Assert.Contains("32 tiles square", unified.Failure);
        }

        [Fact]
        public void AModelWhoseRasterAndLayoutDisagree_IsRefused()
        {
            var layout = LayoutFor(NeighbourhoodModeMap.Downtown, 21);
            var modes = new List<NeighbourhoodModeWorld>
            {
                Mode(Residential, 1),
                new NeighbourhoodModeWorld(NeighbourhoodModeMap.Downtown,
                    layout, NeighbourhoodMap.Create(MapSize * 2))
            };

            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, out var unified));
            Assert.Contains("its layout says", unified.Failure);
        }

        [Fact]
        public void AModeNamedTwice_OrAModeThatIsNotAView_IsRefused()
        {
            var modes = new List<NeighbourhoodModeWorld>
            {
                Mode(Residential, 1),
                Mode(Residential, 2)
            };
            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, out var twice));
            Assert.Contains("twice", twice.Failure);

            modes = new List<NeighbourhoodModeWorld> { Mode(0, 1) };
            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, out var notAView));
            Assert.Contains("not a neighbourhood view", notAView.Failure);
        }

        [Fact]
        public void ARefusedSpaceAnswersNothingRatherThanGuessing()
        {
            var modes = new List<NeighbourhoodModeWorld>
            {
                Mode(Residential, 1),
                new NeighbourhoodModeWorld(NeighbourhoodModeMap.Downtown, null, null)
            };
            Assert.False(NeighbourhoodWorldUnification.TryUnify(Slot, modes, out var unified));

            Assert.False(unified.TryMapLot(1, out _));
            Assert.False(unified.TryMapTile(0, 0, out _, out _, out _));
            Assert.False(unified.TransitionBetweenModes(Residential, Residential, out _));
            Assert.False(unified.TryTransitionForLots(1, 21, out _));
            Assert.Equal(default(NeighbourhoodModeRegion), unified.RegionFor(Residential));
        }

        [Fact]
        public void ALayoutWithNoRaster_StillUnifies_BecauseTheLayoutIsEnoughToPlaceIt()
        {
            var modes = new List<NeighbourhoodModeWorld>
            {
                Mode(Residential, 1),
                new NeighbourhoodModeWorld(NeighbourhoodModeMap.Downtown,
                    LayoutFor(NeighbourhoodModeMap.Downtown, 21, 22), null)
            };

            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, modes, out var unified));
            Assert.Equal(MapSize, unified.RegionSize);
            Assert.True(unified.TryMapLot(22, out var downtown));
            Assert.False(unified.RegionFor(NeighbourhoodModeMap.Downtown).HasRaster);
            Assert.Contains("no raster", unified.Describe());
        }

        [Fact]
        public void OneModeAlone_IsASpaceOfOneWindowWithItselfAtTheOrigin()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot,
                new List<NeighbourhoodModeWorld> { Mode(Residential, 1) }, out var unified));

            Assert.Equal(1, unified.RegionCount);
            Assert.Equal(new NeighbourhoodExtent(0, 0, MapSize, MapSize), unified.SharedOrigin);
            Assert.True(unified.TryTransitionForLots(1, 2, out var sameMode));
            Assert.Equal(NeighbourhoodTravelKind.Walk, sameMode.Kind);
            Assert.False(sameMode.IsCrossMode);
        }

        // ---- the transition rule ----------------------------------------------------------------

        [Fact]
        public void CrossModeTravel_IsATransitionNotAWalk_InTheAutoPackedSpace()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            foreach (var from in unified.Regions)
            {
                foreach (var to in unified.Regions)
                {
                    if (from.Mode == to.Mode) continue;
                    Assert.True(unified.TransitionBetweenModes(from.Mode, to.Mode, out var hop));
                    Assert.True(hop.IsCrossMode);
                    Assert.Equal(NeighbourhoodTravelKind.Transition, hop.Kind);
                    Assert.True(hop.GapTiles > NeighbourhoodWorldUnification.TouchTolerance);
                    Assert.Equal(NeighbourhoodExtent.Empty, hop.SharedEdge);
                }
            }
        }

        [Fact]
        public void TheRefusalToWalk_CitesTheGapAndSaysItIsNotARoad()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            Assert.True(unified.TransitionBetweenModes(Residential, NeighbourhoodModeMap.Vacation,
                out var hop));
            // the gap is the distance between the two windows' edges, not the distance between
            // their origins: vacation's window starts at 130 and residential's ends at 64
            Assert.Equal(66, hop.GapTiles);
            Assert.Contains("66 tile(s) apart", hop.Reason);
            Assert.Contains("not a walk", hop.Reason);
            Assert.Contains("transition", hop.Describe());
        }

        [Fact]
        public void SameModeTravel_IsAWalk_OnOneRoadGraph()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            Assert.True(unified.TryTransitionForLots(1, 2, out var residential));
            Assert.Equal(NeighbourhoodTravelKind.Walk, residential.Kind);
            Assert.False(residential.IsCrossMode);
            Assert.Contains("road graph", residential.Reason);

            // 21 and 22 are both Downtown, even though a caller might not have looked
            Assert.True(unified.TryTransitionForLots(21, 22, out var downtown));
            Assert.Equal(NeighbourhoodTravelKind.Walk, downtown.Kind);
            Assert.False(downtown.IsCrossMode);
        }

        [Fact]
        public void HomeToDowntownAndBack_IsATransitionBothWays()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            Assert.True(unified.TryTransitionForLots(1, 21, out var out1));
            Assert.Equal(NeighbourhoodTravelKind.Transition, out1.Kind);
            Assert.True(out1.IsCrossMode);
            Assert.Equal(Residential, out1.FromMode);
            Assert.Equal(NeighbourhoodModeMap.Downtown, out1.ToMode);

            Assert.True(unified.TryTransitionForLots(21, 1, out var back));
            Assert.Equal(NeighbourhoodTravelKind.Transition, back.Kind);
            Assert.Equal(NeighbourhoodModeMap.Downtown, back.FromMode);
            Assert.Equal(Residential, back.ToMode);
        }

        [Fact]
        public void TwoRegionsThatTouch_AreAWalk_AndReportWhereTheyMeet()
        {
            var modes = AllFiveModes();
            // the two windows deliberately placed edge to edge, with no gap at all
            var regions = new Dictionary<int, NeighbourhoodExtent>();
            for (int i = 0; i < modes.Count; i++)
            {
                regions[modes[i].Mode] = new NeighbourhoodExtent(i * MapSize, 0,
                    i * MapSize + MapSize, MapSize);
            }

            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, modes, regions, out var unified));

            // no gap means the windows share edges, so they are not apart any more
            Assert.False(unified.AllRegionsApart());
            Assert.True(unified.TransitionBetweenModes(Residential, NeighbourhoodModeMap.Downtown, out var hop));
            Assert.Equal(NeighbourhoodTravelKind.Walk, hop.Kind);
            Assert.True(hop.IsCrossMode);
            Assert.Equal(0, hop.GapTiles);
            // the zero-width extent where the two windows meet
            Assert.Equal(MapSize, hop.SharedEdge.MinX);
            Assert.Equal(MapSize, hop.SharedEdge.MaxX);
            Assert.Equal(0, hop.SharedEdge.MinY);
            Assert.Contains("share an edge", hop.Reason);
        }

        [Fact]
        public void AOneTileGap_KeepsTheRegionsApart_AndTheHopATransition()
        {
            var modes = AllFiveModes();
            var regions = new Dictionary<int, NeighbourhoodExtent>();
            for (int i = 0; i < modes.Count; i++)
            {
                regions[modes[i].Mode] = new NeighbourhoodExtent(i * (MapSize + 1), 0,
                    i * (MapSize + 1) + MapSize, MapSize);
            }

            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, modes, regions, out var unified));
            Assert.True(unified.AllRegionsApart());
            Assert.True(unified.TransitionBetweenModes(Residential, NeighbourhoodModeMap.Downtown, out var hop));
            Assert.Equal(NeighbourhoodTravelKind.Transition, hop.Kind);
            Assert.Equal(1, hop.GapTiles);
        }

        [Fact]
        public void ATransitionToAModeThisSlotDoesNotHold_IsRefused_NotGuessedAt()
        {
            // only two modes gathered, so a hop to Studio has no window to reason about
            var modes = new List<NeighbourhoodModeWorld>
            {
                Mode(Residential, 1),
                Mode(NeighbourhoodModeMap.Downtown, 21)
            };
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, modes, out var unified));

            Assert.False(unified.TransitionBetweenModes(Residential, NeighbourhoodModeMap.Studiotown, out _));
            Assert.True(unified.TryTransitionForLots(1, 21, out _));
            Assert.True(unified.TryTransitionForLots(1, 1, out _));
        }

        [Fact]
        public void TouchAndOverlapAreTwoDifferentQuestions_AndBothArePinned()
        {
            var a = new NeighbourhoodExtent(0, 0, 10, 10);
            var edge = new NeighbourhoodExtent(10, 0, 20, 10);
            var corner = new NeighbourhoodExtent(10, 10, 20, 20);
            var oneAway = new NeighbourhoodExtent(11, 0, 21, 10);
            var overlapping = new NeighbourhoodExtent(5, 5, 15, 15);

            // touching is not overlapping: edge-to-edge windows share no tile
            Assert.True(NeighbourhoodWorldUnification.RegionsTouch(a, edge, 0));
            Assert.False(NeighbourhoodWorldUnification.RegionsOverlap(a, edge));

            // a corner counts as touching, and it shares exactly one point - which is not a tile in
            // either half-open box, so it is a walk without being an overlap
            Assert.True(NeighbourhoodWorldUnification.RegionsTouch(a, corner, 0));
            Assert.False(NeighbourhoodWorldUnification.RegionsOverlap(a, corner));

            // one tile apart is neither
            Assert.False(NeighbourhoodWorldUnification.RegionsTouch(a, oneAway, 0));
            Assert.False(NeighbourhoodWorldUnification.RegionsOverlap(a, oneAway));
            // and a tolerance of one is what makes it a walk
            Assert.True(NeighbourhoodWorldUnification.RegionsTouch(a, oneAway, 1));

            Assert.True(NeighbourhoodWorldUnification.RegionsOverlap(a, overlapping));
            Assert.True(NeighbourhoodWorldUnification.RegionsTouch(a, overlapping, 0));
            Assert.Equal(0, NeighbourhoodWorldUnification.Separation(a, overlapping));
            Assert.Equal(1, NeighbourhoodWorldUnification.Separation(a, oneAway));
            Assert.Equal(130, NeighbourhoodWorldUnification.Separation(a,
                new NeighbourhoodExtent(140, 0, 150, 10)));

            // an empty box is not a region, and cannot touch or overlap anything
            Assert.False(NeighbourhoodWorldUnification.RegionsTouch(a, NeighbourhoodExtent.Empty, 0));
            Assert.False(NeighbourhoodWorldUnification.RegionsOverlap(a, NeighbourhoodExtent.Empty));
        }

        [Fact]
        public void TheDocumentedDefaultIsPinnedInTheLogLine()
        {
            Assert.True(NeighbourhoodWorldUnification.TryUnify(Slot, AllFiveModes(), out var unified));

            var text = unified.Describe();
            Assert.StartsWith("slot 0: one shared space [0,0 -> 324,64]", text);
            Assert.Contains("5 mode region(s) of 64x64", text);
            Assert.Contains("pitch 65, 1 tile(s) apart", text);
            Assert.Contains("mode " + Residential + " +(0,0)", text);
            Assert.Contains("mode " + NeighbourhoodModeMap.Magictown + " +(260,0)", text);
            Assert.Contains("cross-mode travel is always a transition", text);
            Assert.Contains("a shared coordinate space is not a road", text);
        }

        [Fact]
        public void TheGeneratedWorldsOfOneSlotFoldIntoTheSameSpace()
        {
            var worlds = new List<NeighbourhoodGeneratedWorld>();
            foreach (var mode in new[] { Residential, NeighbourhoodModeMap.Downtown })
            {
                var layout = LayoutFor(mode, mode == Residential ? (short)1 : (short)21);
                // the lifecycle's own "slot" on a generated world is the view id, not the folder
                var plan = NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout,
                    layout.Seed, layout.GeneratorVersion, null, true);
                worlds.Add(NeighbourhoodGeneratedWorld.Built(mode, layout,
                    NeighbourhoodMap.Create(MapSize), plan));
            }

            Assert.True(NeighbourhoodWorldUnification.TryUnifyFromWorlds(Slot, worlds, out var unified));
            Assert.Equal(2, unified.RegionCount);
            Assert.Equal(0, unified.OffsetXFor(Residential));
            Assert.Equal(MapSize + 1, unified.OffsetXFor(NeighbourhoodModeMap.Downtown));
            Assert.True(unified.TryMapLot(21, out var downtown));
            Assert.Equal(NeighbourhoodModeMap.Downtown, downtown.Mode);

            Assert.False(NeighbourhoodWorldUnification.TryUnifyFromWorlds(Slot, null, out var refused));
            Assert.Contains("no generated worlds", refused.Failure);
        }
    }
}
