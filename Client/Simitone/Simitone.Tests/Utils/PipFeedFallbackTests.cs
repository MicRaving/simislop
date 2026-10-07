using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Tests for <see cref="PipFeedFallback"/>, the pure half of
    /// <see cref="UIPictureInPicture"/>'s "can I show the live lot view?" decision.
    ///
    /// <para>
    /// The lot is drawn into <c>PPXDepthEngine</c>'s backbuffer, which
    /// <c>World.ChangeAAMode</c> creates with 4x MSAA for AA 1 and 8x MSAA for AA 2 in the 2D
    /// camera - a multisampled render target for every anti-aliasing setting except "off". This
    /// class used to treat that as "there is no live view to read unless the player turns AA
    /// off", which was a guess and a wrong one: the engine itself hands the multisampled
    /// backbuffer to a plain <c>SpriteBatch</c> in <c>DrawBackbuffer</c> every frame. The window
    /// now takes a copy of the backbuffer into a small non-multisampled target and crops out of
    /// that, so the feed is available at every setting and the only thing left to decide is what
    /// to do when even that is not possible. So the window still has to do three things, and all
    /// three are decided here rather than in the draw:
    /// </para>
    /// <list type="number">
    /// <item><description>Fall back to the subject's headline, which is live and already rendered.</description></item>
    /// <item><description>Fall back to saying so - a named status and, where it is actionable, a caption - rather than an empty panel.</description></item>
    /// <item><description>Say it once. The old code latched a single bool on the first frame it saw a multisampled backbuffer, so a player who fixed the problem, hit a different one, and came back got silence; the log is now keyed on the reason.</description></item>
    /// </list>
    ///
    /// <para>
    /// Nothing here needs a graphics device, a world or a VM: the diagnosis takes the state as
    /// booleans and an int, which is the whole reason the decision was split out of
    /// <c>ResolveFeed</c>.
    /// </para>
    /// </remarks>
    public sealed class PipFeedFallbackTests
    {
        // ---- diagnosing the backbuffer ---------------------------------------------------

        [Fact]
        public void DiagnoseLotFeed_ReportsNothingWrongOnAReadableBackbuffer()
        {
            Assert.Equal(PipFeedUnavailableReason.None,
                PipFeedFallback.DiagnoseLotFeed(true, true, true, 0, false));
        }

        [Theory]
        [InlineData(4)]
        [InlineData(8)]
        public void DiagnoseLotFeed_NamesAntiAliasingRatherThanAFault(int samples)
        {
            // The distinction that matters: 4x is AA 1, 8x is AA 2 in the 2D camera. Both are the
            // player's graphics setting, not something that went wrong in the game. This is the
            // answer for a caller that has no copy of the lot to read - see the next two tests for
            // the caller that does.
            Assert.Equal(PipFeedUnavailableReason.MultisampledBackbuffer,
                PipFeedFallback.DiagnoseLotFeed(true, true, true, samples, false));
        }

        [Theory]
        [InlineData(4)]
        [InlineData(8)]
        public void DiagnoseLotFeed_ReadsAMultisampledLotFineOnceThereIsACopyOfIt(int samples)
        {
            // The whole point of the snapshot. 8x MSAA is what the default 2D camera produces for
            // AA 2, and it used to be the answer to "the live view needs AA off"; with a
            // non-multisampled copy of the backbuffer to crop there is nothing wrong at all.
            Assert.Equal(PipFeedUnavailableReason.None,
                PipFeedFallback.DiagnoseLotFeed(true, true, true, samples, false, lotSourceAvailable: true));
        }

        [Fact]
        public void DiagnoseLotFeed_StillNamesAntiAliasingWhenTheCopyFailedAsWell()
        {
            // The reason survives for the case it was written for: no copy, and a multisampled
            // backbuffer underneath it. Both failures have to line up before the window tells a
            // player to go and change their graphics settings.
            Assert.Equal(PipFeedUnavailableReason.MultisampledBackbuffer,
                PipFeedFallback.DiagnoseLotFeed(true, true, true, 8, false, lotSourceAvailable: false));
        }

        [Fact]
        public void DiagnoseLotFeed_DoesNotClaimALotIsReadableWhenThereIsNoWorld()
        {
            // A copy of a lot from before the player walked out of it is not a live view. The
            // "no lot" test outranks the copy, so a window left open across a lot change says NO
            // LOT rather than showing the previous lot's last frame as though it were live.
            Assert.Equal(PipFeedUnavailableReason.NoWorld,
                PipFeedFallback.DiagnoseLotFeed(false, false, false, 8, false, lotSourceAvailable: true));
        }

        [Fact]
        public void DiagnoseLotFeed_DoesNotResurrectTheLotFeedOnACopyAfterAFault()
        {
            // The copy arriving is not a reason to forget that the feed threw. A fault outranks
            // every "but the graphics state looks fine now", or a feed that throws on a pattern
            // gets retried until it throws again.
            Assert.Equal(PipFeedUnavailableReason.LotFeedFailed,
                PipFeedFallback.DiagnoseLotFeed(true, true, true, 0, lotFeedDisabled: true, lotSourceAvailable: true));
        }

        [Fact]
        public void DiagnoseLotFeed_WithoutACopyIsTheSameAsTheFiveArgumentForm()
        {
            // The five-argument form is kept as the pre-snapshot rule, so it has to be the six
            // -argument form with no copy - otherwise the two would quietly disagree about the same
            // graphics state.
            for (var samples = 0; samples <= 8; samples += 4)
            {
                Assert.Equal(
                    PipFeedFallback.DiagnoseLotFeed(true, true, true, samples, false),
                    PipFeedFallback.DiagnoseLotFeed(true, true, true, samples, false, lotSourceAvailable: false));
            }
        }

        [Theory]
        [InlineData(false, true, true)]   // no world
        [InlineData(true, false, true)]   // lot screen up, world not drawing
        [InlineData(true, true, false)]   // no backbuffer yet
        public void DiagnoseLotFeed_CollapsesTheNoLotCasesIntoOneReason(bool world, bool visible, bool backbuffer)
        {
            // They are the same thing to a player - there is no lot view to show - and they are
            // all transient, so they are worth exactly one log line between them rather than
            // three reasons that ping-pong while a lot loads.
            Assert.Equal(PipFeedUnavailableReason.NoWorld,
                PipFeedFallback.DiagnoseLotFeed(world, visible, backbuffer, 0, false));
        }

        [Fact]
        public void DiagnoseLotFeed_PrefersAFaultOverAnOtherwisePerfectBackbuffer()
        {
            // A feed that just threw must not be handed a second chance on the next frame's
            // optimism, however readable the backbuffer looks.
            Assert.Equal(PipFeedUnavailableReason.LotFeedFailed,
                PipFeedFallback.DiagnoseLotFeed(true, true, true, 0, lotFeedDisabled: true));
        }

        [Fact]
        public void DiagnoseLotFeed_TreatsAMissingBackbufferAsUnreadableWhateverItsSampleCount()
        {
            // MultiSampleCount on a null backbuffer is reported as 0, so the "no backbuffer" test
            // has to come before the multisampled one or a not-yet-created target would read as
            // a readable one.
            Assert.Equal(PipFeedUnavailableReason.NoWorld,
                PipFeedFallback.DiagnoseLotFeed(true, true, false, 0, false));
        }

        // ---- which feed is drawn ----------------------------------------------------------

        [Fact]
        public void Decide_PrefersTheLiveLotViewWhenThereIsOne()
        {
            var decision = PipFeedFallback.Decide(
                PipFeed.Auto, PipFeedUnavailableReason.None, headlineAvailable: true, 0,
                PipFeedUnavailableReason.None);

            Assert.Equal(PipFeed.LotView, decision.Feed);
            Assert.Equal("LIVE", decision.StatusText);
            Assert.Null(decision.CaptionOverride);
            Assert.False(decision.ShouldReport);
            Assert.Null(decision.LogMessage);
        }

        [Fact]
        public void Decide_FallsBackToTheHeadlineOnAMultisampledBackbuffer()
        {
            // The headline is the sim's own live event and the VM already rendered it this tick,
            // so it is the thing worth showing - not an empty panel, and not last lot's frame.
            var decision = PipFeedFallback.Decide(
                PipFeed.Auto, PipFeedUnavailableReason.MultisampledBackbuffer, headlineAvailable: true, 8,
                PipFeedUnavailableReason.None);

            Assert.Equal(PipFeed.Headline, decision.Feed);
            Assert.Equal("HEADLINE", decision.StatusText);
            // No caption override: a window that is showing the sim does not also lecture about
            // anti-aliasing.
            Assert.Null(decision.CaptionOverride);
        }

        [Fact]
        public void Decide_SaysSoInTheCaptionWhenThereIsNoHeadlineEither()
        {
            // This is the improvement over a flat panel and a "NO SIGNAL" title: the player is
            // told the one thing they can act on. "Try", not "needs" - the live view works with AA
            // on, so turning it off works around a copy that could not be made rather than being
            // a requirement of the feature.
            var decision = PipFeedFallback.Decide(
                PipFeed.Auto, PipFeedUnavailableReason.MultisampledBackbuffer, headlineAvailable: false, 4,
                PipFeedUnavailableReason.None);

            Assert.Equal(PipFeed.Auto, decision.Feed);
            Assert.Equal("AA ON", decision.StatusText);
            Assert.Equal("Try AA off for the live view", decision.CaptionOverride);
        }

        [Fact]
        public void Decide_DoesNotBlameThePlayerForAFaultInTheWindow()
        {
            var decision = PipFeedFallback.Decide(
                PipFeed.Auto, PipFeedUnavailableReason.LotFeedFailed, headlineAvailable: false, 0,
                PipFeedUnavailableReason.None);

            Assert.Equal("FEED ERROR", decision.StatusText);
            Assert.Equal("Live view hit an error", decision.CaptionOverride);
        }

        [Fact]
        public void Decide_AlwaysSaysWhyWhenItHasNothingToShow()
        {
            // An empty window used to be a flat panel and the word NO SIGNAL, whatever the cause.
            // Every cause now names itself, so the player can tell "the game has not drawn a lot
            // yet" from "my anti-aliasing is why there is no live view" without reading a log.
            foreach (var reason in new[]
            {
                PipFeedUnavailableReason.NoWorld,
                PipFeedUnavailableReason.MultisampledBackbuffer,
                PipFeedUnavailableReason.LotFeedFailed
            })
            {
                var decision = PipFeedFallback.Decide(PipFeed.Auto, reason, headlineAvailable: false, 0,
                    PipFeedUnavailableReason.None);

                Assert.Equal(PipFeed.Auto, decision.Feed);
                Assert.False(string.IsNullOrEmpty(decision.CaptionOverride),
                    reason + " leaves the window empty with nothing to say about it.");
            }
        }

        [Fact]
        public void Decide_LeavesTheCallersCaptionAloneWhileAFeedIsShowing()
        {
            // The fallback caption replaces the notification's own only when there is nothing to
            // draw. A window showing the live view or the headline under a caption telling the
            // player how to get the live view would be talking nonsense over the top of the sim.
            var live = PipFeedFallback.Decide(PipFeed.Auto, PipFeedUnavailableReason.None, true, 0,
                PipFeedUnavailableReason.None);
            var headline = PipFeedFallback.Decide(PipFeed.Auto,
                PipFeedUnavailableReason.MultisampledBackbuffer, true, 4, PipFeedUnavailableReason.None);

            Assert.Null(live.CaptionOverride);
            Assert.Null(headline.CaptionOverride);
        }

        [Theory]
        [InlineData(PipFeedUnavailableReason.MultisampledBackbuffer, "AA ON")]
        [InlineData(PipFeedUnavailableReason.LotFeedFailed, "FEED ERROR")]
        [InlineData(PipFeedUnavailableReason.NoWorld, "NO LOT")]
        public void Decide_NamesTheCauseRatherThanSayingTheSameThingForAllOfThem(
            PipFeedUnavailableReason reason, string expected)
        {
            var decision = PipFeedFallback.Decide(PipFeed.Auto, reason, false, 0, PipFeedUnavailableReason.None);

            Assert.Equal(expected, decision.StatusText);
        }

        [Fact]
        public void EveryFallbackCaptionFitsTheStripWithoutBeingTruncated()
        {
            // The caption label is PictureInPictureFraming.MaxCaptionLength wide and is not
            // wrapped, so a caption that overruns it is clipped mid-glyph. A fallback caption is
            // shown at the least convenient moment (a window the player cannot use), so it being
            // cut off in half would make it worse than not being there.
            var reasons = new[]
            {
                PipFeedUnavailableReason.None,
                PipFeedUnavailableReason.NoWorld,
                PipFeedUnavailableReason.MultisampledBackbuffer,
                PipFeedUnavailableReason.LotFeedFailed
            };

            foreach (var reason in reasons)
            {
                var caption = PipFeedFallback.CaptionFor(reason);
                if (caption == null) continue;

                Assert.True(caption.Length <= PictureInPictureFraming.MaxCaptionLength,
                    reason + " caption '" + caption + "' is " + caption.Length
                    + " characters, past the " + PictureInPictureFraming.MaxCaptionLength
                    + " the caption strip can show.");
            }
        }

        // ---- logging once -----------------------------------------------------------------

        [Fact]
        public void Decide_ReportsAFreshReasonExactlyOnce()
        {
            var first = PipFeedFallback.Decide(PipFeed.Auto, PipFeedUnavailableReason.MultisampledBackbuffer,
                false, 8, PipFeedUnavailableReason.None);
            Assert.True(first.ShouldReport);
            Assert.NotNull(first.LogMessage);

            // The caller feeds the reported reason back in, and the next frame is silent.
            var second = PipFeedFallback.Decide(PipFeed.Auto, PipFeedUnavailableReason.MultisampledBackbuffer,
                false, 8, first.Reason);
            Assert.False(second.ShouldReport);
            Assert.Null(second.LogMessage);
        }

        [Fact]
        public void Decide_ReportsACauseAgainAfterItHasBeenSuperseded()
        {
            // The old code latched a single bool on the first frame it saw a multisampled
            // backbuffer and never looked at it again, so a player who turned AA off, hit a feed
            // fault, and went back to AA on got silence for a problem that had returned. Keyed on
            // the last thing said, every *change* of cause is explained - including a change back.
            // The cost is one duplicate line in a sequence the player caused themselves, which is
            // cheaper than a log whose last entry is about a fault that has stopped happening.
            var aa = PipFeedFallback.Decide(PipFeed.Auto, PipFeedUnavailableReason.MultisampledBackbuffer,
                false, 4, PipFeedUnavailableReason.None);
            var fault = PipFeedFallback.Decide(PipFeed.Auto, PipFeedUnavailableReason.LotFeedFailed,
                false, 4, aa.Reason);
            var aaAgain = PipFeedFallback.Decide(PipFeed.Auto, PipFeedUnavailableReason.MultisampledBackbuffer,
                false, 4, fault.Reason);

            Assert.True(aa.ShouldReport);
            Assert.True(fault.ShouldReport);
            Assert.True(aaAgain.ShouldReport);
        }

        [Fact]
        public void Decide_SaysNothingWhenTheLotViewIsWorking()
        {
            var decision = PipFeedFallback.Decide(PipFeed.Auto, PipFeedUnavailableReason.None,
                false, 0, PipFeedUnavailableReason.MultisampledBackbuffer);

            // The last reported reason stays in the log; a working feed is not news.
            Assert.False(decision.ShouldReport);
            Assert.Null(decision.LogMessage);
        }

        [Fact]
        public void TheAntiAliasingLogLineNamesTheSampleCountAndTheWorkaround()
        {
            // This one line is the only place in the game's own log that explains why the live
            // view is not showing, so its two load-bearing facts are asserted rather than trusted:
            // the sample count that is actually in play, and that there is something the player
            // can do about it. The line must not claim AA has to be off for the live view - it
            // does not any more, and a log that says otherwise sends someone to change a setting
            // that is not the problem.
            var message = PipFeedFallback.LogFor(PipFeedUnavailableReason.MultisampledBackbuffer, PipFeed.Auto, 8);

            Assert.Contains("8x", message);
            Assert.Contains("also works around it", message);
            Assert.Contains("3D camera", message);
            Assert.DoesNotContain("has to be off", message);
        }

        [Fact]
        public void TheAntiAliasingCaptionOffersAWorkaroundRatherThanADemand()
        {
            // Same reason as the log line, one layer up: the caption is the one piece of text the
            // player actually reads. "Live view needs AA off" would be a lie now.
            var caption = PipFeedFallback.CaptionFor(PipFeedUnavailableReason.MultisampledBackbuffer);

            Assert.Contains("AA", caption);
            Assert.Contains("Try", caption);
            Assert.DoesNotContain("needs", caption);
        }

        [Fact]
        public void TheLogLineSaysWhenTheLiveViewWasAskedForExplicitly()
        {
            // Feed = PipFeed.LotView is the debug/manual setting. Someone who asked for the live
            // view deserves to know they are not getting it, which is different information from
            // the default Auto path silently taking the fallback.
            var asked = PipFeedFallback.LogFor(PipFeedUnavailableReason.MultisampledBackbuffer, PipFeed.LotView, 4);
            var notAsked = PipFeedFallback.LogFor(PipFeedUnavailableReason.MultisampledBackbuffer, PipFeed.Auto, 4);

            Assert.Contains("asked for explicitly", asked);
            Assert.DoesNotContain("asked for explicitly", notAsked);
        }

        [Fact]
        public void ThereIsNoLogLineForAWorkingFeed()
        {
            Assert.Null(PipFeedFallback.LogFor(PipFeedUnavailableReason.None, PipFeed.Auto, 0));
        }
    }
}
