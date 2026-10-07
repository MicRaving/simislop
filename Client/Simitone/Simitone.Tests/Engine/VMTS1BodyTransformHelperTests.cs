/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The pure half of the TS1 age transforms: global call
    /// <see cref="VMGenericTS1CallMode.ChildToAdult"/> (39) and
    /// <see cref="VMGenericTS1CallMode.PetToAdult"/> (40), which hand a sim who already exists
    /// an adult human body without changing who they are.
    ///
    /// <para>
    /// These are the only two TS1 calls whose failure mode is a <b>corrupt save</b> rather than
    /// a missing feature. A TS1 sim's body is not a property the engine owns; it is 35 strings
    /// in a chunk inside the character's own IFF, and the skeleton the renderer draws comes from
    /// string 0 while the walk animation set comes from a person-data age somewhere else entirely.
    /// Write string 0 without writing the age and the save holds an adult skeleton wearing a
    /// child's body; write the age without string 1 and the sim wears nothing at all. So the
    /// properties worth defending here are not "the transform looks right" - that needs a
    /// lot-script playtest - but:
    ///
    ///   <list type="bullet">
    ///   <item><b>Identity is never touched.</b> Slot 11 is the first name and nothing in the
    ///   replacement set includes it; the family, GUID and relationships are not this helper's
    ///   business at all.</item>
    ///   <item><b>All of the body, or none of it.</b> <see cref="STR.SetString"/> drops an
    ///   out-of-range index silently, so a body string too short for a whole adult body must be
    ///   refused rather than half written.</item>
    ///   <item><b>Every slot the engine reads is written.</b> The 0/12/13/14 the avatar reads on
    ///   init, the 1/2/17-22 it draws the daywear, head and hands from, and the 15/16/27-34 that
    ///   <c>VMSuitProvider</c> names meshes out of when the sim next changes clothes.</item>
    ///   <item><b>The persistent copy is kept in step.</b> <c>VMAvatar.InheritNeighbor</c>
    ///   overwrites the whole runtime person-data array from the NBR on the next lot load, so
    ///   the four slots a transform moves have to be written on both sides.</item>
    ///   <item><b>Repeat calls do nothing.</b> Lot scripts cannot know whether the game has
    ///   already aged this sim, and re-running would re-roll their body.</item>
    ///   </list>
    /// </para>
    ///
    /// <para>
    /// The appearance names used below are the ones a real install ships, read out of
    /// <c>GameData/Skins</c>: <c>b007mafit_01</c> is a male average body whose textures are
    /// <c>b007mafitlgt_lthr.bmp</c> / <c>b007mafitmed_lthr.bmp</c> / <c>b007mafitdrk_lthr.bmp</c>,
    /// and <c>c007ma_01</c> is the matching male head. <see cref="VMTS1BodyTransformHelper"/>
    /// exists to map between that naming and the body strings, so the naming is pinned here too.
    /// </para>
    /// </summary>
    public sealed class VMTS1BodyTransformHelperTests
    {
        // ---- what an adult body is made of ------------------------------------------------

        /// <summary>
        /// The build is the three characters before the underscore, and the BCF catalogue key
        /// drops the four-character "b007" prefix and the skin token, leaving "mafit" - which is
        /// what <c>VMTS1PurchasableOutfitHelper.GetValidOutfits</c> looks up when a grown-up sim
        /// changes clothes. If these two disagree, a transformed sim's swimwear names a mesh
        /// collection that does not exist.
        /// </summary>
        [Theory]
        [InlineData("b007mafit_01", "fit")]
        [InlineData("b011mafat_01", "fat")]
        [InlineData("b012maskn_dave", "skn")]
        [InlineData("b023maskn_01", "skn")]
        [InlineData("b200mafit_ctb", "fit")]
        [InlineData("b590mafit_todd", "fit")]
        [InlineData("B481FAFit_May", "fit")] // the game is not case-consistent about this
        public void ABodyAppearanceNamesItsBuild(string mesh, string build)
        {
            Assert.Equal(build, VMTS1BodyTransformHelper.BuildOf(mesh));
            Assert.Equal(build, VMTS1BodyTransformHelper.BuildOf(mesh.ToLowerInvariant()));
        }

        [Theory]
        [InlineData("b003mcchd_maur")] // a child's body: "chd" is not an adult build
        [InlineData("b620ma_nodminigunner")] // an expansion body named outside the shape
        [InlineData("b603ma_patch2")]
        [InlineData("b007")]
        [InlineData("")]
        [InlineData(null)]
        public void ABodyAppearanceThatNamesNoAdultBuildIsNotOne(string mesh)
        {
            Assert.Null(VMTS1BodyTransformHelper.BuildOf(mesh));
        }

        /// <summary>
        /// The code is five characters and read positionally by <c>SimTemplateCreateInfo</c>:
        /// gender, then the age marker, then the build. The middle character is "a" for an adult
        /// and "c" for a child, and a code of any other length throws inside that constructor -
        /// which is how "fchd" (four characters, build read as "hd?") is caught here rather than
        /// in a lot script.
        /// </summary>
        [Theory]
        [InlineData('m', "b007mafit_01", "mafit")]
        [InlineData('m', "b011mafat_01", "mafat")]
        [InlineData('f', "b013fafit_01", "fafit")]
        [InlineData('f', "b023maskn_01", "faskn")]
        public void TheBodyCodeIsTheGenderTheAgeMarkerAndTheBuild(char gender, string mesh, string expected)
        {
            var code = VMTS1BodyTransformHelper.BodyCodeFor(gender, mesh);

            Assert.Equal(expected, code);
            Assert.Equal(5, code.Length);
            Assert.Equal(gender == 'f' ? 'f' : 'm', code[0]);
            Assert.NotEqual('c', code[1]);
            // And the three characters the constructor reads as the build.
            Assert.Equal(VMTS1BodyTransformHelper.BuildOf(mesh), code.Substring(2, 3));
        }

        [Fact]
        public void AChildsCodeIsTheGenderAndTheFixedChildBuild()
        {
            // A child body names no build ("b003mcchd_maur" -> "chd"), so its code cannot come
            // from BodyCodeFor at all - it is the fixed "mcchd"/"fcchd".
            Assert.Equal("mcchd", VMTS1BodyTransformHelper.ChildBodyCode('m'));
            Assert.Equal("fcchd", VMTS1BodyTransformHelper.ChildBodyCode('f'));
            Assert.Equal(5, VMTS1BodyTransformHelper.ChildBodyCode('m').Length);

            Assert.Null(VMTS1BodyTransformHelper.BodyCodeFor('m', "b003mcchd_maur"));
        }

        [Fact]
        public void ACodeThatIsNotGenderAgeAndAThreeCharacterBuildIsNotACode()
        {
            // These are the shapes that make SimTemplateCreateInfo throw, so they must never
            // reach it. The middle character is fixed and the build is exactly three characters,
            // which is what makes the code five.
            Assert.Null(VMTS1BodyTransformHelper.CodeFor('m', false, "chd"));   // an adult "chd"
            Assert.Null(VMTS1BodyTransformHelper.CodeFor('m', true, "fit"));    // a child "fit"
            Assert.Null(VMTS1BodyTransformHelper.CodeFor('m', false, "fatty"));
            Assert.Null(VMTS1BodyTransformHelper.CodeFor('m', false, "fi"));
            Assert.Null(VMTS1BodyTransformHelper.CodeFor('m', false, null));

            Assert.Equal("mafit", VMTS1BodyTransformHelper.CodeFor('m', false, "fit"));
            Assert.Equal("mcchd", VMTS1BodyTransformHelper.CodeFor('m', true, "chd"));
        }

        [Fact]
        public void ABodyWithNoBuildHasNoCode()
        {
            // The transform must not be able to hand SimTemplateCreateInfo a code it will build
            // mesh names out of, so an unnameable body is no code at all rather than a guess.
            Assert.Null(VMTS1BodyTransformHelper.BodyCodeFor('m', "b003mcchd_maur"));
            Assert.Null(VMTS1BodyTransformHelper.BodyCodeFor('m', "b620ma_nodminigunner"));
        }

        /// <summary>
        /// The BCF keys, checked against how <c>TS1BCFProvider.Init</c> registers them from a real
        /// install's <c>.cmx</c> names: an adult's textures are "ma"/"fa" and a child's are
        /// "mc"/"fc" for heads and "mcchd"/"fcchd" for bodies. This is the key
        /// <c>VMTS1MakeNewCharacter</c> reads, and it used to read "mm"/"fm" for an adult -
        /// a key nothing is registered under, so creating an adult sim through that primitive
        /// threw out of a lot script.
        /// </summary>
        [Fact]
        public void TheAvatarTypeKeysAreTheOnesTheCatalogueRegisters()
        {
            Assert.Equal("ma", VMTS1BodyTransformHelper.AdultAvatarType('m'));
            Assert.Equal("fa", VMTS1BodyTransformHelper.AdultAvatarType('f'));
            Assert.Equal("mc", VMTS1BodyTransformHelper.ChildAvatarType('m'));
            Assert.Equal("fc", VMTS1BodyTransformHelper.ChildAvatarType('f'));
            Assert.Equal("mcchd", VMTS1BodyTransformHelper.ChildBodyAvatarType('m'));
            Assert.Equal("fcchd", VMTS1BodyTransformHelper.ChildBodyAvatarType('f'));

            // An adult's key is never the doubled one the old code produced.
            Assert.DoesNotContain("mm", new[]
            {
                VMTS1BodyTransformHelper.AdultAvatarType('m'),
                VMTS1BodyTransformHelper.AdultAvatarType('f')
            });
        }

        // ---- matching a body against the install's textures -------------------------------

        /// <summary>The three textures a real "b007mafit_01" ships with, plus its head's.</summary>
        private static readonly string[] InstallTextures =
        {
            "b007mafitlgt_lthr.bmp", "b007mafitmed_lthr.bmp", "b007mafitdrk_lthr.bmp",
            "c007malgt_01.bmp", "c007mamed_01.bmp", "c007madrk_01.bmp",
            "huaolgt.bmp", "huaomed.bmp", "huaodrk.bmp",
        };

        [Fact]
        public void TheTexturePrefixIsTheAppearanceNameUpToItsUnderscorePlusTheSkin()
        {
            // The appearance is named without a skin and its three textures with one, plus a
            // per-body variant suffix that varies, so the lookup is a prefix match.
            Assert.Equal("b007mafitdrk", VMTS1BodyTransformHelper.SkinPrefixedTexture("b007mafit_01", "drk"));
            Assert.Equal("c007madrk", VMTS1BodyTransformHelper.SkinPrefixedTexture("c007ma_01", "drk"));
            Assert.Null(VMTS1BodyTransformHelper.SkinPrefixedTexture("b007", "drk"));
            Assert.Null(VMTS1BodyTransformHelper.SkinPrefixedTexture("b007mafit_01", null));
        }

        [Fact]
        public void ABodysTextureIsResolvedForTheSimsOwnSkin()
        {
            var dark = VMTS1BodyTransformHelper.ResolveBodies(
                new[] { "b007mafit_01" }, InstallTextures, "drk", true);

            Assert.Single(dark);
            Assert.Equal("b007mafit_01", dark[0].Mesh);
            // The whole texture name but its extension, trailing per-body variant and all -
            // which is what VMTS1MakeNewCharacter has always written into BODY=, and what
            // Avatar.AddAppearance matches a mesh binding against.
            Assert.Equal("b007mafitdrk_lthr", dark[0].Texture);
            Assert.Equal("fit", dark[0].Build);
        }

        [Theory]
        [InlineData("lgt", "b007mafitlgt_lthr")]
        [InlineData("med", "b007mafitmed_lthr")]
        [InlineData("drk", "b007mafitdrk_lthr")]
        public void EverySkinTheGameHasResolvesToItsOwnTexture(string skin, string expected)
        {
            var bodies = VMTS1BodyTransformHelper.ResolveBodies(
                new[] { "b007mafit_01" }, InstallTextures, skin, true);
            Assert.Equal(expected, Assert.Single(bodies).Texture);
        }

        [Fact]
        public void ABodyThisInstallHasNoTextureForIsNotACandidate()
        {
            // "b011mafat_01" exists; only the dark one was never shipped for it in this install.
            var textures = new[] { "b011mafatlgt_leder.bmp" };

            Assert.Empty(VMTS1BodyTransformHelper.ResolveBodies(new[] { "b011mafat_01" }, textures, "drk", true));
            Assert.Single(VMTS1BodyTransformHelper.ResolveBodies(new[] { "b011mafat_01" }, textures, "lgt", true));
        }

        [Fact]
        public void AnEmptySkinLeavesNoCandidatesRatherThanPickingAnySkin()
        {
            // A sim whose skin token could not be read must not be handed somebody else's body.
            Assert.Empty(VMTS1BodyTransformHelper.ResolveBodies(new[] { "b007mafit_01" }, InstallTextures, null, true));
            Assert.Empty(VMTS1BodyTransformHelper.ResolveHeads(new[] { "c007ma_01" }, InstallTextures, null));
        }

        [Fact]
        public void ABodyWithNoAdultBuildIsOnlyDroppedWhenAnAdultIsBeingBuilt()
        {
            // A child's body names no build and is perfectly good - the build is only needed
            // because an adult's other outfit slots are named from it.
            var meshes = new[] { "b003mcchd_maur", "b007mafit_01" };
            var textures = new[] { "b003mcchddrk_maur.bmp", "b007mafitdrk_lthr.bmp" };

            var forChild = VMTS1BodyTransformHelper.ResolveBodies(meshes, textures, "drk", false);
            Assert.Equal(2, forChild.Count);
            Assert.Equal("b003mcchd_maur", forChild[0].Mesh);
            Assert.Null(forChild[0].Build);

            var forAdult = VMTS1BodyTransformHelper.ResolveBodies(meshes, textures, "drk", true);
            Assert.Equal("b007mafit_01", Assert.Single(forAdult).Mesh);
        }

        [Fact]
        public void AHeadResolvesWithoutABuildOrAHandgroup()
        {
            var head = Assert.Single(VMTS1BodyTransformHelper.ResolveHeads(
                new[] { "c007ma_01", "c008ma_01" }, InstallTextures, "med"));

            Assert.Equal("c007ma_01", head.Mesh);
            Assert.Equal("c007mamed_01", head.Texture);
            Assert.Null(head.Build);
            Assert.Null(head.Handgroup);
        }

        [Fact]
        public void TheHandgroupIsTheNamedHandSetWhenTheInstallHasOneAndTheSkinsHandsOtherwise()
        {
            var textures = new[]
            {
                "b007mafitdrk_lthr.bmp", "huaodrk.bmp",
                "b899mafitdrk_macssx.bmp", "huao_macssx.bmp",
            };

            var bodies = VMTS1BodyTransformHelper.ResolveBodies(
                new[] { "b007mafit_01", "b899mafit_macssx" }, textures, "drk", true);

            Assert.Equal(2, bodies.Count);
            // No "huao_01" in this install, so the plain dark hands are used...
            Assert.Equal("drk", bodies[0].Handgroup);
            // ...but the body named after a hand set gets that hand set. The token is whatever
            // follows "huao", which for a named set begins with an underscore: "huao" + "_macssx"
            // is the texture's own name.
            Assert.Equal("_macssx", bodies[1].Handgroup);
        }

        [Fact]
        public void TheFilterNeverMutatesTheCatalogueItWasGiven()
        {
            // TS1BCFProvider hands out the live List it caches for the whole session. If the
            // filter removed from it, every call would shrink the catalogue a little - and call
            // 16 (ChangeNormalOutfit) makes a lot of calls.
            var catalogue = new List<string> { "b007mafit_01", "b011mafat_01" };
            var textures = new[] { "b007mafitdrk_lthr.bmp" }; // nothing for the fat one

            Assert.Single(VMTS1BodyTransformHelper.ResolveBodies(catalogue, textures, "drk", true));

            Assert.Equal(2, catalogue.Count);
            Assert.Equal(new[] { "b007mafit_01", "b011mafat_01" }, catalogue);
        }

        [Fact]
        public void NoCandidatesIsTheAnswerForAMissingCatalogueOrTextureList()
        {
            Assert.Empty(VMTS1BodyTransformHelper.ResolveBodies(null, InstallTextures, "drk", true));
            Assert.Empty(VMTS1BodyTransformHelper.ResolveBodies(new[] { "b007mafit_01" }, null, "drk", true));
            Assert.Empty(VMTS1BodyTransformHelper.ResolveHeads(new[] { "c007ma_01" }, new string[0], "drk"));
        }

        // ---- the body string set the transform writes --------------------------------------

        private static VMTS1BodyTransformHelper.TS1BodyCandidate FitBody()
        {
            return new VMTS1BodyTransformHelper.TS1BodyCandidate()
            {
                Mesh = "b007mafit_01",
                Texture = "b007mafitdrk",
                Build = "fit",
                Handgroup = "drk"
            };
        }

        private static VMTS1BodyTransformHelper.TS1BodyCandidate Head()
        {
            return new VMTS1BodyTransformHelper.TS1BodyCandidate()
            {
                Mesh = "c007ma_01",
                Texture = "c007madrk"
            };
        }

        [Fact]
        public void TheTransformedBodyStringSaysAdultMaleTwentySevenDaysDark()
        {
            var strings = VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "drk", FitBody(), Head());

            // The four slots VMAvatar reads on init: the skeleton, the sex, the age and the skin.
            Assert.Equal("adult", strings[VMTS1BodyTransformHelper.SlotBodyKind]);
            Assert.Equal("male", strings[VMTS1BodyTransformHelper.SlotGender]);
            Assert.Equal("27", strings[VMTS1BodyTransformHelper.SlotAgeDays]);
            Assert.Equal("drk", strings[VMTS1BodyTransformHelper.SlotSkinColor]);
        }

        [Fact]
        public void TheDaywearAndHeadCarryTheAppearanceAndTheTextureItIsDrawnWith()
        {
            var strings = VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "drk", FitBody(), Head());

            // The "NAME,KEY=VALUE" shape Outfit.Read / Outfit.ReadHead parse, and the exact
            // texture name is the whole point - a body drawn with the wrong one is a sim in
            // somebody else's skin.
            Assert.Equal("b007mafit_01,BODY=b007mafitdrk", strings[VMTS1BodyTransformHelper.SlotDaywear]);
            Assert.Equal("c007ma_01,HEAD-HEAD=c007madrk", strings[VMTS1BodyTransformHelper.SlotHead]);
        }

        [Fact]
        public void EveryHandSlotIsTheSimsOwnHandAndTheBodysHandgroup()
        {
            var strings = VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "drk", FitBody(), Head());

            Assert.Equal("HmLO,HAND=huaodrk", strings[VMTS1BodyTransformHelper.SlotHandLeftOpen]);
            Assert.Equal("HmRO,HAND=huaodrk", strings[VMTS1BodyTransformHelper.SlotHandRightOpen]);
            Assert.Equal("HmLP,HAND=huaodrk", strings[VMTS1BodyTransformHelper.SlotHandLeftPoint]);
            Assert.Equal("HmRP,HAND=huaodrk", strings[VMTS1BodyTransformHelper.SlotHandRightPoint]);
            // The closed pair is "LC"/"RC" - what the name means, and what SetSuit writes. A "LO"
            // here would leave a fist showing an open left hand.
            Assert.Equal("HmLC,HAND=huaodrk", strings[VMTS1BodyTransformHelper.SlotHandLeftFist]);
            Assert.Equal("HmRC,HAND=huaodrk", strings[VMTS1BodyTransformHelper.SlotHandRightFist]);

            foreach (var slot in VMTS1BodyTransformHelper.HandSlots)
            {
                Assert.Equal("huaodrk", strings[slot].Split("HAND=")[1]);
            }
        }

        [Fact]
        public void AFemaleSimGetsTheFemaleHandsAndTheFemaleGender()
        {
            var body = new VMTS1BodyTransformHelper.TS1BodyCandidate()
            {
                Mesh = "b013fafat_01", Texture = "b013fafatlgt", Build = "fat", Handgroup = "lgt"
            };
            var head = new VMTS1BodyTransformHelper.TS1BodyCandidate()
            {
                Mesh = "c009fc_wizd", Texture = "c009fclgt"
            };

            var strings = VMTS1BodyTransformHelper.BuildAdultBodyStrings('f', "lgt", body, head);

            Assert.Equal("female", strings[VMTS1BodyTransformHelper.SlotGender]);
            Assert.Equal("HfLO,HAND=huaolgt", strings[VMTS1BodyTransformHelper.SlotHandLeftOpen]);
            Assert.Equal("HfRC,HAND=huaolgt", strings[VMTS1BodyTransformHelper.SlotHandRightFist]);
            // The per-outfit fallbacks follow the body code, so a fat female's underwear and
            // swimwear are named off "fat" and the female mesh letter, not the male "mfat".
            Assert.Equal("nffat_01,BODY=uffatlgtlguard_01", strings[VMTS1BodyTransformHelper.SlotUnderwear]);
            Assert.Equal("fffat_01,BODY=fffatlgt_01", strings[27]);
        }

        [Fact]
        public void TheOutfitFallbacksAreNamedOffTheBodysOwnBuild()
        {
            // VMSuitProvider.GetPersonSuitTS1 rebuilds a four-character code (gender plus
            // fat/fit/skn) out of body string 1 when the sim next changes clothes, so the mesh
            // names in these slots have to line up with the ones it composes. "f100mafit_original"
            // is The Sims' own name for a male average adult's formalwear; a code missing the "a"
            // would ask for "f100mfit_original", which does not exist.
            var strings = VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "drk", FitBody(), Head());

            Assert.Equal("nmfit_01,BODY=nmfitdrk_01", strings[VMTS1BodyTransformHelper.SlotNaked]);
            Assert.Equal("nmfit_01,BODY=umfitdrklguard_01", strings[VMTS1BodyTransformHelper.SlotUnderwear]);
            Assert.Equal("f100mafit_original,BODY=f100mafitdrk_original", strings[VMTS1BodyTransformHelper.SlotFormal]);
            Assert.Equal("s100mafit_original,BODY=s100mafitdrk_original", strings[VMTS1BodyTransformHelper.SlotSportswear]);
            Assert.Equal("l100mafit_original,BODY=l100mafitdrk_original", strings[VMTS1BodyTransformHelper.SlotSleepwear]);
            Assert.Equal("w100mafit_original,BODY=w100mafitdrk_original", strings[VMTS1BodyTransformHelper.SlotLingerie]);
            Assert.Equal("h533mafit_zoot,BODY=h533mafitdrk_zoot", strings[VMTS1BodyTransformHelper.SlotAccessory]);
        }

        [Fact]
        public void TheTransformNeverTouchesTheName()
        {
            // Slot 11 is the first name. A grown-up sim is the same sim: this is the single
            // property that distinguishes a transform from making a new neighbour, and
            // VMTS1MakeNewCharacter cannot be used for this at all because it makes a new one.
            var strings = VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "drk", FitBody(), Head());
            Assert.DoesNotContain(VMTS1BodyTransformHelper.SlotFirstName, strings.Keys);
        }

        [Fact]
        public void TheReplacementSetCoversEverySlotTheEngineReadsAndStopsThere()
        {
            var strings = VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "drk", FitBody(), Head());

            // Nothing above the accessory slot: the rest of a body string is other outfits and
            // localisations, and rewriting them is not a transform's business.
            Assert.All(strings.Keys, slot => Assert.InRange(slot, 0, VMTS1BodyTransformHelper.HighestSlotWritten));

            // And everything the engine reads is in there - this is the "adult skeleton, child
            // body" list.
            var required = new[]
            {
                VMTS1BodyTransformHelper.SlotBodyKind,       // 0
                VMTS1BodyTransformHelper.SlotDaywear,        // 1
                VMTS1BodyTransformHelper.SlotHead,           // 2
                VMTS1BodyTransformHelper.SlotGender,         // 12
                VMTS1BodyTransformHelper.SlotAgeDays,        // 13
                VMTS1BodyTransformHelper.SlotSkinColor,      // 14
                VMTS1BodyTransformHelper.SlotNaked,          // 15
                VMTS1BodyTransformHelper.SlotUnderwear,      // 16
            }.Concat(VMTS1BodyTransformHelper.HandSlots)
             .Concat(new[]
             {
                 VMTS1BodyTransformHelper.SlotTeddyLeft,
                 VMTS1BodyTransformHelper.SlotTeddyRight,
                 VMTS1BodyTransformHelper.SlotFormal,
                 VMTS1BodyTransformHelper.SlotSportswear,
                 VMTS1BodyTransformHelper.SlotSleepwear,
                 VMTS1BodyTransformHelper.SlotLingerie,
                 VMTS1BodyTransformHelper.SlotAccessory,
             });

            foreach (var slot in required)
            {
                Assert.True(strings.ContainsKey(slot), "slot " + slot + " must be written by a transform");
            }
        }

        // ---- writing it, all or nothing ---------------------------------------------------

        /// <summary>A body string shaped like a real Template Person's: 40 slots of text.</summary>
        private static STR BodyString(params string[] contents)
        {
            var str = new STR();
            for (int i = 0; i < contents.Length; i++)
            {
                str.InsertString(0, new STRItem(contents[contents.Length - 1 - i]));
            }
            return str;
        }

        /// <summary>A child's own body string, as it is on disk before a transform.</summary>
        private static STR ChildBodyString()
        {
            return BodyString(
                "child",                                 // 0
                "b003mcchdlgt_maur,BODY=b003mcchdlgt",   // 1
                "c010mclgt_maur,HEAD-HEAD=c010mclgt",    // 2
                "", "", "", "", "", "", "", "",          // 3-10
                "Bebe",                                  // 11 - the name, which must survive
                "male",                                  // 12
                "9",                                     // 13
                "lgt",                                   // 14
                "nuc_01,BODY=nucdrk_01",                 // 15
                "nuc_01,BODY=uuchdlgtundies_01",         // 16
                "", "", "", "", "", "",                  // 17-22
                "", "", "", "",                          // 23-26
                "ffchd_01,BODY=ffchdlgt_01",             // 27
                "HmLO,HAND=gmao_yeti",                   // 28
                "HmRO,HAND=gmao_yeti",                   // 29
                "ADDED", "ADDED", "ADDED", "ADDED", "ADDED", // 30-34
                "", "", "", "", "");                     // 35-39
        }

        [Fact]
        public void ATransformOverAChildsBodyStringLeavesTheChildsNameAndNothingElseBehind()
        {
            var bodyStrings = ChildBodyString();
            var name = bodyStrings.GetString(VMTS1BodyTransformHelper.SlotFirstName);
            Assert.Equal("Bebe", name);

            var written = VMTS1BodyTransformHelper.ApplyBodyStrings(bodyStrings,
                VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "lgt",
                    new VMTS1BodyTransformHelper.TS1BodyCandidate()
                    {
                        Mesh = "b007mafit_01", Texture = "b007mafitlgt", Build = "fit", Handgroup = "lgt"
                    },
                    new VMTS1BodyTransformHelper.TS1BodyCandidate()
                    {
                        Mesh = "c007ma_01", Texture = "c007malgt"
                    }));

            Assert.True(written);
            Assert.Equal("adult", bodyStrings.GetString(0));
            Assert.Equal("27", bodyStrings.GetString(13));
            Assert.Equal("Bebe", bodyStrings.GetString(VMTS1BodyTransformHelper.SlotFirstName));
        }

        [Fact]
        public void ABodyStringTooShortForAnAdultBodyIsRefusedWhole()
        {
            // The whole point: STR.SetString drops an out-of-range index silently, so a short
            // body string must be refused rather than have slot 0 say "adult" while slots 1-16
            // still hold a child's values.
            var bodyStrings = BodyString(
                "child",                        // 0
                "b003mcchdlgt_maur,BODY=...",   // 1
                "c010mclgt_maur,HEAD-HEAD=...", // 2
                "", "", "", "", "", "", "", "",  // 3-10
                "Bebe",                         // 11
                "male",                         // 12
                "9",                            // 13
                "lgt");                         // 14
            Assert.False(VMTS1BodyTransformHelper.CanHoldAdultBody(bodyStrings));

            var written = VMTS1BodyTransformHelper.ApplyBodyStrings(bodyStrings,
                VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "lgt", FitBody(), Head()));

            Assert.False(written);
            Assert.Equal("child", bodyStrings.GetString(0));
            Assert.Equal("Bebe", bodyStrings.GetString(11));
        }

        [Fact]
        public void ABodyStringThatIsMissingOneMiddleSlotIsAlsoRefusedWhole()
        {
            // 40 slots, but slot 22 is a null entry: GetStringEntry returns null for it even
            // though the array is long enough, and that is the case a Length check would miss.
            var bodyStrings = ChildBodyString();
            bodyStrings.LanguageSets[0].Strings[VMTS1BodyTransformHelper.SlotHandRightFist] = null;

            Assert.False(VMTS1BodyTransformHelper.CanHoldAdultBody(bodyStrings));
            Assert.False(VMTS1BodyTransformHelper.ApplyBodyStrings(bodyStrings,
                VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "lgt", FitBody(), Head())));
            Assert.Equal("child", bodyStrings.GetString(0));
        }

        [Fact]
        public void ANullBodyStringOrReplacementSetIsRefusedRatherThanThrown()
        {
            Assert.False(VMTS1BodyTransformHelper.CanHoldAdultBody(null));
            Assert.False(VMTS1BodyTransformHelper.ApplyBodyStrings(null,
                VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "lgt", FitBody(), Head())));
            Assert.False(VMTS1BodyTransformHelper.ApplyBodyStrings(ChildBodyString(), null));
        }

        // ---- the persistent half: person data ----------------------------------------------

        /// <summary>A neighbour's person data as the NBR stores it: 88 entries.</summary>
        private static short[] NeighbourPersonData()
        {
            var personData = new short[88];
            personData[(int)VMPersonDataVariable.NeighborId] = 12;
            personData[(int)VMPersonDataVariable.PersonsAge] = 9;      // a newborn
            personData[(int)VMPersonDataVariable.SkinColor] = 1;      // medium
            personData[(int)VMPersonDataVariable.Gender] = 8;         // a male dog
            personData[(int)VMPersonDataVariable.TS1ScalingSim] = 55; // and a small one
            personData[(int)VMPersonDataVariable.TS1FamilyNumber] = 3;
            personData[(int)VMPersonDataVariable.BodySkill] = 700;
            return personData;
        }

        [Fact]
        public void ThePersonDataAFamilyPetBecomesIsAHumanOfTheSameSexAndSkin()
        {
            var personData = VMTS1BodyTransformHelper.TransformedPersonData('m', "med", NeighbourPersonData());

            Assert.Equal((short)27, personData[(int)VMPersonDataVariable.PersonsAge]);
            Assert.Equal((short)1, personData[(int)VMPersonDataVariable.SkinColor]);
            // 8 (dogmale) -> 0 (male): IsDog/IsCat/IsPet all read this slot, and a sim left
            // flagged as a dog is unroutable and unsaveable as a person.
            Assert.Equal((short)0, personData[(int)VMPersonDataVariable.Gender]);
            Assert.Equal((short)100, personData[(int)VMPersonDataVariable.TS1ScalingSim]);

            // And the sim is still the same sim.
            Assert.Equal((short)12, personData[(int)VMPersonDataVariable.NeighborId]);
            Assert.Equal((short)3, personData[(int)VMPersonDataVariable.TS1FamilyNumber]);
            Assert.Equal((short)700, personData[(int)VMPersonDataVariable.BodySkill]);
        }

        [Fact]
        public void ThePersonDataAFemaleChildBecomesIsAFemaleAdult()
        {
            var personData = VMTS1BodyTransformHelper.TransformedPersonData('f', "drk", NeighbourPersonData());

            Assert.Equal((short)27, personData[(int)VMPersonDataVariable.PersonsAge]);
            Assert.Equal((short)2, personData[(int)VMPersonDataVariable.SkinColor]);
            Assert.Equal((short)1, personData[(int)VMPersonDataVariable.Gender]);
        }

        [Fact]
        public void TransformedPersonDataDoesNotEditTheArrayItWasGiven()
        {
            var original = NeighbourPersonData();
            VMTS1BodyTransformHelper.TransformedPersonData('m', "med", original);
            Assert.Equal((short)9, original[(int)VMPersonDataVariable.PersonsAge]);
        }

        [Fact]
        public void ATruncatedOrMissingPersonDataBlockIsRefusedRatherThanThrown()
        {
            // InheritNeighbor overwrites the runtime array from the chunk, so a short block is a
            // real (if rare) save state - and an out-of-range write would throw from a lot
            // script partway through a transform.
            Assert.False(VMTS1BodyTransformHelper.ApplyPersonData(null, 27, 0, 0, 100));
            Assert.False(VMTS1BodyTransformHelper.ApplyPersonData(new short[10], 27, 0, 0, 100));
            Assert.False(VMTS1BodyTransformHelper.ApplyPersonData(new short[0], 27, 0, 0, 100));
        }

        [Fact]
        public void TheFourSlotsATransformMovesAreTheOnesInitBodyDataAndTheWorldRead()
        {
            // Pinned because they are the reason the NBR has to be written at all: a transform
            // that only changed the body strings would put the old age and scale back on the
            // next lot load, when InheritNeighbor restores person data from the chunk.
            var personData = new short[88];
            Assert.True(VMTS1BodyTransformHelper.ApplyPersonData(personData, 27, 2, 1, 100));

            Assert.Equal(27, personData[(int)VMPersonDataVariable.PersonsAge]);
            Assert.Equal(2, personData[(int)VMPersonDataVariable.SkinColor]);
            Assert.Equal(1, personData[(int)VMPersonDataVariable.Gender]);
            Assert.Equal(100, personData[(int)VMPersonDataVariable.TS1ScalingSim]);

            // A 88-entry NBR block has to be able to hold all four.
            Assert.True(personData.Length > (int)VMPersonDataVariable.TS1ScalingSim);
        }

        // ---- skin and gender ---------------------------------------------------------------

        [Theory]
        [InlineData((short)0, "lgt")]
        [InlineData((short)1, "med")]
        [InlineData((short)2, "drk")]
        public void APersonDataSkinNumberIsASkinToken(short stored, string expected)
        {
            Assert.Equal(expected, VMTS1BodyTransformHelper.SkinTokenFor(stored));
            Assert.Equal(stored, VMTS1BodyTransformHelper.SkinColorFor(expected));
        }

        [Theory]
        [InlineData((short)-1)]
        [InlineData((short)3)]
        [InlineData(short.MaxValue)]
        public void APersonDataSkinNumberTheGameDoesNotHaveIsNotASkin(short stored)
        {
            // A raw short in a hand-edited save. Returning "no skin" is what makes the transform
            // refuse rather than throw.
            Assert.Null(VMTS1BodyTransformHelper.SkinTokenFor(stored));
            Assert.Equal((short)-1, VMTS1BodyTransformHelper.SkinColorFor("chartreuse"));
        }

        [Theory]
        [InlineData("lgt", "lgt")]
        [InlineData("LGT", "lgt")]
        [InlineData("  med  ", "med")]
        [InlineData("drk;lgt;med", "drk")] // the semicolon-list form the TSO-side reader tolerates
        public void ABodyStringSkinIsNormalisedToAToken(string raw, string expected)
        {
            Assert.Equal(expected, VMTS1BodyTransformHelper.NormalizeSkinToken(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(";lgt")]
        [InlineData("blue")]
        public void ABodyStringSkinThatIsNotATokenIsNotASkin(string raw)
        {
            Assert.Null(VMTS1BodyTransformHelper.NormalizeSkinToken(raw));
        }

        [Theory]
        [InlineData((short)0, 'm')]  // male
        [InlineData((short)1, 'f')]  // female
        [InlineData((short)8, 'm')]  // dogmale
        [InlineData((short)9, 'f')]  // dogfemale
        [InlineData((short)16, 'm')] // catmale
        [InlineData((short)17, 'f')] // catfemale
        public void APetsSexSurvivesTheTransform(short gender, char expected)
        {
            // The low bit is the sex in all six values, which is what lets PetToAdult keep it.
            Assert.Equal(expected, VMTS1BodyTransformHelper.GenderCharFor(gender));
        }

        [Fact]
        public void TheHumanGenderForEachSexIsTheNumberInitBodyDataExpects()
        {
            Assert.Equal((short)0, VMTS1BodyTransformHelper.HumanGenderFor('m'));
            Assert.Equal((short)1, VMTS1BodyTransformHelper.HumanGenderFor('f'));
        }

        // ---- idempotence -------------------------------------------------------------------

        [Theory]
        [InlineData("child", false, true)]
        [InlineData("child", true, false)]  // a child is not a pet
        [InlineData("dog", true, true)]
        [InlineData("kat", true, true)]
        [InlineData("cat", true, true)]     // both spellings of a cat turn up in body strings
        [InlineData("dog", false, false)]
        [InlineData("adult", false, false)] // already grown up
        [InlineData("adult", true, false)]
        [InlineData("", false, false)]
        [InlineData(null, false, false)]
        [InlineData(null, true, false)]
        public void ATransformOnlyFiresOnTheBodyItIsFor(string bodyKind, bool fromPet, bool expected)
        {
            Assert.Equal(expected, VMTS1BodyTransformHelper.CanTransform(bodyKind, fromPet));
        }

        [Fact]
        public void ArepeatCallIsANoOpRatherThanANewBody()
        {
            // The property that makes the calls safe to call from a lot script that cannot know
            // whether the game has already aged this sim. Re-running would re-roll the body.
            Assert.True(VMTS1BodyTransformHelper.CanTransform("child", false));
            Assert.False(VMTS1BodyTransformHelper.CanTransform("adult", false));
            Assert.False(VMTS1BodyTransformHelper.CanTransform("adult", true));
        }

        [Fact]
        public void ABodyStringSaysChildUntilTheTransformWritesIt()
        {
            // A whole cycle on one chunk, which is the shape of the whole call: child in,
            // adult out, and a second pass over the result changes nothing.
            var bodyStrings = ChildBodyString();
            var replacements = VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "lgt",
                new VMTS1BodyTransformHelper.TS1BodyCandidate()
                {
                    Mesh = "b007mafit_01", Texture = "b007mafitlgt", Build = "fit", Handgroup = "lgt"
                },
                new VMTS1BodyTransformHelper.TS1BodyCandidate()
                {
                    Mesh = "c007ma_01", Texture = "c007malgt"
                });

            Assert.True(VMTS1BodyTransformHelper.IsChildBody(bodyStrings.GetString(0)));
            Assert.True(VMTS1BodyTransformHelper.ApplyBodyStrings(bodyStrings, replacements));

            var afterFirst = bodyStrings.GetString(0) + "|" + bodyStrings.GetString(1) + "|"
                + bodyStrings.GetString(2) + "|" + bodyStrings.GetString(13);

            Assert.True(VMTS1BodyTransformHelper.IsAdultBody(bodyStrings.GetString(0)));
            Assert.False(VMTS1BodyTransformHelper.CanTransform(bodyStrings.GetString(0), false));
            // Writing the same set again is a no-op - the strings are already the adult ones.
            Assert.True(VMTS1BodyTransformHelper.ApplyBodyStrings(bodyStrings, replacements));
            Assert.Equal(afterFirst, bodyStrings.GetString(0) + "|" + bodyStrings.GetString(1) + "|"
                + bodyStrings.GetString(2) + "|" + bodyStrings.GetString(13));
        }

        [Fact]
        public void TheAgesTheGameStoresAreNineDaysForANewbornAndTwentySevenForAnAdult()
        {
            // Every age comparison in the engine is "PersonsAge < 18", so the two numbers the
            // body string and the person data hold have to be on opposite sides of it.
            Assert.True(VMTS1BodyTransformHelper.NewbornAgeDays < 18);
            Assert.True(VMTS1BodyTransformHelper.AdultAgeDays >= 18);
            Assert.Equal("27", VMTS1BodyTransformHelper.AdultAgeDays.ToString());
            Assert.Equal(100, VMTS1BodyTransformHelper.FullScale);
        }

        [Fact]
        public void TheCallModesAreTheNumbersTheScriptsSend()
        {
            // Pinned because the whole file is reached through one byte of operand, and a
            // renumbered enum would silently point a lot script's call 39 at something else.
            Assert.Equal(39, (int)VMGenericTS1CallMode.ChildToAdult);
            Assert.Equal(40, (int)VMGenericTS1CallMode.PetToAdult);
        }
    }
}
