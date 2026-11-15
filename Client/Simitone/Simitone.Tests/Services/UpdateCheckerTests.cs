using Simitone.Client;
using Simitone.Client.Services;
using System;
using Xunit;

namespace Simitone.Tests.Services
{
    /// <summary>
    /// Tests for <see cref="UpdateChecker"/> and <see cref="SimitoneVersion"/>, the
    /// self-contained half of upstream commit <c>a998dc6592</c> ("#16").
    ///
    /// <para>
    /// The checker's contract is that it never throws and never blocks: every failure the
    /// player can cause - no network, a rate-limited API, a captive portal, a release renamed
    /// to something unparseable - has to collapse into
    /// <see cref="UpdateCheckStatus.Unknown"/>, which the UI treats as "say nothing". None of
    /// that is reachable by pointing a real request at a real API in a unit test, so the
    /// decision is split into pieces that are:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="UpdateChecker.TryParseVersion"/> - the tag grammar, on its own.
    /// </description></item>
    /// <item><description>
    /// <see cref="UpdateChecker.Classify"/> - older / equal / newer, including the inputs
    /// that used to be compared as strings.
    /// </description></item>
    /// <item><description>
    /// <see cref="UpdateChecker.ParseReleasePayload"/> - the whole response handling, with no
    /// network at all: <c>html_url</c> fallbacks, untrusted URLs, malformed JSON, a missing or
    /// non-string <c>tag_name</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="UpdateChecker.IsTrustedReleaseUrl"/> - the rule that decides what the
    /// checker is willing to hand to the shell.
    /// </description></item>
    /// <item><description>
    /// <see cref="SimitoneVersion"/> - the repository/URL constants, and the promise that
    /// <see cref="SimitoneVersion.Current"/> tracks the assembly it is compiled into.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// <see cref="UpdateChecker.CheckAsync"/> itself is deliberately not called here. It is
    /// the one part that would touch a socket, and a network-dependent test is a flaky test;
    /// everything it does apart from the HTTP round trip is covered below. Nothing in this file
    /// creates a graphics device, reads content, plays audio or writes a log file.
    /// </para>
    /// </summary>
    public sealed class UpdateCheckerTests
    {
        // ---------------------------------------------------------------------
        // TryParseVersion: the tag grammar
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData("v0.8.12", 0, 8, 12)]  // the shape GitHub release tags actually use
        [InlineData("0.8.12", 0, 8, 12)]   // a bare version string is equally valid
        [InlineData("V0.8.12", 0, 8, 12)]  // ...and the prefix is case-insensitive
        [InlineData("  v0.8.12  ", 0, 8, 12)] // a tag pasted out of a URL bar
        [InlineData("v0.9.0-beta1", 0, 9, 0)]   // pre-release suffix
        [InlineData("v0.9.0-rc.2", 0, 9, 0)]
        [InlineData("1.2.3+sha.abcdef", 1, 2, 3)] // SemVer build metadata
        [InlineData("v1.2.3-beta+sha.abcdef", 1, 2, 3)] // both at once
        [InlineData("1.2.3 rc 1", 1, 2, 3)]  // a space cuts too
        [InlineData("v10.0.7", 10, 0, 7)]
        public void ParsesEveryAcceptedTagShape(string tag, int major, int minor, int build)
        {
            Assert.True(UpdateChecker.TryParseVersion(tag, out var version));
            Assert.Equal(new Version(major, minor, build), version);
        }

        [Fact]
        public void AMissingPatchComponentReadsAsZero()
        {
            // "1.2" has to be 1.2.0 and not something .NET considers *older* than 1.2.0:
            // Version leaves Build at -1 for a two-part version, so comparing the raw parsed
            // values would offer every "v1.2" tag as a downgrade of "v1.2.0".
            Assert.True(UpdateChecker.TryParseVersion("v1.2", out var version));
            Assert.Equal(new Version(1, 2, 0), version);
        }

        [Fact]
        public void AFourthComponentIsIgnored()
        {
            // A .NET build/revision number is not a patch bump. Honouring it would make
            // "1.2.3.4" look newer than the "1.2.3" built from the same source.
            Assert.True(UpdateChecker.TryParseVersion("1.2.3.4", out var version));
            Assert.Equal(new Version(1, 2, 3), version);
        }

        [Fact]
        public void EveryAcceptedShapeYieldsExactlyThreeComponents()
        {
            // The property that makes the two rules above safe to rely on: a tag that parses
            // can always be stringified back to a bare "X.Y.Z", which is what the UI shows.
            // Build is a real value (>= 0) and Revision is left unset (-1), so ToString()
            // cannot grow a fourth component on the way to a message.
            var tags = new[] { "v1.2.3", "1.2", "1.2.3.4", "v9.9.9-rc1", "3.0.0+sha", "v0.8.12" };

            foreach (var tag in tags)
            {
                Assert.True(UpdateChecker.TryParseVersion(tag, out var version));
                Assert.Equal(-1, version.Revision);
                Assert.True(version.Build >= 0, tag + " produced a three-part version with no build component.");
                Assert.Matches(@"^\d+\.\d+\.\d+$", version.ToString());
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("v")]
        [InlineData("1")]
        [InlineData("nightly")]
        [InlineData("1.x.3")]
        [InlineData("1..3")]      // an *empty* component is a typo, unlike a *missing* one
        [InlineData("1.")]
        [InlineData(".")]
        [InlineData("1.2.3.4.5")] // too many components to be a version
        [InlineData("1.2.3.4.5.6")]
        [InlineData("-1.2.3")]    // leading sign, cut as a suffix
        [InlineData("1.-2.3")]    // a sign inside a component
        [InlineData("1. 2.3")]    // embedded whitespace is not a missing component
        public void RejectsAnythingThatIsNotAVersion(string tag)
        {
            Assert.False(UpdateChecker.TryParseVersion(tag, out var version));
            Assert.Null(version);
        }

        [Theory]
        // SimitoneVersion.Current and a release tag arrive in slightly different shapes, and
        // they have to go through one grammar. If they did not, a two-part build version
        // would never match a three-part tag and the checker would answer Unknown forever.
        [InlineData("0.8.12", "0.8.12")]
        [InlineData("0.8.12", "v0.8.12")]
        [InlineData("0.8.12", "0.8.12.0")]        // a four-part assembly version
        [InlineData("0.8.12", "0.8.12+sha.abcdef")] // SemVer build metadata
        [InlineData("1.2.3", "1.2.3-beta1")]
        [InlineData("1.2.0", "1.2")]               // ...and the missing-component case
        public void TheVersionItselfGoesThroughTheSameGrammarAsAReleaseTag(string expected, string own)
        {
            Assert.True(UpdateChecker.TryParseVersion(expected, out var want));
            Assert.True(UpdateChecker.TryParseVersion(own, out var got));
            Assert.Equal(want, got);
        }

        // ---------------------------------------------------------------------
        // Classify: older / equal / newer
        // ---------------------------------------------------------------------

        [Fact]
        public void AnOlderRemoteTagIsUpToDate()
        {
            Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Classify("v0.8.11", "0.8.12"));
            Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Classify("0.1.0", "0.8.12"));
        }

        [Fact]
        public void TheSameRemoteTagIsUpToDate()
        {
            Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Classify("v0.8.12", "0.8.12"));
        }

        [Fact]
        public void ANewerRemoteTagIsAnUpdate()
        {
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, UpdateChecker.Classify("v0.8.13", "0.8.12"));
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, UpdateChecker.Classify("v0.9.0", "0.8.12"));
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, UpdateChecker.Classify("v1.0.0", "0.8.12"));
        }

        [Fact]
        public void ComparisonIgnoresPrefixesAndSuffixes()
        {
            // All of these are the same version, so all of them have to compare equal to a
            // bare "0.9.0" - otherwise whether a player is prompted depends on how the
            // maintainer happened to name the tag.
            Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Classify("V0.9.0", "v0.9.0"));
            Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Classify("0.9.0-rc1", "0.9.0"));
            Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Classify("0.9.0+sha.abc", "0.9.0"));

            // And a suffixed tag can still be genuinely newer.
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, UpdateChecker.Classify("v0.9.0-beta1", "0.8.12"));
        }

        [Theory]
        // The regression the original implementation had. It fell back to an ordinal string
        // compare whenever Version.TryParse failed, and "0.8.9" sorts *after* "0.8.10" as a
        // string, so a patch release would have been reported as a downgrade (and, in the
        // mirrored pair, a downgrade as a patch release). Both directions are pinned.
        [InlineData("0.8.9", "0.8.10", UpdateCheckStatus.UpToDate)]
        [InlineData("0.8.10", "0.8.9", UpdateCheckStatus.UpdateAvailable)]
        // Same trap one digit wider, where a lexicographic compare inverts the ordering.
        [InlineData("0.9.0", "0.10.0", UpdateCheckStatus.UpToDate)]
        [InlineData("0.10.0", "0.9.0", UpdateCheckStatus.UpdateAvailable)]
        // And a whole component wider, for the same reason.
        [InlineData("0.9.9", "1.0.0", UpdateCheckStatus.UpToDate)]
        [InlineData("1.0.0", "0.9.9", UpdateCheckStatus.UpdateAvailable)]
        public void ComparesNumericallyRatherThanAsText(string tag, string current, UpdateCheckStatus expected)
        {
            Assert.Equal(expected, UpdateChecker.Classify(tag, current));
        }

        [Fact]
        public void AShortRemoteTagStillMatchesTheEquivalentLongOne()
        {
            // "v1.2" and "1.2.0" are the same build. If the two-part form parsed to something
            // that compares older, every "v1.2" tag would be offered as a downgrade.
            Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Classify("v1.2", "1.2.0"));
            Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Classify("1.2.0", "v1.2"));
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, UpdateChecker.Classify("v1.3", "1.2.0"));
        }

        [Theory]
        [InlineData("nightly", "0.8.12")]   // an unreadable remote tag
        [InlineData("", "0.8.12")]
        [InlineData(null, "0.8.12")]
        [InlineData("v0.8.12", "nightly")]  // an unreadable local version
        [InlineData("v0.8.12", "")]
        [InlineData("v0.8.12", null)]
        [InlineData(null, null)]
        public void AnUnreadableVersionOnEitherSideIsUnknownRatherThanGuessed(string tag, string current)
        {
            // Guessing here is how an update checker starts nagging about a build the player
            // already has, or stays silent about one they do not. Unknown means "say nothing".
            Assert.Equal(UpdateCheckStatus.Unknown, UpdateChecker.Classify(tag, current));
        }

        [Fact]
        public void AClassifiedTagIsNeverAlsoClassifiedUnknown()
        {
            // Sanity check on the wiring between the two halves: anything TryParseVersion
            // accepts on both sides has to produce a real verdict.
            for (var minor = 0; minor < 15; minor++)
            {
                var current = "0.8." + minor;
                Assert.NotEqual(UpdateCheckStatus.Unknown, UpdateChecker.Classify("v" + current, current));
            }
        }

        // ---------------------------------------------------------------------
        // ParseReleasePayload: the response handling, with no network
        // ---------------------------------------------------------------------

        [Fact]
        public void ANewerReleaseInThePayloadBecomesAnUpdate()
        {
            var json = @"{
                ""tag_name"": ""v0.8.13"",
                ""html_url"": ""https://github.com/riperiperi/Simitone/releases/tag/v0.8.13"",
                ""name"": null,
                ""body"": ""Fixed the camera.""
            }";

            var result = UpdateChecker.ParseReleasePayload(json, "0.8.12");

            Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
            Assert.True(result.UpdateAvailable);
            Assert.Equal("0.8.12", result.CurrentVersion);
            Assert.Equal("v0.8.13", result.LatestTag);
            Assert.Equal("0.8.13", result.LatestVersion);
            Assert.Equal("https://github.com/riperiperi/Simitone/releases/tag/v0.8.13", result.ReleaseUrl);
            Assert.Equal("Fixed the camera.", result.ReleaseNotes);
        }

        [Fact]
        public void TheSameReleaseInThePayloadIsUpToDate()
        {
            var json = @"{ ""tag_name"": ""v0.8.12"", ""html_url"": ""https://github.com/riperiperi/Simitone/releases"" }";

            var result = UpdateChecker.ParseReleasePayload(json, "0.8.12");

            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
            Assert.False(result.UpdateAvailable);
            // The latest version is still reported so a "you're up to date" message can name it.
            Assert.Equal("0.8.12", result.LatestVersion);
        }

        [Fact]
        public void AnOlderReleaseInThePayloadIsUpToDate()
        {
            var result = UpdateChecker.ParseReleasePayload(@"{ ""tag_name"": ""v0.7.0"" }", "0.8.12");

            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
            Assert.Equal("0.7.0", result.LatestVersion);
        }

        [Fact]
        public void APayloadWithoutAnHtmlUrlFallsBackToTheReleasesPage()
        {
            var result = UpdateChecker.ParseReleasePayload(@"{ ""tag_name"": ""v0.9.0"" }", "0.8.12");

            Assert.Equal(SimitoneVersion.ReleasesPageUrl, result.ReleaseUrl);
        }

        [Theory]
        // The html_url comes out of an unauthenticated HTTP response, so it is
        // attacker-influenced data. It ends up as a shell target, so anything that is not an
        // https URL on the repository's own host has to be discarded in favour of the
        // releases page.
        //
        // A UNC path ("\\host\share") is the case that cannot be spelled here: a backslash is
        // an escape character in JSON, so embedding one verbatim would make the payload
        // unparseable and the test would be measuring the wrong thing. It is covered at the
        // URL level in RejectsEverythingElse instead, which is where the rule lives anyway.
        [InlineData(@"http://github.com/riperiperi/Simitone/releases")]        // not https
        [InlineData(@"https://evil.example/steal")]                           // wrong host
        [InlineData(@"https://github.com.evil.example/steal")]                 // suffix host
        [InlineData(@"https://user:pass@evil.example/steal")]                 // and with credentials
        [InlineData(@"file:///C:/Windows/System32/calc.exe")]
        [InlineData(@"javascript:alert(1)")]
        [InlineData(@"/relative/path")]
        public void AnUntrustedHtmlUrlIsReplacedByTheReleasesPage(string url)
        {
            var json = "{ \"tag_name\": \"v0.9.0\", \"html_url\": \"" + url + "\" }";

            var result = UpdateChecker.ParseReleasePayload(json, "0.8.12");

            Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
            Assert.Equal(SimitoneVersion.ReleasesPageUrl, result.ReleaseUrl);
        }

        [Theory]
        [InlineData(@"{ ""tag_name"": ""v0.9.0""", UpdateCheckStatus.Unknown)]      // truncated
        [InlineData(@"not json at all", UpdateCheckStatus.Unknown)]                 // an HTML error page
        [InlineData(@"<html><body>503</body></html>", UpdateCheckStatus.Unknown)]   // a proxy's page
        [InlineData(@"[]", UpdateCheckStatus.Unknown)]                              // valid JSON, wrong shape
        [InlineData(@"null", UpdateCheckStatus.Unknown)]
        [InlineData(@"{ ""html_url"": ""https://github.com/riperiperi/Simitone"" }", UpdateCheckStatus.Unknown)] // no tag
        [InlineData(@"{ ""tag_name"": null }", UpdateCheckStatus.Unknown)]
        [InlineData(@"{ ""tag_name"": 12 }", UpdateCheckStatus.Unknown)]            // right key, wrong type
        [InlineData(@"{ ""tag_name"": ""nightly"" }", UpdateCheckStatus.Unknown)]    // not a version
        [InlineData(@"{ ""tag_name"": ""1"" }", UpdateCheckStatus.Unknown)]          // not a version
        [InlineData(@"", UpdateCheckStatus.Unknown)]
        [InlineData("   ", UpdateCheckStatus.Unknown)]
        [InlineData(null, UpdateCheckStatus.Unknown)]
        public void AnUnusablePayloadIsUnknownAndCarriesNothing(string json, UpdateCheckStatus expected)
        {
            var result = UpdateChecker.ParseReleasePayload(json, "0.8.12");

            Assert.NotNull(result);
            Assert.Equal(expected, result.Status);
            Assert.False(result.UpdateAvailable);

            // Nothing half-filled: a caller must not be able to read a latest version or a
            // link out of a result that never got one.
            Assert.Null(result.LatestTag);
            Assert.Null(result.LatestVersion);
            Assert.Null(result.ReleaseUrl);
            Assert.Null(result.ReleaseNotes);

            // CurrentVersion is the one field that is always populated, so a message can be
            // built without asking a second question.
            Assert.Equal("0.8.12", result.CurrentVersion);
        }

        [Fact]
        public void APayloadWithNullsAndExtrasIsStillRead()
        {
            // What the real endpoint actually returns: dozens of fields, most of them null or
            // irrelevant, plus a null name on Simitone's automated releases.
            var json = @"{
                ""url"": ""https://api.github.com/repos/riperiperi/Simitone/releases/195235728"",
                ""assets_url"": ""https://api.github.com/repos/riperiperi/Simitone/releases/195235728/assets"",
                ""id"": 195235728,
                ""tag_name"": ""v0.8.12"",
                ""target_commitish"": ""c3c1678"",
                ""name"": null,
                ""draft"": false,
                ""prerelease"": false,
                ""created_at"": ""2025-01-16T23:15:01Z"",
                ""assets"": [],
                ""body"": null
            }";

            var result = UpdateChecker.ParseReleasePayload(json, "0.8.12");

            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
            Assert.Equal("v0.8.12", result.LatestTag);
            Assert.Null(result.ReleaseNotes);
        }

        [Fact]
        public void TrailingCommasAndCommentsAreTolerated()
        {
            var json = @"{ ""tag_name"": ""v0.9.0"", /* released today */ }";

            Assert.Equal(UpdateCheckStatus.UpdateAvailable, UpdateChecker.ParseReleasePayload(json, "0.8.12").Status);
        }

        [Fact]
        public void AReleaseUrlIsOnlyUsedWhenItIsOnTheRepositoryHost()
        {
            var trusted = UpdateChecker.ParseReleasePayload(
                @"{ ""tag_name"": ""v0.9.0"", ""html_url"": ""https://github.com/riperiperi/Simitone/releases/tag/v0.9.0"" }",
                "0.8.12");

            Assert.Equal("https://github.com/riperiperi/Simitone/releases/tag/v0.9.0", trusted.ReleaseUrl);
            Assert.True(UpdateChecker.IsTrustedReleaseUrl(trusted.ReleaseUrl));
        }

        [Fact]
        public void TheResultDescribesItselfForDiagnostics()
        {
            var available = UpdateChecker.ParseReleasePayload(@"{ ""tag_name"": ""v0.9.0"" }", "0.8.12");
            Assert.Contains("UpdateAvailable", available.ToString());
            Assert.Contains("0.8.12", available.ToString());
            Assert.Contains("0.9.0", available.ToString());

            var unknown = UpdateChecker.ParseReleasePayload("nope", "0.8.12");
            Assert.Contains("Unknown", unknown.ToString());
        }

        // ---------------------------------------------------------------------
        // IsTrustedReleaseUrl: the shell hand-off rule
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData("https://github.com/riperiperi/Simitone/releases")]
        [InlineData("https://github.com/riperiperi/Simitone/releases/tag/v0.8.12")]
        [InlineData("https://GitHub.com/riperiperi/Simitone/releases")]
        public void AcceptsHttpsUrlsOnTheRepositoryHost(string url)
        {
            Assert.True(UpdateChecker.IsTrustedReleaseUrl(url));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a url")]
        [InlineData("/releases")]
        [InlineData("releases")]
        [InlineData("http://github.com/riperiperi/Simitone/releases")]           // plaintext
        [InlineData("ftp://github.com/riperiperi/Simitone/releases")]
        [InlineData("file:///C:/Windows/System32/calc.exe")]
        [InlineData("\\\\evil.example\\share")]
        [InlineData("https://evil.example/github.com")]                             // host in the path
        [InlineData("https://github.com.evil.example/x")]
        [InlineData("https://evil.example/x?u=https://github.com/")]               // in the query
        public void RejectsEverythingElse(string url)
        {
            Assert.False(UpdateChecker.IsTrustedReleaseUrl(url));
        }

        [Fact]
        public void TheAllowlistFollowsTheRepositoryConstant()
        {
            // There is exactly one place the destination is named, so the security rule has to
            // be derived from it rather than hardcoding "github.com" a second time - which is
            // what would silently break the moment the project moved off GitHub.
            Assert.True(UpdateChecker.IsTrustedReleaseUrl(SimitoneVersion.ReleasesPageUrl));

            // A host the constant does not imply is still rejected, so the rule is not just
            // "accept whatever ReleasesPageUrl happens to be" without the https requirement.
            Assert.False(UpdateChecker.IsTrustedReleaseUrl(
                SimitoneVersion.ReleasesPageUrl.Replace("https://", "http://")));
        }

        // ---------------------------------------------------------------------
        // SimitoneVersion: the constants, and the version they report
        // ---------------------------------------------------------------------

        [Fact]
        public void TheCheckerPointsAtTheUpstreamRepository()
        {
            // Not at a fork. A fork's releases are builds the rest of the player base does not
            // have, so offering them as an "update" would be worse than offering nothing.
            Assert.Equal("MicRaving/simislop", SimitoneVersion.RepositorySlug);
        }

        [Fact]
        public void EveryUrlIsBuiltFromTheOneRepositoryConstant()
        {
            Assert.Equal("https://api.github.com/repos/MicRaving/simislop/releases/latest", SimitoneVersion.LatestReleaseApiUrl);
            Assert.Equal("https://github.com/MicRaving/simislop/releases", SimitoneVersion.ReleasesPageUrl);

            // Guard against someone hardcoding a URL back in later, which is how the two
            // constants drifted apart in the version this was ported from.
            Assert.Contains(SimitoneVersion.RepositorySlug, SimitoneVersion.LatestReleaseApiUrl);
            Assert.Contains(SimitoneVersion.RepositorySlug, SimitoneVersion.ReleasesPageUrl);
        }

        [Fact]
        public void TheApiUrlIsHttpsAndPointsAtTheReleaseEndpoint()
        {
            Assert.StartsWith("https://", SimitoneVersion.LatestReleaseApiUrl);
            Assert.StartsWith("https://", SimitoneVersion.ReleasesPageUrl);
            Assert.EndsWith("/releases/latest", SimitoneVersion.LatestReleaseApiUrl);
        }

        [Fact]
        public void TheRunningVersionIsOneTheCheckerCanParse()
        {
            // The whole point of deriving Current from the assembly rather than hardcoding it:
            // the checker has to be able to read it, or it would answer Unknown forever.
            Assert.True(
                UpdateChecker.TryParseVersion(SimitoneVersion.Current, out var current),
                "SimitoneVersion.Current (" + SimitoneVersion.Current + ") is not a parseable version.");

            // ...and nothing derived from a version string may leak into it. A SemVer
            // "+sha.abcdef" suffix or a "-beta" prerelease marker would still parse, but would
            // be shown to the player and compared as part of the version.
            Assert.DoesNotContain("+", SimitoneVersion.Current);
            Assert.DoesNotContain("-", SimitoneVersion.Current);
            Assert.DoesNotContain("v", SimitoneVersion.Current);
        }

        [Fact]
        public void TheRunningVersionTracksTheAssemblyItIsCompiledInto()
        {
            // The failure this replaces was a literal ("0.8.20-forked") that nothing tied to
            // the build, so a release could ship a binary claiming to be the previous version.
            // Asserted against the assembly rather than a hardcoded number so the test keeps
            // passing when the pipeline starts stamping -p:Version=... - it only pins that
            // the two never disagree about major/minor/build.
            var assemblyVersion = typeof(SimitoneVersion).Assembly.GetName().Version;

            Assert.True(UpdateChecker.TryParseVersion(SimitoneVersion.Current, out var current));
            Assert.Equal(assemblyVersion.Major, current.Major);
            Assert.Equal(assemblyVersion.Minor, current.Minor);
            Assert.Equal(assemblyVersion.Build < 0 ? 0 : assemblyVersion.Build, current.Build);
        }

        [Fact]
        public void TheRunningVersionIsNotTheUnstampedFallback()
        {
            // A .NET assembly always carries a numeric AssemblyVersion, so the fallback is
            // unreachable. If it ever is reached, Current would be 0.0.0 and the checker would
            // claim every release is an update - worth failing loudly about.
            Assert.NotEqual(SimitoneVersion.UnstampedVersion, SimitoneVersion.Current);
        }

        [Fact]
        public void TheRunningVersionIsReadTheSameWayAsAReleaseTag()
        {
            // End to end, with no network and no hardcoded expectation: whatever this build
            // reports, comparing it against a real upstream tag has to produce a verdict
            // rather than Unknown.
            Assert.NotEqual(UpdateCheckStatus.Unknown, UpdateChecker.Classify("v0.8.12", SimitoneVersion.Current));

            // ...and the verdict cannot depend on whether the tag carries its "v", because
            // SimitoneVersion.Current never does.
            Assert.Equal(
                UpdateChecker.Classify("v0.8.12", SimitoneVersion.Current),
                UpdateChecker.Classify("0.8.12", SimitoneVersion.Current));
        }
    }
}
