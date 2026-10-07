using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Model;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMPiePermissionRule"/> - the Sims 1 interaction filter, and the three things
    /// that made "eating is completely blocked" unanswerable from a log line.
    ///
    /// <para>
    /// The player report was: an object with three interactions offered none of them, so the pie
    /// menu was empty. The object turned out to be <c>Food - Regular Group Meal</c> (OBJD
    /// 0x41A9, GUID 0x15866694, tree table 0x0082) in the retail install, whose three rows are
    /// reproduced verbatim below. With nothing in the log but "offered none", the four candidate
    /// decisions were the tree table, the routine resolution, this filter, and the interaction's
    /// own check tree - and nothing recorded which of them had spoken.
    /// </para>
    ///
    /// <para>
    /// So these tests do two jobs. They pin the filter's decisions, so that a change to it cannot
    /// quietly take an interaction away; and they pin the one thing that could be settled without
    /// the game: <b>for this object's real rows and a real save's person data, this filter refuses
    /// nothing.</b> That is what narrowed a four-way unknown to the check tree, and it is stated
    /// here as data so the next reader does not have to re-derive it.
    /// </para>
    /// </remarks>
    public sealed class VMPiePermissionRuleTests
    {
        // ---- the retail rows of Objects/Food.iff tree table 0x0082 -------------------------
        // 'Food - Regular Group Meal', read out of D:\Program Files\Sims with the built
        // FSO.Files TTAB reader, so these are the values the engine itself sees.
        private const TTABFlags GroupMealEat = TTABFlags.AllowVisitors | TTABFlags.AutoFirstSelect;   // 0x101
        private const TTABFlags GroupMealCleanUp = TTABFlags.AllowConsecutive;                          // 0x008
        private const TTABFlags GroupMealEatAlternate = TTABFlags.AllowVisitors | TTABFlags.AllowConsecutive; // 0x009

        // and the pet-eat row the same food object carries on its single-meal siblings:
        // 'Interaction - Pet Eat' (0x671, both kinds of pet) and 'Interaction - Dog - Eat'
        // (0x471, dogs only). Note they are all no-child/no-adult rows.
        private const TTABFlags PetEatBoth = TTABFlags.AllowVisitors | TTABFlags.TS1NoChild
            | TTABFlags.TS1NoDemoChild | TTABFlags.TS1NoAdult
            | TTABFlags.TS1AllowCats | TTABFlags.TS1AllowDogs;                     // 0x671
        private const TTABFlags PetEatDogsOnly = TTABFlags.AllowVisitors | TTABFlags.TS1NoChild
            | TTABFlags.TS1NoDemoChild | TTABFlags.TS1NoAdult
            | TTABFlags.TS1AllowDogs;                                            // 0x471

        /// <summary>
        /// A resident adult, as this player's save has them: every neighbour in
        /// <c>UserData/Neighborhood.iff</c> is person data 65 (gender) 0 or 1, 58 (age in days) 9,
        /// 10 or 27, 32 (person type) 0 and 34 (greet status) 0.
        /// </summary>
        private const short AdultAgeDays = 27;
        private const short ResidentGender = 1;
        private const short ChildAgeDays = 9;

        private static bool AllowsResidentAdult(TTABFlags flags)
        {
            return VMPiePermissionRule.Allows(flags, false, false, false, false, false,
                AdultAgeDays, false, out var refusal);
        }

        // ---- the narrowing: this filter is not what refused the group meal -------------------

        [Fact]
        public void TheGroupMealsThreeRowsAreAllOfferedToAResidentAdult()
        {
            // The finding. Every row of tree table 0x0082 carries none of the bits this filter can
            // refuse on: no TS1NoChild, no TS1NoAdult, no pet flag, and two of the three carry
            // AllowVisitors. So "3 interactions but offered none" cannot have come from here for
            // an adult resident - it has to be the routine resolution or the check tree, and the
            // engine now says which of the two in the log.
            Assert.True(AllowsResidentAdult(GroupMealEat));
            Assert.True(AllowsResidentAdult(GroupMealCleanUp));
            Assert.True(AllowsResidentAdult(GroupMealEatAlternate));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        public void TheGenderTheSaveActuallyHoldsIsNotAPet(short gender)
        {
            // The other half of the same narrowing, and the reason the refusal was not
            // "caller is a pet": the IsPet branch refuses every row at once, and every neighbour
            // in this save has gender 0 or 1.
            Assert.False(VMPetKind.IsPet(gender));

            Assert.True(VMPiePermissionRule.Allows(GroupMealEat, false,
                VMPetKind.IsPet(gender), VMPetKind.IsCat(gender), VMPetKind.IsDog(gender),
                false, AdultAgeDays, false, out _));
        }

        // ---- the four refusals, each pinned ------------------------------------------------

        [Fact]
        public void APetIsRefusedAnInteractionThatIsNotMarkedForPets()
        {
            // The one branch that turns down every row of an object at once.
            Assert.False(VMPiePermissionRule.Allows(GroupMealEat, false,
                true, false, false, false, AdultAgeDays, false, out var refusal));

            Assert.Equal(VMPieRefusalKind.IsPet, refusal);
        }

        [Fact]
        public void TheWrongKindOfPetIsRefusedAPetOnlyInteraction()
        {
            // 'Interaction - Pet Eat' is 0x671: cats and dogs. A dog is refused the cats-only
            // reading of it, and vice versa - and the retail data's own reason for the two flags
            // being separate is that one food object carries a dogs-only row (0x471).
            Assert.False(VMPiePermissionRule.Allows(PetEatBoth & ~(TTABFlags)TTABFlags.TS1AllowDogs,
                false, true, false, true, false, AdultAgeDays, false, out var refusal));
            Assert.Equal(VMPieRefusalKind.PetKindMismatch, refusal);

            Assert.False(VMPiePermissionRule.Allows(PetEatDogsOnly, false,
                true, true, false, false, AdultAgeDays, false, out refusal));
            Assert.Equal(VMPieRefusalKind.PetKindMismatch, refusal);
        }

        [Fact]
        public void TheRightKindOfPetIsOfferedItsPetOnlyInteraction()
        {
            // A dog gets the 0x471 dogs-only row, and both get the 0x671 row. The age flags on
            // those rows are why the same sim is refused the same interaction as a human: a pet is
            // never under 18 days and never an adult as far as this filter is concerned, so
            // TS1NoChild and TS1NoAdult are both set for it - and the rows set them too.
            Assert.True(VMPiePermissionRule.Allows(PetEatBoth, false, true, false, true, false,
                AdultAgeDays, false, out _));
            Assert.True(VMPiePermissionRule.Allows(PetEatDogsOnly, false, true, false, true, false,
                AdultAgeDays, false, out _));
        }

        [Fact]
        public void AnInteractionMarkedNoAdultIsRefusedToAnAdultAndOneMarkedNoChildToAChild()
        {
            // The positive half of the mask. The 18 is days, not years - TS1's age line, and the
            // one Global.iff's 'Am I an Adult?' draws at PersonData[58] > 17.
            Assert.False(VMPiePermissionRule.Allows(PetEatBoth, false, false, false, false, false,
                AdultAgeDays, false, out var refusal));
            Assert.Equal(VMPieRefusalKind.AgeFlags, refusal);

            // a child is under the line, so TS1NoAdult does not apply to them and the same row is
            // refused for TS1NoChild instead - still a refusal, still the age mask.
            Assert.False(VMPiePermissionRule.Allows(PetEatBoth, false, false, false, false, false,
                ChildAgeDays, false, out refusal));
            Assert.Equal(VMPieRefusalKind.AgeFlags, refusal);
        }

        [Fact]
        public void AnInteractionMarkedNoChildIsStillOfferedToAnAdult()
        {
            // The other direction, so the mask cannot be "everything with an age bit is refused".
            Assert.True(VMPiePermissionRule.Allows(PetEatBoth & ~(TTABFlags)TTABFlags.TS1NoAdult,
                false, false, false, false, false, AdultAgeDays, false, out _));
        }

        [Fact]
        public void AVisitorIsRefusedAnInteractionThatIsNotMarkedVisitorFriendly()
        {
            // The negative half. 'Clean Up' on the group meal is the retail example: 0x008, with
            // no AllowVisitors bit - a resident may clean up, a visitor may not.
            Assert.False(VMPiePermissionRule.Allows(GroupMealCleanUp, false, false, false, false,
                true, AdultAgeDays, false, out var refusal));
            Assert.Equal(VMPieRefusalKind.NotVisitors, refusal);

            Assert.True(VMPiePermissionRule.Allows(GroupMealEat, false, false, false, false,
                true, AdultAgeDays, false, out _));
        }

        [Fact]
        public void ADebugInteractionIsOfferedToNobody()
        {
            // debugTrees is the engine's build-time switch and is false in the game, so the answer
            // is "no" for everyone - including an admin, because there is no admin concept here.
            Assert.False(VMPiePermissionRule.Allows(TTABFlags.Debug, false, false, false, false,
                false, AdultAgeDays, false, out var refusal));
            Assert.Equal(VMPieRefusalKind.DebugInteraction, refusal);

            Assert.False(VMPiePermissionRule.Allows(TTABFlags.Debug, false, false, false, false,
                true, AdultAgeDays, false, out refusal));
            Assert.Equal(VMPieRefusalKind.DebugInteraction, refusal);
        }

        [Fact]
        public void TheSkipPermissionsInteractionsBypassTheWholeFilter()
        {
            // FSOSkipPermissions is how the CSR interactions are offered to everyone, and it is
            // the first thing the old code tested, so it has to stay first: a row carrying it is
            // not an age- or pet- or visitor-gated interaction at all.
            var anything = PetEatBoth;
            Assert.False(AllowsResidentAdult(anything));

            Assert.True(VMPiePermissionRule.Allows(anything | TTABFlags.FSOSkipPermissions, true,
                true, true, true, true, ChildAgeDays, false, out var refusal));
            Assert.Equal(VMPieRefusalKind.None, refusal);
        }

        // ---- the diagnostic's detail line ---------------------------------------------------

        [Fact]
        public void TheDetailLineCarriesTheValuesTheDecisionWasMadeFrom()
        {
            // A reason on its own is not enough to act on: "refused by the age flags" says which
            // decision fired, and only these numbers say whether the decision was right.
            var detail = VMPiePermissionRule.DescribeCaller(
                PetEatDogsOnly, isPet: false, isVisitor: false, personsAge: AdultAgeDays, gender: ResidentGender);

            Assert.Contains("0x0001", detail);      // the gender word, so IsPet can be checked by eye
            Assert.Contains("PersonsAge=27", detail);
            Assert.Contains("resident", detail);
            Assert.Contains("TS1NoChild/TS1NoAdult=0x3", detail);   // 0x471 >> 4 & 3
            Assert.Contains("AllowVisitors=True", detail);
        }
    }
}
