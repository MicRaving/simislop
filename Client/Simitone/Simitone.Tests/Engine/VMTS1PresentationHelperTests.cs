using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// Pins the three presentation contracts in <see cref="VMTS1PresentationHelper"/> - the
    /// tokenizer behind <c>GetTokensFromString</c> (38), the camera-distance score behind
    /// <c>GetDistanceToCameraInTemp0</c> (12), and the subject rules behind <c>HeadFlush</c> (41).
    ///
    /// <para>
    /// Each of these is a claim that was recovered by disassembling the retail scripts, and each
    /// fails silently if it is wrong. The tokenizer is the clearest case: if it accepts prose as a
    /// token list, Makin' Magic's recipe lookup starts matching ingredients that are not in the
    /// recipe, and the only symptom is that a spell the player assembled perfectly refuses to
    /// cast. The distance score is the same shape - the taxi boarding loop only ever runs a
    /// maximum, so a reversed sign produces a car that fills from the front and nobody can say
    /// why. The head-flush rules are the refusal branches, and a missing one means a sim rendered
    /// with no head.
    /// </para>
    ///
    /// <para>
    /// The disassembly citations live with the helper, so these tests assert behaviour rather than
    /// re-deriving it.
    /// </para>
    /// </summary>
    public sealed class VMTS1PresentationHelperTests
    {
        // =====================================================================================
        //  38. GetTokensFromString
        // =====================================================================================

        [Fact]
        public void ThreeTokenRecipeParsesToCountAndThreeTokens()
        {
            // "27,27,27" - Baking Mix x3, from MagicMasterSpells.iff STR 0x0005 [5].
            var list = VMTS1PresentationHelper.TokenizeTokenList("27,27,27");

            Assert.Equal((short)3, list.Count);
            Assert.Equal((short)27, list.First);
            Assert.Equal((short)27, list.Second);
            Assert.Equal((short)27, list.Third);
            Assert.True(list.Parsed);
            Assert.True(VMTS1PresentationHelper.IsFullTokenList(list));
        }

        [Fact]
        public void SingleIngredientListReportsCountOfOneAndZeroFillsTheRest()
        {
            // A one-ingredient list is what 4104 compares Local[2] (one ingredient) against, so
            // the two registers it does not use have to be written, not left holding the previous
            // call's values.
            var list = VMTS1PresentationHelper.TokenizeTokenList("5");

            Assert.Equal((short)1, list.Count);
            Assert.Equal((short)5, list.First);
            Assert.Equal(VMTS1PresentationHelper.NoToken, list.Second);
            Assert.Equal(VMTS1PresentationHelper.NoToken, list.Third);
            Assert.False(VMTS1PresentationHelper.IsFullTokenList(list));
        }

        [Fact]
        public void MissingTokenSlotsAreZeroAndAreNotTheFailureValue()
        {
            // 0 is the value the shipped scripts write for "no token in this slot" (4103 [11],
            // 4099 [12]); -1 is reserved for "not a token list" (4104 [7]).
            var list = VMTS1PresentationHelper.TokenizeTokenList("11,25,27");

            Assert.NotEqual(VMTS1PresentationHelper.TokenListFail, list.Second);
            Assert.NotEqual(VMTS1PresentationHelper.TokenListFail, list.Third);
        }

        [Fact]
        public void RegistersAreAlwaysThreeLong()
        {
            foreach (var text in new[] { "1", "1,2", "1,2,3", "1,2,3,4", "2,2,5" })
            {
                var registers = VMTS1PresentationHelper.TokenizeTokenList(text).ToRegisters();
                Assert.Equal(VMTS1PresentationHelper.TokenListWidth, registers.Length);
            }
        }

        [Theory]
        // MagicMasterSpells.iff's own literal pool really does contain prose - including the
        // "This should never appear in the game." sentinel - and Temp0 can name any of it, so a
        // lookup can genuinely hand back a sentence. 4104 [7] tests Temp0 == -1 before it reads
        // Temp1..3, so a sentence has to report failure rather than an empty list.
        [InlineData("This should never appear in the game.")]
        [InlineData("Tragic Clown Generator")]
        [InlineData("not used")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ProseAndBlanksAreNotTokenLists(string text)
        {
            var list = VMTS1PresentationHelper.TokenizeTokenList(text);

            Assert.Equal(VMTS1PresentationHelper.TokenListFail, list.Count);
            Assert.False(list.Parsed);
            Assert.Equal(VMTS1PresentationHelper.NoToken, list.First);
            Assert.Equal(VMTS1PresentationHelper.NoToken, list.Second);
            Assert.Equal(VMTS1PresentationHelper.NoToken, list.Third);
        }

        [Theory]
        [InlineData("2,,5")]     // a doubled separator is a typo, not an empty token
        [InlineData(",1,2")]     // a leading separator is the same
        [InlineData("1,2,")]
        [InlineData("2, 2, five")]   // one unparseable element poisons the whole list
        [InlineData("2.5,2,5")]
        [InlineData("2,2,5 ; Black Rose")]
        [InlineData("7A7D732E")]     // a GUID from STR 0x0007 is not a token
        public void MalformedListsFailRatherThanPartiallyParse(string text)
        {
            // Half a list is worse than none: 4104 would then compare an ingredient against
            // tokens that were never in the recipe.
            Assert.Equal(VMTS1PresentationHelper.TokenListFail,
                VMTS1PresentationHelper.TokenizeTokenList(text).Count);
        }

        [Fact]
        public void WhitespaceAroundTokensIsTolerated()
        {
            var list = VMTS1PresentationHelper.TokenizeTokenList(" 2 , 2 , 5 ");

            Assert.Equal((short)3, list.Count);
            Assert.Equal(new short[] { 2, 2, 5 }, list.ToRegisters());
        }

        [Fact]
        public void TokensBeyondTheThirdAreCountedButNotReturned()
        {
            // The count is the whole list's length even though only three registers come back -
            // otherwise 4103 [6] and 4099 [7] could not use it to reject a four-ingredient list.
            var list = VMTS1PresentationHelper.TokenizeTokenList("1,2,3,4,5");

            Assert.Equal((short)5, list.Count);
            Assert.Equal(new short[] { 1, 2, 3 }, list.ToRegisters());
            Assert.False(VMTS1PresentationHelper.IsFullTokenList(list));
        }

        [Fact]
        public void EveryShippedRecipeInTheFoodTableParses()
        {
            // MagicMasterSpells.iff STR 0x0005, the food table that BHAVs 4103 and 4104 read
            // (4098 and 4099 use STR 0x0003 and 0x0004 - see VMTS1TokenTableTests).
            string[] recipes =
            {
                "2,2,25", "25,26,26", "1,2,25", "2,2,5", "27,27,27",
                "11,25,27", "11,27,30", "25,26,27", "3,11,27"
            };

            foreach (var recipe in recipes)
            {
                var list = VMTS1PresentationHelper.TokenizeTokenList(recipe);
                Assert.True(list.Parsed, recipe);
                Assert.Equal((short)3, list.Count);
            }
        }

        [Fact]
        public void TokenValuesFitInATempRegister()
        {
            // Temp1..3 are 16-bit, and an unparseable-but-numeric value would truncate silently.
            var list = VMTS1PresentationHelper.TokenizeTokenList("32767,0,-1");

            Assert.Equal((short)3, list.Count);
            Assert.Equal(short.MaxValue, list.First);
            Assert.Equal((short)0, list.Second);
            Assert.Equal((short)-1, list.Third);
        }

        [Fact]
        public void TokenValuesBeyondAShortAreAFailureNotAWraparound()
        {
            // "40000,1,2" wrapping to a negative would be indistinguishable from -1's meaning in
            // the one place the script checks for a count.
            Assert.Equal(VMTS1PresentationHelper.TokenListFail,
                VMTS1PresentationHelper.TokenizeTokenList("40000,1,2").Count);
        }

        // =====================================================================================
        //  12. GetDistanceToCameraInTemp0
        // =====================================================================================

        [Fact]
        public void DistanceScoreIsMonotoneAndPointsAwayFromTheCamera()
        {
            // The car loop keeps the maximum, so "further away scores higher" is the whole
            // contract. A reversed sign would fill the car front to back and look merely odd.
            var near = VMTS1PresentationHelper.CameraDistanceScore(0, 0, 3, 4);   // 5 tiles
            var far = VMTS1PresentationHelper.CameraDistanceScore(0, 0, 30, 40); // 50 tiles

            Assert.Equal((short)5, near);
            Assert.Equal((short)50, far);
            Assert.True(far > near);
        }

        [Fact]
        public void DistanceScoreIsSymmetricInTheTwoAxes()
        {
            // The taxi trees run the same comparison for every occupant, so a metric that
            // favoured one diagonal would board some crowds back to front and others front to back.
            Assert.Equal(
                VMTS1PresentationHelper.CameraDistanceScore(10, 10, 13, 14),
                VMTS1PresentationHelper.CameraDistanceScore(10, 10, 14, 13));
        }

        [Fact]
        public void DistanceScoreIsInvariantUnderARotationOfTheViewAndTheObjectTogether()
        {
            // Retail's answer is about the camera, not the screen. A metric measured in screen
            // space, or along a fixed camera axis, would change when the player scrolled the lot
            // round - and the car boarding loop has no tie-break of its own, so the boarding order
            // would change underneath the player. Rotating both the viewpoint and the object
            // together must leave the answer alone.
            var viewpoint = (3f, 7f);
            var subject = (9f, 15f);

            var before = VMTS1PresentationHelper.CameraDistanceScore(
                viewpoint.Item1, viewpoint.Item2, subject.Item1, subject.Item2);

            // A 90-degree turn of the whole scene about its own centre.
            var cx = 6f; var cy = 11f;
            var rotView = (cx - (viewpoint.Item2 - cy), cy + (viewpoint.Item1 - cx));
            var rotSubj = (cx - (subject.Item2 - cy), cy + (subject.Item1 - cx));

            var after = VMTS1PresentationHelper.CameraDistanceScore(
                rotView.Item1, rotView.Item2, rotSubj.Item1, rotSubj.Item2);

            Assert.Equal(before, after);
            Assert.Equal((short)10, after);
        }

        [Fact]
        public void DistanceScoreDependsOnlyOnTheDifference()
        {
            // A pure delta: the same offset from any viewpoint scores the same, which is what
            // makes the "furthest from the camera wins" test mean the same thing for every
            // occupant no matter where on the lot they started.
            var fromOrigin = VMTS1PresentationHelper.CameraDistanceScore(0, 0, 12, 5);
            var fromCorner = VMTS1PresentationHelper.CameraDistanceScore(40, 40, 52, 45);

            Assert.Equal(fromOrigin, fromCorner);
            Assert.Equal((short)13, fromOrigin);
        }

        [Fact]
        public void DistanceScoreRoundsToWholeTilesSoThePickIsDeterministic()
        {
            // The loop has no tie-break of its own, so sub-tile precision could only create ties
            // resolved by object creation order.
            Assert.Equal((short)5, VMTS1PresentationHelper.CameraDistanceScore(0, 0, 4.2f, 2.8f));
            Assert.Equal((short)5, VMTS1PresentationHelper.CameraDistanceScore(0, 0, 4.9f, 0.4f));
        }

        [Fact]
        public void DistanceScoreSaturatesInsteadOfWrappingNegative()
        {
            // A wrapped negative would beat every real distance in the "> best so far" test and
            // the car would always pick the same occupant.
            var score = VMTS1PresentationHelper.CameraDistanceScore(0, 0, 100000f, 100000f);
            Assert.Equal(short.MaxValue, score);
            Assert.True(score > 0);
        }

        [Fact]
        public void DistanceScoreOfZeroIsZero()
        {
            // The "no answer yet" sentinel the taxi loop stores in Local[2] is 0, and the first
            // occupant legitimately scores 0 when they are standing on the viewpoint - that is
            // fine, the loop only needs "> best", not "> 0".
            Assert.Equal((short)0, VMTS1PresentationHelper.CameraDistanceScore(5, 5, 5, 5));
        }

        [Fact]
        public void FallbackViewpointIsTheLotCentre()
        {
            VMTS1PresentationHelper.FallbackViewpoint(64, 64, out float x, out float y);
            Assert.Equal(32f, x);
            Assert.Equal(32f, y);
        }

        [Fact]
        public void FallbackViewpointStillOrdersOccupantsByRealDistance()
        {
            // Headless (a server, or a lot before the camera exists) has to answer the same
            // question, and has to answer it the same way every run - a constant would make every
            // occupant tie and hand the decision to object creation order.
            VMTS1PresentationHelper.FallbackViewpoint(64, 64, out float x, out float y);

            var middle = VMTS1PresentationHelper.CameraDistanceScore(x, y, 32, 32);
            var corner = VMTS1PresentationHelper.CameraDistanceScore(x, y, 0, 0);

            Assert.True(corner > middle);
        }

        [Fact]
        public void FallbackViewpointIsStableForOddLotSizes()
        {
            VMTS1PresentationHelper.FallbackViewpoint(50, 30, out float x, out float y);
            Assert.Equal(25f, x);
            Assert.Equal(15f, y);
        }

        // =====================================================================================
        //  41. HeadFlush
        // =====================================================================================

        [Fact]
        public void HeadFlushFlushedForAPersonWithAHeadToken()
        {
            Assert.Equal(VMTS1PresentationHelper.TS1HeadFlushOutcome.Flushed,
                VMTS1PresentationHelper.ClassifyHeadFlush(true, true, "c801ma_elden"));
        }

        [Fact]
        public void HeadFlushRefusesANonPerson()
        {
            // Every shipped site assigns the stack object first, and the open-mic one assigns a
            // member of the audience - so a non-person is a broken caller, not a normal no-op.
            Assert.Equal(VMTS1PresentationHelper.TS1HeadFlushOutcome.NotAPerson,
                VMTS1PresentationHelper.ClassifyHeadFlush(false, true, "c801ma_elden"));
        }

        [Fact]
        public void HeadFlushRefusesAPersonWithNoBodyStrings()
        {
            Assert.Equal(VMTS1PresentationHelper.TS1HeadFlushOutcome.NoBodyStrings,
                VMTS1PresentationHelper.ClassifyHeadFlush(true, false, "c801ma_elden"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void HeadFlushRefusesToReResolveFromABlankHeadSlot(string headToken)
        {
            // The load-bearing refusal: VMOutfitReference.GetContent hands the renderer whatever
            // the reference resolves to, so re-resolving from a blank slot replaces a working
            // head with nothing.
            Assert.Equal(VMTS1PresentationHelper.TS1HeadFlushOutcome.NoHeadToken,
                VMTS1PresentationHelper.ClassifyHeadFlush(true, true, headToken));
        }

        [Fact]
        public void HeadFlushChecksThePersonBeforeTheBodyString()
        {
            // Order matters for the trace: "not a person" and "no body string" are different
            // broken scripts and should not be collapsed into one message.
            Assert.Equal(VMTS1PresentationHelper.TS1HeadFlushOutcome.NotAPerson,
                VMTS1PresentationHelper.ClassifyHeadFlush(false, false, null));
        }

        [Fact]
        public void HeadSlotIsTheOneTheEngineReads()
        {
            // VMOutfitReference(STR, head: true) hands GetString(2) to Outfit.ReadHead, and both
            // SetAvatarBodyStrings and LoadCrossRef build the TS1 head from that constructor.
            Assert.Equal(2, VMTS1PresentationHelper.HeadSlot);
        }

        [Fact]
        public void RenderFlushSlotsAreTheOnesAHeadDependsOn()
        {
            var slots = VMTS1PresentationHelper.RenderFlushSlots;

            // SpellGhostMe clears RenderDisplayFlags bits 1 and 2 by hand just before calling 41,
            // so re-asserting that slot is the flush. CurrentOutfit is the other half, because it
            // is what LoadCrossRef re-asserts immediately before re-resolving HeadOutfit.
            Assert.Equal(VMPersonDataVariable.RenderDisplayFlags, slots[0]);
            Assert.Equal(VMPersonDataVariable.CurrentOutfit, slots[1]);

            // IsGhost and JobPerformance are deliberately excluded: a flush is not a reason to
            // re-run those change handlers.
            Assert.DoesNotContain(VMPersonDataVariable.IsGhost, slots);
            Assert.DoesNotContain(VMPersonDataVariable.JobPerformance, slots);
            Assert.Equal(2, slots.Distinct().Count());
        }
    }
}
