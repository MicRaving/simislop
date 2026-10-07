using Simitone.Client.Services;
using Simitone.Client.Utils;
using System;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// What the Options "Check for updates" row is showing.
    /// </summary>
    /// <remarks>
    /// <see cref="UpdateCheckStatus"/> is the checker's own three-valued answer and is
    /// deliberately silent about a check that is in flight or has not been run; those are states
    /// of the <em>button</em>, not of the release feed. Keeping them apart here means a caller
    /// cannot accidentally treat "nobody has asked yet" as "there is nothing to report".
    /// </remarks>
    public enum UpdateCheckState
    {
        /// <summary>Nobody has pressed the button yet in this session.</summary>
        Idle = 0,

        /// <summary>A check is in flight. The button is disabled and the row says so.</summary>
        Checking = 1,

        /// <summary>The newest published release is this build, or older.</summary>
        UpToDate = 2,

        /// <summary>The check could not be completed. Nothing the player can act on.</summary>
        CouldNotCheck = 3,

        /// <summary>A newer release exists and there is a page to get it from.</summary>
        UpdateAvailable = 4
    }

    /// <summary>
    /// Everything the Options update row needs to draw itself for one result, decided without a
    /// graphics device, a screen or a network.
    /// </summary>
    public readonly struct UpdateCheckPresentation
    {
        public UpdateCheckPresentation(UpdateCheckState state, string title, string message, string statusText,
            bool offersDownload, string downloadUrl)
        {
            State = state;
            Title = title;
            Message = message;
            StatusText = statusText;
            OffersDownload = offersDownload;
            DownloadUrl = downloadUrl;
        }

        /// <summary>Which of the five states this is.</summary>
        public UpdateCheckState State { get; }

        /// <summary>Title for the dialog, already localised.</summary>
        public string Title { get; }

        /// <summary>
        /// Body for the <em>dialog</em>, already localised. A whole sentence or two, so a player
        /// reading a modal gets the reasoning and both versions.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Body for the <em>row</em>, already localised: the same answer in a few words, because
        /// this one has to fit on a single line under a button in the Options margin.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Message"/> rather than a truncation of it. The dialog is a
        /// wrapped block that can afford "Simitone 0.9.1 is available. You are running 0.9.0.",
        /// while the row has about 230px next to a centred OK button - and a status line that
        /// overruns its margin is worse than a terse one.
        /// </remarks>
        public string StatusText { get; }

        /// <summary>
        /// Whether a "Download" button should be offered. True only for
        /// <see cref="UpdateCheckState.UpdateAvailable"/>.
        /// </summary>
        public bool OffersDownload { get; }

        /// <summary>
        /// Where the Download button goes. Always a non-empty string, so the caller never has to
        /// decide between a null URL and a missing one: the releases page is the floor.
        /// </summary>
        public string DownloadUrl { get; }
    }

    /// <summary>
    /// The decision behind the Options "Check for updates" row: which state a result means, the
    /// text to show for it, and when the button may be pressed.
    ///
    /// <para>
    /// Split out of the button itself, which lives in <see cref="UIOptionsPanel"/>, for the same
    /// reason the other panels' models are: a caption that cannot be asserted is a caption that
    /// drifts, and none of this needs a window, a graphics device or a socket to decide. The
    /// network call stays in <see cref="UpdateChecker"/>, which is already total.
    /// </para>
    /// </summary>
    public static class UpdateCheckPresenter
    {
        // -------------------------------------------------------------------------------------
        // String ids.
        //
        // Block 210-219 of Simitone's own table (_f900_simitone.cst), the next free block after
        // the object query panel's 200. Reserved here for this row: the strings are newer than
        // the table, so every lookup currently falls through to the English written beside it and
        // the behaviour is exactly what it was before the row existed. Add the rows to the .cst
        // and they take over.
        // -------------------------------------------------------------------------------------

        /// <summary>"Simitone Updates" - the title of the result dialog and of the row.</summary>
        public const string TitleId = "210";

        /// <summary>"Simitone %s" - the version stamp beside the button.</summary>
        public const string VersionId = "211";

        /// <summary>"Check for updates" - the button caption while idle.</summary>
        public const string CheckCaptionId = "212";

        /// <summary>"Checking..." - the button caption while a check is in flight.</summary>
        public const string CheckingCaptionId = "213";

        /// <summary>Never checked.</summary>
        public const string NeverCheckedId = "214";

        /// <summary>"Simitone %s is the newest release. You are running %s."</summary>
        public const string UpToDateId = "215";

        /// <summary>"Could not reach the release feed. The latest build is on the releases page."</summary>
        public const string CouldNotCheckId = "216";

        /// <summary>"Simitone %s is available. You are running %s."</summary>
        public const string AvailableId = "217";

        /// <summary>"Checking for updates..."</summary>
        public const string CheckingId = "218";

        /// <summary>The Download button's own caption.</summary>
        public const string DownloadCaptionId = "219";

        // The one-line status the row shows beside the button. Same split as the dialog strings
        // above, for the same reason: this one has to fit on a single line in the Options margin.
        /// <summary>Not checked yet.</summary>
        public const string IdleStatusId = "220";

        /// <summary>Checking.</summary>
        public const string CheckingStatusId = "221";

        /// <summary>Nothing newer. %s is the newest release.</summary>
        public const string UpToDateStatusId = "222";

        /// <summary>The feed could not be reached.</summary>
        public const string CouldNotCheckStatusId = "223";

        /// <summary>Version %s is available.</summary>
        public const string AvailableStatusId = "224";

        /// <summary>
        /// How long the button stays disabled after a check finishes.
        /// </summary>
        /// <remarks>
        /// <see cref="UpdateChecker.CheckAsync"/> is a real unauthenticated GitHub API request and
        /// the checker deliberately does not throttle itself - it has no idea whether the caller
        /// is a button a player can mash. GitHub allows 60 per hour per IP, so a debounce here is
        /// what keeps an impatient player from spending the whole hour's budget on one dialog.
        /// A minute is short enough that pressing it again after reading the answer still works.
        /// </remarks>
        public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The version stamp the row shows next to the button.
        /// </summary>
        /// <param name="version">
        /// A version in any form <see cref="UpdateChecker.TryParseVersion"/> accepts; normally
        /// <see cref="SimitoneVersion.Current"/>. Blank or unreadable falls back to the running
        /// version rather than showing a gap.
        /// </param>
        public static string VersionText(string version)
        {
            var shown = UpdateChecker.TryParseVersion(version, out var parsed) ? parsed.ToString() : SimitoneVersion.Current;
            return SimitoneStrings.Format(VersionId, "Simitone %s", shown);
        }

        /// <summary>
        /// The button caption for a state. Idle and every finished state share the same caption:
        /// the row's <em>status</em> changes, the invitation to press it does not.
        /// </summary>
        public static string ButtonCaption(UpdateCheckState state)
        {
            return state == UpdateCheckState.Checking
                ? SimitoneStrings.Get(CheckingCaptionId, "Checking...")
                : SimitoneStrings.Get(CheckCaptionId, "Check for updates");
        }

        /// <summary>
        /// Whether the button may be pressed right now.
        /// </summary>
        /// <param name="state">The state the row is in.</param>
        /// <param name="sinceLastRequest">
        /// How long ago the last check was started, or null when none has been. A null is
        /// "never", which is always allowed.
        /// </param>
        public static bool IsCheckAllowed(UpdateCheckState state, TimeSpan? sinceLastRequest)
        {
            if (state == UpdateCheckState.Checking) return false;
            if (!sinceLastRequest.HasValue) return true;
            return sinceLastRequest.Value >= MinimumInterval;
        }

        /// <summary>
        /// Turns a checker result into the state the row should be in.
        /// </summary>
        /// <param name="result">
        /// What <see cref="UpdateChecker.CheckAsync"/> returned. Null is treated as
        /// <see cref="UpdateCheckStatus.Unknown"/>, which the checker never produces, so a caller
        /// that lost the result still gets a row that says something.
        /// </param>
        public static UpdateCheckState StateFor(UpdateCheckResult result)
        {
            switch (result?.Status ?? UpdateCheckStatus.Unknown)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    return UpdateCheckState.UpdateAvailable;
                case UpdateCheckStatus.UpToDate:
                    return UpdateCheckState.UpToDate;
                default:
                    return UpdateCheckState.CouldNotCheck;
            }
        }

        /// <summary>
        /// The dialog text and the one-line row text for a state.
        /// </summary>
        /// <param name="state">The state to describe.</param>
        /// <param name="result">The result it came from; may be null for the non-result states.</param>
        public static UpdateCheckPresentation Describe(UpdateCheckState state, UpdateCheckResult result)
        {
            var title = SimitoneStrings.Get(TitleId, "Simitone Updates");
            // The latest version is present whenever the status is, but the current one is used as
            // well so a payload that somehow lost it still reads sensibly.
            var latest = result?.LatestVersion ?? SimitoneVersion.Current;
            var current = result?.CurrentVersion ?? SimitoneVersion.Current;

            switch (state)
            {
                case UpdateCheckState.Checking:
                    return new UpdateCheckPresentation(state, title,
                        SimitoneStrings.Get(CheckingId, "Checking for updates..."),
                        SimitoneStrings.Get(CheckingStatusId, "Checking for updates..."),
                        false, SimitoneVersion.ReleasesPageUrl);

                case UpdateCheckState.Idle:
                    return new UpdateCheckPresentation(state, title,
                        SimitoneStrings.Get(NeverCheckedId,
                            "This has not checked for updates in this session."),
                        SimitoneStrings.Get(IdleStatusId, "Not checked in this session."),
                        false, SimitoneVersion.ReleasesPageUrl);

                case UpdateCheckState.UpdateAvailable:
                    return new UpdateCheckPresentation(state, title,
                        SimitoneStrings.Format(AvailableId, "Simitone %s is available. You are running %s.",
                            latest, current),
                        SimitoneStrings.Format(AvailableStatusId, "Version %s is available.", latest),
                        true,
                        // The checker's own URL, already restricted to the repository's host by
                        // UpdateChecker.IsTrustedReleaseUrl, and falling back to the releases page.
                        string.IsNullOrEmpty(result?.ReleaseUrl) ? SimitoneVersion.ReleasesPageUrl : result.ReleaseUrl);

                case UpdateCheckState.UpToDate:
                    return new UpdateCheckPresentation(state, title,
                        SimitoneStrings.Format(UpToDateId, "Simitone %s is the newest release. You are running %s.",
                            latest, current),
                        SimitoneStrings.Format(UpToDateStatusId, "Up to date. Newest release: %s.", latest),
                        false, SimitoneVersion.ReleasesPageUrl);

                default:
                    // CouldNotCheck. Says what happened and where the answer can be read by hand,
                    // rather than blaming the player or pretending there is nothing to see.
                    return new UpdateCheckPresentation(state, title,
                        SimitoneStrings.Get(CouldNotCheckId,
                            "Could not reach the release feed. The latest build is on the releases page."),
                        SimitoneStrings.Get(CouldNotCheckStatusId, "Could not reach the release feed."),
                        false, SimitoneVersion.ReleasesPageUrl);
            }
        }

        /// <summary>
        /// The Download button's caption, for the one state that offers one.
        /// </summary>
        public static string DownloadCaption()
        {
            return SimitoneStrings.Get(DownloadCaptionId, "Download");
        }
    }
}
