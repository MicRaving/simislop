/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using FSO.SimAntics.Utils;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Reading a neighbourhood into a layout: the lot-location table becomes lots, each lot's
    /// house file becomes the handful of facts the generated world needs, and the skipping rules
    /// are the ones the 2D panel already uses.
    ///
    /// <para>
    /// Nothing here touches a disk, a VM or a graphics device. The neighbourhood is a
    /// <see cref="FakeLotContentSource"/> and the reads are captured as data, which is the split
    /// the builder exists to make: what is expensive and fallible (reading twenty IFF files) is
    /// separated from what is arithmetic (where each lot goes), and only the arithmetic is on
    /// trial here.
    /// </para>
    ///
    /// <para>
    /// The terrain assertions deliberately name <c>VMTS1Activator.HouseNumToType</c> rather than
    /// a copied table. A lot's generated surroundings are only right if they agree with the
    /// ground the player walks on inside the lot, and the only way to keep that true across
    /// changes to either side is to compare against the table itself.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodLayoutTests
    {
        private const int Mode = 1;
        private const int Slot = 0;
        private const int Seed = 987654321;

        private static NeighbourhoodLayout Build(FakeLotContentSource source)
        {
            return NeighbourhoodLayoutBuilder.PackOnGrid(NeighbourhoodLayoutBuilder.ReadLots(source, Mode), Slot, Seed);
        }

        // ---- the lot-location table ------------------------------------------------------------

        [Fact]
        public void ParsedTable_BecomesLotsWithTheirBitmapPositions()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Add(22, 150, 91);
            source.Add(31, 240, 130);

            var layout = Build(source);

            Assert.Equal(3, layout.LotCount);
            var first = layout.FindLot(21);
            Assert.NotNull(first);
            Assert.Equal(102, first.MapX);
            Assert.Equal(88, first.MapY);
            Assert.Equal(150, layout.FindLot(22).MapX);
            Assert.Equal(130, layout.FindLot(31).MapY);
            Assert.Equal(Slot, layout.Slot);
            Assert.Equal(Seed, layout.Seed);
            Assert.Equal(NeighbourhoodLayout.DefaultMapSize, layout.MapSize);
            Assert.Equal(NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, layout.GeneratorVersion);
            Assert.Equal(NeighbourhoodLayout.CurrentSchemaVersion, layout.SchemaVersion);
        }

        [Fact]
        public void TheLot99Sentinel_IsNeverPlaced()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Lines.Add("99,500,500");

            var layout = Build(source);

            Assert.Equal(1, layout.LotCount);
            Assert.Null(layout.FindLot(99));
        }

        [Theory]
        [InlineData("21,102")]          //too few fields
        [InlineData("nonsense,1,2")]    //number is not a number
        [InlineData("22,x,91")]         //x is not a number
        [InlineData("23,150,y")]        //y is not a number
        [InlineData("")]                //empty
        [InlineData("  ,1,2")]          //blank number
        public void MalformedEntries_AreSkippedWithoutTakingTheTableWithThem(string malformed)
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Lines.Add(malformed);
            source.Lines.Add("24,160,92");

            var layout = Build(source);

            Assert.Equal(2, layout.LotCount);
            Assert.NotNull(layout.FindLot(21));
            Assert.NotNull(layout.FindLot(24));
        }

        [Fact]
        public void ANullOrEmptyTable_ProducesAnEmptyLayoutRatherThanAFailure()
        {
            var empty = Build(new FakeLotContentSource());
            Assert.Equal(0, empty.LotCount);
            Assert.Equal(NeighbourhoodExtent.Empty, empty.Extent);

            var nullSource = Build(null);
            Assert.Equal(0, nullSource.LotCount);
        }

        [Fact]
        public void ALotNamedTwice_IsPlacedOnce()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Lines.Add("21,400,400");
            source.Lots[21] = new NeighbourhoodLotRead
            {
                Number = 21,
                MapX = 102,
                MapY = 88,
                HasHouse = true,
                Size = 24,
                Zone = LotZoning.ZoneResidential
            };

            var layout = Build(source);

            Assert.Equal(1, layout.LotCount);
            //the first entry wins, so the lot keeps the position the table gave it first
            Assert.Equal(102, layout.FindLot(21).MapX);
        }

        [Fact]
        public void ALotTheSourceCannotDescribe_IsSkipped()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Add(22, 150, 91);
            source.Unreadable.Add(22);

            var layout = Build(source);

            Assert.Equal(1, layout.LotCount);
            Assert.Null(layout.FindLot(22));
        }

        // ---- lot size --------------------------------------------------------------------------

        [Fact]
        public void Size_ComesFromTheHouseFile()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, size: 64);

            Assert.Equal(64, Build(source).FindLot(21).Size);
        }

        [Theory]
        [InlineData(0, NeighbourhoodLayout.MinLotSize)]
        [InlineData(-8, NeighbourhoodLayout.MinLotSize)]
        [InlineData(1, 1)]
        [InlineData(24, 24)]
        [InlineData(NeighbourhoodLayout.MaxLotSize, NeighbourhoodLayout.MaxLotSize)]
        [InlineData(4096, NeighbourhoodLayout.MaxLotSize)]
        public void Size_IsClampedToSomethingALotCanBe(int raw, int expected)
        {
            Assert.Equal(expected, NeighbourhoodLayoutBuilder.ClampSize(raw));
        }

        [Fact]
        public void ASizeNoLotCanHave_NeverReachesTheMap()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, size: 100000);

            var lot = Build(source).FindLot(21);

            Assert.NotNull(lot);
            Assert.True(lot.Size >= NeighbourhoodLayout.MinLotSize && lot.Size <= NeighbourhoodLayout.MaxLotSize);
        }

        [Fact]
        public void AnEmptyPlot_GetsTheSmallestLegalFootprintRatherThanAnInventedSize()
        {
            var source = new FakeLotContentSource();
            source.Lines.Add("21,102,88");
            source.Lots[21] = new NeighbourhoodLotRead { Number = 21, MapX = 102, MapY = 88, HasHouse = false, Size = 0 };

            var lot = Build(source).FindLot(21);

            Assert.False(lot.HasHouse);
            Assert.True(lot.IsEmptyPlot);
            Assert.Equal(NeighbourhoodLayout.MinLotSize, lot.Size);
        }

        // ---- ground ---------------------------------------------------------------------------

        [Theory]
        [InlineData(28, TerrainType.SAND)]        //Desert Hideaway
        [InlineData(40, TerrainType.SNOW)]        //Snowy Chalet
        [InlineData(46, TerrainType.SAND)]
        [InlineData(90, TerrainType.TS1DarkGrass)]//Mountain Tunnel
        [InlineData(95, TerrainType.TS1AutumnGrass)]
        [InlineData(99, TerrainType.TS1Cloud)]
        [InlineData(21, TerrainType.GRASS)]       //not in the table at all
        public void TerrainType_MatchesTheTableTheActivatorUsesForTheLotItself(int house, TerrainType expected)
        {
            Assert.Equal(expected, NeighbourhoodLayoutBuilder.TerrainTypeFor(house));
        }

        [Fact]
        public void TerrainType_IsCarriedFromTheReadOntoTheLot()
        {
            var source = new FakeLotContentSource();
            source.Add(28, 102, 88, terrain: TerrainType.SAND);

            Assert.Equal(TerrainType.SAND, Build(source).FindLot(28).TerrainType);
        }

        [Fact]
        public void BaseHeight_IsTheMedianOfTheDecodedAltitudes()
        {
            //four bytes per tile, altitude in the second, exactly as DecodeHeights reads it
            var altitude = new byte[]
            {
                0, 10, 0, 0,
                0, 12, 0, 0,
                0, 12, 0, 0,
                0, 40, 0, 0
            };

            //the lower median of {10, 12, 12, 40} is 12: the spike must not win
            Assert.Equal((short)12, NeighbourhoodLayoutBuilder.MedianBaseHeight(altitude));
        }

        [Fact]
        public void BaseHeight_AgreesWithTheActivatorsOwnHeightDecoder()
        {
            var raw = new byte[4 * 64 * 64];
            var random = new Random(4242);
            for (int i = 0; i < raw.Length; i += 4) raw[i + 1] = (byte)random.Next(0, 64);

            var decoded = VMTS1Activator.DecodeHeights(raw);
            var sorted = new List<byte>(decoded);
            sorted.Sort();
            var expected = sorted[(sorted.Count - 1) / 2];

            Assert.Equal((short)expected, NeighbourhoodLayoutBuilder.MedianBaseHeight(raw));
        }

        [Fact]
        public void BaseHeight_OfAMissingOrTruncatedArrayIsFlat()
        {
            Assert.Equal((short)0, NeighbourhoodLayoutBuilder.MedianBaseHeight(null));
            Assert.Equal((short)0, NeighbourhoodLayoutBuilder.MedianBaseHeight(new byte[0]));
            Assert.Equal((short)0, NeighbourhoodLayoutBuilder.MedianBaseHeight(new byte[] { 1, 2, 3 }));
        }

        [Fact]
        public void Grass_IsUniformOnALotThatSaysNothing()
        {
            var uniform = new byte[16];
            for (int i = 0; i < uniform.Length; i++) uniform[i] = 0x7f;

            Assert.False(NeighbourhoodLayoutBuilder.HasGrassVariation(uniform));
            Assert.True(NeighbourhoodLayoutBuilder.HasGrassVariation(new byte[] { 0x7f, 0x7f, 0x40, 0x7f }));
            Assert.False(NeighbourhoodLayoutBuilder.HasGrassVariation(null));
        }

        // ---- house files ----------------------------------------------------------------------

        [Fact]
        public void AHouseFile_FingerprintsItsSizeAndWriteTime()
        {
            var when = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);

            var baseHash = NeighbourhoodLayoutBuilder.HouseHashFor(4096, when);

            Assert.NotEqual(0u, baseHash);
            Assert.Equal(baseHash, NeighbourhoodLayoutBuilder.HouseHashFor(4096, when));
            Assert.NotEqual(baseHash, NeighbourhoodLayoutBuilder.HouseHashFor(4097, when));
            Assert.NotEqual(baseHash, NeighbourhoodLayoutBuilder.HouseHashFor(4096, when.AddSeconds(1)));
        }

        [Fact]
        public void AHashIsNeverZero_BecauseZeroMeansThereIsNoHouse()
        {
            //walk a range of sizes and times looking for the reserved value
            for (long length = 0; length < 64; length++)
            {
                for (int minute = 0; minute < 8; minute++)
                {
                    var hash = NeighbourhoodLayoutBuilder.HouseHashFor(length, new DateTime(2024, 1, 1, 0, minute, 0, DateTimeKind.Utc));
                    Assert.NotEqual(0u, hash);
                }
            }
        }

        [Fact]
        public void ABulldozedLot_IsStillPlaced_ButDrawsNothing()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Add(22, 150, 91, hasHouse: false);

            var layout = Build(source);
            var plot = layout.FindLot(22);

            Assert.NotNull(plot);
            Assert.False(plot.HasHouse);
            Assert.True(plot.IsEmptyPlot);
            Assert.Equal(0u, plot.HouseHash);
            //and the ground type is still resolved, because the lot still has to sit on something
            Assert.Equal(TerrainType.GRASS, plot.TerrainType);
        }

        // ---- zone and occupancy ----------------------------------------------------------------

        [Fact]
        public void CommunityAndResidentialLots_KeepTheirOwnZones()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, zone: LotZoning.ZoneResidential);
            source.Add(22, 150, 91, zone: LotZoning.ZoneCommunity);
            source.Add(81, 200, 120, zone: LotZoning.ZoneStudio);

            var layout = Build(source);

            Assert.Equal(LotZoning.ZoneResidential, layout.FindLot(21).Zone);
            Assert.Equal(LotZoning.ZoneCommunity, layout.FindLot(22).Zone);
            Assert.Equal(LotZoning.ZoneStudio, layout.FindLot(81).Zone);
        }

        [Fact]
        public void Occupancy_IsReadButIsNotPartOfTheLayoutScopeAShouldStore()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Add(22, 150, 91);
            source.Lots[22].Occupied = true;

            var reads = NeighbourhoodLayoutBuilder.ReadLots(source, Mode);

            Assert.False(reads[0].Occupied);
            Assert.True(reads[1].Occupied);

            //and nothing about the packed layout changes because a family moved in: occupancy
            //decides what a lot is for, not where it is
            var before = Build(new FakeLotContentSource { Lines = new List<string>(source.Lines), Lots = Clone(source.Lots) });
            Assert.Equal(before.FindLot(22).WorldX, Build(source).FindLot(22).WorldX);
            Assert.Equal(before.FindLot(22).WorldY, Build(source).FindLot(22).WorldY);
        }

        private static Dictionary<int, NeighbourhoodLotRead> Clone(Dictionary<int, NeighbourhoodLotRead> from)
        {
            var copy = new Dictionary<int, NeighbourhoodLotRead>();
            foreach (var pair in from)
            {
                copy[pair.Key] = new NeighbourhoodLotRead
                {
                    Number = pair.Value.Number,
                    MapX = pair.Value.MapX,
                    MapY = pair.Value.MapY,
                    HasHouse = pair.Value.HasHouse,
                    Size = pair.Value.Size,
                    HouseHash = pair.Value.HouseHash,
                    TerrainType = pair.Value.TerrainType,
                    Zone = pair.Value.Zone
                };
            }
            return copy;
        }

        // ---- the model's own rules -------------------------------------------------------------

        [Fact]
        public void Lots_AreHeldInAscendingNumberOrderAndDeduplicated()
        {
            var lots = new[]
            {
                new NeighbourhoodLot { Number = 31, Size = 10, WorldX = 5, WorldY = 5 },
                new NeighbourhoodLot { Number = 21, Size = 10, WorldX = 0, WorldY = 0 },
                new NeighbourhoodLot { Number = 21, Size = 10, WorldX = 90, WorldY = 90 }
            };

            var layout = new NeighbourhoodLayout(0, 1, 1, 1, 512, lots);

            Assert.Equal(2, layout.LotCount);
            Assert.Equal(21, layout.Lots[0].Number);
            Assert.Equal(31, layout.Lots[1].Number);
            //the first of a duplicate pair is the one that survives
            Assert.Equal(0, layout.FindLot(21).WorldX);
        }

        [Fact]
        public void Extent_CoversEveryLotAndNotTheWholeMap()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, size: 20);
            source.Add(22, 150, 91, size: 20);

            var layout = Build(source);
            var a = layout.FindLot(21);
            var b = layout.FindLot(22);

            var expected = new NeighbourhoodExtent(
                Math.Min(a.WorldX, b.WorldX), Math.Min(a.WorldY, b.WorldY),
                Math.Max(a.WorldX, b.WorldX) + 20, Math.Max(a.WorldY, b.WorldY) + 20);

            Assert.Equal(expected, layout.Extent);
            Assert.True(layout.Extent.Contains(a.WorldX, a.WorldY));
            Assert.True(layout.Extent.Contains(b.WorldX + 19, b.WorldY + 19));
            //it is a bounding box of the lots, not the map
            Assert.True(layout.Extent.Width <= layout.MapSize);
            Assert.True(layout.Extent.Union(new NeighbourhoodExtent(0, 0, 512, 512))
                .Equals(new NeighbourhoodExtent(0, 0, 512, 512)));
        }

        [Fact]
        public void Extent_OfNothingIsEmptyAndContainsNothing()
        {
            Assert.Equal(0, NeighbourhoodExtent.Empty.Width);
            Assert.Equal(0, NeighbourhoodExtent.Empty.Height);
            Assert.False(NeighbourhoodExtent.Empty.Contains(0, 0));
            //an empty extent contributes nothing to a union: the other box wins
            Assert.Equal(new NeighbourhoodExtent(3, 4, 9, 10),
                NeighbourhoodExtent.Empty.Union(new NeighbourhoodExtent(3, 4, 9, 10)));
            Assert.Equal(new NeighbourhoodExtent(3, 4, 9, 10),
                new NeighbourhoodExtent(3, 4, 9, 10).Union(NeighbourhoodExtent.Empty));
            //and a real union really is a union
            Assert.Equal(new NeighbourhoodExtent(1, 1, 8, 8),
                new NeighbourhoodExtent(1, 1, 5, 5).Union(new NeighbourhoodExtent(3, 3, 8, 8)));
        }

        [Fact]
        public void HalfOpenExtents_DoNotClaimTheTileAfterTheirEdge()
        {
            var box = new NeighbourhoodExtent(0, 0, 10, 10);

            Assert.True(box.Contains(0, 0));
            Assert.True(box.Contains(9, 9));
            Assert.False(box.Contains(10, 5));
            Assert.False(box.Contains(5, 10));
        }

        [Fact]
        public void LotAt_FindsTheLotCoveringATileAndNothingElse()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, size: 20);

            var layout = Build(source);
            var lot = layout.FindLot(21);

            Assert.Equal(21, layout.LotAt(lot.WorldX, lot.WorldY).Number);
            Assert.Equal(21, layout.LotAt(lot.WorldX + 19, lot.WorldY + 19).Number);
            Assert.Null(layout.LotAt(lot.WorldX + 20, lot.WorldY));
            Assert.Null(layout.LotAt(-1, -1));
            Assert.Null(layout.FindLot(9999));
        }

        [Fact]
        public void ForwardFields_StartAtTheirDefaults_SoTheSeamIsInert()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);

            var lot = Build(source).FindLot(21);

            Assert.Equal(NeighbourhoodLotState.Unloaded, lot.LotState);
            Assert.Equal(0L, lot.LastSimulated);
            Assert.Null(lot.Entrance);
        }

        [Fact]
        public void ALayoutIsOnlyCurrentForTheSeedAndGeneratorThatBuiltIt()
        {
            var layout = Build(new FakeLotContentSource());

            Assert.True(layout.IsCurrentFor(Seed, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion));
            Assert.False(layout.IsCurrentFor(Seed + 1, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion));
            Assert.False(layout.IsCurrentFor(Seed, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion + 1));
        }

        [Fact]
        public void Rotation_IsRecordedEvenThoughScopeAArrangesLotsAxisAligned()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Lots[21].Rotation = 3;

            Assert.Equal(3, Build(source).FindLot(21).Rotation);
        }
    }
}
