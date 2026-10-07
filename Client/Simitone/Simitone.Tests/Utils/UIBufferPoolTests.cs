/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using FSO.Client.UI.Framework;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Tests for <see cref="UIBufferPool{T}"/>, the UI's pool of render targets.
    ///
    /// <para>
    /// The pool used to be two <c>List&lt;RenderTarget2D&gt;</c> fields on <c>UISpriteBatch</c>
    /// and the rules were implicit. All three failures below were reachable from
    /// <c>UIRenderPlane</c>, and the second one was permanent:
    /// </para>
    /// <list type="number">
    /// <item><b>Exhaustion cleared the screen.</b> <c>GetBuffer()</c> returned <c>null</c> once
    /// the pool ran dry - which is what a nested <c>WithBuffer</c> does - and the
    /// <c>UIRenderPlane</c> constructor went on to <c>GD.SetRenderTarget(null);
    /// GD.Clear(...)</c>. Binding null <i>is</i> the back buffer, so a nested render plane
    /// cleared the frame that was already on screen.</item>
    /// <item><b>The pool was poisoned forever.</b> On dispose, <c>FreeBuffer(null)</c> pushed
    /// that null into the free list, and <c>GetBuffer()</c>'s <c>Count &gt; 0</c> check then
    /// handed <c>null</c> to every later caller. One exhaustion and the UI's render planes were
    /// broken for the rest of the session.</item>
    /// <item><b>Double free.</b> There was no disposed flag, so a manual <c>Dispose</c> plus a
    /// <c>using</c> block put the same <c>RenderTarget2D</c> in the list twice, and two owners
    /// ended up drawing into one target.</item>
    /// </list>
    ///
    /// <para>
    /// The pool is generic over the resource precisely so these rules can be tested without a
    /// graphics device; <c>UISpriteBatch</c> instantiates it over <c>RenderTarget2D</c>.
    /// </para>
    /// </summary>
    public sealed class UIBufferPoolTests
    {
        /// <summary>A stand-in for a render target that remembers whether it was disposed.</summary>
        private sealed class Buffer
        {
            public readonly int Width;
            public readonly int Height;
            public bool Disposed;

            public Buffer(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        private sealed class Factory
        {
            public int Built;
            public int FailAfter = int.MaxValue;
            public readonly List<Buffer> Created = new List<Buffer>();

            public Buffer Create(int width, int height)
            {
                if (Built >= FailAfter) { throw new System.InvalidOperationException("device said no"); }
                Built++;
                var buffer = new Buffer(width, height);
                Created.Add(buffer);
                return buffer;
            }
        }

        private static UIBufferPool<Buffer> Pool(Factory factory, int initial = 2, int max = 4, int w = 64, int h = 32)
        {
            return new UIBufferPool<Buffer>(factory.Create, b => b.Disposed = true, initial, max, w, h);
        }

        // ---- exhaustion must not hand out nothing --------------------------------------------

        [Fact]
        public void AnEmptyPoolGrowsInsteadOfHandingOutNull()
        {
            // The failure that cleared the screen. Renting past the initial size must produce a
            // real target, because the caller binds whatever it gets.
            var factory = new Factory();
            var pool = Pool(factory, initial: 1, max: 4);

            var first = pool.Rent();
            var second = pool.Rent();

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual<Buffer>(first, second);
            Assert.Equal(2, pool.Total);
        }

        [Fact]
        public void ThePoolStopsGrowingAtItsCap()
        {
            // Growth has to be bounded, or an accidental recursion of render planes allocates
            // render targets until the device refuses. At the cap the honest answer is null -
            // which is a different thing from "ran dry", and the caller is required to cope.
            var factory = new Factory();
            var pool = Pool(factory, initial: 0, max: 2);

            Assert.NotNull(pool.Rent());
            Assert.NotNull(pool.Rent());
            Assert.Null(pool.Rent());
            Assert.Equal(2, factory.Built);
        }

        [Fact]
        public void AFailedBuildReturnsNullRatherThanThrowing()
        {
            // A device that refuses a render target has to be survivable mid-frame; the old code's
            // equivalent case was an unhandled exception from the constructor.
            var factory = new Factory { FailAfter = 0 };
            var pool = Pool(factory, initial: 0, max: 2);

            Assert.Null(pool.Rent());
            Assert.Equal(0, pool.Total);
        }

        [Fact]
        public void TwoRentersNeverGetTheSameBuffer()
        {
            var pool = Pool(new Factory(), initial: 2, max: 2);

            var a = pool.Rent();
            var b = pool.Rent();

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.NotEqual<Buffer>(a, b);
            Assert.Equal(0, pool.Available);
        }

        // ---- returning null must not poison anything ------------------------------------------

        [Fact]
        public void ReturningNullIsIgnored()
        {
            // The permanent-damage bug. FreeBuffer(null) used to push null into the free list, so
            // GetBuffer's Count > 0 check then returned null to everyone, forever.
            var factory = new Factory();
            var pool = Pool(factory, initial: 1, max: 1);

            pool.Return(null);

            Assert.Equal(1, pool.Available);
            var buffer = pool.Rent();
            Assert.NotNull(buffer);
            Assert.NotSame(null, buffer);
        }

        [Fact]
        public void ReturningNullRepeatedlyNeverStarvesThePool()
        {
            var pool = Pool(new Factory(), initial: 2, max: 2);
            for (var i = 0; i < 100; i++)
            {
                pool.Return(null);
            }

            Assert.Equal(2, pool.Available);
            Assert.NotNull(pool.Rent());
            Assert.NotNull(pool.Rent());
        }

        [Fact]
        public void ReturningTheSameBufferTwicePoolsItOnce()
        {
            // A manual Dispose plus a using block did exactly this, and the second copy was
            // handed to a different render plane, which then drew over the first.
            var pool = Pool(new Factory(), initial: 1, max: 1);
            var buffer = pool.Rent();

            pool.Return(buffer);
            pool.Return(buffer);
            pool.Return(buffer);

            Assert.Equal(1, pool.Available);
            var again = pool.Rent();
            Assert.Same(buffer, again);
            Assert.Equal(0, pool.Available);
        }

        [Fact]
        public void ABufferThePoolDoesNotOwnIsNotPooled()
        {
            var pool = Pool(new Factory(), initial: 1, max: 1);
            pool.Return(new Buffer(1, 1));

            Assert.Equal(1, pool.Available);
        }

        // ---- disposal ---------------------------------------------------------------------------

        [Fact]
        public void DisposeReleasesEveryBufferAndIsIdempotent()
        {
            var pool = Pool(new Factory(), initial: 3, max: 3);
            var buffers = new List<Buffer>();
            for (var i = 0; i < 3; i++)
            {
                buffers.Add(pool.Rent());
            }

            pool.Dispose();
            pool.Dispose();

            Assert.All(buffers, b => Assert.True(b.Disposed));
            Assert.True(pool.IsDisposed);
        }

        [Fact]
        public void ADisposedPoolHandsOutNothing()
        {
            // A use-after-dispose of a GraphicsResource is a crash, not a wrong pixel.
            var pool = Pool(new Factory(), initial: 2, max: 2);
            pool.Dispose();

            Assert.Null(pool.Rent());
            Assert.Equal(0, pool.Total);
        }

        // ---- resize -----------------------------------------------------------------------------

        [Fact]
        public void ResizeBuildsTheWholeReplacementBeforeReleasingTheOldSet()
        {
            // A window resize arrives from the window thread while the render thread can be part
            // way through a frame. The pool must never be empty in the gap, and must never hand
            // out something it has already disposed.
            var factory = new Factory();
            var pool = Pool(factory, initial: 3, max: 3, w: 64, h: 32);
            var old = new List<Buffer>();
            for (var i = 0; i < 3; i++)
            {
                old.Add(pool.Rent());
            }

            Assert.True(pool.Replace(128, 96));

            Assert.All(old, b => Assert.True(b.Disposed));
            Assert.Equal(3, pool.Available);
            var fresh = pool.Rent();
            Assert.False(fresh.Disposed);
            Assert.Equal(128, fresh.Width);
            Assert.Equal(96, fresh.Height);
        }

        [Fact]
        public void AFailedResizeKeepsThePreviousSet()
        {
            // The other half of the same race: if the device refuses the replacements, the UI
            // keeps the buffers it had rather than ending up with none.
            var factory = new Factory();
            var pool = Pool(factory, initial: 2, max: 2);
            var first = pool.Rent();

            factory.FailAfter = 0;
            Assert.False(pool.Replace(256, 256));

            Assert.Equal(2, factory.Built);      // nothing new was built
            Assert.Equal(1, pool.Available);    // and the old set is still all there
            var reused = pool.Rent();
            Assert.NotNull(reused);
            Assert.NotEqual<Buffer>(first, reused);   // first is still checked out
            Assert.False(reused.Disposed);
            Assert.Equal(64, reused.Width);     // still the old size
        }

        [Fact]
        public void AResizeThatFailsHalfwayDiscardsWhatItManagedToBuild()
        {
            // The partial replacement must be released, and the set the pool is still holding
            // must not be: the whole point of building first is that a device which refuses half
            // the replacements leaves the UI with the buffers it already had.
            var factory = new Factory();
            var pool = Pool(factory, initial: 3, max: 3);
            var old = new List<Buffer>();
            for (var i = 0; i < 3; i++)
            {
                old.Add(pool.Rent());
            }

            // Two of the three replacements succeed, the third is refused.
            factory.FailAfter = 5;
            Assert.False(pool.Replace(256, 256));

            var replacements = factory.Created.GetRange(3, 2);
            Assert.All(replacements, b => Assert.True(b.Disposed));   // not leaked
            Assert.All(old, b => Assert.False(b.Disposed));           // still in use

            // And the old set is still poolable, which is the only thing that matters to a caller
            // that is mid-frame: it gives the three back and gets those same three again.
            Assert.Equal(0, pool.Available);                          // all three still checked out
            foreach (var buffer in old) { pool.Return(buffer); }
            Assert.Equal(3, pool.Available);
        }
    }
}
