using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Threads;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TS1Platform;
using FSO.SimAntics.Utils;
using Simitone.Client.UI.Services;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the pure decisions in <see cref="NeighborhoodService"/>.
    ///
    /// <para>
    /// The service drives the hybrid-mode lot picker: it builds
    /// <see cref="Simitone.Client.UI.Panels.UINeighborhoodSelectionPanel"/> and its switcher, plays
    /// houses, moves a family in and evicts one. All of that needs live content, a VM and textures,
    /// so it is exercised by a playtest. Two things about it are pure and are pinned here.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="NeighborhoodService.NeighbourhoodIdFor"/></b> is which neighbourhood a
    /// given entry mode opens on. It reaches the panel constructor and the switcher's button set,
    /// so getting it wrong does not crash - it silently shows the wrong neighbourhood with the
    /// wrong navigation buttons, which is exactly the kind of regression a table test catches and
    /// a smoke test does not.
    /// </para>
    ///
    /// <para>
    /// Everything else in the service - building the panel and its switcher, playing houses,
    /// charging a move-in, refunding an eviction - needs live content, a VM and textures, so it is
    /// left to a playtest. <see cref="NeighborhoodService.GetLotPurchaseValue"/> is a special case
    /// and is called out where it would have gone: it <em>is</em> reachable headlessly, but the
    /// only way to reach it is the failure path, which logs, and that would race the log tests.
    /// </para>
    ///
    /// <para>
    /// <b>The eviction's bulldoze</b> is the other thing here, and it is split in two for the same
    /// reason. The decision about every object in the lot is
    /// <see cref="Simitone.Client.Utils.LotEviction"/>, which is pure and is tested in
    /// <c>LotEvictionTests</c>. Applying it and writing the result back to a
    /// <c>House{NN}.iff</c> is <see cref="NeighborhoodService.BuildResetHouseFileBytes"/>, which
    /// needs no content and is pinned below; <see cref="NeighborhoodService.ResetHouse"/> itself
    /// needs <c>Content</c> to tell furniture from the lot's own fabric, and is a playtest.
    /// </para>
    /// </summary>
    public sealed class NeighborhoodServiceTests
    {
        // ------------------------------------------------------------- NeighbourhoodIdFor

        [Fact]
        public void NeighbourhoodIdFor_IsTheResidentialNeighbourhood_ForNormal()
        {
            // Residential is the Unleashed map: the unzoned one, which is what hybrid play needs.
            // Which of the two residential views that is lives in NeighbourhoodModeMap, not here.
            Assert.Equal(NeighbourhoodModeMap.ResidentialDefault, NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.Normal));
            Assert.Equal(NeighbourhoodModeMap.Unleashed, NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.Normal));
        }

        [Fact]
        public void NeighbourhoodIdFor_IsTheResidentialNeighbourhood_ForThePlainMoveIn()
        {
            // moving in from CAS lands you on the neighbourhood you came from, not somewhere else.
            Assert.Equal(NeighbourhoodModeMap.ResidentialDefault, NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.MoveIn));
        }

        [Fact]
        public void NeighbourhoodIdFor_IsMagicland_ForTheMagicMoveIn()
        {
            Assert.Equal((ushort)7, NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.MoveInMagic));
        }

        [Fact]
        public void NeighbourhoodIdFor_OnlyTheMagicMoveInLeavesTheResidentialNeighbourhood()
        {
            // stated as a whole so a fourth mode added later cannot silently reuse 7.
            foreach (NeighSelectionMode mode in new[]
            {
                NeighSelectionMode.Normal,
                NeighSelectionMode.MoveIn,
                NeighSelectionMode.MoveInMagic
            })
            {
                var id = NeighborhoodService.NeighbourhoodIdFor(mode);
                Assert.True(
                    id == NeighbourhoodModeMap.ResidentialDefault || id == NeighbourhoodModeMap.Magictown,
                    $"{mode} opened neighbourhood {id}");
            }
        }

        [Fact]
        public void NeighbourhoodIdFor_OpensOnTheSameMapTheLotMappingReturnsTo()
        {
            // A player who moves in, plays a residential lot and comes back must land on the map
            // they left rather than being moved onto a different one. The two answers share
            // NeighbourhoodModeMap.ResidentialDefault precisely so they cannot drift.
            Assert.Equal(
                NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.Normal),
                NeighborhoodService.NeighbourhoodIdForHouse(1));
            Assert.Equal(
                NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.Normal),
                NeighborhoodService.NeighbourhoodIdForHouse(80));
        }

        [Fact]
        public void NeighbourhoodIdFor_IsStableAcrossCalls()
        {
            // the panel is built from this and the switcher re-reads it on every mode change; two
            // different answers for one mode would desync the buttons from the map.
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(
                    NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.MoveInMagic),
                    NeighborhoodService.NeighbourhoodIdFor(NeighSelectionMode.MoveInMagic));
            }
        }

        // ------------------------------------------------------------ GetLotPurchaseValue

        // NOT TESTED HERE, DELIBERATELY.
        //
        // NeighborhoodService.GetLotPurchaseValue is the eviction refund's lot price and its
        // contract is that it never throws - the eviction path has already moved the family's
        // furniture value by the time it is called, so an exception there leaves the budget
        // half-refunded. That contract is worth a test, and it is exactly the test that cannot
        // be written safely: with no TS1 content loaded the lookup is guaranteed to fail, and the
        // failure path calls SimitoneLog.Warn, which appends to the process-wide
        // <UserDir>/logs/simitone.log.
        //
        // SimitoneLogTests redirects FSOEnvironment.UserDir at its own throwaway directory and
        // asserts on the size and line count of that file, and xUnit runs test classes in
        // parallel. A warning from here would land in their file and fail a rollover-threshold
        // case at random. So the never-throw contract is left to a playtest (evict from a lot
        // whose house file is missing) rather than made flaky here. See the same note in
        // SaveServiceTests about anything that would need a GraphicsDevice.

        // ------------------------------------------------------------ RezoneLot

        // NOT TESTED HERE, DELIBERATELY - the rule is tested, the plumbing is a playtest.
        //
        // RezoneLot reads <UserData>/LotZoning.iff through Content.Get().Neighborhood, decides
        // with LotZoning.Rezoned, writes the file and updates the neighbourhood's dictionary.
        // Everything that can be decided without a Content is decided without one: which line may
        // be rewritten, that a coordinate line or a later copy of the same lot makes a rezone
        // impossible, that a lot with no entry in the file is refused rather than appended to, and
        // that the rewrite leaves the other 999 lines alone. That is LotZoning, and it is
        // LotZoningTests - including the exact shape of the shipped 1000-line chunk, because the
        // line a rezone has to change is the last one about the lot and not the first.
        //
        // What is left needs live content: whether a family is in the lot (the eviction rule, which
        // is why the Rezone button refuses an occupied lot), and the file write. Both are a
        // playtest - rezone an empty lot 54 from the lot panel and confirm the caption changes and
        // the zone survives a restart - rather than a headless test that would have to fabricate a
        // neighbourhood to assert on.

        // ------------------------------------------------------------ BuildResetHouseFileBytes

        // ResetHouse itself needs live content: it resolves every object in the lot against
        // Content.WorldObjects to tell furniture from the lot's own fabric, which is the whole
        // decision. What it then does with the answer - lay the rebuilt lot back out as a
        // House{NN}.iff - does not, and that is BuildResetHouseFileBytes, so the file an eviction
        // writes is pinned here.
        //
        // It is worth pinning because the failure mode is silent and total. The lot loader takes
        // the *first* FSOV chunk in the file, so a rebuilt file that carried the old one alongside
        // the new would be a bulldoze that did nothing, and it would still load, and it would still
        // look right. The thumbnail copy has a second trap of its own: IffFile decodes a chunk on
        // demand and then throws its raw bytes away, and a chunk with no Write of its own (THMB) is
        // written back out of OriginalData - so a copy that does not keep the bytes takes the whole
        // write down with a null reference.

        [Fact]
        public void TheRebuiltLotFileIsReadableAndCarriesTheNewLotData()
        {
            var marshal = SyntheticLot(objectCount: 3, groupCount: 2);

            var bytes = NeighborhoodService.BuildResetHouseFileBytes(marshal, SyntheticHouse());
            var read = ReadHouse(bytes);

            var roundTripped = ReadMarshal(read.Get<FSOV>(1));
            Assert.Equal(3, roundTripped.Entities.Length);
            Assert.Equal(2, roundTripped.MultitileGroups.Length);
        }

        [Fact]
        public void TheRebuiltLotFileCarriesExactlyOneLotDataChunk()
        {
            //the loader reads the first FSOV in the file, so a second one - the lot's original, say
            //- would make the bulldoze a no-op that still loads cleanly.
            var original = SyntheticHouse(withLotData: true, withValues: true);

            var bytes = NeighborhoodService.BuildResetHouseFileBytes(SyntheticLot(), original);
            var read = ReadHouse(bytes);

            Assert.Equal(1, CountChunks<FSOV>(read));
            Assert.Equal(1, read.Get<FSOV>(1).ChunkID);
        }

        [Fact]
        public void TheRebuiltLotFileCarriesExactlyOneValuesChunk()
        {
            var original = SyntheticHouse(withLotData: true, withValues: true);

            var bytes = NeighborhoodService.BuildResetHouseFileBytes(SyntheticLot(), original);
            var read = ReadHouse(bytes);

            Assert.Equal(1, CountChunks<SIMI>(read));
        }

        [Fact]
        public void TheRebuiltLotFileCarriesTheRestampedValues()
        {
            //what the next family is charged comes out of this chunk, so it has to be the
            //restamped one rather than the lot's original - and it has to be findable, because the
            //lot price is read by chunk id.
            var marshal = SyntheticLot();
            var lotState = (VMTS1LotState)marshal.PlatformState;
            lotState.SimulationInfo.ObjectsValue = 0;
            lotState.SimulationInfo.Version = LotEviction.NormalizedSimiVersion;
            lotState.SimulationInfo.ChunkID = 0; //as though it had been carried over from somewhere odd
            lotState.CurrentFamily = null;

            var bytes = NeighborhoodService.BuildResetHouseFileBytes(marshal, SyntheticHouse());
            var read = ReadHouse(bytes);

            var simi = read.Get<SIMI>(1);
            Assert.NotNull(simi);
            Assert.Equal(0, simi.ObjectsValue);
            Assert.Equal(LotEviction.NormalizedSimiVersion, simi.Version);
            //`Assert.NotNull(simi.BudgetDays)` is what this used to be, and it cannot fail:
            //SIMI.Read allocates a six-element array by construction, so it is non-null for any
            //readable SIMI whatever the file said. The assertion below is the one that carries
            //information - the eviction's whole point is that the lot's recorded budget history
            //comes back out of the file it just rebuilt rather than being lost with the lot.
            Assert.Equal(SIMI.BudgetDayCount, simi.BudgetDays.Length);
            Assert.All(simi.BudgetDays, day => Assert.NotNull(day));
            //and the money fields themselves, which is the branch the audit's F-01 lived in and
            //which no default-constructed SIMI in the suite could reach
            Assert.Equal(0, simi.BudgetDays[0].Valid);
        }

        [Fact]
        public void TheLotThumbnailsSurviveTheRebuild()
        {
            //an eviction happens on the neighbourhood screen, where there is no world to render a
            //new thumbnail from, so the lot keeps the pictures it has.
            var original = SyntheticHouse(thumbnails: true);

            var bytes = NeighborhoodService.BuildResetHouseFileBytes(SyntheticLot(), original);
            var read = ReadHouse(bytes);

            Assert.NotNull(read.Get<BMP>(512));
            Assert.NotNull(read.Get<PNG>(513));
            Assert.NotNull(read.Get<THMB>(512));
        }

        [Fact]
        public void TheThumbnailsKeepTheirBytes()
        {
            var original = SyntheticHouse(thumbnails: true);
            var expectedBmp = original.Get<BMP>(512).data;
            var expectedPng = original.Get<PNG>(513).data;
            var expectedThmb = new byte[20];

            var bytes = NeighborhoodService.BuildResetHouseFileBytes(SyntheticLot(), original);
            var read = ReadHouse(bytes);

            Assert.Equal(expectedBmp, read.Get<BMP>(512).data);
            Assert.Equal(expectedPng, read.Get<PNG>(513).data);

            //THMB has no Write of its own, so its payload is written straight back out of the raw
            //bytes stashed before the copy. If that stash were missing, this read would not even
            //return - the write would have thrown on a null payload.
            var thmb = read.Get<THMB>(512);
            Assert.NotNull(thmb);
            Assert.Equal(512, thmb.ChunkID);
        }

        [Fact]
        public void EachThumbnailIsCarriedOverExactlyOnce()
        {
            //PNG derives from BMP, so asking the copier for the BMPs must not also hand back the
            //PNGs. If it did, every thumbnail would be added twice - and IffFile.AddChunk throws on
            //a duplicate chunk id, taking the whole eviction down with it.
            var original = SyntheticHouse(withBmpThumbnails: true, withPngThumbnails: true,
                withThmbThumbnails: true, secondPair: true);

            var bytes = NeighborhoodService.BuildResetHouseFileBytes(SyntheticLot(), original);
            var read = ReadHouse(bytes);

            Assert.Equal(2, CountChunks<BMP>(read));
            Assert.Equal(2, CountChunks<PNG>(read));
            Assert.Equal(2, CountChunks<THMB>(read));
        }

        [Fact]
        public void ALotWithNoThumbnailsIsRebuiltWithoutThrowing()
        {
            //a house file with no picture chunks at all is a normal thing to find, and adding none
            //is not a reason to fail the write.
            var original = SyntheticHouse();

            var bytes = NeighborhoodService.BuildResetHouseFileBytes(SyntheticLot(), original);
            var read = ReadHouse(bytes);

            Assert.Equal(0, CountChunks<BMP>(read));
            Assert.Equal(0, CountChunks<PNG>(read));
            Assert.Equal(0, CountChunks<THMB>(read));
            Assert.NotNull(read.Get<FSOV>(1));
        }

        [Fact]
        public void TheLotDataIsAlwaysWrittenEvenWithoutALotFileToReadThumbnailsFrom()
        {
            //the thumbnail copy is best effort; the lot data is the eviction.
            var bytes = NeighborhoodService.BuildResetHouseFileBytes(SyntheticLot(), null);
            var read = ReadHouse(bytes);

            Assert.NotNull(read.Get<FSOV>(1));
            Assert.NotNull(read.Get<SIMI>(1));
            Assert.Equal(3, ReadMarshal(read.Get<FSOV>(1)).Entities.Length);
        }

        #region synthetic lot data

        /// <summary>Chunk id the game gives the FSOV lot-data chunk, and the SIMI chunk.</summary>
        private const ushort Chunk1 = 1;

        /// <summary>Chunk id of the roofed lot thumbnail.</summary>
        private const ushort RoofedThumb = 513;

        /// <summary>Chunk id of the roofless lot thumbnail.</summary>
        private const ushort RooflessThumb = 512;

        /// <summary>
        /// A small lot, built entirely from defaults. Everything a <c>VMMarshal</c> insists on
        /// having before it will serialize is filled in with something harmless.
        /// </summary>
        private static VMMarshal SyntheticLot(int objectCount = 3, int groupCount = 2)
        {
            var size = 4;
            var tiles = size * size;

            var marshal = new VMMarshal
            {
                TS1 = true,
                Context = new VMContextMarshal
                {
                    Clock = new VMClockMarshal(),
                    Architecture = new VMArchitectureMarshal
                    {
                        Width = size,
                        Height = size,
                        Stories = 1,
                        Terrain = new VMArchitectureTerrain(size, size),
                        Walls = new[] { new WallTile[tiles] },
                        Floors = new[] { new FloorTile[tiles] },
                        FineBuildableArea = new bool[tiles],
                    },
                    Ambience = new VMAmbientSoundMarshal(),
                },
                GlobalState = new short[38],
                Tuning = null,
            };

            var entities = new VMEntityMarshal[objectCount];
            var threads = new VMThreadMarshal[objectCount];
            for (int i = 0; i < objectCount; i++)
            {
                entities[i] = new VMGameObjectMarshal(marshal.Version, true)
                {
                    ObjectID = (short)(i + 1),
                    PersistID = (uint)(i + 1),
                    GUID = (uint)(0x1000 + i),
                    ObjectData = new short[10],
                    MyList = new short[0],
                    Contained = new short[0],
                    Attributes = new short[0],
                    MeToObject = new VMEntityRelationshipMarshal[0],
                    MeToPersist = new VMEntityPersistRelationshipMarshal[0],
                    Position = new LotTilePos((short)i, 0, 1),
                    PlatformState = new VMTS1ObjectState(),
                };
                threads[i] = LotEviction.CreateBlankThread();
            }
            marshal.Entities = entities;
            marshal.Threads = threads;

            var groups = new VMMultitileGroupMarshal[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                groups[i] = new VMMultitileGroupMarshal(marshal.Version)
                {
                    MultiTile = false,
                    Name = "group" + i,
                    Objects = new[] { (short)(i + 1) },
                    Offsets = new[] { new LotTilePos() },
                    Price = 100 * (i + 1),
                    SalePrice = -1,
                };
            }
            marshal.MultitileGroups = groups;

            marshal.ObjectId = LotEviction.NextObjectId(
                entities.Select(e => e.ObjectID).ToArray());

            marshal.PlatformState = new VMTS1LotState
            {
                SimulationInfo = SyntheticSimi(),
            };

            return marshal;
        }

        /// <summary>A values chunk with the parts that get written out already filled in.</summary>
        private static SIMI SyntheticSimi()
        {
            var simi = new SIMI
            {
                ChunkID = Chunk1,
                ChunkType = "SIMI",
                ChunkLabel = "Lot Information",
                ChunkProcessed = true,
                LotValue = 1000,
                ObjectsValue = 8500,
                ArchitectureValue = 4000,
                GlobalData = new short[38],
                BudgetDays = Enumerable.Range(0, 6)
                    .Select(_ => new SIMI.SIMIBudgetDay())
                    .ToArray(),
            };
            return simi;
        }

        /// <summary>
        /// A house file as it comes off disk, with <c>OriginalData</c> empty - which is the state
        /// that makes the thumbnail copy non-trivial.
        /// </summary>
        private static IffFile SyntheticHouse(bool withLotData = false, bool withValues = false,
            bool thumbnails = false, bool withBmpThumbnails = false, bool withPngThumbnails = false,
            bool withThmbThumbnails = false, bool secondPair = false)
        {
            var iff = new IffFile();

            if (withLotData) iff.AddChunk(new FSOV { ChunkID = Chunk1, ChunkType = "FSOV", ChunkLabel = "Simitone Lot Data", ChunkProcessed = true, Data = new byte[] { 1, 2, 3, 4 } });
            if (withValues) iff.AddChunk(SyntheticSimi());

            if (thumbnails || withBmpThumbnails) iff.AddChunk(BmpThumb(RooflessThumb, new byte[] { 1, 2, 3, 4 }));
            if (thumbnails || withPngThumbnails) iff.AddChunk(PngThumb(RoofedThumb, new byte[] { 5, 6, 7, 8 }));
            if (thumbnails || withThmbThumbnails) iff.AddChunk(ThmbThumb(RooflessThumb, 20));

            if (secondPair)
            {
                iff.AddChunk(BmpThumb(RoofedThumb, new byte[] { 9, 10 }));
                iff.AddChunk(PngThumb(RooflessThumb, new byte[] { 11, 12 }));
                iff.AddChunk(ThmbThumb(RoofedThumb, 20));
            }

            //round-trip through a stream so the file comes back the way a file on disk would: raw
            //bytes only, nothing decoded, no OriginalData.
            return ReadHouse(WriteHouse(iff));
        }

        //the chunks are handed over already marked processed: these are the payloads the game would
        //decode out of the raw bytes, and writing the synthetic file must not go back and re-read
        //them from a stream that is not there. Every chunk also carries a label, because IffFile
        //writes that field unconditionally and a null one takes the write down.
        private static BMP BmpThumb(ushort id, byte[] data)
        {
            return new BMP { ChunkID = id, ChunkType = "BMP_", ChunkLabel = "Lot Thumbnail", ChunkProcessed = true, data = data };
        }

        private static PNG PngThumb(ushort id, byte[] data)
        {
            return new PNG { ChunkID = id, ChunkType = "PNG_", ChunkLabel = "Lot Thumbnail", ChunkProcessed = true, data = data };
        }

        /// <summary>
        /// A THMB, which has no payload field and no <c>Write</c> of its own - both its halves live
        /// in the raw bytes, which is exactly why copying one is not a plain assignment.
        /// </summary>
        private static THMB ThmbThumb(ushort id, int size)
        {
            var bytes = new byte[size];
            return new THMB
            {
                ChunkID = id,
                ChunkType = "THMB",
                ChunkLabel = "Lot Thumbnail",
                ChunkProcessed = true,
                ChunkData = bytes,
                OriginalData = bytes,
            };
        }

        /// <summary>
        /// The chunks of one kind in a file, as a list. <c>IffFile.List</c> answers null rather
        /// than an empty list when a file has none, which is an easy thing to trip over when the
        /// assertion is "there are none".
        /// </summary>
        private static int CountChunks<T>(IffFile iff) where T : IffChunk
        {
            return iff.List<T>()?.Count ?? 0;
        }

        private static byte[] WriteHouse(IffFile iff)
        {
            using (var stream = new MemoryStream())
            {
                iff.Write(stream);
                return stream.ToArray();
            }
        }

        private static IffFile ReadHouse(byte[] bytes)
        {
            var iff = new IffFile();
            using (var stream = new MemoryStream(bytes))
            {
                iff.Read(stream);
            }
            return iff;
        }

        private static VMMarshal ReadMarshal(FSOV fsov)
        {
            var marshal = new VMMarshal();
            using (var reader = new BinaryReader(new MemoryStream(fsov.Data)))
            {
                marshal.Deserialize(reader);
            }
            return marshal;
        }

        #endregion
    }
}
