/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The one transform between the 800x600 neighbourhood bitmap and the generated map's tiles.
    ///
    /// <para>
    /// <b>Why a whole file for a projection and a fit.</b> Because this is the one place the lot
    /// positions, the road mask, the camera, the house buttons and the click test all have to
    /// agree, and because §8.4 froze its signatures. What has to hold is therefore stated here as
    /// arithmetic rather than left to five call sites that each happen to be right today: the round
    /// trip is exact, the bitmap's centre is the drawn region's centre, and - the headline, and
    /// what Part 9 is about - <b>every position on the bitmap lands on ground the map draws</b>.
    /// </para>
    ///
    /// <para>
    /// <b>The headline used to be the opposite.</b> The transform mapped the 800x600 backdrop onto
    /// the whole 512x512 square while every draw and cull path clips to the isometric diamond, so a
    /// lot near a bitmap corner stood in one of the four cut corners where no ground quad and no
    /// road quad are emitted. Six of these tests changed with it, and each says so where it does.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodPixelTransformTests
    {
        /// <summary>
        /// The six shipped <c>LotLocations.iff</c> STR chunks, verbatim: mode, then every lot's
        /// <c>number,x,y</c>.
        /// </summary>
        /// <remarks>
        /// <b>Real data, and deliberately so.</b> §9.3's rule - "a bitmap position becomes a
        /// diamond-interior tile position through the bitmap's own isometric projection" - is only
        /// worth anything if it holds for the neighbourhoods players actually load, and the only
        /// way to say that is to hand the test the coordinates the game reads. They are taken from
        /// a stock TS1 install's <c>UserData\LotLocations.iff</c>, one line per lot, first entry
        /// winning exactly as <see cref="LotLocationParser"/> resolves it.
        ///
        /// <para>Ninety-eight is the highest lot number in the table; 99 is the sentinel the parser
        /// drops, and no shipped chunk uses it.</para>
        /// </remarks>
        private static readonly string[][] RealLotLocations =
        {
            // 1 - Sim Valley, "Sims 1.0 Original Neighborhood"
            new[]
            {
                "1,162,118", "2,636,98", "3,359,226", "4,209,301", "5,522,377",
                "6,105,478", "7,376,450", "8,672,473", "9,277,551", "10,527,544"
            },
            // 2 - Downtown, "Sims Hot Date Downtown"
            new[]
            {
                "21,187,236", "22,359,152", "23,508,79", "24,176,382", "25,328,306",
                "26,497,221", "27,649,145", "28,332,457", "29,493,385", "30,666,305"
            },
            // 3 - Vacation Island, "Sims Vacation Island"
            new[]
            {
                "40,112,103", "41,353,138", "42,632,102", "43,293,345", "44,544,382",
                "45,684,311", "46,357,473", "47,482,538", "48,672,468"
            },
            // 4 - Unleashed / Community, "Sims Unleashed Neighborhood" - the largest, forty-one lots
            new[]
            {
                "1,480,84", "2,722,75", "3,578,142", "4,506,177", "5,658,214", "6,456,262",
                "7,589,249", "8,735,265", "9,538,303", "10,667,299", "50,87,104", "51,226,110",
                "52,84,182", "53,147,213", "54,163,143", "55,227,172", "56,303,213", "57,74,253",
                "58,225,262", "59,133,296", "60,181,331", "61,52,347", "62,208,396", "63,76,412",
                "64,14,458", "65,77,490", "66,143,444", "67,273,428", "68,298,350", "69,359,319",
                "70,421,368", "71,699,413", "72,765,444", "73,595,453", "74,534,504", "75,467,534",
                "76,400,577", "77,672,508", "78,592,551", "79,660,580", "80,730,542"
            },
            // 5 - Studio Town, "Sims Superstar - Studio town"
            new[]
            {
                "81,70,282", "82,235,174", "83,430,109", "84,115,441", "85,348,326",
                "86,574,247", "87,286,536", "88,416,470", "89,760,340", "90,70,253"
            },
            // 7 - Magicland, "Sims Spellbound - Magic land"
            new[]
            {
                "90,70,244", "91,181,132", "92,457,107", "93,147,503", "94,277,397",
                "95,560,321", "96,670,209", "97,574,531", "98,689,443"
            },
        };

        private static IEnumerable<Vector2> RealLotCentres()
        {
            foreach (var mode in RealLotLocations)
            {
                foreach (var entry in (string[])mode)
                {
                    int number, x, y;
                    Assert.True(LotLocationParser.TryParseLotLocation(entry, out number, out x, out y),
                        entry + " is not a LotLocations line");
                    yield return new Vector2(x, y);
                }
            }
        }

        [Fact]
        public void TheBitmapIsEightHundredBySixHundred()
        {
            // Not a preference. Every TS1 neighbourhood backdrop is this size, and the lot-location
            // table's x/y are pixels in it - so a different constant here would silently move every
            // lot on every map.
            Assert.Equal(800, NeighbourhoodPixelTransform.BitmapWidth);
            Assert.Equal(600, NeighbourhoodPixelTransform.BitmapHeight);
        }

        [Fact]
        public void EveryPositionOnTheBitmapLandsOnDrawnGround()
        {
            // The defect, as arithmetic rather than as a screenshot. The transform used to map the
            // 800x600 backdrop onto the whole 512x512 square, so a position near a corner landed in
            // one of the four cut corners where the mesh builder emits neither ground nor roads -
            // "disconnected houses floating in a void, without lighting". Swept at the corners, the
            // edges and the centre: the fit covers the whole bitmap, so nothing on it can miss.
            var size = NeighbourhoodLayout.DefaultMapSize;
            var misses = new List<string>();

            foreach (var pixelX in new[] { 0, 1, 200, 400, 599, 799, 800 })
            {
                foreach (var pixelY in new[] { 0, 1, 150, 300, 450, 599, 600 })
                {
                    var tile = NeighbourhoodPixelTransform.TileForPixel(new Vector2(pixelX, pixelY), size);
                    if (!InsideDiamond(tile)) misses.Add(pixelX + "," + pixelY + " -> " + tile);
                }
            }

            Assert.True(misses.Count == 0, string.Join("; ", misses));
        }

        [Fact]
        public void EveryRealLotLandsInsideTheDrawnRegion()
        {
            // §9.3's rule, asked of the six neighbourhoods a stock install ships. Not "the
            // transformed bitmap is inside the diamond", which is a property of the fit: this is the
            // lot positions the game actually reads, one line each, and the assertion is the one
            // RW0 exists for - every real lot has ground and roads under it.
            var size = NeighbourhoodLayout.DefaultMapSize;
            var misses = new List<string>();
            var worst = int.MaxValue;

            foreach (var centre in RealLotCentres())
            {
                var tile = NeighbourhoodPixelTransform.TileForPixel(centre, size);
                if (InsideDiamond(tile))
                {
                    var row = (int)Math.Floor(tile.Y);
                    var column = (int)Math.Floor(tile.X);
                    var slack = Math.Min(
                        column - NeighbourhoodCityMeshBuilder.DiamondLeft(row),
                        NeighbourhoodCityMeshBuilder.DiamondRight(row) - column);
                    if (slack < worst) worst = slack;
                    continue;
                }
                misses.Add(centre + " -> " + tile);
            }

            Assert.True(misses.Count == 0, "these lots stand on ground that is never drawn: "
                + string.Join("; ", misses));
            //and they are not scraping the edge either: the fit holds every one of them a street
            //corridor or more inside the clip, so a lot's driveway and its kerb are on ground too
            Assert.True(worst >= NeighbourhoodLayoutBuilder.StreetCorridorTiles,
                "the tightest real lot is only " + worst + " tile(s) inside the diamond");
        }

        [Fact]
        public void TheFitIsTheOneTheDiamondHasRoomFor()
        {
            // The fit is not a preference, so it is derived rather than asserted: it puts the whole
            // bitmap on the sum diagonal, one fade skirt inside the diamond, and takes the
            // difference diagonal the projection then hands it. These are the three numbers the
            // class documents rest on, and a change to any of the mesh builder's diamond constants
            // moves them rather than silently invalidating the guarantee above.
            var sumSpan = NeighbourhoodCityMeshBuilder.MapSize
                + NeighbourhoodCityMeshBuilder.DiamondRightPivot - NeighbourhoodCityMeshBuilder.DiamondCentre;
            Assert.Equal(sumSpan - 2 * NeighbourhoodPixelTransform.EdgeMargin,
                NeighbourhoodPixelTransform.SumInside(), 3);

            //the difference diagonal's own room is more than the fit uses, or the fit would
            //overflow it and the guarantee above would not hold
            var differenceSpan = NeighbourhoodCityMeshBuilder.DiamondRightBase
                + NeighbourhoodCityMeshBuilder.DiamondCentre;
            Assert.Equal(differenceSpan - 2 * NeighbourhoodPixelTransform.EdgeMargin,
                NeighbourhoodPixelTransform.DifferenceInside(), 3);

            Assert.Equal(
                (NeighbourhoodPixelTransform.ReferenceSumMin + NeighbourhoodPixelTransform.ReferenceSumMax) / 2f,
                NeighbourhoodPixelTransform.SumMid(), 3);
            Assert.Equal(
                (NeighbourhoodPixelTransform.ReferenceDifferenceMin
                    + NeighbourhoodPixelTransform.ReferenceDifferenceMax) / 2f,
                NeighbourhoodPixelTransform.DifferenceMid(), 3);
        }

        [Fact]
        public void TheBitmapCentreIsTheDrawnRegionsCentre()
        {
            // The anchoring every full-screen camera target assumes: NeighCameraMachine's
            // full-screen bitmap centre is (400, 300), and it has to be the middle of the drawn
            // island.
            //
            // CHANGED by the Part 9 TSO-alignment rework. This used to assert the *square*'s
            // centre, (256, 256). The diamond's two diagonals are 411 and 613 rather than equal, so
            // the middle of the drawn island is half a tile above the middle of the square - and
            // that half tile is the difference between "centred on the map" and "centred on the
            // thing the player can see".
            var size = NeighbourhoodLayout.DefaultMapSize;
            var centre = NeighbourhoodPixelTransform.TileForPixel(new Vector2(400, 300), size);

            Assert.Equal(NeighbourhoodCityMeshBuilder.MapSize / 2f, centre.X, 3);
            Assert.True(InsideDiamond(centre));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(400, 300)]
        [InlineData(636, 102)]
        [InlineData(799, 599)]
        [InlineData(800, 600)]
        public void TheRoundTripIsExact(int pixelX, int pixelY)
        {
            var size = NeighbourhoodLayout.DefaultMapSize;

            var tile = NeighbourhoodPixelTransform.TileForPixel(new Vector2(pixelX, pixelY), size);
            var back = NeighbourhoodPixelTransform.PixelForTile(tile, size);

            Assert.Equal(pixelX, back.X, 3);
            Assert.Equal(pixelY, back.Y, 3);
        }

        [Fact]
        public void GrowingThePixelGrowsTheTile()
        {
            // The projection's shear, which the axis-aligned square mapping had thrown away and
            // which is back with Part 9. A step right on the bitmap is a step right-and-up in the
            // world; a step down it is a step right-and-down. Getting a sign wrong here mirrors the
            // neighbourhood diagonally, which looks like a layout problem and is not.
            var size = NeighbourhoodLayout.DefaultMapSize;
            var origin = NeighbourhoodPixelTransform.TileForPixel(new Vector2(300, 300), size);
            var right = NeighbourhoodPixelTransform.TileForPixel(new Vector2(400, 300), size);
            var down = NeighbourhoodPixelTransform.TileForPixel(new Vector2(300, 400), size);

            Assert.True(right.X > origin.X, "a step right must move right in the world");
            Assert.True(right.Y < origin.Y, "a step right must move up in the world");
            Assert.True(down.X > origin.X, "a step down must move right in the world");
            Assert.True(down.Y > origin.Y, "a step down must move down in the world");
            //and the two are the two halves of (x + 2y, 2y - x): the difference diagonal
            //moves tile x and tile row by equal and opposite amounts, the sum diagonal equally
            Assert.Equal(origin.X - right.X, right.Y - origin.Y, 3);
            Assert.Equal(down.Y - origin.Y, down.X - origin.X, 3);
        }

        [Fact]
        public void TheFitIsAQuarterTurnAndOneScalePerDiagonal()
        {
            // The shape is not a decision this test gets to make: §9.3 names the OBJ exporter's
            // projection, so the composed transform is compared against it here rather than
            // restated. What is chosen is the fit - a quarter turn into the diamond's frame plus
            // one scale per diagonal - and that is what has to hold: the two bitmap axes must land
            // on the two 45-degree diagonals, and each must be scaled onto exactly the room its own
            // diagonal has. A single divisor instead would satisfy the first half and throw half the
            // island's width away.
            var size = NeighbourhoodLayout.DefaultMapSize;
            var origin = NeighbourhoodPixelTransform.TileForPixel(new Vector2(300, 300), size);
            var right = NeighbourhoodPixelTransform.TileForPixel(new Vector2(310, 300), size);
            var down = NeighbourhoodPixelTransform.TileForPixel(new Vector2(300, 310), size);

            //bitmap x drives difference only: tile x up, tile row down, by equal amounts
            Assert.Equal(origin.X - right.X, right.Y - origin.Y, 3);
            //and bitmap y drives sum only: both up, by equal amounts
            Assert.Equal(down.X - origin.X, down.Y - origin.Y, 3);
            //neither axis touches the other diagonal, so the two are the projection's own
            Assert.Equal(right.X - origin.X, NeighbourhoodPixelTransform.DifferenceInside() * 10 / 800f / 2f, 3);
            Assert.Equal(down.X - origin.X, NeighbourhoodPixelTransform.SumInside() * 10 / 600f / 2f, 3);
        }

        [Fact]
        public void ATestMapGetsTheSameArrangementAsAFullOne()
        {
            // A test's 96-tile map and the game's 512-tile one are the same neighbourhood at
            // different scales: two lots either side of each other on the bitmap are two lots
            // either side of each other on the map, at either size. That is what makes a small-map
            // fixture meaningful rather than a different world.
            var wide = NeighbourhoodPixelTransform.TileForPixel(new Vector2(600, 100), 512);
            var small = NeighbourhoodPixelTransform.TileForPixel(new Vector2(600, 100), 96);

            var wideX = (decimal)wide.X / 512m;
            var smallX = (decimal)small.X / 96m;
            var wideY = (decimal)wide.Y / 512m;
            var smallY = (decimal)small.Y / 96m;

            Assert.Equal(wideX, smallX, 4);
            Assert.Equal(wideY, smallY, 4);
        }

        [Fact]
        public void ACentreBecomesAnOriginHalfALotBack()
        {
            // The two steps the layout builder needs and must not spell out separately:
            // LotLocations.iff gives a centre, the map wants an origin.
            //
            // CHANGED by the Part 9 TSO-alignment rework, which is what put the half tile in: the
            // bitmap's centre is the drawn region's centre, and the region's row is half a tile
            // above the square's.
            var size = 512;
            var centre = new Vector2(400, 300);
            var tile = NeighbourhoodPixelTransform.TileForPixel(centre, size);
            var origin = NeighbourhoodPixelTransform.OriginForCentre(centre, 64, size);

            Assert.Equal((int)Math.Floor(tile.X) - 32, origin.X);
            Assert.Equal((int)Math.Floor(tile.Y) - 32, origin.Y);
            //and the centre is inside the rectangle the origin names
            Assert.InRange(tile.X, origin.X, origin.X + 64);
            Assert.InRange(tile.Y, origin.Y, origin.Y + 64);
        }

        [Fact]
        public void AnOriginIsClampedIntoTheMapAndNothingElse()
        {
            // The one adjustment the builder makes to a map-derived position. Clamping is not
            // inventing: a lot whose bitmap position is nonsense has to be put somewhere legal, and
            // a rectangle that hung off the edge would be cut in half by the mesh builder.
            Assert.Equal(new Point(0, 0),
                NeighbourhoodPixelTransform.ClampOrigin(new Point(-40, -3), 32, 512));
            Assert.Equal(new Point(480, 480),
                NeighbourhoodPixelTransform.ClampOrigin(new Point(900, 900), 32, 512));
            //a legal one is untouched
            Assert.Equal(new Point(100, 200),
                NeighbourhoodPixelTransform.ClampOrigin(new Point(100, 200), 32, 512));
            //and a lot bigger than the map lands at the origin rather than off both edges
            Assert.Equal(new Point(0, 0),
                NeighbourhoodPixelTransform.ClampOrigin(new Point(400, 400), 64, 32));
        }

        [Fact]
        public void DegenerateMapSizesDoNotDivideByZero()
        {
            // A camera that has just lost its device asks this question with a default-constructed
            // layout behind it. A crash over a zero is a worse answer than a one-tile map.
            var tile = NeighbourhoodPixelTransform.TileForPixel(new Vector2(400, 300), 0);
            Assert.False(float.IsNaN(tile.X));
            Assert.False(float.IsInfinity(tile.X));

            var back = NeighbourhoodPixelTransform.PixelForTile(tile, -5);
            Assert.False(float.IsNaN(back.X));
            Assert.False(float.IsNaN(back.Y));
        }

        /// <summary>The exporter's own projection, at its own divisor.</summary>
        private static Vector2 Projected(Vector2 pixel)
        {
            NeighbourhoodLayoutBuilder.ProjectMapPosition((int)pixel.X, (int)pixel.Y, 20,
                out var x, out var z);
            return new Vector2(x, z);
        }

        /// <summary>
        /// Whether a tile position is on drawn ground, with a slack in whole tiles so the assertion
        /// names the nearest edge rather than only answering yes or no.
        /// </summary>
        private static bool InsideDiamond(Vector2 tile)
        {
            if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(
                (int)Math.Floor(tile.X), (int)Math.Floor(tile.Y))) return false;
            var row = (int)Math.Floor(tile.Y);
            var column = (int)Math.Floor(tile.X);
            return column > NeighbourhoodCityMeshBuilder.DiamondLeft(row)
                && column < NeighbourhoodCityMeshBuilder.DiamondRight(row);
        }
    }
}
