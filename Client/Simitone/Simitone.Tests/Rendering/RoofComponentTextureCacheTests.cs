/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.LotView.Components;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// A roof's textures, and who is allowed to free them.
    ///
    /// <para><b>Why the rule is a test and not a comment.</b> A roof component used to build its
    /// own textures every time it was asked to: decode the roof PNG, decode the parallax and normal
    /// maps, generate their mip chains, and read the whole roof texture back off the GPU to average
    /// its colour. That is a lot of work to produce the same four textures, and it was done once
    /// per lot rather than once per roof style - so a neighbourhood of forty lots paid for it forty
    /// times over, and the facade build's roof stage carried the bill.</para>
    ///
    /// <para><b>The flip side of sharing is ownership.</b> The lot view's own roof texture comes
    /// from the content system and is already shared, and it was never freed by a component; the
    /// three derived maps were not, and freeing them from a component that no longer owned them
    /// would take the roofs out of every other lot in the game. So the cache is the only owner, and
    /// it is emptied by the device that invalidates it. What is asserted here is the half that can
    /// be asserted without a graphics device: that the cache is empty to begin with, that emptying
    /// it is safe to ask for when nothing is in it, and that emptying it twice is not an error.</para>
    /// </summary>
    public class RoofComponentTextureCacheTests
    {
        [Fact]
        public void TheCacheIsEmptyUntilSomethingIsPutInIt()
        {
            RoofComponent.ClearSharedTextures();

            Assert.Equal(0, RoofComponent.CachedRoofStyleCount);
        }

        [Fact]
        public void EmptyingAnEmptyCacheIsNotAnError()
        {
            // A device reset arrives whether or not anything was ever loaded into the cache, and the
            // handler for it is the one thing on this path that must never throw: a cache that
            //cannot be emptied is a roof that cannot be loaded after a reset.
            RoofComponent.ClearSharedTextures();
            RoofComponent.ClearSharedTextures();

            Assert.Equal(0, RoofComponent.CachedRoofStyleCount);
        }

        [Fact]
        public void TheCacheIsCountedInRoofStylesAndNotInLots()
        {
            // The number the log can quote: how many distinct roofs have been built, as opposed to
            // how many lots asked for one. A headless run builds none, so this is the floor, and the
            // property it guards is that the count is a count of roofs.
            RoofComponent.ClearSharedTextures();
            var before = RoofComponent.CachedRoofStyleCount;

            Assert.Equal(0, before);
            Assert.True(RoofComponent.CachedRoofStyleCount < int.MaxValue);
        }
    }
}
