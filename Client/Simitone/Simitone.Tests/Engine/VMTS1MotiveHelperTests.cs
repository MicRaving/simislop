using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// SimAntics primitive 33, <b>"find 5 worst motives"</b> - the ranking in
    /// <see cref="VMTS1MotiveHelper"/> and the registration in <see cref="VMContext"/>.
    ///
    /// <para>
    /// This primitive is the one gap in the TS1 call/opcode surface that the last audit of the
    /// five progression chains found. <c>VMContext.cs</c> had carried a bare
    /// <c>//TODO: find 5 worst motives</c> between opcodes 32 and 35 since the file was written,
    /// and because <c>VMTranslator.PopulateRoutineFields</c> leaves the operand null and
    /// <c>VMThread.ExecuteInstruction</c> substitutes <c>GOTO_TRUE</c> for an unregistered
    /// opcode, the failure was silent: no crash, no log line, just
    /// <c>Objects.far &rarr; PedPortal.iff</c> BHAV 4107 <c>"show leave dialog"</c> reading a
    /// <c>Temp0</c> it had never been given.
    /// </para>
    ///
    /// <para>
    /// What is pinned here, and why each of these is a test rather than a comment:
    ///
    ///   * <b>The candidate set is the eight needs and nothing else.</b> That is the whole
    ///     contract: the only shipped consumer switches on <c>Temp0</c> against 5, 6, 7, 8, 9,
    ///     13, 14 and 15, and widening the set re-creates the exact silence this primitive
    ///     fixes. A property test asserts the two sides are the same set, so an edit to either
    ///     half alone fails.
    ///   * <b>Exactly five ids come out.</b> The primitive writes <c>Temp0..Temp4</c>
    ///     unconditionally and <c>PedPortal.iff</c> reads <c>Temp3</c>/<c>Temp4</c> in its
    ///     3-deep anti-repeat buffer at [22]-[24], so a short answer would leave another
    ///     caller's values in place. A short candidate list must pad, not truncate.
    ///   * <b>Ties are broken by motive id.</b> "Ordered by lowest first" is the entire
    ///     specification Maxis left, so without an explicit tie-break the answer is a coin flip
    ///     and the anti-repeat buffer it feeds is worthless.
    ///   * <b>Degraded input widens, never empties.</b> A corrupt motive-type byte, or no motive
    ///     reader at all, must produce zeros rather than throw - a primitive that throws takes
    ///     the sim's whole thread with it.
    ///   * <b>The registration exists in both modes.</b> Opcode 33 is not TS1-only (it is a TSO
    ///     primitive whose two call sites happen to be in TS1 data), and the cheap way to
    ///     "fix" it in future is to register it inside the <c>if (ts1)</c> branch, where it
    ///     would silently stop working for TSO objects. A test per mode is the guard.
    /// </para>
    /// </summary>
    public sealed class VMTS1MotiveHelperTests
    {
        /// <summary>
        /// The literals <c>PedPortal.iff</c> BHAV 4107 <c>[3]</c>-<c>[10]</c> compares
        /// <c>Temp0</c> against, transcribed from the shipped bytes. Written out longhand rather
        /// than derived from <c>VMTS1MotiveHelper.Needs</c> on purpose: the point of the test is
        /// that the two agree, which is only a test if one of them is independent.
        /// </summary>
        private static readonly int[] PedPortalLeaveReasons = { 5, 6, 7, 8, 9, 13, 14, 15 };

        // ---- the candidate set ---------------------------------------------------------------

        [Fact]
        public void TheCandidateSetIsExactlyTheEightMotiveIdsTheShippedConsumerCanActOn()
        {
            // Objects.far -> PedPortal.iff 4107 'show leave dialog' [3]-[10] is the only tree in
            // the retail install that reads this primitive's answer, and it tests exactly these
            // eight literals. Every one of them is a <see cref="VMMotive"/> need; none of the
            // array's other eight slots (0-4, 10-12) appears, because a TS1 lot never writes
            // them, so letting them compete would win every comparison on garbage and put the
            // eight shipped branches straight back out of reach.
            Assert.Equal(PedPortalLeaveReasons,
                VMTS1MotiveHelper.Needs.Select(m => (int)m).ToArray());
        }

        [Fact]
        public void TheCandidateSetIsAscendingAndHoldsNoDuplicates()
        {
            // The id order is the tie-break (see FindWorstMotivesTiesBreakByAscendingMotiveId), so
            // it has to be a total order, and a duplicate would silently weight one motive twice.
            var ids = VMTS1MotiveHelper.Needs.Select(m => (int)m).ToArray();
            Assert.Equal(ids.OrderBy(x => x).ToArray(), ids);
            Assert.Equal(ids.Distinct().Count(), ids.Length);
        }

        [Fact]
        public void EveryCandidateIsInsideTheEngineMotiveArray()
        {
            // VMAvatar.GetMotiveData throws above 15, and every helper lookup is fed by it, so a
            // candidate outside 0..15 would be a guaranteed throw the first time a sim's motives
            // are read.
            foreach (var motive in VMTS1MotiveHelper.Needs)
            {
                Assert.InRange((int)motive, 0, 15);
            }
        }

        [Fact]
        public void TheMentalAndPhysicalSplitsAreDisjointAndTogetherMakeTheWholeSet()
        {
            // No shipped site uses motive type 1 or 2 (both call sites pass 0), so this is the
            // only structural guarantee available for them: a filter that can never return a
            // motive the all-motive query would have returned is a filter nobody can debug.
            var mental = VMTS1MotiveHelper.MentalNeeds.Select(m => (int)m).ToArray();
            var physical = VMTS1MotiveHelper.PhysicalNeeds.Select(m => (int)m).ToArray();

            Assert.Empty(mental.Intersect(physical));
            Assert.Equal(
                VMTS1MotiveHelper.Needs.Select(m => (int)m).OrderBy(x => x).ToArray(),
                mental.Concat(physical).OrderBy(x => x).ToArray());
        }

        [Theory]
        [InlineData(VMTS1MotiveHelper.MotiveTypeAll)]
        [InlineData(VMTS1MotiveHelper.MotiveTypeMental)]
        [InlineData(VMTS1MotiveHelper.MotiveTypePhysical)]
        [InlineData(3)]     // undocumented
        [InlineData(255)]   // corrupt
        public void EveryMotiveTypeIsAscendingAndDeduplicated(byte motiveType)
        {
            // An unknown type must widen to the full set rather than empty it - see
            // AnUnknownMotiveTypeFallsBackToEveryMotive - and every list must be in the same
            // ascending order so the ranking is stable across a change of type.
            var ids = VMTS1MotiveHelper.Candidates(motiveType).Select(m => (int)m).ToArray();
            Assert.Equal(ids.OrderBy(x => x).ToArray(), ids);
            Assert.Equal(ids.Distinct().Count(), ids.Length);
            Assert.NotEmpty(ids);
        }

        // ---- the ranking ---------------------------------------------------------------------

        [Fact]
        public void FindWorstMotivesReturnsTheFiveLowestInAscendingOrder()
        {
            // Energy 5 = 10, Comfort 6 = 40, Hunger 7 = 90, Hygiene 8 = 20, Bladder 9 = 30,
            // Room 13 = 50, Social 14 = 5, Fun 15 = 70. Lowest five: Social, Energy, Hygiene,
            // Bladder, Comfort.
            var byId = new Dictionary<VMMotive, short>
            {
                { VMMotive.Energy, 10 },
                { VMMotive.Comfort, 40 },
                { VMMotive.Hunger, 90 },
                { VMMotive.Hygiene, 20 },
                { VMMotive.Bladder, 30 },
                { VMMotive.Room, 50 },
                { VMMotive.Social, 5 },
                { VMMotive.Fun, 70 }
            };

            Assert.Equal(new short[] { 14, 5, 8, 9, 6 },
                VMTS1MotiveHelper.FindWorstMotives(VMTS1MotiveHelper.MotiveTypeAll, byId.GetValueOrDefault));
        }

        [Fact]
        public void FindWorstMotivesIsOrderedByValueNotByMotiveId()
        {
            // The whole point of the primitive: the sim's most urgent need first, whatever slot it
            // happens to live in. Every motive reads the same value, so the order must be the
            // candidate order - i.e. this also fails if the sort is dropped entirely.
            var flat = new Func<VMMotive, short>(_ => 50);

            Assert.Equal(new short[] { 5, 6, 7, 8, 9 }, VMTS1MotiveHelper.FindWorstMotives(0, flat));
        }

        [Fact]
        public void FindWorstMotivesTiesBreakByAscendingMotiveId()
        {
            // Maxis documented "ordered by lowest first" and nothing else. Without a stated
            // tie-break this is a coin flip, and a coin flip feeding PedPortal.iff's 3-deep
            // anti-repeat buffer at [22]-[24] means the same leave reason can be picked four
            // times running.
            var flat = new Func<VMMotive, short>(_ => 100);

            var first = VMTS1MotiveHelper.FindWorstMotives(0, flat);
            var second = VMTS1MotiveHelper.FindWorstMotives(0, flat);

            Assert.Equal(new short[] { 5, 6, 7, 8, 9 }, first);
            Assert.Equal(first, second);
        }

        [Fact]
        public void FindWorstMotivesStopsAtFiveEvenWithEightCandidates()
        {
            // Only the five lowest are reported. Room 13, Social 14 and Fun 15 are all 0 here and
            // so are the three worst, but they are sixth, seventh and eighth and must not appear.
            var byId = new Dictionary<VMMotive, short>
            {
                { VMMotive.Energy, 80 },
                { VMMotive.Comfort, 80 },
                { VMMotive.Hunger, 80 },
                { VMMotive.Hygiene, 80 },
                { VMMotive.Bladder, 80 },
                { VMMotive.Room, 0 },
                { VMMotive.Social, 0 },
                { VMMotive.Fun, 0 }
            };

            Assert.Equal(new short[] { 13, 14, 15, 5, 6 },
                VMTS1MotiveHelper.FindWorstMotives(0, byId.GetValueOrDefault));
        }

        [Fact]
        public void FindWorstMotivesPadsWithZeroRatherThanReturningFewerThanFive()
        {
            // The primitive writes Temp0..Temp4 unconditionally, and PedPortal.iff [22]-[24]
            // rotates Temp0..Temp3. A two-element answer would leave the caller's Temp3/Temp4 in
            // place, so a short list has to pad to five with 0.
            var mental = VMTS1MotiveHelper.FindWorstMotives(
                VMTS1MotiveHelper.MotiveTypeMental,
                m => m == VMMotive.Fun ? (short)5 : (short)90);

            Assert.Equal(VMTS1MotiveHelper.WorstMotiveCount, mental.Length);
            Assert.Equal(new short[] { 15, 14, 0, 0, 0 }, mental);
        }

        [Fact]
        public void AMentalQueryNeverReturnsAPhysicalMotiveAndViceVersa()
        {
            // Deliberately absurd values (some negative, the engine clamps to [-100, 100]) so the
            // filter cannot pass by accident on data that happens to be ordered anyway.
            var byId = new Dictionary<VMMotive, short>
            {
                { VMMotive.Energy, 100 },
                { VMMotive.Comfort, 100 },
                { VMMotive.Hunger, 100 },
                { VMMotive.Hygiene, 100 },
                { VMMotive.Bladder, 100 },
                { VMMotive.Room, 100 },
                { VMMotive.Social, -100 },
                { VMMotive.Fun, -50 }
            };

            var mental = VMTS1MotiveHelper.FindWorstMotives(VMTS1MotiveHelper.MotiveTypeMental, byId.GetValueOrDefault);
            Assert.Equal(new short[] { 14, 15, 0, 0, 0 }, mental);

            var physical = VMTS1MotiveHelper.FindWorstMotives(VMTS1MotiveHelper.MotiveTypePhysical, byId.GetValueOrDefault);
            Assert.Equal(new short[] { 5, 6, 7, 8, 9 }, physical);
        }

        [Fact]
        public void AnUnknownMotiveTypeFallsBackToEveryMotive()
        {
            // Widening is the safe direction: a narrow answer the tree has no branch for is the
            // silence this primitive exists to remove, so an undocumented byte must behave like
            // the documented "all".
            var byId = new Dictionary<VMMotive, short>
            {
                { VMMotive.Energy, 90 },
                { VMMotive.Comfort, 80 },
                { VMMotive.Hunger, 70 },
                { VMMotive.Hygiene, 60 },
                { VMMotive.Bladder, 50 },
                { VMMotive.Room, 40 },
                { VMMotive.Social, 30 },
                { VMMotive.Fun, 20 }
            };

            var expected = new short[] { 15, 14, 13, 9, 8 };

            Assert.Equal(expected, VMTS1MotiveHelper.FindWorstMotives(0, byId.GetValueOrDefault));
            Assert.Equal(expected, VMTS1MotiveHelper.FindWorstMotives(7, byId.GetValueOrDefault));
            Assert.Equal(expected, VMTS1MotiveHelper.FindWorstMotives(200, byId.GetValueOrDefault));
        }

        [Fact]
        public void NoMotiveReaderProducesZerosRatherThanThrowing()
        {
            // The one input a hand-edited or future primitive can hand us. A throw here would
            // unwind the sim's whole thread mid-interaction, which is strictly worse than
            // reporting no reason at all.
            var none = VMTS1MotiveHelper.FindWorstMotives(0, null);

            Assert.Equal(new short[] { 0, 0, 0, 0, 0 }, none);
        }

        [Fact]
        public void TheAnswerIsAlwaysFiveMotiveIds()
        {
            foreach (var motiveType in new byte[] { 0, 1, 2, 9 })
            {
                var result = VMTS1MotiveHelper.FindWorstMotives(motiveType, m => (short)((int)m));
                Assert.Equal(VMTS1MotiveHelper.WorstMotiveCount, result.Length);
                Assert.All(result, id => Assert.InRange((int)id, 0, 15));
            }
        }

        // ---- the operand ---------------------------------------------------------------------

        [Fact]
        public void TheAllZeroOperandBothShippedCallSitesUseMeansMeAndEveryMotive()
        {
        // PedPortal.iff 4107 [2] and HDSI_Nag.iff 4349 [24] are both
        // "02 00 <t> <f> 00 00 00 00 00 00 00 00" - eight zero operand bytes. Decoding them as
        // "target = me, motive type = all" is the only reading both sites can have had.
            var operand = new VMFind5WorstMotivesOperand();
            operand.Read(new byte[8]);

            Assert.Equal(VMTS1MotiveHelper.TargetCaller, operand.Target);
            Assert.Equal(VMTS1MotiveHelper.MotiveTypeAll, operand.MotiveType);
        }

        [Fact]
        public void TheOperandReadsTargetFromByteOneAndMotiveTypeFromByteTwo()
        {
        // The primitive's operand table is [unused][Target][MotiveType].... Both shipped sites
        // are all-zero, so the offsets are pinned here against the documented table rather than
        // against a sample; byte 0 is a documented spare and must not be read as either field.
            var operand = new VMFind5WorstMotivesOperand();
            operand.Read(new byte[] { 0xAB, VMTS1MotiveHelper.TargetStackObject, VMTS1MotiveHelper.MotiveTypeMental });

            Assert.Equal(VMTS1MotiveHelper.TargetStackObject, operand.Target);
            Assert.Equal(VMTS1MotiveHelper.MotiveTypeMental, operand.MotiveType);
        }

        [Fact]
        public void TheOperandRoundTrips()
        {
            var bytes = new byte[8];
            var written = new VMFind5WorstMotivesOperand
            {
                Target = VMTS1MotiveHelper.TargetStackObject,
                MotiveType = VMTS1MotiveHelper.MotiveTypePhysical
            };
            written.Write(bytes);

            var read = new VMFind5WorstMotivesOperand();
            read.Read(bytes);

            Assert.Equal(VMTS1MotiveHelper.TargetStackObject, read.Target);
            Assert.Equal(VMTS1MotiveHelper.MotiveTypePhysical, read.MotiveType);
        }

        [Fact]
        public void ATruncatedOperandReadsAsAllZeroRatherThanThrowing()
        {
            // A BHAV can hand a primitive fewer bytes than its operand model declares (the
            // translator passes the raw array straight through), and IoBuffer would throw on the
            // third ReadByte. The caller is a departing sim's interaction; it must not die of an
            // arithmetic detail.
            var operand = new VMFind5WorstMotivesOperand();
            operand.Read(new byte[0]);

            Assert.Equal(VMTS1MotiveHelper.TargetCaller, operand.Target);
            Assert.Equal(VMTS1MotiveHelper.MotiveTypeAll, operand.MotiveType);
        }

        // ---- the registration ----------------------------------------------------------------

        [Theory]
        [InlineData(true)]   // TS1
        [InlineData(false)]  // TSO
        public void OpcodeThirtyThreeIsRegisteredInBothModes(bool ts1)
        {
        // 33 is a TSO primitive whose only callers are TS1 trees, so the cheap way to break it
        // later is to register it inside the "if (ts1)" branch of InitVMConfig - which would
        // leave the TS1 audit green and quietly unregister it for TSO objects. The opcode
        // number is checked as well as the handler, because an unregistered opcode does not
        // fail: VMThread substitutes GOTO_TRUE, which is exactly the bug this fixes.
            VMContext.InitVMConfig(ts1);

            var registration = VMContext.Primitives[33];

            Assert.NotNull(registration);
            Assert.IsType<VMFind5WorstMotives>(registration.GetHandler());
            Assert.Equal(typeof(VMFind5WorstMotivesOperand), registration.OperandModel);
        }
    }
}
