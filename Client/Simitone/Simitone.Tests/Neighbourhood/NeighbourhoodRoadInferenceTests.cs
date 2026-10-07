/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The connector mask: read out of a neighbourhood's 2D backdrop, or inferred from the lot
    /// lattice when the pixels are not available.
    ///
    /// <para>
    /// <b>All of it is run against synthetic bitmaps.</b> There is no TS1 install, no provider and no
    /// graphics device anywhere in this file - a <see cref="TexBitmap"/> is a complete input, which
    /// is what makes the one genuinely heuristic step in Part 8 arguable rather than merely
    /// asserted. The three properties that matter are: a grey or black road is found, a green field
    /// is not, and a bitmap that classifies as nothing (or as everything) does not become a
    /// neighbourhood with no streets - it becomes the deterministic fallback with a reason.
    /// </para>
    /// </summary>
    [Collection("generated world cache")]
    public sealed class NeighbourhoodRoadInferenceTests
    {
        private const int Width = NeighbourhoodPixelTransform.BitmapWidth;
        private const int Height = NeighbourhoodPixelTransform.BitmapHeight;

        public NeighbourhoodRoadInferenceTests()
        {
            NeighbourhoodRoadInference.ForgetAll();
        }

        // ---- the classifier ------------------------------------------------------------------

        [Theory]
        [InlineData(96, 96, 96, true)]      //US grey tarmac
        [InlineData(128, 128, 128, true)]   //mid grey
        [InlineData(32, 32, 32, true)]      //International black asphalt
        [InlineData(8, 8, 8, true)]         //the darkest the test admits
        [InlineData(60, 62, 64, true)]      //slightly blue-shifted tarmac: still no chroma
        [InlineData(0, 0, 0, false)]        //an unset palette entry, not a road
        [InlineData(255, 255, 255, false)]  //a white highlight
        [InlineData(40, 120, 40, false)]    //grass
        [InlineData(30, 60, 140, false)]    //the sea
        [InlineData(200, 180, 120, false)]  //sand
        [InlineData(120, 200, 90, false)]   //a tree canopy
        [InlineData(12, 28, 12, false)]     //a DARK tree canopy: low absolute chroma, high hue
        [InlineData(21, 36, 12, false)]     //shadowed ground: the same failure, the other pixel
        public void ARoadPixelIsGreyAndDarkToMedium(int r, int g, int b, bool expected)
        {
            Assert.Equal(expected, NeighbourhoodRoadInference.IsRoadPixel(r, g, b));
        }

        [Fact]
        public void TheClassifierIsSymmetricInTheChannels()
        {
            // TexBitmap is documented BGRA and Simitone's own decoder returns RGBA. Rather than
            // swizzle one of them for a classifier that only ever asks for max, min and a luma, the
            // test pins the property that makes that safe.
            for (int v = 0; v < 256; v += 17)
            {
                Assert.Equal(NeighbourhoodRoadInference.IsRoadPixel(v, 100, 60),
                    NeighbourhoodRoadInference.IsRoadPixel(100, v, 60));
            }
        }

        // ---- reading a bitmap ------------------------------------------------------------------

        [Fact]
        public void AGreyRoadBecomesRoadTilesAndGreenGrassDoesNot()
        {
            var bitmap = Bitmap(fill: Grass);
            LayStreetGrid(bitmap, 128, 128, 128);

            var mask = NeighbourhoodRoadInference.BuildFromBitmap(
                4, bitmap, 64, NoLots(), out var reason);

            Assert.Equal(64 * 64, mask.Length);
            Assert.True(NeighbourhoodRoadInference.CameFromBitmap(reason), reason);

            //CHANGED by the Part 9 TSO-alignment rework. This used to name the tiles outright - (32, 28..35)
            //and (2, 2) - which is only answerable while the transform is an axis-aligned square
            //mapping. It now asks the transform which tile a pixel known to be road falls in, so it
            //is still about the classifier and is no longer a second accidental copy of the
            //transform's scale factors.
            Assert.True(mask[TileAt(410, 310)], "the tile at the middle of the road grid is not road");
            Assert.False(mask[TileAt(750, 560)], "a tile of open grass was read as road");
        }

        [Fact]
        public void ABlackRoadIsFoundJustAsAGreyOneIs()
        {
            // The two editions paint their roads differently and both have to classify, which is why
            // the test is "low chroma, dark to medium" and not "this exact grey".
            var bitmap = Bitmap(fill: Grass);
            LayStreetGrid(bitmap, 24, 24, 24);

            var mask = NeighbourhoodRoadInference.BuildFromBitmap(
                7, bitmap, 64, NoLots(), out var reason);

            Assert.True(NeighbourhoodRoadInference.CameFromBitmap(reason), reason);
            //CHANGED by the Part 9 TSO-alignment rework: a named tile index is only answerable
            //under an axis-aligned square mapping, so the tile comes from the transform now.
            Assert.True(mask[TileAt(410, 310)]);
            Assert.False(mask[TileAt(750, 560)]);
        }

        [Fact]
        public void ABitmapOfNothingButGrassFallsBackToTheLotLattice()
        {
            // The degenerate case a mis-decoded palette produces. Honouring it would give a
            // neighbourhood with no streets at all, which looks like a successful inference rather
            // than a failure - so the fallback takes over and says so.
            var reason = string.Empty;
            var mask = NeighbourhoodRoadInference.BuildFromBitmap(
                4, Bitmap(fill: Grass), 64, LatticeLots(), out reason);

            Assert.False(NeighbourhoodRoadInference.CameFromBitmap(reason), reason);
            Assert.Contains("lot lattice", reason);
            Assert.True(Count(mask) > 0, "the lattice fallback found no street at all");
        }

        [Fact]
        public void ABitmapOfNothingButTarmacFallsBackToTheLotLattice()
        {
            // The other degenerate shape: a solid fill, or a palette that decoded to one colour.
            var reason = string.Empty;
            var mask = NeighbourhoodRoadInference.BuildFromBitmap(
                4, Bitmap(fill: Tarmac), 64, LatticeLots(), out reason);

            Assert.False(NeighbourhoodRoadInference.CameFromBitmap(reason), reason);
            Assert.Contains("lot lattice", reason);
            Assert.True(Count(mask) > 0);
        }

        [Fact]
        public void ANullOrUndecodableBackdropFallsBackWithAReason()
        {
            string reason;

            var noReference = NeighbourhoodRoadInference.Build(
                6, null, 64, LatticeLots(), out reason);
            Assert.False(NeighbourhoodRoadInference.CameFromBitmap(reason), reason);
            Assert.Contains("lot lattice", reason);
            Assert.True(Count(noReference) > 0);

            var nothing = NeighbourhoodRoadInference.BuildFromBitmap(
                6, new TexBitmap(), 64, LatticeLots(), out reason);
            Assert.False(NeighbourhoodRoadInference.CameFromBitmap(reason), reason);
            Assert.Contains("decoded to nothing", reason);
            Assert.Equal(noReference.Length, nothing.Length);
        }

        [Fact]
        public void ACallerWithNoPixelsGetsTheInstallLookedInBeforeTheLattice()
        {
            // The defect this is the regression for. The production caller
            // (TS1LotContentSource.BackdropBitmap) only ever looks for the backdrop as a loose file
            // under UserData, and on a stock install it is not there - it is an entry of
            // UIGraphics.far - so it handed over null and every neighbourhood got the lattice. The
            // fix is that a null bitmap means "go and find it", so this has to produce a mask from
            // wherever the file actually is - and the reason has to name that place, because a line
            // saying "decoded to nothing" is what hid the defect in the first place.
            var mask = NeighbourhoodRoadInference.FromInstall(4, 64, LatticeLots(), out var reason);

            Assert.Equal(64 * 64, mask.Length);
            Assert.NotNull(reason);
            Assert.Contains(NeighbourhoodRoadInference.Describe(4), reason);

            if (NeighbourhoodRoadInference.CameFromBitmap(reason))
            {
                //An install was found behind this process and the backdrop was read out of it. The
                //line must then say where, because that is the whole difference from the lattice.
                Assert.Contains("out of", reason);
                Assert.Contains(".far", reason);
                Assert.False(reason.Contains("lot lattice"), reason);
            }
            else
            {
                //No install behind this process - which is what a test run normally is - and the
                //honest answer is still the lattice, with the search named rather than glossed.
                Assert.Contains("lot lattice", reason);
                Assert.Contains(".far archive", reason);
            }
        }

        [Fact]
        public void ForBitmapWithNothingToOfferGoesAndLooksRatherThanGivingUp()
        {
            // The seam the production path actually uses. A null or degenerate bitmap used to go
            // straight to the lattice with "decoded to nothing", which is a lie: nothing was
            // decoded, because nothing was found. Whatever the answer turns out to be, it must not
            // be that sentence - and with no install behind it, the lattice fallback is still right.
            foreach (var nothing in new[]
            {
                null,
                new TexBitmap(),
                new TexBitmap { Data = new byte[0], Width = 8, Height = 8, PixelSize = 4 },
            })
            {
                var mask = NeighbourhoodRoadInference.ForBitmap(
                    4, 64, nothing, LatticeLots(), out var reason);

                Assert.Equal(64 * 64, mask.Length);
                Assert.Contains(NeighbourhoodRoadInference.Describe(4), reason);
                Assert.False(reason.Contains("decoded to nothing"), reason);
            }
        }

        [Fact]
        public void AModeWithNoBackdropNameIsStillAnswered()
        {
            // Nothing to look for, so nothing can be found, and the answer says which of the two
            // reasons it is - the name is missing, not the file.
            var mask = NeighbourhoodRoadInference.FromInstall(6, 64, LatticeLots(), out var reason);
            Assert.Equal(64 * 64, mask.Length);
            Assert.Contains("could not be named", reason);
            Assert.False(NeighbourhoodRoadInference.CameFromBitmap(reason), reason);
        }

        [Fact]
        public void AMapSizeOfZeroIsRefusedByTheInstallRouteTooRatherThanDividedBy()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NeighbourhoodRoadInference.FromInstall(4, 0, LatticeLots(), out _));
        }

        [Fact]
        public void LookingUpOneFileIsEitherItWithItsSourceOrAnAnswerWithAReason()
        {
            // Two states and no third. A null-with-no-reason is what put "decoded to nothing" back
            // on the log: the caller has something to print in one case and something to print in
            // the other, and there is no branch where it has nothing.
            var found = NeighbourhoodRoadInference.FromInstall("Nbhd\\NScreen.BMP");
            Assert.NotNull(found);
            if (found.Bitmap != null)
            {
                Assert.NotNull(found.Source);
                Assert.Null(found.Failure);
                Assert.True(found.Bitmap.Width > 0 && found.Bitmap.Height > 0);
                Assert.NotNull(found.Bitmap.Data);
                Assert.True(found.Bitmap.Data.Length > 0);
            }
            else
            {
                Assert.Null(found.Source);
                Assert.NotNull(found.Failure);
            }

            //and a name nobody could have, which is the other end of the same contract
            Assert.Null(NeighbourhoodRoadInference.FromInstall(null));
            Assert.Null(NeighbourhoodRoadInference.FromInstall(""));
        }

        [Fact]
        public void TheBackdropOfEachModeIsTheOnesThePanelDraws()
        {
            // Six modes, six different file names and one deliberately empty slot. The connectors
            // have to come out of the picture the player is looking at, so the table is read from
            // the 2D panel rather than restated.
            Assert.Equal("Nbhd\\NScreen.BMP", NeighbourhoodRoadInference.Describe(1));
            Assert.Equal("Community\\NScreen_unleashed.bmp", NeighbourhoodRoadInference.Describe(4));
            Assert.Equal("Magicland\\DScreen.bmp", NeighbourhoodRoadInference.Describe(7));
            Assert.Null(NeighbourhoodRoadInference.Describe(6));
            Assert.Null(NeighbourhoodRoadInference.Describe(0));
        }

        [Fact]
        public void AMapSizeOfZeroIsRefusedRatherThanDividedBy()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NeighbourhoodRoadInference.BuildFromBitmap(
                    4, Bitmap(fill: Grass), 0, NoLots(), out _));
        }

        // ---- the lot-lattice fallback -----------------------------------------------------------

        [Fact]
        public void TheLatticeStreetsTheGapBetweenTwoNeighbours()
        {
            // Two lots side by side with a ten-tile gap, and nothing else on the map. The fallback's
            // whole claim is that this is where a street goes, and it needs no pixels to say it.
            var lots = new List<NeighbourhoodLot>
            {
                Lot(21, 10, 10, 32),
                Lot(22, 52, 10, 32),
            };

            var mask = NeighbourhoodRoadInference.BuildFromLattice(lots, 96, out var reason);

            Assert.False(NeighbourhoodRoadInference.CameFromBitmap(reason), reason);
            Assert.Contains("lot lattice", reason);

            //the street lies inside the gap - not under either lot, and not in open ground beyond
            //it. Its exact columns are the corridor rule's business (as wide as the gap allows up to
            //MainStreetWidth, with a pavement tile kept against the low end); what is asserted here
            // is that it is in the gap at all and at the full width the gap allows.
            var road = 0;
            for (var y = 10; y < 42; y++)
            {
                for (var x = 42; x < 52; x++)
                {
                    if (mask[y * 96 + x]) road++;
                }
            }
            Assert.True(road >= RoadGraph.MainStreetWidth * 32, "only " + road + " road tile(s)");

            //and nothing of the street is inside a house
            for (var y = 10; y < 42; y++)
            {
                for (var x = 10; x < 42; x++) Assert.False(mask[y * 96 + x], "under lot 21");
                for (var x = 52; x < 84; x++) Assert.False(mask[y * 96 + x], "under lot 22");
            }
        }

        [Fact]
        public void TheLatticeStreetsTheGapBetweenTwoRows()
        {
            var lots = new List<NeighbourhoodLot>
            {
                Lot(21, 10, 10, 32),
                Lot(22, 10, 52, 32),
            };

            var mask = NeighbourhoodRoadInference.BuildFromLattice(lots, 96, out _);

            var road = 0;
            for (var x = 10; x < 42; x++)
            {
                for (var y = 42; y < 52; y++)
                {
                    if (mask[y * 96 + x]) road++;
                }
            }
            Assert.True(road >= RoadGraph.MainStreetWidth * 32, "only " + road + " road tile(s)");

            //the corner beside both lots is open ground, not street
            Assert.False(mask[47 * 96 + 47]);
        }

        [Fact]
        public void TheLatticeIsDeterministicAndNeedsNoLots()
        {
            var lots = LatticeLots();
            var a = NeighbourhoodRoadInference.BuildFromLattice(lots, 64, out var reasonA);
            var b = NeighbourhoodRoadInference.BuildFromLattice(lots, 64, out var reasonB);

            Assert.Equal(reasonA, reasonB);
            Assert.Equal(a, b);

            //and a neighbourhood with no lots is a legitimate answer, not an exception
            var none = NeighbourhoodRoadInference.BuildFromLattice(NoLots(), 64, out _);
            Assert.Equal(0, Count(none));
            Assert.Equal(64 * 64, none.Length);
        }

        [Fact]
        public void TheLatticeIgnoresLotsThatAreOffTheMapEntirely()
        {
            // A lot outside 0..mapSize can contribute no ground, so it must not reach into the mask
            // through the gap rule and put a street somewhere it cannot be drawn.
            var lots = new List<NeighbourhoodLot>
            {
                Lot(21, -400, 10, 32),
                Lot(22, 52, 10, 32),
            };

            var mask = NeighbourhoodRoadInference.BuildFromLattice(lots, 96, out _);
            for (var y = 10; y < 42; y++) Assert.False(mask[y * 96 + 42]);
        }

        // ---- the cache and the slot binding -----------------------------------------------------

        [Fact]
        public void TheCacheAnswersOnceAndForgetsOnRequest()
        {
            // §8.7's mitigation for pixel inference being new: the decode is the expensive part and
            // depends on nothing but the mode.
            var bitmap = Bitmap(fill: Grass);
            LayStreetGrid(bitmap, 128, 128, 128);

            Assert.Null(NeighbourhoodRoadInference.Cached(4, 64));

            var first = NeighbourhoodRoadInference.ForBitmap(
                4, 64, bitmap, NoLots(), out var firstReason);
            var second = NeighbourhoodRoadInference.ForBitmap(
                4, 64, null, NoLots(), out var secondReason);

            //the second ask is the cached array itself, not a rebuild of it
            Assert.Same(first, second);
            Assert.Equal(firstReason, secondReason);

            Assert.Same(first, NeighbourhoodRoadInference.Cached(4, 64));
            Assert.Equal(firstReason, NeighbourhoodRoadInference.CachedReason(4, 64));

            NeighbourhoodRoadInference.Forget(4, 64);
            Assert.Null(NeighbourhoodRoadInference.Cached(4, 64));
        }

        [Fact]
        public void ABoundSlotGetsItsModesMaskAndOnlyAtThatMapSize()
        {
            // The seam the cache cannot do by itself: the reader of a neighbourhood knows the mode,
            // the generator of a map knows the slot, and neither knows the other. The map-size check
            // matters because a mask is only meaningful in the tile space it was built for.
            var mask = new bool[64 * 64];
            mask[10 * 64 + 10] = true;

            NeighbourhoodRoadInference.Remember(4, 64, mask, "a test's mask");
            NeighbourhoodRoadInference.Bind(3, 4, 64);

            Assert.Same(mask, NeighbourhoodRoadInference.CachedForSlot(3, 64));
            Assert.Equal("a test's mask", NeighbourhoodRoadInference.ReasonForSlot(3, 64));
            //a different map size is a different grid, so this slot has nothing for it
            Assert.Null(NeighbourhoodRoadInference.CachedForSlot(3, 128));
            //and an unbound slot has nothing either
            Assert.Null(NeighbourhoodRoadInference.CachedForSlot(4, 64));

            //forgetting a mode takes its bindings with it, or a stale mask would outlive the file it
            //came from
            NeighbourhoodRoadInference.Forget(4, 64);
            Assert.Null(NeighbourhoodRoadInference.CachedForSlot(3, 64));
        }

        // ---- helpers ---------------------------------------------------------------------------

        /// <summary>
        /// The 64-tile mask index a backdrop pixel falls in, through the one transform - so a test
        /// that knows a pixel is road does not have to know the transform's arithmetic to ask which
        /// tile that is.
        /// </summary>
        private static int TileAt(int pixelX, int pixelY)
        {
            const int mapSize = 64;
            var tile = NeighbourhoodPixelTransform.TileForPixel(
                new Microsoft.Xna.Framework.Vector2(pixelX, pixelY), mapSize);
            return (int)Math.Floor(tile.Y) * mapSize + (int)Math.Floor(tile.X);
        }

        private static IReadOnlyList<NeighbourhoodLot> NoLots()
        {
            return new NeighbourhoodLot[0];
        }

        /// <summary>Two lots with a wide gap between them, the shape the fallback reads.</summary>
        private static IReadOnlyList<NeighbourhoodLot> LatticeLots()
        {
            return new List<NeighbourhoodLot>
            {
                Lot(21, 8, 8, 16),
                Lot(22, 40, 8, 16),
                Lot(23, 8, 40, 16),
            };
        }

        private static NeighbourhoodLot Lot(short number, int x, int y, int size)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Size = size,
                WorldX = x,
                WorldY = y,
            };
        }

        private static int Count(bool[] mask)
        {
            var n = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i]) n++;
            }
            return n;
        }

        private static readonly int[] Grass = { 70, 130, 60 };
        private static readonly int[] Tarmac = { 128, 128, 128 };

        /// <summary>
        /// An 800x600 BGRA bitmap filled with one flat colour, which is the shape every test here
        /// starts from and which is also the shape a mis-decoded palette produces.
        /// </summary>
        private static TexBitmap Bitmap(int[] fill)
        {
            var data = new byte[Width * Height * 4];
            for (int i = 0; i < Width * Height; i++)
            {
                //BGRA, as ITextureRef.GetImage documents it
                data[i * 4 + 0] = (byte)fill[2];
                data[i * 4 + 1] = (byte)fill[1];
                data[i * 4 + 2] = (byte)fill[0];
                data[i * 4 + 3] = 255;
            }
            return new TexBitmap { Data = data, Width = Width, Height = Height, PixelSize = 4 };
        }

        /// <summary>
        /// Three avenues across the bitmap and three streets down it, in one colour: about a sixth
        /// of the area, which is roughly what a real neighbourhood's road network occupies and
        /// comfortably clears the classification's own "this found nothing" guard.
        /// </summary>
        /// <remarks>
        /// The positions are chosen so a whole map tile falls inside a road - 400..419 in x is
        /// exactly tile 32's pixel range at 64 tiles across, and 300..319 is tile 32's range in y -
        /// which is what lets a test assert on a named tile rather than only on a count.
        /// </remarks>
        private static void LayStreetGrid(TexBitmap bitmap, int r, int g, int b)
        {
            foreach (var x in new[] { 200, 400, 600 })
            {
                Lay(bitmap, r, g, b, x, x + 20, 0, Height);
            }
            foreach (var y in new[] { 100, 300, 500 })
            {
                Lay(bitmap, r, g, b, 0, Width, y, y + 20);
            }
        }

        private static void Lay(TexBitmap bitmap, int r, int g, int b,
            int x0, int x1, int y0, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++) Set(bitmap, x, y, r, g, b);
            }
        }

        private static void Set(TexBitmap bitmap, int x, int y, int r, int g, int b)
        {
            var at = (y * bitmap.Width + x) * bitmap.PixelSize;
            bitmap.Data[at + 0] = (byte)b;
            bitmap.Data[at + 1] = (byte)g;
            bitmap.Data[at + 2] = (byte)r;
            bitmap.Data[at + 3] = 255;
        }
    }
}
