using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// SimAntics primitive 30 - <b>"gosub found action"</b> in TS1, <b>"syslog"</b> in TSO - and
    /// the registration in <see cref="VMContext.InitVMConfig"/> that has to pick between them.
    ///
    /// <para>
    /// The primitive is one opcode with two unrelated meanings, which is why this file is mostly
    /// registration tests. The community primitive table (simstek.fandom.com/wiki/Primitives,
    /// written against The Sims Online) documents 30 as "syslog", says it "always logs a string
    /// from private String Table 308", and carries no operand - which is the whole reason the
    /// operand shape cannot settle the question. <c>VMContext.cs</c> grew up with the TSO reading
    /// registered unconditionally and the TS1 one dropped in as an overwrite inside
    /// <c>if (ts1)</c>, with a stale <c>//TS1 - gosub found action</c> comment stranded above the
    /// <c>VMSysLog</c> block. The effective TS1 behaviour was therefore correct, but only by
    /// accident of <c>AddPrimitive</c> being last-wins: the code said "syslog", the comment said
    /// something else again, and nothing in the tree would have said so if the two had been
    /// swapped.
    /// </para>
    ///
    /// <para>
    /// The shipped TS1 data decides it. Over the 11 TS1 cabinets in <c>D:\Program Files\Sims</c>
    /// there are <b>31</b> opcode-30 sites. All 31 have an all-zero 8-byte operand; all 31 sit in
    /// a "try autonomy"-shaped tree; and all 31 are in trees that also contain opcode 3,
    /// <c>find_best_interaction</c> (there are 32 opcode-3 sites, one per tree, and 31 of those
    /// trees pair with a 30 - the only unpaired one is
    /// <c>ExpansionPack/GenieLantern.iff 4127 "bad leisure"</c>). The canonical shape, from
    /// <c>Global/PersonGlobals.iff 8233 "try autonomy"</c>:
    /// <code>
    /// [1] find_best_interaction      T-&gt;3  F-&gt;RETURN_FALSE
    /// [3] MyAttrs[6] = 1              T-&gt;5                       // an interaction was found
    /// [5] GLOBAL 8292                 T-&gt;2  F-&gt;end              // "transition from idle"
    /// [2] opcode 30                   T-&gt;4  F-&gt;6                // &lt;-- this primitive
    /// [4] MyAttrs[6] = 0              T-&gt;9                       // ...and it is finished
    /// </code>
    /// and the same five-instruction run appears in <c>Global/WaiterGlobals.iff 8233</c>,
    /// <c>Global/CatGlobals.iff 8204</c>, the twelve <c>ExpansionPack3/NPC_*.iff 4099</c> barmaids
    /// and pianists, and so on. Attribute 6 is raised the moment an autonomous interaction is found
    /// and cleared on the far side of opcode 30, so it is held <em>across</em> the opcode - which
    /// is only meaningful if the opcode is what performs the handover. <c>VMFindBestAction</c>
    /// (opcode 3) merely <c>EnqueueAction</c>s at <c>VMQueuePriority.Autonomous</c>; something has to
    /// push that queue entry, and 30 is the only opcode in those trees that can.
    /// </para>
    ///
    /// <para>
    /// Three further nails:
    /// <list type="bullet">
    /// <item>Opcode 30's <em>false</em> branch is a second handover, not a fallback. In
    /// <c>Global/PersonGlobals.iff 8233</c> that branch is instruction [6],
    /// <c>GLOBAL 8282 "check queue"</c>, whose instructions [4]/[2]/[1]/[3] are
    /// <c>GLOBAL 8292 "transition from idle"</c> &rarr; <c>MyAttrs[6] = 1</c> &rarr;
    /// <c>GLOBAL 281 "Wait For Notify"</c> (an <c>idle_for_input</c> with AllowPush, i.e.
    /// <c>VMIdleForInput</c>'s own <c>AttemptPush</c>) &rarr; <c>MyAttrs[6] = 0</c>. So the true
    /// branch pushes the found action now and the false branch goes idle and lets allow-push do it
    /// on the next tick: two handover paths, one primitive and one global, the same attribute
    /// bracket. A log line explains neither. This is also why FreeSO's return codes line up -
    /// <c>CONTINUE</c> on a successful push, <c>GOTO_FALSE</c> to "go idle and wait", and after
    /// the interaction finishes the re-entry finds an empty queue and takes the false branch
    /// again, which is the correct place to land.</item>
    /// <item>No <c>.iff</c> in the entire TS1 install ships <c>STR 308</c> (<c>0x013C</c>) - not the
    /// 20 objects that use opcode 30, not any other. A syslog as documented has nothing to
    /// print at any TS1 site.</item>
    /// <item><c>Global/PersonGlobals.iff 8518 "Autofollow - Use Restroom"</c> instruction [2] has
    /// <c>T-&gt;RETURN_TRUE</c> and <c>F-&gt;RETURN_TRUE</c>: the return value is discarded. A
    /// primitive whose purpose is a control handover does not need its two branches to disagree; a
    /// debug log sitting on the autonomy path would be a strange thing to write that way 31
    /// times.</item>
    /// </list>
    /// So: TSO gets <c>VMSysLog</c>, TS1 gets <c>VMGosubFoundAction</c>, each registered in exactly
    /// one branch of <c>InitVMConfig</c>.
    /// </para>
    ///
    /// <para>
    /// <c>VMContext.Primitives</c> is process-global and <c>AddPrimitive</c> never clears the
    /// array, so these tests sit in <see cref="NonParallelCollection"/> and every one of them
    /// re-runs <c>InitVMConfig</c> immediately before reading - reading the array without
    /// initialising it first would only prove what some other test left behind.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class VMTS1GosubFoundActionTests
    {
        // ---- the registration ----------------------------------------------------------------

        [Fact]
        public void Ts1RegistersGosubFoundAction()
        {
            VMContext.InitVMConfig(true);

            var registration = VMContext.Primitives[30];

            Assert.NotNull(registration);
            Assert.IsType<VMGosubFoundAction>(registration.GetHandler());
            Assert.Equal(typeof(VMGosubFoundActionOperand), registration.OperandModel);
            Assert.Equal((ushort)30, registration.Opcode);
            Assert.Equal("gosub_found_action", registration.Name);
        }

        [Fact]
        public void TsoRegistersSysLog()
        {
            VMContext.InitVMConfig(false);

            var registration = VMContext.Primitives[30];

            Assert.NotNull(registration);
            Assert.IsType<VMSysLog>(registration.GetHandler());
            Assert.Equal(typeof(VMSysLogOperand), registration.OperandModel);
            Assert.Equal((ushort)30, registration.Opcode);
            Assert.Equal("syslog", registration.Name);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void NeitherModeLeavesOpcodeThirtyUnregistered(bool ts1)
        {
            // An unregistered opcode does not fail loudly: VMThread.ExecuteInstruction and
            // VMRoutine.Execute both substitute GOTO_TRUE for a null registration. That is the
            // exact failure the old shape was one "tidy up" away from - 31 shipped trees would
            // have skipped their handover with nothing logged.
            VMContext.InitVMConfig(ts1);

            Assert.NotNull(VMContext.Primitives[30]);
        }

        [Theory]
        [InlineData(true, false, typeof(VMSysLog))]
        [InlineData(false, true, typeof(VMGosubFoundAction))]
        [InlineData(true, true, typeof(VMGosubFoundAction))]
        [InlineData(false, false, typeof(VMSysLog))]
        public void TheRegistrationIsTheOneForWhicheverModeWasInitialisedLast(bool first, bool second, Type expected)
        {
            // The point of moving syslog out of the shared block and into the else branch. The old
            // shape depended on an unconditional registration being overwritten; the invariant that
            // actually matters is that the last InitVMConfig call wins, in either order, with no
            // cross-contamination. (The array is never cleared, so "last wins" has to hold for the
            // opcodes the two modes disagree about and nothing else may drift.)
            VMContext.InitVMConfig(first);
            VMContext.InitVMConfig(second);

            Assert.IsType(expected, VMContext.Primitives[30].GetHandler());
        }

        // ---- why the two readings are not interchangeable ---------------------------------------

        [Fact]
        public void SysLogIsAPureNoOpThatNeverTouchesTheThread()
        {
            // The TSO reading, characterised: it reads no operand and reaches no thread state, so
            // it can be called with a null frame. That is also the whole hazard - registered in TS1
            // it would return GOTO_TRUE on every one of the 31 autonomy sites, so each tree would
            // clear MyAttrs[6] and fall out of its idle while the interaction opcode 3 had just
            // queued sat unstarted in Thread.Queue, with no crash and no log line to say so.
            var exit = new VMSysLog().Execute(null, null);

            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, exit);
        }

        [Fact]
        public void TheTs1HandlerIsNotTheNoOp()
        {
            // A regression guard rather than a behavioural test: if the two handlers ever became the
            // same type, "which one is registered" would stop being a question worth asking.
            Assert.NotEqual(typeof(VMSysLog), typeof(VMGosubFoundAction));
            Assert.NotEqual(typeof(VMGosubFoundActionOperand), typeof(VMSysLogOperand));
        }

        // ---- the operand shape ----------------------------------------------------------------

        [Fact]
        public void BothOperandModelsAreZeroOperand()
        {
            // All 31 shipped sites carry eight zero bytes, and the documented syslog carries no
            // operand either, so neither model may require a byte to be present. VMTranslator hands
            // the operand model the raw array it was given, which can be empty, so an empty read
            // has to survive too - the same reasoning as the truncated-operand test in
            // VMTS1MotiveHelperTests.
            var shipped = new byte[8];

            var foundAction = new VMGosubFoundActionOperand();
            foundAction.Read(shipped);
            foundAction.Read(new byte[0]);
            foundAction.Write(shipped);

            var syslog = new VMSysLogOperand();
            syslog.Read(shipped);
            syslog.Read(new byte[0]);
            syslog.Write(shipped);
        }

        // ---- the tree-level contract ------------------------------------------------------------

        /// <summary>
        /// The instruction stream of <c>Global/PersonGlobals.iff 8233 "try autonomy"</c>, raw
        /// opcode and branch pointers, transcribed from the shipped bytes (the 255/254/253
        /// sentinels are <c>RETURN_FALSE</c>/<c>RETURN_TRUE</c>/"fall through"). This is the
        /// canonical shape of all 31 sites: a find, the attribute-6 raise, "transition from idle",
        /// opcode 30, the attribute-6 clear.
        /// </summary>
        private static VMInstruction[] PersonGlobalsTryAutonomy()
        {
            // opcode, truePtr, falsePtr
            var raw = new (ushort Op, byte T, byte F)[]
            {
                (321, 1, 7),                        // GLOBAL 321, "Is lot a Residential Lot?"
                (3,   3, 255),                      // find_best_interaction
                (30,  4, 6),                        // <-- gosub found action
                (2,   5, 253),                      // MyAttrs[6] = 1
                (2,   9, 253),                      // MyAttrs[6] = 0
                (8292, 2, 253),                     // GLOBAL 8292, "transition from idle"
                (8282, 10, 254),                    // GLOBAL 8282, "check queue"
                (2,   1, 8),                        // Global[30] == Literal[1]
                (2,   11, 254),                     // MyObj[11] != Global[3]
                (8300, 6, 253),                     // GLOBAL 8300
                (8300, 254, 253),                   // GLOBAL 8300
                (424, 12, 1),                       // GLOBAL 424
                (2,   254, 1),                      // MyAttrs[77] orEq Literal[0]
            };

            var instructions = new VMInstruction[raw.Length];
            for (var i = 0; i < raw.Length; i++)
            {
                instructions[i] = new VMInstruction
                {
                    Index = (byte)i,
                    Opcode = raw[i].Op,
                    TruePointer = raw[i].T,
                    FalsePointer = raw[i].F,
                };
            }
            return instructions;
        }

        [Fact]
        public void EveryPrimitiveInTheTryAutonomyTreeIsRegisteredInTs1()
        {
            // The tree-level contract. If opcode 30 were unregistered, or if a future edit left any
            // other opcode in these 31 trees unregistered, the caller would substitute GOTO_TRUE and
            // the autonomy loop would quietly run off the end of its own success path.
            VMContext.InitVMConfig(true);
            var instructions = PersonGlobalsTryAutonomy();

            var missing = instructions
                .Where(i => i.Opcode < 256 && VMContext.Primitives[i.Opcode] == null)
                .Select(i => $"[{i.Index}] op{i.Opcode}")
                .ToList();

            Assert.Empty(missing);
        }

        [Fact]
        public void TheFindAndHandoverOpcodesAreTheEnqueueAndPushPair()
        {
            // Opcode 3 enqueues onto Caller.Thread.Queue at VMQueuePriority.Autonomous and sets the
            // stack object; opcode 30 is the only opcode in the tree that can push that entry back
            // off. Asserting the pair rather than either half is what stops a future edit from
            // pairing the find with something that cannot complete it - which is precisely the
            // state opcode 30 was in for as long as it was registered as VMSysLog.
            VMContext.InitVMConfig(true);

            var find = VMContext.Primitives[3];
            var handover = VMContext.Primitives[30];

            Assert.IsType<VMFindBestAction>(find.GetHandler());
            Assert.IsType<VMGosubFoundAction>(handover.GetHandler());

            // The TS0 spelling of the find (opcode 65) must stay the same handler, so the enqueue
            // side of the pair is genuinely shared and only the handover half is mode-dependent.
            Assert.IsType<VMFindBestAction>(VMContext.Primitives[65].GetHandler());
        }

        [Fact]
        public void TheDeclaredOperandModelReadsTheShippedOperandBytes()
        {
            // Round-trip the opcode-30 instruction's operand through whatever operand model the
            // registration declares, rather than through the concrete type, so a registration that
            // points at the wrong model fails here instead of at run time on a real autonomy tree.
            VMContext.InitVMConfig(true);
            var instructions = PersonGlobalsTryAutonomy();
            Assert.Single(instructions, i => i.Opcode == 30);
            var shipped = new byte[8];

            var model = VMContext.Primitives[30].OperandModel;
            var operand = (VMPrimitiveOperand)Activator.CreateInstance(model);
            operand.Read(shipped);

            Assert.IsType<VMGosubFoundActionOperand>(operand);
        }

        [Fact]
        public void TheHandoverSiteIsReachedOnlyFromTheFindResult()
        {
            // The claim the whole verdict rests on, expressed against the transcribed tree: the
            // opcode-30 site has exactly two incoming edges in the whole tree, and both of them are
            // "an autonomous interaction was found and is ready to start" - instruction [1]
            // (find_best_interaction) branching true onto the MyAttrs[6] = 1 raise, which falls
            // straight through into the site, and instruction [5] (GLOBAL 8292, "transition from
            // idle") branching true straight at it. No false branch anywhere in the tree reaches
            // it, and nothing else in the tree can reach it at all. That is why a reading of 30 as
            // a debug log has nothing to explain, and why "Autofollow - Use Restroom"'s discarded
            // return value is the outlier rather than the rule.
            var instructions = PersonGlobalsTryAutonomy();
            var site = instructions.Single(i => i.Opcode == 30);

            var predecessors = new List<string>();
            for (var i = 0; i < instructions.Length; i++)
            {
                if (i == site.Index) continue;
                var ins = instructions[i];
                if (ins.TruePointer == site.Index) predecessors.Add($"[{i}] op{ins.Opcode} true");
                if (ins.FalsePointer == site.Index) predecessors.Add($"[{i}] op{ins.Opcode} false");
                if (i + 1 == site.Index) predecessors.Add($"[{i}] op{ins.Opcode} fallthrough");
            }

            Assert.Equal(new[] { "[1] op3 fallthrough", "[5] op8292 true" }, predecessors);
        }

        [Fact]
        public void TheFalseBranchOfTheHandoverIsTheDeferredHandover()
        {
            // PersonGlobals.iff 8233 [2]'s false pointer lands on [6], GLOBAL 8282 "check queue",
            // which performs the same handover through GLOBAL 281 "Wait For Notify" - an
            // idle_for_input with AllowPush, i.e. the other AttemptPush call site. Asserted against
            // the transcription rather than in a comment because it is the single strongest piece of
            // evidence: a primitive whose false branch is "go idle and let allow-push start it
            // instead" is a control-transfer primitive, and one whose false branch were a debug log
            // would have nothing to fall through to.
            VMContext.InitVMConfig(true);
            var instructions = PersonGlobalsTryAutonomy();
            var site = instructions.Single(i => i.Opcode == 30);
            var falseTarget = instructions[site.FalsePointer];

            // 8282 is >= 256, so it is dispatched as a sub-routine call and never consults the
            // primitive table at all - which is why the two readings of opcode 30 could differ
            // without 8282's own resolution mattering to the argument.
            Assert.Equal((ushort)8282, falseTarget.Opcode);
            Assert.True(falseTarget.Opcode >= 256);
            Assert.Equal(256, VMContext.Primitives.Length);
        }
    }
}
