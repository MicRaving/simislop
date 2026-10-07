using System;
using System.IO;
using System.Linq;
using FSO.Common;
using FSO.SimAntics.NetPlay.EODs.Utils;
using FSO.SimAntics.NetPlay.Model.Commands;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The log lines the EOD framework writes for itself - <see cref="VMEODDiagnostics"/>.
    ///
    /// <para>
    /// These exist because of a specific failure mode rather than for tidiness.
    /// <c>VMNetEODMessageCmd.Verify</c> delivers a panel message inside
    /// <c>try { vm.EODHost.Deliver(this, caller); } catch (Exception) { }</c>, with a
    /// <c>// some future NLog statement</c> where the log line was always going to go. That empty
    /// catch is why the TS1 owner-mode bug was invisible: thirty-three owner checks dereferenced a
    /// platform state a TS1 lot does not have, every one of those messages was dropped, and the
    /// only symptom was a panel that never opened.
    /// </para>
    ///
    /// <para>
    /// The catch has to stay - a malformed message from a modified client must not be able to take
    /// the message loop down - so what is tested here is the half that was missing: that a dropped
    /// message is recorded, that it names the plugin and the event so it can be acted on, and that
    /// it is recorded once rather than once per attempt.
    /// </para>
    ///
    /// <para>
    /// The tests redirect <see cref="FSOEnvironment.UserDir"/> at a throwaway directory so nothing
    /// scribbles into a developer's real user dir, and so the lines can be read back. The dedupe
    /// set is process-global and cannot be reset, so every test uses a key of its own; a shared key
    /// would make the result depend on which test ran first. The collection attribute is not
    /// optional for the same reason it is not optional on <c>SimitoneLogTests</c>: this class
    /// writes the same file that class counts lines in. See <c>NonParallelCollection</c>.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class VMEODDiagnosticsTests : IDisposable
    {
        private readonly string _originalUserDir;
        private readonly string _tempRoot;

        public VMEODDiagnosticsTests()
        {
            _originalUserDir = FSOEnvironment.UserDir;
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-eod-diagnostics-" + Guid.NewGuid().ToString("N"));
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

        private string[] Lines()
        {
            if (!File.Exists(LogFile)) return new string[0];
            return File.ReadAllLines(LogFile).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        }

        /// <summary>A key no other test can have used, because nothing else knows this one.</summary>
        private static string Key(string name)
        {
            return "test-eod-diagnostics/" + name + "/" + Guid.NewGuid().ToString("N");
        }

        [Fact]
        public void AWarningIsWrittenOnceAndNotRepeatedForTheSameKey()
        {
            var key = Key("once");

            // The plugins call this on every owner check, which is every message the panel sends.
            // Without the dedupe a table opened and closed all evening writes the line all evening,
            // and a log that is mostly one repeated sentence is a log nobody reads.
            Assert.True(VMEODDiagnostics.WarnOnce(key, "first"), "the first call is the one that writes");
            Assert.False(VMEODDiagnostics.WarnOnce(key, "first"), "a repeat must report that it did nothing");
            Assert.False(VMEODDiagnostics.WarnOnce(key, "first"), "and stay that way");

            var lines = Lines();
            Assert.Single(lines);
            Assert.Contains("first", lines[0]);
        }

        [Fact]
        public void TwoDifferentKeysAreTwoDifferentLines()
        {
            // The counterpart to the test above, and the one that matters for owner mode: the
            // refusal is keyed per plugin, so a player who opens a roulette table and then a
            // blackjack table gets a line naming each rather than one line naming whichever came
            // first.
            Assert.True(VMEODDiagnostics.WarnOnce(Key("blackjack"), "blackjack has no owner mode"));
            Assert.True(VMEODDiagnostics.WarnOnce(Key("roulette"), "roulette has no owner mode"));

            var lines = Lines();
            Assert.Equal(2, lines.Length);
            Assert.Contains(lines, x => x.Contains("blackjack"));
            Assert.Contains(lines, x => x.Contains("roulette"));
        }

        [Fact]
        public void AWarningLandsInTheLogAPlayerWouldSend()
        {
            VMEODDiagnostics.WarnOnce(Key("destination"), "a recognizable sentence");

            // The engine has no logger of its own. Appending to <UserDir>/logs/simitone.log is the
            // whole point: that is the file the game already writes and the one a bug report would
            // carry. A second log of the EOD framework's own would be a file nobody knows to look
            // in, which is the situation this class exists to end.
            var line = Assert.Single(Lines());
            Assert.Contains("[EOD]", line);
            Assert.Contains("a recognizable sentence", line);
            Assert.Contains("[WARN]", line);
        }

        [Fact]
        public void AWarningWithANullKeyStillDoesNotThrow()
        {
            // Callers build keys from ids, but a null key must not be the reason a panel stops
            // working. "No key" collapses to a single shared key, which means it is logged once -
            // the safe direction for a line that should not exist anyway.
            VMEODDiagnostics.WarnOnce(null, "keyless warning");

            Assert.Single(Lines());
        }

        [Fact]
        public void ADroppedMessageNamesThePluginAndTheEvent()
        {
            // The line has to be actionable on its own. "something went wrong" in a log of
            // thousands is not; "plugin 2B2FC514 dropped blackjack_owner_show" is a plugin id from
            // VMEODServer.IDToHandler and an event name from the panel's handler table, which is
            // everything needed to find the throw.
            VMEODDiagnostics.ReportDroppedMessage(
                new VMNetEODMessageCmd
                {
                    PluginID = 0x2B2FC514,
                    EventName = "blackjack_owner_show",
                    ActorUID = 1234,
                },
                new InvalidOperationException("Object reference not set to an instance of an object."));

            var line = Assert.Single(Lines());
            Assert.Contains("2B2FC514", line);
            Assert.Contains("blackjack_owner_show", line);
            Assert.Contains("1234", line);
            Assert.Contains("InvalidOperationException", line);
        }

        [Fact]
        public void ADroppedMessageIsRecordedOncePerPluginAndEvent()
        {
            var cmd = new VMNetEODMessageCmd { PluginID = 0x0B2A6B83, EventName = "roulette_manage" };
            var other = new VMNetEODMessageCmd { PluginID = 0x0B2A6B83, EventName = "roulette_min_bet" };

            VMEODDiagnostics.ReportDroppedMessage(cmd, new Exception("boom"));
            VMEODDiagnostics.ReportDroppedMessage(cmd, new Exception("boom"));
            VMEODDiagnostics.ReportDroppedMessage(other, new Exception("boom"));

            // Three attempts across two distinct messages, two lines: a panel retrying one
            // message is one thing that went wrong, and two different events are two.
            Assert.Equal(2, Lines().Length);
        }

        [Fact]
        public void ADroppedMessageIsStillRecordedWithoutAnExceptionToName()
        {
            // The catch is the only thing that knows what went wrong, so an entry with no
            // exception behind it should be unusual - but "unusual" is not a reason to throw.
            VMEODDiagnostics.ReportDroppedMessage(
                new VMNetEODMessageCmd { PluginID = 0xCB2819CB, EventName = "slots_owner_init" },
                null);

            var line = Assert.Single(Lines());
            Assert.Contains("slots_owner_init", line);
            Assert.Contains("no exception recorded", line);
        }

        [Fact]
        public void ReportingIsHarmlessForAMessageThatIsNotThere()
        {
            VMEODDiagnostics.ReportDroppedMessage(null, new Exception("boom"));

            Assert.Empty(Lines());
        }

        [Fact]
        public void ALogThatCannotBeWrittenNeverBecomesTheReasonTheEodStopsWorking()
        {
            // A player with a read-only user dir, a full disk, or a UserDir that has been pointed
            // at a file. Pointing it at a file is reproducible and makes every step of the write
            // fail: the parent cannot be created because the path is already a file.
            var blocker = Path.Combine(_tempRoot, "not-a-directory");
            Directory.CreateDirectory(_tempRoot);
            File.WriteAllText(blocker, "this is a file, not a folder");
            FSOEnvironment.UserDir = blocker;

            // The one invariant with no exceptions to it.
            VMEODDiagnostics.WarnOnce(Key("unwritable"), "nobody will ever read this");
            VMEODDiagnostics.ReportDroppedMessage(
                new VMNetEODMessageCmd { PluginID = 1, EventName = "eod_enter" }, new Exception("boom"));

            Assert.Empty(Lines());
        }
    }
}
