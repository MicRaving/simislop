/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="UIBatchPolicy"/> - the render state a UI <c>SpriteBatch.Begin</c> has
    /// to establish, and therefore the state every re-<c>Begin</c> has to restore.
    ///
    /// <para>
    /// The rule exists because <c>SpriteBatch.Begin</c> takes seven optional parameters and fills
    /// every one of them <i>wrong</i> for this UI if you leave it out. The UI establishes
    /// <c>SpriteSortMode.Immediate</c> and <c>RasterizerState.CullNone</c> once per frame in
    /// <c>UISpriteBatch.UIBegin</c>, and three places re-began the batch without both: the
    /// vector-font path in <c>UIElement.DrawLocalString</c> (per label, per frame), the tooltip
    /// loop in <c>UILayer.DrawTooltip</c>, and <c>UISpriteBatch.Resume</c> (after a render plane
    /// or a cached container). <c>Begin</c>/<c>End</c> is a flush boundary and
    /// <c>Deferred</c> groups everything between them by texture, so one label moved every
    /// overlapping quad after it in the frame behind it, for the rest of the frame. <c>Resume</c>
    /// also dropped the transform on <c>BatchMatrixStack</c>, which is the entire positioning of
    /// a cached container's contents.
    /// </para>
    /// <para>
    /// What is testable here is the decision - "what must a re-Begin use, and what is the default
    /// it must not fall back to" - not the driver calls, which need a device.
    /// </para>
    /// </summary>
    public sealed class UIBatchPolicyTests
    {
        [Fact]
        public void TheDefaultBeginSortModeIsTheOneTheUiMustNotGet()
        {
            // This is the whole finding, in one line: default(SpriteSortMode) is what a bare
            // Begin() uses, and it is not what the UI needs. If MonoGame ever renumbered the
            // enum so that Immediate became the default, this test would fail rather than the UI
            // quietly changing behaviour.
            Assert.Equal(SpriteSortMode.Deferred, default(SpriteSortMode));
            Assert.NotEqual(UIBatchPolicy.UISortMode, default(SpriteSortMode));
        }

        [Fact]
        public void TheUiDrawsInTreeOrder()
        {
            Assert.Equal(SpriteSortMode.Immediate, UIBatchPolicy.UISortMode);
            Assert.True(UIBatchPolicy.IsUISortMode(UIBatchPolicy.UISortMode));
        }

        [Fact]
        public void UiQuadsAreWoundBothWaysSoCullingHasToBeOff()
        {
            // A sprite and a negative scale are both common here, and CullCounterClockwise is
            // SpriteBatch's default. A Begin() that omits rasterizerState silently culls half
            // the UI.
            Assert.True(UIBatchPolicy.CullNone);
        }

        [Fact]
        public void ARebeginWithNoScopeFallsBackToTheFrameWideMode()
        {
            // The VFont path and the tooltip path: nothing pushed a scope, so the answer is the
            // mode UIBegin established.
            Assert.Equal(SpriteSortMode.Immediate,
                UIBatchPolicy.SortFor(UIBatchPolicy.UISortMode, null));
        }

        [Fact]
        public void ARebeginInsideAScopeKeepsTheScope()
        {
            // A cached container renders its contents into its own target with a transform and
            // with Deferred sorting on purpose (its children are hundreds of icons that do not
            // overlap, and grouping them by texture is a real saving). A vector-font string
            // inside it re-begins the batch, and that re-begin has to come back with Deferred,
            // not with the frame-wide Immediate.
            var cachedContainer = new UIBatchScope(SpriteSortMode.Deferred, null, null);

            Assert.Equal(SpriteSortMode.Deferred,
                UIBatchPolicy.SortFor(UIBatchPolicy.UISortMode, cachedContainer));
        }

        [Fact]
        public void OnlyTheScopeOrTheFrameDecidesTheSortMode()
        {
            // There is no third source. In particular Restart must never fall back to
            // SpriteBatch's own default, which is the bug. Every other SpriteSortMode is equally
            // not-the-UI-mode, so IsUISortMode is only ever true for the one answer.
            foreach (SpriteSortMode mode in System.Enum.GetValues(typeof(SpriteSortMode)))
            {
                Assert.Equal(mode == SpriteSortMode.Immediate, UIBatchPolicy.IsUISortMode(mode));
            }
        }
    }
}
