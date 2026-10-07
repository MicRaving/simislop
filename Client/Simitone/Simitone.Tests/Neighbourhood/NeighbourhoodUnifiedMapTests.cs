/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// §8's one connected map: the three claims this workstream can be held to without a graphics
    /// device, a content provider or a screen.
    ///
    /// <para>
    /// <b>One authority for the map.</b> Before, the neighbourhood screen and the in-lot surround each
    /// built a <see cref="NeighbourhoodSceneSource"/> over the same generated world, and that type
    /// clones the raster in its constructor - so "one connected map" was two 3 MB copies and two
    /// answers to "where is lot 21". <see cref="NeighbourhoodSharedWorld"/> is now the only thing
    /// either of them asks for a map through, and the assertion here is reference identity rather
    /// than equality of contents.
    /// </para>
    ///
    /// <para>
    /// <b>The click and the buttons agree (D1).</b> The lot buttons are hidden by
    /// <see cref="UINeighborhoodSelectionPanel.PaintedMapLayersVisible"/> and the world click is gated
    /// by <see cref="UINeighborhoodSelectionPanel.WorldClickIsEnabled"/>. They used to be two
    /// different rules reading two different settings, and the shipped defaults made them disagree: the
    /// view setting is 3D and every Part B capability flag is off, so a fresh install had neither
    /// buttons nor a click and the only way to open a house was Next and Previous.
    /// </para>
    ///
    /// <para>
    /// <b>The create-a-family button is top-left.</b> The switcher rebuilds its whole column on every
    /// neighbourhood switch, so "it happens to be first today" is not a property anybody can rely on.
    /// The index and the geometry are named, and both are asserted here instead of being photographed.
    /// </para>
    ///
    /// <para>
    /// <b>Why none of this can be asserted end to end.</b> The remaining half of §8.3 - the lot view
    /// being a camera state of the map rather than a second scene - is not testable here at all: it
    /// needs a graphics device, a lot <c>World</c> and a live <c>VM</c>. What is testable is that the
    /// world both views read is one object, that the two views' input rules cannot disagree, and that
    /// the button the owner asked to keep is where it was asked to be.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodUnifiedMapTests
    {
        private const int Slot = 1;
        private const int MapSize = 64;
        private const int Seed = 1234;

        public NeighbourhoodUnifiedMapTests()
        {
            NeighbourhoodSharedWorld.InvalidateAll();
        }

        // ------------------------------------------------ the shared world, and the clone it saves

        [Fact]
        public void TwoViewsOfOneNeighbourhoodGetTheSameWorldObject()
        {
            // The claim §8.1 makes and §8.6 turns into "a lot sits at its LotLocations position
            // relative to its neighbours": if the map view and the lot view each held their own
            // NeighbourhoodSceneSource they would each hold their own raster, and nothing in the tree
            // would make the two agree except that they were built from the same inputs.
            var layout = Layout();
            var map = NeighbourhoodMap.Create(MapSize);

            var mapView = NeighbourhoodSharedWorld.Acquire(Slot, layout, map);
            var lotView = NeighbourhoodSharedWorld.Acquire(Slot, layout, map);

            Assert.Same(mapView, lotView);
            Assert.Same(mapView.Map, lotView.Map);
            //a value, so equality rather than identity - and equal only because it is one mapping
            //object's worth of numbers, which is the point being made
            Assert.Equal(mapView.Mapping, lotView.Mapping);
            Assert.Same(layout, mapView.Layout);
        }

        [Fact]
        public void TheSharedWorldHoldsACopyRatherThanTheCachedRaster()
        {
            // Unchanged from the rule the clone existed for, and it still holds after the sharing: the
            // authority's entry is keyed on the map instance the lifecycle cache holds, and what the
            // views get is a copy of it. NeighbourhoodWorldCache is what the next visit and the
            // refresh both read, so a view must not be able to reach it.
            var map = NeighbourhoodMap.Create(MapSize);
            var source = NeighbourhoodSharedWorld.Acquire(Slot, Layout(), map);

            Assert.NotSame(map, source.Map);
            Assert.True(map.ContentEquals(source.Map));
        }

        [Fact]
        public void AWorldThatMovedReplacesTheSharedOneRatherThanBeingServedFromIt()
        {
            // The case the identity test exists for. A bulldoze re-places a lot, which produces a new
            // layout and a new raster; serving the old source would put a street and a house back on a
            // lot that was just emptied, and it would do it silently - the map would still be the right
            // size and the right terrain.
            var before = Layout();
            var beforeMap = NeighbourhoodMap.Create(MapSize);
            var first = NeighbourhoodSharedWorld.Acquire(Slot, before, beforeMap);

            var after = Layout();
            var afterMap = NeighbourhoodMap.Create(MapSize);
            Assert.NotSame(before, after);
            var second = NeighbourhoodSharedWorld.Acquire(Slot, after, afterMap);

            Assert.NotSame(first, second);
            Assert.Same(after, second.Layout);
            Assert.Equal(1, NeighbourhoodSharedWorld.Count);
            Assert.True(NeighbourhoodSharedWorld.Holds(Slot, after, afterMap));
            Assert.False(NeighbourhoodSharedWorld.Holds(Slot, before, beforeMap));
        }

        [Fact]
        public void DroppingTheNeighbourhoodDropsTheSharedWorldWithIt()
        {
            // "Current" has to mean the same thing to the renderer and to the lifecycle. The cache is
            // dropped by NeighbourhoodLifecycle.InvalidateLot/InvalidateSlot on a bulldoze, a rezone or
            // a switch; an entry left behind would be a world nothing considers current any more, and
            // the next acquire for that slot would have to notice.
            var layout = Layout();
            var map = NeighbourhoodMap.Create(MapSize);
            NeighbourhoodSharedWorld.Acquire(Slot, layout, map);
            Assert.Equal(1, NeighbourhoodSharedWorld.Count);

            Assert.True(NeighbourhoodSharedWorld.Invalidate(Slot));
            Assert.Equal(0, NeighbourhoodSharedWorld.Count);
            Assert.Null(NeighbourhoodSharedWorld.Current(Slot));
            Assert.False(NeighbourhoodSharedWorld.Invalidate(Slot));
        }

        [Fact]
        public void BothViewsAreToldWhereALotIsInTheSharedSpace()
        {
            // The unification is no longer a plan nobody calls. One window in the shared space sits at
            // offset (0, 0), so a shared-space tile is this view's own tile - which is the property the
            // single-scene change needs, and the reason adding a second window later cannot silently
            // move a lot that code already believed in.
            var layout = Layout();
            var map = NeighbourhoodMap.Create(MapSize);
            NeighbourhoodSharedWorld.Acquire(Slot, layout, map);

            var entry = NeighbourhoodSharedWorld.Current(Slot);
            Assert.NotNull(entry);
            Assert.True(entry.IsUnified, entry.Failure);
            Assert.Equal(0, entry.Unification.OffsetXFor(Slot));
            Assert.Equal(0, entry.Unification.OffsetYFor(Slot));

            NeighbourhoodSharedLot placed;
            Assert.True(entry.TryMapLot(24, out placed));
            Assert.Equal(24, placed.Number);
            Assert.Equal(placed.LocalX, placed.SlotX);
            Assert.Equal(placed.LocalY, placed.SlotY);
            Assert.Equal(layout.FindLot(24).WorldX, placed.LocalX);
            Assert.Equal(layout.FindLot(24).WorldY, placed.LocalY);
        }

        [Fact]
        public void AWorldWithNoMapIsNeverSharedAndAlwaysSaysWhy()
        {
            // The 2D fallback's contract, now behind the authority rather than in the panel. A
            // neighbourhood that cannot generate still has to open, and it has to say what happened.
            var source = NeighbourhoodSharedWorld.Acquire(Slot, Layout(), null);

            Assert.NotNull(source);
            Assert.False(source.HasWorld);
            Assert.Equal(0, NeighbourhoodSharedWorld.Count);
            Assert.False(string.IsNullOrWhiteSpace(source.Failure));
        }

        [Fact]
        public void EveryHandoutIsCounted()
        {
            // The number that says the sharing is doing anything: one entry and one clone per slot,
            // however many views ask for it. If this ever reads one per handout the sharing has silently
            // become the duplication it replaced.
            var layout = Layout();
            var map = NeighbourhoodMap.Create(MapSize);

            var first = NeighbourhoodSharedWorld.Acquire(Slot, layout, map);
            var second = NeighbourhoodSharedWorld.Acquire(Slot, layout, map);
            var third = NeighbourhoodSharedWorld.Acquire(Slot, layout, map);

            Assert.Same(first, second);
            Assert.Same(second, third);
            Assert.Equal(1, NeighbourhoodSharedWorld.Count);
            Assert.Equal(3, NeighbourhoodSharedWorld.Handouts);
            Assert.Contains("3 time(s)", NeighbourhoodSharedWorld.Describe());
        }

        // ------------------------------------------------ D1: the click and the buttons, one rule

        [Fact]
        public void TheWorldClickAndTheLotButtonsCanNeverDisagree()
        {
            // D1's acceptance, over every decision this panel can reach and not over the two a reader
            // would pick. Whatever is true of a world, the painted map's lot buttons are on screen
            // exactly when the world is not carrying the picture - and the click that replaces them is
            // on exactly when the buttons are off. Both directions, because a rule that disabled
            // everything would pass the direction that matters and break the 2D map.
            var checked_ = 0;
            foreach (var decision in EveryDecision())
            {
                var buttons = UINeighborhoodSelectionPanel.PaintedMapLayersVisible(decision);
                var click = UINeighborhoodSelectionPanel.WorldClickIsEnabled(decision);

                Assert.False(buttons && click);
                Assert.Equal(buttons, !click);
                checked_++;
            }

            Assert.True(checked_ >= 7, "the sweep covered only " + checked_ + " decision(s)");
        }

        [Fact]
        public void TheWorldClickFollowsTheViewAndNotTheCapabilityFlags()
        {
            // The reachability half, and the case a player hit on a fresh install. The view setting is
            // the player's instruction and defaults to 3D; the Part B flags default off. A world that
            // is carrying the picture has already hidden the lot sprites, so refusing the click there
            // produced a screen with no way to open a house.
            var carrying = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, Carrying());
            Assert.True(carrying.Show3D, carrying.Reason);
            Assert.False(UINeighborhoodSelectionPanel.PaintedMapLayersVisible(carrying));
            Assert.True(UINeighborhoodSelectionPanel.WorldClickIsEnabled(carrying));

            //and the 2D side: the painted bitmap is what the player is looking at, so a click is a
            //click on the sprites and the world has nothing to say about it
            var painted = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.Bitmap2D, Carrying());
            Assert.False(painted.Show3D);
            Assert.True(UINeighborhoodSelectionPanel.PaintedMapLayersVisible(painted));
            Assert.False(UINeighborhoodSelectionPanel.WorldClickIsEnabled(painted));

            //the warm-up is the same case as 2D: the painted map is still on screen over the world, so
            //a click must still be a click on the bitmap
            var warming = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, WarmingUp());
            Assert.False(warming.Show3D);
            Assert.False(UINeighborhoodSelectionPanel.WorldClickIsEnabled(warming));
        }

        // ------------------------------------------------ the create-a-family button, top-left

        [Fact]
        public void TheCreateAFamilyButtonIsTheTopLeftOne()
        {
            // §8.1's fourth requirement. The switcher rebuilds its column on every neighbourhood
            // switch, so this is a fact about the code rather than about today's layout - which is why
            // the index is a named constant the build asserts on and the geometry is a pure function
            // rather than a loop written out in LayBtns.
            Assert.Equal(0, UINeighbourhoodSwitcher.CreateFamilyButtonIndex);

            var first = UINeighbourhoodSwitcher.LeftButtonPosition(UINeighbourhoodSwitcher.CreateFamilyButtonIndex);
            var second = UINeighbourhoodSwitcher.LeftButtonPosition(
                UINeighbourhoodSwitcher.CreateFamilyButtonIndex + 1);

            //top left: the smallest x and the smallest y in the column, both well inside the window
            Assert.Equal(UINeighbourhoodSwitcher.LeftColumnX, first.X);
            Assert.Equal(UINeighbourhoodSwitcher.LeftColumnY, first.Y);
            //one column, so the second button is below the first and not beside it
            Assert.Equal(first.X, second.X);
            Assert.True(first.Y < second.Y, "the column does not run downwards");
            Assert.True(first.Y >= 0 && first.X >= 0, "the first button is off the top-left corner");
        }

        [Fact]
        public void TheNeighbourhoodOpensOnTheWholeConnectedMap()
        {
            // §8.1's first requirement. The panel's initial camera state is the zoomed-out target with
            // no lot selected - which is what FinishPopulate resets to, what Neighbourhood3DView's
            // constructor focuses on, and what FullScreen() is. Pinned here because it is the only one
            // of the three requirements that is entirely a matter of the numbers below.
            var initial = NeighCameraMachine.FullScreen();

            Assert.Equal(NeighCameraMode.FullScreen, initial.Mode);
            Assert.Equal(NeighCameraMachine.FullScreenZoom, initial.Zoom);
            Assert.Null(initial.House);
            //centred on the bitmap, which is the middle of the connected map under
            // NeighbourhoodPixelTransform - the whole map in frame, not a crop of it
            Assert.Equal(NeighCameraMachine.DefaultCenterX, initial.CenterX);
            Assert.Equal(NeighCameraMachine.DefaultCenterY, initial.CenterY);
        }

        // ------------------------------------------------------- helpers

        private static NeighbourhoodWorldHealth Carrying() => new NeighbourhoodWorldHealth(
            attached: true, meshUploaded: true, terrainTriangles: 24576, roadTriangles: 4096,
            groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 12,
            secondsSinceAttach: 2d, submittedTriangles: 24576, submittedFrames: 5,
            facadeTriangles: 4128, facadesVisible: 12, facadeEligible: 12);

        private static NeighbourhoodWorldHealth WarmingUp() => new NeighbourhoodWorldHealth(
            attached: true, meshUploaded: false, terrainTriangles: 0, roadTriangles: 0,
            groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 0,
            secondsSinceAttach: 0d);

        private static NeighbourhoodViewDecision[] EveryDecision()
        {
            return new[]
            {
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.Off, Carrying()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.Bitmap2D, Carrying()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, WarmingUp()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, Carrying()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    new NeighbourhoodWorldHealth(failure: "the road graph could not be made connected")),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    new NeighbourhoodWorldHealth(attached: true, groundQuads: 0, roadQuads: 0,
                        lotCount: 12, secondsSinceAttach: 60d)),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    new NeighbourhoodWorldHealth(attached: true, groundQuads: 0, roadQuads: 0,
                        lotCount: 12)),
            };
        }

        /// <summary>
        /// A small neighbourhood with lot 24 in it, packed the ordinary way. 64 tiles rather than 512 so
        /// the test spends nothing on generation - nothing here generates - and so that a layout
        /// identity failure is obvious rather than a 3 MB comparison.
        /// </summary>
        private static NeighbourhoodLayout Layout()
        {
            return NeighbourhoodLayoutBuilder.Pack(FourLots(), Slot, Seed, MapSize);
        }

        private static IReadOnlyList<NeighbourhoodLotRead> FourLots()
        {
            //Two rows of two, so lot 24 has neighbours in both axes and a rectangle that is not
            //degenerate in either.
            return new[]
            {
                Read(20, 4, 4), Read(21, 4, 40), Read(22, 40, 4), Read(24, 40, 40),
            };
        }

        private static NeighbourhoodLotRead Read(short number, int x, int y)
        {
            return new NeighbourhoodLotRead
            {
                Number = number,
                Size = 16,
                MapX = x,
                MapY = y,
                HasHouse = true,
            };
        }
    }
}
