using FSO.LotView.Platform;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for the two render-target decisions the 3D lot platform makes, plus the portal
    /// depth-mask decision - all of which used to be written as a statement that could not fail.
    /// </summary>
    /// <remarks>
    /// A GPU resource cannot be created or drawn headlessly, so what is under test here is the
    /// <em>decision</em>: whether the thing has to be rebuilt at all. Each of these was an
    /// assignment followed by a test of the assignment, which is why the leak survived - the
    /// condition was provably always true, so the check read as a null guard and behaved as a
    /// "always allocate".
    /// </remarks>
    public sealed class ThumbnailAndDepthMaskTests
    {
        // ---- the lot thumbnail target -----------------------------------------------------------

        [Fact]
        public void AThumbnailTargetOfTheRightSizeIsKept()
        {
            // The case that was leaking. `LotThumbTarget = null;` on the line above
            // `if (LotThumbTarget == null)` cannot produce anything but true, so a fresh
            // size x size x 8 byte target was allocated for every catalog tile, every lot button
            // and every neighbour page - and the one it replaced was unreachable from Dispose,
            // because the field had been cleared.
            Assert.False(WorldPlatform3D.NeedsThumbTarget(
                hasTarget: true, targetDisposed: false, targetWidth: 1024, targetHeight: 1024, size: 1024));
        }

        [Fact]
        public void NoThumbnailTargetMeansThereIsOneToMake()
        {
            Assert.True(WorldPlatform3D.NeedsThumbTarget(
                hasTarget: false, targetDisposed: false, targetWidth: 0, targetHeight: 0, size: 1024));
        }

        [Fact]
        public void ADisposedThumbnailTargetIsRebuiltEvenAtTheRightSize()
        {
            // A device reset leaves the fields populated and the resources disposed-but-present, so
            // a null test is not enough - the same shape as the terrain grid buffers, and the same
            // reason this asks about the disposed flag separately.
            Assert.True(WorldPlatform3D.NeedsThumbTarget(
                hasTarget: true, targetDisposed: true, targetWidth: 1024, targetHeight: 1024, size: 1024));
        }

        [Fact]
        public void AThumbnailTargetOfTheWrongSizeIsReplaced()
        {
            // The lot thumbnail is bp.Width * 16 for TS1 and 576 otherwise, so its size genuinely
            // changes - between lots, and between a TS1 and a non-TS1 install.
            Assert.True(WorldPlatform3D.NeedsThumbTarget(
                hasTarget: true, targetDisposed: false, targetWidth: 1024, targetHeight: 1024, size: 576));
            Assert.True(WorldPlatform3D.NeedsThumbTarget(
                hasTarget: true, targetDisposed: false, targetWidth: 576, targetHeight: 576, size: 1024));
        }

        [Fact]
        public void AThumbnailTargetOfTheWrongShapeIsReplacedEvenAtTheRightWidth()
        {
            // Checked on both axes: a square request satisfied by a non-square target is still a
            // target of the wrong shape, and the lot would come out stretched.
            Assert.True(WorldPlatform3D.NeedsThumbTarget(
                hasTarget: true, targetDisposed: false, targetWidth: 1024, targetHeight: 512, size: 1024));
        }

        [Fact]
        public void TheObjectThumbnailSizeIsTheOneTheDrawMathsAssumes()
        {
            // GetObjectThumb projects its bounding points through a hard-coded 512, which is half of
            // this. If the allocation size moves, that half has to move with it, so the constant
            // is named rather than written twice.
            Assert.Equal(1024, WorldPlatform3D.ObjectThumbSize);
            Assert.Equal(WorldPlatform3D.ObjectThumbSize / 2, 512);
            Assert.False(WorldPlatform3D.NeedsThumbTarget(
                hasTarget: true, targetDisposed: false,
                targetWidth: WorldPlatform3D.ObjectThumbSize, targetHeight: WorldPlatform3D.ObjectThumbSize,
                size: WorldPlatform3D.ObjectThumbSize));
        }

        // ---- the portal depth mask --------------------------------------------------------------

        [Fact]
        public void APortalWithADrawableMaskDrawsItsStencilClear()
        {
            // The ordinary path: mask present, mask has a vertex buffer.
            Assert.True(DGRPRenderer.DepthMaskDrawable(hasDepthMask: true, depthMaskHasVerts: true));
        }

        [Fact]
        public void APortalWithNoMaskAtAllIsNotDrawn()
        {
            // The crash. `MaskType` and `DepthMask` are read from the FSOM separately - the mask
            // chunk is only constructed when the mask type is greater than None, so a file whose
            // mask type says Portal and whose mask chunk is missing arrives with one set and the
            // other null. The ordinary draw path always guarded this; the portal stencil-clear
            // branch did not, and threw a NullReferenceException on the render thread with the
            // device left bound to whatever the previous object was drawing into.
            Assert.False(DGRPRenderer.DepthMaskDrawable(hasDepthMask: false, depthMaskHasVerts: false));
            Assert.False(DGRPRenderer.DepthMaskDrawable(hasDepthMask: false, depthMaskHasVerts: true));
        }

        [Fact]
        public void AMaskWithNoVertexBufferIsNotDrawn()
        {
            // The other half of the same guard, and the one the old inner `geom.Verts != null`
            // test covered - kept as one rule so the two call sites cannot disagree about it again.
            Assert.False(DGRPRenderer.DepthMaskDrawable(hasDepthMask: true, depthMaskHasVerts: false));
        }

        // ---- the device state the lighting pass has to hand back ---------------------------------

        [Fact]
        public void ADeviceStateSnapshotCarriesAllThreePiecesOfState()
        {
            // The reason this is a value-equality struct and not a bag of locals: a restore is
            // only as complete as what was captured, and this makes "captured everything" a
            // property that can be asserted. If a fourth piece of state is ever added and
            // forgotten here, the round trip below stops comparing equal.
            var blend = BlendState.AlphaBlend;
            var rasterizer = RasterizerState.CullNone;
            var scissor = new Microsoft.Xna.Framework.Rectangle(12, 34, 56, 78);

            var snapshot = new DeviceStateSnapshot(blend, rasterizer, scissor);

            Assert.Same(blend, snapshot.Blend);
            Assert.Same(rasterizer, snapshot.Rasterizer);
            Assert.Equal(scissor, snapshot.Scissor);
        }

        [Fact]
        public void TwoSnapshotsOfTheSameStateAreEqual()
        {
            var first = new DeviceStateSnapshot(BlendState.Opaque, RasterizerState.CullNone, new Microsoft.Xna.Framework.Rectangle(1, 2, 3, 4));
            var second = new DeviceStateSnapshot(BlendState.Opaque, RasterizerState.CullNone, new Microsoft.Xna.Framework.Rectangle(1, 2, 3, 4));

            Assert.True(first == second);
            Assert.False(first != second);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
        }

        [Fact]
        public void ADifferentBlendStateIsADifferentSnapshot()
        {
            // The lighting pass installs Opaque, Mul, Min, Max, AddRed and MaxGreen as it goes; if
            // the restore compared equal whatever the blend state, restoring would be a no-op for
            // that one field and the lot would be drawn with the last room's blend.
            var opaque = new DeviceStateSnapshot(BlendState.Opaque, RasterizerState.CullNone, Microsoft.Xna.Framework.Rectangle.Empty);
            var alpha = new DeviceStateSnapshot(BlendState.AlphaBlend, RasterizerState.CullNone, Microsoft.Xna.Framework.Rectangle.Empty);

            Assert.True(opaque != alpha);
        }

        [Fact]
        public void ADifferentRasterizerIsADifferentSnapshot()
        {
            // The one that actually bites. The 2D world batch sets a blend state and never a
            // rasterizer state, so a lighting pass that leaves the scissor test on hands the lot a
            // scissor rectangle sized for the last room it drew.
            var plain = new DeviceStateSnapshot(BlendState.Opaque, RasterizerState.CullNone, Microsoft.Xna.Framework.Rectangle.Empty);
            var scissored = new DeviceStateSnapshot(BlendState.Opaque, RasterizerState.CullCounterClockwise, Microsoft.Xna.Framework.Rectangle.Empty);

            Assert.True(plain != scissored);
        }

        [Fact]
        public void ADifferentScissorRectangleIsADifferentSnapshot()
        {
            // The rasterizer state is shared, so the rectangle is the only thing distinguishing
            // one room's clip from the next - and it is the piece most likely to be forgotten
            // because setting `ScissorTestEnable` and setting the rectangle look like one action.
            var first = new DeviceStateSnapshot(BlendState.Opaque, RasterizerState.CullNone, new Microsoft.Xna.Framework.Rectangle(0, 0, 10, 10));
            var second = new DeviceStateSnapshot(BlendState.Opaque, RasterizerState.CullNone, new Microsoft.Xna.Framework.Rectangle(10, 10, 10, 10));

            Assert.True(first != second);
        }

        [Fact]
        public void CapturingFromNoDeviceIsAnEmptySnapshotRatherThanAThrow()
        {
            // A scope can be opened before the device exists (a thumbnail world built during
            // startup), and the default snapshot restores to nothing rather than throwing.
            var snapshot = DeviceStateSnapshot.Capture(null);

            Assert.Null(snapshot.Blend);
            Assert.Null(snapshot.Rasterizer);
            Assert.Equal(Microsoft.Xna.Framework.Rectangle.Empty, snapshot.Scissor);
            snapshot.RestoreTo(null);
        }
    }
}
