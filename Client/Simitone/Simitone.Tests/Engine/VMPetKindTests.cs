using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMPetKind"/> - person data 65, the word that says whether a sim is a dog.
    ///
    /// <para>
    /// The engine read this word's pet bits at 8 and 16. The retail data puts them at 2 and 4:
    /// <c>Global.iff</c> BHAV 467 <c>Am I a Dog?</c> is one instruction, <c>MyPersonData[65]
    /// IsFlagSet 2</c>, and 468 <c>Am I a Cat?</c> is <c>IsFlagSet 3</c>; a flag word's bit N is
    /// <c>1 &lt;&lt; (N - 1)</c>, so the dog bit is 2 and the cat bit is 4. <c>DogGlobals.iff</c>
    /// BHAV 8194 <c>Main</c> writes the same bit the other way round (<c>MyPersonData[65] SetFlag
    /// 2</c>), and <c>Initialize - Female</c> writes the whole word as 1, so bit 1 is the female
    /// bit.
    /// </para>
    ///
    /// <para>
    /// Both halves were wrong together, which is why it survived: the writer
    /// (<c>VMAvatar.SetAvatarBodyStrings</c>) and the reader (<c>VMAvatar.IsPet</c>) agreed, so a
    /// pet created inside the engine looked like a pet. A pet loaded from a save - which the game
    /// wrote as 2, 3, 4 or 5 - did not. That is the half with a visible consequence:
    /// <c>VMThread.CheckTS1Action</c> refused every non-pet row for an <c>IsPet</c> caller, and
    /// handed a save-loaded dog the <c>Interaction - Pet Eat</c> rows and every human interaction
    /// on the lot.
    /// </para>
    /// </summary>
    public sealed class VMPetKindTests
    {
        [Theory]
        [InlineData(0, false, false, false)]
        [InlineData(1, false, false, false)]
        [InlineData(2, true, true, false)]   // dog male   - the value the retail game writes
        [InlineData(3, true, true, false)]   // dog female
        [InlineData(4, true, false, true)]   // cat male   - and this one
        [InlineData(5, true, false, true)]   // cat female
        public void TheSixGenderWordsNameTheSixKindsOfSim(short gender, bool isPet, bool isDog, bool isCat)
        {
            Assert.Equal(isPet, VMPetKind.IsPet(gender));
            Assert.Equal(isDog, VMPetKind.IsDog(gender));
            Assert.Equal(isCat, VMPetKind.IsCat(gender));
        }

        [Theory]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(16)]
        [InlineData(17)]
        public void TheBitsTheEngineUsedToReadAreNotTheGames(short gender)
        {
            // The regression, stated as the thing it was. 8/16 is one bit too high for the dog
            // and cat bits, so a word carrying them is not a pet - and nothing the game writes
            // produces one.
            Assert.False(VMPetKind.IsPet(gender));
            Assert.False(VMPetKind.IsDog(gender));
            Assert.False(VMPetKind.IsCat(gender));
        }

        [Fact]
        public void TheSexBitIsTheLowOneOnEveryKind()
        {
            // Why one bit is enough for GenderCharFor, and why PetToAdult keeps an animal's sex:
            // clearing or setting the low bit turns any of the six into its opposite-sex twin and
            // changes nothing else - including the species.
            foreach (var male in new[] { VMPetKind.MaleHuman, VMPetKind.MaleDog, VMPetKind.MaleCat })
            {
                var female = (short)(male | VMPetKind.SexMask);
                Assert.Equal('m', VMTS1BodyTransformHelper.GenderCharFor(male));
                Assert.Equal('f', VMTS1BodyTransformHelper.GenderCharFor(female));

                // and back again: a pet-to-adult transform keeps the animal's sex and drops the
                // species bits, which is exactly `word & ~SpeciesMask`.
                Assert.Equal((short)(female & ~VMPetKind.SpeciesMask),
                    VMTS1BodyTransformHelper.HumanGenderFor(
                        VMTS1BodyTransformHelper.GenderCharFor(female)));
                Assert.Equal(VMPetKind.IsPet(male), VMPetKind.IsPet(female));
                Assert.Equal(VMPetKind.IsDog(male), VMPetKind.IsDog(female));
                Assert.Equal(VMPetKind.IsCat(male), VMPetKind.IsCat(female));
            }
        }

        [Theory]
        [InlineData("male", VMPetKind.MaleHuman)]
        [InlineData("MALE", VMPetKind.MaleHuman)]
        [InlineData("female", VMPetKind.FemaleHuman)]
        [InlineData("dogmale", VMPetKind.MaleDog)]
        [InlineData("dogfemale", VMPetKind.FemaleDog)]
        [InlineData("catmale", VMPetKind.MaleCat)]
        [InlineData("catfemale", VMPetKind.FemaleCat)]
        public void TheBodyStringTokenIsTheGenderWord(string token, short expected)
        {
            // Body string slot 12. The template's own STR 0x00C8 says "male" at 12 and "27" at 13,
            // and a pet's says "dogmale" - which is the only place in the install where the six
            // tokens appear.
            Assert.Equal(expected, VMPetKind.FromBodyStringToken(token, fallback: -1));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("hamster")]
        [InlineData("m")]
        public void AnUnknownTokenLeavesTheWordAlone(string token)
        {
            // The word also comes out of the save, where the game wrote it. A body string the
            // engine does not recognise is not a reason to guess - guessing is how a sim turns
            // into whatever was guessed, which for this word is a pet.
            Assert.Equal((short)1, VMPetKind.FromBodyStringToken(token, 1));
        }

        [Fact]
        public void TheTwoWordsAHumanAndAPetDifferOnlyInTheSpeciesBits()
        {
            // The invariant the transform helpers rely on when they rewrite a person-data table:
            // species and sex are separate bits of one word, so either can be changed alone.
            Assert.Equal(VMPetKind.MaleDog, VMPetKind.MaleHuman | VMPetKind.DogBit);
            Assert.Equal(VMPetKind.FemaleCat, VMPetKind.FemaleHuman | VMPetKind.CatBit);
            Assert.Equal(VMPetKind.FemaleHuman, VMPetKind.FemaleDog & ~VMPetKind.SpeciesMask);
            Assert.Equal(VMPetKind.MaleHuman, VMPetKind.MaleCat & ~VMPetKind.SpeciesMask);
        }
    }
}
