using FSO.SimAntics.Engine;
using FSO.SimAntics.Primitives;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// Primitive 40, <c>online_jobs_call</c> - the <c>VM.TSOState.JobUI</c> null guard in
    /// <see cref="VMOnlineJobsCall"/>.
    ///
    /// <para>
    /// <c>VM.TSOState</c> is <c>PlatformState as VMTSOLotState</c> (<c>VM.cs</c>), and
    /// <c>VM.Init</c> puts a <c>VMTS1LotState</c> on a TS1 lot. So on any Unleashed save the
    /// property is <b>null</b>, and the primitive dereferenced it unconditionally - once to lazily
    /// create the job UI, and once more to read it - for all thirteen call modes. Any TS1 lot that
    /// reached opcode 40 took a <c>NullReferenceException</c> through the calling sim's whole
    /// thread, with no log line and no partial state.
    /// </para>
    ///
    /// <para>
    /// What is pinned here is the split. The primitive has two jobs that look related and are not:
    /// eight of the thirteen calls are pure VM state (temp registers, a global controller id, a
    /// random job offer) and have always worked headless, while four write into the job-status UI
    /// the TSO client renders. Refusing all thirteen would be a second, quieter bug - it would turn
    /// a crash into thirteen no-ops. <see cref="VMOnlineJobsCall.RequiresJobUI"/> names the four, and
    /// the tests below hold the partition to the enum's own contents so a new call mode cannot be
    /// added to the wrong side of it.
    /// </para>
    /// </summary>
    public sealed class VMOnlineJobsCallTests
    {
        /// <summary>
        /// The call modes that dereference the job UI. These are the four the primitive previously
        /// enumerated inline in its "lazily allocate" block, and the four whose bodies touch
        /// <c>jobui</c>.
        /// </summary>
        private static readonly VMOnlineJobsCallMode[] JobUIModes =
        {
            VMOnlineJobsCallMode.AddStatusMessage,
            VMOnlineJobsCallMode.RemoveStatusMessage,
            VMOnlineJobsCallMode.SetTimeRemaining,
            VMOnlineJobsCallMode.SetWorkMode,
        };

        [Fact]
        public void ExactlyTheFourUIBearingCallModesRequireTheJobUI()
        {
            foreach (var mode in JobUIModes)
            {
                Assert.True(VMOnlineJobsCall.RequiresJobUI(mode), $"{mode} reads or writes the job UI");
            }
        }

        [Fact]
        public void TheOtherCallsDoNotRequireTheJobUIAndSoStillRunOnATs1Lot()
        {
            // These are the calls that must keep working with no TSOState at all. GotoJobLot and
            // SetControllerID are VM state, GetRandomJob writes temps, IsJobAvailable reads one, and
            // AttemptToValidateWorker sets a person-data flag. Refusing any of them on a TS1 lot
            // would trade a crash for silent inaction.
            var headlessSafe = new[]
            {
                VMOnlineJobsCallMode.GotoJobLot,
                VMOnlineJobsCallMode.SetControllerID,
                VMOnlineJobsCallMode.AttemptToValidateWorker,
                VMOnlineJobsCallMode.GetRandomJob,
                VMOnlineJobsCallMode.IsJobAvailable,
            };

            foreach (var mode in headlessSafe)
            {
                Assert.False(VMOnlineJobsCall.RequiresJobUI(mode), $"{mode} must not need a TSOState");
            }
        }

        [Fact]
        public void ThePartitionCoversEveryDeclaredCallModeExactlyOnce()
        {
            // The enum has thirteen members and the two lists above hold nine of them. The remaining
            // four are SignalJobTypeChange, SignalJobGradeChange and the two Unused slots - none of
            // which has a case in the switch, so they are no-ops and need nothing. This asserts the
            // partition is total and disjoint rather than trusting the two lists to stay in sync.
            var declared = Enum.GetValues(typeof(VMOnlineJobsCallMode)).Cast<VMOnlineJobsCallMode>().ToArray();
            var ui = JobUIModes;
            var headless = new[]
            {
                VMOnlineJobsCallMode.GotoJobLot,
                VMOnlineJobsCallMode.SetControllerID,
                VMOnlineJobsCallMode.AttemptToValidateWorker,
                VMOnlineJobsCallMode.GetRandomJob,
                VMOnlineJobsCallMode.IsJobAvailable,
                VMOnlineJobsCallMode.SignalJobTypeChange,
                VMOnlineJobsCallMode.SignalJobGradeChange,
                VMOnlineJobsCallMode.Unused1,
                VMOnlineJobsCallMode.Unused2,
            };

            Assert.Equal(declared.Length, ui.Length + headless.Length);
            Assert.Empty(ui.Intersect(headless));
            Assert.Equal(declared.OrderBy(x => (int)x).ToArray(),
                ui.Concat(headless).OrderBy(x => (int)x).ToArray());
        }

        [Fact]
        public void TheUnusedCallModesAreNeverAskedForTheJobUI()
        {
            // `Unused1`/`Unused2` fall through the switch to `default: break`, so allocating a job
            // UI for them would be the engine inventing a TSO-only resource on a TS1 lot. They are
            // pinned to the "no" side explicitly because a future "just allocate it for everything"
            // refactor would otherwise be invisible here.
            Assert.False(VMOnlineJobsCall.RequiresJobUI(VMOnlineJobsCallMode.Unused1));
            Assert.False(VMOnlineJobsCall.RequiresJobUI(VMOnlineJobsCallMode.Unused2));
            Assert.False(VMOnlineJobsCall.RequiresJobUI(VMOnlineJobsCallMode.SignalJobTypeChange));
            Assert.False(VMOnlineJobsCall.RequiresJobUI(VMOnlineJobsCallMode.SignalJobGradeChange));
        }

        [Fact]
        public void TheOperandRoundTripsEveryCallModeThroughItsByte()
        {
            // The operand is a raw u8 off the instruction, so an unknown byte must survive as an
            // unknown mode and hit `default: break` - not throw, and not be mistaken for a call that
            // needs a job UI. This is the path the null guard has to keep quiet on.
            foreach (var mode in Enum.GetValues(typeof(VMOnlineJobsCallMode)).Cast<VMOnlineJobsCallMode>())
            {
                var bytes = new byte[1];
                new VMOnlineJobsCallOperand { Call = mode }.Write(bytes);
                Assert.Equal((byte)mode, bytes[0]);

                var read = new VMOnlineJobsCallOperand();
                read.Read(bytes);
                Assert.Equal(mode, read.Call);
            }

            var corrupt = new VMOnlineJobsCallOperand();
            corrupt.Read(new byte[] { 200 });
            Assert.Equal((VMOnlineJobsCallMode)200, corrupt.Call);
            Assert.False(VMOnlineJobsCall.RequiresJobUI(corrupt.Call));
        }
    }
}
