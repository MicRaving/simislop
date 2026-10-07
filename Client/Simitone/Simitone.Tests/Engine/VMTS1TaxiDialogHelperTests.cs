using FSO.SimAntics.Engine;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMTS1TaxiDialogHelper"/>: the text, the string ids and - the part that matters -
    /// the answer rule of TS1 global call 3, <c>PullDownTaxiDialog</c>.
    ///
    /// <para>
    /// Call 3 is the last TS1 global call with no implementation. Every one of its twelve shipped
    /// call sites is the arrival half of a "family has landed on a shared lot" marker, and in
    /// every one of them the call is a piece of interface rather than a change to the lot: nothing
    /// is read out of it, and no register comes back in. That makes it the one call in the file
    /// whose correctness is a *safety* property rather than a behaviour, and a safety property
    /// nobody can observe at runtime is exactly what a test is for.
    /// </para>
    ///
    /// <para>
    /// The two facts pinned here:
    /// </para>
    /// <list type="bullet">
    /// <item><b>No answer can produce <c>GOTO_FALSE</c>.</b> All twelve call sites put 253 in
    /// the false pointer, and <c>VMThread.MoveToInstruction</c>'s <c>case 253</c> resolves that
    /// as "continue along the only path available" by taking the <em>other</em> branch - it
    /// prefers the true pointer whenever the true pointer is not itself 253. So at every site
    /// <c>GOTO_FALSE</c> and <c>GOTO_TRUE</c> land on the same instruction, and the answer is
    /// unobservable rather than merely unread. Reporting anything else would be a change with
    /// no meaning in the shipped data, and reporting false "because the player said no" would
    /// be a lie about a question the scripts never asked.</item>
    /// <item><b>The string ids address the table that gets built.</b>
    /// <c>VMDialogHandler.ShowDialog</c> reads <c>table.GetString(id - 1)</c>, so an off-by-one in
    /// the ids shows up as a null caption on screen and nothing else - the dialog still works, it
    /// just has no words. A test is the only place that catches it.</item>
    /// </list>
    /// </summary>
    public sealed class VMTS1TaxiDialogHelperTests
    {
        // ---- the dialog type byte -----------------------------------------------------------

        [Fact]
        public void TheTaxiPromptIsTheFirstFreeDialogTypeByte()
        {
            // 16 is what the enum comment claims and what VMDialogPrivateStrings.CreateTS1TaxiOperand
            // puts on the wire. A change here is a behaviour change for every shipped call site.
            Assert.Equal(16, (int)VMDialogType.TS1TaxiLeave);
        }

        [Fact]
        public void TheTaxiPromptDoesNotCollideWithAnyShippedDialogType()
        {
            // The TS1 dialog_private type bytes that are already spoken for. 5-15 are the
            // expansion dialogs, 3 and 4 are the base game's own, and 127-129 are FreeSO's.
            var taken = new[]
            {
                VMDialogType.Message,          // 0
                VMDialogType.YesNo,            // 1
                VMDialogType.YesNoCancel,      // 2
                VMDialogType.TextEntry,        // 3
                VMDialogType.Sims1Tutorial,    // 4
                VMDialogType.NumericEntry,     // 5 == TS1Downtown
                VMDialogType.TS1Downtown,
                VMDialogType.TS1Clothes,       // 6
                VMDialogType.TS1Vacation,      // 7
                VMDialogType.TS1Neighborhood,  // 8
                VMDialogType.TS1PetChoice,     // 9
                VMDialogType.TS1PhoneBook,     // 10
                VMDialogType.TS1StudioTown,    // 11
                VMDialogType.TS1Spellbook,     // 12
                VMDialogType.TS1Magictown,     // 13
                VMDialogType.TS1TransformMe,   // 14
                VMDialogType.TS1Cookbook,      // 15
                VMDialogType.FSOJob,           // 127
                VMDialogType.FSOColor,         // 128
                VMDialogType.FSOChars,         // 129
            };

            Assert.DoesNotContain(VMDialogType.TS1TaxiLeave, taken);
        }

        [Fact]
        public void EveryExpansionDialogTypeIsBelowTheTaxiPrompt()
        {
            // "5 to 15 are taken" is the reason this call needed a new byte at all, and it is the
            // claim a future wave would most plausibly re-derive wrongly.
            var expansionTypes = new[]
            {
                VMDialogType.TS1Downtown, VMDialogType.TS1Clothes, VMDialogType.TS1Vacation,
                VMDialogType.TS1Neighborhood, VMDialogType.TS1PetChoice, VMDialogType.TS1PhoneBook,
                VMDialogType.TS1StudioTown, VMDialogType.TS1Spellbook, VMDialogType.TS1Magictown,
                VMDialogType.TS1TransformMe, VMDialogType.TS1Cookbook,
            };

            Assert.All(expansionTypes, t => Assert.InRange((int)t, 5, 15));
            Assert.Equal(11, expansionTypes.Length);
        }

        // ---- the string ids address the table that is built ----------------------------------

        [Fact]
        public void EveryStringIdAddressesItsOwnLine()
        {
            // VMDialogHandler.ShowDialog reads table.GetString(id - 1) for the title, the buttons
            // and the icon name, and Math.Max(0, id - 1) for the message. So the invariant is
            // Lines[id - 1] == the constant, for all four.
            Assert.Equal(VMTS1TaxiDialogHelper.Title, VMTS1TaxiDialogHelper.Lines[VMTS1TaxiDialogHelper.TitleStringId - 1]);
            Assert.Equal(VMTS1TaxiDialogHelper.Message, VMTS1TaxiDialogHelper.Lines[VMTS1TaxiDialogHelper.MessageStringId - 1]);
            Assert.Equal(VMTS1TaxiDialogHelper.Yes, VMTS1TaxiDialogHelper.Lines[VMTS1TaxiDialogHelper.YesStringId - 1]);
            Assert.Equal(VMTS1TaxiDialogHelper.No, VMTS1TaxiDialogHelper.Lines[VMTS1TaxiDialogHelper.NoStringId - 1]);
        }

        [Fact]
        public void NoTwoStringIdsShareALine()
        {
            var ids = new[]
            {
                VMTS1TaxiDialogHelper.TitleStringId,
                VMTS1TaxiDialogHelper.MessageStringId,
                VMTS1TaxiDialogHelper.YesStringId,
                VMTS1TaxiDialogHelper.NoStringId,
            };

            Assert.Equal(ids.Length, ids.Distinct().Count());
        }

        [Fact]
        public void TheLinesAreContiguousAndStartAtOne()
        {
            // 0 is VMDialogHandler's "this line is absent" marker, so no real id may be 0, and a
            // gap in the middle would read as an empty caption rather than as an error. The four
            // ids are therefore exactly 1..4, in Lines order.
            Assert.Equal(0, VMTS1TaxiDialogHelper.AbsentStringId);
            Assert.All(VMTS1TaxiDialogHelper.Lines, line => Assert.False(string.IsNullOrEmpty(line)));
            Assert.Equal(
                new byte[] { 1, 2, 3, 4 },
                new[]
                {
                    VMTS1TaxiDialogHelper.TitleStringId,
                    VMTS1TaxiDialogHelper.MessageStringId,
                    VMTS1TaxiDialogHelper.YesStringId,
                    VMTS1TaxiDialogHelper.NoStringId,
                });
            Assert.Equal(4, VMTS1TaxiDialogHelper.Lines.Length);
        }

        [Fact]
        public void TheBuiltTableReadsBackEveryLineTheIdsName()
        {
            // The end-to-end of the string path, minus the client: BuildTable is what
            // PullDownTaxiDialog hands to VMDialogPrivateStrings.ExecuteGeneric, and this is
            // exactly the read ShowDialog performs.
            var table = VMTS1TaxiDialogHelper.BuildTable();

            Assert.Equal(VMTS1TaxiDialogHelper.Lines.Length, table.Length);
            Assert.Equal(VMTS1TaxiDialogHelper.Title, table.GetString(VMTS1TaxiDialogHelper.TitleStringId - 1));
            Assert.Equal(VMTS1TaxiDialogHelper.Message, table.GetString(VMTS1TaxiDialogHelper.MessageStringId - 1));
            Assert.Equal(VMTS1TaxiDialogHelper.Yes, table.GetString(VMTS1TaxiDialogHelper.YesStringId - 1));
            Assert.Equal(VMTS1TaxiDialogHelper.No, table.GetString(VMTS1TaxiDialogHelper.NoStringId - 1));
        }

        [Fact]
        public void EveryBuiltLineIsReadable()
        {
            var table = VMTS1TaxiDialogHelper.BuildTable();

            for (var i = 0; i < table.Length; i++)
                Assert.Equal(VMTS1TaxiDialogHelper.Lines[i], table.GetString(i));
        }

        [Fact]
        public void TheTableDoesNotFallOverOnAnIndexPastTheEnd()
        {
            // STR.GetString is bounds-checked and returns null rather than throwing, so a future id
            // that runs off the end of the table is a blank caption and not a crash - this states
            // that, so the failure mode of a future edit is known to be cosmetic.
            var table = VMTS1TaxiDialogHelper.BuildTable();

            Assert.Null(table.GetString(VMTS1TaxiDialogHelper.Lines.Length));
            Assert.Null(table.GetString(-1));
        }

        // ---- the answer rule ------------------------------------------------------------------

        [Theory]
        [InlineData((byte)0)]  // yes / ok
        [InlineData((byte)1)]  // no
        [InlineData((byte)2)]  // cancel
        public void EveryButtonPressIsAnsweredTrue(byte responseCode)
        {
            // The honest half: no shipped caller reads the answer, so the prompt cannot be allowed
            // to steer the call. Both buttons and the timeout (which arrives here as 0) mean the
            // same thing to every arrival marker.
            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, VMTS1TaxiDialogHelper.AnswerFor(responseCode));
        }

        [Theory]
        [InlineData((byte)3)]
        [InlineData((byte)42)]
        [InlineData((byte)128)]
        [InlineData((byte)255)]
        public void AnAnswerFromAModifiedClientStillReportsTrue(byte responseCode)
        {
            // A response byte nothing defines must not be able to route this call somewhere else.
            Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, VMTS1TaxiDialogHelper.AnswerFor(responseCode));
        }

        [Fact]
        public void NoPossibleAnswerProducesGotoFalse()
        {
            // The rule, stated over the whole byte range rather than a sample of it. All twelve
            // shipped call sites write 253 into the false pointer, which the VM resolves as
            // "continue along the only path available" by taking the other branch - so a false
            // answer here would be indistinguishable from a true one, and a "the player said no"
            // answer would be inventing a distinction the data does not contain.
            for (var code = 0; code <= byte.MaxValue; code++)
            {
                var answer = VMTS1TaxiDialogHelper.AnswerFor((byte)code);
                Assert.NotEqual(VMPrimitiveExitCode.GOTO_FALSE, answer);
                Assert.NotEqual(VMPrimitiveExitCode.GOTO_FALSE_NEXT_TICK, answer);
                Assert.Equal(VMPrimitiveExitCode.GOTO_TRUE, answer);
            }
        }

        // ---- the operand the call hands the dialog machinery ----------------------------------

        [Fact]
        public void TheOperandIsTheTaxiDeparturePrompt()
        {
            var operand = VMDialogPrivateStrings.CreateTS1TaxiOperand();

            Assert.Equal(VMDialogType.TS1TaxiLeave, operand.Type);
            Assert.Equal(VMTS1TaxiDialogHelper.TitleStringId, operand.TitleStringID);
            Assert.Equal(VMTS1TaxiDialogHelper.MessageStringId, operand.MessageStringID);
            Assert.Equal(VMTS1TaxiDialogHelper.YesStringId, operand.YesStringID);
            Assert.Equal(VMTS1TaxiDialogHelper.NoStringId, operand.NoStringID);
        }

        [Fact]
        public void ThePromptHasNoCancelButtonAndNoIconName()
        {
            // 0 is the "absent" value in both fields, and it is what makes this a two-button Yes/No
            // rather than a Yes/No/Cancel: the client would draw a third button with no caption.
            var operand = VMDialogPrivateStrings.CreateTS1TaxiOperand();

            Assert.Equal(VMTS1TaxiDialogHelper.AbsentStringId, operand.CancelStringID);
            Assert.Equal(VMTS1TaxiDialogHelper.AbsentStringId, operand.IconNameStringID);
        }

        [Fact]
        public void ThePromptBlocksTheCallingThread()
        {
            // ExecuteGeneric only parks the thread in a VMDialogResult when the Continue flag is
            // clear. A non-blocking taxi prompt would let the arrival loop run away from under the
            // player instead of waiting for the answer.
            var operand = VMDialogPrivateStrings.CreateTS1TaxiOperand();

            Assert.False(operand.Continue);
            Assert.Equal((VMDialogFlags)0, operand.Flags & VMDialogFlags.Continue);
        }

        [Fact]
        public void EachRaiseGetsAFreshOperandAndTable()
        {
            // Nothing may share a mutable VMDialogOperand or STR between raises: ExecuteGeneric is
            // re-entered every tick while the prompt is up, and a shared table would be one
            // localised write away from a dialog showing the wrong words.
            var first = VMDialogPrivateStrings.CreateTS1TaxiOperand();
            var second = VMDialogPrivateStrings.CreateTS1TaxiOperand();
            first.MessageStringID = 200;

            Assert.NotSame(first, second);
            Assert.Equal(VMTS1TaxiDialogHelper.MessageStringId, second.MessageStringID);
            Assert.NotSame(VMTS1TaxiDialogHelper.BuildTable(), VMTS1TaxiDialogHelper.BuildTable());
        }
    }
}
