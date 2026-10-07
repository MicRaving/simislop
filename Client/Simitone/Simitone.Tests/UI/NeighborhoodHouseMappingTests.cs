using Simitone.Client.UI.Screens;
using Simitone.Client.UI.Services;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// "Return to neighbourhood" (ported PR #57) has to send the player back to the map the
    /// lot actually belongs to. <see cref="NeighborhoodService.NeighbourhoodIdForHouse"/> is the
    /// pure lot-number to neighbourhood-id mapping behind that, and it deliberately mirrors the
    /// ranges <c>UIMainPanel.ResolveLotType</c> already uses (pinned by
    /// <see cref="LotTypeResolutionTests"/>).
    ///
    /// The 89/90 boundary is the regression this locks down: the original upstream change used
    /// <c>81..90</c> for Studio before testing <c>90..99</c> for Magic, so lot 90 resolved to
    /// Studio and returning from a Magic Town lot opened the Studio Town map.
    /// </summary>
    public sealed class NeighborhoodHouseMappingTests
    {
        // ---- Downtown: 21..31 -----------------------------------------------------------------

        [Theory]
        [InlineData(21, 2)]
        [InlineData(26, 2)]
        [InlineData(31, 2)]
        public void DowntownRange_MapsToDowntown(short house, ushort expected)
        {
            Assert.Equal(expected, NeighborhoodService.NeighbourhoodIdForHouse(house));
        }

        [Theory]
        [InlineData(20)]
        [InlineData(32)]
        public void DowntownRangeBoundaries_FallBackToTheResidentialNeighbourhood(short house)
        {
            Assert.Equal(NeighbourhoodModeMap.ResidentialDefault, NeighborhoodService.NeighbourhoodIdForHouse(house));
        }

        // ---- Vacation: 40..49 -----------------------------------------------------------------

        [Theory]
        [InlineData(40)]
        [InlineData(45)]
        [InlineData(49)]
        public void VacationRange_MapsToVacation(short house)
        {
            Assert.Equal((ushort)3, NeighborhoodService.NeighbourhoodIdForHouse(house));
        }

        [Theory]
        [InlineData(39)]
        [InlineData(50)]
        public void VacationRangeBoundaries_FallBackToTheResidentialNeighbourhood(short house)
        {
            Assert.Equal(NeighbourhoodModeMap.ResidentialDefault, NeighborhoodService.NeighbourhoodIdForHouse(house));
        }

        // ---- StudioTown: 81..89 ---------------------------------------------------------------

        [Theory]
        [InlineData(81)]
        [InlineData(85)]
        [InlineData(89)]
        public void StudioRange_MapsToStudio(short house)
        {
            Assert.Equal((ushort)5, NeighborhoodService.NeighbourhoodIdForHouse(house));
        }

        [Theory]
        [InlineData(80)]
        public void StudioRangeBoundaries_FallBackToTheResidentialNeighbourhood(short house)
        {
            Assert.Equal(NeighbourhoodModeMap.ResidentialDefault, NeighborhoodService.NeighbourhoodIdForHouse(house));
        }

        // ---- MagicTown: 90..99 ----------------------------------------------------------------

        [Theory]
        [InlineData(90)]
        [InlineData(95)]
        [InlineData(99)]
        public void MagicRange_MapsToMagic(short house)
        {
            Assert.Equal((ushort)7, NeighborhoodService.NeighbourhoodIdForHouse(house));
        }

        // ---- everything else ------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(100)]
        [InlineData(-1)]
        public void EverythingElse_IsTheUnleashedNeighbourhood(short house)
        {
            // "Return to neighbourhood" from a residential lot comes back to the same map the game
            // opens on, so the default of the mapping and the default of the mode agree - which is
            // the thing a base-map default used to get wrong in the other direction.
            Assert.Equal(NeighbourhoodModeMap.Unleashed, NeighborhoodService.NeighbourhoodIdForHouse(house));
            Assert.Equal(NeighbourhoodModeMap.ResidentialDefault, NeighborhoodService.NeighbourhoodIdForHouse(house));
            Assert.Equal(NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.Normal),
                NeighborhoodService.NeighbourhoodIdForHouse(house));
        }
    }
}
