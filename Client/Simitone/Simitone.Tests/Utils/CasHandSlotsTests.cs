using FSO.SimAntics.Utils;
using Simitone.Client.Utils;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Tests for <see cref="CasHandSlots"/>: the six handgroup body-string slots (17-22) a
    /// Create-a-Sim sim is written out with.
    ///
    /// <para>
    /// These are the one part of a CAS sim that the CAS does not get to invent. A TS1 body string
    /// names the sim's own hands six times, and <c>FSO.Vitaboy.Outfit.Read</c> reads them as the
    /// idle, pointing and <em>closed</em> gesture of the left and the right hand: 17 left open,
    /// 19 left point, 21 left fist, 18/20/22 the right equivalents. The mesh names are the only
    /// place the hand letter and the gesture are tied together, so the slots are positional in
    /// meaning and not in spelling.
    /// </para>
    ///
    /// <para>
    /// The bug this file exists for: <c>CASToNeighGen</c> wrote <c>info.BodyStringReplace[21] =
    /// "H" + hand + "LO,..."</c> - the left <em>open</em> hand, in the slot the reader takes as
    /// the left <em>closed</em> one - while the preview drawn in the same screen named
    /// <c>hu...lc.apr</c>. A CAS sim therefore looked correct in the CAS and was saved with its
    /// left fist drawn open. The fix points CAS at the engine's shared writer rather than fixing
    /// the string, so the test that matters is the one that pins the six slots to that writer:
    /// a second copy of the rule is how the disagreement came back the first time.
    /// </para>
    /// </summary>
    public sealed class CasHandSlotsTests
    {
        /// <summary>The six slots a handgroup occupies, in the order Outfit.Read reads them.</summary>
        private static readonly int[] HandSlotIndices =
        {
            VMTS1BodyTransformHelper.SlotHandLeftOpen,
            VMTS1BodyTransformHelper.SlotHandRightOpen,
            VMTS1BodyTransformHelper.SlotHandLeftPoint,
            VMTS1BodyTransformHelper.SlotHandRightPoint,
            VMTS1BodyTransformHelper.SlotHandLeftFist,
            VMTS1BodyTransformHelper.SlotHandRightFist
        };

        // ---- which hand meshes ------------------------------------------------------------

        [Theory]
        [InlineData((short)0, "m")]   // adult male
        [InlineData((short)1, "f")]   // adult female
        [InlineData((short)2, "u")]   // child male: the genderless Hu meshes
        [InlineData((short)3, "u")]   // child female
        public void HandLetterFor_MapsTheCasGenderCodeOntoTheHandMeshes(short genderCode, string expected)
        {
            Assert.Equal(expected, CasHandSlots.HandLetterFor(genderCode).ToString());
        }

        [Theory]
        [InlineData((short)-1)]
        [InlineData((short)4)]
        [InlineData(short.MaxValue)]
        public void HandLetterFor_GivesAnythingOutsideTheCasCodesTheChildHands(short genderCode)
        {
            // A hand mesh that does not exist is a sim with no hands at all, so the safe answer to
            // an unexpected code is the mesh every child has rather than a guess.
            Assert.Equal("u", CasHandSlots.HandLetterFor(genderCode).ToString());
        }

        [Fact]
        public void TheTwoWritersAgreeOnWhichMeshesAGenderGets()
        {
            // The engine decides the same thing for a sim born from a lot script; if the CAS ever
            // disagreed about a child's hands the two would produce visibly different sims.
            // Compared as ordered "slot=value" text rather than as two dictionaries, so the
            // assertion is about the strings and not about enumeration order.
            foreach (var gender in new[] { 'm', 'f' })
            {
                var engine = VMTS1BodyTransformHelper.HandBodyStrings(gender, "lgt");
                var cas = CasHandSlots.BodyStringsFor(gender == 'm' ? (short)0 : (short)1, "lgt");

                Assert.Equal(Flatten(engine), Flatten(cas));
            }
        }

        /// <summary>
        /// A slot dictionary as ordered "slot=value" text, so two of them can be compared without
        /// the assertion depending on how either one happens to enumerate.
        /// </summary>
        private static string[] Flatten(Dictionary<int, string> slots)
        {
            return slots.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Key + "=" + kvp.Value).ToArray();
        }

        // ---- the six strings --------------------------------------------------------------
        [Fact]
        public void BodyStringsFor_WritesExactlyTheSixHandSlots()
        {
            var slots = CasHandSlots.BodyStringsFor(0, "lgt");

            Assert.Equal(6, slots.Count);
            foreach (var index in HandSlotIndices)
            {
                Assert.True(slots.ContainsKey(index), "no hand string written to slot " + index);
            }
            Assert.All(slots.Keys, slot =>
                Assert.True(slot >= 17 && slot <= 22, "hand string written outside slots 17-22: " + slot));
        }

        [Fact]
        public void TheLeftFistSlotNamesTheClosedHandNotTheOpenOne()
        {
            // The regression itself. Slot 21 is read by Outfit.Read as the left *closed* hand, so
            // anything ending "LO" there is a sim whose left fist is drawn with their open hand -
            // and it is invisible until someone makes that sim punch something.
            var slots = CasHandSlots.BodyStringsFor(0, "lgt");

            Assert.Equal("HmLC,HAND=huaolgt", slots[VMTS1BodyTransformHelper.SlotHandLeftFist]);
            Assert.DoesNotContain("LO", slots[VMTS1BodyTransformHelper.SlotHandLeftFist]);
        }

        [Fact]
        public void EverySlotNamesTheGestureItsIndexIsReadAs()
        {
            // The full table, so a future edit that moves a gesture to the wrong slot is caught
            // here rather than as a sim that points with its fist.
            var slots = CasHandSlots.BodyStringsFor(0, "lgt");

            Assert.Equal("HmLO,HAND=huaolgt", slots[17]);
            Assert.Equal("HmRO,HAND=huaolgt", slots[18]);
            Assert.Equal("HmLP,HAND=huaolgt", slots[19]);
            Assert.Equal("HmRP,HAND=huaolgt", slots[20]);
            Assert.Equal("HmLC,HAND=huaolgt", slots[21]);
            Assert.Equal("HmRC,HAND=huaolgt", slots[22]);
        }

        [Fact]
        public void TheOpenAndClosedHandsNeverShareASlot()
        {
            // "All six are correct" and "slot 21 is not the same string as slot 17" are not the
            // same claim: a helper that wrote the same open hand into all six would satisfy a
            // count check and still give every sim a left fist that opens when it closes.
            foreach (var gender in new short[] { 0, 1, 2, 3 })
            {
                var slots = CasHandSlots.BodyStringsFor(gender, "drk");

                Assert.NotEqual(slots[17], slots[21]);
                Assert.NotEqual(slots[18], slots[22]);
                Assert.NotEqual(slots[19], slots[21]);
                Assert.NotEqual(slots[20], slots[22]);
            }
        }

        [Theory]
        [InlineData("lgt")]
        [InlineData("drk")]
        [InlineData("Y")]
        [InlineData("_macssx")]   // a named hand set keeps its leading underscore
        public void BodyStringsFor_KeepsTheHandgroupTokenTheCasStored(string handgroupTex)
        {
            // CASFamilyMember.HandgroupTex holds the token *after* "huao" (see CasEdit
            // .HandgroupTexFrom), and the engine's writer re-adds the prefix - so a full
            // "huaolgt" here would produce "huaohuaolgt" and a sim with no hands.
            var slots = CasHandSlots.BodyStringsFor(1, handgroupTex);

            foreach (var slot in HandSlotIndices)
            {
                Assert.Equal("huao" + handgroupTex, slots[slot].Split("HAND=")[1]);
            }
        }

        [Fact]
        public void BodyStringsFor_UsesTheHandsOwnMeshAndNotTheGenderOneForAChild()
        {
            var adult = CasHandSlots.BodyStringsFor(1, "lgt");
            var child = CasHandSlots.BodyStringsFor(3, "lgt");

            Assert.StartsWith("Hf", adult[17]);
            Assert.StartsWith("Hu", child[17]);
        }

        [Fact]
        public void BodyStringsFor_IsTheEngineWriterRatherThanACopyOfIt()
        {
            // The reason the helper delegates at all. The engine has three writers of these six
            // strings - a new neighbour, an adult transform, and now the CAS - and they used to
            // disagree about slot 21. A copy would be a fourth writer and would drift again.
            var cas = CasHandSlots.BodyStringsFor(0, "med");
            var engine = VMTS1BodyTransformHelper.HandBodyStrings('m', "med");

            Assert.Equal(Flatten(engine), Flatten(cas));
        }

        [Fact]
        public void BodyStringsFor_SurvivesAMissingHandgroupToken()
        {
            // A sim loaded from an IFF with no handgroup falls back to the skin colour before it
            // gets here, but the writer must not throw if it does not: the CAS builds a
            // SimTemplateCreateInfo and hands it to the generator, and an exception there loses
            // the whole family rather than one sim's hands.
            var slots = CasHandSlots.BodyStringsFor(0, null);

            Assert.Equal(6, slots.Count);
            Assert.Equal("HmLC,HAND=huao", slots[VMTS1BodyTransformHelper.SlotHandLeftFist]);
        }

        [Fact]
        public void TheCasCarouselRoundTripsTheTokenItStores()
        {
            // CASToNeighGen writes "huao" + HandgroupTex, and loading a family back strips that
            // prefix into the same field. If the two halves ever disagree, editing a family and
            // saving it changes every sim's hands - which is why HandgroupTex is the bare token.
            const string stored = "_macssx";

            var written = CasHandSlots.BodyStringsFor(0, stored)[17];
            var recovered = CasEdit.HandgroupTexFrom(written.Split("HAND=")[1]);

            Assert.Equal(stored, recovered);
        }

        /// <summary>
        /// Guards the slot constants the assertions above are written against: they are the
        /// engine's, and a reordering there would quietly invalidate every "H?LC into slot 21"
        /// claim in this file rather than fail it.
        /// </summary>
        [Fact]
        public void TheHandSlotConstantsAreTheSixContiguousSlotsOutfitReads()
        {
            var constants = new Dictionary<string, int>
            {
                { "left open", VMTS1BodyTransformHelper.SlotHandLeftOpen },
                { "right open", VMTS1BodyTransformHelper.SlotHandRightOpen },
                { "left point", VMTS1BodyTransformHelper.SlotHandLeftPoint },
                { "right point", VMTS1BodyTransformHelper.SlotHandRightPoint },
                { "left fist", VMTS1BodyTransformHelper.SlotHandLeftFist },
                { "right fist", VMTS1BodyTransformHelper.SlotHandRightFist }
            };

            Assert.Equal(
                new[] { 17, 18, 19, 20, 21, 22 },
                constants.Values.OrderBy(v => v).ToArray());

            // The gender slots sit either side of them, so 21 has to be the left fist and not
            // something that happens to also be 21.
            Assert.True(VMTS1BodyTransformHelper.SlotGender < VMTS1BodyTransformHelper.SlotHandLeftOpen);
            Assert.True(VMTS1BodyTransformHelper.SlotHandRightFist < VMTS1BodyTransformHelper.SlotTeddyLeft);
        }
    }
}
