/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The generated-world sidecar: where it is written, what it holds, and - the part that
    /// matters most - what it refuses to be trusted about.
    ///
    /// <para>
    /// The store's job is not to be clever. It writes the layout atomically, it stamps the file
    /// with the schema and generator versions that produced it, and it answers "no" to anything
    /// it does not fully recognise. A cached file that is stale, corrupt, from a future build, or
    /// for a different slot must cost a regeneration and nothing worse - a half-read layout
    /// would place some lots from one world and some from another and look entirely plausible.
    /// </para>
    ///
    /// <para>
    /// The tests run against a throwaway directory per test, and the last group asserts the one
    /// thing that must never be negotiable: nothing derived is ever written into a
    /// <c>UserData*</c> folder.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodLayoutStoreTests : IDisposable
    {
        private readonly string _root;

        public NeighbourhoodLayoutStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-neigh-layout-" + Guid.NewGuid().ToString("N"));
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

        private static NeighbourhoodLayout SampleLayout(int slot = 4, int seed = 123456789)
        {
            return NeighbourhoodLayoutBuilder.PackOnGrid(new List<NeighbourhoodLotRead>
            {
                new NeighbourhoodLotRead
                {
                    Number = 21, MapX = 102, MapY = 88, HasHouse = true, Size = 24,
                    Rotation = 2, TerrainType = TerrainType.GRASS, BaseHeight = 17,
                    HouseHash = 0xa1b2c3d4u, Zone = LotZoning.ZoneResidential
                },
                new NeighbourhoodLotRead
                {
                    Number = 22, MapX = 150, MapY = 91, HasHouse = false, Size = 0,
                    TerrainType = TerrainType.SAND, BaseHeight = 0,
                    HouseHash = 0u, Zone = LotZoning.ZoneCommunity
                },
                new NeighbourhoodLotRead
                {
                    Number = 31, MapX = 240, MapY = 130, HasHouse = true, Size = 32,
                    Rotation = 1, TerrainType = TerrainType.SNOW, BaseHeight = 240,
                    HouseHash = 0x0000beefu, Zone = LotZoning.ZoneStudio
                }
            }, slot, seed);
        }

        private void WriteRaw(int slot, string json)
        {
            var path = NeighbourhoodLayoutStore.PathFor(_root, slot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private string RawJson(int slot)
        {
            return File.ReadAllText(NeighbourhoodLayoutStore.PathFor(_root, slot));
        }

        // ---- where the file goes -----------------------------------------------------------------

        [Fact]
        public void TheSidecarLivesBesideTheNamesFile_NotInTheNeighbourhoodFolder()
        {
            var path = NeighbourhoodLayoutStore.PathFor(_root, 4);

            Assert.NotNull(path);
            Assert.Equal(
                Path.Combine(_root, "Neighbourhoods", "generated", "4.json"),
                path);
            Assert.Equal(NeighbourhoodManager.NamesFolderName, NeighbourhoodLayoutStore.RootFolderName);
        }

        [Fact]
        public void EachSlotGetsItsOwnFile_NamedByTheSlotId()
        {
            for (var slot = 0; slot <= 8; slot++)
            {
                Assert.Equal(
                    Path.Combine(_root, "Neighbourhoods", "generated", slot + ".json"),
                    NeighbourhoodLayoutStore.PathFor(_root, slot));
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(9)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void AnUnknownSlotHasNoPath(int slot)
        {
            Assert.Null(NeighbourhoodLayoutStore.PathFor(_root, slot));
            Assert.False(NeighbourhoodLayoutStore.IsKnownSlot(slot));
            Assert.Null(NeighbourhoodLayoutStore.Load(_root, slot));
            Assert.False(NeighbourhoodLayoutStore.Save(_root, SampleLayout(slot)));
        }

        [Fact]
        public void NoUserDirectory_MeansNoPath()
        {
            Assert.Null(NeighbourhoodLayoutStore.PathFor(null, 0));
            Assert.Null(NeighbourhoodLayoutStore.PathFor(string.Empty, 0));
            Assert.Null(NeighbourhoodLayoutStore.GeneratedFolder(null));
        }

        // ---- atomicity --------------------------------------------------------------------------

        [Fact]
        public void SavingWritesTheLayoutAndLeavesNoStagedFileBehind()
        {
            Assert.True(NeighbourhoodLayoutStore.Save(_root, SampleLayout()));

            var path = NeighbourhoodLayoutStore.PathFor(_root, 4);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void SavingTwiceKeepsThePreviousLayoutAsABackup()
        {
            var first = SampleLayout(seed: 1);
            var second = SampleLayout(seed: 2);

            Assert.True(NeighbourhoodLayoutStore.Save(_root, first));
            Assert.True(NeighbourhoodLayoutStore.Save(_root, second));

            var path = NeighbourhoodLayoutStore.PathFor(_root, 4);
            //AtomicFileCommit's contract, asserted here because "the store uses it" is the claim
            //that is worth pinning: a crash mid-write leaves a readable previous generation
            Assert.True(File.Exists(path + ".bak"));
            Assert.Equal(NeighbourhoodLayoutStore.Serialize(first), File.ReadAllBytes(path + ".bak"));
            Assert.Equal(NeighbourhoodLayoutStore.Serialize(second), File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void TheSidecarIsTheOnlyThingInTheGeneratedFolder()
        {
            NeighbourhoodLayoutStore.Save(_root, SampleLayout());
            NeighbourhoodLayoutStore.Save(_root, SampleLayout(slot: 5));

            var folder = NeighbourhoodLayoutStore.GeneratedFolder(_root);
            var files = Directory.GetFiles(folder);
            Array.Sort(files, StringComparer.Ordinal);

            Assert.Equal(
                new[]
                {
                    Path.Combine(folder, "4.json"),
                    Path.Combine(folder, "5.json")
                },
                files);
        }

        // ---- round trip -------------------------------------------------------------------------

        [Fact]
        public void ALayoutSurvivesTheRoundTripFieldForField()
        {
            var original = SampleLayout();

            Assert.True(NeighbourhoodLayoutStore.Save(_root, original));
            var reloaded = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(reloaded);
            Assert.Equal(original.Slot, reloaded.Slot);
            Assert.Equal(original.Seed, reloaded.Seed);
            Assert.Equal(original.MapSize, reloaded.MapSize);
            Assert.Equal(original.GeneratorVersion, reloaded.GeneratorVersion);
            Assert.Equal(original.SchemaVersion, reloaded.SchemaVersion);
            Assert.Equal(original.Extent, reloaded.Extent);
            Assert.Equal(original.LotCount, reloaded.LotCount);

            for (int i = 0; i < original.Lots.Count; i++)
            {
                var a = original.Lots[i];
                var b = reloaded.Lots[i];
                Assert.Equal(a.Number, b.Number);
                Assert.Equal(a.Zone, b.Zone);
                Assert.Equal(a.Size, b.Size);
                Assert.Equal(a.MapX, b.MapX);
                Assert.Equal(a.MapY, b.MapY);
                Assert.Equal(a.WorldX, b.WorldX);
                Assert.Equal(a.WorldY, b.WorldY);
                Assert.Equal(a.Rotation, b.Rotation);
                Assert.Equal(a.TerrainType, b.TerrainType);
                Assert.Equal(a.BaseHeight, b.BaseHeight);
                Assert.Equal(a.DoorSide, b.DoorSide);
                Assert.Equal(a.HouseHash, b.HouseHash);
                Assert.Equal(a.HasHouse, b.HasHouse);
            }
        }

        [Fact]
        public void TheSameLayoutAlwaysSerialisesToTheSameBytes()
        {
            //the sidecar is a cache of a deterministic value; a layout that reshuffled its own
            //JSON would show up as a file changing under a player's feet for no reason
            Assert.Equal(NeighbourhoodLayoutStore.Serialize(SampleLayout()),
                NeighbourhoodLayoutStore.Serialize(SampleLayout()));
        }

        [Fact]
        public void TheFileNamesTheFieldsThePlanDocuments()
        {
            var json = Encoding.UTF8.GetString(NeighbourhoodLayoutStore.Serialize(SampleLayout()));

            foreach (var field in new[]
            {
                "\"schemaVersion\"", "\"generatorVersion\"", "\"slot\"", "\"seed\"", "\"mapSize\"", "\"extent\"", "\"lots\"",
                "\"number\"", "\"zone\"", "\"size\"", "\"mapX\"", "\"mapY\"", "\"worldX\"", "\"worldY\"",
                "\"rotation\"", "\"terrain\"", "\"baseHeight\"", "\"doorSide\"", "\"houseHash\"", "\"hasHouse\""
            })
            {
                Assert.Contains(field, json);
            }
            //a house hash is eight hex digits, so a sidecar is readable by eye
            Assert.Contains("\"houseHash\": \"a1b2c3d4\"", json);
        }

        [Fact]
        public void TheForwardFieldsAreOmittedWhileTheyAreEmpty()
        {
            var json = Encoding.UTF8.GetString(NeighbourhoodLayoutStore.Serialize(SampleLayout()));

            //a file with none of them is a file a build that has never heard of them wrote, which
            //is what makes the seam removable
            Assert.DoesNotContain("lotState", json);
            Assert.DoesNotContain("lastSimulated", json);
            Assert.DoesNotContain("entrance", json);
        }

        [Fact]
        public void AHandEditedExtentIsRecomputedRatherThanTrusted()
        {
            //the extent is written for readability; on load it is derived from the lots, so a
            //stale or hand-edited value cannot put the world's bounds out of step with its lots
            WriteRaw(4, "{\"schemaVersion\": 1, \"generatorVersion\": 1, \"slot\": 4, \"seed\": 1, \"mapSize\": 512,"
                + " \"extent\": {\"minX\": 400, \"minY\": 400, \"maxX\": 411, \"maxY\": 411},"
                + " \"lots\": [{\"number\": 21, \"size\": 10, \"worldX\": 8, \"worldY\": 8, \"houseHash\": \"a1b2c3d4\"}]}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(new NeighbourhoodExtent(8, 8, 18, 18), layout.Extent);
        }

        [Fact]
        public void TheForwardFieldsRoundTripWhenSomethingHasWrittenThem()
        {
            var layout = SampleLayout();
            layout.Lots[0].LotState = NeighbourhoodLotState.Hollow;
            layout.Lots[0].LastSimulated = 123456789012345L;
            layout.Lots[0].Entrance = new NeighbourhoodLotEntrance(11, 22, 3);

            Assert.True(NeighbourhoodLayoutStore.Save(_root, layout));
            var reloaded = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.Equal(NeighbourhoodLotState.Hollow, reloaded.Lots[0].LotState);
            Assert.Equal(123456789012345L, reloaded.Lots[0].LastSimulated);
            Assert.Equal(11, reloaded.Lots[0].Entrance.Value.WorldX);
            Assert.Equal(22, reloaded.Lots[0].Entrance.Value.WorldY);
            Assert.Equal(3, reloaded.Lots[0].Entrance.Value.Facing);
        }

        [Fact]
        public void ALotWithNoForwardFieldsReadsBackAtItsDefaults()
        {
            //this is the "absent means defaulted" half of the seam, exercised through a file that
            //has no forward fields in it at all
            Assert.True(NeighbourhoodLayoutStore.Save(_root, SampleLayout()));
            var reloaded = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.Equal(NeighbourhoodLotState.Unloaded, reloaded.Lots[0].LotState);
            Assert.Equal(0L, reloaded.Lots[0].LastSimulated);
            Assert.Null(reloaded.Lots[0].Entrance);
        }

        [Fact]
        public void AnEmptyLayoutIsStillALayout()
        {
            var empty = NeighbourhoodLayout.Empty(2, 7, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion);

            Assert.True(NeighbourhoodLayoutStore.Save(_root, empty));
            var reloaded = NeighbourhoodLayoutStore.Load(_root, 2);

            Assert.NotNull(reloaded);
            Assert.Equal(0, reloaded.LotCount);
        }

        // ---- what is not believed ----------------------------------------------------------------

        [Fact]
        public void AMissingFileIsSimplyNothingToLoad()
        {
            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
        }

        [Fact]
        public void ASchemaFromTheFuture_IsIgnoredRatherThanReinterpreted()
        {
            WriteRaw(4, "{\"schemaVersion\": 99, \"generatorVersion\": 1, \"slot\": 4, \"seed\": 1, \"lots\": []}");

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
        }

        [Fact]
        public void ThePlanSampleSpellingOfTheSchemaStampIsAlsoAccepted()
        {
            //the plan's example JSON calls it "version"; both are written and either is read
            WriteRaw(4, "{\"version\": 1, \"generatorVersion\": 1, \"slot\": 4, \"seed\": 1, \"lots\": []}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            //a stamp of 1 is read as the first schema and migrated in memory, so what comes back
            //reports the schema this build works in
            Assert.Equal(NeighbourhoodLayout.CurrentSchemaVersion, layout.SchemaVersion);
        }

        [Fact]
        public void AFileWithNoSchemaStampIsTreatedAsTheFirstOne()
        {
            WriteRaw(4, "{\"slot\": 4, \"seed\": 1, \"lots\": []}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            //as above: accepted, read as schema 1, and brought forward in memory
            Assert.Equal(NeighbourhoodLayout.CurrentSchemaVersion, layout.SchemaVersion);
        }

        [Fact]
        public void AFileForADifferentSlotIsNotBorrowed()
        {
            Assert.True(NeighbourhoodLayoutStore.Save(_root, SampleLayout(slot: 4)));
            //slot 5's file has been replaced with slot 4's
            WriteRaw(5, RawJson(4));

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 5));
            //slot 4's own file is untouched and still loads
            Assert.NotNull(NeighbourhoodLayoutStore.Load(_root, 4));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{ not json")]
        [InlineData("[1, 2, 3]")]
        [InlineData("\"just a string\"")]
        [InlineData("null")]
        [InlineData("{\"schemaVersion\": 1, \"slot\": 4, \"seed\": 1, \"lots\": [ }")]
        [InlineData("{\"schemaVersion\": 1, \"slot\": 4, \"seed\": 1, \"lots\": [42]}")]
        [InlineData("{\"schemaVersion\": 1, \"slot\": 4, \"seed\": 1, \"lots\": [{\"number\": 21, \"size\": 0}]}")]
        [InlineData("{\"schemaVersion\": 1, \"slot\": 4, \"seed\": 1, \"mapSize\": 0, \"lots\": []}")]
        [InlineData("{\"schemaVersion\": 1, \"seed\": 1, \"lots\": []}")]
        [InlineData("{\"schemaVersion\": 1, \"slot\": 42, \"seed\": 1, \"lots\": []}")]
        [InlineData("{\"schemaVersion\": 1, \"slot\": 4, \"seed\": 1, \"generatorVersion\": 0, \"lots\": []}")]
        public void AFileThatIsNotALayoutIsIgnored(string json)
        {
            WriteRaw(4, json);

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
        }

        [Fact]
        public void ALotPlacedOutsideTheMapIsRefused()
        {
            //a hand-edited or truncated file must not put a rectangle past the edge: the
            //generator would clip it and produce an unexplained seam
            WriteRaw(4, "{\"schemaVersion\": 1, \"generatorVersion\": 1, \"slot\": 4, \"seed\": 1, \"mapSize\": 512,"
                + " \"lots\": [{\"number\": 21, \"size\": 24, \"worldX\": 500, \"worldY\": 0, \"houseHash\": \"00000000\"}]}");

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
        }

        [Fact]
        public void ANegativeLotPositionIsRefused()
        {
            WriteRaw(4, "{\"schemaVersion\": 1, \"generatorVersion\": 1, \"slot\": 4, \"seed\": 1, \"mapSize\": 512,"
                + " \"lots\": [{\"number\": 21, \"size\": 24, \"worldX\": -1, \"worldY\": 0, \"houseHash\": \"00000000\"}]}");

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
        }

        [Fact]
        public void AHouseHashThatIsNotHexIsRefusedRatherThanGuessedAt()
        {
            WriteRaw(4, "{\"schemaVersion\": 1, \"generatorVersion\": 1, \"slot\": 4, \"seed\": 1, \"mapSize\": 512,"
                + " \"lots\": [{\"number\": 21, \"size\": 24, \"worldX\": 0, \"worldY\": 0, \"houseHash\": \"nonsense\"}]}");

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
        }

        [Fact]
        public void AnAbsentHouseHashMeansTheLotHasNoHouse()
        {
            WriteRaw(4, "{\"schemaVersion\": 1, \"generatorVersion\": 1, \"slot\": 4, \"seed\": 1, \"mapSize\": 512,"
                + " \"lots\": [{\"number\": 21, \"size\": 24, \"worldX\": 0, \"worldY\": 0}]}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(0u, layout.Lots[0].HouseHash);
            Assert.False(layout.Lots[0].HasHouse);
        }

        [Fact]
        public void UnknownMembersAreIgnored_SoALaterBuildCanAddFields()
        {
            WriteRaw(4, "{\"schemaVersion\": 1, \"generatorVersion\": 1, \"slot\": 4, \"seed\": 1, \"mapSize\": 512,"
                + " \"somethingNew\": {\"nested\": [1, 2, 3]},"
                + " \"lots\": [{\"number\": 21, \"size\": 24, \"worldX\": 4, \"worldY\": 8, \"houseHash\": \"a1b2c3d4\","
                + "            \"hasHouse\": true, \"unknownLotField\": 7}]}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(1, layout.LotCount);
            Assert.Equal(4, layout.Lots[0].WorldX);
        }

        // ---- invalidation ------------------------------------------------------------------------

        [Fact]
        public void ALayoutIsOnlyReusedForTheSeedItWasBuiltWith()
        {
            Assert.True(NeighbourhoodLayoutStore.Save(_root, SampleLayout(seed: 100)));

            Assert.NotNull(NeighbourhoodLayoutStore.LoadFor(_root, 4, 100, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion));
            Assert.Null(NeighbourhoodLayoutStore.LoadFor(_root, 4, 101, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion));
        }

        [Fact]
        public void ALayoutIsOnlyReusedForTheGeneratorThatBuiltIt()
        {
            Assert.True(NeighbourhoodLayoutStore.Save(_root, SampleLayout(seed: 100)));

            Assert.NotNull(NeighbourhoodLayoutStore.LoadFor(_root, 4, 100, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion));
            Assert.Null(NeighbourhoodLayoutStore.LoadFor(_root, 4, 100, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion + 1));
        }

        [Fact]
        public void AnUnrecognisedGeneratorVersionIsRefusedOutright()
        {
            //generator version 0 is not a version this build wrote, so it is treated exactly like
            //one from the future: dropped and regenerated, never read with a hopeful guess
            WriteRaw(4, "{\"schemaVersion\": 1, \"generatorVersion\": 0, \"slot\": 4, \"seed\": 1, \"lots\": []}");

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
            Assert.Null(NeighbourhoodLayoutStore.LoadFor(_root, 4, 1, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion));
        }

        [Fact]
        public void AFileWithNoGeneratorVersionIsReadAsThisBuildsOwn()
        {
            //an absent stamp is the first version, so a hand-written file works; only a stamp that
            //is present and wrong is a problem
            WriteRaw(4, "{\"schemaVersion\": 1, \"slot\": 4, \"seed\": 1, \"lots\": []}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, layout.GeneratorVersion);
            Assert.NotNull(NeighbourhoodLayoutStore.LoadFor(_root, 4, 1, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion));
        }

        [Fact]
        public void AnEditedLotIsReportedAsTheOnlyThingThatChanged()
        {
            var layout = SampleLayout();
            Assert.True(NeighbourhoodLayoutStore.Save(_root, layout));

            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, size: 24);
            source.Add(22, 150, 91, hasHouse: false);
            source.Add(31, 240, 130, size: 32);
            //the fixture's two untouched lots must hash exactly as the cached layout recorded,
            //or the test would be reporting its own fixture as a change
            source.Lots[21].HouseHash = 0xa1b2c3d4u;
            source.Lots[22].HouseHash = 0u;
            source.Lots[31].HouseHash = 0x0000beefu;
            //the player bought a swimming pool: same length of house file, new write time
            source.Lots[31].HouseHash = 0x0badf00du;

            var changed = NeighbourhoodLayoutStore.ChangedLots(layout, source, 1);

            Assert.Equal(new short[] { 31 }, changed);
        }

        [Fact]
        public void ALotAddedToTheNeighbourhoodIsAlsoReportedAsChanged()
        {
            var layout = SampleLayout();
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, size: 24);
            source.Add(22, 150, 91, hasHouse: false);
            source.Add(31, 240, 130, size: 32);
            source.Add(40, 300, 160, size: 24);
            //lots 21 and 22 are untouched and hash as the cached layout recorded; 31 changed and
            //40 is new
            source.Lots[21].HouseHash = 0xa1b2c3d4u;
            source.Lots[22].HouseHash = 0u;
            source.Lots[31].HouseHash = 0x0badf00du;

            var changed = NeighbourhoodLayoutStore.ChangedLots(layout, source, 1);

            //31 is the lot that was edited and 40 is the lot that is new; 21 and 22 are untouched
            //and must not be reported, or the cheap invalidation would rebuild the whole world
            //every time one lot changed
            Assert.Equal(new short[] { 31, 40 }, changed);
        }

        [Fact]
        public void ChangedLotsOfNothingIsNothing()
        {
            Assert.Empty(NeighbourhoodLayoutStore.ChangedLots(null, new FakeLotContentSource(), 1));
            Assert.Empty(NeighbourhoodLayoutStore.ChangedLots(SampleLayout(), null, 1));
        }

        [Fact]
        public void DeletingTheSidecarMakesTheNextLoadRegenerate()
        {
            Assert.True(NeighbourhoodLayoutStore.Save(_root, SampleLayout()));
            Assert.NotNull(NeighbourhoodLayoutStore.Load(_root, 4));

            Assert.True(NeighbourhoodLayoutStore.Delete(_root, 4));

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
            //deleting something that is not there is not a failure
            Assert.True(NeighbourhoodLayoutStore.Delete(_root, 4));
            Assert.False(NeighbourhoodLayoutStore.Delete(_root, 42));
        }

        // ---- never inside UserData ------------------------------------------------------------------

        [Fact]
        public void NothingIsEverWrittenIntoATs1NeighbourhoodFolder()
        {
            //a realistic user directory: the primary neighbourhood, present and full of files
            var userData = Path.Combine(_root, "UserData");
            Directory.CreateDirectory(Path.Combine(userData, "Houses"));
            File.WriteAllText(Path.Combine(userData, "Neighborhood.iff"), "not really an iff");

            Assert.True(NeighbourhoodLayoutStore.Save(_root, SampleLayout()));

            //nothing new appeared in the neighbourhood, or anywhere under it
            var entries = Directory.GetFileSystemEntries(userData);
            Array.Sort(entries, StringComparer.Ordinal);
            Assert.Equal(
                new[] { "Houses", "Neighborhood.iff" },
                entries.Select(Path.GetFileName).ToArray());
            Assert.Empty(Directory.GetFiles(userData, "*.json", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(userData, "4.json")));
        }

        [Fact]
        public void TheSidecarPathIsRecognisedAsBeingOutsideEverySlotFolder()
        {
            var path = NeighbourhoodLayoutStore.PathFor(_root, 4);

            Assert.False(NeighbourhoodLayoutStore.IsInsideUserData(_root, path));
        }

        [Theory]
        [InlineData("UserData")]
        [InlineData("UserData/Houses")]
        [InlineData("UserData2")]
        [InlineData("UserData8/4.json")]
        public void APutInsideASlotFolderIsRecognisedAsSuch(string relative)
        {
            var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(NeighbourhoodLayoutStore.IsInsideUserData(_root, path));
        }

        [Fact]
        public void ASiblingWhoseNameMerelyStartsWithTheSlotFolderIsNotInsideIt()
        {
            //"UserDataExtra" is not "UserData"; getting this wrong would refuse a legitimate write
            var path = Path.Combine(_root, "UserDataExtra", "generated", "4.json");

            Assert.False(NeighbourhoodLayoutStore.IsInsideUserData(_root, path));
        }

        [Fact]
        public void ThePathCheckIsNotFooledByMissingOrUnusableInput()
        {
            Assert.False(NeighbourhoodLayoutStore.IsInsideUserData(null, "UserData/x"));
            Assert.False(NeighbourhoodLayoutStore.IsInsideUserData(_root, null));
            Assert.False(NeighbourhoodLayoutStore.IsInsideUserData(_root, string.Empty));
        }

        [Fact]
        public void EverySidecarPathIsOutsideEverySlotFolder()
        {
            //not just slot 4: the guard is checked on the path the store is about to use, for
            //every slot it will ever be asked about
            for (var slot = 0; slot <= 8; slot++)
            {
                var path = NeighbourhoodLayoutStore.PathFor(_root, slot);
                Assert.False(NeighbourhoodLayoutStore.IsInsideUserData(_root, path),
                    "slot " + slot + "'s sidecar would land inside a neighbourhood folder");
            }
        }
    }
}
