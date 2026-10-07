/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Common.Utils;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for the two halves of R-10 / R-39: a static GPU resource cache that has to survive
    /// a device reset, and a render target factory that has to honour the depth format it is
    /// given.
    ///
    /// <para>
    /// <c>TextureGenerator</c> holds about seventeen <c>private static Texture2D</c> fields, each
    /// guarded by <c>if (X == null)</c>, and <c>null</c> is the only signal those getters have. A
    /// reset disposes them without nulling them, so the guard does not fire and the next caller
    /// is handed a disposed texture. The client's fullscreen toggle is the trigger. The only fix
    /// available from this tree is <c>TextureGenerator.ResetAll</c>, called from
    /// <c>UILayer.GraphicsDevice_DeviceReset</c>; the other half of the finding,
    /// <c>WorldContent.ResetGPU</c>, lives in tso.world and is not this tree's to write.
    /// </para>
    ///
    /// <para>
    /// <c>PPXDepthEngine.CreateRenderTarget</c> took a <c>DepthFormat</c> and ignored it, handing
    /// out Depth24Stencil8 for everything - including the two 2D world buffers that ask for
    /// <c>DepthFormat.None</c> and never depth test, which is roughly twice the memory of the
    /// colour surface for nothing.
    /// </para>
    ///
    /// <para>
    /// Non-parallel because <c>ResetAll</c> clears process-global static state.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class StaticGpuResourceTests
    {
        [Fact]
        public void ARequestedDepthFormatIsHonoured()
        {
            // The finding. DepthFormat.None used to come back as Depth24Stencil8.
            Assert.Equal(DepthFormat.None, PPXDepthEngine.ResolveDepthFormat(DepthFormat.None, softwareDepth: false));
            Assert.Equal(DepthFormat.Depth24, PPXDepthEngine.ResolveDepthFormat(DepthFormat.Depth24, softwareDepth: false));
            Assert.Equal(DepthFormat.Depth24Stencil8, PPXDepthEngine.ResolveDepthFormat(DepthFormat.Depth24Stencil8, softwareDepth: false));
            Assert.Equal(DepthFormat.Depth16, PPXDepthEngine.ResolveDepthFormat(DepthFormat.Depth16, softwareDepth: false));
        }

        [Fact]
        public void TheSoftwareDepthPathKeepsItsStencil()
        {
            // The one exception, and it is a compatibility guard rather than a policy.
            // _2DWorldBatch.GenBuffers asks for
            //   (AlwaysDS[i] || (!UseMRT && !SoftwareDepth)) ? depthformat : DepthFormat.None
            // and with software depth on that is None for the static-depth target - the one
            // PPXDepthEngine.RenderPPXDepth stencil-writes into. It has only ever worked because
            // this function ignored the argument. Software depth clears the target white, draws
            // a stencil comparison and samples it back, so a target with no stencil renders the
            // whole 2D world in whatever order the sprites happen to be in.
            Assert.Equal(DepthFormat.Depth24Stencil8, PPXDepthEngine.ResolveDepthFormat(DepthFormat.None, softwareDepth: true));
        }

        [Fact]
        public void AnExplicitDepthFormatIsNotOverriddenBySoftwareDepth()
        {
            // The guard is only about the None case. A caller that asked for a specific format
            // has said what it means.
            Assert.Equal(DepthFormat.Depth16, PPXDepthEngine.ResolveDepthFormat(DepthFormat.Depth16, softwareDepth: true));
            Assert.Equal(DepthFormat.Depth24, PPXDepthEngine.ResolveDepthFormat(DepthFormat.Depth24, softwareDepth: true));
        }

        [Fact]
        public void ResettingTheTextureCacheWithNothingInItIsANoOp()
        {
            // DeviceReset is raised from a driver callback, so it runs in whatever state the
            // process happens to be in - including before anything has been generated, and
            // including more than once. Neither may throw. ResetAll disposes before it nulls, so
            // the second pass has to see nulls rather than already-disposed resources, and
            // nothing may be rebuilt: a rebuild here would need a graphics device, and a device
            // reset is precisely the moment the old one is gone.
            TextureGenerator.ResetAll();
            TextureGenerator.ResetAll();
        }
    }
}
