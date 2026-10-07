using Simitone.Client.Utils;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Simitone.Client.Services
{
    /// <summary>
    /// Outcome of an update check. Ordered so that the most interesting answer is the largest
    /// value, which lets a caller that merges several sources (a startup check and a manual
    /// one, say) just take the maximum.
    /// </summary>
    public enum UpdateCheckStatus
    {
        /// <summary>
        /// The check could not be completed, or completed without producing a release version
        /// we recognise. Treated as "no update": the UI should say nothing rather than warn
        /// the player about a failure they cannot act on.
        /// </summary>
        Unknown = 0,

        /// <summary>The newest published release is the same as, or older than, this build.</summary>
        UpToDate = 1,

        /// <summary>The newest published release is strictly newer than this build.</summary>
        UpdateAvailable = 2
    }

    /// <summary>
    /// Immutable result of an <see cref="UpdateChecker"/> call. Never null, and never carries
    /// an exception: a failed check is <see cref="UpdateCheckStatus.Unknown"/> with the reason
    /// recorded in <see cref="SimitoneLog"/>, never an exception at the call site.
    /// </summary>
    public sealed class UpdateCheckResult
    {
        internal UpdateCheckResult(
            UpdateCheckStatus status,
            string currentVersion,
            string latestTag,
            string latestVersion,
            string releaseUrl,
            string releaseNotes)
        {
            Status = status;
            CurrentVersion = currentVersion;
            LatestTag = latestTag;
            LatestVersion = latestVersion;
            ReleaseUrl = releaseUrl;
            ReleaseNotes = releaseNotes;
        }

        /// <summary>What the check concluded. See <see cref="UpdateCheckStatus"/>.</summary>
        public UpdateCheckStatus Status { get; }

        /// <summary>
        /// The version this build reports, normalised to <c>X.Y.Z</c>. Always populated, even
        /// for <see cref="UpdateCheckStatus.Unknown"/>, so a message can be built without
        /// having to ask a second question.
        /// </summary>
        public string CurrentVersion { get; }

        /// <summary>
        /// The release tag exactly as GitHub published it, e.g. <c>v0.8.12</c>. Null when the
        /// check was <see cref="UpdateCheckStatus.Unknown"/>.
        /// </summary>
        public string LatestTag { get; }

        /// <summary>
        /// <see cref="LatestTag"/> reduced to <c>X.Y.Z</c>, ready to show a player. Null when
        /// the check was <see cref="UpdateCheckStatus.Unknown"/>.
        /// </summary>
        public string LatestVersion { get; }

        /// <summary>
        /// Page to send the player to for the download, defaulted to
        /// <see cref="SimitoneVersion.ReleasesPageUrl"/> when the payload did not carry a
        /// usable one. Null when the check was <see cref="UpdateCheckStatus.Unknown"/>, in
        /// which case the UI should fall back to
        /// <see cref="SimitoneVersion.ReleasesPageUrl"/> itself.
        /// </summary>
        public string ReleaseUrl { get; }

        /// <summary>
        /// Release notes from the payload, which for Simitone's automated releases is an
        /// auto-generated commit list. Null when absent. Diagnostics/UI only - never assumed
        /// to be well formed, and never shown raw without trimming.
        /// </summary>
        public string ReleaseNotes { get; }

        /// <summary>Convenience for the common <c>if (result.UpdateAvailable)</c> branch.</summary>
        public bool UpdateAvailable => Status == UpdateCheckStatus.UpdateAvailable;

        public override string ToString()
        {
            var text = Status + " (running " + CurrentVersion;
            if (LatestVersion != null) text += ", latest " + LatestVersion;
            return text + ")";
        }
    }

    /// <summary>
    /// Asks the upstream Simitone repository whether a newer release exists.
    ///
    /// <para>
    /// Ported from the upstream fork's commit <c>a998dc6592</c> ("#16"), reshaped for this
    /// tree. What that commit did: a static <c>CheckForUpdatesAsync()</c> that fired a
    /// fire-and-forget <c>Task.Run</c> on startup, swallowed every exception silently, and
    /// then marshalled back onto the game thread to throw a <c>UIMobileAlert</c> at the
    /// player from inside the checker.
    /// </para>
    ///
    /// <para>
    /// What this does instead, and why:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Nothing is called for you.</b> There is no startup hook and no one-shot latch; the
    /// checker is a plain library call that returns a value. A "check once per process" latch
    /// is actively wrong for the place this should be surfaced - an Options entry - because it
    /// would let the player ask exactly once and never again. Deciding <em>when</em> to check
    /// is a product decision, so it is left to the caller.
    /// </description></item>
    /// <item><description>
    /// <b>No UI inside the checker.</b> The original built and showed the dialog itself, which
    /// meant the feature could not be reused (a quiet "check now" button wants a different
    /// presentation) and meant the UI decision was smuggled in with the network code. Here the
    /// result carries everything a dialog needs - version, notes, link - and the caller
    /// decides whether to present it.
    /// </description></item>
    /// <item><description>
    /// <b>It never throws.</b> <see cref="CheckAsync"/> is total: no DNS, no proxy, no TLS
    /// failure, no rate limit, no malformed payload and no cancelled token will reach the
    /// caller as an exception. Everything degrades to
    /// <see cref="UpdateCheckStatus.Unknown"/> and is written to <see cref="SimitoneLog"/>.
    /// </description></item>
    /// <item><description>
    /// <b>It never blocks startup.</b> The call is genuinely asynchronous and the
    /// <see cref="HttpClient"/> is built on first use rather than in a static constructor, so
    /// merely loading this type allocates nothing and opens no socket. Network I/O only
    /// happens on a call somebody actually made.
    /// </description></item>
    /// <item><description>
    /// <b>It talks to the right repository.</b> The destination is
    /// <see cref="SimitoneVersion.RepositorySlug"/> (upstream, not a fork), and a release
    /// payload's <c>html_url</c> is only handed to the shell if it is an https URL on that
    /// repository's host - otherwise the API response gets to choose an arbitrary protocol
    /// handler to launch.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Where this should be surfaced</b> (deliberately not wired up here - the UI lives in
    /// <c>TS1GameScreen</c>/<c>UIMainPanel</c>, and picking a spot is a product call):
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// A "Check for updates" row in the Options/Settings panel, next to the version string.
    /// The obvious first home: it is opt-in, so it costs a boot nothing, and it is the only
    /// presentation where "no update" and "could not check" can honestly share a message.
    /// Awaits <see cref="CheckAsync"/> off the game thread, then marshals the dialog through
    /// <c>GameThread.NextUpdate</c>; show anything only when
    /// <see cref="UpdateCheckResult.UpdateAvailable"/>, and debounce repeat presses because
    /// each call is a real API request.
    /// </description></item>
    /// <item><description>
    /// Optionally also once per session, after the first lot loads rather than at process
    /// start, and only if the Options entry has not been used. That keeps a player who is
    /// already mid-game from being interrupted, and it is the case the original's startup
    /// hook was reaching for.
    /// </description></item>
    /// </list>
    /// <para>
    /// The "Download" button is <see cref="OpenReleasePage"/>, and a permanent manual link is
    /// <see cref="SimitoneVersion.ReleasesPageUrl"/>. <see cref="UpdateCheckResult"/> is
    /// ordered so a caller merging a session check with a manual one can just take the
    /// largest <see cref="UpdateCheckStatus"/>.
    /// </para>
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>
        /// Product token sent in the <c>User-Agent</c> header. GitHub answers 403 to API
        /// requests that carry none, and proxies are free to drop requests that do.
        /// Deliberately a constant rather than the running version: putting the version in a
        /// static constructor would make <see cref="SimitoneVersion"/>'s initialisation
        /// depend on this type's, for no benefit.
        /// </summary>
        private const string UserAgentProduct = "Simitone-UpdateChecker";

        private const string GitHubJsonMediaType = "application/vnd.github+json";

        /// <summary>Ceiling on the whole request/response exchange.</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private static readonly object HttpClientLock = new object();
        private static HttpClient _httpClient;

        /// <summary>
        /// Asks GitHub for the newest release of <see cref="SimitoneVersion.RepositorySlug"/>
        /// and reports whether it is newer than <see cref="SimitoneVersion.Current"/>.
        /// </summary>
        /// <param name="cancellationToken">
        /// Optional. Cancelling does not throw: the call returns
        /// <see cref="UpdateCheckStatus.Unknown"/> like any other failure, so a caller that
        /// cancels a background check cannot crash itself.
        /// </param>
        /// <returns>
        /// A result that is never null and never accompanies an exception. An
        /// <see cref="UpdateCheckStatus.UpdateAvailable"/> result is the only one that should
        /// ever prompt the player; everything else means "carry on silently".
        /// </returns>
        /// <remarks>
        /// <para>
        /// This is the whole public entry point and it is safe to call from the game thread
        /// with <c>await</c>, from a background task with <c>await</c>, or fire-and-forget -
        /// it never blocks the caller and never throws at it. It is <em>not</em> throttled:
        /// each call is a real request, and GitHub allows 60 unauthenticated API calls per
        /// hour per IP, so an entry that the player can press repeatedly should debounce on
        /// its own side.
        /// </para>
        /// </remarks>
        public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            string payload;

            try
            {
                using (var response = await GetHttpClient()
                    .GetAsync(SimitoneVersion.LatestReleaseApiUrl, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        SimitoneLog.Warn(
                            "Update check: the release API answered " + (int)response.StatusCode +
                            " " + response.ReasonPhrase + "; treating this build as up to date.");
                        return Unknown(SimitoneVersion.Current);
                    }

                    payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // No network, DNS/proxy/TLS failure, rate limit, a cancelled token, a disposed
                // client. All of them mean the same thing to a player: do not interrupt them.
                SimitoneLog.Warn("Update check could not reach the release API; treating this build as up to date.", ex);
                return Unknown(SimitoneVersion.Current);
            }

            // The parse is total in its own right, so this is belt and braces - but an
            // unreadable payload is the one failure a user would otherwise never hear about,
            // so it is worth a line in the log even though the answer stays "no update".
            var result = ParseReleasePayload(payload, SimitoneVersion.Current);
            if (result.Status == UpdateCheckStatus.Unknown)
            {
                SimitoneLog.Warn("Update check could not read a release version from the API payload; treating this build as up to date.");
            }

            return result;
        }

        /// <summary>
        /// Interprets a GitHub "latest release" JSON payload and compares its tag against the
        /// running version.
        /// </summary>
        /// <param name="json">The raw response body. May be null, empty or malformed.</param>
        /// <param name="currentVersion">
        /// The running version, in any form <see cref="TryParseVersion"/> accepts (typically
        /// <see cref="SimitoneVersion.Current"/>).
        /// </param>
        /// <returns>
        /// A result that is never null. Anything unusable - not JSON, not an object, no
        /// <c>tag_name</c>, a <c>tag_name</c> that is not a recognisable version - yields
        /// <see cref="UpdateCheckStatus.Unknown"/>.
        /// </returns>
        /// <remarks>
        /// Pure: no IO, no globals, no logging, and it never throws. That is what makes the
        /// whole decision - including the malformed-payload paths that cannot be provoked over
        /// a real network - unit testable.
        /// </remarks>
        public static UpdateCheckResult ParseReleasePayload(string json, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(json)) return Unknown(currentVersion);

            string tag;
            string htmlUrl;
            string notes;

            try
            {
                using (var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    // Matches RemeshPackage: GitHub does not send either, but a payload that
                    // has them is still obviously a release document and worth reading.
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return Unknown(currentVersion);

                    if (!TryGetString(document.RootElement, "tag_name", out tag)) return Unknown(currentVersion);
                    TryGetString(document.RootElement, "html_url", out htmlUrl);
                    TryGetString(document.RootElement, "body", out notes);
                }
            }
            catch (JsonException)
            {
                // An HTML error page, a truncated body, a proxy's captive-portal JSON: all
                // arrive here and all mean the same thing.
                return Unknown(currentVersion);
            }

            // Classify answers Unknown for an unreadable tag *or* an unreadable running
            // version, and either way guessing would be worse than staying quiet: a tag the
            // parser cannot read is far more likely to be a rename or a non-release tag than
            // something the player is missing.
            var status = Classify(tag, currentVersion);
            if (status == UpdateCheckStatus.Unknown) return Unknown(currentVersion);

            var releaseUrl = IsTrustedReleaseUrl(htmlUrl) ? htmlUrl : SimitoneVersion.ReleasesPageUrl;

            // Classify is the single place the verdict is decided; the parse here only turns
            // the tag into something to show a player, and cannot fail because it just
            // accepted the same string.
            TryParseVersion(tag, out var latest);

            return new UpdateCheckResult(status, Normalize(currentVersion), tag, latest.ToString(), releaseUrl, notes);
        }

        /// <summary>
        /// Decides what a release tag means relative to the running version.
        /// </summary>
        /// <param name="latestTag">The remote tag, e.g. <c>v0.8.12</c> or <c>v0.9.0-beta1</c>.</param>
        /// <param name="currentVersion">The running version, e.g. <c>0.8.12</c>.</param>
        /// <returns>
        /// <see cref="UpdateCheckStatus.UpdateAvailable"/> when the tag is strictly newer,
        /// <see cref="UpdateCheckStatus.UpToDate"/> when it is the same or older, and
        /// <see cref="UpdateCheckStatus.Unknown"/> when <em>either</em> side is not a
        /// recognisable version. An unreadable version is deliberately not guessed at: a
        /// silent "no update" is always the harmless answer.
        /// </returns>
        public static UpdateCheckStatus Classify(string latestTag, string currentVersion)
        {
            if (!TryParseVersion(latestTag, out var latest)) return UpdateCheckStatus.Unknown;
            if (!TryParseVersion(currentVersion, out var current)) return UpdateCheckStatus.Unknown;

            return latest.CompareTo(current) > 0
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate;
        }

        /// <summary>
        /// Reads a release version out of a tag or version string.
        /// </summary>
        /// <param name="tag">
        /// Accepted shapes, all of which yield a three-component <see cref="Version"/>:
        /// <list type="bullet">
        /// <item><description>An optional leading <c>v</c>/<c>V</c>: <c>v0.8.12</c>.</description></item>
        /// <item><description>Surrounding whitespace, and a pre-release or build-metadata
        /// suffix that is cut at the first <c>-</c>, <c>+</c> or space:
        /// <c>0.9.0-beta1</c>, <c>1.2.3+sha.abcdef</c>.</description></item>
        /// <item><description>A missing third component, which reads as 0: <c>1.2</c> is
        /// <c>1.2.0</c>, so it compares equal to <c>v1.2.0</c>.</description></item>
        /// <item><description>A fourth component, which is ignored: <c>1.2.3.4</c> is
        /// <c>1.2.3</c>. A .NET build/revision number is not a SemVer patch bump, and
        /// honouring it would make "1.2.3.4" look newer than the "1.2.3" that shipped the
        /// same code.</description></item>
        /// </list>
        /// </param>
        /// <param name="version">
        /// The parsed version, or null when <paramref name="tag"/> is not a version. Always
        /// exactly three components, which is what makes the missing-component rule above
        /// possible: without it, .NET's own <see cref="Version"/> compares <c>1.2</c> as
        /// <em>older</em> than <c>1.2.0</c> (its build is -1), and every such tag would
        /// offer a "downgrade".
        /// </param>
        /// <returns>True when <paramref name="tag"/> is a recognisable version.</returns>
        /// <remarks>
        /// Rejects rather than guesses: null/blank, a lone <c>v</c>, a single component
        /// (<c>1</c>), an empty component (<c>1..3</c>, <c>1.</c>) and a non-numeric one
        /// (<c>1.x.3</c>). Note the difference between a <em>missing</em> component
        /// (<c>1.2</c>) and an <em>empty</em> one: the first is a short but valid tag, the
        /// second is a malformed one. A sign or an embedded space cannot reach the component
        /// parser at all - both are cut as suffix separators first - which is why components
        /// are additionally read with <see cref="NumberStyles.None"/>.
        /// </remarks>
        public static bool TryParseVersion(string tag, out Version version)
        {
            version = null;

            if (string.IsNullOrWhiteSpace(tag)) return false;

            var text = tag.Trim();

            // GitHub release tags conventionally carry a "v" ("v0.8.12"). A bare version
            // string is equally valid, so the prefix is optional rather than required.
            if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
            {
                text = text.Substring(1).TrimStart();
            }

            // Cut the pre-release / build-metadata suffix. Done by hand rather than with
            // Split so this method touches no static state, which in turn keeps it callable
            // from SimitoneVersion's own initialiser.
            var end = text.Length;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '-' || c == '+' || char.IsWhiteSpace(c))
                {
                    end = i;
                    break;
                }
            }
            if (end == 0) return false;
            text = text.Substring(0, end);

            var parts = text.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;

            if (!TryComponent(parts[0], out var major)) return false;
            if (!TryComponent(parts[1], out var minor)) return false;

            // Only a genuinely absent third component defaults to 0.
            var build = 0;
            if (parts.Length >= 3 && !TryComponent(parts[2], out build)) return false;

            version = new Version(major, minor, build);
            return true;
        }

        /// <summary>
        /// Whether <paramref name="url"/> may be handed to the shell to open in a browser.
        /// </summary>
        /// <remarks>
        /// The <c>html_url</c> in the payload is attacker-influenced data - it comes from an
        /// unauthenticated HTTP response, so a hostile network or a spoofed API can put
        /// anything in it. Launching it with <c>UseShellExecute</c> would let that response
        /// choose the protocol handler, including <c>file:</c> and UNC paths. So the only
        /// URLs accepted are https URLs whose host is the host of
        /// <see cref="SimitoneVersion.ReleasesPageUrl"/> - which is what makes this a
        /// single-constant policy that retargets along with the repository.
        /// </remarks>
        public static bool IsTrustedReleaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;
            if (!string.Equals(uri.Host, GetExpectedReleaseHost(), StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }

        /// <summary>
        /// Opens a release page in the player's default browser.
        /// </summary>
        /// <param name="url">
        /// Preferred destination, typically <see cref="UpdateCheckResult.ReleaseUrl"/>. An
        /// untrusted value is not opened as given: the releases page is opened instead.
        /// </param>
        /// <returns>True if the browser was launched, false if it could not be. Never throws.</returns>
        public static bool OpenReleasePage(string url)
        {
            var target = IsTrustedReleaseUrl(url) ? url : SimitoneVersion.ReleasesPageUrl;

            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                };

                // The process belongs to the shell now; all that is left is to release the
                // handle so the object does not linger for the finalizer.
                var process = Process.Start(info);
                process?.Dispose();

                SimitoneLog.Info("Update check: opened " + target);
                return true;
            }
            catch (Exception ex)
            {
                // No browser registered, a locked-down shell, a headless session. The player
                // still has the URL on screen to copy.
                SimitoneLog.Warn("Update check could not open a browser for " + target, ex);
                return false;
            }
        }

        /// <summary>
        /// Builds the "nothing to report" result. Reports the version the caller asked about
        /// rather than re-reading the global, so a caller that passed an explicit version (as
        /// every test and any future "compare against this build" caller does) gets that
        /// version back and not the running one.
        /// </summary>
        private static UpdateCheckResult Unknown(string currentVersion)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unknown, Normalize(currentVersion), null, null, null, null);
        }

        /// <summary>
        /// Reduces a version to the bare <c>X.Y.Z</c> shape a message can show, leaving an
        /// unrecognisable one alone rather than inventing a value for it.
        /// </summary>
        private static string Normalize(string version)
        {
            if (TryParseVersion(version, out var parsed)) return parsed.ToString();
            return string.IsNullOrWhiteSpace(version) ? SimitoneVersion.Current : version;
        }

        private static bool TryComponent(string text, out int value)
        {
            value = 0;

            // An empty component is malformed, not absent: "1..3" and "1." are typos, and
            // reading them as "1.0.3" would be inventing a version nobody published.
            if (string.IsNullOrEmpty(text)) return false;

            // NumberStyles.None rejects signs and whitespace, so "-1" and " 1" are not
            // silently folded into a positive component.
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetString(JsonElement element, string name, out string value)
        {
            value = null;

            if (element.ValueKind != JsonValueKind.Object) return false;
            if (!element.TryGetProperty(name, out var property)) return false;
            if (property.ValueKind != JsonValueKind.String) return false;

            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string GetExpectedReleaseHost()
        {
            // ReleasesPageUrl is a const, so this is folded at compile time and creates no
            // initialisation ordering with SimitoneVersion.
            return Uri.TryCreate(SimitoneVersion.ReleasesPageUrl, UriKind.Absolute, out var uri)
                ? uri.Host
                : string.Empty;
        }

        private static HttpClient GetHttpClient()
        {
            // Created on first use rather than in a static constructor: loading this type must
            // not allocate a handler, and a static field that threw would poison every later
            // call with a TypeInitializationException - including the paths that exist
            // precisely to report that something is wrong.
            lock (HttpClientLock)
            {
                if (_httpClient != null) return _httpClient;

                try
                {
                    var handler = new SocketsHttpHandler
                    {
                        // Opts in to gzip/brotli and transparently undoes it. A release
                        // payload is mostly a commit list, so the saving is real - and it
                        // costs nothing when the response does turn out to be small.
                        AutomaticDecompression = DecompressionMethods.All,
                        ConnectTimeout = RequestTimeout,
                        // Keeps DNS from being pinned to a single address for the life of a
                        // long play session.
                        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                    };

                    var client = new HttpClient(handler)
                    {
                        // Covers the whole exchange, not just the connect, so a server that
                        // accepts and then trickles cannot pin the caller either.
                        Timeout = RequestTimeout
                    };

                    // GitHub answers 403 to API requests with no User-Agent, so this is
                    // load-bearing rather than polite. Guarded because a rejected header
                    // value would otherwise cost us the whole client.
                    try
                    {
                        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProduct));
                        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubJsonMediaType));
                    }
                    catch (Exception ex)
                    {
                        SimitoneLog.Warn("Update check could not set its HTTP headers; the check may be rejected.", ex);
                    }

                    _httpClient = client;
                }
                catch (Exception ex)
                {
                    // A client with no handler still works; it just loses connection reuse
                    // and decompression. Better than having no check at all.
                    SimitoneLog.Warn("Update check could not configure its HTTP client; falling back to defaults.", ex);
                    _httpClient = new HttpClient { Timeout = RequestTimeout };
                }

                return _httpClient;
            }
        }
    }
}
