using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Covers the pure decision logic behind CAS family editing (<c>Simitone.Client.Utils.CasEdit</c>).
    ///
    /// These helpers exist because the in-place edit path is the only CAS flow that writes over
    /// neighbourhood data which already exists, and the risky parts of it are all pure decisions:
    /// rebuilding a FAMI's GUID list without duplicates or 0s, deciding whether a CAS member is an
    /// existing sim or a new one, and recovering the body/head/handgroup/skin values that
    /// <c>CASToNeighGen</c> is going to write straight back out. Each of those is a silent
    /// corruption of a character IFF if it is wrong, and none of them are reachable from a
    /// headless test through the UI, so they are pinned here.
    /// </summary>
    public sealed class CasEditTests
    {
        [Fact]
        public void MergeFamilyGuids_NullInputs_YieldEmpty()
        {
            Assert.Empty(CasEdit.MergeFamilyGuids(null, null, null));
            Assert.Empty(CasEdit.MergeFamilyGuids(new uint[0], null, null));
        }

        [Fact]
        public void MergeFamilyGuids_NoChanges_KeepsOriginalOrder()
        {
            var result = CasEdit.MergeFamilyGuids(new uint[] { 10, 20, 30 }, new uint[0], new uint[0]);
            Assert.Equal(new uint[] { 10, 20, 30 }, result);
        }

        [Fact]
        public void MergeFamilyGuids_AddsAppendAfterExistingInOrder()
        {
            var result = CasEdit.MergeFamilyGuids(new uint[] { 10, 20 }, new uint[] { 40, 50 }, new uint[0]);
            Assert.Equal(new uint[] { 10, 20, 40, 50 }, result);
        }

        [Fact]
        public void MergeFamilyGuids_RemovesAskedGuids()
        {
            var result = CasEdit.MergeFamilyGuids(new uint[] { 10, 20, 30 }, new uint[0], new uint[] { 20 });
            Assert.Equal(new uint[] { 10, 30 }, result);
        }

        [Fact]
        public void MergeFamilyGuids_RemoveAndAddTogether()
        {
            //the realistic edit: one sim deleted, one sim added.
            var result = CasEdit.MergeFamilyGuids(new uint[] { 10, 20, 30 }, new uint[] { 40 }, new uint[] { 20 });
            Assert.Equal(new uint[] { 10, 30, 40 }, result);
        }

        [Fact]
        public void MergeFamilyGuids_NeverProducesDuplicates()
        {
            //duplicates already present in the source data, plus one that is both added and kept.
            var result = CasEdit.MergeFamilyGuids(new uint[] { 10, 10, 20 }, new uint[] { 20, 30, 30 }, new uint[0]);
            Assert.Equal(new uint[] { 10, 20, 30 }, result);
        }

        [Fact]
        public void MergeFamilyGuids_SkipsZeroGuids()
        {
            //0 is not a valid neighbour. A FAMI holding it would break
            //VMContext.CreateObjectInstance for the whole family, so it can never be written back.
            var result = CasEdit.MergeFamilyGuids(new uint[] { 0, 10 }, new uint[] { 0, 20 }, new uint[0]);
            Assert.Equal(new uint[] { 10, 20 }, result);
        }

        [Fact]
        public void MergeFamilyGuids_RemovalBeatsAdd()
        {
            //a guid asked for in both sets is treated as removed, never as a re-add.
            var result = CasEdit.MergeFamilyGuids(new uint[] { 10 }, new uint[] { 99 }, new uint[] { 99 });
            Assert.Equal(new uint[] { 10 }, result);
        }

        [Fact]
        public void MergeFamilyGuids_RemovingEverythingYieldsEmpty()
        {
            var result = CasEdit.MergeFamilyGuids(new uint[] { 10, 20 }, new uint[] { 30 }, new uint[] { 10, 20, 30 });
            Assert.Empty(result);
        }

        [Fact]
        public void MergeFamilyGuids_DoesNotMutateInputs()
        {
            var existing = new uint[] { 10, 20, 30 };
            var add = new uint[] { 40 };
            var remove = new uint[] { 20 };
            CasEdit.MergeFamilyGuids(existing, add, remove);
            Assert.Equal(new uint[] { 10, 20, 30 }, existing);
            Assert.Equal(new uint[] { 40 }, add);
            Assert.Equal(new uint[] { 20 }, remove);
        }

        [Fact]
        public void MergeFamilyGuids_IsIdempotent()
        {
            //merging the same add/remove sets twice must not grow the list.
            var existing = new uint[] { 10, 20, 30 };
            var add = new uint[] { 40, 50 };
            var remove = new uint[] { 20 };
            var once = CasEdit.MergeFamilyGuids(existing, add, remove);
            var twice = CasEdit.MergeFamilyGuids(once, add, remove);
            Assert.Equal(once, twice);
        }

        [Fact]
        public void BackupPathFor_AppendsBakSuffix()
        {
            Assert.Equal("C:/UserData/Characters/User00001.iff.bak",
                CasEdit.BackupPathFor("C:/UserData/Characters/User00001.iff"));
        }

        [Fact]
        public void BackupPathFor_DoesNotAppendTwice()
        {
            //the suffix contract is literal: the caller must not feed an already-backed-up path in.
            Assert.Equal("user00001.iff.bak.bak", CasEdit.BackupPathFor("user00001.iff.bak"));
        }

        [Fact]
        public void BackupPathFor_Null_ReturnsNullRatherThanThrowing()
        {
            Assert.Null(CasEdit.BackupPathFor(null));
        }

        [Fact]
        public void BackupPathFor_EmptyPathIsStillUsable()
        {
            Assert.Equal(".bak", CasEdit.BackupPathFor(""));
        }

        [Fact]
        public void BackupDirectoryFor_IsASubfolderOfCharacters()
        {
            //It must NOT be the Characters directory itself: LoadCharacters enumerates that
            //directory and aborts the whole loop on the first non-".iff" file, so a .bak sitting
            //next to the characters would leave some characters unregistered.
            var dir = CasEdit.BackupDirectoryFor("C:/UserData/Characters");
            Assert.NotEqual("C:/UserData/Characters", dir);
            Assert.StartsWith("C:/UserData/Characters", dir);
            Assert.EndsWith(CasEdit.BackupFolderName, dir);
        }

        [Fact]
        public void BackupDirectoryFor_KeepsTheIffExtensionOfTheCopiedFile()
        {
            //composed with BackupPathFor, the backup is a full IFF name inside a subfolder.
            var target = System.IO.Path.Combine(
                CasEdit.BackupDirectoryFor("C:/UserData/Characters"), "User00007.iff");
            Assert.Equal(target + CasEdit.BackupSuffix, CasEdit.BackupPathFor(target));
            //and the copy itself is a real, openable IFF: only the final suffix differs.
            Assert.EndsWith("User00007.iff" + CasEdit.BackupSuffix, CasEdit.BackupPathFor(target));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void BackupDirectoryFor_NullOrEmpty_ReturnsNull(string input)
        {
            Assert.Null(CasEdit.BackupDirectoryFor(input));
        }

        [Theory]
        [InlineData(0u, false)]
        [InlineData(1u, true)]
        [InlineData(uint.MaxValue, true)]
        [InlineData(0x7FD96B54u, true)]
        public void ShouldRewriteInPlace_NonZeroGuidRewrites(uint guid, bool expected)
        {
            Assert.Equal(expected, CasEdit.ShouldRewriteInPlace(guid));
        }

        [Fact]
        public void ShouldRewriteInPlace_ZeroGuidAlwaysCreates()
        {
            //RefGUID 0 is the default of a freshly built CASFamilyMember, so "not an existing sim"
            //must be the answer for every non-neighbour value.
            Assert.False(CasEdit.ShouldRewriteInPlace(0));
        }

        [Fact]
        public void FirstBodyStringEntry_TakesTheFirstAlternative()
        {
            //this is exactly what Outfit.Read(STR) does with a ';'-separated body list.
            Assert.Equal("mab,BODY=mab_lgt", CasEdit.FirstBodyStringEntry("mab,BODY=mab_lgt;fab,BODY=fab_lgt"));
        }

        [Fact]
        public void FirstBodyStringEntry_NoSemicolonReturnsInput()
        {
            Assert.Equal("mab,BODY=mab_lgt", CasEdit.FirstBodyStringEntry("mab,BODY=mab_lgt"));
        }

        [Fact]
        public void FirstBodyStringEntry_NullOrEmptyIsEmpty()
        {
            Assert.Equal("", CasEdit.FirstBodyStringEntry(null));
            Assert.Equal("", CasEdit.FirstBodyStringEntry(""));
            Assert.Equal("", CasEdit.FirstBodyStringEntry(";"));
        }

        [Theory]
        [InlineData("mab,BODY=mab_lgt", "mab")]
        [InlineData("hab,HEAD-HEAD=hab_drk", "hab")]
        [InlineData("fafat", "fafat")]
        [InlineData("mab", "mab")]
        public void AppearanceId_TakesEverythingBeforeTheFirstComma(string input, string expected)
        {
            Assert.Equal(expected, CasEdit.AppearanceId(input));
        }

        [Fact]
        public void AppearanceId_StripsAnAlreadyPresentAprSuffix()
        {
            //a value that has been through Outfit.ReadHead carries ".apr"; the CAS stores the bare
            //id, so it has to come back off rather than being doubled up.
            Assert.Equal("hab", CasEdit.AppearanceId("hab.apr,HEAD-HEAD=hab_drk"));
            Assert.Equal("hab", CasEdit.AppearanceId("hab.APR,HEAD-HEAD=hab_drk"));
        }

        [Fact]
        public void AppearanceId_TrimsSurroundingSpace()
        {
            Assert.Equal("mab", CasEdit.AppearanceId(" mab ,BODY=mab_lgt"));
        }

        [Fact]
        public void AppearanceId_NullOrEmptyIsEmpty()
        {
            Assert.Equal("", CasEdit.AppearanceId(null));
            Assert.Equal("", CasEdit.AppearanceId(""));
            Assert.Equal("", CasEdit.AppearanceId(",BODY=x"));
        }

        [Theory]
        [InlineData("mab,BODY=mab_lgt", "mab_lgt")]
        [InlineData("hab,HEAD-HEAD=hab_drk", "hab_drk")]
        [InlineData("BODY=", "")]
        [InlineData("a=1=2", "1=2")]
        public void TextureId_TakesEverythingAfterTheFirstEquals(string input, string expected)
        {
            Assert.Equal(expected, CasEdit.TextureId(input));
        }

        [Fact]
        public void TextureId_NoEqualsIsNull()
        {
            //matches Outfit.ToTex: "assume there is one texture bound to the appearance" - if
            //there is no '=' there is no texture reference at all.
            Assert.Null(CasEdit.TextureId("mab"));
            Assert.Null(CasEdit.TextureId(null));
            Assert.Null(CasEdit.TextureId(""));
        }

        [Fact]
        public void TextureId_RoundTripsWithAppearanceId()
        {
            //the write side is "Body + \",BODY=\" + BodyTex" in CASToNeighGen, so decoding that
            //string has to give the exact pair back.
            const string body = "mab", bodyTex = "mab_lgt";
            var stored = body + ",BODY=" + bodyTex;
            Assert.Equal(body, CasEdit.AppearanceId(CasEdit.FirstBodyStringEntry(stored)));
            Assert.Equal(bodyTex, CasEdit.TextureId(CasEdit.FirstBodyStringEntry(stored)));
        }

        [Theory]
        //CASToNeighGen writes "huao" + HandgroupTex, so a CAS-created sim round trips exactly.
        [InlineData("huaolgt", "lgt")]
        [InlineData("huaoY", "Y")]
        //A shipped TS1 character uses the underscore form, which strips to "_lgt" - and that is
        //exactly what has to be written back for the texture reference to resolve.
        [InlineData("huao_lgt", "_lgt")]
        public void HandgroupTexFrom_StripsTheHuaoPrefix(string input, string expected)
        {
            Assert.Equal(expected, CasEdit.HandgroupTexFrom(input));
        }

        [Fact]
        public void HandgroupTexFrom_NoPrefixIsReturnedUnchanged()
        {
            //A different naming scheme entirely (the shipped "huafem"/"huamd" family of names).
            //There is nothing to strip, so the value is passed through rather than mangled.
            Assert.Equal("huafem", CasEdit.HandgroupTexFrom("huafem"));
            Assert.Equal("lgt", CasEdit.HandgroupTexFrom("lgt"));
        }

        [Fact]
        public void HandgroupTexFrom_NullOrEmptyIsEmpty()
        {
            Assert.Equal("", CasEdit.HandgroupTexFrom(null));
            Assert.Equal("", CasEdit.HandgroupTexFrom(""));
            //"huao" on its own leaves an empty suffix rather than exploding.
            Assert.Equal("", CasEdit.HandgroupTexFrom("huao"));
        }

        [Fact]
        public void HandgroupTexFrom_RoundTripsTheWriteSide()
        {
            //CASToNeighGen writes "huao" + HandgroupTex, so decoding that must give it back.
            const string handgroupTex = "lgt";
            Assert.Equal(handgroupTex, CasEdit.HandgroupTexFrom("huao" + handgroupTex));
        }

        [Theory]
        [InlineData("lgt", "lgt")]
        [InlineData("med", "med")]
        [InlineData("drk", "drk")]
        [InlineData("LGT", "lgt")]
        [InlineData("  med  ", "med")]
        [InlineData("lgt;med;drk", "lgt")]
        [InlineData(null, "lgt")]
        [InlineData("", "lgt")]
        [InlineData("blue", "lgt")]
        [InlineData(";", "lgt")]
        public void SkinColorCode_NormalisesToTheThreeKnownCodes(string input, string expected)
        {
            Assert.Equal(expected, CasEdit.SkinColorCode(input));
        }

        [Theory]
        [InlineData(0, "lgt")]
        [InlineData(1, "med")]
        [InlineData(2, "drk")]
        [InlineData(3, "lgt")]
        [InlineData(-1, "lgt")]
        public void SkinColorFromPersonData_MapsNumericTone(short tone, string expected)
        {
            Assert.Equal(expected, CasEdit.SkinColorFromPersonData(tone));
        }

        [Theory]
        [InlineData(0, true)]   //no age recorded - the engine treats 0 as a child
        [InlineData(1, true)]
        [InlineData(9, true)]   //what MakePersonData writes for a CAS child
        [InlineData(17, true)]
        [InlineData(18, false)]
        [InlineData(27, false)] //what MakePersonData writes for a CAS adult
        [InlineData(80, false)]
        public void IsChildPersonAge_MatchesTheEngineRule(short age, bool expected)
        {
            Assert.Equal(expected, CasEdit.IsChildPersonAge(age));
        }

        [Theory]
        [InlineData(false, false, (short)0)] //adult male
        [InlineData(false, true, (short)1)]  //adult female
        [InlineData(true, false, (short)2)]  //child male
        [InlineData(true, true, (short)3)]   //child female
        public void GenderCode_ProducesTheFourCasCodes(bool child, bool female, short expected)
        {
            Assert.Equal(expected, CasEdit.GenderCode(child, female));
        }

        [Fact]
        public void PersonalityFromPersonData_ReadsTheGeneratorIndices()
        {
            //MakePersonData writes 2,3,5,6,7 - offset 4 (generous) is never written.
            var pd = new short[88];
            pd[2] = 1;
            pd[3] = 2;
            pd[4] = 99; //generous: must be ignored, it is not a CAS personality
            pd[5] = 3;
            pd[6] = 4;
            pd[7] = 5;

            Assert.Equal(new short[] { 1, 2, 3, 4, 5 }, CasEdit.PersonalityFromPersonData(pd));
        }

        [Fact]
        public void PersonalityFromPersonData_MissingIndicesBecomeZero()
        {
            var pd = new short[88];
            pd[2] = 7;
            Assert.Equal(new short[] { 7, 0, 0, 0, 0 }, CasEdit.PersonalityFromPersonData(pd));
        }

        [Fact]
        public void PersonalityFromPersonData_NullOrShortArrayYieldsZeros()
        {
            Assert.Equal(new short[5], CasEdit.PersonalityFromPersonData(null));
            Assert.Equal(new short[5], CasEdit.PersonalityFromPersonData(new short[0]));
            Assert.Equal(new short[5], CasEdit.PersonalityFromPersonData(new short[4]));
        }

        [Fact]
        public void PersonalityFromPersonData_AlwaysFiveSlots()
        {
            Assert.Equal(5, CasEdit.PersonalitySlots);
            Assert.Equal(5, CasEdit.PersonalityFromPersonData(new short[88]).Length);
            Assert.Equal(5, CasEdit.PersonalityFromPersonData(null).Length);
        }

        [Fact]
        public void PersonalityFromPersonData_RoundTripsMakePersonDataLayout()
        {
            //what the CAS writes for a member with points {10,20,30,40,50} must decode back to
            //the same five values, or opening and re-saving a family would quietly shuffle them.
            var pd = new short[88];
            var points = new short[] { 10, 20, 30, 40, 50 };
            pd[2] = points[0];
            pd[3] = points[1];
            pd[5] = points[2];
            pd[6] = points[3];
            pd[7] = points[4];
            Assert.Equal(points, CasEdit.PersonalityFromPersonData(pd));
        }
    }
}
