/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using FSO.Vitaboy;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The purchasable-outfit half of the three TS1 suit calls: global call
    /// <see cref="VMGenericTS1CallMode.GetStackObjectsSuit"/> (30, "suit type in temp0, suit index
    /// in temp1"), <see cref="VMGenericTS1CallMode.SetStackObjectsSuit"/> (29, which returns the
    /// index the sim was in) and <see cref="VMGenericTS1CallMode.CountStackObjectSuits"/> (31).
    ///
    /// <para>
    /// These are the calls behind buying and wearing a Downtown outfit, and both of the two
    /// properties they need are structural rather than cosmetic, which is why they are worth
    /// pinning here rather than leaving to a lot-script playtest:
    ///
    ///   <list type="bullet">
    ///   <item><b>A suit is identified by its appearance name.</b> A catalogue entry and a body
    ///   string slot are both "NAME,KEY=TEXTURE" strings that have to be recognised as the same
    ///   suit. The element type of the catalogue is <c>Tuple&lt;string, string&gt;</c>, so no
    ///   whole-array search can do it - which is exactly how call 30 came to answer -1 for every
    ///   input, forever, because <c>Array.IndexOf</c> compares with the array's element type and a
    ///   tuple never equals a string.</item>
    ///   <item><b>29 and 30 have to agree.</b> 29 puts a sim in suit N and reports the index they
    ///   were in; 30 reports the index they are in. Both answers come from one definition of "which
    ///   suit is that", or a set-then-read round trip comes back with two different numbers.</item>
    ///   <item><b>Changing a suit only replaces the hand texture.</b> Body strings 17-22 are
    ///   "H" + hand letter + mesh code + ",HAND=" + texture, and <c>Outfit.Read</c> takes the
    ///   mesh at the first ',' and the texture at the first '='. A rewrite that kept the wrong half
    ///   of that string draws no hands at all, so the tests read the six slots back through
    ///   <see cref="Outfit"/> rather than asserting on the helper's own idea of the format.</item>
    ///   <item><b>A value a lot script made up is refused, not thrown at.</b> The suit type and the
    ///   suit index both arrive in temp registers, so an out-of-range one has to leave the sim in
    ///   the suit they were in instead of ending the script.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class VMTS1PurchasableOutfitHelperTests
    {
        // ---- the catalogue and the sim, as the helper's callers see them ---------------------

        /// <summary>
        /// Three of the purchasable "b" suits a <c>b011mafat_01</c> sim can wear, in the shape
        /// <c>GetValidOutfits</c> returns: the appearance with its skin texture in Item1, and the
        /// handgroup token (what follows "huao") in Item2. The middle one is a named hand set, so
        /// applying it changes all six hand slots.
        /// </summary>
        private static Tuple<string, string>[] FatSuits()
        {
            return new[]
            {
                Tuple.Create("b011mafat_01,BODY=b011mafatlgt", "lgt"),
                Tuple.Create("b012mafat_01,BODY=b012mafatlgt", "_macssx"),
                Tuple.Create("b013mafat_01,BODY=b013mafatlgt", "med"),
            };
        }

        /// <summary>
        /// A body string shaped like a real sim's: 35 slots, the daywear in slot 1, the five other
        /// purchasable outfit slots in 30-34, and the six hand slots (17-22) an adult male's body
        /// string carries - all with a named hand set's textures, so a suit that brings a
        /// different one with it visibly moves them.
        /// </summary>
        private static STR BodyString(string daywear = "b011mafat_01,BODY=b011mafatlgt")
        {
            return BodyStringOf(
                "adult",                                                   // 0
                daywear,                                                   // 1
                "c007ma_01,HEAD-HEAD=c007mamed",                           // 2
                "", "", "", "", "", "", "", "", "", "", "", "", "", "",   // 3-16
                "HmLO,HAND=huaomed",                                       // 17
                "HmRO,HAND=huaomed",                                       // 18
                "HmLP,HAND=huapmed",                                       // 19
                "HmRP,HAND=huapmed",                                       // 20
                "HmLC,HAND=huacmed",                                       // 21
                "HmRC,HAND=huacmed",                                       // 22
                "", "", "", "", "",                                       // 23-27
                ",BODY=fmafatmed_01",                                      // 28
                ",BODY=fmafatmed_01",                                      // 29
                "f100mafat_original,BODY=f100mafatmed_original",           // 30
                "s100mafat_original,BODY=s100mafatmed_original",           // 31
                "l100mafat_original,BODY=l100mafatmed_original",           // 32
                "w100mafat_original,BODY=w100mafatmed_original",           // 33
                "h533mafat_zoot,BODY=h533mafatmed_zoot");                  // 34
        }

        /// <summary>contents[0] is slot 0, contents[1] is slot 1, and so on.</summary>
        private static STR BodyStringOf(params string[] contents)
        {
            var str = new STR();
            for (int i = 0; i < contents.Length; i++)
            {
                str.InsertString(0, new STRItem(contents[contents.Length - 1 - i]));
            }
            return str;
        }

        // ---- which suit is the sim in (calls 29 and 30) -------------------------------------

        [Fact]
        public void ASimWearingTheSecondSuitIsInTheSecondSuit()
        {
            Assert.Equal(1, VMTS1PurchasableOutfitHelper.IndexOfSuit(FatSuits(), "b012mafat_01,BODY=b012mafatlgt"));
            Assert.Equal(0, VMTS1PurchasableOutfitHelper.IndexOfSuit(FatSuits(), "b011mafat_01,BODY=b011mafatlgt"));
            Assert.Equal(2, VMTS1PurchasableOutfitHelper.IndexOfSuit(FatSuits(), "b013mafat_01,BODY=b013mafatlgt"));
        }

        [Fact]
        public void TheBodyStringSuitIsNotACatalogueEntryAndNeverCouldHaveBeen()
        {
            // The shape of the bug call 30 shipped with: the catalogue is a Tuple[] and the thing
            // being searched for is a string, so a whole-array search compares a Tuple with a
            // string and can never match. Pinned because the fix is a comparison that has to be
            // written element by element - a future "simplification" back to Array.IndexOf looks
            // exactly like this and passes every other test in the file.
            var suits = FatSuits();
            const string worn = "b012mafat_01,BODY=b012mafatlgt";

            Assert.Equal(-1, Array.IndexOf(suits, worn));
            Assert.Equal(1, VMTS1PurchasableOutfitHelper.IndexOfSuit(suits, worn));
        }

        [Fact]
        public void ASuitTheSimIsNotWearingIsMinusOne()
        {
            // What a sim gets when they are wearing something this install's catalogue does not
            // list: a bought-from-the-shop outfit, a CAS edit, an expansion body. Also the answer
            // 29 gives for a sim who was not in the list, and the answer NextSuitIndex treats as
            // "on the last one".
            Assert.Equal(-1, VMTS1PurchasableOutfitHelper.IndexOfSuit(FatSuits(), "b999mazzz_01,BODY=b999mazzzlgt"));
            Assert.Equal(-1, VMTS1PurchasableOutfitHelper.IndexOfSuit(FatSuits(), null));
            Assert.Equal(-1, VMTS1PurchasableOutfitHelper.IndexOfSuit(FatSuits(), ""));
            Assert.Equal(-1, VMTS1PurchasableOutfitHelper.IndexOfSuit(null, "b011mafat_01,BODY=x"));
            Assert.Equal(-1, VMTS1PurchasableOutfitHelper.IndexOfSuit(new Tuple<string, string>[0], "b011mafat_01,BODY=x"));
        }

        [Fact]
        public void TheTextureTheSimHappensToBeDrawnWithIsNotWhatMakesItADifferentSuit()
        {
            // The texture half of a body string is derived from the sim's own skin, so it can be
            // stale - or missing entirely on a body string a CAS edit wrote - without the sim
            // being in another suit. Answering -1 there would make a dressed sim look undressed to
            // every one of the three calls.
            var suits = FatSuits();
            Assert.Equal(1, VMTS1PurchasableOutfitHelper.IndexOfSuit(suits, "b012mafat_01,BODY=b012mafatdrk"));
            Assert.Equal(1, VMTS1PurchasableOutfitHelper.IndexOfSuit(suits, "b012mafat_01"));
        }

        [Fact]
        public void SuitNamesAreMatchedWithoutRegardToCaseOrToAlternativeEntries()
        {
            // The BCF provider lowercases every appearance name it registers, and the game is not
            // case-consistent about the ones it writes into body strings, so a case-sensitive
            // comparison would answer -1 for a correctly dressed sim. A ';' list is the form
            // Outfit.Read tolerates, and it always uses the first entry.
            Assert.Equal(2, VMTS1PurchasableOutfitHelper.IndexOfSuit(FatSuits(), "B013MAFAT_01,BODY=B013MAFATMED"));
            Assert.Equal(0, VMTS1PurchasableOutfitHelper.IndexOfSuit(FatSuits(), "b011mafat_01;f011mafat_01,BODY=x"));
        }

        [Fact]
        public void SettingASuitAndThenReadingItBackGivesTheSameIndex()
        {
            // The round trip 29 and 30 exist for: set an index, then ask which index the sim is in.
            // It only works if the string 29 writes and the comparison 30 makes are the same
            // definition of a suit, which is why both go through IndexOfSuit.
            var suits = FatSuits();
            var bodyStrings = BodyString();

            Assert.True(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, 0, suits, 2, out var wasWearing));
            Assert.Equal(0, wasWearing); // they were in the first suit
            Assert.Equal(2, VMTS1PurchasableOutfitHelper.IndexOfSuit(suits, bodyStrings.GetString(1)));

            // And a second change reports the suit the first one put them in.
            Assert.True(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, 0, suits, 1, out var from));
            Assert.Equal(2, from);
            Assert.Equal(1, VMTS1PurchasableOutfitHelper.IndexOfSuit(suits, bodyStrings.GetString(1)));
        }

        [Fact]
        public void SettingASuitForASimWhoWasNotInTheCatalogueReportsMinusOne()
        {
            // 29's return value is the index the sim was in, and "not wearing one of these" is a
            // real answer to that - not a failure to find one.
            var bodyStrings = BodyString("b999mazzz_01,BODY=b999mazzzlgt");

            Assert.True(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, 0, FatSuits(), 1, out var previous));
            Assert.Equal(-1, previous);
        }

        // ---- the hand slots beside a suit (17-22) --------------------------------------------

        [Fact]
        public void TilEQKeepsTheEntryUpToAndIncludingItsEqualsSign()
        {
            // "up to the =". The returned string is what a new texture is appended to, and
            // Outfit.Read splits a hand entry at the first ',' for the mesh and the first '=' for
            // the texture, so the mesh name and the "HAND=" key both have to be inside it.
            Assert.Equal("HmLO,HAND=", VMTS1PurchasableOutfitHelper.TilEQ("HmLO,HAND=huaolgt"));
            Assert.Equal("HMLC,HAND=", VMTS1PurchasableOutfitHelper.TilEQ("HMLC,HAND=huaclgt"));
            // No '=' means there is no texture to replace, so the entry comes back as it was.
            Assert.Equal("HmLO", VMTS1PurchasableOutfitHelper.TilEQ("HmLO"));
            Assert.Null(VMTS1PurchasableOutfitHelper.TilEQ(null));
        }

        [Fact]
        public void ANewHandTextureReplacesTheOldOneAndNothingElse()
        {
            Assert.Equal("HmLO,HAND=huao_macssx",
                VMTS1PurchasableOutfitHelper.HandSlotWithTexture("HmLO,HAND=huaolgt", "huao_macssx"));
            // Nothing to keep means nothing to write: a hand slot with no value in it has no mesh
            // name to preserve, and STR.SetString would store a bare texture name that Outfit.Read
            // then looks for as an appearance.
            Assert.Null(VMTS1PurchasableOutfitHelper.HandSlotWithTexture("", "huao_macssx"));
            Assert.Null(VMTS1PurchasableOutfitHelper.HandSlotWithTexture(null, "huao_macssx"));
            Assert.Null(VMTS1PurchasableOutfitHelper.HandSlotWithTexture("HmLO,HAND=huaolgt", ""));
        }

        [Fact]
        public void AfterASuitChangeTheSimStillHasHandsAndTheyAreTheNewOnes()
        {
            // Read back through the consumer: Outfit.Read is what turns the six strings into the
            // six hand appearances the renderer asks for, so this is the assertion that a hand
            // slot is still a hand slot - mesh name at the first ',', texture after the first '='.
            var bodyStrings = BodyString();
            Assert.True(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, 0, FatSuits(), 1, out _));

            var outfit = new Outfit();
            outfit.Read(bodyStrings);
            var hands = outfit.LiteralHandgroup.LightSkin;

            Assert.Equal("HmLO.apr", hands.LeftHand.Idle.Name);
            Assert.Equal("HmRO.apr", hands.RightHand.Idle.Name);
            Assert.Equal("HmLP.apr", hands.LeftHand.Pointing.Name);
            Assert.Equal("HmRP.apr", hands.RightHand.Pointing.Name);
            Assert.Equal("HmLC.apr", hands.LeftHand.Fist.Name);
            Assert.Equal("HmRC.apr", hands.RightHand.Fist.Name);

            // The named hand set the suit was wearing, in each gesture's own texture family.
            Assert.Equal("huao_macssx", hands.LeftHand.Idle.TexName);
            Assert.Equal("huap_macssx", hands.LeftHand.Pointing.TexName);
            Assert.Equal("huac_macssx", hands.LeftHand.Fist.TexName);
            Assert.Equal("huao_macssx", hands.RightHand.Idle.TexName);
            Assert.Equal("huap_macssx", hands.RightHand.Pointing.TexName);
            Assert.Equal("huac_macssx", hands.RightHand.Fist.TexName);

            // The invariant the old "=huaolgt" rewrite broke: an appearance name is a name, and
            // this one never held an '=' or a texture.
            foreach (var gesture in AllGestures(outfit))
            {
                Assert.DoesNotContain("=", gesture.Name);
                Assert.EndsWith(".apr", gesture.Name);
            }
        }

        [Fact]
        public void AHandSlotWithNothingInItIsLeftAloneRatherThanFilledWithATexture()
        {
            var bodyStrings = BodyString();
            // A body string that has no hand slots at all: six slots of nothing.
            for (int slot = 17; slot <= 22; slot++)
            {
                bodyStrings.SetString(slot, "", STRLangCode.EnglishUS);
            }

            Assert.True(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, 0, FatSuits(), 1, out _));

            for (int slot = 17; slot <= 22; slot++)
            {
                Assert.Equal("", bodyStrings.GetString(slot));
            }
        }

        [Fact]
        public void OnlyTheDefaultOutfitBringsItsHandgroupWithIt()
        {
            // Slots 17-22 are the sim's own hands, and the other five suit types are sets the sim
            // wears instead: changing a swimsuit does not change anybody's hands.
            var bodyStrings = BodyString();
            var before = string.Join("|", VMTS1BodyTransformHelper.HandSlots.Select(x => bodyStrings.GetString(x)));

            Assert.True(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, 1, FatSuits(), 2, out _));

            Assert.Equal(before, string.Join("|", VMTS1BodyTransformHelper.HandSlots.Select(x => bodyStrings.GetString(x))));
            // ...while the formalwear slot itself moved. The suit calls index a table of body
            // string slots (OutfitTypeToInd), so a change has to land in the slot that table names
            // for the suit type it was given.
            Assert.Equal("b013mafat_01,BODY=b013mafatlgt", bodyStrings.GetString(VMTS1BodyTransformHelper.SlotFormal));
            Assert.Equal("b011mafat_01,BODY=b011mafatlgt", bodyStrings.GetString(VMTS1BodyTransformHelper.SlotDaywear));
        }

        [Fact]
        public void ASuitWithNoHandgroupLeavesTheHandsAlone()
        {
            var bodyStrings = BodyString();
            var suits = new[] { Tuple.Create("b011mafat_01,BODY=b011mafatlgt", "") };
            var before = string.Join("|", VMTS1BodyTransformHelper.HandSlots.Select(x => bodyStrings.GetString(x)));

            Assert.True(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, 0, suits, 0, out _));

            Assert.Equal(before, string.Join("|", VMTS1BodyTransformHelper.HandSlots.Select(x => bodyStrings.GetString(x))));
        }

        // ---- values a lot script made up ----------------------------------------------------

        [Theory]
        [InlineData((short)-1)]  // the pet convention (-1 dog, -2 cat) is not a suit type
        [InlineData((short)-2)]
        [InlineData((short)6)]   // past the end of the table
        [InlineData(short.MaxValue)]
        public void ASuitTypeThatIsNotOneOfTheSixIsRefusedRatherThanThrown(short outfitType)
        {
            var bodyStrings = BodyString();
            var before = bodyStrings.GetString(1);

            Assert.False(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, outfitType, FatSuits(), 1, out var previous));
            Assert.Equal(-1, previous);
            Assert.Equal(before, bodyStrings.GetString(1));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]          // the list has three suits
        [InlineData(99)]
        public void ASuitIndexThatIsNotInTheListIsRefusedAndChangesNothing(int newIndex)
        {
            var bodyStrings = BodyString();
            var before = bodyStrings.GetString(1);
            var hands = string.Join("|", VMTS1BodyTransformHelper.HandSlots.Select(x => bodyStrings.GetString(x)));

            Assert.False(VMTS1PurchasableOutfitHelper.ApplySuit(bodyStrings, 0, FatSuits(), newIndex, out var previous));

            Assert.Equal(-1, previous);
            Assert.Equal(before, bodyStrings.GetString(1));
            Assert.Equal(hands, string.Join("|", VMTS1BodyTransformHelper.HandSlots.Select(x => bodyStrings.GetString(x))));
        }

        [Fact]
        public void AMissingBodyStringOrCatalogueIsRefusedRatherThanThrown()
        {
            Assert.False(VMTS1PurchasableOutfitHelper.ApplySuit(null, 0, FatSuits(), 0, out _));
            Assert.False(VMTS1PurchasableOutfitHelper.ApplySuit(BodyString(), 0, null, 0, out _));
            Assert.False(VMTS1PurchasableOutfitHelper.ApplySuit(BodyString(), 0, new Tuple<string, string>[0], 0, out _));
        }

        // ---- the BCF key a suit list is looked up by ----------------------------------------

        [Theory]
        [InlineData("b011mafat_01,BODY=b011mafatlgt", "mafat")]  // a fat male adult
        [InlineData("b007mafit_01,BODY=b007mafitdrk", "mafit")]
        [InlineData("b023maskn_01", "maskn")]
        [InlineData("b003mcchd_maur,BODY=b003mcchdlgt", "mcchd")] // a child's body
        [InlineData("b012fafat_01", "fafat")]
        [InlineData("b620ma_nodminigunner", "ma")]  // an expansion body, named outside the shape
        [InlineData("b011mafat_01;f011mafat_01,BODY=x", "mafat")] // the ';' list form
        [InlineData("b011mafat", "mafat")]            // no variant suffix at all
        public void TheCatalogueKeyIsTheAppearanceNameWithoutItsNumber(string daywear, string expected)
        {
            // Characters 4 to the underscore. TS1BCFProvider.Init registers exactly this key, and
            // alongside it the build-free "ma"/"fa" one, so this is a key the catalogue has.
            Assert.Equal(expected, VMTS1PurchasableOutfitHelper.SuitAvatarType(daywear));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("b007")]       // too short to be "b007" plus a build
        [InlineData("b011_01")]    // the build is empty
        [InlineData("_abcdn")]     // the underscore is inside the four-character number
        [InlineData("b011,Body=x")]
        public void ABodyStringThatNamesNoBuildIsNoKeyAtAll(string daywear)
        {
            // Every one of these is a body string this engine has to survive. The last two used to
            // throw (a negative Substring length) from inside GetValidOutfits, which is called by
            // all three suit calls with a value straight out of a script register.
            Assert.Null(VMTS1PurchasableOutfitHelper.SuitAvatarType(daywear));
        }

        // ---- the tables the calls index into ------------------------------------------------

        [Fact]
        public void TheSixOutfitTypesAndTheBodyStringSlotsTheyOwn()
        {
            Assert.Equal(new[] { "b", "f", "s", "l", "w", "h" }, VMTS1PurchasableOutfitHelper.OutfitTypes);
            Assert.Equal(VMTS1PurchasableOutfitHelper.OutfitTypes.Length, VMTS1PurchasableOutfitHelper.OutfitTypeToInd.Length);

            // The five collection letters are the first character TS1BCFProvider registers an
            // appearance under, and the slots are the body strings VMSuitProvider reads a TS1
            // person's own suit out of. VMTS1BodyTransformHelper names the same six slots, and the
            // two tables have to agree: 30 and 31 index this one, and 29 writes the slot this one
            // names.
            Assert.Equal((int)VMTS1BodyTransformHelper.SlotDaywear, (int)VMTS1PurchasableOutfitHelper.OutfitTypeToInd[0]);
            Assert.Equal((int)VMTS1BodyTransformHelper.SlotFormal, (int)VMTS1PurchasableOutfitHelper.OutfitTypeToInd[1]);
            Assert.Equal((int)VMTS1BodyTransformHelper.SlotSportswear, (int)VMTS1PurchasableOutfitHelper.OutfitTypeToInd[2]);
            Assert.Equal((int)VMTS1BodyTransformHelper.SlotSleepwear, (int)VMTS1PurchasableOutfitHelper.OutfitTypeToInd[3]);
            Assert.Equal((int)VMTS1BodyTransformHelper.SlotLingerie, (int)VMTS1PurchasableOutfitHelper.OutfitTypeToInd[4]);
            Assert.Equal((int)VMTS1BodyTransformHelper.SlotAccessory, (int)VMTS1PurchasableOutfitHelper.OutfitTypeToInd[5]);
        }

        [Fact]
        public void TheSuitCallsAreTheNumbersTheScriptsSend()
        {
            // Pinned for the same reason the transform's are: the whole file is reached through one
            // byte of operand, and a renumbered enum would point a lot script's call 30 at call 31
            // - which returns a count, not an index, and would look like it worked.
            Assert.Equal(29, (int)VMGenericTS1CallMode.SetStackObjectsSuit);
            Assert.Equal(30, (int)VMGenericTS1CallMode.GetStackObjectsSuit);
            Assert.Equal(31, (int)VMGenericTS1CallMode.CountStackObjectSuits);
        }

        private static Gesture[] AllGestures(Outfit outfit)
        {
            var hands = outfit.LiteralHandgroup.LightSkin;
            return new[]
            {
                hands.LeftHand.Idle, hands.LeftHand.Pointing, hands.LeftHand.Fist,
                hands.RightHand.Idle, hands.RightHand.Pointing, hands.RightHand.Fist,
            };
        }
    }
}
