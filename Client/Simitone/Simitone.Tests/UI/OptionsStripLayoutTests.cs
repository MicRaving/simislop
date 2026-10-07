using Simitone.Client.UI.Panels;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the arithmetic behind the live-mode Options strip.
    ///
    /// <para>
    /// <c>UIButtonSubpanel</c> lays its buttons out on a fixed pitch and the main panel's own
    /// constants mirror it, so the position of a slot is arithmetic rather than something to
    /// read off a screenshot. The constants and the strip's own layout have to agree, or a
    /// button is drawn on top of the one before it.
    /// </para>
    /// </summary>
    public sealed class OptionsStripLayoutTests
    {
        /// <summary>
        /// The Options buttons: Save, Neighbourhood, Settings, Quit.
        /// </summary>
        private const int ExistingOptionsButtons = UIMainPanel.OptionsStripOptionsButtonCount;

        [Fact]
        public void TheOptionsCategoryIsSaveNeighbourhoodSettingsAndQuit()
        {
            Assert.Equal(4, ExistingOptionsButtons);
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
            for (int slot = 0; slot < ExistingOptionsButtons; slot++)
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
        /// One more slot than the Options buttons is past the edge at 800px, and comfortably fits
        /// a normal desktop window.
        /// </summary>
        [Fact]
        public void OneMoreSlotOverflowsTheMinimumWindowButFitsAComfortableOne()
        {
            Assert.True(UIMainPanel.OptionsStripRequiredWidth(ExistingOptionsButtons + 1) > 800);
            Assert.True(UIMainPanel.OptionsStripRequiredWidth(ExistingOptionsButtons + 1) <= 1280);
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
