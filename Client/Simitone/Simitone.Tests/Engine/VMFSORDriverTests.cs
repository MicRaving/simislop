using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.NetPlay.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// E-15 · <c>VMFSORDriver</c> could not tell a truncated recording from a torn lot.
    ///
    /// <para>
    /// The driver had one <c>try</c> around the read, the command reordering and
    /// <c>InternalTick</c> together, and a bare <c>catch</c> over all of it, with the comment
    /// "might be incomplete, but that shouldn't crash the program". The comment is right and the
    /// shape hid the thing worth knowing. Those are two different events:
    /// </para>
    /// <list type="bullet">
    /// <item>the recording ended where a tick was expected - nothing was applied, the VM is exactly
    /// as it was, and playback stopping is the whole correct answer;</item>
    /// <item>a SimAntics exception came out of one of the tick's commands - <em>half the tick is
    /// now applied</em>, the VM is in a state neither the recording nor the player can explain, and
    /// playback carries on from the next tick as though nothing happened.</item>
    /// </list>
    ///
    /// <para>
    /// The rule is separated and named in <see cref="VMFSORDriver.PlayOnce"/>, and it takes its read
    /// and its apply as delegates precisely so a test process can stand in for both without a
    /// recording, a lot or a content manager. The shape of the method is asserted from the source
    /// as well, because the finding was about a <c>try</c> that covered too much - and a test that
    /// only checked the outcome would still pass if somebody widened the try again.
    /// </para>
    /// </summary>
    public sealed class VMFSORDriverTests
    {
        private static VMNetTick Tick(uint id) => new VMNetTick { TickID = id, Commands = new List<VMNetCommand>() };

        [Fact]
        public void ATickThatRunsIsApplied()
        {
            var applied = new List<uint>();
            var tick = Tick(41371);

            var attempt = VMFSORDriver.PlayOnce(() => tick, t => applied.Add(t.TickID));

            Assert.Equal(VMFSORDriver.PlaybackResult.Applied, attempt.Result);
            Assert.True(attempt.Applied);
            Assert.Equal(41371u, attempt.TickID);
            Assert.Equal(new uint[] { 41371 }, applied);
        }

        [Fact]
        public void ARecordingThatRanOutIsTruncatedAndTouchesNothing()
        {
            // The read throws: an EndOfStreamException from the reader, or a command count in the
            // file that runs past the end of it. Either way nothing has reached the VM, and the
            // driver is allowed to assume there are no more commands.
            var applyRan = false;

            var attempt = VMFSORDriver.PlayOnce(
                () => throw new EndOfStreamException("truncated"),
                _ => applyRan = true);

            Assert.Equal(VMFSORDriver.PlaybackResult.Truncated, attempt.Result);
            Assert.False(attempt.Applied);
            Assert.False(applyRan);
            // there was no tick to name
            Assert.Equal(0u, attempt.TickID);
        }

        [Fact]
        public void ATruncatedRecordingIsNotTheSameEventAsATickThatFailedToApply()
        {
            // This is the whole finding. Both used to end in the same catch, and the difference is
            // the difference between "the recording is over" and "the VM is half way through a tick".
            var truncated = VMFSORDriver.PlayOnce(() => throw new EndOfStreamException(), _ => { });
            var torn = VMFSORDriver.PlayOnce(() => Tick(900), _ => throw new InvalidOperationException("simantics"));

            Assert.NotEqual(truncated.Result, torn.Result);
            Assert.Equal(VMFSORDriver.PlaybackResult.Truncated, truncated.Result);
            Assert.Equal(VMFSORDriver.PlaybackResult.ApplyFailed, torn.Result);
        }

        [Fact]
        public void ATickThatThrowsPartWayThroughSaysWhichTickItFailedOn()
        {
            // The recording is the only thing that knows. Reporting "something went wrong" for a
            // torn VM is what let this stay invisible.
            var attempt = VMFSORDriver.PlayOnce(
                () => Tick(41371),
                _ => throw new VMSimanticsException("SimAntics Exception", null));

            Assert.Equal(VMFSORDriver.PlaybackResult.ApplyFailed, attempt.Result);
            Assert.Equal(41371u, attempt.TickID);
        }

        [Fact]
        public void ATickThatThrowsStillCountsAsNotAppliedSoTheLotKeepsTicking()
        {
            // The old behaviour: `success` stayed false, so the driver ran its empty fallback tick
            // and the lot carried on. That is kept - what changed is that the two failures are now
            // told apart, not that either one stops the lot.
            var attempt = VMFSORDriver.PlayOnce(() => Tick(1), _ => throw new Exception());

            Assert.False(attempt.Applied);
        }

        [Fact]
        public void AReadThatProducesNothingIsTruncatedRatherThanApplied()
        {
            var attempt = VMFSORDriver.PlayOnce(() => null, _ => throw new Exception("apply must not run"));

            Assert.Equal(VMFSORDriver.PlaybackResult.Truncated, attempt.Result);
        }

        [Fact]
        public void AReadIsMandatoryAndSayingSoIsNotATickFailure()
        {
            // A null read is a programming error in the driver, not a corrupt recording, so it is
            // allowed to throw out of the helper rather than being reported as a truncated file.
            Assert.Throws<ArgumentNullException>(() => VMFSORDriver.PlayOnce(null, _ => { }));
        }

        // ---- the shape of the method, which is the shape of the finding -------------------

        [Fact]
        public void TheReadAndTheApplyAreNotInOneTry()
        {
            var body = MethodBody(Source(), "public static PlaybackAttempt PlayOnce(Func<VMNetTick> read, Action<VMNetTick> apply)");

            // Two catches, and the first one closes before the apply is reached: reading is a pure
            // function of the file, so a failure there has changed nothing, while applying is not.
            var catches = Regex.Matches(body, @"catch\s*\(");
            Assert.Equal(2, catches.Count);

            var readTry = body.IndexOf("tick = read();", StringComparison.Ordinal);
            var readCatch = catches[0].Index;
            var applyTry = body.IndexOf("apply(tick);", StringComparison.Ordinal);
            var applyCatch = catches[1].Index;

            Assert.True(readTry >= 0 && readCatch > readTry, "the read is no longer wrapped.");
            Assert.True(applyTry > readCatch, "the apply is inside the read's try again, so a"
                + " half-applied tick is still reported as a truncated stream.");
            Assert.True(applyCatch > applyTry, "the apply is no longer wrapped.");
        }

        [Fact]
        public void ATickFailureIsTracedWithItsOwnId()
        {
            var body = MethodBody(Source(), "public static PlaybackAttempt PlayOnce(Func<VMNetTick> read, Action<VMNetTick> apply)");

            var trace = Regex.Match(body, @"catch\s*\(\s*Exception\s+\w+\s*\)");
            Assert.True(trace.Success, "the apply failure is swallowed again.");

            var window = body.Substring(trace.Index);
            Assert.Contains("tick.TickID", window);
            Assert.Contains("Debug.WriteLine", window);
        }

        [Fact]
        public void TheDriverAsksTheHelperAndFallsBackOnAnythingThatWasNotApplied()
        {
            var body = MethodBody(Source(), "public override bool Tick(VM vm)");

            Assert.Contains("PlayOnce(", body);
            Assert.Contains("if (attempt.Applied)", body);
            // the empty fallback tick, which is what kept the lot ticking when the recording ran out
            Assert.Contains("InternalTick(vm, new VMNetTick()", body);
        }

        [Fact]
        public void TheEndOfTheRecordingIsSaidOnceAndNotEveryTickAfterIt()
        {
            // A recording that runs out is the expected end of a playback, not an event, and this
            // driver is asked for a tick every frame - so a line per tick would be noise. A tick that
            // failed to apply has already reported itself, with its id, so it does not report twice.
            var body = MethodBody(Source(), "public override bool Tick(VM vm)");

            Assert.Contains("ReportedEndOfRecording", body);
            Assert.Contains("attempt.Result == PlaybackResult.Truncated && !ReportedEndOfRecording", body);
            Assert.Single(Regex.Matches(body, @"Debug\.WriteLine"));
        }

        [Fact]
        public void TheReorderingOfAStateSyncIsPartOfReadingAndNotOfApplying()
        {
            // A recorded tick with no command list at all is a corrupt recording, not a torn VM:
            // the reordering only touches the list the read produced, so nothing has reached the VM.
            var body = MethodBody(Source(), "private VMNetTick ReadTick()");

            Assert.Contains("tick.Commands == null", body);
            Assert.Contains("EndOfStreamException", body);
        }

        // ---- helpers ---------------------------------------------------------------------

        private static string Source()
        {
            var segments = new[] { FreeSoDir(), "FreeSO", "TSOClient", "tso.simantics", "NetPlay", "Drivers", "VMFSORDriver.cs" };
            var full = Path.Combine(segments);
            Assert.True(File.Exists(full), "could not find " + full);

            var text = File.ReadAllText(full);
            var withoutBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
        }

        private static string FreeSoDir()
        {
            var dir = new DirectoryInfo(ClientDir());
            for (var i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO", "TSOClient"))) return dir.FullName;
            }

            throw new DirectoryNotFoundException("Could not find FreeSO/TSOClient above " + ClientDir());
        }

        private static string ClientDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows", "Simitone.Windows.csproj"))) return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows.csproj"))) return dir.FullName;

                dir = dir.Parent;
            }

            throw new FileNotFoundException("Could not find Simitone.Windows.csproj above " + AppContext.BaseDirectory);
        }

        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "no '" + signature + "' in the source under test");

            var open = source.IndexOf('{', at);
            Assert.True(open >= 0, "'" + signature + "' has no body");

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }

            throw new InvalidOperationException("unbalanced braces after '" + signature + "'");
        }
    }
}
