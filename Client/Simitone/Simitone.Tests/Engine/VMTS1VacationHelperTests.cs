using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The engine half of the Vacation Island lot logic:
    /// <see cref="VMTS1VacationHelper"/>'s lot-number facts and counting, and
    /// <see cref="VMTS1PetAutonomyHelper"/>'s Unleashed pet-autonomy decisions.
    ///
    /// <para>
    /// Both helpers are pure, and both exist to pin a claim that is otherwise only prose in a
    /// comment. The Vacation Island one pins the two numbers the shipped pay-booth scripts decide
    /// the feature on - the lot range (40-49 inclusive) and "0 means none available" - because
    /// getting the range wrong is silent: the pay booth simply stops offering to travel, and
    /// nothing in the log says why. The count itself came from replacing a hard-coded 9.
    /// </para>
    ///
    /// <para>
    /// The pet one pins the opposite kind of claim: that the Unleashed roadmap's "pets randomly
    /// starve to death" is <em>not</em> what the shipped scripts do. The pet's own
    /// <c>main loop</c> clamps Hunger at -90 and its <c>check motive failure</c> needs -98, so
    /// motive decay cannot reach the failure path at all. If a future edit to either helper ever
    /// closes that gap, or ever lets a stray reach an object, these fail.
    /// </para>
    ///
    /// <para>
    /// Both sets of constants are read off the retail install's own behaviour, cited in the helper
    /// docs (BHAV ids and file names), so a reader can go and check them. What is <em>not</em>
    /// asserted here is anything only a playtest can settle - see the stray-parity note in
    /// <see cref="VMTS1PetAutonomyHelper"/>.
    /// </para>
    /// </summary>
    public sealed class VMTS1VacationHelperTests
    {
        private const short Shipped = VMTS1VacationHelper.ShippedVacationLotCount;

        // ---- the lot range the shipped pay-booth check tree tests -------------------------

        [Theory]
        [InlineData(40, true)]   // first vacation lot
        [InlineData(44, true)]
        [InlineData(48, true)]
        [InlineData(49, true)]   // last - PhonePayVacation.iff BHAV 4101 [1]: Global[10] <= 49
        [InlineData(39, false)]  // the residential block's last lot
        [InlineData(50, false)]
        [InlineData(21, false)]  // Downtown
        [InlineData(81, false)]  // Studio Town
        [InlineData(90, false)]  // Makin' Magic
        [InlineData(0, false)]
        [InlineData(-1, false)]
        [InlineData(500, false)]
        public void VacationLotRangeIsFortyToFortyNineInclusive(int houseId, bool expected)
        {
            Assert.Equal(expected, VMTS1VacationHelper.IsVacationLot(houseId));
        }

        [Fact]
        public void TheLotRangeHasTenNumbersButTheInstallShipsNine()
        {
            // The number that used to be hard-coded is the file count, not the range size. This is
            // the whole reason the constant could not simply be kept: "9" and "40..49" are
            // different facts, and only one of them is true of any given install.
            var range = VMTS1VacationHelper.VacationLotIds().ToList();

            Assert.Equal(10, range.Count);
            Assert.Equal(VMTS1VacationHelper.FirstVacationLot, range.First());
            Assert.Equal(VMTS1VacationHelper.LastVacationLot, range.Last());
            Assert.Equal(9, Shipped);
            Assert.True(VMTS1VacationHelper.LastVacationLot - VMTS1VacationHelper.FirstVacationLot + 1
                > Shipped, "the range must be wider than a complete install, or the count is a tautology");
        }

        [Fact]
        public void EveryVacationLotIdTheHelperEnumeratesIsAVacationLot()
        {
            Assert.All(VMTS1VacationHelper.VacationLotIds(), id =>
                Assert.True(VMTS1VacationHelper.IsVacationLot(id), id + " should be a vacation lot"));
        }

        // ---- counting what is actually available -------------------------------------------

        [Fact]
        public void ACompleteInstallCountsTheNineLotsItShips()
        {
            // House40..House48 exist, House49 does not. This is the case the old hard-coded 9 got
            // right, and it must keep getting right - it is every real player's install.
            var present = Enumerable.Range(40, 9).ToList();

            Assert.Equal(Shipped, VMTS1VacationHelper.CountAvailableVacationLots(present));
        }

        [Fact]
        public void APartialInstallCountsOnlyTheLotsItHas()
        {
            Assert.Equal((short)4, VMTS1VacationHelper.CountAvailableVacationLots(
                new[] { 40, 41, 42, 43 }));
        }

        [Fact]
        public void NoVacationIslandCountsZero()
        {
            // This is the answer the hard-coded 9 could never give, and it is the one that makes
            // the shipped "island fully booked" dialog (dialog_private 34) reachable at all.
            Assert.Equal((short)0, VMTS1VacationHelper.CountAvailableVacationLots(
                new[] { 1, 2, 3, 12, 21, 30, 81, 90 }));
        }

        [Fact]
        public void AnEmptyInstallCountsZero()
        {
            Assert.Equal((short)0, VMTS1VacationHelper.CountAvailableVacationLots(
                Array.Empty<int>()));
        }

        [Fact]
        public void MissingDataCountsZeroSoTheCallerCanFallBackExplicitly()
        {
            // A null sequence is "no answer", and the helper's answer to that is 0 - the engine
            // method then substitutes the shipped count itself rather than letting the helper
            // guess on its behalf and silently disabling the pay booth.
            Assert.Equal((short)0, VMTS1VacationHelper.CountAvailableVacationLots(null));
        }

        [Fact]
        public void OutOfRangeLotNumbersAreNeverCounted()
        {
            // A neighbourhood full of non-vacation lots must not inflate the island's pool. Only
            // 40 and 45 here are vacation lots; 39, 50, 51 and 100 are all real lot numbers in
            // other ranges and all ignored.
            Assert.Equal((short)2, VMTS1VacationHelper.CountAvailableVacationLots(
                new[] { 39, 40, 45, 50, 51, 100 }));
        }

        [Fact]
        public void ADuplicatedLotCountsOnce()
        {
            Assert.Equal((short)1, VMTS1VacationHelper.CountAvailableVacationLots(
                new[] { 44, 44, 44 }));
        }

        [Fact]
        public void NegativeLotNumbersAreNeverCounted()
        {
            Assert.Equal((short)0, VMTS1VacationHelper.CountAvailableVacationLots(
                new[] { -40, -1, 0 }));
        }

        [Fact]
        public void TheCountNeverExceedsTheSizeOfTheRange()
        {
            var everything = VMTS1VacationHelper.VacationLotIds()
                .Concat(VMTS1VacationHelper.VacationLotIds())
                .Concat(Enumerable.Range(0, 200));

            var count = VMTS1VacationHelper.CountAvailableVacationLots(everything);

            Assert.InRange(count, (short)0, (short)10);
            Assert.Equal((short)10, count);
        }

        // ---- the arrival cleanup predicate --------------------------------------------------

        [Fact]
        public void AMemberOfTheArrivingFamilyIsNotALeftover()
        {
            Assert.False(VMTS1VacationHelper.IsLeftoverFromAnotherFamily(3, 3));
        }

        [Fact]
        public void AMemberOfAnotherFamilyIsALeftover()
        {
            // The whole point of the sweep: on a shared island lot, somebody else's sim is the
            // previous visit's residue.
            Assert.True(VMTS1VacationHelper.IsLeftoverFromAnotherFamily(4, 3));
        }

        [Theory]
        [InlineData(0)]   // visitor
        [InlineData(-1)]  // unset / corrupt
        [InlineData(short.MinValue)]
        public void ASimWithNoFamilyIsNeverALeftover(short familyNumber)
        {
            // This is the property that protects a vacation lot's own cast. The director, the
            // carnie, the mascot and the rental clerk all carry family number 0, so a test that
            // removed "everyone who is not the arriving family" would delete the lot's NPCs; only
            // a test that removes a *positively different* family cannot.
            Assert.False(VMTS1VacationHelper.IsLeftoverFromAnotherFamily(familyNumber, 3));
        }

        [Theory]
        [InlineData(0)]    // no family at all
        [InlineData(-1)]   // unset
        public void WithNoArrivingFamilyNothingIsALeftover(short arrivingFamilyNumber)
        {
            // Refuse to guess: without a family to compare against there is no evidence that
            // anybody is an intruder, and deleting on suspicion is the failure mode. Only 0 and
            // negative are "no family" - a FAMI chunk id is a ushort, so any positive value,
            // however odd, names a real family and is compared against normally.
            Assert.False(VMTS1VacationHelper.IsLeftoverFromAnotherFamily(4, arrivingFamilyNumber));
        }

        [Fact]
        public void AnOddButPositiveArrivingFamilyNumberIsStillCompared()
        {
            Assert.True(VMTS1VacationHelper.IsLeftoverFromAnotherFamily(4, 12345));
            Assert.False(VMTS1VacationHelper.IsLeftoverFromAnotherFamily(12345, 12345));
        }

        [Fact]
        public void TheCleanupPredicateIsIdempotentOverAWholeLotCast()
        {
            // A lot modelled the way one actually loads: the lot's four NPCs, the arriving family's
            // two Sims, and (in the shared-lot case the roadmap describes) two from a family that
            // was here before. Only the last two may go.
            var arriving = 3;
            var cast = new Dictionary<string, short>
            {
                { "Vacation Director", 0 },
                { "Carnie", 0 },
                { "Vacation Mascot", 0 },
                { "Rental Clerk", 0 },
                { "Downtown Controller", 0 },
                { "Arriving Sim A", 3 },
                { "Arriving Sim B", 3 },
                { "Previous Visitor A", 7 },
                { "Previous Visitor B", 7 },
            };

            var cleared = cast.Where(x =>
                VMTS1VacationHelper.IsLeftoverFromAnotherFamily(x.Value, (short)arriving))
                .Select(x => x.Key).ToList();

            Assert.Equal(
                new[] { "Previous Visitor A", "Previous Visitor B" },
                cleared.OrderBy(x => x).ToArray());
        }
    }

    /// <summary>
    /// The Unleashed pet-autonomy decisions in <see cref="VMTS1PetAutonomyHelper"/>, pinned so the
    /// two behaviours the roadmap mis-describes cannot drift:
    ///
    /// <list type="bullet">
    /// <item>a stray pet is a <b>visitor pet that has not been greeted</b> and nothing else - the
    /// same four person-data reads the engine already made, moved behind a name and a test;</item>
    /// <item>a stray <b>cannot approach any object</b>, which is why a stray is permanently hungry,
    /// and the only thing keeping it alive is its own script's motive clamp;</item>
    /// <item>the pet hunger floor (-90) sits strictly <b>above</b> the pet hunger failure (-98),
    /// which is the arithmetic that makes "a pet starves to death" impossible.</item>
    /// </list>
    /// </summary>
    public sealed class VMTS1PetAutonomyHelperTests
    {
        [Fact]
        public void AVisitorPetThatHasNotBeenGreetedIsAStray()
        {
            Assert.True(VMTS1PetAutonomyHelper.IsStray(
                isPet: true, isTS1: true,
                greetStatus: VMTS1PetAutonomyHelper.NotGreeted,
                personType: VMTS1PetAutonomyHelper.VisitorPersonType));
        }

        [Fact]
        public void AHousePetIsNotAStray()
        {
            // Person type 0 is the normal case for a family's own pet, and this is the case that
            // must reach the food bowl.
            Assert.False(VMTS1PetAutonomyHelper.IsStray(true, true, 0, 0));
        }

        [Fact]
        public void AGreetedVisitorPetIsNotAStray()
        {
            // Greet status 1 or 2 means the visitor has been through the greeting script, which is
            // the difference the game draws between a stray and an ordinary house guest.
            Assert.False(VMTS1PetAutonomyHelper.IsStray(true, true, 1, 1));
            Assert.False(VMTS1PetAutonomyHelper.IsStray(true, true, 2, 1));
        }

        [Fact]
        public void AHumanVisitorIsNotAStray()
        {
            Assert.False(VMTS1PetAutonomyHelper.IsStray(false, true, 0, 1));
        }

        [Fact]
        public void AVisitorPetIsNotAStrayOnATSOlot()
        {
            // The stray rule is an Unleashed/TS1 behaviour. TSO pets get no such restriction.
            Assert.False(VMTS1PetAutonomyHelper.IsStray(true, false, 0, 1));
        }

        [Fact]
        public void OnlyAPetIsEverAStray()
        {
            Assert.False(VMTS1PetAutonomyHelper.IsStray(false, false, 0, 0));
        }

        [Fact]
        public void AStrayCannotApproachAnyObject()
        {
            // The behaviour that makes a stray permanently hungry. It is left retail-faithful on
            // purpose; if a playtest ever shows a retail stray eating, this is the line to change.
            Assert.False(VMTS1PetAutonomyHelper.StrayMayApproach(isStray: true, isGameObject: true));
        }

        [Fact]
        public void AStrayCanStillApproachOtherSims()
        {
            Assert.True(VMTS1PetAutonomyHelper.StrayMayApproach(isStray: true, isGameObject: false));
        }

        [Fact]
        public void ANonStrayMayApproachAnything()
        {
            Assert.True(VMTS1PetAutonomyHelper.StrayMayApproach(isStray: false, isGameObject: true));
            Assert.True(VMTS1PetAutonomyHelper.StrayMayApproach(isStray: false, isGameObject: false));
        }

        [Fact]
        public void APetCanNeverStarveToDeath()
        {
            // The arithmetic, stated as a test. The pet's own main loop clamps Hunger at
            // PetHungerFloor; the "failure hunger" handler that the roadmap's starvation bug would
            // come through needs PetHungerFailure. The clamp is strictly above the failure point,
            // so ordinary motive decay can never reach it - and the handler is a balloon headline
            // and a sound, not a death, in any case.
            Assert.True(VMTS1PetAutonomyHelper.PetHungerFloor > VMTS1PetAutonomyHelper.PetHungerFailure,
                "the pet script's hunger clamp must stay above its hunger-failure threshold");

            // And the engine's own decay floor is the only number that could go past both.
            const short EngineDecayFloor = -100;
            Assert.True(EngineDecayFloor <= VMTS1PetAutonomyHelper.PetHungerFailure,
                "motive decay's own floor is below the failure point, which is why the clamp matters");
        }
    }

    /// <summary>
    /// The room-index clamp <see cref="Primitives.VMFindBestAction"/>'s stray branch uses, which is
    /// the only engine code in the pet-autonomy path that indexes an array with a value it computed.
    /// </summary>
    public sealed class VMTS1PetAutonomyHelperRoomClampTests
    {
        [Theory]
        [InlineData(0, 1, 0)]
        [InlineData(0, 5, 0)]
        [InlineData(3, 5, 3)]
        [InlineData(4, 5, 4)]
        [InlineData(5, 5, 4)]   // one past the end
        [InlineData(99, 5, 4)]
        [InlineData(-1, 5, 0)]  // below the start
        [InlineData(int.MinValue, 5, 0)]
        public void ARoomIndexIsClampedIntoRange(int roomId, int roomCount, int expected)
        {
            var clamped = VMTS1PetAutonomyHelper.ClampRoomIndex(roomId, roomCount);

            Assert.Equal(expected, clamped);
            Assert.InRange(clamped, 0, roomCount - 1);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(3, 0)]
        [InlineData(0, -1)]
        public void WithNoRoomsAtAllThereIsNoRoomToIndexInto(int roomId, int roomCount)
        {
            // -1 is a stated answer, not an index: the caller refuses instead of reading
            // RoomInfo[-1]. On a loaded lot RoomInfo always holds at least room 0, so this branch
            // exists to be total rather than to be reachable.
            Assert.Equal(-1, VMTS1PetAutonomyHelper.ClampRoomIndex(roomId, roomCount));
        }
    }
}
