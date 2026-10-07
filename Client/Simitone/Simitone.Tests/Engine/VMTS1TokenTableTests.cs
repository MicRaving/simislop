using FSO.SimAntics.Utils;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// Pins the addressing half of <c>GetTokensFromString</c> (38) - the step that turns the
    /// <c>(Temp0, Temp1)</c> pair a Makin' Magic script passes in into a string.
    ///
    /// <para>
    /// <see cref="VMTS1PresentationHelperTests"/> covers what happens to the string once it is in
    /// hand. This file covers where the string comes from, which is the part that reading the
    /// tokenizer alone could not settle: the call sites never name a string, they name a
    /// <c>STR</c> chunk and an index into it, and one object holds four valid recipe tables that
    /// all answer the same index differently. A regression here does not crash and does not look
    /// wrong - it just makes spell and recipe lookups silently answer "no such combination" for
    /// some ingredients and not others, which is the shape of bug a player reports as "the game
    /// forgot a recipe".
    /// </para>
    ///
    /// <para>
    /// The tables below are transcribed from the shipped
    /// <c>ExpansionPack7/MagicMasterSpells.iff</c>, so the expectations are about real data. The
    /// argument for reading the pair as (chunk id, index) is written out on
    /// <see cref="VMTS1PresentationHelper.TokenListRequest"/>; what these tests do is keep that
    /// reading true of the data, in particular that the loop bounds the retail trees pair with each
    /// table id are the sizes those tables actually have.
    /// </para>
    /// </summary>
    public sealed class VMTS1TokenTableTests
    {
        // =====================================================================================
        //  The shipped tables
        // =====================================================================================

        /// <summary>
        /// <c>MagicMasterSpells.iff</c>'s string chunks, as shipped. Index 0 is <c>"not used"</c>
        /// in every recipe table and the recipes sit at 1..N, which is why the call sites index
        /// them raw with no -1. <c>0x0006</c>'s trailing four entries were never filled in at all,
        /// which is exactly what 4104's loop bound of 5 protects against.
        /// </summary>
        private static readonly Dictionary<int, string[]> MagicControllerTables =
            new Dictionary<int, string[]>
        {
            // 24 spell recipes - the table 4098 pairs with a loop bound of 24.
            { 0x0003, new[] { "not used",
                "13,13,14", "13,15,16", "13,15,15", "13,18,19", "20,20,20", "20,20,21",
                "20,22,14", "20,23,17", "20,15,18", "20,17,24", "20,21,19", "50,50,50",
                "1,2,3", "1,1,3", "1,4,5", "1,6,3", "1,10,12", "11,7,5", "11,7,3",
                "11,8,4", "11,5,10", "11,9,10", "50,50,50", "51,51,51" } },

            // 9 charm recipes - the table 4098 pairs with a loop bound of 9.
            { 0x0004, new[] { "not used",
                "7,7,7", "7,7,6", "7,7,31", "7,31,31", "7,6,22", "7,22,22", "7,22,31",
                "6,6,31", "31,31,6" } },

            // 9 food recipes - 4103's "press/oven" table, and 4104's when the recipe's fourth
            // ingredient is Honey.
            { 0x0005, new[] { "not used",
                "2,2,25", "25,26,26", "1,2,25", "2,2,5", "27,27,27", "11,25,27", "11,27,30",
                "25,26,27", "3,11,27" } },

            // 5 more food recipes and then four never-filled entries - the table 4104 pairs with
            // a loop bound of 5.
            { 0x0006, new[] { "not used",
                "27,27,27", "11,25,27", "11,27,30", "25,26,27", "3,11,27", "", "", "", "" } },

            // Not a recipe table: prose, in the same IFF, and the shape a lookup must not accept.
            { 0x07D0, new[] { "Tragic Clown Generator", "This should never appear in the game." } },
        };

        /// <summary>
        /// Stands in for the engine's <c>STR</c> lookup: null for a table that is not there, and
        /// null for an index outside the table, which is what <c>STR.GetString</c> does.
        /// </summary>
        private static string ShippedLiteral(int table, int index)
        {
            if (!MagicControllerTables.TryGetValue(table, out var entries)) return null;
            return index >= 0 && index < entries.Length ? entries[index] : null;
        }

        private static VMTS1PresentationHelper.TokenList ReadShipped(int table, int index)
        {
            return VMTS1PresentationHelper.ReadTokenList(
                new VMTS1PresentationHelper.TokenListRequest((short)table, (short)index), ShippedLiteral);
        }

        /// <summary>The block the call would leave in Temp0..Temp3 for a request that failed.</summary>
        private static short[] FailedBlock =>
            new short[]
            {
                VMTS1PresentationHelper.TokenListFail,
                VMTS1PresentationHelper.NoToken,
                VMTS1PresentationHelper.NoToken,
                VMTS1PresentationHelper.NoToken,
            };

        // =====================================================================================
        //  Which table the id in Temp0 selects
        // =====================================================================================

        [Fact]
        public void TheSameIndexInDifferentTablesIsADifferentRecipe()
        {
            // This is the whole reason one flat table could not be guessed. Index 5 is a spell
            // recipe, a charm recipe and a food recipe depending only on which STR chunk Temp0
            // names, and the answer goes straight into an ingredient comparison - so a table
            // picked wrongly reads as "this combination does not exist".
            Assert.Equal(new short[] { 3, 20, 20, 20 }, ReadShipped(0x0003, 5).ToRegisterBlock());
            Assert.Equal(new short[] { 3, 7, 6, 22 }, ReadShipped(0x0004, 5).ToRegisterBlock());
            Assert.Equal(new short[] { 3, 27, 27, 27 }, ReadShipped(0x0005, 5).ToRegisterBlock());
        }

        [Fact]
        public void TablesFiveAndSixDisagreeAndThatIsCorrect()
        {
            // An earlier reading of this data treated STR 0x0006 as a broken duplicate of 0x0005.
            // It is not: it is a separate five-entry recipe table, and 4104 picks between the two on
            // the strength of the recipe's fourth ingredient (4104 [32] Temps[3] == 1). Only index
            // 1 of 0x0006 coincides with an entry of 0x0005.
            Assert.Equal(new short[] { 3, 27, 27, 27 }, ReadShipped(0x0005, 5).ToRegisterBlock());
            Assert.Equal(new short[] { 3, 27, 27, 27 }, ReadShipped(0x0006, 1).ToRegisterBlock());
            Assert.Equal(new short[] { 3, 2, 2, 25 }, ReadShipped(0x0005, 1).ToRegisterBlock());
            Assert.Equal(new short[] { 3, 3, 11, 27 }, ReadShipped(0x0006, 5).ToRegisterBlock());
        }

        [Fact]
        public void TheSpellTableReallyDoesHaveTwentyFourEntries()
        {
            // 4098's bound of 24 is a fact about the data, not a constant to copy, and the same for
            // its bound of 9 on the charm table.
            Assert.Equal(24, MagicControllerTables[0x0003].Length - 1);
            Assert.Equal(9, MagicControllerTables[0x0004].Length - 1);
        }

        [Fact]
        public void TheFiveEntryTableReallyDoesStopAtFive()
        {
            // 4104 pairs STR 0x0006 with a loop bound of 5, and the shipped chunk has exactly five
            // recipes followed by four entries that were never filled in. If those were ever
            // populated the bound would need revisiting, so pin the count from the data.
            Assert.Equal(5, MagicControllerTables[0x0006].Count(text => !string.IsNullOrWhiteSpace(text)) - 1);
            for (var index = 6; index <= 9; index++)
                Assert.Equal(VMTS1PresentationHelper.TokenListFail, ReadShipped(0x0006, index).Count);
        }

        [Fact]
        public void IndexZeroIsTheTableHeaderAndNotARecipe()
        {
            // "not used" sits at index 0 in every recipe table and the call sites start at 1, so a
            // lookup of 0 is a caller asking for the header and must not read as a recipe.
            Assert.Equal(VMTS1PresentationHelper.TokenListFail, ReadShipped(0x0003, 0).Count);
            Assert.Equal(VMTS1PresentationHelper.TokenListFail, ReadShipped(0x0004, 0).Count);
            Assert.Equal(VMTS1PresentationHelper.TokenListFail, ReadShipped(0x0005, 0).Count);
        }

        [Fact]
        public void ProseHeldByTheSameObjectIsStillNotATokenList()
        {
            // STR 0x07D0 is in the same IFF and the literal pool can name it, so a lookup that
            // finds prose has to fail rather than report zero tokens: 4104 [7] tests Temp0 == -1
            // before it reads any token register.
            var list = ReadShipped(0x07D0, 1);

            Assert.Equal(VMTS1PresentationHelper.TokenListFail, list.Count);
            Assert.Equal(FailedBlock, list.ToRegisterBlock());
        }

        [Fact]
        public void EveryIndexTheLoopBoundsReachParsesAsAThreeTokenList()
        {
            // 4098 iterates 1..24 on 0x0003 and 1..9 on 0x0004; 4103 and 4104 use 1..9 on 0x0005
            // and 1..5 on 0x0006. Every one of those entries has to come back as exactly three
            // tokens, because 4104 [9] and 4103 [6] / 4099 [7] reject anything else and move on.
            foreach (var (table, last) in new[] { (0x0003, 24), (0x0004, 9), (0x0005, 9), (0x0006, 5) })
            {
                for (var index = 1; index <= last; index++)
                {
                    var list = ReadShipped(table, index);
                    Assert.True(list.Parsed, $"0x{table:X4}[{index}]");
                    Assert.Equal((short)3, list.Count);
                    Assert.True(VMTS1PresentationHelper.IsFullTokenList(list), $"0x{table:X4}[{index}]");
                }
            }
        }

        [Fact]
        public void AnOutOfRangeIngredientIdIsCarriedThroughUnchanged()
        {
            // 0x0003[12], [23] and [24] hold "50,50,50" and "51,51,51" - ids past the end of the
            // 32-name ingredient table in STR 0x0002. Retail still hands those numbers to the
            // script, and that is how those three spells are made un-craftable; clamping or
            // rejecting them here would change three recipes' behaviour.
            Assert.Equal(new short[] { 3, 50, 50, 50 }, ReadShipped(0x0003, 12).ToRegisterBlock());
            Assert.Equal(new short[] { 3, 51, 51, 51 }, ReadShipped(0x0003, 24).ToRegisterBlock());
        }

        // =====================================================================================
        //  Unresolvable requests
        // =====================================================================================

        [Fact]
        public void AMissingTableIsAFailureAndNotAnException()
        {
            // A modded or stripped object. One recipe stops crafting; the interaction does not die.
            var list = ReadShipped(0x9999, 1);

            Assert.Equal(VMTS1PresentationHelper.TokenListFail, list.Count);
            Assert.Equal(FailedBlock, list.ToRegisterBlock());
        }

        [Fact]
        public void AnIndexPastTheEndOfTheTableIsAFailure()
        {
            // 4098 walks straight off the end of 0x0004 whenever the charm branch is taken, and
            // 4104 walks off 0x0006's filled-in region. Both must simply not match.
            Assert.Equal(VMTS1PresentationHelper.TokenListFail, ReadShipped(0x0004, 10).Count);
            Assert.Equal(VMTS1PresentationHelper.TokenListFail, ReadShipped(0x0003, 25).Count);
            Assert.Equal(VMTS1PresentationHelper.TokenListFail, ReadShipped(0x0003, 30000).Count);
            Assert.Equal(FailedBlock, ReadShipped(0x0003, 30000).ToRegisterBlock());
        }

        [Fact]
        public void ANegativeIndexNeverReachesTheTable()
        {
            // The lookup is not even attempted, so a negative index cannot become a wrap-around
            // read of the end of some other table.
            var consulted = false;
            var list = VMTS1PresentationHelper.ReadTokenList(
                new VMTS1PresentationHelper.TokenListRequest(0x0003, -1),
                (table, index) => { consulted = true; return ShippedLiteral(table, index); });

            Assert.False(consulted);
            Assert.Equal(FailedBlock, list.ToRegisterBlock());
        }

        [Fact]
        public void ANegativeTableIdNeverReachesTheTable()
        {
            // A negative Temp0 is the case the four call sites cannot produce (they load a literal
            // from the TS1 literal scope, which is 0..32767), so it is a broken caller rather than
            // a normal no-op - and the one answer that cannot be mistaken for a recipe is the
            // failure count.
            var consulted = false;
            var list = VMTS1PresentationHelper.ReadTokenList(
                new VMTS1PresentationHelper.TokenListRequest(-1, 1),
                (table, index) => { consulted = true; return ShippedLiteral(table, index); });

            Assert.False(consulted);
            Assert.Equal(FailedBlock, list.ToRegisterBlock());
        }

        [Fact]
        public void ATableIdThatNamesNoChunkStillFails()
        {
            // 0x9999 is a perfectly good ushort and so passes the addressability check; it simply
            // is not a chunk of this object. The lookup is attempted, finds nothing, and the answer
            // is still the failure count.
            var list = ReadShipped(0x9999, 1);

            Assert.Equal(FailedBlock, list.ToRegisterBlock());
        }

        [Fact]
        public void ANullLookupIsAFailureAndNotANullReference()
        {
            var list = VMTS1PresentationHelper.ReadTokenList(
                new VMTS1PresentationHelper.TokenListRequest(0x0003, 1), null);

            Assert.Equal(FailedBlock, list.ToRegisterBlock());
        }

        [Fact]
        public void TheRequestItselfKnowsWhetherItCanNameAnything()
        {
            // A STR chunk id is a ushort and a Temp is a short, so the only id that cannot name a
            // chunk is a negative one; and a negative index cannot address an entry at all.
            Assert.True(new VMTS1PresentationHelper.TokenListRequest(0x0003, 1).IsAddressable);
            Assert.True(new VMTS1PresentationHelper.TokenListRequest(0, 0).IsAddressable);
            Assert.True(new VMTS1PresentationHelper.TokenListRequest(short.MaxValue, 1).IsAddressable);
            Assert.False(new VMTS1PresentationHelper.TokenListRequest(0x0003, -1).IsAddressable);
            Assert.False(new VMTS1PresentationHelper.TokenListRequest(-1, 1).IsAddressable);
            Assert.False(new VMTS1PresentationHelper.TokenListRequest(-1, -1).IsAddressable);
        }

        // =====================================================================================
        //  What the call leaves in Temp0..Temp3
        // =====================================================================================

        [Theory]
        [InlineData("1", 1, new short[] { 1, 0, 0 })]
        [InlineData("1,2", 2, new short[] { 1, 2, 0 })]
        [InlineData("1,2,3", 3, new short[] { 1, 2, 3 })]
        [InlineData("1,2,3,4", 4, new short[] { 1, 2, 3 })]
        [InlineData("1,2,3,4,5", 5, new short[] { 1, 2, 3 })]
        public void TheCountInTemp0IsTheWholeListsLength(string text, short expectedCount, short[] expectedTokens)
        {
            // 4103 [6] and 4099 [7] branch on exactly three, so the count has to be the length of
            // the whole list and not the number of registers that came back - and the unfilled
            // slots have to read as NoToken rather than as a leftover from the last call.
            var list = VMTS1PresentationHelper.ReadTokenList(
                new VMTS1PresentationHelper.TokenListRequest(0x0003, 1), (table, index) => text);

            Assert.Equal(expectedCount, list.Count);
            Assert.Equal(expectedTokens, list.ToRegisters());
            Assert.Equal(new short[] { expectedCount }.Concat(expectedTokens).ToArray(),
                list.ToRegisterBlock());
        }

        [Theory]
        [InlineData("27,27,27")]
        [InlineData("5")]
        [InlineData("")]
        [InlineData("not used")]
        [InlineData("2,,5")]
        [InlineData(null)]
        public void TheRegisterBlockIsAlwaysFourLongIncludingOnFailure(string text)
        {
            // 4104 [8]-[11] reads Temp1..Temp3 whenever the count is not -1, so every register has
            // to be written every time: a register left over from the previous candidate recipe
            // would be compared against this one.
            var list = VMTS1PresentationHelper.TokenizeTokenList(text);

            Assert.Equal(4, list.ToRegisterBlock().Length);
            Assert.Equal(3, list.ToRegisters().Length);
        }

        [Fact]
        public void AFailedLookupStillClearsTheTokenRegisters()
        {
            // The four-register block is written whatever the outcome, so a failure cannot leave
            // the previous recipe's tokens sitting in Temp1..Temp3 for 4104 to compare against.
            var list = ReadShipped(0x9999, 1);

            Assert.Equal(new short[]
            {
                VMTS1PresentationHelper.TokenListFail,
                VMTS1PresentationHelper.NoToken,
                VMTS1PresentationHelper.NoToken,
                VMTS1PresentationHelper.NoToken,
            }, list.ToRegisterBlock());
        }

        [Fact]
        public void ASuccessfulLookupFillsTheBlockCountFirst()
        {
            // The order matters: the count is Temp0, the first token is Temp1, and the scripts read
            // the two in exactly that pairing (4103 [6] tests the count, [7] then moves it down).
            var list = ReadShipped(0x0005, 1);   // "2,2,25" - Grapes, Grapes, Sugar

            Assert.Equal(new short[] { 3, 2, 2, 25 }, list.ToRegisterBlock());
        }
    }
}
