using FSO.LotView;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for <see cref="TextureCacheClearPolicy"/> - when the lot view's 2D sprite texture
    /// cache is emptied.
    ///
    /// <para>
    /// The bug this is here for is worth stating in its own terms, because it is a bug that makes
    /// the game slower rather than broken, and the kind that is invisible on a screenshot.
    /// <c>World.Draw</c> read <c>if (FrameCounter &lt; LastCacheClear + 60*60)</c> and
    /// <c>LastCacheClear</c> was declared and never written anywhere in the tree. So the
    /// condition was true for frames 1 to 3600 - the whole first minute of play - and the
    /// <c>State._2D.ClearTextureCache()</c> inside it ran on <em>every one of those frames</em>.
    /// After a minute it was never true again, so the periodic clear it was written to be never
    /// happened either. Inverted, and with a period of one frame for its first minute.
    /// </para>
    ///
    /// <para>
    /// What that costs is per sprite per frame: a dictionary miss, a <c>CachableTexture2D</c>
    /// acquire and a <c>TimedReferenceController.KeepAlive</c>, on the single hottest path in the
    /// 2D renderer. It is a churn cost and not a leak - <c>SPR2Frame</c> owns its texture through
    /// the reference-counted cache - but it is the most expensive thing that can happen to that
    /// loop, and it happened 60 times a second for a minute.
    /// </para>
    ///
    /// <para>
    /// Pure, so none of it needs a graphics device or a world.
    /// </para>
    /// </remarks>
    public sealed class TextureCacheClearPolicyTests
    {
        // ---- the interval is a minute at 60fps ------------------------------------------------

        [Fact]
        public void TheDefaultIntervalIsTheOneTheOriginalExpressionMeant()
        {
            Assert.Equal(60u * 60u, TextureCacheClearPolicy.DefaultInterval);
            Assert.Equal(TextureCacheClearPolicy.DefaultInterval, new TextureCacheClearPolicy().Interval);
        }

        [Fact]
        public void TheCacheSurvivesTheFirstMinute()
        {
            // The bug, stated as the assertion that now holds. Not one clear in the first
            // 3600 frames, where the old comparison cleared on 3600 of them.
            var policy = new TextureCacheClearPolicy();
            var clears = 0;

            for (uint frame = 1; frame < TextureCacheClearPolicy.DefaultInterval; frame++)
            {
                if (!policy.ShouldClear(frame)) continue;
                policy.Record(frame);
                clears++;
            }

            Assert.Equal(0, clears);
        }

        [Fact]
        public void TheCacheIsEmptiedOnceTheIntervalHasElapsed()
        {
            var policy = new TextureCacheClearPolicy();

            Assert.False(policy.ShouldClear(TextureCacheClearPolicy.DefaultInterval - 1));

            Assert.True(policy.ShouldClear(TextureCacheClearPolicy.DefaultInterval));
            policy.Record(TextureCacheClearPolicy.DefaultInterval);

            Assert.Equal(TextureCacheClearPolicy.DefaultInterval, policy.LastClear);
        }

        [Fact]
        public void ClearingIsPeriodicAndNotJustOnce()
        {
            // The half of the original that also never ran. Ten minutes of play, sixty seconds at
            // a time: ten clears, and never two in the same second.
            var policy = new TextureCacheClearPolicy();
            var clearedAt = new System.Collections.Generic.List<uint>();
            const int TenMinutes = 60 * 60 * 10;

            for (uint frame = 1; frame <= TenMinutes; frame++)
            {
                if (!policy.ShouldClear(frame)) continue;
                policy.Record(frame);
                clearedAt.Add(frame);
            }

            Assert.Equal(10, clearedAt.Count);
            for (int i = 1; i < clearedAt.Count; i++)
            {
                Assert.True(clearedAt[i] - clearedAt[i - 1] >= TextureCacheClearPolicy.DefaultInterval,
                    "clears " + clearedAt[i - 1] + " and " + clearedAt[i] + " were less than a minute apart");
            }
        }

        // ---- a clear that is not acknowledged keeps asking -------------------------------------

        [Fact]
        public void AnUnacknowledgedClearKeepsBeingAskedFor()
        {
            // ShouldClear is a question, Record is the acknowledgement, and a caller that clears
            // but forgets to record must not end up never clearing at all. The old field arrangement
            // was the mirror image of this failure: written nowhere, asked once.
            var policy = new TextureCacheClearPolicy();

            Assert.True(policy.ShouldClear(TextureCacheClearPolicy.DefaultInterval));
            Assert.True(policy.ShouldClear(TextureCacheClearPolicy.DefaultInterval + 1));
        }

        // ---- a zero interval would reintroduce the bug ----------------------------------------

        [Fact]
        public void AZeroIntervalIsFlooredRatherThanClearingOnEveryFrame()
        {
            // A zero interval makes `frame - 0 >= 0` true for every frame, which is precisely the
            // bug this class exists to remove, so it is floored to one rather than trusted.
            var policy = new TextureCacheClearPolicy(0);

            Assert.Equal(1u, policy.Interval);
            Assert.False(policy.ShouldClear(0));
        }

        // ---- a wrapped frame counter -------------------------------------------------------------

        [Fact]
        public void AWrappedFrameCounterDoesNotBringTheStormBack()
        {
            // 60fps, so uint.MaxValue is about 2.2 years of play. The original compared
            // `frame < LastCacheClear + 3600`, and that addition wraps too: with LastCacheClear
            // eleven frames from the top, `LastCacheClear + 3600` becomes 355, so the counter
            // spends the first 355 frames of its new life satisfying the condition - the original
            // bug, one wrap later. The subtraction form counts frames forward and waits the whole
            // interval, so the burst does not come back.
            var policy = new TextureCacheClearPolicy();
            policy.Record(uint.MaxValue - 10);

            var clearedAt = new System.Collections.Generic.List<uint>();
            for (uint frame = 0; frame < TextureCacheClearPolicy.DefaultInterval * 2; frame++)
            {
                if (!policy.ShouldClear(frame)) continue;
                policy.Record(frame);
                clearedAt.Add(frame);
            }

            // Eleven frames had already elapsed when the counter wrapped, so the interval
            // completes eleven frames into the new life - and then only once more, a whole
            // interval later. Two clears, not three hundred and fifty five.
            Assert.Equal(2, clearedAt.Count);
            Assert.Equal(TextureCacheClearPolicy.DefaultInterval - 11u, clearedAt[0]);
            Assert.Equal(TextureCacheClearPolicy.DefaultInterval * 2u - 11u, clearedAt[1]);
        }

        [Fact]
        public void TheIntervalIsConfigurableAndHonoured()
        {
            // Ten frames, so a test of the scheduling does not have to run 3600 of them.
            var policy = new TextureCacheClearPolicy(10);

            Assert.False(policy.ShouldClear(9));
            Assert.True(policy.ShouldClear(10));

            policy.Record(10);
            Assert.False(policy.ShouldClear(19));
            Assert.True(policy.ShouldClear(20));
        }
    }
}
