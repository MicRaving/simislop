using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the decision that makes travelling between lots reversible: the lot being left is
    /// stashed only when the switch targets a different lot. (Stashing for the same lot would
    /// overwrite the live state the restore is about to load back.)
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
    }
}
