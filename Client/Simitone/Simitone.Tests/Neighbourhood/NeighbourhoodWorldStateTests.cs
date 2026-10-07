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
using System.Text.Json;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Sidecar schema 2: the session world state, and the three promises that make adding it safe.
    ///
    /// <para>
    /// <b>Schema 1 is a byte-identical prefix.</b> A layout with no world state must serialise to
    /// exactly the document schema 1 wrote, or every cached Scope A neighbourhood is invalidated
    /// for nothing and the "the world is still the world you had" promise is broken. The first
    /// group pins that with a golden document rather than a field-by-field comparison, because a
    /// field comparison cannot see a member that was <em>added</em>.
    /// </para>
    ///
    /// <para>
    /// <b>Schema 1 still loads, and comes back as schema 2.</b> The migration is in memory and
    /// free: a sidecar written by the previous build is not an error, it is an older file, and
    /// regenerating a whole neighbourhood because the schema number moved would cost a player
    /// their generated world for a change that added a field to it.
    /// </para>
    ///
    /// <para>
    /// <b>A corrupt world is not a corrupt layout.</b> The lots in the file are a pure function of
    /// the seed and the generator version, and both are still checked, so throwing the file away
    /// over a bad clock would lose a generated world to save a session. A bad <em>lot</em> is
    /// still fatal, because a half-read layout places some lots from one world and some from
    /// another and looks entirely plausible.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldStateTests : IDisposable
    {
        private readonly string _root;

        public NeighbourhoodWorldStateTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-neigh-world-" + Guid.NewGuid().ToString("N"));
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

        // ---- fixtures -------------------------------------------------------------------------

        /// <summary>
        /// One lot, placed by hand rather than by the packer, so every number in the golden
        /// document below is one this file chose and the pin fails on a change to the writer
        /// rather than on a change to the packer.
        /// </summary>
        private static NeighbourhoodLayout OneLot(NeighbourhoodWorldState world = null)
        {
            return new NeighbourhoodLayout(4, 7, 3, NeighbourhoodLayout.CurrentSchemaVersion, 512,
                new List<NeighbourhoodLot>
                {
                    new NeighbourhoodLot
                    {
                        Number = 21, Zone = 0, Size = 10, MapX = 4, MapY = 8,
                        WorldX = 16, WorldY = 32, Rotation = 2,
                        TerrainType = TerrainType.GRASS, BaseHeight = 7,
                        DoorSide = NeighbourhoodLotSide.South,
                        HouseHash = 0xa1b2c3d4u, HasHouse = true
                    }
                }, world);
        }

        private static NeighbourhoodWorldState AWorld()
        {
            var world = NeighbourhoodWorldState.FromClock(null, 21);
            world.Flags = 3;
            world.ClockAvailable = true;
            world.Hours = 13;
            world.Minutes = 45;
            world.MinuteFractions = 30;
            world.TicksPerMinute = 60;
            world.DayOfMonth = 12;
            world.Month = 5;
            world.Year = 2030;
            world.Ticks = 987654321L;
            world.Transit.Add(new NeighbourhoodTransitRecord(5, 21, 31, 1, 40));
            return world;
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

        /// <summary>
        /// A schema-1 sidecar, written out by hand as the previous build's writer would have
        /// written it: no <c>worldState</c> member, the two schema stamps at 1, and this build's
        /// generator version - so it is a file that is genuinely still current, which is what the
        /// migration tests need it to be.
        /// </summary>
        private static readonly string Schema1Document =
            "{\"schemaVersion\": 1, \"version\": 1, \"generatorVersion\": "
            + NeighbourhoodLayoutBuilder.CurrentGeneratorVersion
            + ", \"slot\": 4, \"seed\": 7,"
            + " \"mapSize\": 512, \"extent\": {\"minX\": 0, \"minY\": 0, \"maxX\": 26, \"maxY\": 42},"
            + " \"lots\": [{\"number\": 21, \"zone\": 0, \"size\": 10, \"mapX\": 4, \"mapY\": 8,"
            + " \"worldX\": 16, \"worldY\": 32, \"rotation\": 2, \"terrain\": 0, \"baseHeight\": 7,"
            + " \"doorSide\": 2, \"houseHash\": \"a1b2c3d4\", \"hasHouse\": true}]}";

        private static string Platform(string unix)
        {
            //Utf8JsonWriter indents with two spaces and breaks lines with the platform's newline,
            //so a golden document written with \n is translated rather than being a pin on the
            //operating system
            return unix.Replace("\n", Environment.NewLine);
        }

        private static string[] Names(JsonElement element)
        {
            return element.EnumerateObject().Select(p => p.Name).ToArray();
        }

        // ---- schema 1 is a byte-identical prefix --------------------------------------------------

        [Fact]
        public void ALayoutWithNoWorldStateSerialisesToTheSchema1DocumentByteForByte()
        {
            //The claim this whole step rests on, pinned as bytes. If a member is added to the
            //document, removed, renamed, reordered or re-indented, this fails - which is the point:
            //a schema-2 world state that leaked into every Scope A sidecar would invalidate every
            //cached neighbourhood in every install.
            var golden = Platform(
                "{\n"
                + "  \"schemaVersion\": 2,\n"
                + "  \"version\": 2,\n"
                + "  \"generatorVersion\": 3,\n"
                + "  \"slot\": 4,\n"
                + "  \"seed\": 7,\n"
                + "  \"mapSize\": 512,\n"
                + "  \"extent\": {\n"
                + "    \"minX\": 16,\n"
                + "    \"minY\": 32,\n"
                + "    \"maxX\": 26,\n"
                + "    \"maxY\": 42\n"
                + "  },\n"
                + "  \"lots\": [\n"
                + "    {\n"
                + "      \"number\": 21,\n"
                + "      \"zone\": 0,\n"
                + "      \"size\": 10,\n"
                + "      \"mapX\": 4,\n"
                + "      \"mapY\": 8,\n"
                + "      \"worldX\": 16,\n"
                + "      \"worldY\": 32,\n"
                + "      \"rotation\": 2,\n"
                + "      \"terrain\": 0,\n"
                + "      \"baseHeight\": 7,\n"
                + "      \"doorSide\": 2,\n"
                + "      \"houseHash\": \"a1b2c3d4\",\n"
                + "      \"hasHouse\": true\n"
                + "    }\n"
                + "  ]\n"
                + "}");

            var bytes = NeighbourhoodLayoutStore.Serialize(OneLot());

            Assert.Equal(golden, Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("worldState", golden);
        }

        [Fact]
        public void AWorldStateIsOneExtraMemberAtTheEnd_AndNothingElseDiffers()
        {
            //"prefix", stated as a prefix: everything up to the end of the lots array is the same
            //bytes in both documents, which is the property a schema-1 reader depends on
            var plain = Encoding.UTF8.GetString(NeighbourhoodLayoutStore.Serialize(OneLot()));
            var withWorld = Encoding.UTF8.GetString(
                NeighbourhoodLayoutStore.Serialize(OneLot(AWorld())));

            Assert.Equal(ThroughLots(plain), ThroughLots(withWorld));
            Assert.DoesNotContain("worldState", plain);
            Assert.Contains(Environment.NewLine + "  \"worldState\": {", withWorld);
            Assert.EndsWith(Environment.NewLine + "  }" + Environment.NewLine + "}", withWorld);
        }

        /// <summary>Everything from the start of the document to the end of the lots array.</summary>
        private static string ThroughLots(string json)
        {
            const string lotsEnd = "  ]";
            var at = json.IndexOf(lotsEnd, StringComparison.Ordinal);
            return at < 0 ? json : json.Substring(0, at + lotsEnd.Length);
        }

        [Fact]
        public void TheSchema2DocumentHasTheShapeItIsDocumentedToHave()
        {
            //Member names, and their order, are pinned: the sidecar is a cache a human reads when
            //something has gone wrong, and a stable order is part of that.
            var root = JsonDocument.Parse(
                NeighbourhoodLayoutStore.Serialize(OneLot(AWorld()))).RootElement;

            Assert.Equal(new[]
            {
                "schemaVersion", "version", "generatorVersion", "slot", "seed", "mapSize",
                "extent", "lots", "worldState"
            }, Names(root));

            var world = root.GetProperty("worldState");
            Assert.Equal(new[] { "worldVersion", "flags", "activeLot", "clock", "transit" }, Names(world));
            Assert.Equal(new[]
            {
                "available", "hours", "minutes", "minuteFractions", "ticksPerMinute",
                "dayOfMonth", "month", "year", "ticks"
            }, Names(world.GetProperty("clock")));
            Assert.Equal(new[] { "sim", "origin", "destination", "mode", "progress" },
                Names(world.GetProperty("transit")[0]));

            Assert.Equal(1, world.GetProperty("worldVersion").GetInt32());
            Assert.Equal(3, world.GetProperty("flags").GetInt32());
            Assert.Equal(21, world.GetProperty("activeLot").GetInt32());
            Assert.True(world.GetProperty("clock").GetProperty("available").GetBoolean());
            Assert.Equal(987654321L, world.GetProperty("clock").GetProperty("ticks").GetInt64());

            var hop = world.GetProperty("transit")[0];
            Assert.Equal(5, hop.GetProperty("sim").GetInt32());
            Assert.Equal(21, hop.GetProperty("origin").GetInt32());
            Assert.Equal(31, hop.GetProperty("destination").GetInt32());
            Assert.Equal(1, hop.GetProperty("mode").GetInt32());
            Assert.Equal(40, hop.GetProperty("progress").GetInt32());
        }

        [Fact]
        public void AWorldWithNothingToSayOmitsTheMembersThatAreZero()
        {
            //Same rule the per-lot forward fields follow: a world state that has not been stamped
            //and has no flags and no hops writes the smallest document that still says "there was
            //a world, and here is the clock, and it was unavailable"
            var json = Encoding.UTF8.GetString(NeighbourhoodLayoutStore.Serialize(
                OneLot(NeighbourhoodWorldState.CreateDefault())));

            Assert.DoesNotContain("worldVersion", json);
            Assert.DoesNotContain("flags", json);
            Assert.DoesNotContain("transit", json);
            Assert.Contains("\"activeLot\": 0", json);
            Assert.Contains("\"available\": false", json);
        }

        [Fact]
        public void ARecordThatIsNotAHopIsNotWritten()
        {
            //origin == destination is a lot travelling to itself, and the defaulted record is the
            //degenerate case. Writing one would hand B7's resume path a hop that goes nowhere.
            var world = NeighbourhoodWorldState.CreateDefault();
            world.Transit.Add(new NeighbourhoodTransitRecord(5, 21, 21, 1, 40));
            world.Transit.Add(new NeighbourhoodTransitRecord());
            world.Transit.Add(new NeighbourhoodTransitRecord(5, 21, 31, 1, 40));

            var reloaded = NeighbourhoodLayoutStore.TryDeserialize(
                NeighbourhoodLayoutStore.Serialize(OneLot(world)), out var layout, out _);

            Assert.True(reloaded);
            Assert.Single(layout.WorldState.Transit);
            Assert.Equal(31, layout.WorldState.Transit[0].Destination);
        }

        // ---- the round trip ---------------------------------------------------------------------

        [Fact]
        public void AWorldStateSurvivesTheRoundTripFieldForField()
        {
            var world = AWorld();
            world.Transit.Add(new NeighbourhoodTransitRecord(9, 31, 21, 2, 100));

            Assert.True(NeighbourhoodLayoutStore.Save(_root, OneLot(world)));
            var reloaded = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded.WorldState);
            Assert.True(world.SameAs(reloaded.WorldState));

            var back = reloaded.WorldState;
            Assert.Equal(21, back.ActiveLot);
            Assert.Equal(3, back.Flags);
            Assert.Equal(13, back.Hours);
            Assert.Equal(45, back.Minutes);
            Assert.Equal(30, back.MinuteFractions);
            Assert.Equal(60, back.TicksPerMinute);
            Assert.Equal(12, back.DayOfMonth);
            Assert.Equal(5, back.Month);
            Assert.Equal(2030, back.Year);
            Assert.Equal(987654321L, back.Ticks);
            //Seconds is derived, exactly as INeighbourhoodClock derives it
            Assert.Equal(30, back.Seconds);
            Assert.Equal(2, back.TransitCount);
            //one hop at 40% and one that has arrived: the player's position in the world
            Assert.True(back.InTransit);
        }

        [Fact]
        public void AWorldStateWithNoClockKeepsSayingItHasNone()
        {
            //"no clock" and "midnight" are different statements, and a sidecar that cannot tell
            //them apart would move every world to midnight the first time somebody saved from the
            //neighbourhood screen
            var world = NeighbourhoodWorldState.CreateDefault();

            Assert.True(NeighbourhoodLayoutStore.Save(_root, OneLot(world)));
            var reloaded = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(reloaded.WorldState);
            Assert.False(reloaded.WorldState.ClockAvailable);
            Assert.Equal(0, reloaded.WorldState.Hours);
            Assert.Equal(0, reloaded.WorldState.Seconds);
            Assert.Equal(0, reloaded.WorldState.ActiveLot);
            Assert.Empty(reloaded.WorldState.Transit);
            Assert.Equal(0, reloaded.WorldState.Flags);
        }

        [Fact]
        public void AClockCopiedFromALiveMirrorLandsInTheSidecar()
        {
            //the shape B1's save path uses: capture the clock, stamp the lot, write it
            var mirror = new LotClockMirror(new FSO.SimAntics.VMClock
            {
                Hours = 7, Minutes = 5, MinuteFractions = 15, TicksPerMinute = 60,
                DayOfMonth = 3, Month = 6, Year = 2000, Ticks = 4242L
            });
            Assert.True(mirror.IsAvailable);

            var world = NeighbourhoodWorldState.FromClock(mirror, 21);

            Assert.True(NeighbourhoodLayoutStore.TryDeserialize(
                NeighbourhoodLayoutStore.Serialize(OneLot(world)), out var layout, out _));
            Assert.Equal(7, layout.WorldState.Hours);
            Assert.Equal(3, layout.WorldState.DayOfMonth);
            Assert.Equal(4242L, layout.WorldState.Ticks);
            Assert.Equal(21, layout.WorldState.ActiveLot);
        }

        [Fact]
        public void AnUnstampedWorldIsStampedByTheFactory_AndTheDefaultIsAFreshOneEveryTime()
        {
            //a process-wide "default" world state would be a global everybody writes to
            var first = NeighbourhoodWorldState.CreateDefault();
            var second = NeighbourhoodWorldState.CreateDefault();

            Assert.NotSame(first, second);
            first.ActiveLot = 21;
            Assert.Equal(0, second.ActiveLot);
            Assert.Equal(0, NeighbourhoodWorldState.CreateDefault().WorldVersion);
        }

        [Fact]
        public void AClockIsNotValidatedOnTheWayIn_OnlyOnTheWayOut()
        {
            //the type is a data holder: a caller that sets Hours to 25 gets 25 and can see its own
            //bug. A file that says 25 gets 23, because that is the only thing a clock can mean.
            WriteRaw(4, "{\"schemaVersion\": 2, \"generatorVersion\": 3, \"slot\": 4, \"seed\": 7,"
                + " \"lots\": [{\"number\": 21, \"size\": 10, \"worldX\": 16, \"worldY\": 32,"
                + " \"houseHash\": \"a1b2c3d4\"}],"
                + " \"worldState\": {\"clock\": {\"available\": true, \"hours\": 25, \"minutes\": 99,"
                + " \"dayOfMonth\": 0, \"month\": 44, \"ticksPerMinute\": -5, \"minuteFractions\": 900}}}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(NeighbourhoodWorldState.MaxHours, layout.WorldState.Hours);
            Assert.Equal(NeighbourhoodWorldState.MaxMinutes, layout.WorldState.Minutes);
            Assert.Equal(NeighbourhoodWorldState.MinDayOfMonth, layout.WorldState.DayOfMonth);
            Assert.Equal(NeighbourhoodWorldState.MaxMonth, layout.WorldState.Month);
            Assert.Equal(0, layout.WorldState.TicksPerMinute);
            //and the division the clock would have done is still safe
            Assert.Equal(0, layout.WorldState.Seconds);
        }

        // ---- 1 -> 2 ----------------------------------------------------------------------------

        [Fact]
        public void ASchema1SidecarLoadsAsSchema2WithNoWorldState()
        {
            WriteRaw(4, Schema1Document);

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(NeighbourhoodLayout.CurrentSchemaVersion, layout.SchemaVersion);
            Assert.True(layout.HasCurrentSchema);
            //the lots are untouched by the migration
            Assert.Equal(1, layout.LotCount);
            Assert.Equal(16, layout.Lots[0].WorldX);
            Assert.Equal(32, layout.Lots[0].WorldY);
            Assert.Equal(0xa1b2c3d4u, layout.Lots[0].HouseHash);
            //and the world is "nothing has happened", which is what a layout-only sidecar means
            Assert.NotNull(layout.WorldState);
            Assert.False(layout.WorldState.ClockAvailable);
            Assert.Equal(0, layout.WorldState.ActiveLot);
            Assert.Equal(0, layout.WorldState.Flags);
            Assert.Equal(0, layout.WorldState.TransitCount);
        }

        [Fact]
        public void MigratingASchema1SidecarWritesNothingBack()
        {
            WriteRaw(4, Schema1Document);

            Assert.NotNull(NeighbourhoodLayoutStore.Load(_root, 4));

            //the file is still a schema-1 file: the migration is free and lazy, and a player who
            //quits without simulating has paid nothing for the schema having moved
            Assert.Equal(Schema1Document, RawJson(4));
        }

        [Fact]
        public void ASchema1SidecarIsStillReusableForItsSeedAndGenerator()
        {
            //the schema bump is a statement about the file's shape, not about how lots are
            //placed, so it must not invalidate a cached generated world
            WriteRaw(4, Schema1Document);

            var reused = NeighbourhoodLayoutStore.LoadFor(_root, 4, 7,
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion);

            Assert.NotNull(reused);
            //a different seed still refuses it, exactly as it did before the bump
            Assert.Null(NeighbourhoodLayoutStore.LoadFor(_root, 4, 8,
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion));
        }

        [Fact]
        public void ASavedMigratedLayoutIsStampedSchema2()
        {
            //the migration completes on the next write, and the generator version is untouched:
            //nothing about how lots are placed changed, so a layout packed by the previous
            //generator is still valid
            WriteRaw(4, Schema1Document);
            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.True(NeighbourhoodLayoutStore.Save(_root, layout));

            var json = RawJson(4);
            Assert.Contains("\"schemaVersion\": 2", json);
            Assert.Contains("\"generatorVersion\": "
                + NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, json);
            Assert.Contains("\"worldState\"", json);
            Assert.DoesNotContain("\"schemaVersion\": 1", json);
        }

        [Fact]
        public void ASchema2FileWithNoWorldStateMemberIsAWorldlessWorldState()
        {
            //legal and expected: this build writes schema 2 for every layout, and a layout with
            //no session has no worldState member. An absent member is a default, not a failure.
            WriteRaw(4, "{\"schemaVersion\": 2, \"generatorVersion\": 3, \"slot\": 4, \"seed\": 7,"
                + " \"lots\": [{\"number\": 21, \"size\": 10, \"worldX\": 16, \"worldY\": 32,"
                + " \"houseHash\": \"a1b2c3d4\"}]}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.NotNull(layout.WorldState);
            Assert.False(layout.WorldState.ClockAvailable);
        }

        [Fact]
        public void ASchema1FileThatCarriesAWorldStateAnywayIgnoresIt()
        {
            //the downgrade-safe direction: a stamp of 1 means the file predates the member, so the
            //member is not read. A layout stamped 1 is a layout that had no session.
            WriteRaw(4, "{\"schemaVersion\": 1, \"generatorVersion\": 3, \"slot\": 4, \"seed\": 7,"
                + " \"lots\": [{\"number\": 21, \"size\": 10, \"worldX\": 16, \"worldY\": 32,"
                + " \"houseHash\": \"a1b2c3d4\"}],"
                + " \"worldState\": {\"activeLot\": 21, \"clock\": {\"available\": true, \"hours\": 9}}}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(0, layout.WorldState.ActiveLot);
            Assert.False(layout.WorldState.ClockAvailable);
        }

        // ---- what is refused -------------------------------------------------------------------

        [Fact]
        public void ASchemaThisBuildDoesNotKnowIsRefusedRatherThanReinterpreted()
        {
            foreach (var schema in new[] { 3, 4, 99, int.MaxValue })
            {
                WriteRaw(4, "{\"schemaVersion\": " + schema + ", \"generatorVersion\": 3,"
                    + " \"slot\": 4, \"seed\": 7, \"lots\": []}");

                Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
            }
        }

        [Fact]
        public void AWorldVersionThisBuildDoesNotKnowCostsTheSessionAndNotTheLayout()
        {
            //the one version stamp inside the file, and the only place a rejection is survivable:
            //the lots around it are still this seed and this generator, and both are still checked
            WriteRaw(4, "{\"schemaVersion\": 2, \"generatorVersion\": 3, \"slot\": 4, \"seed\": 7,"
                + " \"lots\": [{\"number\": 21, \"size\": 10, \"worldX\": 16, \"worldY\": 32,"
                + " \"houseHash\": \"a1b2c3d4\"}],"
                + " \"worldState\": {\"worldVersion\": 99, \"activeLot\": 21,"
                + " \"clock\": {\"available\": true, \"hours\": 9}}}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(1, layout.LotCount);
            Assert.Equal(0, layout.WorldState.ActiveLot);
            Assert.False(layout.WorldState.ClockAvailable);
        }

        [Theory]
        [InlineData("42")]
        [InlineData("\"yesterday\"")]
        [InlineData("[1, 2, 3]")]
        [InlineData("null")]
        [InlineData("{\"clock\": \"noon\"}")]
        [InlineData("{\"clock\": 7}")]
        [InlineData("{\"transit\": {}}")]
        [InlineData("{\"transit\": \"walking\"}")]
        [InlineData("{\"transit\": [42]}")]
        [InlineData("{\"transit\": [\"21 -> 31\"]}")]
        public void AWorldBodyOfTheWrongShapeIsSalvaged_AndTheLayoutIsNot(string worldState)
        {
            //the layout survives with a fresh world: the lots in the file are a pure function of
            //the seed and the generator version, and throwing them away over a bad clock would cost
            //a player their generated world to save a session
            WriteRaw(4, "{\"schemaVersion\": 2, \"generatorVersion\": 3, \"slot\": 4, \"seed\": 7,"
                + " \"lots\": [{\"number\": 21, \"size\": 10, \"worldX\": 16, \"worldY\": 32,"
                + " \"houseHash\": \"a1b2c3d4\", \"hasHouse\": true},"
                + " {\"number\": 22, \"size\": 20, \"worldX\": 64, \"worldY\": 64,"
                + " \"houseHash\": \"0000beef\"}],"
                + " \"worldState\": " + worldState + "}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(2, layout.LotCount);
            Assert.Equal(16, layout.FindLot(21).WorldX);
            Assert.NotNull(layout.WorldState);
            Assert.Equal(0, layout.WorldState.ActiveLot);
            Assert.False(layout.WorldState.ClockAvailable);
            Assert.Equal(0, layout.WorldState.TransitCount);
        }

        [Fact]
        public void ACorruptLotIsStillFatal()
        {
            //the asymmetry is deliberate and this is the half that must not move: a half-read
            //layout places some lots from one world and some from another, and looks plausible
            WriteRaw(4, "{\"schemaVersion\": 2, \"generatorVersion\": 3, \"slot\": 4, \"seed\": 7,"
                + " \"lots\": [{\"number\": 21, \"size\": 0}],"
                + " \"worldState\": {\"activeLot\": 21}}");

            Assert.Null(NeighbourhoodLayoutStore.Load(_root, 4));
        }

        [Fact]
        public void AnUnknownMemberInsideTheWorldIsIgnored_SoALaterBuildCanAddOne()
        {
            WriteRaw(4, "{\"schemaVersion\": 2, \"generatorVersion\": 3, \"slot\": 4, \"seed\": 7,"
                + " \"lots\": [{\"number\": 21, \"size\": 10, \"worldX\": 16, \"worldY\": 32,"
                + " \"houseHash\": \"a1b2c3d4\"}],"
                + " \"worldState\": {\"activeLot\": 21, \"somethingNew\": {\"nested\": [1, 2]},"
                + " \"clock\": {\"available\": true, \"hours\": 9, \"weather\": \"rain\"}}}");

            var layout = NeighbourhoodLayoutStore.Load(_root, 4);

            Assert.NotNull(layout);
            Assert.Equal(21, layout.WorldState.ActiveLot);
            Assert.Equal(9, layout.WorldState.Hours);
        }

        // ---- the patch -------------------------------------------------------------------------

        [Fact]
        public void APatchThatChangesNothingKeepsEveryLotsSessionState()
        {
            var cached = Cached(out var reads);
            MarkHollow(cached);

            var patch = NeighbourhoodLayoutPatch.Apply(cached, reads, Slot, Seed);

            Assert.False(patch.RepackedEverything);
            Assert.Equal(4, patch.KeptLots.Count);
            for (int i = 0; i < patch.Layout.LotCount; i++)
            {
                AssertHollow(patch.Layout.Lots[i]);
            }
        }

        [Fact]
        public void APatchThatMovesOneLotCarriesTheWorldAndTheOtherLotsState()
        {
            var cached = Cached(out var reads);
            MarkHollow(cached);
            cached.Lots[0].LotState = NeighbourhoodLotState.Hollow;
            var world = AWorld();

            //the lot the player edited: its house file changed, so its record is rebuilt
            reads[3].HouseHash = 0x9999u;
            var patched = WithWorld(cached, world);

            var patch = NeighbourhoodLayoutPatch.Apply(patched, reads, Slot, Seed);

            Assert.False(patch.RepackedEverything);
            //the session belongs to the neighbourhood, not to the lot that moved
            Assert.Same(world, patch.Layout.WorldState);
            //and the lots that did not move keep the state the sidecar recorded for them
            for (int i = 0; i < patch.Layout.LotCount; i++)
            {
                if (patch.Layout.Lots[i].Number == 24) continue;
                AssertHollow(patch.Layout.Lots[i]);
            }
            //the edited lot is rebuilt from LotLocations.iff, so its cached simulation state goes
            //with it - a house that was rewritten has no state worth keeping
            var edited = patch.Layout.FindLot(24);
            Assert.Equal(NeighbourhoodLotState.Unloaded, edited.LotState);
            Assert.Equal(0L, edited.LastSimulated);
            Assert.Null(edited.Entrance);
        }

        [Fact]
        public void AFirstBuildHasNoSessionToCarry()
        {
            var patch = NeighbourhoodLayoutPatch.Apply(null, Reads(), Slot, Seed);

            Assert.Null(patch.Layout.WorldState);
        }

        private const int Slot = 4;
        private const int Seed = 424242;
        private const int MapSize = 128;

        /// <summary>
        /// Four lots in the four quadrants of the 800x600 backdrop, far enough apart that on the
        /// 128-tile map these tests use
        /// <see cref="NeighbourhoodPixelTransform"/>'s diamond puts sixteen-tile rectangles a
        /// street corridor clear of one another. The four quadrants are the whole spread the
        /// backdrop has: a fixture written near a corner would put every lot on one edge of the
        /// island, where there is no drawn ground to put anything on at all.
        /// </summary>
        private static List<NeighbourhoodLotRead> Reads()
        {
            return new List<NeighbourhoodLotRead>
            {
                Read(21, 60, 60, 0x1000u),
                Read(22, 500, 60, 0x2000u),
                Read(23, 60, 500, 0x3000u),
                Read(24, 500, 500, 0x4000u),
            };
        }

        private static NeighbourhoodLotRead Read(short number, int mapX, int mapY, uint hash)
        {
            return new NeighbourhoodLotRead
            {
                Number = number, MapX = mapX, MapY = mapY, Size = 16, HasHouse = true,
                Rotation = 0, TerrainType = TerrainType.GRASS, BaseHeight = 4,
                HouseHash = hash, Zone = LotZoning.ZoneResidential
            };
        }

        /// <summary>A packed four-lot neighbourhood, and the reads it was packed from.</summary>
        private static NeighbourhoodLayout Cached(out List<NeighbourhoodLotRead> reads)
        {
            reads = Reads();
            return NeighbourhoodLayoutBuilder.Pack(reads, Slot, Seed, MapSize);
        }

        private static NeighbourhoodLayout WithWorld(NeighbourhoodLayout layout, NeighbourhoodWorldState world)
        {
            return new NeighbourhoodLayout(layout.Slot, layout.Seed, layout.GeneratorVersion,
                layout.SchemaVersion, layout.MapSize, layout.Lots, world);
        }

        /// <summary>Give every lot a hollow lot's state, so a dropped field is visible.</summary>
        private static void MarkHollow(NeighbourhoodLayout layout)
        {
            for (int i = 0; i < layout.LotCount; i++)
            {
                var lot = layout.Lots[i];
                lot.LotState = NeighbourhoodLotState.Hollow;
                lot.LastSimulated = 1000L + i;
                lot.Entrance = new NeighbourhoodLotEntrance(lot.WorldX + 1, lot.WorldY + 1, 2);
            }
        }

        private static void AssertHollow(NeighbourhoodLot lot)
        {
            Assert.Equal(NeighbourhoodLotState.Hollow, lot.LotState);
            Assert.True(lot.LastSimulated > 0L);
            Assert.NotNull(lot.Entrance);
            Assert.Equal(2, lot.Entrance.Value.Facing);
        }
    }
}
