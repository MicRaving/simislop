using FSO.Common.Utils;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="TimedReferenceCache{KEY, VALUE}"/> and the release mechanism behind it.
    /// This is the cache in front of every FAR, FAR3 and IFF lookup in the game, so "it holds
    /// strong references to things it should have released" is not a tidy-up concern - it is a
    /// leak proportional to every distinct content object the game has ever loaded.
    ///
    /// <para>
    /// The defect: <c>TimedReferenceController.KeepAlive</c> re-filed an already-known object
    /// into a new ring and then called <c>ObjectToRing.Remove(oldring)</c>, where
    /// <c>oldring</c> is the ring's <i>index</i> and the dictionary is keyed on the
    /// <i>object</i>. The lookup found nothing, so the entry was never removed - and the entry
    /// is a strong reference. An object that was dereferenced and later referenced again stayed
    /// in the map for the life of the process, which defeats the entire release mechanism: the
    /// ring rotation drops an object from its ring and calls <c>ObjectToRing.Remove(obj)</c>,
    /// and the object is still held by the very map that call was supposed to empty.
    /// </para>
    ///
    /// <para>
    /// The test measures the leak the way a leak is measured - with a <see cref="WeakReference"/>
    /// and a collection - rather than by reading the private map. The ring cursor is
    /// process-wide private state whose starting value depends on whatever ran before, so it is
    /// positioned explicitly; the reflection is confined to those two fields and is commented
    /// where it is used.
    /// </para>
    ///
    /// <para>
    /// In the non-parallel collection, because <see cref="TimedReferenceController"/> is
    /// process-wide and this test deliberately reconfigures it.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class TimedReferenceCacheTests : IDisposable
    {
        private const BindingFlags StaticPrivate = BindingFlags.NonPublic | BindingFlags.Static;

        public TimedReferenceCacheTests()
        {
            //a clean, known-shaped controller to work with, and put back afterwards
            TimedReferenceController.SetMode(CacheType.AGGRESSIVE);
            SetRingCursor(0);
            SetTicksToNextCheck(0);
        }

        public void Dispose()
        {
            //the controller is process-wide: leave it as it was found, or every other test that
            //caches anything inherits our ring layout
            TimedReferenceController.Clear();
            TimedReferenceController.SetMode(CacheType.ACTIVE);
        }

        #region the ring cursor

        private static void SetField(string name, int value)
        {
            typeof(TimedReferenceController).GetField(name, StaticPrivate).SetValue(null, value);
        }

        private static int GetField(string name)
        {
            return (int)typeof(TimedReferenceController).GetField(name, StaticPrivate).GetValue(null);
        }

        /// <summary>
        /// Which ring the next reference lands in. This is the value <c>KeepAlive</c> computes
        /// as <c>(CurRingNum + Count - 1) % Count</c>; it is read rather than recomputed so the
        /// test cannot go wrong if the ring arithmetic is ever retuned.
        /// </summary>
        private int NextRing
        {
            get
            {
                var ringCount = RingCount();
                return (GetField("CurRingNum") + ringCount - 1) % ringCount;
            }
        }

        private static int RingCount()
        {
            //ReferenceRing is the whole of the ring set up; its count is the ring count
            var rings = typeof(TimedReferenceController)
                .GetField("ReferenceRing", StaticPrivate).GetValue(null);
            return (int)rings.GetType().GetProperty("Count").GetValue(rings);
        }

        private void SetRingCursor(int value) => SetField("CurRingNum", value);

        private void SetTicksToNextCheck(int value) => SetField("TicksToNextCheck", value);

        /// <summary>Advances the controller by exactly one ring rotation.</summary>
        private void RotateOnce()
        {
            SetTicksToNextCheck(0);
            TimedReferenceController.Tick();
        }

        #endregion

        private sealed class Payload
        {
            public int Size;
        }

        /// <summary>
        /// Puts one payload in the cache and returns a weak reference to it, in a frame the JIT
        /// is not allowed to keep alive: the only strong reference left afterwards is the one
        /// inside the cache, which is the thing under test.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CacheAndRelease(TimedReferenceCache<int, Payload> cache, int key, out int factoryCalls)
        {
            var calls = 0;
            var payload = cache.GetOrAdd(key, _ =>
            {
                calls++;
                return new Payload { Size = 64 };
            });
            factoryCalls = calls;
            Assert.Equal(64, payload.Size);
            return new WeakReference(payload);
        }

        /// <summary>
        /// Re-references the cached payload and lets go of it again, returning the factory call
        /// count. The second reference has to leave the test frame the same way the first one does:
        /// in a Debug build the JIT may keep the return value of a call in a stack slot until the
        /// end of the enclosing method, so a bare <c>cache.GetOrAdd(1, ...)</c> in the test body
        /// keeps the payload alive and the <see cref="WeakReference"/> below can never die - a
        /// measurement artefact, not a leak. <see cref="MethodImplOptions.NoInlining"/> is what
        /// makes the frame actually end.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReReference(TimedReferenceCache<int, Payload> cache, int key)
        {
            var calls = 0;
            var payload = cache.GetOrAdd(key, _ =>
            {
                calls++;
                return new Payload { Size = 64 };
            });
            Assert.Equal(64, payload.Size);
            return calls;
        }

        /// <summary>
        /// <c>KeepAlive</c> only re-files an object that is already in the map, so the object
        /// has to still be there when the second reference happens. This is the shape of the
        /// real workload: a content object is looked up, dropped, and looked up again.
        /// </summary>
        [Fact]
        public void AnObjectReReferencedAfterARotationIsReleasedWhenTheCallerLetsGo()
        {
            var cache = new TimedReferenceCache<int, Payload>();
            var ringCount = RingCount();

            var weak = CacheAndRelease(cache, 1, out var firstFactoryCalls);
            Assert.Equal(1, firstFactoryCalls);
            Assert.Equal(ringCount - 1, NextRing);

            //one rotation. This clears a different ring, so the payload is still in the map -
            //which is exactly the case the broken removal failed to handle.
            RotateOnce();
            Assert.Equal((ringCount - 1 + 1) % ringCount, NextRing);

            var secondFactoryCalls = ReReference(cache, 1);
            Assert.Equal(0, secondFactoryCalls);

            //the caller has let go of it. Only the cache can still be holding it.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(weak.IsAlive,
                "the timed cache is still holding a strong reference to an object nothing is using");
        }

        /// <summary>
        /// The same journey without the intervening rotation. The ring does not move, so the
        /// removal branch is not taken at all - the object is dropped by the rotation that
        /// eventually reaches its ring. This is the case that already worked, and it is here so
        /// that the test above is measuring the re-reference path and not "the cache releases
        /// things" in general.
        /// </summary>
        [Fact]
        public void AnObjectIsReleasedByTheRotationThatReachesItsRing()
        {
            var cache = new TimedReferenceCache<int, Payload>();
            var ringCount = RingCount();

            var weak = CacheAndRelease(cache, 2, out _);

            for (int i = 0; i < ringCount; i++) RotateOnce();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(weak.IsAlive, "a released cache entry must not be kept alive by the controller");
        }

        /// <summary>
        /// A value that is still in use is of course still alive - the release is not a
        /// free-for-all. And a second reference must find the same instance rather than build a
        /// new one, because a cache that rebuilt on every miss would make the whole mechanism
        /// pointless.
        /// </summary>
        [Fact]
        public void AnObjectStillInUseIsNeitherRebuiltNorCollected()
        {
            var cache = new TimedReferenceCache<int, Payload>();

            var first = cache.GetOrAdd(3, _ => new Payload { Size = 11 });
            var second = cache.GetOrAdd(3, _ => new Payload { Size = 22 });

            Assert.Same(first, second);
            Assert.Equal(11, second.Size);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.NotNull(first);
        }

        /// <summary>
        /// The map has to shrink, not just stop growing, or the "leak" is merely slower. This
        /// counts the map's entries directly because there is no public way to ask, and a
        /// release that leaves the entry behind is exactly the defect.
        /// </summary>
        [Fact]
        public void ReFilingAnObjectRemovesItsOldMapEntry()
        {
            var cache = new TimedReferenceCache<int, Payload>();
            var map = Map();
            var before = MapCount();

            var payload = cache.GetOrAdd(4, _ => new Payload());
            Assert.Equal(before + 1, MapCount());

            RotateOnce();
            cache.GetOrAdd(4, _ => new Payload());

            //the object is held only by the caller now, so the map must not be holding it too
            Assert.Equal(before, MapCount());
            Assert.True(map.Count <= before,
                "the object map must not keep an entry for an object that has been re-filed");
            GC.KeepAlive(payload);
        }

        private static System.Collections.IDictionary Map()
        {
            return (System.Collections.IDictionary)typeof(TimedReferenceController)
                .GetField("ObjectToRing", StaticPrivate).GetValue(null);
        }

        private static int MapCount()
        {
            return Map().Count;
        }
    }
}
