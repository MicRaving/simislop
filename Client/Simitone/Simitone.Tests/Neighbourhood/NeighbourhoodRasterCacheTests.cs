/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The generated map on disk: that it round-trips exactly, and - the part that matters - that
    /// it is believed only when it is stamped for the world being asked about.
    ///
    /// <para>
    /// The asymmetry is the whole design. A raster that is a bit off in one corner is a
    /// neighbourhood with a hole in it and no way to tell, so every way of being wrong has to end
    /// the same way: no map, and a regeneration. Missing, truncated, scribbled on, written by a
    /// build that did not exist yet, generated from a different seed, a different generator, a
    /// different map size or a different arrangement of the lots - six ways to be wrong, one
    /// answer, and the test names all six.
    /// </para>
    ///
    /// <para>
    /// Pure with respect to the game: a temp directory, canned lot records, and a map small enough
    /// to build by hand rather than a 512x512 generation per assertion.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodRasterCacheTests : IDisposable
    {
        private const int Slot = 3;
        private const int Side = 12;

        private readonly string _root;

        public NeighbourhoodRasterCacheTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-neigh-raster-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                //best effort - a leftover temp dir must never fail the test run
            }
            catch (UnauthorizedAccessException)
            {
                //same: a locked handle on Windows must not turn into a test failure
            }
        }

        // ---- fixtures ----------------------------------------------------------------------------

        /// <summary>
        /// A layout with two lots, placed so the arrangement is distinctive enough that a stamp
        /// built from it cannot collide with one built from anything else in this file by accident.
        /// </summary>
        private static NeighbourhoodLayout Layout(int seed = 4242, int generatorVersion = 7,
            int mapSize = Side, short moveFirstLotBy = 0, int slot = Slot)
        {
            var first = new NeighbourhoodLot
            {
                Number = 21, Zone = LotZoning.ZoneResidential, Size = 3, MapX = 10, MapY = 11,
                WorldX = 2 + moveFirstLotBy, WorldY = 3, Rotation = 1,
                TerrainType = TerrainType.SAND, BaseHeight = 5, DoorSide = NeighbourhoodLotSide.East,
                HouseHash = 0xdeadbeefu, HasHouse = true
            };
            var second = new NeighbourhoodLot
            {
                Number = 22, Zone = LotZoning.ZoneCommunity, Size = 2, MapX = 20, MapY = 21,
                WorldX = 7, WorldY = 8, Rotation = 0,
                TerrainType = TerrainType.SNOW, BaseHeight = 9, DoorSide = NeighbourhoodLotSide.West,
                HouseHash = 0x00000042u, HasHouse = false
            };
            return new NeighbourhoodLayout(slot, seed, generatorVersion,
                NeighbourhoodLayout.CurrentSchemaVersion, mapSize, new[] { first, second });
        }

        /// <summary>
        /// A map with a different value in every one of its six arrays at every tile, including
        /// both colour arrays - a round-trip that only checked terrain would pass a format that
        /// dropped the other five.
        /// </summary>
        private static NeighbourhoodMap BusyMap(int side = Side)
        {
            var map = NeighbourhoodMap.Create(side);
            var count = side * side;
            for (int i = 0; i < count; i++)
            {
                map.TerrainType[i] = (byte)(i % 5);
                map.ElevationData[i] = (byte)((i * 7) & 0xFF);
                //both nibbles vary, so a swapped or squashed road byte cannot survive the round-trip
                map.RoadData[i] = (byte)(((i / 3) % 16) | (((i / 5) % 16) << 4));
                map.ForestDensityData[i] = (byte)((i * 11) & 0xFF);
                map.TerrainTypeColorData[i] = new Color(
                    (byte)(i & 0xFF), (byte)((i >> 3) & 0xFF), (byte)((i >> 5) & 0xFF), (byte)255);
                map.ForestTypeData[i] = new Color(
                    (byte)((i * 3) & 0xFF), (byte)((i * 13) & 0xFF), (byte)((i * 29) & 0xFF), (byte)200);
            }
            return map;
        }

        private static void AssertSameArrays(NeighbourhoodMap expected, NeighbourhoodMap actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            //and the type's own element-by-element comparison, which is the assertion the rest of
            //the codebase makes about determinism
            Assert.True(expected.ContentEquals(actual),
                "the round-tripped map is not element-for-element the map that was written");

            Assert.Equal(expected.TerrainType, actual.TerrainType);
            Assert.Equal(expected.ElevationData, actual.ElevationData);
            Assert.Equal(expected.RoadData, actual.RoadData);
            Assert.Equal(expected.ForestDensityData, actual.ForestDensityData);
            Assert.Equal(expected.TerrainTypeColorData, actual.TerrainTypeColorData);
            Assert.Equal(expected.ForestTypeData, actual.ForestTypeData);
        }

        private static byte[] RoundTrip(NeighbourhoodLayout layout, NeighbourhoodMap map, out string reason)
        {
            var bytes = NeighbourhoodRasterCache.Serialize(layout, map);
            NeighbourhoodMap loaded;
            var ok = NeighbourhoodRasterCache.TryDeserialize(bytes, layout, out loaded, out reason);
            Assert.True(ok, "the round trip was refused: " + reason);
            AssertSameArrays(map, loaded);
            return bytes;
        }

        // ---- where the file goes -------------------------------------------------------------------

        [Fact]
        public void TheRasterLivesBesideTheLayoutSidecar_NotInTheNeighbourhoodFolder()
        {
            var path = NeighbourhoodRasterCache.PathFor(_root, Slot);

            Assert.NotNull(path);
            Assert.Equal(Path.Combine(_root, "Neighbourhoods", "generated", Slot + ".raster"), path);
            //the same folder the layout sidecar uses, which is the whole of "beside it"
            Assert.Equal(Path.GetDirectoryName(NeighbourhoodLayoutStore.PathFor(_root, Slot)),
                Path.GetDirectoryName(path));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(9)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void AnUnknownSlotHasNoPath(int slot)
        {
            Assert.Null(NeighbourhoodRasterCache.PathFor(_root, slot));
            Assert.Null(NeighbourhoodRasterCache.Load(_root, slot, Layout()));
            //and the gate holds on the way out too: a layout for a slot that does not exist has
            //nowhere to go, whatever it holds
            Assert.False(NeighbourhoodRasterCache.Save(_root, Layout(slot: slot), BusyMap()));
            Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        }

        [Fact]
        public void ThereIsNoRasterWithoutAUserDirectory()
        {
            Assert.Null(NeighbourhoodRasterCache.PathFor(null, Slot));
            Assert.Null(NeighbourhoodRasterCache.PathFor(string.Empty, Slot));
        }

        [Fact]
        public void NothingDerivedIsEverWrittenIntoANeighbourhoodFolder()
        {
            //The promise Scope A is made of. A real UserData folder exists, empty, and the cache is
            //asked to write to the slot whose folder it is - and the answer is a file in the user's
            //own directory and an untouched neighbourhood.
            var userData = Path.Combine(_root, "UserData" + Slot);
            Directory.CreateDirectory(userData);
            File.WriteAllText(Path.Combine(userData, "neighborhood.iff"), "not ours");

            var layout = Layout();
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));

            Assert.True(File.Exists(NeighbourhoodRasterCache.PathFor(_root, Slot)));
            Assert.Equal(new[] { "neighborhood.iff" },
                Directory.GetFiles(userData).Select(Path.GetFileName).ToArray());
            Assert.False(NeighbourhoodLayoutStore.IsInsideUserData(_root,
                NeighbourhoodRasterCache.PathFor(_root, Slot)));
        }

        // ---- the round trip ------------------------------------------------------------------------

        [Fact]
        public void AllSixArraysComeBackExactly()
        {
            var layout = Layout();
            var map = BusyMap();

            RoundTrip(layout, map, out var reason);

            Assert.Null(reason);
        }

        [Fact]
        public void TheSameLayoutAndMapAlwaysProduceTheSameBytes()
        {
            //Determinism at the file level, and the reason a test may compare bytes rather than
            //values: the stamp is a hash, not a timestamp.
            var layout = Layout();
            var map = BusyMap();

            var first = NeighbourhoodRasterCache.Serialize(layout, map);
            var second = NeighbourhoodRasterCache.Serialize(layout, map);

            Assert.Equal(first, second);
        }

        [Fact]
        public void TheFileIsTheSizeItsHeaderSaysItIs()
        {
            var bytes = RoundTrip(Layout(), BusyMap(), out _);

            //48-byte header, 12 bytes a tile, 8-byte checksum. Computable, not declared, which is
            //what makes a truncated file detectable before anything is allocated.
            Assert.Equal(NeighbourhoodRasterCache.HeaderSize
                + Side * Side * NeighbourhoodRasterCache.BytesPerTile
                + NeighbourhoodRasterCache.ChecksumSize, bytes.Length);
        }

        /// <summary>
        /// Put bytes where the cache would have put them, for the tests that need a file this
        /// build will not accept. The write goes through the same file the store would use, so a
        /// test is exercising the real read path and not a private one.
        /// </summary>
        private void WriteRaw(byte[] bytes)
        {
            var path = NeighbourhoodRasterCache.PathFor(_root, Slot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }

        // ---- what invalidates it ---------------------------------------------------------------------

        [Fact]
        public void ADifferentSeedIsRegenerated()
        {
            var layout = Layout(seed: 4242);
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));

            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, Layout(seed: 4243)));
        }

        [Fact]
        public void ADifferentGeneratorVersionIsRegenerated()
        {
            var layout = Layout(generatorVersion: 7);
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));

            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, Layout(generatorVersion: 8)));
        }

        [Fact]
        public void ADifferentMapSizeIsRegenerated()
        {
            //The file is stamped with the size it was generated at, so a layout that has moved to
            //another size - or a build that changed the default - cannot hand back the wrong
            //number of tiles. The lot rectangles are placed inside the smaller map and the larger
            //one, so nothing else about the two layouts differs but the size.
            var layout = Layout(mapSize: Side);
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));

            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, Layout(mapSize: 64)));
        }

        [Fact]
        public void ADifferentArrangementIsRegenerated()
        {
            //The interesting one, and the one a seed and a version cannot catch: the seed is the
            //same, the generator is the same, and one lot has been bulldozed - so the layout's
            //rectangle for it moved, and the map in the file is a map of the neighbourhood as it
            //was before.
            var layout = Layout();
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));

            var moved = Layout(moveFirstLotBy: 1);
            Assert.NotEqual(NeighbourhoodRasterCache.LayoutHash(layout),
                NeighbourhoodRasterCache.LayoutHash(moved));
            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, moved));
        }

        [Fact]
        public void TheLayoutHashIgnoresWhatTheGeneratorNeverReads()
        {
            //A session saved onto the layout, a lot's rotation and its position on the
            //neighbourhood bitmap all move without moving a tile. Including them would throw away
            //a three-megabyte file over a clock, so the stamp is a hash of the generator's input
            //and not of the layout.
            var before = Layout();
            var withRotation = new NeighbourhoodLayout(Slot, before.Seed, before.GeneratorVersion,
                before.SchemaVersion, before.MapSize, before.Lots.Select(l =>
                    new NeighbourhoodLot
                    {
                        Number = l.Number, Zone = l.Zone, Size = l.Size, MapX = l.MapX, MapY = l.MapY,
                        WorldX = l.WorldX, WorldY = l.WorldY, Rotation = (l.Rotation + 1) & 3,
                        TerrainType = l.TerrainType, BaseHeight = l.BaseHeight, DoorSide = l.DoorSide,
                        HouseHash = l.HouseHash, HasHouse = l.HasHouse
                    }),
                NeighbourhoodWorldState.CreateDefault());

            Assert.Equal(NeighbourhoodRasterCache.LayoutHash(before),
                NeighbourhoodRasterCache.LayoutHash(withRotation));
        }

        [Fact]
        public void ARefusedStampLeavesTheFileAlone()
        {
            //A cache miss is not a cache deletion. The stale file is still the best answer
            //available until a generation replaces it, and the atomic write that replaces it does
            //not care what was there.
            var layout = Layout();
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));
            var path = NeighbourhoodRasterCache.PathFor(_root, Slot);
            var before = File.ReadAllBytes(path);

            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, Layout(seed: 1)));

            Assert.Equal(before, File.ReadAllBytes(path));
        }

        [Fact]
        public void AMapForADifferentSlotIsRefused()
        {
            //The same file, read as a different neighbourhood: the map is perfectly valid and
            //utterly wrong, and the slot stamp is the only thing that can tell.
            var layout = Layout();
            var bytes = NeighbourhoodRasterCache.Serialize(layout, BusyMap());
            var elsewhere = new NeighbourhoodLayout(Slot + 1, layout.Seed, layout.GeneratorVersion,
                layout.SchemaVersion, layout.MapSize, layout.Lots);

            NeighbourhoodMap map;
            string reason;
            Assert.False(NeighbourhoodRasterCache.TryDeserialize(bytes, elsewhere, out map, out reason));
            Assert.Null(map);
            Assert.Contains("slot", reason);
        }

        [Fact]
        public void AMatchWithNothingToMatchAgainstIsRefused()
        {
            NeighbourhoodMap map;
            string reason;
            Assert.False(NeighbourhoodRasterCache.TryDeserialize(
                NeighbourhoodRasterCache.Serialize(Layout(), BusyMap()), null,
                out map, out reason));
            Assert.Null(map);
            Assert.NotNull(reason);
        }

        // ---- a bad file -------------------------------------------------------------------------------

        [Fact]
        public void AMissingFileIsAMissAndNotAWarning()
        {
            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, Layout()));
        }

        [Fact]
        public void AnEmptyFileIsIgnoredRatherThanThrown()
        {
            var path = NeighbourhoodRasterCache.PathFor(_root, Slot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[0]);

            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, Layout()));
        }

        [Fact]
        public void ATruncatedFileIsIgnoredRatherThanThrown()
        {
            var layout = Layout();
            var bytes = NeighbourhoodRasterCache.Serialize(layout, BusyMap());
            //every prefix, not just one: a file that is short by a byte and short by a megabyte
            //are the same failure, and the one that reads past the end is the one that matters
            foreach (var length in new[] { 1, 4, 47, 60, bytes.Length / 2, bytes.Length - 1 })
            {
                var cut = new byte[length];
                Array.Copy(bytes, cut, length);
                NeighbourhoodMap map;
                string reason;
                Assert.False(NeighbourhoodRasterCache.TryDeserialize(cut, layout, out map, out reason));
                Assert.Null(map);
            }
        }

        [Fact]
        public void AFileThatIsNotARasterIsIgnoredRatherThanThrown()
        {
            var layout = Layout();
            var json = NeighbourhoodLayoutStore.Serialize(layout);

            NeighbourhoodMap map;
            string reason;
            Assert.False(NeighbourhoodRasterCache.TryDeserialize(json, layout, out map, out reason));
            Assert.Null(map);
            Assert.Contains("not a generated-map raster", reason);

            //and the same on disk, where the layout sidecar's own bytes are the most likely thing
            //to be in the way
            WriteRaw(json);
            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, layout));
        }

        [Fact]
        public void AFileWhoseContentsDoNotMatchItsChecksumIsIgnored()
        {
            //The right length and the right header, and one byte of road scribbled on. Nothing
            //about the shape gives this away - which is exactly why the trailing checksum is there.
            var layout = Layout();
            var bytes = NeighbourhoodRasterCache.Serialize(layout, BusyMap());
            bytes[NeighbourhoodRasterCache.HeaderSize + 2 * NeighbourhoodRasterCache.BytesPerTile] ^= 0x5A;

            NeighbourhoodMap map;
            string reason;
            Assert.False(NeighbourhoodRasterCache.TryDeserialize(bytes, layout, out map, out reason));
            Assert.Null(map);
            Assert.Contains("checksum", reason);
        }

        [Fact]
        public void AHeaderThatDisagreesWithItselfIsIgnored()
        {
            var layout = Layout();
            var good = NeighbourhoodRasterCache.Serialize(layout, BusyMap());

            //each of these is a header field a hand-edit or a partial write could plausibly have
            //moved, and each has to be caught before a single array is believed
            var futureVersion = (byte[])good.Clone();
            futureVersion[NeighbourhoodRasterCache.VersionOffset] = 0xFF;
            var wrongSize = (byte[])good.Clone();
            wrongSize[NeighbourhoodRasterCache.WidthOffset] = 0x0B;               // one tile narrower
            var impossibleSize = (byte[])good.Clone();
            impossibleSize[NeighbourhoodRasterCache.WidthOffset] = 0xFF;
            impossibleSize[NeighbourhoodRasterCache.WidthOffset + 1] = 0xFF;
            impossibleSize[NeighbourhoodRasterCache.WidthOffset + 2] = 0xFF;
            impossibleSize[NeighbourhoodRasterCache.WidthOffset + 3] = 0x7F;

            foreach (var candidate in new[] { futureVersion, wrongSize, impossibleSize })
            {
                NeighbourhoodMap map;
                string reason;
                Assert.False(NeighbourhoodRasterCache.TryDeserialize(candidate, layout, out map, out reason));
                Assert.Null(map);
            }
        }

        [Fact]
        public void AFileFromABuildThisOneIsNotIsIgnored()
        {
            //The version is read before anything else is believed, so a future format is refused
            //rather than reinterpreted - which is what lets version 2 be added without this build
            //misreading it as a damaged version 1.
            var layout = Layout();
            var bytes = NeighbourhoodRasterCache.Serialize(layout, BusyMap());
            bytes[NeighbourhoodRasterCache.VersionOffset] = 2;
            bytes[NeighbourhoodRasterCache.VersionOffset + 1] = 0;
            bytes[NeighbourhoodRasterCache.VersionOffset + 2] = 0;
            bytes[NeighbourhoodRasterCache.VersionOffset + 3] = 0;

            NeighbourhoodMap map;
            string reason;
            Assert.False(NeighbourhoodRasterCache.TryDeserialize(bytes, layout, out map, out reason));
            Assert.Null(map);
            Assert.Contains("format version is 2", reason);

            WriteRaw(bytes);
            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, layout));
        }

        [Fact]
        public void ACorruptFileIsReplacedByTheNextGeneration()
        {
            //The whole rule in one test: a file nobody can stand behind costs a regeneration, and
            //the regeneration puts a good file back where the bad one was.
            var layout = Layout();
            var path = NeighbourhoodRasterCache.PathFor(_root, Slot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[] { 0x53, 0x4E, 0x52, 0x53, 0x01, 0x00, 0x00, 0x00 });

            Assert.Null(NeighbourhoodRasterCache.Load(_root, Slot, layout));

            var fresh = BusyMap();
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, fresh));
            AssertSameArrays(fresh, NeighbourhoodRasterCache.Load(_root, Slot, layout));
        }

        // ---- writing -----------------------------------------------------------------------------------

        [Fact]
        public void AnAtomicWriteLeavesNoStagedFileBehind()
        {
            var layout = Layout();
            var path = NeighbourhoodRasterCache.PathFor(_root, Slot);

            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));
            //twice, so the second write is the replacing half of the contract rather than the
            //creating one: File.Replace is the step that could leave a .tmp next to a good file
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));

            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp"));
            AssertSameArrays(BusyMap(), NeighbourhoodRasterCache.Load(_root, layout.Slot, layout));
        }

        [Fact]
        public void AFailedWriteIsAWarningAndNotAThrow()
        {
            //The raster is a cache, and a cache that cannot be written costs a regeneration - not
            //an exception out of a background refresh and not a fallback to the painted map.
            var layout = Layout();
            var blocker = NeighbourhoodRasterCache.PathFor(_root, Slot) + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(blocker));
            //a directory where the staged file goes: the write fails at the staging step, which is
            //the only step a test can provoke from outside
            Directory.CreateDirectory(blocker);

            Assert.False(NeighbourhoodRasterCache.Save(_root, layout, BusyMap()));
            Assert.False(File.Exists(NeighbourhoodRasterCache.PathFor(_root, Slot)));
        }

        [Fact]
        public void AMapThatIsNotTheLayoutsSizeIsNotWritten()
        {
            //A raster stamped with a size the layout does not have would be refused by every later
            //read, so it is not written at all rather than written uselessly.
            Assert.False(NeighbourhoodRasterCache.Save(_root, Layout(mapSize: 64), BusyMap(Side)));
            Assert.False(File.Exists(NeighbourhoodRasterCache.PathFor(_root, Slot)));
        }

        [Fact]
        public void NothingIsWrittenWithoutALayoutOrAMap()
        {
            Assert.False(NeighbourhoodRasterCache.Save(_root, null, BusyMap()));
            Assert.False(NeighbourhoodRasterCache.Save(_root, Layout(), null));
            Assert.False(NeighbourhoodRasterCache.Save(null, Layout(), BusyMap()));
            Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        }
    }

    /// <summary>
    /// The one thing <see cref="NeighbourhoodRasterCache"/> exists for, asserted through the
    /// pipeline that uses it: a refresh that finds a stamped raster must hand back <em>that</em>
    /// map, and a refresh that finds nothing must write one.
    ///
    /// <para>
    /// In the <c>generated world cache</c> collection, and for the reason the other class in it
    /// gives: <see cref="NeighbourhoodWorldCache"/> is process-wide, so these tests must not run
    /// beside anything that is refreshing a world or counting hits.
    /// </para>
    /// </summary>
    [Collection("generated world cache")]
    public sealed class NeighbourhoodRasterCacheLifecycleTests : IDisposable
    {
        private const int Slot = 5;
        private const int Mode = 4;

        private readonly string _root;
        private readonly int _seed;

        public NeighbourhoodRasterCacheLifecycleTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-neigh-raster-life-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _seed = NeighbourhoodSceneSource.SeedForMode(Slot);
            NeighbourhoodWorldCache.Clear();
            NeighbourhoodLifecycle.Forget();
        }

        public void Dispose()
        {
            NeighbourhoodWorldCache.Clear();
            NeighbourhoodLifecycle.Forget();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        [Fact]
        public void ARefreshWritesTheRasterItJustGenerated()
        {
            //The half that pays for itself on the next launch. Nothing here is a cache hit - the
            //sidecar is empty and nothing is in memory - so the generator runs, and what it ran
            //must be left on disk.
            var world = Refresh(FourLots());

            Assert.True(world.HasWorld);
            var path = NeighbourhoodRasterCache.PathFor(_root, Slot);
            Assert.True(File.Exists(path));

            //and the file is the map: not merely present, not merely the right size, the very
            //arrays the refresh is about to hand a renderer
            var read = NeighbourhoodRasterCache.Load(_root, Slot, world.Layout);
            Assert.NotNull(read);
            Assert.True(world.Map.ContentEquals(read));
        }

        [Fact]
        public void ARefreshThatFindsAStampedRasterLoadsItInsteadOfGeneratingIt()
        {
            //The claim, as an assertion. A raster is planted for exactly the layout this refresh
            //will arrive at, with values the generator could not have produced, and the refresh
            //must hand back those values. Nothing short of reading the file gets that: the
            //generator is a pure function of (layout, seed) and would produce its own answer.
            var reads = FourLots();
            //The shipped revert packs synthetically, so the planted raster is stamped for the shelf
            //layout the refresh will arrive at. See NeighbourhoodConnected.
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, _seed);
            var planted = Planted(layout);
            Assert.True(NeighbourhoodRasterCache.Save(_root, layout, planted));

            var world = Refresh(reads);

            Assert.True(world.HasWorld);
            Assert.True(world.Map.ContentEquals(planted),
                "the refresh generated the map instead of reading the raster cache");
        }

        [Fact]
        public void AStaleRasterIsIgnoredAndReplaced()
        {
            //A lot built between two sessions. The planted raster is stamped for the arrangement
            //as it was, the new lot changes what the generator is asked for, and the refresh must
            //not hand back the old world - and must leave the new one behind for the next launch.
            //
            //A new lot rather than a bulldozed one, and that is the whole of the design: the stamp
            //is a hash of what the generator reads, so a lot whose house file changed but whose
            //rectangle did not move would invalidate nothing - correctly, because the generator
            //would produce the very same tiles. Only a change that reaches the generator's input
            //may throw the file away.
            var reads = FourLots();
            var before = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, _seed);
            Assert.True(NeighbourhoodRasterCache.Save(_root, before, Planted(before)));

            reads.Add(Read(25, 220, 20, 16, 0x5000u));
            var world = Refresh(reads);

            Assert.True(world.HasWorld);
            Assert.NotNull(world.Layout.FindLot(25));
            Assert.False(world.Map.ContentEquals(Planted(before)));
            Assert.True(world.Map.ContentEquals(
                NeighbourhoodRasterCache.Load(_root, Slot, world.Layout)));
        }

        [Fact]
        public void ACorruptRasterIsIgnoredAndTheRefreshStillProducesAWorld()
        {
            //The failure mode the whole refusal rule is for: three megabytes of garbage where a
            //map should be must cost a regeneration, never an exception and never a 2D fallback.
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(FourLots(), Slot, _seed);
            var path = NeighbourhoodRasterCache.PathFor(_root, Slot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bytes = NeighbourhoodRasterCache.Serialize(layout, Planted(layout));
            bytes[NeighbourhoodRasterCache.HeaderSize + 17] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            var world = Refresh(FourLots());

            Assert.True(world.HasWorld);
            Assert.Equal(0, NeighbourhoodWorldCache.Stats.Fallbacks);
            //and the bad file is gone, because the good one replaced it
            Assert.NotEqual(bytes, File.ReadAllBytes(path));
            Assert.True(world.Map.ContentEquals(
                NeighbourhoodRasterCache.Load(_root, Slot, world.Layout)));
        }

        [Fact]
        public void TheSettingOffReadsAndWritesNoRaster()
        {
            //§0 decision 1 and §10, restated for the new file. A player who has not opted into the
            //3D neighbourhood must not pay for any of it, and a new cache file is part of "any of
            //it".
            var world = NeighbourhoodLifecycle.RefreshBlocking(_root, Slot, Mode,
                NeighbourhoodView.Bitmap2D, new SourceOver(FourLots()));

            Assert.False(world.HasWorld);
            Assert.False(File.Exists(NeighbourhoodRasterCache.PathFor(_root, Slot)));
            Assert.Empty(Directory.GetFiles(_root, "*.raster", SearchOption.AllDirectories));
        }

        // ---- fixtures -----------------------------------------------------------------------------------

        /// <summary>
        /// A map no generator would produce for this layout: every tile snow, a road mask that
        /// increases monotonically across the whole map, and a forest tint that is a function of
        /// the tile index. It exists so "the file was read" and "the file was regenerated" are two
        /// different answers.
        /// </summary>
        private static NeighbourhoodMap Planted(NeighbourhoodLayout layout)
        {
            var map = NeighbourhoodMap.Create(layout.MapSize);
            for (int i = 0; i < map.TerrainType.Length; i++)
            {
                map.TerrainType[i] = NeighbourhoodTerrain.Snow;
                map.TerrainTypeColorData[i] = NeighbourhoodTerrain.ColorFor(NeighbourhoodTerrain.Snow);
                map.ElevationData[i] = (byte)(i % 251);
                map.RoadData[i] = (byte)(0x10 | (i % 16));
                map.ForestDensityData[i] = (byte)(i % 256);
                map.ForestTypeData[i] = new Color((byte)(i & 0xFF), (byte)7,
                    (byte)((i >> 2) & 0xFF), (byte)255);
            }
            return map;
        }

        private NeighbourhoodGeneratedWorld Refresh(List<NeighbourhoodLotRead> reads)
        {
            return NeighbourhoodLifecycle.RefreshBlocking(_root, Slot, Mode,
                NeighbourhoodView.World3D, new SourceOver(reads));
        }

        /// <summary>
        /// Four lots a hundred pixels apart on the backdrop, well clear of one another, so the
        /// packer has nothing to relax and a change to one of them has somewhere to go without
        /// touching the others.
        /// </summary>
        private static List<NeighbourhoodLotRead> FourLots()
        {
            return new List<NeighbourhoodLotRead>
            {
                Read(21, 20, 20, 16, 0x1000u),
                Read(22, 120, 20, 16, 0x2000u),
                Read(23, 20, 120, 16, 0x3000u),
                Read(24, 120, 120, 16, 0x4000u),
            };
        }

        private static NeighbourhoodLotRead Read(short number, int mapX, int mapY, int size, uint hash)
        {
            return new NeighbourhoodLotRead
            {
                Number = number,
                MapX = mapX,
                MapY = mapY,
                Size = size,
                HasHouse = true,
                Rotation = 0,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = hash,
                Zone = LotZoning.ZoneResidential
            };
        }

        /// <summary>
        /// A content source over a fixed list of lot records, so the lifecycle can be driven with
        /// no neighbourhood folder anywhere.
        /// </summary>
        private sealed class SourceOver : ILotContentSource
        {
            private readonly List<NeighbourhoodLotRead> _reads;

            public SourceOver(List<NeighbourhoodLotRead> reads)
            {
                _reads = reads;
            }

            public IReadOnlyList<string> LotLocationLines(int mode)
            {
                return _reads.Select(r => r.Number + "," + r.MapX + "," + r.MapY).ToList();
            }

            public bool TryReadLot(int number, out NeighbourhoodLotRead lot)
            {
                lot = _reads.FirstOrDefault(r => r.Number == number);
                return lot != null;
            }
        }
    }
}
