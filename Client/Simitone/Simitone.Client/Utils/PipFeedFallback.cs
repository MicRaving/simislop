/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client.UI.Panels;
using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Why the picture-in-picture window cannot be showing the live lot view this frame.
    /// </summary>
    /// <remarks>
    /// This is the input to the fallback decision, and it exists so the decision can be a pure
    /// function. Reading <c>PPXDepthEngine.GetBackbuffer()</c>, the world and the graphics device
    /// is the part that needs a running game; deciding what to do about what that read says is
    /// not, and that is where the interesting rules are.
    /// </remarks>
    public enum PipFeedUnavailableReason
    {
        /// <summary>Nothing is wrong: the live lot view can be sampled and is being drawn.</summary>
        None = 0,

        /// <summary>
        /// There is no lot view to read: no world is loaded, the lot screen is not drawing, or the
        /// engine has not made a backbuffer yet. Normal before the first lot loads, and on the
        /// frames either side of a lot change.
        /// </summary>
        NoWorld,

        /// <summary>
        /// The lot is being drawn, but there is no non-multisampled copy of it to read: the
        /// snapshot could not be taken and the backbuffer is multisampled.
        ///
        /// <para>
        /// <c>World.ChangeAAMode</c> turns the supersampled setting (AA 2) into 8x MSAA when the
        /// camera is 2D, and AA 1 is 4x MSAA, so in the default camera the backbuffer is
        /// multisampled for every anti-aliasing setting except "off". That used to be the end of
        /// the live view - the window reported that the player had to turn AA off, which was a
        /// guess, since the engine itself blits the multisampled target to the screen. It is now
        /// reached only when the copy into a plain target has also failed, which is a device
        /// refusing a small render target rather than a graphics setting, so it is rare and worth
        /// naming when it happens.
        /// </para>
        /// </summary>
        MultisampledBackbuffer,

        /// <summary>
        /// The lot feed threw while drawing and has been switched off for the session. Reported
        /// separately from a backbuffer that was never readable because it is a one-off fault
        /// rather than a graphics setting, and because it does not go away when AA is changed.
        /// </summary>
        LotFeedFailed
    }

    /// <summary>
    /// What the picture-in-picture window should draw this frame, and what it should say while it
    /// is drawing it. Produced by <see cref="PipFeedFallback.Decide"/>; immutable and free of
    /// graphics state, so the whole fallback is checkable in a test.
    /// </summary>
    public readonly struct PipFeedDecision
    {
        internal PipFeedDecision(PipFeed feed, PipFeedUnavailableReason reason, bool shouldReport,
            string statusText, string captionOverride, string logMessage)
        {
            Feed = feed;
            Reason = reason;
            ShouldReport = shouldReport;
            StatusText = statusText;
            CaptionOverride = captionOverride;
            LogMessage = logMessage;
        }

        /// <summary>The feed to draw. <see cref="PipFeed.Auto"/> means "nothing to draw".</summary>
        public PipFeed Feed { get; }

        /// <summary>Why the lot view is unavailable, or <see cref="PipFeedUnavailableReason.None"/>.</summary>
        public PipFeedUnavailableReason Reason { get; }

        /// <summary>
        /// Whether this reason is worth putting in the log. False when it is the same reason that
        /// was last reported, which is what stops the same sentence appearing every frame.
        /// </summary>
        public bool ShouldReport { get; }

        /// <summary>
        /// The status strip's text: "LIVE", "HEADLINE", or the specific "why not" when there is
        /// nothing to draw. Short by construction - the strip is <see cref="PictureInPictureFraming.ViewWidth"/>
        /// UI units wide at 11pt.
        /// </summary>
        public string StatusText { get; }

        /// <summary>
        /// A caption to show <em>instead of</em> the caller's, or null to leave the caller's alone.
        /// Only ever set when there is nothing to draw, so that a window with a real feed does not
        /// lecture the player about a graphics setting while it is showing them the sim.
        /// </summary>
        public string CaptionOverride { get; }

        /// <summary>
        /// The log line for this reason, or null when <see cref="ShouldReport"/> is false. Built
        /// here rather than at the call site so the wording is covered by a test too.
        /// </summary>
        public string LogMessage { get; }
    }

    /// <summary>
    /// The picture-in-picture window's fallback decision, with no graphics device, no content and
    /// no statics in it.
    ///
    /// <para>
    /// The live lot view is the feed that shows the sim, so it wins whenever the engine leaves a
    /// readable frame behind. The subject's headline is the next best thing: it is live, it is the
    /// sim's own, and it is a render target the VM already produced this tick for the world. When
    /// even that is not up there - no headline fired this tick, the sim was not selected, the lot
    /// changed - the window says what it is doing and why, rather than showing an empty panel or
    /// silently holding the last frame.
    /// </para>
    ///
    /// <para>
    /// <b>How the lot view is read.</b> The lot lives in the engine's backbuffer, which
    /// <c>World.ChangeAAMode</c> creates with 4x or 8x MSAA for every anti-aliasing setting except
    /// "off", so it is not a plain texture to sample. The window does not have to care: it takes a
    /// copy of the backbuffer into a small, permanently non-multisampled render target
    /// (<c>PPXDepthEngine.CaptureLotSnapshot</c> - the same blit <c>DrawBackbuffer</c> performs on
    /// the way to the screen) and crops out of that, which is why the live view is available at
    /// every graphics setting. Reading the backbuffer directly stays as the second choice, for when
    /// the copy could not be made and the samples happen to allow it, and this class is what says
    /// so - and names the cause - when neither is possible.
    /// </para>
    /// </summary>
    public static class PipFeedFallback
    {
        /// <summary>
        /// Works out whether the lot view can be sampled this frame, and if not, why.
        /// </summary>
        /// <remarks>
        /// The five-argument form is the pre-snapshot rule: the backbuffer is the only thing there
        /// is, and a multisampled one is unreadable. It is kept because it is the honest answer for
        /// a caller that has no copy, and because the six-argument form delegates to it with
        /// <c>lotSourceAvailable: false</c> - so the behaviour it describes is still the behaviour
        /// rather than a historical note.
        /// </remarks>
        public static PipFeedUnavailableReason DiagnoseLotFeed(
            bool worldPresent, bool worldVisible, bool backbufferPresent, int multiSampleCount, bool lotFeedDisabled)
        {
            return DiagnoseLotFeed(worldPresent, worldVisible, backbufferPresent, multiSampleCount,
                lotFeedDisabled, lotSourceAvailable: false);
        }

        /// <summary>
        /// Works out whether the lot view can be sampled this frame, and if not, why.
        /// </summary>
        /// <param name="worldPresent">A world is loaded on the current lot screen.</param>
        /// <param name="worldVisible">That world is being drawn (the lot screen is up).</param>
        /// <param name="backbufferPresent">The engine has created a backbuffer to read.</param>
        /// <param name="multiSampleCount">The backbuffer's sample count, for the log line.</param>
        /// <param name="lotFeedDisabled">
        /// The lot feed has already thrown and been switched off for the session. Checked first,
        /// because it outranks every "but the backbuffer looks fine": a feed that has just thrown
        /// must not be handed a second chance on the next frame's optimism.
        /// </param>
        /// <param name="lotSourceAvailable">
        /// There is a texture the lot view can be cropped out of - a non-multisampled snapshot, or
        /// a backbuffer the samples allow. This is what makes the live view independent of the
        /// player's anti-aliasing: it is true with 8x MSAA selected, where it used to be false.
        /// </param>
        public static PipFeedUnavailableReason DiagnoseLotFeed(
            bool worldPresent, bool worldVisible, bool backbufferPresent, int multiSampleCount,
            bool lotFeedDisabled, bool lotSourceAvailable)
        {
            if (lotFeedDisabled) return PipFeedUnavailableReason.LotFeedFailed;
            if (!worldPresent || !worldVisible) return PipFeedUnavailableReason.NoWorld;

            // A target that exists and no copy of it can be read is the anti-aliasing case; a
            // target that does not exist yet is the ordinary "no lot loaded" case, and the two are
            // told apart because they are not the same thing to a player.
            if (lotSourceAvailable) return PipFeedUnavailableReason.None;
            if (!PictureInPictureFraming.CanSample(backbufferPresent, multiSampleCount))
            {
                return backbufferPresent
                    ? PipFeedUnavailableReason.MultisampledBackbuffer
                    : PipFeedUnavailableReason.NoWorld;
            }

            return PipFeedUnavailableReason.None;
        }

        /// <summary>
        /// The feed to draw, the status strip, the caption, and whether the reason is worth
        /// logging - from a reason, the subject's headline, and what has already been reported.
        /// </summary>
        /// <param name="requested">
        /// The feed the caller asked for. It does not change <em>which</em> feed is drawn (an
        /// unavailable feed is never drawn, whatever was asked for, and an available one is always
        /// preferred), but it does change the log line: someone who explicitly asked for the live
        /// view deserves to be told that they are not getting it.
        /// </param>
        /// <param name="reason">From <see cref="DiagnoseLotFeed"/>.</param>
        /// <param name="headlineAvailable">The subject has a headline texture this tick.</param>
        /// <param name="multiSampleCount">The backbuffer's sample count, for the log line.</param>
        /// <param name="lastReported">
        /// The reason already reported to the log, or
        /// <see cref="PipFeedUnavailableReason.None"/> for "nothing yet". The caller owns this
        /// because it is per-window state, but the comparison that matters lives here: a reason
        /// equal to the last one reported is silence, not a new sentence every frame.
        ///
        /// <para>
        /// Note this is "the last thing that was said", not "every reason ever said". A cause that
        /// goes away and comes back is explained again, so the last line in the log is always
        /// about the state the window is in now. The cost is a second copy of one line in a
        /// sequence a player has to cause by toggling a setting, which is cheaper than a log whose
        /// final entry is about a problem that stopped happening.
        /// </para>
        /// </param>
        public static PipFeedDecision Decide(PipFeed requested, PipFeedUnavailableReason reason,
            bool headlineAvailable, int multiSampleCount, PipFeedUnavailableReason lastReported)
        {
            var lotAvailable = reason == PipFeedUnavailableReason.None;
            var feed = PictureInPictureFraming.ResolveFeed(requested, lotAvailable, headlineAvailable);

            var shouldReport = reason != PipFeedUnavailableReason.None && reason != lastReported;

            return new PipFeedDecision(
                feed,
                reason,
                shouldReport,
                StatusFor(feed, reason),
                feed == PipFeed.Auto ? CaptionFor(reason) : null,
                shouldReport ? LogFor(reason, requested, multiSampleCount) : null);
        }

        /// <summary>
        /// The status strip's text. A window with a real feed says which one; a window without one
        /// says what stopped it, which is more use than the same "NO SIGNAL" for every cause.
        /// </summary>
        public static string StatusFor(PipFeed feed, PipFeedUnavailableReason reason)
        {
            switch (feed)
            {
                case PipFeed.LotView:
                    return "LIVE";
                case PipFeed.Headline:
                    return "HEADLINE";
                default:
                    switch (reason)
                    {
                        case PipFeedUnavailableReason.MultisampledBackbuffer:
                            return "AA ON";
                        case PipFeedUnavailableReason.LotFeedFailed:
                            return "FEED ERROR";
                        case PipFeedUnavailableReason.NoWorld:
                            return "NO LOT";
                        default:
                            return "NO SIGNAL";
                    }
            }
        }

        /// <summary>
        /// The caption to show when there is nothing to draw, or null to keep the caller's own.
        /// </summary>
        /// <remarks>
        /// Null is only ever reached for
        /// <see cref="PipFeedUnavailableReason.None"/>, and only on a frame that is drawing
        /// something anyway - <see cref="Decide"/> only asks for a caption when the feed came out
        /// as <see cref="PipFeed.Auto"/>, and <c>Auto</c> is only reachable when the lot view is
        /// unavailable. So in practice an empty window always carries an explanation, and the
        /// notification's own caption is never displaced by one; it is just still there
        /// underneath, for the frame the feed comes back.
        /// </remarks>
        public static string CaptionFor(PipFeedUnavailableReason reason)
        {
            switch (reason)
            {
                case PipFeedUnavailableReason.MultisampledBackbuffer:
                    // The one caption a player can act on. "Try", not "needs": the live view now
                    // works with anti-aliasing on, so turning it off is a workaround for a copy
                    // that could not be made rather than a requirement. Everything else here is a
                    // state report, so it does not earn a piece of the caption strip.
                    return "Try AA off for the live view";
                case PipFeedUnavailableReason.LotFeedFailed:
                    return "Live view hit an error";
                case PipFeedUnavailableReason.NoWorld:
                    return "No lot view yet";
                default:
                    return null;
            }
        }

        /// <summary>
        /// The log line for a reason. Long on purpose: this is the single line in the log that
        /// explains why the feature a player is looking at is not showing, and it is the only
        /// place the relationship between the lot backbuffer and the anti-aliasing setting gets
        /// written down in the game's own words.
        /// </summary>
        public static string LogFor(PipFeedUnavailableReason reason, PipFeed requested, int multiSampleCount)
        {
            var asked = requested == PipFeed.LotView
                ? " The live view was asked for explicitly, so this is a fallback, not a default."
                : "";

            switch (reason)
            {
                case PipFeedUnavailableReason.MultisampledBackbuffer:
                    return "Picture-in-picture cannot read the lot view: the backbuffer is "
                        + multiSampleCount + "x multisampled and the non-multisampled copy of it that the "
                        + "window draws from could not be made, so the window is showing the subject's headline "
                        + "instead. In the 2D camera the engine turns the supersampled setting (AA 2) into "
                        + multiSampleCount + "x MSAA and AA 1 is 4x MSAA, so the copy is what makes the live view "
                        + "readable at all; the 3D camera modes supersample instead and are readable directly. "
                        + "Switching anti-aliasing off also works around it." + asked;

                case PipFeedUnavailableReason.LotFeedFailed:
                    return "Picture-in-picture's lot feed threw while drawing and has been switched off for "
                        + "the rest of this session, so the window is showing the subject's headline "
                        + "instead. The exception is the one logged with it." + asked;

                case PipFeedUnavailableReason.NoWorld:
                    return "Picture-in-picture has no lot view to sample (no world is loaded, the lot screen "
                        + "is not drawing, or the engine has not made a backbuffer yet), so the window is "
                        + "showing the subject's headline instead." + asked;

                default:
                    return null;
            }
        }
    }
}
