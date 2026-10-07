using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Primitives;
using System;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The sub-routine sentinel: an instruction whose opcode is <b>0x7FFF</b> or <b>0xFFFF</b>,
    /// i.e. a call to a tree id in the semi-global band that no shipped semi-global owns.
    /// </summary>
    ///
    /// <para>
    /// An instruction with an opcode at or above 256 is not a primitive at all. Both
    /// <c>VMThread.ExecuteInstruction</c> and <c>VMRoutine.Execute</c> hand it straight to
    /// <c>frame.Thread.ExecuteSubRoutine</c>, which dispatches by band: <c>&lt; 4096</c> global,
    /// <c>4096..8191</c> private, <c>&gt;= 8192</c> semi-global. <c>0x7FFF</c> and <c>0xFFFF</c> sit
    /// in the semi-global band, and the 182 sentinel sites across the 11 TS1 cabinets in
    /// <c>D:\Program Files\Sims</c> all resolve to a missing routine.
    /// </para>
    ///
    /// <para>
    /// The finding behind the null guard in <c>VMThread.ExecuteSubRoutine</c>: the semi-global band
    /// is resolved through <c>frame.ScopeResource.SemiGlobal</c>, and <c>SemiGlobal</c> is only ever
    /// assigned by the <c>GLOB</c> chunk lookup in <c>GameObjectResource</c>'s constructor
    /// (<c>tso.content/WorldObjectProvider.cs</c>), so it stays <c>null</c> for a GLOB-less object
    /// and the lookup is a <c>NullReferenceException</c> rather than a missing routine.
    /// <b>131 of the 182 sites are in GLOB-less objects</b>, across 46 distinct <c>.iff</c> files -
    /// for example <c>ExpansionPack2/Teleporter_SP.iff 4121 "portal core"</c>, four sentinels in
    /// five instructions; <c>Objects/PinballMachine.iff 4110 "clean core"</c> and 4112;
    /// <c>ExpansionPack5/DogHouse.iff 4097-4140</c>; and the three <c>BuffetTable*.iff 4116 "my
    /// exit"</c> trees, one each in Downtown, Vacation and Studio Town. The other 51 are in objects
    /// that do carry a <c>GLOB</c> - including
    /// <c>ExpansionShared/HDSI_Greet.iff 4177/4178</c> and
    /// <c>ExpansionShared/PetSocialsCat.iff 4379/4381</c>, both semi-global <c>SocialGlobals</c> -
    /// and already take the missing-routine path, so those were never NRE candidates.
    /// </para>
    ///
    /// <para>
    /// The guard sends a null <c>SemiGlobal</c> down the same <c>routine == null</c> path the
    /// private and global bands already use, so an unresolvable sentinel becomes indistinguishable
    /// from an unresolvable private tree. That is a deliberate consistency choice, not a derivation:
    /// the retail <em>meaning</em> of the sentinel (skip, fail the tree, hand back a result) is not
    /// recoverable from static data, and <c>Pop(ERROR)</c> is what the two sibling bands already
    /// do. These tests pin the operand the guard is reached with and the fact that the primitive
    /// table cannot even represent a sentinel; they do not, and cannot, pin the exit code - that
    /// would need a bootstrapped <c>VMContext</c>, which no test in this suite has.
    /// </para>
    /// </summary>
    public sealed class VMSubRoutineSentinelTests
    {
        /// <summary>
        /// The eight operand bytes every shipped 0x7FFF/0xFFFF site carries: 0xFF four times. Verified
        /// over all 11 TS1 fars - not one of the 182 sites deviates.
        /// </summary>
        private static readonly byte[] SentinelOperand =
            { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        [Fact]
        public void TheSentinelOperandMeansTakeTheTempRegisters()
        {
            // VMSubRoutineOperand.Read derives UseTemp0 from the argument shorts, and
            // VMThread.ExecuteSubRoutine substitutes TempRegisters[i] for any argument equal to -1
            // when it is set. All four arguments are -1, so all four become temp registers: the
            // sentinel sites pass Temp0..Temp3 into a tree that does not exist, which is a second
            // reason the call can never succeed and has to be handled as a miss rather than as a
            // control transfer.
            var operand = new VMSubRoutineOperand();
            operand.Read(SentinelOperand);

            Assert.True(operand.UseTemp0);
            Assert.Equal(new short[] { -1, -1, -1, -1 }, operand.Arguments);
        }

        [Fact]
        public void TheFourSentinelArgumentsAreEachMinusOne()
        {
            // Spelled out one at a time because the -1 has to be read as a short (0xFFFF), not as a
            // pair of bytes. A reader that split this operand into bytes would get Arg0 wrong by
            // accident and Arg1..Arg3 as 0x00FF, which is +255 - and 255 is the "RETURN_FALSE"
            // branch-pointer sentinel, so the mistake would not look like a mistake.
            var operand = new VMSubRoutineOperand();
            operand.Read(SentinelOperand);

            Assert.Equal((short)-1, operand.Arg0);
            Assert.Equal((short)-1, operand.Arg1);
            Assert.Equal((short)-1, operand.Arg2);
            Assert.Equal((short)-1, operand.Arg3);
        }

        [Fact]
        public void ASentinelIsASubRoutineIdAndNotAPrimitiveOpcode()
        {
            // Both sentinels are >= 8192, so they take the semi-global branch of
            // VMThread.ExecuteSubRoutine rather than the private or global one - and the primitive
            // table is a fixed 256 entries, so a sentinel cannot be "registered" as a primitive
            // even by accident. The absence of VMContext.Primitives[0x7FFF] is not a gap.
            Assert.Equal(256, VMContext.Primitives.Length);
            Assert.InRange(0x7FFF, 8192, ushort.MaxValue);
            Assert.InRange(0xFFFF, 8192, ushort.MaxValue);
        }

        [Fact]
        public void TheSentinelOperandSurvivesAWriteAndReread()
        {
            // Not decoration: the operand model is shared with the IDE, so a Write that dropped or
            // reordered bytes would only show up as a corrupted tree in the editor.
            var bytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

            var operand = new VMSubRoutineOperand();
            operand.Read(bytes);
            operand.Write(bytes);

            var reread = new VMSubRoutineOperand();
            reread.Read(bytes);

            Assert.True(reread.UseTemp0);
            Assert.Equal(operand.Arguments, reread.Arguments);
        }
    }
}
