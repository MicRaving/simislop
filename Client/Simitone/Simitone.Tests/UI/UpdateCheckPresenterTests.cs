/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client;
using Simitone.Client.Services;
using Simitone.Client.UI.Panels.Options;
using System;
using System.IO;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="UpdateCheckPresenter"/>, the decision behind the Options
    /// "Check for updates" row.
    ///
    /// <para>
    /// The row is the first place <see cref="UpdateChecker"/> has a production caller, so what is
    /// worth pinning is the mapping from a checker's answer to what a player is shown - and in
    /// particular the two answers that are easy to conflate. "There is nothing newer" and "we
    /// could not find out" are both non-events for the player, and both must be silent enough not
    /// to interrupt, but only one of them is a claim about the world, and neither may be dressed
    /// up as a download.
    /// </para>
    ///
    /// <para>
    /// <see cref="UpdateCheckResult"/> has an internal constructor, so the results here are built
    /// the only way a caller can build them: through
    /// <see cref="UpdateChecker.ParseReleasePayload"/>. That is also the more honest fixture,
    /// because it is exactly the shape the real feed hands over.
    /// </para>
    /// </summary>
    public sealed class UpdateCheckPresenterTests
    {
        /// <summary>A payload the checker reads as a given release tag, against a given build.</summary>
        private static UpdateCheckResult Result(string tag, string current)
        {
            return UpdateChecker.ParseReleasePayload("{ \"tag_name\": \"" + tag + "\" }", current);
        }

        private static UpdateCheckResult Available()
        {
            return Result("v0.9.1", "0.9.0");
        }

        private static UpdateCheckResult UpToDate()
        {
            return Result("v0.9.0", "0.9.0");
        }

        private static UpdateCheckResult CouldNotCheck()
        {
            return UpdateChecker.ParseReleasePayload("not json at all", "0.9.0");
        }

        // ---- state mapping ---------------------------------------------------------------------

        [Fact]
        public void EachCheckerAnswerMapsToItsOwnState()
        {
            Assert.Equal(UpdateCheckState.UpdateAvailable, UpdateCheckPresenter.StateFor(Available()));
            Assert.Equal(UpdateCheckState.UpToDate, UpdateCheckPresenter.StateFor(UpToDate()));
            Assert.Equal(UpdateCheckState.CouldNotCheck, UpdateCheckPresenter.StateFor(CouldNotCheck()));
        }

        /// <summary>
        /// A caller that lost the result entirely must not end up claiming the player is up to
        /// date: the checker's own answer for that is Unknown, and Unknown is not UpToDate.
        /// </summary>
        [Fact]
        public void AMissingResultIsTreatedAsUnknownRatherThanUpToDate()
        {
            Assert.Equal(UpdateCheckState.CouldNotCheck, UpdateCheckPresenter.StateFor(null));
        }

        [Fact]
        public void AnOlderReleaseIsStillUpToDate()
        {
            // "Up to date" is the same answer for "identical" and "newer than me but still older
            // than the tag" - the player has nothing to do either way.
            Assert.Equal(UpdateCheckState.UpToDate, UpdateCheckPresenter.StateFor(Result("v0.8.12", "0.9.0")));
        }

        // ---- the messages ----------------------------------------------------------------------

        [Fact]
        public void AnUpdateNamesBothVersionsAndOffersADownload()
        {
            var shown = UpdateCheckPresenter.Describe(UpdateCheckPresenter.StateFor(Available()), Available());

            Assert.True(shown.OffersDownload);
            Assert.Contains("0.9.1", shown.Message);
            Assert.Contains("0.9.0", shown.Message);
            Assert.False(string.IsNullOrWhiteSpace(shown.Title));
        }

        [Fact]
        public void NoStateButAnUpdateOffersADownload()
        {
            // The property is the whole of the button's enablement, so a regression here would
            // either nag with no way to act or hide the one actionable answer.
            Assert.False(UpdateCheckPresenter.Describe(UpdateCheckState.UpToDate, UpToDate()).OffersDownload);
            Assert.False(UpdateCheckPresenter.Describe(UpdateCheckState.CouldNotCheck, CouldNotCheck()).OffersDownload);
            Assert.False(UpdateCheckPresenter.Describe(UpdateCheckState.Idle, null).OffersDownload);
            Assert.False(UpdateCheckPresenter.Describe(UpdateCheckState.Checking, null).OffersDownload);
            Assert.True(UpdateCheckPresenter.Describe(UpdateCheckState.UpdateAvailable, Available()).OffersDownload);
        }

        [Fact]
        public void AnUpToDateAnswerNamesTheVersionItIsUpToDateWith()
        {
            // Otherwise "you are up to date" is a claim the player cannot check.
            var shown = UpdateCheckPresenter.Describe(UpdateCheckState.UpToDate, Result("v0.9.0", "0.9.0"));
            Assert.Contains("0.9.0", shown.Message);
        }

        [Fact]
        public void AFailureSaysItCouldNotReachTheFeedRatherThanClaimingAnything()
        {
            // The wording matters: "you are up to date" after a failed check would be a false claim
            // about a build the checker never heard from anyone about.
            var shown = UpdateCheckPresenter.Describe(UpdateCheckState.CouldNotCheck, CouldNotCheck());
            Assert.DoesNotContain("up to date", shown.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("releases page", shown.Message);
        }

        [Fact]
        public void EveryStateProducesSomethingToRead()
        {
            // A blank caption is indistinguishable from a bug, and this row is the only place a
            // player ever sees the answer.
            foreach (var state in Enum.GetValues<UpdateCheckState>())
            {
                var shown = UpdateCheckPresenter.Describe(state, null);
                Assert.False(string.IsNullOrWhiteSpace(shown.Message), state + " has no message");
                Assert.False(string.IsNullOrWhiteSpace(shown.StatusText), state + " has no status text");
                Assert.False(string.IsNullOrWhiteSpace(shown.Title), state + " has no title");
            }
        }

        [Fact]
        public void TheRowStatusFitsTheMarginItIsPrintedIn()
        {
            // The row is one line in a ~230px column beside a centred OK button, at 12pt, so
            // roughly 40 characters is the budget. Sharing the dialog's whole sentence with it is
            // what made the row overrun, so the budget is the property - not "shorter than", which
            // a one-word status could satisfy while still being too long.
            foreach (var state in Enum.GetValues<UpdateCheckState>())
            {
                var shown = UpdateCheckPresenter.Describe(state, Available());
                Assert.True(shown.StatusText.Length <= 40,
                    state + ": the row status is " + shown.StatusText.Length + " characters: '" + shown.StatusText + "'");
                Assert.True(shown.StatusText.Length <= shown.Message.Length,
                    state + ": the row status is longer than the dialog message");
            }
        }

        [Fact]
        public void TheRowStatusStillNamesTheVersionItIsAbout()
        {
            Assert.Contains("0.9.1", UpdateCheckPresenter.Describe(UpdateCheckState.UpdateAvailable, Available()).StatusText);
            Assert.Contains("0.9.0", UpdateCheckPresenter.Describe(UpdateCheckState.UpToDate, UpToDate()).StatusText);
        }

        [Fact]
        public void TheRowStatusForAFailureDoesNotClaimToBeUpToDate()
        {
            Assert.DoesNotContain("up to date",
                UpdateCheckPresenter.Describe(UpdateCheckState.CouldNotCheck, CouldNotCheck()).StatusText,
                StringComparison.OrdinalIgnoreCase);
        }

        // ---- the download link ------------------------------------------------------------------

        [Fact]
        public void TheDownloadLinkIsAlwaysUsableAndNeverEmpty()
        {
            // A "Download" button with nowhere to go is worse than no button, so every state
            // resolves a URL even the ones that do not show it.
            foreach (var state in Enum.GetValues<UpdateCheckState>())
            {
                var shown = UpdateCheckPresenter.Describe(state, null);
                Assert.False(string.IsNullOrWhiteSpace(shown.DownloadUrl), state + " has no download URL");
                Assert.True(UpdateChecker.IsTrustedReleaseUrl(shown.DownloadUrl),
                    state + " offered a URL the checker would not open: " + shown.DownloadUrl);
            }
        }

        [Fact]
        public void TheDownloadLinkIsTheCheckerOwnUrlWhenItSuppliedOne()
        {
            var withUrl = UpdateChecker.ParseReleasePayload(
                "{ \"tag_name\": \"v0.9.1\", \"html_url\": \"https://github.com/riperiperi/Simitone/releases/tag/v0.9.1\" }",
                "0.9.0");

            var shown = UpdateCheckPresenter.Describe(UpdateCheckPresenter.StateFor(withUrl), withUrl);

            Assert.Equal("https://github.com/riperiperi/Simitone/releases/tag/v0.9.1", shown.DownloadUrl);
        }

        [Fact]
        public void TheDownloadLinkFallsBackToTheReleasesPageWhenThePayloadHadNoUrl()
        {
            Assert.Equal(SimitoneVersion.ReleasesPageUrl,
                UpdateCheckPresenter.Describe(UpdateCheckPresenter.StateFor(Available()), Available()).DownloadUrl);
        }

        // ---- the button ---------------------------------------------------------------------------

        [Fact]
        public void TheButtonReadsAsBusyOnlyWhileACheckIsInFlight()
        {
            Assert.Equal(UpdateCheckPresenter.ButtonCaption(UpdateCheckState.Checking),
                UpdateCheckPresenter.ButtonCaption(UpdateCheckState.Checking));

            // Everything else - including the three finished states - invites another press, which
            // is what makes a debounce a debounce rather than a one-shot.
            var idle = UpdateCheckPresenter.ButtonCaption(UpdateCheckState.Idle);
            Assert.False(string.IsNullOrWhiteSpace(idle));
            foreach (var state in new[] { UpdateCheckState.Idle, UpdateCheckState.UpToDate,
                UpdateCheckState.CouldNotCheck, UpdateCheckState.UpdateAvailable })
            {
                Assert.Equal(idle, UpdateCheckPresenter.ButtonCaption(state));
            }
        }

        [Fact]
        public void TheButtonCannotBePressedTwiceForOneCheck()
        {
            Assert.False(UpdateCheckPresenter.IsCheckAllowed(UpdateCheckState.Checking, null));
        }

        [Fact]
        public void TheButtonIsAlwaysAllowedBeforeAnyCheckHasRun()
        {
            // null is "never checked", which must not be read as "checked a moment ago".
            Assert.True(UpdateCheckPresenter.IsCheckAllowed(UpdateCheckState.Idle, null));
        }

        [Fact]
        public void TheButtonIsDebouncedAfterACheckAndAllowedAgainOnceTheWindowHasPassed()
        {
            // Each check is a real unauthenticated GitHub request against a 60/hour budget, so an
            // unthrottled button would spend the whole hour on one dialog.
            Assert.False(UpdateCheckPresenter.IsCheckAllowed(UpdateCheckState.UpToDate, TimeSpan.Zero));
            Assert.False(UpdateCheckPresenter.IsCheckAllowed(UpdateCheckState.UpdateAvailable, TimeSpan.FromSeconds(30)));
            Assert.True(UpdateCheckPresenter.IsCheckAllowed(UpdateCheckState.UpToDate, UpdateCheckPresenter.MinimumInterval));
            Assert.True(UpdateCheckPresenter.IsCheckAllowed(UpdateCheckState.UpToDate, TimeSpan.FromHours(1)));
        }

        [Fact]
        public void TheDebounceIsLongEnoughToMatterAndShortEnoughNotToAnnoy()
        {
            // A window of a second or two would not stop a determined clicker; a window of an hour
            // would make the button feel broken for the rest of the session.
            Assert.True(UpdateCheckPresenter.MinimumInterval >= TimeSpan.FromSeconds(15));
            Assert.True(UpdateCheckPresenter.MinimumInterval <= TimeSpan.FromMinutes(5));
        }

        [Fact]
        public void TheDebounceIsMeasuredFromTheLastRequestNotTheLastResult()
        {
            // A long request must not leave the button enabled on completion: the budget is spent by
            // the request, whatever the answer was.
            Assert.False(UpdateCheckPresenter.IsCheckAllowed(UpdateCheckState.Checking, TimeSpan.FromHours(1)));
        }

        // ---- the version stamp ---------------------------------------------------------------------

        [Theory]
        [InlineData("0.9.0", "Simitone 0.9.0")]
        [InlineData("v0.9.0", "Simitone 0.9.0")]
        [InlineData("0.9.0+sha.abcdef", "Simitone 0.9.0")]
        [InlineData("0.9", "Simitone 0.9.0")]
        public void TheRowShowsTheVersionItWouldCompareAgainst(string given, string expected)
        {
            // The same normalisation the checker applies, so the number on screen and the number in
            // the comparison cannot be spelled differently.
            Assert.Equal(expected, UpdateCheckPresenter.VersionText(given));
        }

        [Fact]
        public void TheRowAlwaysShowsSomethingForTheVersion()
        {
            // The fallback is the running version, not a blank: this label is how a player reports
            // which build they are on.
            Assert.Contains(SimitoneVersion.Current, UpdateCheckPresenter.VersionText(null));
            Assert.Contains(SimitoneVersion.Current, UpdateCheckPresenter.VersionText(""));
            Assert.Contains(SimitoneVersion.Current, UpdateCheckPresenter.VersionText("nightly"));
        }

        [Fact]
        public void TheRowShowsTheRealRunningVersionInThisBuild()
        {
            // The number on the row is the number in the comparison, spelled identically.
            Assert.Equal("Simitone " + SimitoneVersion.Current, UpdateCheckPresenter.VersionText(SimitoneVersion.Current));
        }

        // ---- the version source ---------------------------------------------------------------------

        [Theory]
        [InlineData("Simitone")]
        [InlineData("simitone")]
        [InlineData("SIMITONE")]
        public void TheGamesOwnAssemblyNameIsRecognised(string name)
        {
            Assert.True(SimitoneVersion.IsGameAssemblyName(name));
        }

        /// <summary>
        /// The test host, an IDE and a profiler are all entry assemblies too, and a version read off
        /// one of those would be the host's, not Simitone's. This is the rule that stops it.
        /// </summary>
        [Theory]
        [InlineData("testhost")]
        [InlineData("Simitone.Client")]
        [InlineData("Simitone.Windows")]
        [InlineData("Simitone.exe")]
        [InlineData("dotnet")]
        [InlineData("")]
        [InlineData(null)]
        public void AnyOtherEntryAssemblyIsNotTheGame(string name)
        {
            Assert.False(SimitoneVersion.IsGameAssemblyName(name));
        }

        [Fact]
        public void TheRunningVersionInATestHostIsNotTheHostsOwn()
        {
            // Asserted rather than assumed: a regression here would report the test runner's
            // version (17.x) as the game's, and every comparison in the suite would be meaningless.
            Assert.True(UpdateChecker.TryParseVersion(SimitoneVersion.Current, out var current));
            Assert.True(current.Major < 10,
                "SimitoneVersion.Current is " + SimitoneVersion.Current
                + ", which is not a plausible Simitone version - it looks like the test host's.");
        }

        // ---- the stamp, in the project that owns it -----------------------------------------------

        /// <summary>
        /// The value of <c>&lt;Version&gt;</c> in the game project, read from the file rather than
        /// from a built assembly.
        /// </summary>
        /// <remarks>
        /// <see cref="SimitoneVersion.Current"/> cannot answer this in a test process - it
        /// deliberately reads Simitone.Client rather than the entry assembly there, because the
        /// entry assembly is the test host. So the one thing that has to hold for the update check
        /// to ever do anything is checked where it is written instead: the shipped executable's
        /// project has to carry a real version, and the two ends of the wiring (the project file
        /// and the constant that decides which assembly to believe) have to agree.
        /// </remarks>
        [Fact]
        public void TheGameProjectStampsARealVersion()
        {
            var project = File.ReadAllText(FindGameProject());

            var version = ReadProperty(project, "Version");
            Assert.False(string.IsNullOrWhiteSpace(version),
                "Simitone.Windows.csproj sets no <Version>, so every build reports the SDK default of"
                + " 1.0.0 and the update check can never tell an unstamped build from a stamped one.");

            // 1.0.0 is Simitone's first real release, so the historical "must not be the SDK
            // default of 1.0.0" guard no longer applies - the version is now the release the update
            // checker compares tags against. What still has to hold is that the element is stamped
            // at all (ReadProperty returns null when it is absent, caught above) and that it is a
            // version a release tag could be compared with.
            Assert.True(UpdateChecker.TryParseVersion(version, out _),
                "The <Version> in Simitone.Windows.csproj (" + version + ") is not a version a release tag could be compared with.");
        }

        [Fact]
        public void TheAssemblyTheVersionIsReadFromIsTheOneTheGameProjectBuilds()
        {
            // If the <AssemblyName> and the constant SimitoneVersion matches by ever drift apart,
            // the game silently falls back to Simitone.Client's version and the check goes quiet
            // again - with no error anywhere, because both halves are individually correct.
            var project = File.ReadAllText(FindGameProject());
            var assemblyName = ReadProperty(project, "AssemblyName");

            Assert.Equal(SimitoneVersion.GameAssemblyName, assemblyName);
        }

        /// <summary>
        /// The game project file, found by walking up out of the test output rather than by a
        /// fixed number of levels, so a change to the build layout does not silently turn this
        /// into a "file not found" skip.
        /// </summary>
        private static string FindGameProject()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Simitone.Windows", "Simitone.Windows.csproj");
                if (File.Exists(candidate)) return candidate;

                candidate = Path.Combine(dir.FullName, "Simitone.Windows.csproj");
                if (File.Exists(candidate)) return candidate;

                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                "Could not find Simitone.Windows.csproj above " + AppContext.BaseDirectory);
        }

        /// <summary>
        /// The text of a top-level MSBuild property, whitespace-insensitive on the closing tag.
        /// Deliberately crude: this is a test assertion that the element is present and says
        /// something sensible, not an MSBuild evaluator, and a project that grew a second
        /// <c>&lt;Version&gt;</c> inside a condition would need a real evaluator anyway.
        /// </summary>
        private static string ReadProperty(string project, string name)
        {
            var open = "<" + name + ">";
            var close = "</" + name + ">";
            var start = project.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return null;

            var end = project.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0) return null;

            return project.Substring(start + open.Length, end - start - open.Length).Trim();
        }
    }
}
