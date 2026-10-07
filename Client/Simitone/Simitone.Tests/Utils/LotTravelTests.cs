using Simitone.Client.Utils;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the two decisions that make travelling between lots reversible.
    ///
    /// There is only ever one live VM, so a travel switch has to marshalled the lot being left
    /// out and kept it until the player came back. The original code kept a single slot, which
    /// only ever held the home lot: state built up on Downtown, Vacation, Studio or Magic was
    /// dropped on the way out, and returning to that lot reloaded the shipped template instead
    /// of the player's own session. These tests pin the two comparisons that decide "save the
    /// current lot" and "resume a preserved lot" so a multi-slot store cannot quietly collapse
    /// back into a single one.
    ///
    /// <see cref="Dictionary{TKey,TValue}"/> of string stands in for the
    /// <c>Dictionary&lt;short, VMMarshal&gt;</c> the screen really keeps, so the decisions are
    /// exercised without building a VM or a graphics device.
    /// </summary>
    public sealed class LotTravelTests
    {
        [Theory]
        [InlineData(21, 26)] // home -> downtown
        [InlineData(26, 42)] // downtown -> vacation
        [InlineData(42, 21)] // vacation -> home
        [InlineData(21, 92)] // home -> magic
        [InlineData(6, 7)]   // next door
        public void ASwitchToADifferentLotStashesTheCurrentOne(int currentHouse, int targetHouse)
        {
            Assert.True(LotTravel.ShouldStashCurrentLot(currentHouse, targetHouse));
        }

        [Theory]
        [InlineData(21, 21)]
        [InlineData(42, 42)]
        [InlineData(92, 92)]
        public void ASwitchToTheSameLotDoesNotStash(int house, int target)
        {
            //stashing here would overwrite the live state the restore is about to load back
            Assert.False(LotTravel.ShouldStashCurrentLot(house, target));
        }

        [Fact]
        public void EveryPairOfDistinctLotsIsStashedRegardlessOfDirection()
        {
            //the rule is a plain inequality - no notion of "closer", "home" or "downtown"
            for (int current = 1; current <= 100; current++)
            {
                for (int target = 1; target <= 100; target++)
                {
                    Assert.Equal(current != target, LotTravel.ShouldStashCurrentLot(current, target));
                }
            }
        }

        [Fact]
        public void AStoredLotIsRestored()
        {
            var saved = new Dictionary<short, string> { { 21, "home-marshal" } };

            string marshal;
            Assert.True(LotTravel.TryRestore(saved, 21, out marshal));
            Assert.Equal("home-marshal", marshal);
        }

        [Fact]
        public void AnUnvisitedLotIsNotRestored()
        {
            var saved = new Dictionary<short, string> { { 21, "home-marshal" } };

            string marshal;
            Assert.False(LotTravel.TryRestore(saved, 26, out marshal));
            Assert.Null(marshal);
        }

        [Fact]
        public void AnEmptyStoreRestoresNothing()
        {
            var saved = new Dictionary<short, string>();

            string marshal;
            Assert.False(LotTravel.TryRestore(saved, 21, out marshal));
            Assert.Null(marshal);
        }

        [Fact]
        public void AMissingStoreRestoresNothing()
        {
            //defensive: a null store must be reported as "no preserved state", not thrown
            string marshal;
            Assert.False(LotTravel.TryRestore<string>(null, 21, out marshal));
            Assert.Null(marshal);
        }

        [Fact]
        public void AnEntryWithNoStateIsTreatedAsUnvisited()
        {
            //a null value would be handed straight to vm.Load and crash the switch
            var saved = new Dictionary<short, string> { { 26, null } };

            string marshal;
            Assert.False(LotTravel.TryRestore(saved, 26, out marshal));
            Assert.Null(marshal);
        }

        [Fact]
        public void EveryLotKeepsItsOwnState()
        {
            //the regression this whole change exists for: a round trip through downtown and
            //vacation must return each lot to its own snapshot, not to the one-slot's
            var saved = new Dictionary<short, string>
            {
                { 21, "home" },
                { 26, "downtown" },
                { 42, "vacation" },
            };

            string marshal;
            Assert.True(LotTravel.TryRestore(saved, 21, out marshal));
            Assert.Equal("home", marshal);
            Assert.True(LotTravel.TryRestore(saved, 26, out marshal));
            Assert.Equal("downtown", marshal);
            Assert.True(LotTravel.TryRestore(saved, 42, out marshal));
            Assert.Equal("vacation", marshal);
        }

        [Fact]
        public void ARemovedEntryRestoresNothingTheSecondTime()
        {
            //the screen removes the entry after restoring it, so a later visit reloads from
            //House{NN}.iff rather than replaying the stale snapshot
            var saved = new Dictionary<short, string> { { 21, "home-marshal" } };

            string marshal;
            Assert.True(LotTravel.TryRestore(saved, 21, out marshal));
            saved.Remove(21);

            Assert.False(LotTravel.TryRestore(saved, 21, out marshal));
            Assert.Null(marshal);
        }

        [Fact]
        public void TheSecondStashOfALotOverwritesTheFirst()
        {
            //re-entering a lot overwrites its entry with the newer state, so the store stays
            //one entry per lot rather than growing on every visit
            var saved = new Dictionary<short, string> { { 26, "downtown-before" } };
            saved[26] = "downtown-after";

            string marshal;
            Assert.True(LotTravel.TryRestore(saved, 26, out marshal));
            Assert.Equal("downtown-after", marshal);
            Assert.Single(saved);
        }

        [Fact]
        public void ClearingTheStoreDropsEveryLot()
        {
            //ExitLot() clears the store; nothing may survive it
            var saved = new Dictionary<short, string> { { 21, "home" }, { 26, "downtown" } };
            saved.Clear();

            string marshal;
            Assert.False(LotTravel.TryRestore(saved, 21, out marshal));
            Assert.Null(marshal);
            Assert.Empty(saved);
        }

        [Fact]
        public void AValueTypeStoreIsSupported()
        {
            //TryRestore is generic, so it must work for a value type as well as a reference
            //type - the null check cannot assume references
            var saved = new Dictionary<short, int> { { 21, 1500 } };

            int marshal;
            Assert.True(LotTravel.TryRestore(saved, 21, out marshal));
            Assert.Equal(1500, marshal);

            Assert.False(LotTravel.TryRestore(saved, 22, out marshal));
            Assert.Equal(0, marshal);
        }
    }
}
