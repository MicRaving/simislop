using FSO.Content.Utils;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMPieRefusal"/> - the one line per refused interaction that says which of the
    /// decisions said no.
    ///
    /// <para>
    /// A pie menu is rebuilt on every mouse move over an object that has one, so the diagnostic
    /// has to be throttled or it writes a line per frame for as long as the cursor rests there.
    /// What it must not do is go quiet for so long that a <em>new</em> reason for the same
    /// interaction is swallowed - so the key is the triple (object, interaction, reason) and the
    /// window only collapses exact repeats.
    /// </para>
    ///
    /// <para>
    /// Non-parallel because it installs a <see cref="ContentLog.Sink"/> and a
    /// <see cref="VMPieRefusal.Clock"/>, and both are process-wide: another test that logs while
    /// this one's sink is installed would write into the list these tests count.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class VMPieRefusalTests : IDisposable
    {
        private readonly List<string> Lines = new List<string>();
        private readonly Action<ContentLogLevel, string, Exception> PriorSink;
        private readonly Func<DateTime> PriorClock;
        private DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0);

        public VMPieRefusalTests()
        {
            PriorSink = ContentLog.Sink;
            PriorClock = VMPieRefusal.Clock;
            ContentLog.Sink = (level, message, ex) => Lines.Add(message);
            VMPieRefusal.Clock = () => Now;
            VMPieRefusal.Reset();
        }

        public void Dispose()
        {
            ContentLog.Sink = PriorSink;
            VMPieRefusal.Clock = PriorClock;
            VMPieRefusal.Reset();
        }

        private static string Line(VMPieRefusalKind kind)
        {
            return VMPieRefusal.Describe(0x15866694, 0, kind, "Gender=0x0001 (isPet=false)");
        }

        // ---- the line says enough to act on -----------------------------------------------

        [Fact]
        public void TheLineNamesTheObjectTheInteractionAndTheReason()
        {
            // The three things the old report could not say. The object's GUID rather than its
            // object id, because the object id changes every lot load and the GUID does not - it
            // is what makes the line searchable against the retail install.
            Assert.Equal("pie menu refuse: 0x15866694 id=0: caller is a pet and the interaction "
                + "is not marked for pets (Gender=0x0001 (isPet=false))",
                Line(VMPieRefusalKind.IsPet));
        }

        [Theory]
        [InlineData(VMPieRefusalKind.NoTreeTableRow, "no InteractionByIndex entry")]
        [InlineData(VMPieRefusalKind.ActionRoutineUnresolvable, "action routine not resolvable")]
        [InlineData(VMPieRefusalKind.CheckRoutineUnresolvable, "check tree not resolvable")]
        [InlineData(VMPieRefusalKind.IsPet, "caller is a pet")]
        [InlineData(VMPieRefusalKind.PetKindMismatch, "pet-only interaction")]
        [InlineData(VMPieRefusalKind.AgeFlags, "age flags")]
        [InlineData(VMPieRefusalKind.NotVisitors, "visitors")]
        [InlineData(VMPieRefusalKind.CheckTreeFalse, "check tree returned false")]
        [InlineData(VMPieRefusalKind.CallerHidden, "caller is hidden")]
        [InlineData(VMPieRefusalKind.CallerOutOfWorld, "caller is off the lot")]
        [InlineData(VMPieRefusalKind.DebugInteraction, "debug interaction")]
        public void EveryReasonHasWordsOfItsOwn(VMPieRefusalKind kind, string expected)
        {
            // "none" is the one that must never appear in a line, and every other kind has to be
            // distinguishable from all the others - a log full of "refused" is what we are fixing.
            var line = Line(kind);
            Assert.StartsWith(VMPieRefusal.LinePrefix, line);
            Assert.Contains(expected, line);
            Assert.DoesNotContain("none", line);
        }

        [Fact]
        public void AReasonWithNoDetailIsStillOneReadableLine()
        {
            // Not every decision needs a detail - a missing tree table has nothing to add - and a
            // trailing bracket pair reads as a bug.
            var line = VMPieRefusal.Describe(0x15866694, 2, VMPieRefusalKind.NoTreeTableRow, null);

            Assert.Equal("pie menu refuse: 0x15866694 id=2: GetAction null "
                + "(no InteractionByIndex entry)", line);
        }

        [Fact]
        public void NoRefusalIsNotARefusal()
        {
            Assert.False(VMPieRefusal.Report(0x15866694, 0, VMPieRefusalKind.None, null));
            Assert.Empty(Lines);
        }

        // ---- the throttle ------------------------------------------------------------------

        [Fact]
        public void TheSameRefusalIsWrittenOnceAndThenQuiet()
        {
            // The whole reason the throttle exists: a menu is rebuilt per mouse move, and the
            // player who leaves the cursor on a food object would otherwise fill the log.
            Assert.True(VMPieRefusal.Report(0x15866694, 0, VMPieRefusalKind.CheckTreeFalse, "d"));

            for (int i = 0; i < 500; i++)
            {
                Assert.False(VMPieRefusal.Report(0x15866694, 0, VMPieRefusalKind.CheckTreeFalse, "d"));
            }

            Assert.Single(Lines);
        }

        [Fact]
        public void ADifferentReasonForTheSameInteractionIsNotSwallowed()
        {
            // The other half of the throttle's contract. If the reason is part of the key, a
            // player who is told "check tree returned false" and then hits "caller is a pet" gets
            // told both, without waiting out the window.
            Assert.True(VMPieRefusal.Report(0x15866694, 0, VMPieRefusalKind.CheckTreeFalse, null));
            Assert.True(VMPieRefusal.Report(0x15866694, 0, VMPieRefusalKind.IsPet, null));

            Assert.Equal(2, Lines.Count);
        }

        [Fact]
        public void EachInteractionOfEachObjectIsCountedSeparately()
        {
            // Three interactions refusing for three different reasons is three lines, not one.
            for (uint id = 0; id < 3; id++)
            {
                Assert.True(VMPieRefusal.Report(0x15866694, id, VMPieRefusalKind.CheckTreeFalse, null));
            }
            Assert.True(VMPieRefusal.Report(0x543BE3FE, 1, VMPieRefusalKind.IsPet, null));

            Assert.Equal(4, Lines.Count);
        }

        [Fact]
        public void TheSameRefusalComesBackAfterTheWindow()
        {
            // Otherwise "throttled" becomes "reported once per process", which loses the second
            // time a player hits the same wall an hour later - the case a bug report is made of.
            Assert.True(VMPieRefusal.Report(0x15866694, 0, VMPieRefusalKind.IsPet, null));

            Now = Now.AddSeconds(VMPieRefusal.RepeatWindowSeconds - 1);
            Assert.False(VMPieRefusal.Report(0x15866694, 0, VMPieRefusalKind.IsPet, null));

            Now = Now.AddSeconds(2);
            Assert.True(VMPieRefusal.Report(0x15866694, 0, VMPieRefusalKind.IsPet, null));

            Assert.Equal(2, Lines.Count);
        }

        [Fact]
        public void EveryLineCarriesThePrefixSoOneSearchFindsThem()
        {
            foreach (var kind in Enum.GetValues(typeof(VMPieRefusalKind)).Cast<VMPieRefusalKind>())
            {
                if (kind == VMPieRefusalKind.None) continue;
                Assert.StartsWith(VMPieRefusal.LinePrefix, VMPieRefusal.Describe(1, 1, kind, null));
            }
        }

        [Fact]
        public void TheTwoStepFormIsTheOneStepForm()
        {
            // VMEntity uses ShouldReport/Write rather than Report so it can skip building the detail
            // half on the frames the throttle drops. That is only the same behaviour if it is.
            Assert.True(VMPieRefusal.ShouldReport(0x15866694, 0, VMPieRefusalKind.IsPet));
            Assert.False(VMPieRefusal.ShouldReport(0x15866694, 0, VMPieRefusalKind.IsPet));

            VMPieRefusal.Write(0x15866694, 0, VMPieRefusalKind.IsPet, "Gender=0x0001");
            Assert.Single(Lines);

            // and a different interaction on the same object is still separate
            Assert.True(VMPieRefusal.ShouldReport(0x15866694, 1, VMPieRefusalKind.IsPet));
            Assert.False(VMPieRefusal.ShouldReport(0x15866694, 1, VMPieRefusalKind.IsPet));
        }

        [Fact]
        public void ANonRefusalIsNeverDueToBeWritten()
        {
            // Both halves, so a caller that does not test for None itself still gets nothing out.
            Assert.False(VMPieRefusal.ShouldReport(1, 1, VMPieRefusalKind.None));

            VMPieRefusal.Write(1, 1, VMPieRefusalKind.None, null);
            Assert.Empty(Lines);
        }
    }
}
