using System;
using System.Collections.Generic;
using System.IO;
using FSO.Common;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <c>IniConfig.Save</c> writes a temp file and swaps it in, skips read-only properties
    /// (<c>ActivePath</c>), and still loads the old <c>Key=Value</c> format.
    /// </summary>
    public sealed class IniConfigSaveTests : IDisposable
    {
        private sealed class TestConfig : IniConfig
        {
            public TestConfig(string path) : base(path) { }
            public override string HeadingComment => "test";
            public override Dictionary<string, string> DefaultValues { get; set; } =
                new Dictionary<string, string> { { "Width", "800" }, { "Name", "x" } };
            public int Width { get; set; }
            public string Name { get; set; }
            public string Computed => "never-written";
        }

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "simitone-ini-" + Guid.NewGuid().ToString("N"));

        public IniConfigSaveTests() { Directory.CreateDirectory(_dir); }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Fact]
        public void SaveRoundTripsWritableKeysOnlyAndLeavesNoTempFile()
        {
            var path = Path.Combine(_dir, "config.ini");
            var cfg = new TestConfig(path);
            cfg.Width = 1234;
            cfg.Save();

            var text = File.ReadAllText(path);
            Assert.Contains("Width=1234", text);
            Assert.DoesNotContain("ActivePath", text);
            Assert.DoesNotContain("Computed", text);
            Assert.False(File.Exists(path + ".tmp"));

            Assert.Equal(1234, new TestConfig(path).Width);
        }

        [Fact]
        public void LoadStillReadsTheOldFormatAndASecondSaveReplacesTheFile()
        {
            var path = Path.Combine(_dir, "old.ini");
            File.WriteAllText(path, "# old\nWidth=640\nName=legacy\nActivePath=C:\\ignored\n");

            var cfg = new TestConfig(path);
            Assert.Equal(640, cfg.Width);
            Assert.Equal("legacy", cfg.Name);
            Assert.Equal(path, cfg.ActivePath);

            cfg.Width = 5;
            cfg.Save();
            Assert.Equal(5, new TestConfig(path).Width);
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void AFailedSaveDoesNotThrow()
        {
            var cfg = new TestConfig(Path.Combine(_dir, "missing-dir", "config.ini"));
            cfg.Save();
        }
    }
}
