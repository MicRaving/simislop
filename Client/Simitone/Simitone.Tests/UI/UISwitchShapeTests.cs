using Simitone.Client.UI.Controls;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The switch's pill mask. Its integer predecessor clamped to an empty range when the radius
    /// was half the track height, threw, and took the whole Options dialog down with it; the
    /// texture code needs a device, so the shape is pinned here instead.
    /// </summary>
    public sealed class UISwitchShapeTests
    {
        [Fact]
        public void EveryPixelOfTheShippedPillCanBeAskedWithoutThrowing()
        {
            for (int y = 0; y < UISwitch.TrackHeight; y++)
                for (int x = 0; x < UISwitch.TrackWidth; x++)
                    UISwitch.InRoundedRect(x, y, UISwitch.TrackWidth, UISwitch.TrackHeight, UISwitch.TrackHeight / 2);
        }

        [Fact]
        public void ThePillHasABodyAndRoundedEnds()
        {
            int w = UISwitch.TrackWidth, h = UISwitch.TrackHeight, r = h / 2;
            Assert.True(UISwitch.InRoundedRect(w / 2, h / 2, w, h, r));      // centre
            Assert.True(UISwitch.InRoundedRect(w / 2, 0, w, h, r));          // straight top edge
            Assert.True(UISwitch.InRoundedRect(0, h / 2, w, h, r));          // middle of the left cap
            Assert.False(UISwitch.InRoundedRect(0, 0, w, h, r));             // corners are cut
            Assert.False(UISwitch.InRoundedRect(w - 1, h - 1, w, h, r));
        }

        [Fact]
        public void ARadiusLargerThanTheShapeIsCappedNotFatal()
        {
            Assert.True(UISwitch.InRoundedRect(5, 5, 10, 10, 50));
            Assert.False(UISwitch.InRoundedRect(0, 0, 10, 10, 50));
            Assert.False(UISwitch.InRoundedRect(-1, 5, 10, 10, 5));
        }
    }
}
