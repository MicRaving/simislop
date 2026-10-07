using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="NeighCameraMachine"/>, the pure decision half of the neighbourhood
    /// view's camera (ported PR #68).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, the neighbourhood screen's camera was moved ad hoc: clicking a lot zoomed
    /// in on it, clicking the background zoomed back out, switching neighbourhood reset the
    /// numbers without touching an open lot panel, and a window resize re-ran the layout but
    /// left the camera wherever it was. Nothing recorded which state the camera was in, so
    /// states were reachable that nothing could get out of - zoomed in with no lot panel, for
    /// instance, which is exactly what declining the move-in prompt used to leave behind.
    /// <see cref="UINeighborhoodSelectionPanel"/> now drives everything through this machine,
    /// which is why it is worth pinning down on its own: no graphics device, no neighbourhood
    /// content, no screen.
    /// </para>
    ///
    /// <para>
    /// The values asserted here are the ones the screen has always used, so a test failing
    /// means the camera moved somewhere the player will notice, not that a number was
    /// tidied up.
    /// </para>
    /// </remarks>
    public sealed class NeighCameraMachineTests
    {
        private static Dictionary<int, Vector2> Map(params (int lot, float x, float y)[] lots)
        {
            var map = new Dictionary<int, Vector2>();
            foreach (var (lot, x, y) in lots) map[lot] = new Vector2(x, y);
            return map;
        }

        private static readonly Vector2 Fallback = new Vector2(400f, 300f);

        // ---- the zoomed-out view -------------------------------------------------------------

        [Fact]
        public void FullScreen_CentresTheWholeNeighbourhood()
        {
            var target = NeighCameraMachine.FullScreen();

            Assert.Equal(NeighCameraMode.FullScreen, target.Mode);
            Assert.Equal(1f, target.Zoom);
            // the base bitmap is 800x600, so this is its middle
            Assert.Equal(400f, target.CenterX);
            Assert.Equal(300f, target.CenterY);
            // nothing is selected, so there is no lot panel to open
            Assert.Null(target.House);
        }

        [Fact]
        public void TweenArgs_NameTheThreePropertiesTheScreenTweens()
        {
            // UITween.To reflects over the target object by name, so a typo here is a tween
            // that quietly animates nothing
            var args = NeighCameraMachine.FullScreen().TweenArgs();

            Assert.Equal(3, args.Count);
            Assert.Equal(1f, args["Zoom"]);
            Assert.Equal(400f, args["CenterPositionX"]);
            Assert.Equal(300f, args["CenterPositionY"]);
        }

        // ---- focusing a lot -----------------------------------------------------------------

        [Fact]
        public void HouseFocus_SlidesTheCameraLeftOfTheLot()
        {
            // the lot panel covers the left half of the screen, so centring the lot dead middle
            // would hide it behind the panel
            var target = NeighCameraMachine.HouseFocus(21, new Vector2(500f, 250f), 2);

            Assert.Equal(NeighCameraMode.HouseFocus, target.Mode);
            Assert.Equal(1.5f, target.Zoom);
            Assert.Equal(320f, target.CenterX); // 500 - 180
            Assert.Equal(250f, target.CenterY); // vertical is not offset, the panel is full height
            Assert.Equal(21, target.House);
        }

        [Fact]
        public void HouseFocus_UnleashedZoomsFurtherAndSlidesLess()
        {
            // Unleashed draws its map at half scale, so it has to come in further to reach the
            // same apparent zoom, and the same 180px offset in map space would be twice as far
            // on screen
            var target = NeighCameraMachine.HouseFocus(1, new Vector2(200f, 100f), NeighCameraMachine.UnleashedMode);

            Assert.Equal(3f, target.Zoom);
            Assert.Equal(110f, target.CenterX); // 200 - 90
            Assert.Equal(100f, target.CenterY);
        }

        [Theory]
        [InlineData((ushort)1)]
        [InlineData((ushort)2)]
        [InlineData((ushort)3)]
        [InlineData((ushort)5)]
        [InlineData((ushort)7)]
        public void HouseFocus_EveryNeighbourhoodButUnleashedZoomsTheSame(ushort mode)
        {
            var target = NeighCameraMachine.HouseFocus(10, new Vector2(300f, 300f), mode);

            Assert.Equal(1.5f, target.Zoom);
            Assert.Equal(120f, target.CenterX);
        }

        // ---- nothing to focus on -------------------------------------------------------------

        [Fact]
        public void TryHouseFocus_NoSelectionFallsBackToTheWholeView()
        {
            // this is the "camera is in house-focus but the selection has gone" case, which is
            // what a resize or a cancelled move-in prompt can produce
            var ok = NeighCameraMachine.TryHouseFocus(null, Map((21, 500f, 250f)), 4, Fallback, out var target);

            Assert.False(ok);
            Assert.Equal(NeighCameraMode.FullScreen, target.Mode);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-99)]
        public void TryHouseFocus_NegativeLotIsNoSelection(int house)
        {
            Assert.False(NeighCameraMachine.TryHouseFocus(house, Map((21, 500f, 250f)), 4, Fallback, out var target));
            Assert.Equal(NeighCameraMode.FullScreen, target.Mode);
        }

        // ---- lots that are not on this map --------------------------------------------------

        [Fact]
        public void TryHouseFocus_KnownLotUsesItsPosition()
        {
            var map = Map((1, 40f, 50f), (21, 500f, 250f), (22, 600f, 260f));

            var ok = NeighCameraMachine.TryHouseFocus(22, map, 2, Fallback, out var target);

            Assert.True(ok);
            Assert.Equal(22, target.House);
            Assert.Equal(420f, target.CenterX);
            Assert.Equal(260f, target.CenterY);
        }

        [Fact]
        public void TryHouseFocus_UnknownLotCentresTheViewInsteadOfThrowing()
        {
            // A lot can be selected that this map has never heard of: the map only knows the
            // lots in its own location string (lot 99 and unparseable entries are skipped), and
            // a lot can be selected after the player has already moved to another
            // neighbourhood. Falling back keeps the lot panel opening instead of taking the
            // screen down with a KeyNotFoundException.
            var map = Map((1, 40f, 50f));

            var ok = NeighCameraMachine.TryHouseFocus(63, map, 2, new Vector2(123f, 456f), out var target);

            Assert.True(ok);
            Assert.Equal(63, target.House);
            Assert.Equal(-57f, target.CenterX); // 123 - 180
            Assert.Equal(456f, target.CenterY);
        }

        [Fact]
        public void TryHouseFocus_NoPositionMapAtAllStillResolves()
        {
            // the map is only built once a location string has been read, so it can be absent
            // altogether
            var ok = NeighCameraMachine.TryHouseFocus(63, null, 2, new Vector2(123f, 456f), out var target);

            Assert.True(ok);
            Assert.Equal(63, target.House);
            Assert.Equal(-57f, target.CenterX);
            Assert.Equal(456f, target.CenterY);
        }

        [Fact]
        public void TryHouseFocus_OutOfRangeTargetIsNeverZoomedOut()
        {
            // an absurd house number still gets a lot panel; the camera just centres the view
            var ok = NeighCameraMachine.TryHouseFocus(int.MaxValue, Map((1, 40f, 50f)), 2, Fallback, out var target);

            Assert.True(ok);
            Assert.Equal(NeighCameraMode.HouseFocus, target.Mode);
            Assert.Equal(1.5f, target.Zoom);
        }

        // ---- position lookup ----------------------------------------------------------------

        [Fact]
        public void ResolveHousePosition_PrefersTheMap()
        {
            var map = Map((7, 11f, 22f));
            Assert.Equal(new Vector2(11f, 22f), NeighCameraMachine.ResolveHousePosition(map, 7, Fallback));
        }

        [Fact]
        public void ResolveHousePosition_FallsBackForAMiss()
        {
            var map = Map((7, 11f, 22f));
            Assert.Equal(Fallback, NeighCameraMachine.ResolveHousePosition(map, 8, Fallback));
            Assert.Equal(Fallback, NeighCameraMachine.ResolveHousePosition(null, 7, Fallback));
        }
    }
}
