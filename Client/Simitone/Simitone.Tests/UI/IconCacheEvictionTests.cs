using System.Collections.Generic;
using System.Linq;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Panels.LiveSubpanels.Catalog;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The two icon caches that used to grow for the lifetime of the process.
    ///
    /// <para>
    /// <c>UICatalogItem.IconCache</c> held every object icon the player ever scrolled past in Buy
    /// mode, and the one method that could release them had its only call site commented out
    /// ("might want to be careful here"). <c>UIIconCache</c>'s rendered head icons had no clear
    /// method at all, and each entry costs a whole rendered 3D scene. Both are now bounded and
    /// clearable.
    /// </para>
    ///
    /// <para>
    /// The eviction <em>policy</em> is what is tested, not the caching: a <c>Texture2D</c> needs a
    /// graphics device, which a headless test has no way to get, so both caches expose the "which
    /// keys go" decision as a pure function over a key order and a count. That is the part that
    /// can actually be wrong - a policy that evicts the key being added, or one that evicts nothing,
    /// or one that is a no-op once the cache is over its bound.
    /// </para>
    /// </summary>
    public sealed class IconCacheEvictionTests
    {
        // ---- the shared policy, exercised through both caches ---------------------------------

        [Fact]
        public void NothingIsEvictedWhileTheCacheIsWithinItsBound()
        {
            Assert.Empty(UICatalogItem.KeysToEvict(new uint[] { 1, 2, 3 }, count: 3, capacity: 3, incoming: 3));
            Assert.Empty(UICatalogItem.KeysToEvict(new uint[] { 1, 2 }, count: 2, capacity: 3, incoming: 2));
            Assert.Empty(UIIconCache.KeysToEvict(new[] { "a", "b" }, count: 2, capacity: 3, incoming: "b"));
        }

        [Fact]
        public void ExactlyTheOverflowIsEvicted()
        {
            // 5 keys, a bound of 3, so two have to go - no more, which would throw away live
            // textures, and no fewer, which is the leak this is fixing.
            var evicted = UICatalogItem.KeysToEvict(
                new uint[] { 1, 2, 3, 4, 5 }, count: 5, capacity: 3, incoming: 5);

            Assert.Equal(2, evicted.Count);
            Assert.Equal(new uint[] { 1, 2 }, evicted);
        }

        [Fact]
        public void TheKeyBeingAddedIsNeverTheOneEvicted()
        {
            // The catastrophic version of this bug: the caller adds a key, the cache evicts it,
            // and the row is left with a null icon even though the cache "has" it.
            var evicted = UICatalogItem.KeysToEvict(
                new uint[] { 1, 2, 3, 4 }, count: 4, capacity: 1, incoming: 1);

            Assert.DoesNotContain(1u, evicted);
            Assert.Equal(3, evicted.Count);
            Assert.Equal(new uint[] { 2, 3, 4 }, evicted);
        }

        [Fact]
        public void TheSameHoldsForTheHeadIconCache()
        {
            var evicted = UIIconCache.KeysToEvict(
                new[] { "a", "b", "c", "d" }, count: 4, capacity: 2, incoming: "a");

            Assert.DoesNotContain("a", evicted);
            Assert.Equal(new[] { "b", "c" }, evicted);
        }

        [Fact]
        public void AKeyThatHasBeenUsedAgainIsNotEvictedFirst()
        {
            // Least-recently-used, not first-come: the objects a player keeps coming back to are
            // the ones they are shopping for, and a re-use moves a key to the young end. The
            // order is a history, so 1 appears twice and its *last* use is what counts - which is
            // the entry at index 2, after 2's. So 2 goes first and 1 second: neither is the
            // untouched 3, and the re-used 1 is not the first casualty.
            var evicted = UICatalogItem.KeysToEvict(
                new uint[] { 1, 2, 1, 3, 4 }, count: 4, capacity: 2, incoming: 4);

            Assert.Equal(new uint[] { 2, 1 }, evicted);
        }

        [Fact]
        public void ADuplicateEntryIsOnlyEvictedOnce()
        {
            // The key order is a history, not a set: a key that was touched twice has two entries
            // and must only be dropped once, or the count the caller asked for is not what the
            // cache ends up with.
            var evicted = UIIconCache.KeysToEvict(
                new[] { "a", "a", "a", "b" }, count: 2, capacity: 1, incoming: "b");

            Assert.Equal(new[] { "a" }, evicted);
        }

        [Fact]
        public void AZeroOrNegativeBoundEvictsNothingRatherThanThrowing()
        {
            // Defensive: a bound of zero would mean "cache nothing", which is not what any caller
            // means, and throwing here would take a Buy-mode panel down.
            Assert.Empty(UICatalogItem.KeysToEvict(new uint[] { 1, 2 }, count: 2, capacity: 0, incoming: 2));
            Assert.Empty(UIIconCache.KeysToEvict(new[] { "a" }, count: 1, capacity: -1, incoming: "a"));
        }

        [Fact]
        public void AMissingOrderEvictsNothingRatherThanThrowing()
        {
            Assert.Empty(UICatalogItem.KeysToEvict(null, count: 9, capacity: 3, incoming: 1));
        }

        [Fact]
        public void AnEmptyOrderCannotEvictTheOverflowItCannotName()
        {
            // count and order disagreeing is not a state production reaches; the honest answer is
            // to do nothing rather than to guess and dispose a texture that is still in use.
            Assert.Empty(UICatalogItem.KeysToEvict(new uint[0], count: 9, capacity: 3, incoming: 1));
        }

        // ---- the caches themselves ---------------------------------------------------------------

        [Fact]
        public void BothCachesAreBoundedAndBothCanBeCleared()
        {
            // The bound is a number a headless test can check even though the values are
            // Texture2Ds, and a clear path is the one the review found missing entirely.
            Assert.True(UICatalogItem.MaxCachedIcons > 0);
            Assert.True(UIIconCache.MaxCachedHeadIcons > 0);

            //Clearing an empty cache is a no-op rather than a null dereference on the dictionary.
            UICatalogItem.ClearIconCache();
            UIIconCache.ClearAvatarHeadCache();
            Assert.Empty(UICatalogItem.IconCache);
            Assert.Equal(0, UIIconCache.CachedHeadCount);
        }

        [Fact]
        public void TheCatalogIconCacheIsNoLongerReassignable()
        {
            // It was a `public static Dictionary` field, so any caller could replace the whole
            // cache and orphan every texture in it. A fixed reference is one fewer accident.
            var field = typeof(UICatalogItem).GetField("IconCache");
            Assert.NotNull(field);
            Assert.True(field.IsInitOnly, "IconCache must not be assignable");
        }

        [Fact]
        public void TheBoundIsSmallEnoughToBeABoundAndLargeEnoughToBeUseful()
        {
            // Not a magic number: big enough that paging a category and coming back is free,
            // small enough that the whole cache is a rounding error next to the lot's textures.
            Assert.InRange(UICatalogItem.MaxCachedIcons, 64, 1024);
            Assert.InRange(UIIconCache.MaxCachedHeadIcons, 16, 256);
        }

        [Fact]
        public void TheTwoCachesDoNotShareTheirBounds()
        {
            // They hold different things at different costs - a decoded BMP against a rendered
            // scene - so one number for both would be one number wrong for one of them.
            Assert.NotEqual(UICatalogItem.MaxCachedIcons, UIIconCache.MaxCachedHeadIcons);
        }

        [Fact]
        public void BothCachesHoldDistinctKeyKinds()
        {
            // A regression guard on the shape of the fix: one is keyed by an object GUID, the other
            // by a "mesh:texture" string. Sharing an implementation would mean one eviction
            // routine casting the other's keys.
            var catalog = typeof(UICatalogItem).GetField("IconCache");
            var heads = typeof(UIIconCache).GetField("AvatarHeadCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(catalog);
            Assert.NotNull(heads);
            Assert.Equal(typeof(Dictionary<uint, Microsoft.Xna.Framework.Graphics.Texture2D>), catalog.FieldType);
            Assert.Equal(typeof(Dictionary<string, Microsoft.Xna.Framework.Graphics.Texture2D>), heads.FieldType);
        }
    }
}
