/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Content.Model;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Utils;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The two things a facade build has to decide before it stands a throwaway lot up: whether
    /// the house file is one the lot view can be sized from, and - on a later visit - whether a
    /// mesh built last time is still the right mesh for the lot as the layout describes it now.
    ///
    /// <para><b>Both are free checks that used to be five seconds of crash.</b> The first is a
    /// lot size read out of an untrusted house file, which the lot view turns into the size of a
    /// render target without a bound; the second is the difference between a second visit costing
    /// nothing and costing the same eleven seconds all over again.</para>
    ///
    /// <para><b>Serialised</b>, because <c>NeighbourhoodFacadeMeshCache</c> is a process-wide
    /// static that these tests empty. A test that clears it while another class is asserting on
    /// what it adopted is a race between classes rather than a property of the cache.</para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public class NeighbourhoodFacadeFailFastTests
    {
        private static NeighbourhoodLot Lot(short number, int worldX, int worldY,
            uint hash = 0xABCD, int size = 32, int rotation = 0, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                WorldX = worldX,
                WorldY = worldY,
                Rotation = rotation,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = hash,
                HasHouse = hasHouse
            };
        }

        /// <summary>A house file whose <c>SIMI</c> globals are <paramref name="globals"/> long.</summary>
        private static IffFile HouseWithGlobals(short[] globals)
        {
            var simi = new SIMI
            {
                ChunkID = 1,
                ChunkType = "simi",
                GlobalData = globals
            };
            var house = new IffFile();
            house.AddChunk(simi);
            return house;
        }

        /// <summary>Globals long enough for the loader, with the lot size written at index 23.</summary>
        private static short[] GlobalsWithLotSize(short lotSize)
        {
            var globals = new short[NeighbourhoodFacadeBuilder.MinimumSimulationGlobals];
            globals[NeighbourhoodFacadeBuilder.LotSizeGlobalIndex] = lotSize;
            globals[35] = 7;
            return globals;
        }

        // ---- the lot size, which is the E_INVALIDARG ----------------------------------------

        [Fact]
        public void TheLotSizeIsReadFromTheGlobalTheActivatorReadsItFrom()
        {
            // VMTS1Activator.LoadFromIff's third line is simi.GlobalData[23], and that number
            // becomes the width of the lot view's light map. Pinning the index rather than the
            // value is the point: if the activator ever moved the word, this fails here rather
            // than the check silently reading a game's expansion-pack bit.
            Assert.Equal(23, NeighbourhoodFacadeBuilder.LotSizeGlobalIndex);
            Assert.Equal(64, NeighbourhoodFacadeBuilder.LotSizeFor(
                HouseWithGlobals(GlobalsWithLotSize(64))));
        }

        [Fact]
        public void AFileThatCannotBeAskedIsZeroRatherThanAnException()
        {
            // The pre-flight runs on the failure path as well, and a house file that will not
            // parse must not turn "cannot be drawn" into a second exception.
            Assert.Equal(0, NeighbourhoodFacadeBuilder.LotSizeFor(null));
            Assert.Equal(0, NeighbourhoodFacadeBuilder.LotSizeFor(new IffFile()));
            //a SIMI chunk whose globals array is shorter than the word the activator indexes
            Assert.Equal(0, NeighbourhoodFacadeBuilder.LotSizeFor(HouseWithGlobals(new short[4])));
            Assert.Equal(0, NeighbourhoodFacadeBuilder.LotSizeFor(HouseWithGlobals(null)));
        }

        [Fact]
        public void ALotTheLotViewCannotBeSizedForIsRefusedBeforeTheWorldExists()
        {
            // The whole of the E_INVALIDARG fix. LMapBatch.Init sizes its render targets as
            // resPerTile * (size - borderSize) with a borderSize of 1, so a house file claiming
            // one tile asks D3D for a zero-pixel-wide texture and is answered with
            // SharpDXException: E_INVALIDARG - after a World, a VM and a whole house file load
            // have already been paid for. Two lots in a forty-one lot neighbourhood were dying
            // of exactly that.
            foreach (short size in new short[] { 0, 1, -1, -64 })
            {
                var reason = NeighbourhoodFacadeBuilder.HouseFileRefusal(
                    HouseWithGlobals(GlobalsWithLotSize(size)), 12);

                Assert.NotNull(reason);
                Assert.Contains("12", reason);
                Assert.Contains("E_INVALIDARG", reason);
                Assert.Contains("render target", reason);
                Assert.Contains(size.ToString(), reason);
            }
        }

        [Fact]
        public void ALotTooLargeForADeviceToAllocateIsRefusedToo()
        {
            // The same arithmetic at the other end, and the same class of failure: a hand-edited
            // SIMI claiming four thousand tiles asks for a light map of 16 * 3999 * 3 pixels
            // across, which no device will hand out and which fails identically.
            var reason = NeighbourhoodFacadeBuilder.HouseFileRefusal(
                HouseWithGlobals(GlobalsWithLotSize(4000)), 7);

            Assert.NotNull(reason);
            Assert.Contains("4000", reason);
            Assert.Contains("E_INVALIDARG", reason);
        }

        [Fact]
        public void ARetailSizedLotIsNotRefused()
        {
            // Every genuine TS1 house file is 64 tiles, and the bound is a factor of eight either
            // side of that - so a file this refuses is a file this cannot draw rather than a file
            // this is being awkward about.
            foreach (short size in new short[]
            {
                NeighbourhoodFacadeBuilder.MinimumLotSize,
                16, 32, 64, 128,
                (short)NeighbourhoodFacadeBuilder.MaximumLotSize
            })
            {
                Assert.True(NeighbourhoodFacadeBuilder.LotSizeIsUsable(size), size + " should be usable");
                Assert.Null(NeighbourhoodFacadeBuilder.HouseFileRefusal(
                    HouseWithGlobals(GlobalsWithLotSize(size)), 12));
            }
        }

        [Fact]
        public void TheBoundsAreDerivedRatherThanGuessed()
        {
            // Two is the smallest size that leaves the lot view a pixel to work with, and 512 is
            // generous past the largest size any TS1 content uses. Both are pinned so a later
            // change to either is a visible decision rather than a drift.
            Assert.Equal(2, NeighbourhoodFacadeBuilder.MinimumLotSize);
            Assert.Equal(512, NeighbourhoodFacadeBuilder.MaximumLotSize);
            Assert.False(NeighbourhoodFacadeBuilder.LotSizeIsUsable(
                NeighbourhoodFacadeBuilder.MinimumLotSize - 1));
            Assert.False(NeighbourhoodFacadeBuilder.LotSizeIsUsable(
                NeighbourhoodFacadeBuilder.MaximumLotSize + 1));
        }

        [Fact]
        public void TheActivatorClampsToTheSameRangeTheBuilderRefuses()
        {
            // The pre-flight above is free, so the facade path still refuses a house file whose lot
            // size the lot view cannot be sized from rather than quietly loading a 2x2 version of
            // a lot that is really 64x64. But it is not the only caller: the OBJ exporter stands a
            // throwaway lot per lot and reads its wall and floor chunks, and it does not pre-flight
            // anything. So the activator clamps the same word to the same range at the source,
            // where both callers get it, and the two sets of bounds cannot disagree.
            Assert.Equal(23, VMTS1Activator.LotSizeGlobalIndex);
            Assert.Equal(NeighbourhoodFacadeBuilder.LotSizeGlobalIndex,
                VMTS1Activator.LotSizeGlobalIndex);
            Assert.Equal(NeighbourhoodFacadeBuilder.MinimumLotSize, VMTS1Activator.MinimumLotSize);
            Assert.Equal(NeighbourhoodFacadeBuilder.MaximumLotSize, VMTS1Activator.MaximumLotSize);
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 2)]
        [InlineData(-1, 2)]
        [InlineData(-64, 2)]
        [InlineData(2, 2)]
        [InlineData(64, 64)]
        [InlineData(512, 512)]
        [InlineData(4000, 512)]
        public void NoDeclaredLotSizeCanAskTheDeviceForNothing(int declared, int expected)
        {
            // Every one of these is the E_INVALIDARG. LMapBatch.Init sizes its render targets as
            // resPerTile * (size - borderSize) with a borderSize of 1, so 0 and 1 ask D3D for a
            // zero-pixel-wide texture, and 4000 asks for one past what a device will allocate.
            Assert.Equal(expected, VMTS1Activator.ClampLotSize(declared));
            Assert.Equal(expected,
                VMTS1Activator.ClampLotSize(GlobalsWithLotSize((short)declared), 12));
        }

        [Fact]
        public void ASimulationChunkThisLoaderCannotReadIsTheSmallestLotRatherThanACrash()
        {
            // The globals array is untrusted input, and the word being read is at index 23 of it.
            // A short array, and a missing chunk that reads as a null, both come out as zero - and
            // zero is below the minimum, so it is clamped to it rather than indexing off the end
            // of the array with an IndexOutOfRangeException that names nothing.
            Assert.Equal(VMTS1Activator.MinimumLotSize, VMTS1Activator.ClampLotSize(new short[4], 12));
            Assert.Equal(VMTS1Activator.MinimumLotSize, VMTS1Activator.ClampLotSize(new short[0], 12));
            Assert.Equal(VMTS1Activator.MinimumLotSize, VMTS1Activator.ClampLotSize(null, 12));
        }

        [Fact]
        public void AFileTheChecklistAlreadyNamesIsNotNamedTwice()
        {
            // A house file with no usable SIMI chunk is already reported by Checklist with a
            // better sentence, and this check runs first - so it must stay quiet about those and
            // speak only about the files the checklist accepts and the lot view still cannot draw.
            Assert.Null(NeighbourhoodFacadeBuilder.HouseFileRefusal(null, 12));
            Assert.Null(NeighbourhoodFacadeBuilder.HouseFileRefusal(new IffFile(), 12));
            Assert.Null(NeighbourhoodFacadeBuilder.HouseFileRefusal(
                HouseWithGlobals(new short[4]), 12));
        }

        // ---- the order the two cheap checks run in --------------------------------------------

        [Fact]
        public void TheTwoLotsThatDiedOfEInvalidArgAreRefusedBeforeAnythingElseIsAskedFor()
        {
            // Lots 70 and 73 named a lot size of one tile, and the lot view sizes its light map as
            // (size - 1) pixels across - so the whole World, the whole VM and the whole house file
            // were loaded, and then D3D refused a zero-pixel render target with E_INVALIDARG. The
            // refusal costs a pair of array reads on a file already in memory, and it has to be
            // asked for before the checks that need a graphics device at all: here there is no
            // device, no scene layer and no content manager, and the lot size is still what comes
            // back, which is only true if it is asked for first.
            foreach (short lot in new short[] { 70, 73 })
            {
                foreach (short size in new short[] { 0, 1, -1 })
                {
                    var reason = NeighbourhoodFacadeBuilder.PreFlight(
                        null, null, null, HouseWithGlobals(GlobalsWithLotSize(size)), lot);

                    Assert.NotNull(reason);
                    Assert.Contains(lot.ToString(), reason);
                    Assert.Contains("E_INVALIDARG", reason);
                    //and it is the lot size that gets named, not any of the three things the
                    //checklist would have gone on to complain about
                    Assert.DoesNotContain("there is no graphics device", reason);
                    Assert.DoesNotContain("3D scene layer", reason);
                    Assert.DoesNotContain("content manager", reason);
                }
            }
        }

        [Fact]
        public void AFileThatIsWorthLoadingIsRefusedForWhatIsActuallyMissing()
        {
            // The other end of the same order, and the reason it is worth having: once the file
            // passes the free check, the next thing to be asked for is the device, because that is
            // the next thing that would have been dereferenced. A lot whose size is fine and whose
            // device is not is a lot that has to be refused, and this is what it is refused for.
            var reason = NeighbourhoodFacadeBuilder.PreFlight(
                null, null, null, HouseWithGlobals(GlobalsWithLotSize(64)), 12);

            Assert.NotNull(reason);
            Assert.Contains("there is no graphics device", reason);
            Assert.DoesNotContain("E_INVALIDARG", reason);
        }

        [Fact]
        public void AFileWithNoSimulationChunkIsNotBlamedForItsLotSize()
        {
            // The lot size check runs first and is deliberately quiet about a file it cannot read
            // at all, because the checklist has a better sentence for one - and the checklist only
            // gets to use it if the lot size check stands aside. Asserted on the check itself, and
            // on the order: a file with no SIMI is a house-file problem, not a lot-size problem,
            // and saying otherwise would send the next person to look for a graphics driver.
            Assert.Null(NeighbourhoodFacadeBuilder.HouseFileRefusal(
                HouseWithGlobals(new short[4]), 12));
            Assert.Equal(0, NeighbourhoodFacadeBuilder.LotSizeFor(HouseWithGlobals(new short[4])));
        }

        [Fact]
        public void TheRefusalIsNamedOnTheLogRatherThanThrown()
        {
            // Every sentence the pass can produce has to be readable without the code, because
            // the log is what a player sends. The new piece joins the set every other piece is
            // asserted against.
            var described = NeighbourhoodFacadeDiagnostics.Describe(NeighbourhoodFacadePiece.LotSize);
            Assert.Contains("lot size", described);
            Assert.Contains("render target", described);
            Assert.Contains("E_INVALIDARG", described);

            var sentence = NeighbourhoodFacadeDiagnostics.Sentence(
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.LotSize, false,
                    "the light map is sized as (size - 1) pixels across"), 64);
            Assert.Contains("64", sentence);
            Assert.Contains("lot size", sentence);
        }

        // ---- the cross-visit cache key -------------------------------------------------------

        [Fact]
        public void AStoredMeshIsAdoptedWhenTheHouseFileHasNotMoved()
        {
            // The second visit of a neighbourhood is the one this exists for, and the whole of the
            // key is three numbers: the house file's fingerprint, the lot's own side and its facing.
            var stored = Lot(11, 200, 300, 0xDEADBEEF);
            var current = Lot(11, 200, 300, 0xDEADBEEF);

            Assert.True(NeighbourhoodFacadeMeshCache.Adoptable(stored, current));
        }

        [Fact]
        public void AMeshIsAdoptedForALotThatHasMovedAndThePlotIsReappliedInstead()
        {
            // CHANGED by the Part 9 TSO-alignment rework (RW0). This test used to be
            // AMeshIsNotAdoptedForALotThatHasBeenRezonedOntoAnotherPlot, and it asserted the
            // opposite of this: a plot that moved made the mesh un-adoptable, on the grounds that
            // PlacedVertices bakes world tiles into the vertices and a re-used mesh would draw the
            // house at its old address.
            //
            // That was true and it was the wrong answer. The placement is not part of the render -
            // it is a cheap translation on top of it (NeighbourhoodFacadeMeshCache.Reposition) -
            // and keying the cross-visit cache on it meant that any change to the placement rule,
            // which Part 8 made to every lot at once, threw away a whole neighbourhood's meshes
            // and rebuilt them on the game thread at seconds each. That is the twenty-second
            // freeze this rework exists to remove.
            var stored = Lot(11, 200, 300, 0xDEADBEEF);

            Assert.True(NeighbourhoodFacadeMeshCache.Adoptable(stored, Lot(11, 232, 300, 0xDEADBEEF)));
            Assert.True(NeighbourhoodFacadeMeshCache.Adoptable(stored, Lot(11, 200, 332, 0xDEADBEEF)));
        }

        [Fact]
        public void AMeshIsNotAdoptedForALotThatIsNotTheSameHouse()
        {
            // The two facts a house hash cannot see, and the only ones left in the key: the activator
            // builds a Size x Size blueprint, so a different side is a different house, and the
            // facing is baked into the render too.
            var stored = Lot(11, 200, 300, 0xDEADBEEF);

            Assert.False(NeighbourhoodFacadeMeshCache.Adoptable(stored, Lot(11, 200, 300, 0xDEADBEEF, size: 64)));
            Assert.False(NeighbourhoodFacadeMeshCache.Adoptable(stored, Lot(11, 200, 300, 0xDEADBEEF, rotation: 1)));
        }

        [Fact]
        public void AMeshIsNotAdoptedForALotWhoseHouseFileChanged()
        {
            // The same rule the per-visit cache has always used, and the reason a bulldoze shows
            // up: the file was rewritten, so the hash moved, so the mesh is the wrong one and the
            // lot goes back in the build queue.
            var stored = Lot(11, 200, 300, 0xDEADBEEF);
            var edited = Lot(11, 200, 300, 0x00000001);

            Assert.False(NeighbourhoodFacadeMeshCache.Adoptable(stored, edited));
        }

        [Fact]
        public void NothingIsAdoptedFromNothing()
        {
            // A caller that has no lot - the neighbourhood screen before its layout is packed -
            // must get a refusal rather than a mesh that would then be drawn against nothing.
            Assert.False(NeighbourhoodFacadeMeshCache.Adoptable(null, Lot(11, 0, 0)));
            Assert.False(NeighbourhoodFacadeMeshCache.Adoptable(Lot(11, 0, 0), null));
            Assert.False(NeighbourhoodFacadeMeshCache.Adoptable(null, null));
        }

        [Fact]
        public void TheStoreIgnoresAMeshWithNoLotRatherThanHoldingAnEmptyEntry()
        {
            // A facade with no lot is one the draw loop could never place and the store could
            // never key; storing it would be an entry that costs memory and hands out nothing.
            NeighbourhoodFacadeMeshCache.Clear();
            NeighbourhoodFacadeMeshCache.Store(null);
            NeighbourhoodFacadeMeshCache.Store(new Simitone.Client.Neighbourhood.NeighbourhoodFacade());
            NeighbourhoodFacadeMeshCache.Store(
                new Simitone.Client.Neighbourhood.NeighbourhoodFacade(), 0x1234u);

            Assert.Equal(0, NeighbourhoodFacadeMeshCache.Count);
            Assert.Null(NeighbourhoodFacadeMeshCache.Take(Lot(11, 0, 0)));
            //and a null device is a legitimate thing to be handed by a headless caller
            NeighbourhoodFacadeMeshCache.Watch(null);
            Assert.Equal(0, NeighbourhoodFacadeMeshCache.Count);
        }

        [Fact]
        public void AStoreWithNothingInItAdoptsNothing()
        {
            // The state every session starts in, and the one the "second visit is free" claim
            // rests on being false the first time rather than always.
            NeighbourhoodFacadeMeshCache.Clear();

            Assert.Equal(0, NeighbourhoodFacadeMeshCache.Count);
            Assert.False(NeighbourhoodFacadeMeshCache.Contains(11));
            Assert.Null(NeighbourhoodFacadeMeshCache.Take(Lot(11, 200, 300, 0xDEADBEEF)));
            //and forgetting something that is not there is not an error
            NeighbourhoodFacadeMeshCache.Forget(11);
            Assert.Equal(0, NeighbourhoodFacadeMeshCache.Count);
        }
    }
}
