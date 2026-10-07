using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the rule that only the active family's own home lot may be written back to
    /// <c>House{NN}.iff</c>.
    ///
    /// <para>
    /// The other lots a family can be standing on while travelling - Downtown (21-31),
    /// Vacation (40-49), Studio (81-89) and Magic (90-99) - are copies of the templates shipped
    /// with the game. Saving on one of them overwrote the template with the played family's
    /// house and stamped the family's SIMI globals into it, which is the long-standing
    /// "does not support saving on vacation" data loss. The decision itself is a one-line
    /// comparison, but it is a data-destruction decision, so it gets its own tests.
    /// </para>
    ///
    /// <para>
    /// The guard no longer ends in a refusal: a refused lot is saved to its own slot instead
    /// (<c>Utils/TravelSave</c>, tested by <c>TravelSaveTests</c>). The two tests at the bottom
    /// pin the seam between them, because a lot that took neither branch would reach the shipped
    /// template again - the exact failure this file exists to prevent.
    /// </para>
    /// </summary>
    public sealed class LotPersistenceTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(20)]
        [InlineData(51)]
        [InlineData(80)]
        [InlineData(100)]
        public void TheFamiliesOwnHomeLotIsPersistable(int house)
        {
            Assert.True(LotPersistence.IsPersistableLot(house, house));
        }

        [Fact]
        public void ATravelLotIsNotPersistable()
        {
            // family 7 travelling to downtown lot 26 / vacation lot 42 / studio 85 / magic 92
            Assert.False(LotPersistence.IsPersistableLot(26, 7));
            Assert.False(LotPersistence.IsPersistableLot(42, 7));
            Assert.False(LotPersistence.IsPersistableLot(85, 7));
            Assert.False(LotPersistence.IsPersistableLot(92, 7));
        }

        [Fact]
        public void AnyOtherLotIsNotPersistable()
        {
            Assert.False(LotPersistence.IsPersistableLot(8, 7));
            // going home is allowed, going next door is not
            Assert.False(LotPersistence.IsPersistableLot(6, 7));
        }

        [Fact]
        public void AnUnknownFamilyBlocksEveryLot()
        {
            // no family -> no claim on any file, even for a lot number that would otherwise match
            Assert.False(LotPersistence.IsPersistableLot(7, null));
            Assert.False(LotPersistence.IsPersistableLot(0, null));
            Assert.False(LotPersistence.IsPersistableLot(-1, null));
        }

        [Fact]
        public void AFamilyWithNoHomeLotBlocksEveryRealLot()
        {
            // a freshly spawned / evicted family can carry house number 0 or a stale/negative
            // value. that must not hand it any real lot's file.
            Assert.False(LotPersistence.IsPersistableLot(1, 0));
            Assert.False(LotPersistence.IsPersistableLot(12, 0));
            Assert.False(LotPersistence.IsPersistableLot(1, -1));
            Assert.False(LotPersistence.IsPersistableLot(12, -1));
        }

        [Theory]
        [InlineData(0, 0, true)]
        [InlineData(-1, -1, true)]
        [InlineData(-5, -1, false)]
        [InlineData(-1, 12, false)]
        [InlineData(0, 12, false)]
        public void NonPositiveIdsCompareConsistently(int lotHouseId, int? familyHouseNumber, bool expected)
        {
            // negatives are not real lot numbers, but they must not make the guard inconsistent:
            // the rule is a plain equality against the family's own number, nothing else.
            Assert.Equal(expected, LotPersistence.IsPersistableLot(lotHouseId, familyHouseNumber));
        }

        [Fact]
        public void OnlyExactEqualityPersists()
        {
            Assert.False(LotPersistence.IsPersistableLot(21, 21 + 1));
            Assert.False(LotPersistence.IsPersistableLot(21 + 1, 21));
            Assert.False(LotPersistence.IsPersistableLot(2, 21));
            Assert.True(LotPersistence.IsPersistableLot(21, 21));
        }

        /// <summary>
        /// The guard is only half the save decision: the other half sends a refused lot to its own
        /// slot instead (<c>Utils/TravelSave</c>). Because <c>TravelSave.IsTravelLot</c> is this
        /// method's exact negation, the two branches in <c>TS1GameScreen.Save</c> are exhaustive -
        /// so this test exists to catch the day one of them is "adjusted" and a lot falls through
        /// both, silently reaching the shipped template again.
        /// </summary>
        [Fact]
        public void EveryLotTakesExactlyOneOfTheTwoSaveBranches()
        {
            for (int lot = 1; lot <= 100; lot++)
            {
                foreach (var family in new int?[] { null, 0, 1, 5, 20, 21, 26, 42, 85, 92, 99, 100 })
                {
                    var toHouseFile = LotPersistence.IsPersistableLot(lot, family);
                    var toSlot = TravelSave.IsTravelLot(lot, family);

                    Assert.NotEqual(toHouseFile, toSlot);
                    Assert.True(toHouseFile || toSlot);
                }
            }
        }

        /// <summary>
        /// A travel slot is keyed by family, so "the family that saved it" and "the lot it is
        /// stored under" are different facts and both must line up: the header family decides
        /// whether a slot may be loaded, the lot number decides which file is read. A slot whose
        /// header family is unknown can never be loaded, so an anonymous save can never be
        /// handed to a family that happens to own the destination lot.
        /// </summary>
        [Fact]
        public void ARefusedLotIsOnlyEverResumableByTheFamilyThatSavedIt()
        {
            const int vacationLot = 42;
            var familyAtHome = 7;

            //the lot is refused for the house file...
            Assert.False(LotPersistence.IsPersistableLot(vacationLot, familyAtHome));
            //...so it is a travel lot and gets a slot...
            Assert.True(TravelSave.IsTravelLot(vacationLot, familyAtHome));
            //...but the family that owns the slot is the one that saved it, not the lot.
            Assert.False(TravelSave.ShouldRestoreSlot(vacationLot, familyAtHome));
            Assert.True(TravelSave.ShouldRestoreSlot(familyAtHome, familyAtHome));
        }
    }
}
