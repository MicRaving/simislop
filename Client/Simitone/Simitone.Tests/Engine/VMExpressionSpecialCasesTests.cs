using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Engine.Primitives;
using FSO.SimAntics.Engine.Scopes;
using FSO.SimAntics.Model;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// Two special cases inside <see cref="VMExpression.Execute"/>, the hottest primitive in the
    /// engine: the stair <c>MyObject[29] vs 1024</c> substitution, and the flag bit number.
    ///
    /// <para>
    /// <b>The stair substitution.</b> A retail stair tests <c>s3[29] &lt; s7[1024]</c> or
    /// <c>s3[29] &gt;= s7[1024]</c> - <c>MyObject[29]</c> is the room - meaning "is my room below
    /// (or at/above) the stairs' own room", which is how a stair routes a sim to the right side.
    /// FreeSO does not decode room 1024 that way, so the comparison is replaced wholesale by a
    /// level one. <b>A room comparison is being replaced, not corrected</b> - this is not a fix for
    /// room decoding, and reading it as one is how the next person "improves" it away. It is
    /// needed: 12 shipped sites, all stairs (Objects/stair.iff 4124/4125, ExpansionPack/Stair2.iff,
    /// ExpansionPack2/stair3.iff, ExpansionPack4/Stair4.iff, ExpansionPack5/StairsSpiral.iff
    /// 4124/4125 plus 4113 and 4097 'goto other side'). The comment's "only used in stairs" is
    /// right, which is what makes it safe to leave alone rather than to remove.
    /// </para>
    ///
    /// <para>
    /// The finding's crash was that the expression dereferenced <c>context.Callee</c> with no
    /// null check, in every frame in the engine that ran that shape - and a frame with no callee
    /// (a check tree, a restored TS1 frame whose callee is gone) is entirely ordinary. With no
    /// callee the substitution cannot be computed, so the plain comparison runs instead. The
    /// finding's other half was duplication: the expression was written out twice, once per
    /// operator, and the two copies of "which way round does the level delta go" could drift. There
    /// is now one, and the tests below assert the two operators cannot disagree.
    /// </para>
    ///
    /// <para>
    /// <b>The flag bit.</b> The word is a <c>short</c>, so bit N is <c>1 &lt;&lt; (N - 1)</c> and
    /// N lives in 1..16. The bit number is a script register, and it used to be shifted
    /// unchecked: <c>flagSet 0</c> became <c>1 &lt;&lt; 31</c> (bit 15 after the truncation to a
    /// short) and <c>flagSet 33</c> became <c>1 &lt;&lt; 0</c>, i.e. silent corruption of an
    /// unrelated flag. This is the word <c>VM.GameEditionGlobalValue</c> is built on, and the
    /// engine's own API for it (<c>VM.IsObjectFlagBit</c> / <c>VM.SetObjectFlag</c>) already
    /// validated the number; the primitive - the only path content can reach - now does too.
    /// </para>
    /// </summary>
    public sealed class VMExpressionSpecialCasesTests
    {
        private const int RoomVariable = (int)VMStackObjectVariable.Room;
        private const int StairRoomThreshold = 1024;

        /// <summary>
        /// An entity at a known level and in a known room, built without its constructor - which
        /// wants a marshal, a lot and a content manager. <c>Position</c>'s setter also reaches for
        /// the world (<c>VM.UseWorld</c> is a process-global true by default, so it would write
        /// through a null <c>WorldUI</c>), so the backing field is written directly; that is all
        /// the substitution reads.
        /// </summary>
        private static VMEntity AtLevel(int level, ushort levelOffset = 0, short room = 3, short objectId = 0)
        {
            var gob = (VMGameObject)RuntimeHelpers.GetUninitializedObject(typeof(VMGameObject));
            gob.ObjectData = new short[80];
            gob.ObjectData[RoomVariable] = room;
            gob.ObjectID = objectId;
            gob.Object = new GameObject { OBJ = new OBJD { LevelOffset = levelOffset } };
            SetPosition(gob, new LotTilePos(0, 0, (sbyte)level));
            return gob;
        }

        private static void SetPosition(VMEntity entity, LotTilePos pos)
        {
            typeof(VMEntity)
                .GetField("_Position", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(entity, pos);
        }

        /// <summary>The retail stair expression: <c>MyObject[29] &lt;|&gt;= 1024</c>.</summary>
        private static VMExpressionOperand StairTest(VMExpressionOperator op, short room = 3)
        {
            return new VMExpressionOperand
            {
                LhsOwner = VMVariableScope.MyObject,
                LhsData = (short)RoomVariable,
                RhsOwner = VMVariableScope.Literal,
                RhsData = (short)StairRoomThreshold,
                Operator = op,
            };
        }

        private static VMPrimitiveExitCode Evaluate(VMStackFrame frame, VMExpressionOperand operand)
            => new VMExpression().Execute(frame, operand);

        // ---- the stair substitution, when both sides of it exist -------------------------

        [Theory]
        [InlineData(2, 1, 0, false)]  // caller on level 2, stairs on level 1: the caller is above
        [InlineData(1, 1, 0, true)]   // same level: the delta is 0, which is "<= 0"
        [InlineData(1, 2, 0, true)]   // caller below the stairs
        public void TheLessThanSubstitutionIsALevelComparison(int callerLevel, int calleeLevel, ushort levelOffset, bool expected)
        {
            var frame = new VMStackFrame { Caller = AtLevel(callerLevel), Callee = AtLevel(calleeLevel, levelOffset) };

            Assert.Equal(expected.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.LessThan)));
        }

        [Theory]
        [InlineData(2, 1, 0, true)]   // caller on level 2, stairs on level 1: the delta is > 0
        [InlineData(1, 1, 0, false)]
        [InlineData(1, 2, 0, false)]
        public void TheGreaterThanOrEqualToSubstitutionIsTheSameDeltaTheOtherWayRound(int callerLevel, int calleeLevel, ushort levelOffset, bool expected)
        {
            var frame = new VMStackFrame { Caller = AtLevel(callerLevel), Callee = AtLevel(calleeLevel, levelOffset) };

            Assert.Equal(expected.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.GreaterThanOrEqualTo)));
        }

        [Fact]
        public void TheRoomIdItselfIsNeverLookedAtWhenTheSubstitutionApplies()
        {
            // What the substitution is: the comparison the script asked for is discarded, and a
            // level comparison answered in its place. Pinned so nobody reads it as a fix for how
            // room 1024 decodes - and so a "fix" to decode it is a deliberate change, not a
            // tidying-up.
            foreach (var room in new short[] { 0, 1, 3, 500, 1023, 1024, 2000 })
            {
                var frame = new VMStackFrame { Caller = AtLevel(1, room: room), Callee = AtLevel(1) };

                Assert.Equal(true.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.LessThan, room)));
            }
        }

        [Fact]
        public void TheStairsOwnLevelOffsetCountsAgainstTheCaller()
        {
            // A floating stair's OBJ.LevelOffset is subtracted from its own level, so a stair whose
            // position is level 1 with an offset of 1 counts as level 0. Without the term it would
            // look a storey higher than it is and route everyone the wrong way - and this is the
            // only place the offset is used.
            var stairFloating = new VMStackFrame { Caller = AtLevel(1), Callee = AtLevel(1, 1) };
            var stairGrounded = new VMStackFrame { Caller = AtLevel(1), Callee = AtLevel(1, 0) };

            // delta = 1 - (1 - 1) = 1  -> the caller is above a stair that claims to be below ground
            Assert.Equal(false.AsGotoExitCode(), Evaluate(stairFloating, StairTest(VMExpressionOperator.LessThan)));
            // delta = 1 - (1 - 0) = 0  -> level with a stair that claims to be on this level
            Assert.Equal(true.AsGotoExitCode(), Evaluate(stairGrounded, StairTest(VMExpressionOperator.LessThan)));
        }

        // ---- the crash: a frame with no callee -------------------------------------------

        [Fact]
        public void AFrameWithNoCalleeComparesTheRoomIdsInsteadOfThrowing()
        {
            // The finding. This is the hottest primitive in the engine and it dereferenced
            // context.Callee.Position.Level - and Callee - for every frame that ran the shape. A
            // check tree, and a restored TS1 frame whose callee is gone, both have no callee.
            var frame = new VMStackFrame { Caller = AtLevel(3, room: 500) };

            Assert.Null(frame.Callee);
            Assert.Equal(true.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.LessThan)));
            Assert.Equal(false.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.GreaterThanOrEqualTo)));
        }

        [Fact]
        public void AFrameWithNoCalleeStillComparesTheRealRoomIds()
        {
            // ... and the comparison it falls back to is the real one, not a constant.
            var frame = new VMStackFrame { Caller = AtLevel(3, room: 2000) };

            Assert.Equal(false.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.LessThan)));
            Assert.Equal(true.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.GreaterThanOrEqualTo)));
        }

        // ---- when the substitution must NOT apply ----------------------------------------

        [Theory]
        [InlineData((short)1024, true)]   // the stair substitution: the caller is below the stairs
        [InlineData((short)1023, false)]
        [InlineData((short)1025, false)]
        [InlineData((short)500, false)]
        [InlineData((short)3000, true)]
        public void OnlyTheLiteralRoom1024TriggersTheSubstitution(short rightRoom, bool expected)
        {
            // The substitution is keyed on the *literal* 1024, because that is the room the stair
            // trees compare against, and on the variable being MyObject[29]. Every other literal is
            // a real room id and gets compared - and the caller is in room 2000, above all of them,
            // so here the plain comparison and the substitution disagree on purpose.
            var frame = new VMStackFrame { Caller = AtLevel(1, room: 2000), Callee = AtLevel(3) };
            var operand = StairTest(VMExpressionOperator.LessThan);
            operand.RhsData = rightRoom;

            Assert.Equal(expected.AsGotoExitCode(), Evaluate(frame, operand));
        }

        [Fact]
        public void TheRoomOfSomebodyElseIsNotTheStairSubstitution()
        {
            // The expression is about MyObject. A StackObject[29] against 1024 is a real
            // comparison - the stack object's room here is above the threshold, and the
            // substitution would have answered from the caller's and the stairs' levels.
            var frame = new VMStackFrame
            {
                Caller = AtLevel(1),
                Callee = AtLevel(3),
                StackObject = AtLevel(1, room: 2000),
            };
            var operand = StairTest(VMExpressionOperator.LessThan);
            operand.LhsOwner = VMVariableScope.StackObject;

            Assert.Equal(false.AsGotoExitCode(), Evaluate(frame, operand));
        }

        [Fact]
        public void AVersionOfTheVariableThatIsNotTheRoomIsNotTheSubstitution()
        {
            var frame = new VMStackFrame { Caller = AtLevel(1, objectId: 2000), Callee = AtLevel(3) };
            var operand = StairTest(VMExpressionOperator.LessThan);
            operand.LhsData = (short)VMStackObjectVariable.ObjectId;

            Assert.Equal(false.AsGotoExitCode(), Evaluate(frame, operand));
        }

        [Fact]
        public void OperatorsOtherThanTheTwoStairOnesAreNeverSubstituted()
        {
            // The substitution exists for `<` and `>=` only. `==` against 1024 in a stair tree is
            // asking a real question, and answering it with a level comparison would be worse than
            // the bug.
            var frame = new VMStackFrame { Caller = AtLevel(1, room: 3), Callee = AtLevel(3) };

            Assert.Equal(false.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.Equals)));
            Assert.Equal(true.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.NotEqualTo)));
            Assert.Equal(false.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.GreaterThan)));
            Assert.Equal(true.AsGotoExitCode(), Evaluate(frame, StairTest(VMExpressionOperator.LessThanOrEqualTo)));
        }

        [Fact]
        public void BothStairOperatorsAgreeOnTheSameComparison()
        {
            // `<` and `>=` are the two halves of one question and must not be able to drift. For
            // every pair of levels exactly one of them is true, which is the statement the
            // duplicated expression could not make.
            for (int caller = 1; caller <= 3; caller++)
            {
                for (int callee = 1; callee <= 3; callee++)
                {
                    var frame = new VMStackFrame { Caller = AtLevel(caller), Callee = AtLevel(callee) };

                    var below = Evaluate(frame, StairTest(VMExpressionOperator.LessThan)) == VMPrimitiveExitCode.GOTO_TRUE;
                    var above = Evaluate(frame, StairTest(VMExpressionOperator.GreaterThanOrEqualTo)) == VMPrimitiveExitCode.GOTO_TRUE;

                    Assert.True(below != above,
                        $"caller {caller} vs stairs {callee}: < is {below} and >= is {above}");
                }
            }
        }

        // ---- the flag bit number ---------------------------------------------------------

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(8, 128)]
        [InlineData(9, 256)]      // the first of the object-private window in Global[20]
        [InlineData(15, 1 << 14)]
        [InlineData(16, 1 << 15)]
        public void BitNIsOneShiftedByNMinusOne(int bitNumber, int expectedMask)
        {
            // `flagSet N` means bit N, and the word is 16 bits wide, so the top answer is bit 16.
            Assert.True(VMExpression.TryGetFlagBit(bitNumber, out var mask));
            Assert.Equal(expectedMask, mask);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(17)]
        [InlineData(33)]          // used to alias to bit 0 and set it
        [InlineData(32)]          // used to alias to bit 1
        [InlineData(int.MaxValue)]
        public void ABitNumberWithNoBitBehindItIsRefused(int bitNumber)
        {
            // The silent corruption: `1 << (rhsValue - 1)` with an unvalidated register. 33 became
            // 1 << 0 and set the wrong bit of a word 1613 shipped instructions read.
            Assert.False(VMExpression.TryGetFlagBit(bitNumber, out var mask));
            Assert.Equal(0, mask);
        }

        [Fact]
        public void EveryBitTheShippedDataCanAddressHasAMask()
        {
            // 1..16, exhaustively, so the window is stated rather than implied.
            for (int bit = 1; bit <= 16; bit++)
            {
                Assert.True(VMExpression.TryGetFlagBit(bit, out var mask));
                Assert.Equal(1 << (bit - 1), mask);
            }
        }

        [Fact]
        public void TheMaskIsTheOneTheEngineOwnsApiAlreadyUses()
        {
            // VM.ObjectFlagBit is `1 << (bitNumber - 1)` over the same 16-bit word, and
            // VM.IsObjectFlagBit is the check the primitive now shares the bound with. If either
            // moves, the primitive and the engine's own API would disagree about what bit 9 is.
            for (short bit = 1; bit <= 16; bit++)
            {
                Assert.True(VMExpression.TryGetFlagBit(bit, out var mask));
                Assert.Equal(1 << (bit - 1), mask);
            }

            // The object-private half of the window is 9..16, which is what the primitive's upper
            // bound has to admit for Global[20] to be usable as documented.
            Assert.True(VM.IsObjectFlagBit(9));
            Assert.True(VM.IsObjectFlagBit(16));
            Assert.False(VM.IsObjectFlagBit(8));
        }

        // ---- SetFlag/ClearFlag actually moving the bit ------------------------------------

        /// <summary>
        /// A word on a stack object, without a constructor (which wants a marshal, a lot and a
        /// content manager). The primitive reads and writes it through <c>ObjectData</c>.
        /// </summary>
        private static VMGameObject StackObjectWithWord()
        {
            var gob = (VMGameObject)RuntimeHelpers.GetUninitializedObject(typeof(VMGameObject));
            gob.ObjectData = new short[80];
            return gob;
        }

        /// <summary>
        /// <c>StackObject[word] setFlag/clearFlag N</c>. The word is a reserved slot with no
        /// special handling, because the point under test is the bit arithmetic and not the
        /// object's flags; <c>VMStackObjectVariable.Flags</c> would reach for a world that a test
        /// process does not have.
        /// </summary>
        private static VMExpressionOperand FlagOp(VMExpressionOperator op, short bit)
        {
            return new VMExpressionOperand
            {
                LhsOwner = VMVariableScope.StackObject,
                LhsData = (short)VMStackObjectVariable.Reserved77,
                RhsOwner = VMVariableScope.Literal,
                RhsData = bit,
                Operator = op,
            };
        }

        [Fact]
        public void ClearFlagClearsTheBitTheScriptNamed()
        {
            // The regression. The food scripts set the meal's Occupied bit (bit 5) when the meal is
            // created and clear it with `clearFlag 6` when serving finishes. ClearFlag took
            // TryGetFlagBit's *mask* - 32 for bit 6 - and shifted it a second time, so it cleared
            // bit 32, which is no bit of a 16-bit word, and left bit 5 set. The meal stayed
            // Occupied, its check tree's `StackObj[8] flagSet 6` gate refused every time, and "Eat"
            // silently disappeared from the pie menu.
            var gob = StackObjectWithWord();
            var frame = new VMStackFrame { StackObject = gob };
            var word = (short)VMStackObjectVariable.Reserved77;

            Evaluate(frame, FlagOp(VMExpressionOperator.SetFlag, 6));
            Assert.Equal((short)VMEntityFlags.Occupied, gob.ObjectData[word]);

            Evaluate(frame, FlagOp(VMExpressionOperator.ClearFlag, 6));
            Assert.Equal(0, gob.ObjectData[word]);
        }

        [Fact]
        public void ClearFlagLeavesEveryOtherBitAlone()
        {
            // The same bug seen from the other side: the old shift also failed to touch bit 5, so
            // an all-bits-set word came back unchanged. Clearing one bit must clear exactly one.
            var gob = StackObjectWithWord();
            var frame = new VMStackFrame { StackObject = gob };
            var word = (short)VMStackObjectVariable.Reserved77;
            gob.ObjectData[word] = unchecked((short)0xFFFF);

            Evaluate(frame, FlagOp(VMExpressionOperator.ClearFlag, 6));

            Assert.Equal(unchecked((short)0xFFDF), gob.ObjectData[word]);
        }

        [Theory]
        [InlineData((short)1)]
        [InlineData((short)5)]
        [InlineData((short)6)]
        [InlineData((short)9)]
        [InlineData((short)16)]
        public void SettingThenClearingABitRestoresTheWord(short bit)
        {
            // SetFlag and ClearFlag have to be inverses for every bit the data can name - the
            // property the old code broke for all of them at once, and the one the food scripts
            // depend on for bit 6.
            var gob = StackObjectWithWord();
            var frame = new VMStackFrame { StackObject = gob };
            var word = (short)VMStackObjectVariable.Reserved77;

            Evaluate(frame, FlagOp(VMExpressionOperator.SetFlag, bit));
            Assert.NotEqual(0, gob.ObjectData[word]);

            Evaluate(frame, FlagOp(VMExpressionOperator.ClearFlag, bit));
            Assert.Equal(0, gob.ObjectData[word]);
        }
    }
}
