using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FSO.Common;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The one hard invariant SimitoneLog promises: logging must never take the game down.
    /// Every public entry point swallows its own failures, so these tests are as much about
    /// "no exception ever escapes, for any input" as they are about the text in the file.
    ///
    /// Each test redirects <see cref="FSOEnvironment.UserDir"/> at a throwaway temp directory
    /// so nothing scribbles into the developer's real user dir, and so we can assert on what
    /// was actually written. The original value is restored in <see cref="Dispose"/>.
    ///
    /// <para>
    /// The collection attribute is not optional: <see cref="SimitoneLog"/> resolves its path
    /// from that same global, so any other test whose code under test logs would write into
    /// whichever temp directory happens to be installed at the time, and the line counts
    /// asserted here would race it. See <see cref="NonParallelCollection"/>.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class SimitoneLogTests : IDisposable
    {
        private readonly string _originalUserDir;
        private readonly string _tempRoot;

        /// <summary>Matches the timestamp + "[LEVEL] " prefix produced by SimitoneLog.Format.</summary>
        private static readonly Regex LinePrefix = new Regex(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(INFO|WARN|ERROR)\] ",
            RegexOptions.Compiled);

        public SimitoneLogTests()
        {
            _originalUserDir = FSOEnvironment.UserDir;
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-log-tests-" + Guid.NewGuid().ToString("N"));
            FSOEnvironment.UserDir = _tempRoot;
        }

        public void Dispose()
        {
            FSOEnvironment.UserDir = _originalUserDir;
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
            }
            catch (IOException)
            {
                //best effort - a leftover temp dir must never fail the test run
            }
        }

        private string LogFile => Path.Combine(_tempRoot, "logs", "simitone.log");

        private string RotatedFile => LogFile + ".1";

        // ------------------------------------------------------------------ test data

        /// <summary>
        /// Message shapes that a logger is expected to survive. Referenced by name rather than
        /// by value so the very large ones are not baked into the test-case discovery data.
        /// </summary>
        public static IEnumerable<object[]> LevelsAndMessageKinds()
        {
            var kinds = new[] { "normal", "null", "empty", "whitespace", "newlines", "unicode", "verylong" };
            foreach (var level in new[] { "Info", "Warn", "Error" })
            {
                foreach (var kind in kinds)
                {
                    yield return new object[] { level, kind };
                }
            }
        }

        private static string Message(string kind)
        {
            switch (kind)
            {
                case "normal": return "a perfectly ordinary message";
                case "null": return null;
                case "empty": return string.Empty;
                case "whitespace": return "   \t  ";
                case "newlines": return "line one\nline two\r\nline three";
                case "unicode": return "unicode: § ß Ω 日本語";
                case "verylong": return new string('x', 100_000); // ~100 KB, past any sane line length
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown message kind");
            }
        }

        private static void Log(string level, string message)
        {
            switch (level)
            {
                case "Info": SimitoneLog.Info(message); break;
                case "Warn": SimitoneLog.Warn(message); break;
                case "Error": SimitoneLog.Error(message); break;
                default: throw new ArgumentOutOfRangeException(nameof(level), level, "unknown level");
            }
        }

        private static void Log(string level, string message, Exception ex)
        {
            switch (level)
            {
                case "Info": SimitoneLog.Info(message, ex); break;
                case "Warn": SimitoneLog.Warn(message, ex); break;
                case "Error": SimitoneLog.Error(message, ex); break;
                default: throw new ArgumentOutOfRangeException(nameof(level), level, "unknown level");
            }
        }

        // ------------------------------------------------------------------ LogPath

        [Fact]
        public void LogPath_ResolvesUnderUserDir_WithoutTouchingContent()
        {
            Assert.Equal(LogFile, SimitoneLog.LogPath);
            Assert.False(File.Exists(LogFile)); // resolving the path must not create it
        }

        [Fact]
        public void LogPath_FallsBackToARelativePath_WhenUserDirCannotBeResolved()
        {
            FSOEnvironment.UserDir = null;

            string path = null;
            var thrown = Record.Exception(() => path = SimitoneLog.LogPath);

            Assert.Null(thrown);
            Assert.Equal(Path.Combine("logs", "simitone.log"), path);
        }

        // ------------------------------------------------------------ "never throws"

        [Theory]
        [MemberData(nameof(LevelsAndMessageKinds))]
        public void SeverityMethods_NeverThrow_ForAnyMessage(string level, string kind)
        {
            var thrown = Record.Exception(() => Log(level, Message(kind)));

            Assert.Null(thrown);
        }

        [Theory]
        [MemberData(nameof(LevelsAndMessageKinds))]
        public void SeverityMethods_NeverThrow_ForAnyMessage_WhenGivenAnException(string level, string kind)
        {
            var ex = new InvalidOperationException("a real, constructed exception");

            var thrown = Record.Exception(() => Log(level, Message(kind), ex));

            Assert.Null(thrown);
        }

        [Theory]
        [InlineData("Info")]
        [InlineData("Warn")]
        [InlineData("Error")]
        public void SeverityMethods_AcceptAnExplicitNullException(string level)
        {
            var thrown = Record.Exception(() => Log(level, "message with an explicit null exception", null));

            Assert.Null(thrown);
            var expected = $"[{level.ToUpperInvariant()}] message with an explicit null exception";
            Assert.Contains(expected, ReadAllLines().Single(), StringComparison.Ordinal);
        }

        // ----------------------------------------------------------- what gets written

        [Fact]
        public void EverySeverity_WritesOneTimestampedLineTaggedWithItsLevel()
        {
            SimitoneLog.Info("info message");
            SimitoneLog.Warn("warn message");
            SimitoneLog.Error("error message");

            var lines = ReadAllLines();
            Assert.Equal(3, lines.Count);
            Assert.Contains("[INFO] info message", lines[0], StringComparison.Ordinal);
            Assert.Contains("[WARN] warn message", lines[1], StringComparison.Ordinal);
            Assert.Contains("[ERROR] error message", lines[2], StringComparison.Ordinal);

            foreach (var line in lines)
            {
                Assert.Matches(LinePrefix, line);
            }
        }

        [Fact]
        public void NullOrEmptyMessage_StillProducesAWellFormedLine()
        {
            SimitoneLog.Info(null);
            SimitoneLog.Warn(string.Empty);

            var lines = ReadAllLines();
            Assert.Equal(2, lines.Count);
            // "message ?? string.Empty" - no "<null>" ever leaks into the file
            Assert.Matches(LinePrefix, lines[0]);
            Assert.EndsWith("] ", lines[0], StringComparison.Ordinal);
            Assert.Matches(LinePrefix, lines[1]);
            Assert.EndsWith("] ", lines[1], StringComparison.Ordinal);
        }

        [Fact]
        public void Exception_AppendsTypeAndMessage_OntoTheSameLine()
        {
            SimitoneLog.Error("save failed", new InvalidOperationException("disk is on fire"));

            var line = Assert.Single(ReadAllLines());
            Assert.Contains("[ERROR] save failed | InvalidOperationException: disk is on fire", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Exception_WithAStackTrace_IsFollowedByTheTraceOnItsOwnLine()
        {
            // thrown and caught so that StackTrace is genuinely populated
            Exception caught;
            try
            {
                throw new NotSupportedException("nope");
            }
            catch (Exception e)
            {
                caught = e;
            }

            Assert.NotNull(caught.StackTrace);

            SimitoneLog.Warn("caught something", caught);

            var raw = File.ReadAllText(LogFile);
            Assert.Contains("| NotSupportedException: nope" + Environment.NewLine + caught.StackTrace, raw, StringComparison.Ordinal);
        }

        [Fact]
        public void EmbeddedNewlinesInAMessage_AreWrittenThroughVerbatim()
        {
            SimitoneLog.Info("first\nsecond");

            var raw = File.ReadAllText(LogFile);
            // The message body is appended verbatim - no escaping, no truncation - and the only
            // trailing terminator is the one StreamWriter.WriteLine adds.
            Assert.EndsWith("] first\nsecond" + Environment.NewLine, raw, StringComparison.Ordinal);
        }

        [Fact]
        public void AMultiHundredKilobyteMessage_IsWrittenInFull()
        {
            var message = new string('x', 100_000);

            SimitoneLog.Info(message);

            var line = Assert.Single(ReadAllLines());
            Assert.Equal(message, line.Substring(LinePrefix.Match(line).Length));
        }

        // ----------------------------------------------------------------- rotation

        [Fact]
        public void Log_IsRotatedOnceItReachesTwoMebibytes()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
            using (var oversize = new FileStream(LogFile, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                oversize.SetLength(2 * 1024 * 1024);
            }

            SimitoneLog.Info("after the rollover");

            Assert.True(File.Exists(RotatedFile), "the oversized log should have been shifted to simitone.log.1");
            Assert.Equal(2L * 1024 * 1024, new FileInfo(RotatedFile).Length);

            // only one generation is kept, and the fresh log starts clean
            var line = Assert.Single(ReadAllLines());
            Assert.Contains("[INFO] after the rollover", line, StringComparison.Ordinal);
        }

        [Fact]
        public void ALogUnderTheRolloverThreshold_IsAppendedToRatherThanRotated()
        {
            SimitoneLog.Info("first entry");
            SimitoneLog.Info("second entry");

            Assert.False(File.Exists(RotatedFile));
            Assert.Equal(2, ReadAllLines().Count);
        }

        // --------------------------------------------------------------- concurrency

        [Fact]
        public void ConcurrentWrites_EachProduceExactlyOneWholeLine()
        {
            const int writers = 8;
            const int perWriter = 25;

            var tasks = new List<Task>();
            for (int w = 0; w < writers; w++)
            {
                int id = w;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < perWriter; i++)
                    {
                        SimitoneLog.Info($"writer-{id} entry-{i}");
                    }
                }));
            }
            Task.WaitAll(tasks.ToArray());

            var lines = ReadAllLines();
            Assert.Equal(writers * perWriter, lines.Count);

            // The write lock means one call is never interleaved with another, so every line
            // must carry exactly one intact writer/entry tag and no duplicates.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                Assert.Matches(LinePrefix, line);
                var match = Regex.Match(line, @"\[INFO\] (writer-\d+ entry-\d+)$");
                Assert.True(match.Success, "interleaved or truncated line: " + line);
                Assert.True(seen.Add(match.Groups[1].Value), "duplicate entry: " + match.Groups[1].Value);
            }
        }

        // -------------------------------------------------------------------- helpers

        private List<string> ReadAllLines()
        {
            Assert.True(File.Exists(LogFile), "expected the log to have been written at " + LogFile);
            var raw = File.ReadAllText(LogFile);
            var lines = raw.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).ToList();
            Assert.NotEmpty(lines);
            return lines;
        }
    }
}
