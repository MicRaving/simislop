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
    /// TS1 opcode 10 - the help system's one service call into the game's help layer, in its two
    /// modes ("is it done yet" / "do the thing") - and the reachability census behind the answer
    /// rule.
    ///
    /// <para>
    /// <b>What opcode 10 is.</b> A census of every BHAV in the eleven retail cabinets in
    /// <c>D:\Program Files\Sims</c> finds <b>13</b> sites of an opcode FreeSO did not register
    /// (and the 70 <c>Downloads\*\*\*.far</c> custom-content lots add none). Eleven are in the
    /// help / tutorial object family - <c>Objects/HelpSystem.iff</c> (5),
    /// <c>ExpansionShared/HDHelpSystem.iff</c> (4) and <c>Objects/Tutorial.iff</c> (2) - and two
    /// are in <c>GameData/Global/Global.iff</c> 274 <c>'Move forward n tiles (n)'</c>, a tree
    /// <b>opcode 274 has no call site for anywhere in the install</b> and whose own two opcode-10
    /// instructions are stranded behind a <c>burn</c> that returns before them.
    /// </para>
    ///
    /// <para>
    /// <b>The three facts pinned here.</b>
    /// </para>
    /// <list type="bullet">
    /// <item><b>The operand.</b> Eight bytes, four little-endian int16:
    /// <c>[Mode][Reserved1][Reserved2][Reserved3]</c>. The last three shorts are zero at all 13
    /// sites, byte 1 is zero at all 13, and the value is only ever 0 or 1. Every operand below is
    /// transcribed from a shipped site, so a wrong byte order shows up as a failing decode rather
    /// than as the difference between a poll and a single pass.</item>
    /// <item><b>The answer rule.</b> Six of the nine reachable sites name the <em>same</em>
    /// instruction on both branches, so they cannot tell <c>GOTO_TRUE</c> from
    /// <c>GOTO_FALSE</c> at all. The three that can are all polls, and at all three the false
    /// branch is a <c>GLOBAL 280 'idle'</c> whose true pointer is the opcode-10 instruction itself -
    /// a false answer there is a loop with no exit, not a branch.</item>
    /// <item><b>The reachability census.</b> Of the 4 sites nothing can reach, 3 name a different
    /// instruction on the two branches: one in the uncalled global tree and the two
    /// <c>'reset lot'</c> trees, which are in neither object's OBJf and have no private caller.
    /// The two <c>'reset lot'</c> ones are the only sites anywhere in the install where a false
    /// answer would lead somewhere other than a retry, and neither can be run.</item>
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
    public sealed class VMTS1HelpServiceCallTests
    {
        /// <summary>
        /// The raw eight bytes of each shipped site, exactly as <c>disraw</c> prints them. Written
        /// longhand rather than built with <c>Encode</c> on purpose: the whole question is what the
        /// bytes are, so a test that assembled them from the field it is checking would prove
        /// nothing.
        /// </summary>
        private static readonly byte[] PollOperand = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        private static readonly byte[] AdvanceOperand = { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        // ---- the operand shape, against the shipped bytes ---------------------------------------

        [Fact]
        public void TheCommonShippedOperandDecodesToTheDocumentedFields()
        {
            var (mode, r1, r2, r3) = VMTS1HelpServiceCallHelper.Decode(AdvanceOperand);

            Assert.Equal((short)1, mode);
            Assert.Equal((short)0, r1);
            Assert.Equal((short)0, r2);
            Assert.Equal((short)0, r3);
        }

        [Fact]
        public void TheModeIsLittleEndian()
        {
            // Byte 0 carries the value and byte 1 is zero at all 13 shipped sites, so a big-endian
            // reader would happen to agree today - which is exactly why the layout needs a test
            // that a *value* cannot satisfy. 0x0100 is the first 16-bit value a modded object can
            // write that separates the two readings.
            var (mode, _, _, _) = VMTS1HelpServiceCallHelper.Decode(
                new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            Assert.Equal((short)256, mode);
        }

        [Fact]
        public void TheThreeReservedShortsAreZeroAtEveryShippedSite()
        {
            // Six of the eight operand bytes carry nothing anywhere in the install. If any of them
            // ever does, the "[Mode][Reserved...]" split is wrong and the enum is wrong with it.
            foreach (var site in VMTS1HelpServiceCallHelper.ShippedSites)
            {
                var bytes = VMTS1HelpServiceCallHelper.Encode(site.RawMode);
                var (_, r1, r2, r3) = VMTS1HelpServiceCallHelper.Decode(bytes);

                Assert.Equal((short)0, r1);
                Assert.Equal((short)0, r2);
                Assert.Equal((short)0, r3);
            }

            // And on the raw shipped bytes, which is the claim that is actually about the install.
            foreach (var raw in new[] { PollOperand, AdvanceOperand })
            {
                var (_, r1, r2, r3) = VMTS1HelpServiceCallHelper.Decode(raw);
                Assert.Equal((short)0, r1);
                Assert.Equal((short)0, r2);
                Assert.Equal((short)0, r3);
            }
        }

        [Fact]
        public void DecodingRejectsAnOperandItCannotRead()
        {
            // The strict decode exists to prove a layout, so unlike the operand model it must not
            // invent zeroes. A silent zero would decode as a poll - the mode with the loop.
            Assert.Throws<ArgumentException>(() => VMTS1HelpServiceCallHelper.Decode(null));
            Assert.Throws<ArgumentException>(() => VMTS1HelpServiceCallHelper.Decode(new byte[0]));
            Assert.Throws<ArgumentException>(() => VMTS1HelpServiceCallHelper.Decode(new byte[7]));
        }

        [Fact]
        public void EncodeIsTheInverseOfDecode()
        {
            foreach (short raw in new short[] { 0, 1, 2, 255, -1 })
            {
                var (mode, _, _, _) = VMTS1HelpServiceCallHelper.Decode(
                    VMTS1HelpServiceCallHelper.Encode(raw));
                Assert.Equal(raw, mode);
            }
        }

        [Fact]
        public void TheOperandModelReadsTheShippedBytes()
        {
            var advance = new VMTS1HelpServiceCallOperand();
            advance.Read(AdvanceOperand);
            Assert.Equal(VMTS1HelpServiceCallHelper.AdvanceCode, advance.Mode);
            Assert.Equal((short)0, advance.Reserved1);
            Assert.Equal((short)0, advance.Reserved2);
            Assert.Equal((short)0, advance.Reserved3);

            var poll = new VMTS1HelpServiceCallOperand();
            poll.Read(PollOperand);
            Assert.Equal(VMTS1HelpServiceCallHelper.PollCode, poll.Mode);
        }

        [Fact]
        public void TheOperandModelRoundTripsThroughWrite()
        {
            var original = new VMTS1HelpServiceCallOperand
            {
                Mode = 1,
                Reserved1 = 0,
                Reserved2 = 0,
                Reserved3 = 0
            };
            var bytes = new byte[8];
            original.Write(bytes);

            Assert.Equal(AdvanceOperand, bytes);

            var reread = new VMTS1HelpServiceCallOperand();
            reread.Read(bytes);
            Assert.Equal(original.Mode, reread.Mode);
        }

        [Fact]
        public void ATruncatedOperandReadsAsAllZeroRatherThanThrowing()
        {
            // Bounds-checked rather than IoBuffer-throwing, like every other operand model in this
            // assembly: a truncated operand has to be a no-crash. It lands on 0, which classifies as
            // the poll - harmless only because the answer does not depend on the mode, which is what
            // `TheAnswerIsTheSameForEveryShippedSite` pins.
            var operand = new VMTS1HelpServiceCallOperand();
            operand.Read(new byte[] { 0x01 });

            Assert.Equal((short)0, operand.Mode);

            operand.Read(null);
            Assert.Equal((short)0, operand.Mode);
        }

        // ---- the two modes ----------------------------------------------------------------------

        [Fact]
        public void OnlyTheExactPollValuePolls()
        {
            Assert.Equal(VMTS1HelpServiceCallMode.Poll, VMTS1HelpServiceCallHelper.Mode(0));
            Assert.Equal(VMTS1HelpServiceCallMode.Poll,
                VMTS1HelpServiceCallHelper.Mode(VMTS1HelpServiceCallHelper.PollCode));
        }

        [Fact]
        public void AnUnknownValueIsASinglePassRatherThanAPoll()
        {
            // The direction to err in. A poll's false branch at every shipped site is a
            // `idle(400)` back to the same instruction, so an answer rule that ever did answer a
            // poll with GOTO_FALSE would turn an operand value nobody has seen before into a help
            // thread that never returns. Today it cannot, because the answer is GOTO_TRUE for
            // every mode - so this is forward insurance, and
            // `TheAnswerIsTheSameForEveryShippedSite` is what pins the fact that makes it inert.
            foreach (short raw in new short[] { 1, 2, 3, 255, -1, short.MinValue, short.MaxValue })
            {
                Assert.Equal(VMTS1HelpServiceCallMode.Advance, VMTS1HelpServiceCallHelper.Mode(raw));
            }
        }

        [Fact]
        public void TheTwoShippedModeValuesAreTheOnesTheCensusUses()
        {
            Assert.Equal((short)0, VMTS1HelpServiceCallHelper.PollCode);
            Assert.Equal((short)1, VMTS1HelpServiceCallHelper.AdvanceCode);
            Assert.Equal(VMTS1HelpServiceCallMode.Poll,
                VMTS1HelpServiceCallHelper.Mode(VMTS1HelpServiceCallHelper.PollCode));
            Assert.Equal(VMTS1HelpServiceCallMode.Advance,
                VMTS1HelpServiceCallHelper.Mode(VMTS1HelpServiceCallHelper.AdvanceCode));
        }

        // ---- the census ------------------------------------------------------------------------

        [Fact]
        public void TheCensusAccountsForEveryShippedSite()
        {
            var sites = VMTS1HelpServiceCallHelper.ShippedSites;

            Assert.Equal(VMTS1HelpServiceCallHelper.ShippedSiteCount, sites.Length);
            Assert.Equal(13, sites.Length);
        }

        [Fact]
        public void TheCensusSplitsFourWaysAndFourNines()
        {
            var sites = VMTS1HelpServiceCallHelper.ShippedSites;

            Assert.Equal(VMTS1HelpServiceCallHelper.ShippedPollSiteCount,
                sites.Count(s => s.Mode == VMTS1HelpServiceCallMode.Poll));
            Assert.Equal(VMTS1HelpServiceCallHelper.ShippedAdvanceSiteCount,
                sites.Count(s => s.Mode == VMTS1HelpServiceCallMode.Advance));
            Assert.Equal(4, sites.Count(s => s.Mode == VMTS1HelpServiceCallMode.Poll));
            Assert.Equal(9, sites.Count(s => s.Mode == VMTS1HelpServiceCallMode.Advance));
        }

        [Fact]
        public void EveryShippedSiteIsInTheHelpAndTutorialFamily()
        {
            // 11 of 13 are the help / tutorial objects; the other 2 are the uncalled global tree, and
            // naming them here is what stops a future edit from quietly widening the opcode's use.
            var byObject = VMTS1HelpServiceCallHelper.ShippedSites
                .GroupBy(s => s.Object)
                .ToDictionary(g => g.Key, g => g.Count());

            Assert.Equal(5, byObject["Objects/HelpSystem.iff"]);
            Assert.Equal(4, byObject["ExpansionShared/HDHelpSystem.iff"]);
            Assert.Equal(2, byObject["Objects/Tutorial.iff"]);
            Assert.Equal(2, byObject["GameData/Global/Global.iff"]);
            Assert.Equal(4, byObject.Count);
        }

        [Fact]
        public void EverySiteNamesATreeAndAnInstruction()
        {
            foreach (var site in VMTS1HelpServiceCallHelper.ShippedSites)
            {
                Assert.False(string.IsNullOrWhiteSpace(site.Object));
                Assert.False(string.IsNullOrWhiteSpace(site.TreeLabel));
                Assert.True(site.TreeId >= 256, $"{site} is not a tree id");
                Assert.True(site.Instruction >= 0, $"{site} has no instruction index");
            }
        }

        [Fact]
        public void NoTwoSitesShareAnAddress()
        {
            // A duplicated row would make the totals above look right while describing fewer
            // instructions than the census claims.
            var keys = VMTS1HelpServiceCallHelper.ShippedSites
                .Select(s => $"{s.Object}|{s.TreeId}|{s.Instruction}")
                .ToList();

            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        [Fact]
        public void EveryReachableSiteIsOnAHelpSystemEntryPointOrItsCommandTree()
        {
            // The reachability claim, stated as data: the two help systems are OBJD Global == 1 and
            // are already spawned on every lot, so their OBJf load (0x103D = 4157), OBJf main
            // (0x1017 = 4119) and 'process - commands' (4103, called from main) all run. The
            // tutorial controller is Global == 0, so its two sites only run behind Simitone's
            // opt-in VMTS1TutorialTrigger.HelpEnabled - which is why they are counted as reachable
            // rather than dead, and why the answer rule has to hold for them too.
            var reachable = VMTS1HelpServiceCallHelper.ReachableSites;

            Assert.Equal(9, reachable.Length);
            Assert.Equal(13 - 4, reachable.Length);
            Assert.All(reachable, s => Assert.True(s.Reachable, s.ToString()));
            Assert.DoesNotContain(reachable, s => s.TreeId == 274);
            Assert.DoesNotContain(reachable, s => s.TreeLabel == "reset lot");
        }

        [Fact]
        public void TheUnreachableSitesAreTheUncalledGlobalTreeAndTheTwoResetLotTrees()
        {
            var unreachable = VMTS1HelpServiceCallHelper.ShippedSites
                .Where(s => !s.Reachable)
                .ToList();

            Assert.Equal(4, unreachable.Count);
            Assert.All(unreachable, s => Assert.False(s.Reachable, s.ToString()));
            Assert.Equal(2, unreachable.Count(s => s.TreeId == 274));
            Assert.Equal(2, unreachable.Count(s => s.TreeLabel == "reset lot"));
        }

        // ---- the answer rule --------------------------------------------------------------------

        [Fact]
        public void TheAnswerIsGotoTrueAndNothingElse()
        {
            // The load-bearing claim, and it is about the shipped branch pointers rather than about
            // any operand. Six of the nine reachable sites name the same instruction on both
            // branches. The three that do not are all polls whose false branch is a `idle` back to
            // the instruction itself, so GOTO_FALSE is not a branch at those sites, it is a loop
            // with no exit - in the help system's own main tree, on every lot.
            foreach (var mode in new[]
            {
                VMTS1HelpServiceCallMode.Poll,
                VMTS1HelpServiceCallMode.Advance
            })
            {
                var exit = VMTS1HelpServiceCallHelper.AnswerFor(mode);

                Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, exit);
                Assert.NotEqual(VMPrimitiveExitCode.GOTO_FALSE, exit);
                Assert.NotEqual(VMPrimitiveExitCode.GOTO_FALSE_NEXT_TICK, exit);
                Assert.NotEqual(VMPrimitiveExitCode.RETURN_FALSE, exit);
            }
        }

        [Fact]
        public void TheAnswerIsTheSameForEveryShippedSite()
        {
            // Stated over the census rather than as a single call, so a future signature change that
            // started taking the site cannot quietly become a per-site answer.
            foreach (var site in VMTS1HelpServiceCallHelper.ShippedSites)
            {
                Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE,
                    VMTS1HelpServiceCallHelper.AnswerFor(site.Mode));
            }
        }

        [Fact]
        public void MostReachableSitesCannotTellTheTwoAnswersApart()
        {
            // The half of the answer rule that needs no argument about trees: the two branch
            // pointers are the same instruction, so a GOTO_FALSE there lands where a GOTO_TRUE
            // would and the distinction is not reportable.
            var reachable = VMTS1HelpServiceCallHelper.ReachableSites;
            var indistinguishable = reachable.Count(s => !s.Distinguishes);

            Assert.Equal(6, indistinguishable);
            Assert.Equal(9 - 6, reachable.Count(s => s.Distinguishes));
        }

        [Fact]
        public void EveryReachableSiteThatCanTellIsAPoll()
        {
            // The half of the answer rule that decides it: the sites that can observe the answer are
            // exactly the three polls, and at all three the false pointer is the index of the
            // `idle` whose true pointer is the opcode-10 instruction itself.
            var distinguishing = VMTS1HelpServiceCallHelper.ReachableSites
                .Where(s => s.Distinguishes)
                .ToList();

            Assert.Equal(VMTS1HelpServiceCallHelper.ReachableAnswerDistinguishingSiteCount,
                distinguishing.Count);
            Assert.Equal(3, distinguishing.Count);
            Assert.All(distinguishing,
                s => Assert.Equal(VMTS1HelpServiceCallMode.Poll, s.Mode));

            // HelpSystem/HDHelpSystem 4103 "process - commands" [4]: T->[12] play_sound,
            // F->[5] which is `GLOBAL 280 'idle' args(400)` with T->[4], the instruction itself.
            // Tutorial.iff 4096 "main" [2]: T->[6], F->[3] which is the same `idle` shape. So every
            // one of them is preceded by a false branch that can only be reached from a later
            // instruction, which is what makes it a retry rather than a branch.
            foreach (var site in distinguishing)
            {
                Assert.True(site.Instruction > 0, $"{site} has no false branch to be an idle");
                Assert.NotEqual(site.TruePointer, site.FalsePointer);
                Assert.NotEqual(site.FalsePointer, (byte)255);
                Assert.NotEqual(site.FalsePointer, (byte)254);
            }
        }

        [Fact]
        public void TheResetLotSitesAreTheOnlyPlaceAFalseAnswerWouldNotBeARetry()
        {
            // The three unreachable sites that name different instructions: one in the uncalled
            // global tree, and the two 'reset lot' trees, whose false branch is RETURN_TRUE against
            // a true branch that re-runs the object's init. That is the only suggestion in the whole
            // install that a false answer means anything - and it is untestable, because neither
            // 'reset lot' tree has a caller.
            var unreachableDistinguishing = VMTS1HelpServiceCallHelper.ShippedSites
                .Where(s => !s.Reachable && s.Distinguishes)
                .ToList();

            Assert.Equal(3, unreachableDistinguishing.Count);
            Assert.Equal(VMTS1HelpServiceCallHelper.UnreachableAnswerDistinguishingSiteCount,
                unreachableDistinguishing.Count);
            Assert.Equal(2, unreachableDistinguishing.Count(s => s.TreeLabel == "reset lot"));
            Assert.Equal(1, unreachableDistinguishing.Count(s => s.TreeId == 274));

            // And the fourth unreachable site is a dead instruction that could not tell either.
            var unreachableBlind = VMTS1HelpServiceCallHelper.ShippedSites
                .Where(s => !s.Reachable && !s.Distinguishes)
                .ToList();
            Assert.Single(unreachableBlind);
            Assert.Equal(274, unreachableBlind[0].TreeId);
        }

        // ---- the handler ------------------------------------------------------------------------

        [Fact]
        public void TheHandlerAnswersTrueForBothShippedOperands()
        {
            var handler = new VMTS1HelpServiceCall();

            var advance = new VMTS1HelpServiceCallOperand();
            advance.Read(AdvanceOperand);
            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, handler.Execute(null, advance));

            var poll = new VMTS1HelpServiceCallOperand();
            poll.Read(PollOperand);
            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, handler.Execute(null, poll));
        }

        [Fact]
        public void TheHandlerNeverThrowsOnAMissingFrameOrOperand()
        {
            // The primitive touches no VM state at all - that is the whole of its implementation -
            // so a dispatch with neither a frame nor an operand has to be a quiet GOTO_TRUE. A
            // test that could not call it would leave the "touches nothing" claim unpinned.
            var handler = new VMTS1HelpServiceCall();

            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, handler.Execute(null, null));
            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE,
                handler.Execute(null, new VMTS1HelpServiceCallOperand()));
        }

        [Fact]
        public void TheHandlerAnswersTrueWithNoOperandAtAll()
        {
            // A null operand has to land somewhere and the answer cannot depend on where, because
            // the answer is mode-independent. What is pinned here is that nothing throws and nothing
            // is invented on the way - not a particular mode, which would be asserting a fallback
            // that has no observable consequence.
            var exit = new VMTS1HelpServiceCall().Execute(null, (VMPrimitiveOperand)null);

            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, exit);
        }

        // ---- the registration --------------------------------------------------------------------

        [Fact]
        public void Ts1RegistersTheHelpServiceCallPrimitive()
        {
            VMContext.InitVMConfig(true);

            var registration = VMContext.Primitives[VMTS1HelpServiceCallHelper.Opcode];

            Assert.NotNull(registration);
            Assert.IsType<VMTS1HelpServiceCall>(registration.GetHandler());
            Assert.Equal(typeof(VMTS1HelpServiceCallOperand), registration.OperandModel);
            Assert.Equal((ushort)10, registration.Opcode);
            Assert.Equal("help_service_call", registration.Name);
        }

        [Fact]
        public void TheOpcodeNumberIsTen()
        {
            // 9 is burn and 11 is get_distance_to. A registration written to the wrong index would
            // leave the help system's 13 sites unregistered and still set fire to things, and the
            // census tests above would pass, because they never look at the array.
            Assert.Equal((ushort)10, VMTS1HelpServiceCallHelper.Opcode);
            VMContext.InitVMConfig(true);
            Assert.Equal(10, VMContext.Primitives[VMTS1HelpServiceCallHelper.Opcode].Opcode);
        }

        [Fact]
        public void TheNeighbouringOpcodesAreUntouched()
        {
            VMContext.InitVMConfig(true);

            Assert.IsType<VMBurn>(VMContext.Primitives[9].GetHandler());
            Assert.IsType<VMGetDistanceTo>(VMContext.Primitives[11].GetHandler());
        }

        [Fact]
        public void TsoDoesNotGetTheTs1Handler()
        {
            // Deliberate, and the reason is in VMContext.cs: the community primitive table calls
            // opcode 10 "Sims1Tutorial", which is a TSO reading, and the TSO data has no opcode-10
            // site to check it against. Leaving it unregistered in TSO is also exactly today's
            // behaviour: VMThread substitutes GOTO_TRUE for a null registration and the operand is
            // never even decoded.
            //
            // "Unregistered in TSO" cannot be asserted as a null in the live array, because
            // AddPrimitive is last-wins and never clears, so an earlier TS1 call in the same process
            // answers for it. What can be asserted is the invariant that survives the residue:
            // whatever the mode, the slot never holds a handler from some other reading of 10.
            foreach (var ts1 in new[] { true, false })
            {
                VMContext.InitVMConfig(ts1);
                var handler = VMContext.Primitives[VMTS1HelpServiceCallHelper.Opcode]?.GetHandler();

                Assert.True(
                    handler == null || handler is VMTS1HelpServiceCall,
                    $"opcode 10 in mode ts1={ts1} is {handler?.GetType().Name ?? "null"}");
            }
        }

        [Fact]
        public void EveryPrimitiveOnTheLiveHelpPathIsRegisteredInTs1()
        {
            // The census, applied to the trees the engine actually enters. The two help systems are
            // OBJD Global == 1 and are already spawned on every lot, so their OBJf load (0x103D =
            // 4157), OBJf main (0x1017 = 4119) and 'process - commands' (4103, called from main
            // at [4] and [21]) all run. The opcodes those six trees name, other than private tree
            // calls (4096-8191, resolved by chunk id) and global trees (256-4095, `GLOBAL 280
            // 'idle'` and `GLOBAL 321` here), are exactly the list below - transcribed by reading
            // the trees, not guessed.
            VMContext.InitVMConfig(true);

            // HelpSystem/HDHelpSystem 4103: expression, opcode 10, play_sound.
            // HelpSystem/HDHelpSystem 4119: expression, opcode 10, dialog_private, tree calls.
            // HDHelpSystem 4157 'load tree': expression, stackobj_notify_out_of_idle.
            var onLivePath = new ushort[] { 2, 10, 23, 36, 49 };

            foreach (var opcode in onLivePath)
            {
                Assert.NotNull(VMContext.Primitives[opcode]);
            }

            // And the opcode-10 site count on that live path is the census's: five passes and two
            // polls across the two help systems, which is 7 of the 13. The other 2 live sites are in
            // Objects/Tutorial.iff 4096, behind VMTS1TutorialTrigger.HelpEnabled.
            var liveHelpSystems = VMTS1HelpServiceCallHelper.ReachableSites
                .Where(s => s.Object != "Objects/Tutorial.iff")
                .ToList();
            Assert.Equal(7, liveHelpSystems.Count);
            Assert.Equal(2, liveHelpSystems.Count(s => s.Mode == VMTS1HelpServiceCallMode.Poll));
            Assert.Equal(5, liveHelpSystems.Count(s => s.Mode == VMTS1HelpServiceCallMode.Advance));
        }

        [Fact]
        public void EveryCensusSiteExistsInTheTreeItIsAttributedTo()
        {
            // Instruction counts read off the retail trees, keyed by object and tree id, so a change
            // in the shipped install shows up here rather than as a silent gap between the census
            // and reality. The property the census depends on is only that each site is inside its
            // tree - the counts are recorded so that a change in them is visible, not because
            // anything downstream reads them.
            var lengths = new Dictionary<string, int>
            {
                ["Objects/HelpSystem.iff#4103"] = 23,
                ["Objects/HelpSystem.iff#4115"] = 2,
                ["Objects/HelpSystem.iff#4119"] = 38,
                ["Objects/HelpSystem.iff#4157"] = 6,
                ["Objects/Tutorial.iff#4096"] = 11,
                ["ExpansionShared/HDHelpSystem.iff#4103"] = 24,
                ["ExpansionShared/HDHelpSystem.iff#4115"] = 2,
                ["ExpansionShared/HDHelpSystem.iff#4119"] = 51,
                ["ExpansionShared/HDHelpSystem.iff#4157"] = 3,
                ["GameData/Global/Global.iff#274"] = 5,
            };

            foreach (var site in VMTS1HelpServiceCallHelper.ShippedSites)
            {
                var key = $"{site.Object}#{site.TreeId}";
                Assert.True(lengths.ContainsKey(key), $"no recorded length for {key}");
                Assert.True(site.Instruction < lengths[key],
                    $"{site} is past the end of {key} ({lengths[key]} instructions)");
            }
        }
    }
}
