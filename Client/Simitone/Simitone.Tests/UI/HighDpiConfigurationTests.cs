using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Checks that the two places Simitone's high DPI mode is written down agree with each other.
    ///
    /// <para>
    /// The mode is declared twice, deliberately. <c>Simitone.Windows.csproj</c> says
    /// <c>&lt;ApplicationHighDpiMode&gt;</c>, which is the SDK's own vocabulary for it, and
    /// <c>app.manifest</c> says <c>&lt;dpiAware&gt;</c>, which is the only one of the two that
    /// actually reaches Windows. The WinForms analyzer's WFO0003 tells you to delete the manifest
    /// element and use the API/property instead; Simitone suppresses WFO0003 instead, with the
    /// reasoning written out in the project file (nothing calls
    /// <c>Application.SetHighDpiMode</c>, because Program.cs has its own hand-written Main, so
    /// trimming the manifest would leave the process DPI-unaware). A suppression plus two
    /// declarations is only safe while the two agree, and "only safe" is a thing that decays on
    /// its own - which is what this file is for.
    /// </para>
    ///
    /// <para>
    /// The value also has to stay per-monitor <em>v1</em>. Per-monitor v2 makes Windows deliver
    /// DPI changes to every window when it moves between displays, and the game's UI scaling is a
    /// single DPIScaleFactor baked into the back buffer for the session; a v2 upgrade would be a
    /// behaviour change nobody asked for, made silently by editing one attribute. So the test
    /// fails if either file drifts to v2 rather than trusting that nobody will.
    /// </para>
    ///
    /// <para>
    /// Both files carry long comments explaining exactly that, which is the point of them - and
    /// which is why every assertion here reads the files with their comments stripped first. A
    /// comment that <em>mentions</em> <c>PerMonitorV2</c> to say it must never be used would
    /// otherwise fail a test that exists to catch somebody using it.
    /// </para>
    /// </summary>
    public sealed class HighDpiConfigurationTests
    {
        [Fact]
        public void TheProjectDeclaresAHighDpiMode()
        {
            var mode = ReadProperty(WithoutComments(File.ReadAllText(GameProject())), "ApplicationHighDpiMode");

            Assert.False(string.IsNullOrWhiteSpace(mode),
                "Simitone.Windows.csproj declares no <ApplicationHighDpiMode>, so the DPI mode is"
                + " only recorded in app.manifest and the two can drift apart unnoticed.");
            Assert.Equal("PerMonitor", mode);
        }

        [Fact]
        public void TheManifestStillDeclaresDpiAwareness()
        {
            // The element WFO0003 asks us to delete. It is the only thing that sets the process's
            // awareness: ApplicationHighDpiMode is read by the WinForms source generator, which
            // emits a call to Application.SetHighDpiMode inside an ApplicationConfiguration
            // .Initialize() that Simitone's own Program.Main never calls.
            var manifest = WithoutComments(ManifestText());

            Assert.Contains("dpiAware", manifest);
            Assert.Equal("true/pm", ManifestDpiAwareValue(manifest));
        }

        [Fact]
        public void TheTwoDeclarationsNameTheSameAwareness()
        {
            var project = WithoutComments(File.ReadAllText(GameProject()));
            var projectMode = ReadProperty(project, "ApplicationHighDpiMode");
            var manifestValue = ManifestDpiAwareValue(WithoutComments(ManifestText()));
            var equivalent = EquivalentHighDpiMode(manifestValue);

            // Assert.True rather than a three-argument Assert.Equal: xunit v2 has no
            // (expected, actual, message) overload for strings, so the shape that reads like the
            // obvious one does not compile and silently becomes a collection comparison attempt.
            Assert.True(equivalent == projectMode,
                "Simitone.Windows.csproj says ApplicationHighDpiMode=" + projectMode
                + " but app.manifest says dpiAware=" + manifestValue
                + ". Those are " + Describe(projectMode) + " and " + Describe(manifestValue)
                + " to Windows, so the process would not run at the mode the project file claims.");
        }

        [Theory]
        [InlineData("true/pm", "PerMonitor")]   // per-monitor v1, what Simitone wants
        [InlineData("true/px", "PerMonitorV2")]
        [InlineData("true", "SystemAware")]
        [InlineData("false", "DpiUnaware")]
        [InlineData("nonsense", null)]
        public void TheManifestDpiAwareValuesMapOntoTheSdkVocabulary(string manifestValue, string expected)
        {
            // The table the "do they agree" assertion rests on, so the mapping cannot be quietly
            // re-picked to make a mismatch look like a match. An unrecognised value maps to null
            // so it fails the agreement check rather than passing as a match on anything.
            Assert.Equal(expected, EquivalentHighDpiMode(manifestValue));
        }

        [Fact]
        public void NeitherDeclarationHasDriftedToPerMonitorV2()
        {
            // See the class remarks: v2 changes how the game behaves when the window is dragged
            // between displays, which is a decision to be made on purpose rather than by editing
            // one attribute.
            var project = WithoutComments(File.ReadAllText(GameProject()));
            var manifest = WithoutComments(ManifestText());

            Assert.DoesNotContain("PerMonitorV2", project);
            Assert.DoesNotContain("PerMonitorV2", manifest);
            Assert.DoesNotContain("true/px", manifest);
        }

        [Fact]
        public void TheManifestKeepsTheWindowsVersionGuids()
        {
            // The other thing app.manifest is for: without the compatibility list Windows reports
            // 8.1 to anything asking, which changes DPI rounding and font metrics. A "just delete
            // the dpiAware element" edit that took the whole <application> block with it would
            // trade a warning for that.
            var manifest = WithoutComments(ManifestText());

            Assert.Contains("8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a", manifest);   // Windows 10/11
            Assert.Contains("1f676c76-80e1-4239-95bb-83d0f6d0da78", manifest);   // Windows 8.1
        }

        [Fact]
        public void TheWfo0003WarningIsSuppressedAndTheReasonIsWrittenDown()
        {
            // A bare <NoWarn> is a warning that has been silenced with no way back to it, so the
            // justification is asserted to be there. Only the presence of a substantial comment
            // next to it is checked, not its exact wording.
            var project = File.ReadAllText(GameProject());
            var noWarn = ReadProperty(WithoutComments(project), "NoWarn");

            Assert.False(string.IsNullOrWhiteSpace(noWarn),
                "Simitone.Windows.csproj does not suppress WFO0003, so the manifest's dpiAware"
                + " element will warn on every build. Either it should be suppressed with a"
                + " reason, or the element removed - not left in place warning.");
            Assert.Contains("WFO0003", noWarn);

            var comment = CommentPreceding(project, "NoWarn");
            Assert.True(comment.Length > 200,
                "The WFO0003 suppression has no substantive comment next to it (" + comment.Length
                + " characters). A suppression nobody can justify later is how a build quietly"
                + " ends up with a different DPI mode than it says it has.");
            Assert.Contains("SetHighDpiMode", comment);
        }

        [Fact]
        public void TheSuppressionIsScopedToThisProject()
        {
            // <NoWarn> is per project, but a property that leaked into a Directory.Build.props
            // would silence the warning for every project in the tree, including any that should
            // be listening for it. The suppression has to be readable here and nowhere wider.
            var project = WithoutComments(File.ReadAllText(GameProject()));

            Assert.DoesNotContain("Directory.Build.props", project);
            Assert.Single(Regex.Matches(project, "<NoWarn>"));
        }

        // ---- helpers ----------------------------------------------------------------------

        /// <summary>
        /// The high DPI mode a WinForms <c>HighDpiMode</c> value corresponds to, for the manifest
        /// spellings a desktop app actually uses. Null for anything else, so an unrecognised value
        /// fails the agreement check rather than quietly passing as a match.
        /// </summary>
        private static string EquivalentHighDpiMode(string manifestDpiAware)
        {
            switch (manifestDpiAware)
            {
                case "true/pm": return "PerMonitor";
                case "true/px": return "PerMonitorV2";
                case "true": return "SystemAware";
                case "false": return "DpiUnaware";
                default: return null;
            }
        }

        private static string Describe(string highDpiMode)
        {
            if (highDpiMode == "PerMonitorV2") return "per-monitor v2";
            if (highDpiMode == "PerMonitor") return "per-monitor (v1)";
            return highDpiMode ?? "an unrecognised mode";
        }

        /// <summary>
        /// The value of the manifest's legacy dpiAware element, as written. Null when the element
        /// is not there, which is the failure the suppression's justification rests on.
        /// </summary>
        private static string ManifestDpiAwareValue(string manifest)
        {
            var match = Regex.Match(manifest,
                "<dpiAware\\b[^>]*>\\s*([^<]*?)\\s*</dpiAware>", RegexOptions.IgnoreCase);
            Assert.True(match.Success,
                "app.manifest has no <dpiAware> element. WFO0003 asks for exactly that, and this"
                + " build suppresses WFO0003 on the grounds that the element is the only thing"
                + " giving the process its DPI awareness - so if it has gone, the suppression is"
                + " wrong and the game is being DPI-virtualised.");

            return match.Groups[1].Value;
        }

        /// <summary>
        /// An XML/MSBuild file with its comments removed, so an assertion about what a file
        /// <em>says</em> cannot be satisfied - or broken - by what it explains.
        /// </summary>
        private static string WithoutComments(string file)
        {
            return Regex.Replace(file, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        }

        /// <summary>
        /// The XML comment immediately before a property, i.e. the justification a suppression is
        /// supposed to come with.
        /// </summary>
        private static string CommentPreceding(string project, string property)
        {
            var at = project.IndexOf("<" + property + ">", StringComparison.Ordinal);
            if (at < 0) return string.Empty;

            var before = project.Substring(0, at);
            var end = before.LastIndexOf("-->", StringComparison.Ordinal);
            if (end < 0) return string.Empty;

            var start = before.LastIndexOf("<!--", StringComparison.Ordinal);
            if (start < 0 || start > end) return string.Empty;

            return before.Substring(start, end + 3 - start);
        }

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

        private static string GameProject()
        {
            return Path.Combine(ProjectDirectory(), "Simitone.Windows.csproj");
        }

        private static string Manifest()
        {
            return Path.Combine(ProjectDirectory(), "app.manifest");
        }

        private static string ManifestText()
        {
            return File.ReadAllText(Manifest());
        }

        /// <summary>
        /// The game project's own folder, found by walking up out of the test output rather than by
        /// a fixed number of levels, so a change to the build layout does not silently turn these
        /// into "file not found" failures.
        /// </summary>
        private static string ProjectDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows.csproj"))) return dir.FullName;

                var nested = Path.Combine(dir.FullName, "Simitone.Windows");
                if (File.Exists(Path.Combine(nested, "Simitone.Windows.csproj"))) return nested;

                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                "Could not find Simitone.Windows.csproj above " + AppContext.BaseDirectory);
        }
    }
}
