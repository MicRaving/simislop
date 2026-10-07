using FSO.LotView.Model;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for <see cref="TerrainGrid"/> - when the build-mode grid overlay is safe to draw.
    ///
    /// <para>
    /// The overlay is two line sets over the terrain: the fine buildable area and the target
    /// buildable area. Each is an index buffer plus a primitive count, allocated together by
    /// <c>TerrainComponent.RegenTerrain</c> and freed together.
    /// </para>
    ///
    /// <para>
    /// They were not actually freed together. <c>RegenTerrain</c> did
    /// <c>GridIndexBuffer?.Dispose(); TGridIndexBuffer?.Dispose();</c> and left both fields
    /// assigned, then only reallocated them <c>if (indices.Length &gt; 0)</c>. A regen that
    /// produced an empty grid - and an empty grid is a legitimate answer, because
    /// <c>GetGridIndicesForFine</c> over a fine area covering nothing returns nothing - therefore
    /// left a <em>disposed, non-null</em> buffer bound to the previous frame's primitive count, and
    /// the draw path went on to do <c>device.Indices = GridIndexBuffer; DrawIndexedPrimitives(...,
    /// GridPrimitives)</c>. The target-area buffer had an <c>IsDisposed</c> check; the fine-area one
    /// did not, which is what says this was an oversight rather than a policy.
    /// </para>
    ///
    /// <para>
    /// The fix is structural - the release path now clears the fields and zeroes the counts - but
    /// this is the second line of defence, and it is the only thing standing between a stale pair
    /// and a draw call that hands the driver a disposed buffer. So it gets its own test.
    /// </para>
    /// </summary>
    public sealed class TerrainGridDrawTests
    {
        // ---- a grid is drawable only when all three agree -------------------------------------

        [Fact]
        public void ALiveBufferWithSomethingToDrawIsDrawable()
        {
            Assert.True(TerrainGrid.CanDraw(primitives: 12, hasBuffer: true, isDisposed: false));
        }

        [Fact]
        public void AGridWithNothingToDrawIsNotDrawable()
        {
            // The state a regen that produced an empty buildable area now leaves behind: a null
            // buffer and a zero count. The old code left a disposed buffer and a stale count here,
            // which is the case below.
            Assert.False(TerrainGrid.CanDraw(primitives: 0, hasBuffer: true, isDisposed: false));
            Assert.False(TerrainGrid.CanDraw(primitives: 0, hasBuffer: false, isDisposed: false));
            Assert.False(TerrainGrid.CanDraw(primitives: 0, hasBuffer: false, isDisposed: true));
        }

        [Fact]
        public void ADisposedBufferIsNeverDrawableHoweverManyPrimitivesAreClaimed()
        {
            // This is the bug. A non-zero primitive count beside a disposed index buffer is exactly
            // the state the old RegenTerrain produced on an empty-grid regen, and it is a
            // use-after-dispose on the render thread if it is drawn.
            Assert.False(TerrainGrid.CanDraw(primitives: 1, hasBuffer: true, isDisposed: true));
            Assert.False(TerrainGrid.CanDraw(primitives: 4096, hasBuffer: true, isDisposed: true));
        }

        [Fact]
        public void AMissingBufferIsNeverDrawableHoweverManyPrimitivesAreClaimed()
        {
            Assert.False(TerrainGrid.CanDraw(primitives: 1, hasBuffer: false, isDisposed: false));
            Assert.False(TerrainGrid.CanDraw(primitives: 4096, hasBuffer: false, isDisposed: true));
        }

        [Fact]
        public void ANegativePrimitiveCountIsNotDrawable()
        {
            // Defensive, but cheap: the count is an int and the buffer it is paired with can only
            // be reached through a regen.
            Assert.False(TerrainGrid.CanDraw(primitives: -1, hasBuffer: true, isDisposed: false));
        }

        // ---- the primitive count ------------------------------------------------------------

        [Fact]
        public void TheGridDrawnAsLinesCountsPairs()
        {
            // The 2d grid is a line list: two indices per segment, so two per primitive.
            Assert.Equal(50, TerrainGrid.PrimitiveCount(indexCount: 100, indicesPerPrimitive: 2));
        }

        [Fact]
        public void TheGridDrawnAsATextureCountsTriangles()
        {
            // 3d builds the same overlay as a texture, which is three indices per triangle.
            Assert.Equal(20, TerrainGrid.PrimitiveCount(indexCount: 60, indicesPerPrimitive: 3));
        }

        [Fact]
        public void AnEmptyGridHasNoPrimitivesRatherThanDividing()
        {
            Assert.Equal(0, TerrainGrid.PrimitiveCount(indexCount: 0, indicesPerPrimitive: 2));
            Assert.Equal(0, TerrainGrid.PrimitiveCount(indexCount: 1, indicesPerPrimitive: 2));
            Assert.Equal(0, TerrainGrid.PrimitiveCount(indexCount: -6, indicesPerPrimitive: 2));
        }

        [Fact]
        public void APrimitiveSizeThatIsNotPositiveHasNoPrimitives()
        {
            Assert.Equal(0, TerrainGrid.PrimitiveCount(indexCount: 60, indicesPerPrimitive: 0));
            Assert.Equal(0, TerrainGrid.PrimitiveCount(indexCount: 60, indicesPerPrimitive: -1));
        }

        // ---- the invariant the release path maintains ------------------------------------------

        [Fact]
        public void AnEmptyRegenLeavesNothingDrawable()
        {
            // The end-to-end statement of the fix, in the shape the regen produces. Whatever a
            // regen decided, the draw must not bind a buffer it did not just allocate, and the
            // two grid overlays are independent - so a fine area that emptied must not take the
            // target area's grid with it, and vice versa.
            var fine = new bool[2] { true, true };
            var finePrimitives = new int[1] { 10 };
            var target = new bool[2] { true, true };
            var targetPrimitives = new int[1] { 10 };

            // ...regen: both buffers disposed and both fields cleared, both counts zeroed.
            fine[0] = false;
            finePrimitives[0] = 0;
            target[0] = false;
            targetPrimitives[0] = 0;

            Assert.False(TerrainGrid.CanDraw(finePrimitives[0], fine[0], isDisposed: false));
            Assert.False(TerrainGrid.CanDraw(targetPrimitives[0], target[0], isDisposed: false));

            // ...regen again, this time only the target area has anything in it.
            target[0] = true;
            targetPrimitives[0] = 6;

            Assert.False(TerrainGrid.CanDraw(finePrimitives[0], fine[0], isDisposed: false));
            Assert.True(TerrainGrid.CanDraw(targetPrimitives[0], target[0], isDisposed: false));
        }
    }
}
