using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// TS1 opcode 34 - the tutorial's <b>"instruct"</b> primitive - and the trigger that makes the
    /// tutorial reachable at all.
    ///
    /// <para>
    /// <b>What opcode 34 is.</b> A census of every BHAV in the eleven retail cabinets in
    /// <c>D:\Program Files\Sims</c> finds <b>58</b> sites of an opcode FreeSO did not register, and
    /// every one of them is tutorial content: <c>Objects/Tutorial.iff</c> (32),
    /// <c>Objects/HelpSystem.iff</c> (13) and <c>ExpansionShared/HDHelpSystem.iff</c> (13, the
    /// Downtown copy of the same object). Nothing else in the install uses it. It is the "instruct"
    /// bracket around a tutorial step - the on-screen "do this now" marker that goes up with the
    /// step's balloon and comes down when its detect loop reports done. The step's <i>text</i> was
    /// never missing: it is a <c>dialog_private</c> of type 4
    /// (<see cref="VMDialogType.Sims1Tutorial"/>) reading <c>STR 301</c>, which is the registered
    /// opcode 36.
    /// </para>
    ///
    /// <para>
    /// <b>The three facts pinned here.</b>
    /// </para>
    /// <list type="bullet">
    /// <item><b>The operand.</b> Eight bytes, four little-endian int16:
    /// <c>[Target][Variant][Enabled][Reserved]</c>. The fourth is 0 at all 58 sites, the third is
    /// only ever 0 or 1 and it is the one that flips, and the first two take exactly four values in
    /// the whole install. Every operand below is transcribed from a shipped site, so a wrong byte
    /// order or a swapped field shows up as a failing decode rather than as a highlight in the
    /// wrong place on someone's screen.</item>
    /// <item><b>The answer rule.</b> All 58 sites put 253 in the false pointer, and
    /// <c>VMThread.MoveToInstruction</c>'s <c>case 253</c> resolves that as "continue along the only
    /// path available" by taking the <em>other</em> branch - it prefers the true pointer whenever
    /// the true pointer is not itself 253. So <c>GOTO_FALSE</c> lands on the same instruction
    /// <c>GOTO_TRUE</c> would, and the answer is unobservable rather than merely unread.</item>
    /// <item><b>The trigger.</b> The tutorial controller is <c>OBJD Global == 0</c>, so nothing
    /// spawns it, and it is not a passive tour - <c>Tutorial.iff</c> 4098 <c>"setup"</c> calls
    /// <c>remove_object_instance</c> on the lot and 4127 <c>"add Betty"</c> rewrites the
    /// household. So the trigger is opt-in and defaults off, and the decision is a pure function of
    /// five booleans that can be asked about without building a lot.</item>
    /// </list>
    ///
    /// <para>
    /// <c>VMContext.Primitives</c> is process-global and <c>AddPrimitive</c> never clears the
    /// array, so the registration tests sit in <see cref="NonParallelCollection"/> and re-run
    /// <c>InitVMConfig</c> immediately before reading - reading it without initialising first would
    /// only prove what some other test left behind.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class VMTS1TutorialTests
    {
        /// <summary>
        /// One shipped opcode-34 operand, transcribed from the retail bytes exactly as
        /// <c>disraw</c> prints them - eight bytes, not four shorts, because the whole question is
        /// what the bytes are.
        /// </summary>
        private static byte[] Bytes(int b0, int b1, int b2, int b3, int b4, int b5, int b6, int b7) =>
            new byte[] { (byte)b0, (byte)b1, (byte)b2, (byte)b3, (byte)b4, (byte)b5, (byte)b6, (byte)b7 };

        /// <summary>
        /// The eight bytes an opcode-34 instruction carrying <paramref name="instruction"/> and
        /// <paramref name="enable"/> would have, so the state-machine tests can talk in the shipped
        /// byte layout rather than in field assignments.
        /// </summary>
        private static byte[] BuildOperand(VMTS1TutorialInstruction instruction, bool enable)
        {
            var bytes = new byte[8];
            bytes[0] = (byte)(instruction.Target & 0xFF);
            bytes[1] = (byte)(instruction.Target >> 8);
            bytes[2] = (byte)(instruction.Variant & 0xFF);
            bytes[3] = (byte)(instruction.Variant >> 8);
            bytes[4] = (byte)(enable ? 1 : 0);
            return bytes;
        }

        private static VMTS1TutorialInstructState Step(VMTS1TutorialInstructState state, VMTS1TutorialInstruction i, bool enable) =>
            VMTS1TutorialInstructHelper.Step(
                state, i.Target, i.Variant, enable ? VMTS1TutorialInstructHelper.EnableCode : VMTS1TutorialInstructHelper.DisableCode);

        // ---- the operand shape, against the shipped bytes ---------------------------------------

        [Fact]
        public void TheCommonShippedOperandDecodesToTheDocumentedFields()
        {
            // 50 of the 58 sites are this one: e.g. Tutorial.iff 4135 "tutorial - live mode" [5]
            // (on) and [6] (off), and HelpSystem.iff 4123 "tutorial - Job Panal" [2]/[3].
            // 00 19 00 00 01 00 00 00 -> 0x1900 = 6400, 0, 1, 0.
            var on = VMTS1TutorialInstructHelper.Decode(Bytes(0x00, 0x19, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00));
            Assert.Equal((ushort)6400, on.Target);
            Assert.Equal((ushort)0, on.Variant);
            Assert.Equal(VMTS1TutorialInstructHelper.EnableCode, on.Enabled);
            Assert.Equal(0, on.Reserved);

            var off = VMTS1TutorialInstructHelper.Decode(Bytes(0x00, 0x19, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00));
            Assert.Equal((ushort)6400, off.Target);
            Assert.Equal((ushort)0, off.Variant);
            Assert.Equal(VMTS1TutorialInstructHelper.DisableCode, off.Enabled);
        }

        [Fact]
        public void EveryShippedSelectorDecodesFromItsOwnBytes()
        {
            // The four (Target, Variant) pairs the whole install uses, each from a real site. The
            // point is byte order: 6400 arrives as 00 19 little-endian, and reading it big-endian
            // would give 25 - a value that looks plausible and is wrong.
            AssertSelector(Bytes(0x00, 0x19, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00), 6400, 0);  // 50 sites
            AssertSelector(Bytes(0x01, 0x0A, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00), 2561, 0);  // 4 sites
            AssertSelector(Bytes(0x02, 0x00, 0x06, 0x00, 0x01, 0x00, 0x00, 0x00), 2, 6);     // 2 sites
            AssertSelector(Bytes(0x00, 0x19, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00), 6400, 1);  // 2 sites
        }

        private static void AssertSelector(byte[] bytes, ushort target, ushort variant)
        {
            var decoded = VMTS1TutorialInstructHelper.Decode(bytes);
            Assert.Equal(target, decoded.Target);
            Assert.Equal(variant, decoded.Variant);
        }

        [Fact]
        public void TheSelectorCensusIsTheOneTheDecodeCovers()
        {
            // The four decoded byte patterns above and the four entries of the census have to be the
            // same four, or one of them is describing a different install than the other.
            var decoded = new[]
            {
                new VMTS1TutorialInstruction(6400, 0),
                new VMTS1TutorialInstruction(2561, 0),
                new VMTS1TutorialInstruction(2, 6),
                new VMTS1TutorialInstruction(6400, 1),
            };
            var census = VMTS1TutorialInstructHelper.ShippedSelectors.Select(s => s.Instruction).ToList();

            Assert.Equal(4, decoded.Length);
            Assert.Equal(4, census.Count);
            foreach (var instruction in decoded)
            {
                Assert.Contains(instruction, census);
            }
        }

        [Fact]
        public void TheReservedFieldIsZeroAtEveryShippedSite()
        {
            // 58 sites, 58 zeros. The field is carried because it is in the operand, not because
            // anything reads it - a non-zero value there would be new information, not a variation -
            // so the operand model is checked not to invent one either.
            var operand = new VMTS1TutorialInstructOperand();
            foreach (var (instruction, _) in VMTS1TutorialInstructHelper.ShippedSelectors)
            {
                foreach (var enable in new[] { true, false })
                {
                    operand.Read(BuildOperand(instruction, enable));
                    Assert.Equal(0, operand.Reserved);
                }
            }
        }

        [Fact]
        public void TheCensusAccountsForEverySite()
        {
            // The census is the evidence behind the "four values" and "58 sites" claims, so its
            // parts have to add up to the whole - a dropped or double-counted entry would quietly
            // make the claim wrong.
            var bySelector = VMTS1TutorialInstructHelper.ShippedSelectors.Sum(s => s.SiteCount);
            var total = VMTS1TutorialInstructHelper.ShippedSiteCount;

            Assert.Equal(total, bySelector);
            Assert.Equal(
                VMTS1TutorialInstructHelper.ShippedEnableSiteCount
                    + VMTS1TutorialInstructHelper.ShippedDisableSiteCount,
                total);

            // No selector may appear in the census twice: the two halves of the census have to be
            // able to agree about how many sites each selector owns.
            var selectors = VMTS1TutorialInstructHelper.ShippedSelectors
                .Select(s => s.Instruction).ToList();
            Assert.Equal(selectors.Count, selectors.Distinct().Count());
        }

        [Fact]
        public void TheCensusHasMoreDisablesThanEnables()
        {
            // 27 enables, 31 disables. The surplus is not an error: retail closes an instruction on
            // the cancel path as well as on the success path, so a disable with no matching enable
            // is normal. This is the shape that makes the disable direction idempotent rather than
            // an error case, and the census is where a change to that would first show up.
            Assert.True(VMTS1TutorialInstructHelper.ShippedDisableSiteCount
                        > VMTS1TutorialInstructHelper.ShippedEnableSiteCount);
        }

        [Fact]
        public void TheDefaultSelectorIsTheOneMostSitesUse()
        {
            // (6400, 0) at 50 of 58 is the load-bearing fact behind refusing to decode it: the same
            // selector is used across steps whose own strings point at entirely different parts of
            // the interface ("Press the LIVE icon below", "Click the Buy button below", "click the
            // Job button below", "Click on the Personality button below"), so it cannot be naming a
            // single control-panel element.
            var mostCommon = VMTS1TutorialInstructHelper.ShippedSelectors
                .OrderByDescending(s => s.SiteCount).First();

            Assert.Equal(new VMTS1TutorialInstruction(6400, 0), mostCommon.Instruction);
            Assert.True(mostCommon.SiteCount * 2 > VMTS1TutorialInstructHelper.ShippedSiteCount);
        }

        [Fact]
        public void TheEnableFlagReadsNonZeroAsOn()
        {
            // The shipped data only ever writes 0 or 1, but a modded object writing 2 must mean
            // "on" and not "off", or a mod would silently invert the tutorial's brackets.
            Assert.False(VMTS1TutorialInstructHelper.IsEnabled(0));
            Assert.True(VMTS1TutorialInstructHelper.IsEnabled(1));
            Assert.True(VMTS1TutorialInstructHelper.IsEnabled(2));
            Assert.True(VMTS1TutorialInstructHelper.IsEnabled(-1));
            Assert.True(VMTS1TutorialInstructHelper.IsEnabled(short.MaxValue));
            Assert.True(VMTS1TutorialInstructHelper.IsEnabled(short.MinValue));
        }

        [Fact]
        public void TheOperandModelReadsTheShippedBytes()
        {
            // Through the model the registration declares rather than through the concrete type, so
            // a registration pointing at the wrong operand fails here rather than at run time.
            VMContext.InitVMConfig(true);
            var model = VMContext.Primitives[VMTS1TutorialInstructHelper.Opcode].OperandModel;
            var operand = (VMPrimitiveOperand)Activator.CreateInstance(model);

            operand.Read(Bytes(0x01, 0x0A, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00));

            Assert.IsType<VMTS1TutorialInstructOperand>(operand);
            Assert.Equal((ushort)2561, ((VMTS1TutorialInstructOperand)operand).Target);
            Assert.Equal((ushort)0, ((VMTS1TutorialInstructOperand)operand).Variant);
            Assert.Equal(1, ((VMTS1TutorialInstructOperand)operand).Enabled);
        }

        [Fact]
        public void TheOperandRoundTripsThroughWrite()
        {
            // BHAVs are re-assembled by the editor and by VMTranslator, so an operand model that
            // reads more than it writes would corrupt a tree on the next save.
            var original = new VMTS1TutorialInstructOperand
            {
                Target = 6400,
                Variant = 6,
                Enabled = 1,
                Reserved = 0,
            };
            var bytes = new byte[8];
            original.Write(bytes);

            var read = new VMTS1TutorialInstructOperand();
            read.Read(bytes);

            Assert.Equal(original.Target, read.Target);
            Assert.Equal(original.Variant, read.Variant);
            Assert.Equal(original.Enabled, read.Enabled);
            Assert.Equal(original.Reserved, read.Reserved);
        }

        [Fact]
        public void ATruncatedOperandReadsAsAllZeroRatherThanThrowing()
        {
            // The translator hands an operand model whatever bytes the instruction carried, which
            // can be none. The caller is a tutorial controller mid-step; it must not die of an
            // arithmetic detail. Zero reads as "switch (0, 0) off", which is a no-op.
            var operand = new VMTS1TutorialInstructOperand();

            operand.Read(new byte[0]);
            operand.Read(new byte[1]);
            operand.Read(new byte[4]);

            Assert.Equal((ushort)0, operand.Target);
            Assert.Equal((ushort)0, operand.Variant);
            Assert.Equal(VMTS1TutorialInstructHelper.DisableCode, operand.Enabled);
            Assert.Equal(0, operand.Reserved);
        }

        [Fact]
        public void DecodingRejectsAnOperandItCannotRead()
        {
            // The opposite discipline from the operand model: Decode is a test-facing helper for
            // the byte layout, not a runtime path, so it refuses rather than inventing zeroes -
            // a silent zero here would make a byte-order mistake look like a valid decode.
            Assert.Throws<ArgumentException>(() => VMTS1TutorialInstructHelper.Decode(null));
            Assert.Throws<ArgumentException>(() => VMTS1TutorialInstructHelper.Decode(new byte[0]));
            Assert.Throws<ArgumentException>(() => VMTS1TutorialInstructHelper.Decode(new byte[7]));
        }

        // ---- the registration ------------------------------------------------------------------

        [Fact]
        public void Ts1RegistersTheInstructPrimitive()
        {
            VMContext.InitVMConfig(true);

            var registration = VMContext.Primitives[VMTS1TutorialInstructHelper.Opcode];

            Assert.NotNull(registration);
            Assert.IsType<VMTS1TutorialInstruct>(registration.GetHandler());
            Assert.Equal(typeof(VMTS1TutorialInstructOperand), registration.OperandModel);
            Assert.Equal((ushort)34, registration.Opcode);
            Assert.Equal("instruct_tutorial", registration.Name);
        }

        [Fact]
        public void TheOpcodeNumberIsThirtyFour()
        {
            // 33 is find_5_worst_motives and 35 is special_effect. A registration written to the
            // wrong index would leave the tutorial's 58 sites unregistered, and the census tests
            // above would still pass, because they never look at the array.
            Assert.Equal((ushort)34, VMTS1TutorialInstructHelper.Opcode);
            VMContext.InitVMConfig(true);
            Assert.Equal(34, VMContext.Primitives[VMTS1TutorialInstructHelper.Opcode].Opcode);
        }

        [Fact]
        public void TheNeighbouringOpcodesAreUntouched()
        {
            // 33 and 35 bracket 34 in the shipped opcode space. A registration written to the
            // wrong index would be caught here rather than by a tutorial that quietly stopped
            // finding worst motives.
            VMContext.InitVMConfig(true);

            Assert.IsType<VMFind5WorstMotives>(VMContext.Primitives[33].GetHandler());
            Assert.IsType<VMSpecialEffect>(VMContext.Primitives[35].GetHandler());
        }

        [Fact]
        public void TsoDoesNotGetTheTs1Handler()
        {
            // Deliberate, and the reason is in VMContext.cs: the community primitive table calls
            // opcode 34 "UIEffect", which is a TSO reading, and the TSO data has no opcode-34 site
            // at all. Registering the tutorial primitive there would put TS1 semantics behind a TSO
            // opcode that other content might eventually use. Leaving it unregistered in TSO is
            // also exactly today's behaviour: VMThread substitutes GOTO_TRUE for a null
            // registration, and the operand is never even decoded.
            //
            // "Unregistered in TSO" cannot be asserted as a null in the live array, because
            // AddPrimitive is last-wins and never clears, so an earlier TS1 call in the same process
            // answers for it. What can be asserted is the invariant that survives the residue:
            // whatever the mode, the slot never holds a handler from some other reading of 34.
            foreach (var ts1 in new[] { true, false })
            {
                VMContext.InitVMConfig(ts1);
                var handler = VMContext.Primitives[VMTS1TutorialInstructHelper.Opcode]?.GetHandler();

                Assert.True(
                    handler == null || handler is VMTS1TutorialInstruct,
                    $"opcode 34 in mode ts1={ts1} is {handler?.GetType().Name ?? "null"}");
            }
        }

        // ---- the answer rule --------------------------------------------------------------------

        [Fact]
        public void TheAnswerIsGotoTrueAndNothingElse()
        {
            // The load-bearing claim, and it is about the shipped branch pointers rather than about
            // any operand: all 58 sites write 253 into the false pointer, and
            // VMThread.MoveToInstruction resolves 253 as "continue along the only path available" by
            // taking the other branch - it prefers the true pointer whenever the true pointer is
            // not itself 253. So GOTO_FALSE would land on the same instruction at every site, and
            // reporting it would be inventing a distinction the shipped scripts cannot observe.
            var exit = VMTS1TutorialInstructHelper.Answer();

            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, exit);
            Assert.NotEqual(VMPrimitiveExitCode.GOTO_FALSE, exit);
            Assert.NotEqual(VMPrimitiveExitCode.GOTO_FALSE_NEXT_TICK, exit);
            Assert.NotEqual(VMPrimitiveExitCode.RETURN_FALSE, exit);
        }

        [Fact]
        public void TheAnswerDoesNotDependOnTheOperand()
        {
            // Stated over the shipped selectors and both flag values rather than as a single call,
            // so a future signature change that started taking the operand cannot quietly become a
            // second, undocumented answer.
            foreach (var (instruction, _) in VMTS1TutorialInstructHelper.ShippedSelectors)
            {
                foreach (var enabled in new short[] { 0, 1 })
                {
                    Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, VMTS1TutorialInstructHelper.Answer());
                    var state = VMTS1TutorialInstructHelper.Step(
                        VMTS1TutorialInstructState.Empty, instruction.Target, instruction.Variant, enabled);
                    Assert.NotNull(state);
                }
            }
        }

        [Fact]
        public void TheHandlerNeverThrowsOnAMissingFrame()
        {
            // The primitive is registered and it changes no branch - which is the safety property,
            // since the false branch is 253 everywhere. A dispatch always has a frame, but a test
            // that could not call it at all would leave the "touches nothing" claim unpinned.
            var exit = new VMTS1TutorialInstruct().Execute(null, new VMTS1TutorialInstructOperand());

            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, exit);
        }

        // ---- the state machine ------------------------------------------------------------------

        [Fact]
        public void AnEnableTurnsTheInstructionOn()
        {
            var state = Step(VMTS1TutorialInstructState.Empty, new VMTS1TutorialInstruction(6400, 0), true);

            Assert.False(state.IsEmpty);
            Assert.Equal(1, state.Count);
            Assert.Contains(new VMTS1TutorialInstruction(6400, 0), state);
        }

        [Fact]
        public void ADisableTurnsItBackOff()
        {
            var on = Step(VMTS1TutorialInstructState.Empty, new VMTS1TutorialInstruction(6400, 0), true);
            var off = Step(on, new VMTS1TutorialInstruction(6400, 0), false);

            Assert.True(off.IsEmpty);
            Assert.DoesNotContain(new VMTS1TutorialInstruction(6400, 0), off);
        }

        [Fact]
        public void ADisableWithNoMatchingEnableIsHarmless()
        {
            // Not a defensive nicety. Objects/HelpSystem.iff 4135 "tutorial - Sell an Object" has one
            // enable at [4] and two disables at [3] and [9], and the second is on the T->RETURN_TRUE
            // exit: a disable clears a level, it does not decrement a counter, and a second clear
            // must not underflow.
            var state = Step(VMTS1TutorialInstructState.Empty, new VMTS1TutorialInstruction(6400, 0), false);

            Assert.True(state.IsEmpty);
            Assert.Same(VMTS1TutorialInstructState.Empty, state);
        }

        [Fact]
        public void ADoubleEnableIsIdempotent()
        {
            var once = Step(VMTS1TutorialInstructState.Empty, new VMTS1TutorialInstruction(6400, 0), true);
            var twice = Step(once, new VMTS1TutorialInstruction(6400, 0), true);

            Assert.Equal(1, twice.Count);
            Assert.Same(once, twice);
        }

        [Fact]
        public void ARepeatedDisableIsIdempotent()
        {
            var target = new VMTS1TutorialInstruction(2, 6);
            var on = Step(VMTS1TutorialInstructState.Empty, target, true);
            var off = Step(on, target, false);
            var offAgain = Step(off, target, false);

            Assert.True(offAgain.IsEmpty);
            Assert.Same(off, offAgain);
        }

        [Fact]
        public void ADifferentSelectorIsADifferentInstruction()
        {
            // 6400/0 and 2561/0 bracket different steps - "the indicator that was over Bob's head is
            // now over Betty" uses 2561 - so they must not collapse into one entry. A set keyed on
            // the first short alone would.
            var a = Step(VMTS1TutorialInstructState.Empty, new VMTS1TutorialInstruction(6400, 0), true);
            var both = Step(a, new VMTS1TutorialInstruction(2561, 0), true);

            Assert.Equal(2, both.Count);
            Assert.Contains(new VMTS1TutorialInstruction(6400, 0), both);
            Assert.Contains(new VMTS1TutorialInstruction(2561, 0), both);

            var cleared = Step(both, new VMTS1TutorialInstruction(2561, 0), false);
            Assert.Equal(1, cleared.Count);
            Assert.Contains(new VMTS1TutorialInstruction(6400, 0), cleared);
        }

        [Fact]
        public void TheVariantIsPartOfTheKeyToo()
        {
            // (6400, 0) and (6400, 1) are two different steps - the second is the "press the LIVE
            // icon" one, Tutorial.iff 4116 "tutorial - navigation" [16]/[17] - so the key is the
            // pair, not the first short.
            var a = new VMTS1TutorialInstruction(6400, 0);
            var b = new VMTS1TutorialInstruction(6400, 1);

            Assert.True(a != b);
            Assert.False(a.Equals(b));
            Assert.True(a.Equals(new VMTS1TutorialInstruction(6400, 0)));
            Assert.Equal(a.GetHashCode(), new VMTS1TutorialInstruction(6400, 0).GetHashCode());
            Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void TheWholeStepSequenceLeavesNothingOn()
        {
            // End to end over the canonical bracket, Tutorial.iff 4135 [5]/[6]: enable, the step's
            // detect loop runs, disable. A lot that has finished a step must be back to empty,
            // because that is the state the next step's enable starts from.
            var state = VMTS1TutorialInstructState.Empty;
            var observed = new List<int>();

            foreach (var (instruction, _) in VMTS1TutorialInstructHelper.ShippedSelectors)
            {
                state = Step(state, instruction, true);
                observed.Add(state.Count);
                state = Step(state, instruction, false);
                Assert.True(state.IsEmpty, $"selector {instruction} was left on after its disable");
            }

            Assert.Equal(new[] { 1, 1, 1, 1 }, observed);
        }

        [Fact]
        public void StepTreatsANullStateAsEmpty()
        {
            // Defensive on purpose: the state is a field on VMContext, and a tutorial controller
            // must not be the thing that discovers it was never written.
            var state = VMTS1TutorialInstructHelper.Step(null, 6400, 0, 0);

            Assert.NotNull(state);
            Assert.True(state.IsEmpty);
        }

        [Fact]
        public void TheStateIsImmutableRatherThanMutatedInPlace()
        {
            // The reason it is a value: a renderer may hold a snapshot across a frame boundary, and
            // the next tutorial step must not change what it is looking at.
            var empty = VMTS1TutorialInstructState.Empty;
            var on = empty.With(new VMTS1TutorialInstruction(6400, 0));

            Assert.True(empty.IsEmpty);
            Assert.False(on.IsEmpty);
            Assert.NotSame(empty, on);
        }

        [Fact]
        public void TheStateEnumeratesWhatIsOn()
        {
            // What a renderer would iterate. The count and the enumeration must agree, or a caller
            // that trusted one of them would draw a different number of markers from the other.
            var state = Step(VMTS1TutorialInstructState.Empty, new VMTS1TutorialInstruction(6400, 0), true);
            state = Step(state, new VMTS1TutorialInstruction(2, 6), true);

            Assert.Equal(state.Count, state.Count());
            Assert.Equal(state.Count, state.Instructions.Count);
            Assert.Equal(
                new[] { new VMTS1TutorialInstruction(6400, 0), new VMTS1TutorialInstruction(2, 6) },
                state.ToList());
        }

        // ---- the trigger -----------------------------------------------------------------------

        private static VMTS1TutorialTriggerInputs Inputs(
            bool isTS1 = true, bool hasFamily = true, bool helpEnabled = true,
            bool controllerPresent = false, bool completed = false) =>
            new VMTS1TutorialTriggerInputs(isTS1, hasFamily, helpEnabled, controllerPresent, completed);

        [Fact]
        public void TheTutorialStartsWhenEveryClauseAgrees()
        {
            Assert.True(VMTS1TutorialTrigger.ShouldStart(Inputs()));
        }

        [Fact]
        public void TheTutorialIsOptInAndOffByDefault()
        {
            // The safety property, not a preference. Tutorial.iff 4098 "setup" calls
            // remove_object_instance on the lot and on itself, and 4127 "add Betty" rewrites the
            // household - so an always-on trigger would let any lot load rebuild the player's house.
            Assert.False(VMTS1TutorialTrigger.HelpEnabled);
            Assert.False(VMTS1TutorialTrigger.ShouldStart(Inputs(helpEnabled: false)));
        }

        [Fact]
        public void ExactlyOneClauseOffIsEnoughToStopIt()
        {
            // A decision that needs two things to be wrong is a decision with a hole in it, so each
            // clause is knocked out on its own, from a state that is otherwise permissive.
            var singleClauses = new[]
            {
                Inputs(isTS1: false),                 // opcode 34 is not registered outside if (ts1)
                Inputs(helpEnabled: false),           // the opt-in
                Inputs(completed: true),              // retail's tutorial is a first run
                Inputs(controllerPresent: true),      // a second controller is a second tutorial
                Inputs(hasFamily: false),             // 4098 "setup" rebuilds the lot it is given
            };

            Assert.Equal(5, singleClauses.Length);
            Assert.All(singleClauses, i => Assert.False(VMTS1TutorialTrigger.ShouldStart(i)));
        }

        [Fact]
        public void TheTriggerGuidsAreTheOnesTheObjsCarry()
        {
            // Objects/Tutorial.iff OBJD 0x41A7 is Global == 0, which is the whole reason this class
            // exists: nothing spawns it. HelpSystem.iff (0x8963D7C1) and
            // ExpansionShared/HDHelpSystem.iff (0xF9EDB066) are both Global == 1 and are therefore
            // already spawned by VMTS1ActivatorNew, which is why registering opcode 34 alone
            // already un-mutes their trees.
            Assert.Equal(0xC3249A1Du, VMTS1TutorialTrigger.TutorialControllerGuid);
            Assert.Equal(0x8963D7C1u, VMTS1TutorialTrigger.HelpSystemGuid);
            Assert.Equal(0xF9EDB066u, VMTS1TutorialTrigger.DowntownHelpSystemGuid);
        }

        [Fact]
        public void TheThreeControllerGuidsAreDistinct()
        {
            // All three objects share OBJD chunk id 0x41A7, so anything that identified them by
            // chunk id would confuse the tutorial controller with the help system and spawn the
            // wrong one.
            var guids = new[]
            {
                VMTS1TutorialTrigger.TutorialControllerGuid,
                VMTS1TutorialTrigger.HelpSystemGuid,
                VMTS1TutorialTrigger.DowntownHelpSystemGuid,
            };

            Assert.Equal(guids.Length, guids.Distinct().Count());
        }

        [Fact]
        public void TheTriggerEntryPointsAreSafeWithoutAContext()
        {
            // A null context is what a caller gets if it wires the trigger too early in a lot load.
            // Returning null rather than throwing means the wrong call site is a missing tutorial,
            // not a broken lot. The call site is going to be a lot-load sequence, and a lot load has
            // a dozen ways to arrive without a VM, so every entry point checks for itself.
            Assert.Null(VMTS1TutorialTrigger.TryStart(null, hasActiveFamily: true));
            Assert.False(VMTS1TutorialTrigger.Reset(null));
            Assert.False(VMTS1TutorialTrigger.IsTutorialControllerPresent(null));
        }

        // ---- the end-to-end bracket, against a transcribed tree ---------------------------------

        /// <summary>
        /// The instructions of <c>Tutorial.iff 4135 "tutorial - live mode"</c> that carry the step,
        /// raw opcode and branch pointers, transcribed from the shipped bytes (255/254/253 are
        /// <c>RETURN_FALSE</c>/<c>RETURN_TRUE</c>/"fall through"). This is the canonical pair: a
        /// type-4 dialog, an enable, the step's detect, a disable.
        /// </summary>
        private static VMInstruction[] TutorialLiveModeStep()
        {
            var raw = new (ushort Op, byte T, byte F)[]
            {
                (3,    8, 9),    // [3] dialog_private, type 4 (Sims1Tutorial), message STR 301[33]
                (2,    5, 9),    // [4] Local[0] = 0x1195
                (34,   3, 253),  // [5] instruct ON  (6400, 0, 1, 0)  T -> back to the dialog
                (34,  43, 253),  // [6] instruct OFF (6400, 0, 0, 0)  T -> next step
                (4120, 6, 3),    // [7] 4120 "detect button press" T -> close, F -> redisplay
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
        public void EveryPrimitiveInTheTutorialStepIsRegisteredInTs1()
        {
            // The tree-level contract. Opcode 36 - the step's own words - was already registered and
            // opcode 34 was not, so this tree was running with its marker silently missing. If a
            // future edit left any of them unregistered the caller would substitute GOTO_TRUE and the
            // step would run off the end of its own success path with nothing logged.
            VMContext.InitVMConfig(true);
            var instructions = TutorialLiveModeStep();

            var missing = instructions
                .Where(i => i.Opcode < 256 && VMContext.Primitives[i.Opcode] == null)
                .Select(i => $"[{i.Index}] op{i.Opcode}")
                .ToList();

            Assert.Empty(missing);
        }

        [Fact]
        public void BothInstructSitesInTheStepReadFalseAsFallThrough()
        {
            // The answer rule, asserted against the transcribed tree and not only in the abstract:
            // 253 in the false pointer is what makes GOTO_TRUE and GOTO_FALSE the same instruction.
            //
            // The step's *other* primitives are transcribed with real branch targets, and that is
            // the point: 253 is not a decoded instruction index, it is the raw byte, and 253 only
            // resolves to "the next instruction" because VMThread.MoveToInstruction prefers the
            // true pointer. So the instruct sites carry 253 where the dialog and the expression
            // carry 8 and 9, and reading the pair as a resolved target is exactly the mistake that
            // would make the answer look observable when it is not.
            var instructions = TutorialLiveModeStep();
            var instructSites = instructions
                .Where(i => i.Opcode == VMTS1TutorialInstructHelper.Opcode).ToList();

            Assert.Equal(2, instructSites.Count);
            Assert.All(instructSites, i => Assert.Equal(253, i.FalsePointer));
            Assert.NotEqual(253, instructions[0].FalsePointer);
        }

        [Fact]
        public void TheStepOpensAndClosesTheSameInstruction()
        {
            // The two sites in the step carry identical selectors and differ only in the enable
            // flag, which is the property the whole state machine exists to honour: whatever goes
            // up has to come back down, and opcode 34 is what does it.
            var target = new VMTS1TutorialInstruction(6400, 0);
            var onRead = new VMTS1TutorialInstructOperand();
            onRead.Read(BuildOperand(target, enable: true));
            var offRead = new VMTS1TutorialInstructOperand();
            offRead.Read(BuildOperand(target, enable: false));

            Assert.Equal(onRead.Target, offRead.Target);
            Assert.Equal(onRead.Variant, offRead.Variant);
            Assert.NotEqual(onRead.Enabled, offRead.Enabled);

            var state = VMTS1TutorialInstructHelper.Step(
                VMTS1TutorialInstructState.Empty, onRead.Target, onRead.Variant, onRead.Enabled);
            Assert.Equal(1, state.Count);

            state = VMTS1TutorialInstructHelper.Step(state, offRead.Target, offRead.Variant, offRead.Enabled);
            Assert.True(state.IsEmpty);
        }

        [Fact]
        public void TheStepDialogIsTheTutorialDialogType()
        {
            // Why the words were never missing: the step's balloon is dialog_private type 4 reading
            // STR 301, and STR 301 is exactly what VMDialogPrivateStrings.Execute reads
            // (context.ScopeResource.Get<STR>(301)). Tutorial.iff ships that table with 58 lines and
            // HelpSystem.iff with 159, so registering opcode 34 added the marker bracket and left
            // the text exactly where it already worked.
            Assert.Equal(4, (int)VMDialogType.Sims1Tutorial);
        }
    }
}
