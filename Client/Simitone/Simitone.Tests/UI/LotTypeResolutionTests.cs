using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Boundary tests for <see cref="UIMainPanel.ResolveLotType"/>. The house-number ranges are
    /// how Simitone decides which buy catalog to show, and the studio/magic-town split (81-89 vs
    /// 90-99) was previously off by one — MagicTown lots were being treated as studio lots. The
    /// 89/90 boundary is the exact regression this pins down.
    /// </summary>
    public sealed class LotTypeResolutionTests
    {
        private static UICatalogMode Resolve(int house, bool community = false, bool music = false, bool simless = false)
        {
            return UIMainPanel.ResolveLotType(house, community, music, simless);
        }

        // ---- Downtown: 21..31 -------------------------------------------------------------

        [Theory]
        [InlineData(21)]
        [InlineData(26)]
        [InlineData(31)]
        public void DowntownRange_IsDowntown(int house)
        {
            Assert.Equal(UICatalogMode.Downtown, Resolve(house));
        }

        [Theory]
        [InlineData(20)]
        [InlineData(32)]
        public void DowntownRangeBoundaries_AreExclusive(int house)
        {
            Assert.Equal(UICatalogMode.Normal, Resolve(house));
        }

        [Fact]
        public void DowntownRange_WinsOverCommunityAndMusic()
        {
            Assert.Equal(UICatalogMode.Downtown, Resolve(25, community: true, music: true, simless: true));
        }

        // ---- Vacation: 40..49 -------------------------------------------------------------

        [Theory]
        [InlineData(40)]
        [InlineData(45)]
        [InlineData(49)]
        public void VacationRange_IsVacation(int house)
        {
            Assert.Equal(UICatalogMode.Vacation, Resolve(house));
        }

        [Theory]
        [InlineData(39)]
        [InlineData(50)]
        public void VacationRangeBoundaries_AreExclusive(int house)
        {
            Assert.Equal(UICatalogMode.Normal, Resolve(house));
        }

        // ---- Studiotown: 81..89 -----------------------------------------------------------

        [Theory]
        [InlineData(81)]
        [InlineData(85)]
        [InlineData(89)]
        public void StudiotownRange_IsStudiotown(int house)
        {
            Assert.Equal(UICatalogMode.Studiotown, Resolve(house));
        }

        [Theory]
        [InlineData(80)]
        [InlineData(90)]
        public void StudiotownRangeBoundaries_AreExclusive(int house)
        {
            // 90 is the first magic-town lot, not a studio lot.
            Assert.Equal(UICatalogMode.Normal, Resolve(house));
        }

        [Fact]
        public void StudiotownRange_WinsOverCommunityAndMusic()
        {
            Assert.Equal(UICatalogMode.Studiotown, Resolve(85, community: true, music: true));
        }

        // ---- Magictown: 90..99, only with music or community -------------------------------

        [Theory]
        [InlineData(90)]
        [InlineData(95)]
        [InlineData(99)]
        public void MagicTownRange_IsMagicTown_WhenMusic(int house)
        {
            Assert.Equal(UICatalogMode.Magictown, Resolve(house, music: true));
        }

        [Theory]
        [InlineData(90)]
        [InlineData(99)]
        public void MagicTownRange_IsMagicTown_WhenCommunity(int house)
        {
            Assert.Equal(UICatalogMode.Magictown, Resolve(house, community: true));
        }

        [Theory]
        [InlineData(90)]
        [InlineData(99)]
        public void MagicTownRange_FallsThrough_WhenNeitherMusicNorCommunity(int house)
        {
            Assert.Equal(UICatalogMode.Normal, Resolve(house));
        }

        // ---- Community fallback ------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(20)]
        [InlineData(32)]
        [InlineData(50)]
        [InlineData(80)]
        [InlineData(100)]
        public void CommunityZoned_IsCommunity(int house)
        {
            Assert.Equal(UICatalogMode.Community, Resolve(house, community: true));
        }

        // ---- Final fallback: music + simless build mode ------------------------------------

        [Fact]
        public void MusicAndSimlessBuildMode_IsDowntown()
        {
            Assert.Equal(UICatalogMode.Downtown, Resolve(100, music: true, simless: true));
        }

        [Fact]
        public void MusicWithoutSimlessBuildMode_IsNormal()
        {
            Assert.Equal(UICatalogMode.Normal, Resolve(100, music: true));
        }

        [Fact]
        public void SimlessBuildModeWithoutMusic_IsNormal()
        {
            Assert.Equal(UICatalogMode.Normal, Resolve(100, simless: true));
        }
    }
}
