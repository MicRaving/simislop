using Simitone.Client.UI.Panels;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the arithmetic behind the free will button on the live-mode strip.
    ///
    /// <para>
    /// <c>UIButtonSubpanel</c> lays its buttons out on a fixed pitch and the main panel's own
    /// constants mirror it, so the position of a slot is arithmetic rather than something to
    /// read off a screenshot. That matters because the last slot is only built when the whole
    /// strip is on screen: a button past the right edge renders but cannot be clicked, so the
    /// "is there room" decision has to agree with the positions the strip is actually drawn at.
    /// </para>
    ///
    /// <para>
    /// It matters a second time, which is why the slot the toggle takes is pinned here. It sits
    /// on the same pitch as the Options buttons rather than being laid out after them, so a
    /// count that drifts out of step with the button list is not a gap - it is a button drawn on
    /// top of the one before it.
    /// </para>
    /// </summary>
    public sealed class OptionsStripLayoutTests
    {
        /// <summary>
        /// The Options buttons: Save, Neighbourhood, Settings, Quit. Credits used to be the
        /// fifth; it is no longer offered here, so this is a fourth and the toggle's slot moved
        /// down with it.
        /// </summary>
        private const int ExistingOptionsButtons = UIMainPanel.OptionsStripOptionsButtonCount;

        [Fact]
        public void TheOptionsCategoryIsSaveNeighbourhoodSettingsAndQuit()
        {
            Assert.Equal(4, ExistingOptionsButtons);
        }

        /// <summary>
        /// The toggle takes the first slot no Options button uses. Derived in the panel rather
        /// than counted here, so this only has to catch the two drifting apart.
        /// </summary>
        [Fact]
        public void TheFreeWillSlotIsTheFirstSlotNoOptionsButtonTakes()
        {
            Assert.Equal(ExistingOptionsButtons, UIMainPanel.FreeWillSlot);
        }

        [Fact]
        public void FirstButtonSitsWhereTheStripStarts()
        {
            Assert.Equal(50f, UIMainPanel.OptionsStripButtonX(0));
        }

        [Fact]
        public void FirstLabelSitsTwentySevenPixelsLeftOfItsButton()
        {
            Assert.Equal(23f, UIMainPanel.OptionsStripLabelX(0));
        }

        [Theory]
        [InlineData(0, 50f)]
        [InlineData(1, 170f)]
        [InlineData(3, 410f)]
        [InlineData(4, 530f)]
        public void SlotsAreOneHundredAndTwentyApart(int slot, float expected)
        {
            Assert.Equal(expected, UIMainPanel.OptionsStripButtonX(slot));
        }

        /// <summary>
        /// The pitch has to hold at every slot, not just the ones enumerated above: a slot that
        /// drifts would silently overlap the button beside it.
        /// </summary>
        [Fact]
        public void EverySlotIsTheSameDistanceFromTheOneBefore()
        {
            for (int slot = 1; slot <= 8; slot++)
            {
                Assert.Equal(UIMainPanel.OptionsStripSlotSpacing,
                    UIMainPanel.OptionsStripButtonX(slot) - UIMainPanel.OptionsStripButtonX(slot - 1));
            }
        }

        /// <summary>Labels track their buttons, so they never drift onto the next slot.</summary>
        [Fact]
        public void LabelsAreTheSameDistanceLeftOfTheirButtonInEverySlot()
        {
            for (int slot = 0; slot <= 8; slot++)
            {
                Assert.Equal(27f, UIMainPanel.OptionsStripButtonX(slot) - UIMainPanel.OptionsStripLabelX(slot));
            }
        }

        /// <summary>
        /// A 120px label hangs over a 50px button, so the pitch is what keeps the two apart. If
        /// it ever stopped clearing, every slot would be drawn over the next one - which is what
        /// a stale toggle slot looks like.
        /// </summary>
        [Fact]
        public void EachLabelClearsTheButtonOfTheSlotAfterIt()
        {
            for (int slot = 0; slot < UIMainPanel.FreeWillSlot; slot++)
            {
                var labelRight = UIMainPanel.OptionsStripLabelX(slot) + UIMainPanel.OptionsStripLabelWidth;
                var nextButtonLeft = UIMainPanel.OptionsStripButtonX(slot + 1);

                Assert.True(labelRight < nextButtonLeft,
                    "the label in slot " + slot + " reaches " + labelRight
                    + " and the button in slot " + (slot + 1) + " starts at " + nextButtonLeft);
            }
        }

        /// <summary>
        /// The four Options buttons fit a 1024-wide window with room to spare, which is the
        /// least a normal desktop window is.
        /// </summary>
        [Fact]
        public void ExistingStripFitsANarrowDesktopWindow()
        {
            Assert.True(UIMainPanel.OptionsStripRequiredWidth(ExistingOptionsButtons) <= 1024);
        }

        /// <summary>
        /// The Options buttons on their own now clear 800px - the engine's minimum supported
        /// width - which five of them did not. The toggle is the only thing still past the edge
        /// there, and it is left off rather than built unreachable.
        /// </summary>
        [Fact]
        public void TheOptionsButtonsOnTheirOwnFitTheMinimumWindow()
        {
            Assert.True(UIMainPanel.OptionsStripRequiredWidth(ExistingOptionsButtons) <= 800);
        }

        /// <summary>
        /// The last slot still is past the edge at 800px, so on the narrowest window the game
        /// supports the button is deliberately not built.
        /// </summary>
        [Fact]
        public void AddingTheFreeWillSlotOverflowsTheMinimumWindow()
        {
            Assert.True(UIMainPanel.OptionsStripRequiredWidth(ExistingOptionsButtons + 1) > 800);
        }

        /// <summary>And comfortably fits a normal one.</summary>
        [Fact]
        public void AddingTheFreeWillSlotFitsAComfortableWindow()
        {
            Assert.True(UIMainPanel.OptionsStripRequiredWidth(ExistingOptionsButtons + 1) <= 1280);
        }

        /// <summary>
        /// The width the fit decision is made on is the width of the whole strip, toggle
        /// included. If these two ever disagree the button is built on a screen it does not fit
        /// on, or left off a screen it would have fitted.
        /// </summary>
        [Fact]
        public void TheFitDecisionMeasuresTheWholeStrip()
        {
            Assert.Equal(
                UIMainPanel.OptionsStripRequiredWidth(ExistingOptionsButtons + 1),
                UIMainPanel.OptionsStripRequiredWidth(UIMainPanel.FreeWillSlot + 1));
        }

        /// <summary>
        /// Measured to the right edge of the last <em>label</em>, not the last button: a label is
        /// 120px wide under a 50px button, so it overhangs and it is what would be clipped.
        /// </summary>
        [Fact]
        public void RequiredWidthCoversTheLastLabelNotTheLastButton()
        {
            var slots = 3;
            var lastButtonRight = UIMainPanel.OptionsStripLeft + UIMainPanel.OptionsStripButtonX(slots - 1)
                + UIMainPanel.OptionsStripButtonWidth;
            var required = UIMainPanel.OptionsStripRequiredWidth(slots);

            Assert.True(required > lastButtonRight);
        }

        /// <summary>A wider strip is never narrower - the fit decision depends on that.</summary>
        [Fact]
        public void RequiredWidthGrowsWithTheSlotCount()
        {
            for (int slots = 1; slots < 10; slots++)
            {
                Assert.True(UIMainPanel.OptionsStripRequiredWidth(slots + 1)
                    > UIMainPanel.OptionsStripRequiredWidth(slots));
            }
        }

        /// <summary>An empty strip needs nothing, rather than going negative.</summary>
        [Fact]
        public void RequiredWidthIsZeroForAnEmptyStrip()
        {
            Assert.Equal(0, UIMainPanel.OptionsStripRequiredWidth(0));
            Assert.Equal(0, UIMainPanel.OptionsStripRequiredWidth(-1));
        }
    }
}
