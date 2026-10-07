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
    /// B7 as the lifecycle does it: a refresh reads a session, keeps it across whatever it did to
    /// the layout, and writes it back - and with the flag off it does none of that, byte for byte.
    ///
    /// <para>
    /// <b>What is worth pinning here, and why it is not the store's tests.</b>
    /// <see cref="NeighbourhoodWorldStateTests"/> already owns the document: that schema 1 is a
    /// byte-identical prefix, that a schema-1 file comes back as schema 2, and that a corrupt world
    /// body is salvaged rather than fatal. This file owns the two things that can put a
    /// <c>worldState</c> member into a file that must not have one, or take a session out of a file
    /// that had one:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>The gate, both ways.</b> <see cref="NeighbourhoodWorldFlag.Persist"/> off: the
    ///     session in a hand-written sidecar is neither handed to a caller nor written back - the
    ///     refreshed file has the eight members schema 1 had and no ninth. On: the same
    ///     neighbourhood keeps its clock, its active lot and its hop through a <em>full repack</em>
    ///     and through the <em>cheap patch</em>, which are the two arrangements a refresh can
    ///     arrive at.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The seam between a layout and a session.</b> A world cached while the flag was off
    ///     meets a sidecar that now has a session, and the panel's cache hit - which reads no file
    ///     of its own - still has to be able to say what time it is. That is the one place the
    ///     in-memory world and the file can meet, and it is why
    ///     <see cref="NeighbourhoodLifecycle.CachedWorldFor"/> is not just a comparison.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Why this class is serialised against the whole suite.</b> The capability switches are
    /// process-wide and cached in a static, and so is the generated-world cache these tests assert
    /// through: a test that turns B7 on changes what every other class sees until it is turned off
    /// again, and a test that leaves a world in the cache can be served somebody else's answer.
    /// Both are the hazard <c>NonParallelCollection</c> exists for, and both are set up and put
    /// back in the constructor and <see cref="Dispose"/> so a failure halfway through cannot leave
    /// either one behind.
    /// </para>
    ///
    /// <para>
    /// No provider, no game content, no graphics device and no <c>UserData</c> folder: a temp
    /// directory, four canned lot records, and the real 512x512 generator where the claim needs a
    /// real refresh. Only the merge rule itself is tested without generating a raster, because
    /// that rule is a value and a value should not need a quarter of a million tiles to check.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class NeighbourhoodWorldPersistenceLifecycleTests : IDisposable
    {
        private const int Slot = 3;
        private const int Mode = 3;

        private readonly string _root;
        private readonly int _seed;

        public NeighbourhoodWorldPersistenceLifecycleTests()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "simitone-neigh-b7-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _seed = NeighbourhoodSceneSource.SeedForMode(Slot);

            NeighbourhoodWorldCache.Clear();
            NeighbourhoodLifecycle.Forget();
            Flags(false);
        }

        public void Dispose()
        {
            //Flags first: a flag left on is the one piece of state here that outlives the temp
            //directory, and the class next to run must not inherit a B7 it did not ask for.
            Flags(false);
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

        // ---- the gate, off ---------------------------------------------------------------------

        [Fact]
        public void WithTheFlagOffARefreshNeitherExposesNorRewritesTheSession()
        {
            //A current sidecar that says a session, and a refresh of a neighbourhood that has not
            //changed. Everything about the plan is the cheap one, so the only thing that can put a
            //worldState member into this file - or hand one to a caller - is the lifecycle.
            WriteSidecar(FourLots(), _seed, ASession());

            var world = Refresh(FourLots());

            Assert.True(world.HasWorld);
            Assert.Null(world.WorldState);
            //and not merely hidden: the layout the world reports carries none either, so there is
            //no session object floating through a Part A world for a caller to find another way
            Assert.Null(world.Layout.WorldState);

            var json = File.ReadAllText(SidecarPath());
            Assert.DoesNotContain("worldState", json);
            Assert.Equal(new[]
            {
                "schemaVersion", "version", "generatorVersion", "slot", "seed",
                "mapSize", "extent", "lots"
            }, Members(json));
        }

        [Fact]
        public void WithTheFlagOffASessionCannotBeSaved()
        {
            WriteSidecar(FourLots(), _seed, null);
            var before = File.ReadAllBytes(SidecarPath());

            var saved = NeighbourhoodLifecycle.SaveWorldState(_root, Slot, ASession());

            Assert.False(saved);
            Assert.Equal(before, File.ReadAllBytes(SidecarPath()));
        }

        [Fact]
        public void WithTheFlagOffTheMergeStripsTheSessionAPatchCarried()
        {
            //The rule on its own, with no refresh around it, and the pair of branches that together
            //keep a Part A sidecar a Part A sidecar. The patcher carries the session forward for its
            //own reasons - a repaired layout should not lose the player's clock - and the writer
            //takes it back off again. Both halves are asserted, because either one on its own would
            //produce a file that silently gained a member.
            Flags(false);
            var session = ASession();
            var previous = WithWorld(NeighbourhoodLayoutBuilder.Pack(FourLots(), Slot, _seed), session);

            var patched = NeighbourhoodLayoutPatch.Apply(previous, FourLots(), Slot, _seed);
            Assert.Same(session, patched.Layout.WorldState);

            var written = NeighbourhoodLifecycle.LayoutToWrite(patched.Layout, previous);

            Assert.Null(written.WorldState);
            //and the arrangement is untouched by the stripping, which is the other half of the
            //claim: this is the same neighbourhood, not a rebuilt one
            Assert.Equal(patched.Layout.LotCount, written.LotCount);
            Assert.Equal(patched.Layout.Seed, written.Seed);
            Assert.Equal(patched.Layout.Lots[0].WorldX, written.Lots[0].WorldX);
            Assert.Same(patched.Layout.Lots[0], written.Lots[0]);
        }

        // ---- the gate, on ----------------------------------------------------------------------

        [Fact]
        public void WithTheFlagOnTheSessionSurvivesAFullRepack()
        {
            //A sidecar packed for a different seed, so the plan is §4.2's "stale": every lot record
            //is rebuilt from LotLocations.iff and not one of them survives. The session belongs to
            //the neighbourhood, not to the arrangement, so it has to.
            Flags(true);
            WriteSidecar(FourLots(), _seed + 1, ASession());

            var world = Refresh(FourLots());

            Assert.True(world.HasWorld);
            Assert.Equal(NeighbourhoodRefreshKind.SidecarStale, world.Plan.Kind);
            Assert.Equal(_seed, world.Layout.Seed);
            Assert.NotNull(world.WorldState);
            Assert.True(ASession().SameAs(world.WorldState));
            Assert.True(world.WorldState.InTransit);

            var reloaded = NeighbourhoodLayoutStore.Load(_root, Slot);
            Assert.Equal(_seed, reloaded.Seed);
            Assert.True(ASession().SameAs(reloaded.WorldState));
        }

        [Fact]
        public void WithTheFlagOnTheCheapPatchKeepsTheSessionToo()
        {
            //The other arrangement: the layout as a whole is still valid and only one lot is
            //re-placed. The plan says so, which is what makes this a different test from the one
            //above rather than a slower copy of it.
            Flags(true);
            WriteSidecar(FourLots(), _seed, ASession());
            var edited = FourLots();
            edited[3].HouseHash = 0x9999u;

            var world = Refresh(edited);

            Assert.Equal(NeighbourhoodRefreshKind.LotsChanged, world.Plan.Kind);
            Assert.True(world.Plan.PatchOnlyChangedLots);
            Assert.True(ASession().SameAs(world.WorldState));
            Assert.True(ASession().SameAs(NeighbourhoodLayoutStore.Load(_root, Slot).WorldState));
        }

        [Fact]
        public void ACorruptWorldBodyStillLoadsTheLayoutAndTheWorld()
        {
            //B7's recovery clause, end to end rather than at the file boundary: a world body of
            //the wrong shape costs the player their session and nothing else. A layout that would
            //not parse is still refused outright - that is the store's rule and this does not
            //weaken it.
            Flags(true);
            WriteSidecar(FourLots(), _seed, null, "\"worldState\": 7");

            var world = Refresh(FourLots());

            Assert.True(world.HasWorld);
            Assert.Equal(4, world.Layout.LotCount);
            Assert.NotNull(world.WorldState);
            Assert.False(world.WorldState.ClockAvailable);
            Assert.Equal(0, world.WorldState.ActiveLot);
            Assert.Equal(0, world.WorldState.TransitCount);

            //and the file it leaves behind is a good one, so the second visit is not a second
            //recovery: the world body is an object again, and it still says that nothing has
            //happened - no clock, no active lot, no hop. Not a field-for-field round trip, and it
            //should not be read as one: the store clamps a hand-edited clock into the range a
            //clock can be asked for, so the date a session that never had one comes back as the
            //first of the first month rather than the zero that went in.
            var reloaded = NeighbourhoodLayoutStore.Load(_root, Slot);
            Assert.NotNull(reloaded.WorldState);
            Assert.False(reloaded.WorldState.ClockAvailable);
            Assert.Equal(0, reloaded.WorldState.ActiveLot);
            Assert.Equal(0, reloaded.WorldState.TransitCount);
            Assert.Contains("worldState", File.ReadAllText(SidecarPath()));
        }

        [Fact]
        public void TheCachedWorldLearnsTheSessionFromTheSidecar()
        {
            //A world built while B7 was off, a session saved afterwards, and then a panel attach
            //that is answered entirely from the cache. Nothing in that last step reads a file except
            //the comparison, so without the carry the caller would be told the neighbourhood has no
            //clock at all.
            var built = Refresh(FourLots());
            NeighbourhoodWorldCache.Store(built);
            Assert.Null(built.WorldState);

            Flags(true);
            Assert.True(NeighbourhoodLifecycle.SaveWorldState(_root, Slot, ASession()));

            //the save reached the world already in memory, so a caller holding the cache entry
            //rather than a fresh read agrees with the file
            var cached = NeighbourhoodWorldCache.Peek(Slot);
            Assert.True(ASession().SameAs(cached.WorldState));

            var hit = NeighbourhoodLifecycle.CachedWorldFor(_root, Slot);
            Assert.NotNull(hit);
            Assert.Same(built.Map, hit.Map);
            Assert.True(ASession().SameAs(hit.WorldState));
            Assert.Same(built.Layout.Lots[0], hit.Layout.Lots[0]);
        }

        [Fact]
        public void ASavedSessionDoesNotInvalidateTheRaster()
        {
            //The rule SameLayout rests on, stated as a test because getting it wrong is expensive
            //and silent: a session is not an arrangement, so writing one must not cost a
            //regeneration.
            Flags(true);
            var world = Refresh(FourLots());
            NeighbourhoodWorldCache.Store(world);

            Assert.True(NeighbourhoodLifecycle.SaveWorldState(_root, Slot, ASession()));
            var hit = NeighbourhoodWorldCache.Peek(Slot);

            Assert.True(hit.HasWorld);
            Assert.Same(world.Map, hit.Map);
        }

        // ---- the merge, on its own --------------------------------------------------------------

        [Fact]
        public void WithTheFlagOnTheMergePrefersTheLayoutsOwnSession()
        {
            Flags(true);
            var fresh = NeighbourhoodLayoutBuilder.Pack(FourLots(), Slot, _seed);
            var cached = WithWorld(NeighbourhoodLayoutBuilder.Pack(FourLots(), Slot, _seed), ASession());
            var mine = ASession();
            var theirs = cached.WorldState;
            theirs.ActiveLot = 31;

            //what the patcher built wins: it is the one the caller is holding
            var carried = NeighbourhoodLifecycle.LayoutToWrite(
                NeighbourhoodLifecycle.WithWorldState(fresh, mine), cached);

            Assert.Same(mine, carried.WorldState);

            //a full repack has none of its own and takes the sidecar's
            var repacked = NeighbourhoodLifecycle.LayoutToWrite(fresh, cached);
            Assert.Same(theirs, repacked.WorldState);

            //and a neighbourhood nobody has played in yet still has somewhere to write a session.
            //It is unstamped, which is what "nothing has happened" is: the world version is the
            //only stamp and there is no shape to stamp until something has been simulated.
            var virgin = NeighbourhoodLifecycle.LayoutToWrite(fresh, null);
            Assert.NotNull(virgin.WorldState);
            Assert.Equal(0, virgin.WorldState.ActiveLot);
            Assert.Equal(0, virgin.WorldState.WorldVersion);
        }

        [Fact]
        public void StampingASessionChangesNothingButTheSession()
        {
            //Pure, and the reason WithWorldState can be a rule rather than a save path: same slot,
            //same seed, same lots, the same instances even - a lot record is shared with the layout
            //it was built from, and this must not quietly copy or replace it.
            var layout = NeighbourhoodLayoutBuilder.Pack(FourLots(), Slot, _seed);
            var world = ASession();

            var stamped = NeighbourhoodLifecycle.WithWorldState(layout, world);

            Assert.NotSame(layout, stamped);
            Assert.Same(layout.Lots[0], stamped.Lots[0]);
            Assert.Equal(layout.Seed, stamped.Seed);
            Assert.Equal(layout.GeneratorVersion, stamped.GeneratorVersion);
            Assert.Equal(layout.SchemaVersion, stamped.SchemaVersion);
            Assert.Equal(layout.MapSize, stamped.MapSize);
            Assert.Same(world, stamped.WorldState);

            //and the ordinary case allocates nothing
            Assert.Same(stamped, NeighbourhoodLifecycle.WithWorldState(stamped, world));
            Assert.Null(NeighbourhoodLifecycle.WithWorldState(null, world));
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static void Flags(bool on)
        {
            //Reset first, so the six other capabilities come from wherever the flags file happens
            //to point rather than from whatever the previous test left cached, and Persist itself
            //is then set explicitly: nothing here depends on a developer's simitone.ini.
            NeighbourhoodWorldFlags.Reset();
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Persist, on, save: false);
        }

        /// <summary>The worker's own pipeline, and the only place a session is written by a refresh.</summary>
        private NeighbourhoodGeneratedWorld Refresh(IReadOnlyList<NeighbourhoodLotRead> reads)
        {
            return NeighbourhoodLifecycle.RefreshBlocking(
                _root, Slot, Mode, NeighbourhoodView.World3D, Source(reads));
        }

        private string SidecarPath()
        {
            return NeighbourhoodLayoutStore.PathFor(_root, Slot);
        }

        private static NeighbourhoodLayout WithWorld(
            NeighbourhoodLayout layout, NeighbourhoodWorldState world)
        {
            return new NeighbourhoodLayout(layout.Slot, layout.Seed, layout.GeneratorVersion,
                layout.SchemaVersion, layout.MapSize, layout.Lots, world);
        }

        /// <summary>
        /// A sidecar packed by hand, so the tests can start from a file with a session in it rather
        /// than from a session one of these tests created. <paramref name="rawWorldState"/> is a
        /// <c>worldState</c> member written as text, for the one case a well-formed session cannot
        /// express: a body the store has to salvage.
        /// </summary>
        private void WriteSidecar(IReadOnlyList<NeighbourhoodLotRead> reads, int seed,
            NeighbourhoodWorldState world, string rawWorldState = null)
        {
            var packed = NeighbourhoodLayoutBuilder.Pack(reads, Slot, seed);
            var path = SidecarPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (rawWorldState == null)
            {
                NeighbourhoodLayoutStore.Save(_root,
                    NeighbourhoodLifecycle.WithWorldState(packed, world));
                return;
            }

            //the same document with one more member at the end, which is all schema 2 ever was
            var text = Encoding.UTF8.GetString(NeighbourhoodLayoutStore.Serialize(packed));
            var close = text.LastIndexOf('}');
            File.WriteAllText(path, text.Substring(0, close).TrimEnd('\r', '\n')
                + ",\n  " + rawWorldState + "\n}", new UTF8Encoding(false));
        }

        private static FakeLotContentSource Source(IReadOnlyList<NeighbourhoodLotRead> reads)
        {
            var source = new FakeLotContentSource();
            foreach (var read in reads)
            {
                source.Lines.Add(read.Number + "," + read.MapX + "," + read.MapY);
                source.Lots[read.Number] = read;
            }
            return source;
        }

        private static string[] Members(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            }
        }

        /// <summary>
        /// A session worth losing: a clock, the lot the player is standing in, and a hop half way.
        /// <see cref="NeighbourhoodWorldState.SameAs"/> compares all of it, so a merge that kept
        /// the object but reset a field would fail.
        /// </summary>
        private static NeighbourhoodWorldState ASession()
        {
            var world = NeighbourhoodWorldState.FromClock(null, 21);
            world.ClockAvailable = true;
            world.Hours = 13;
            world.Minutes = 45;
            world.TicksPerMinute = 60;
            world.DayOfMonth = 12;
            world.Month = 5;
            world.Year = 2030;
            world.Ticks = 987654321L;
            world.Transit.Add(new NeighbourhoodTransitRecord(5, 21, 31, 1, 40));
            return world;
        }

        /// <summary>
        /// Four lots a hundred pixels apart on the backdrop, each with its own house hash: sixteen
        /// tiles of rectangle apiece, clear of every neighbour with a street corridor between, which
        /// is what the road graph needs to lay one connected network.
        /// </summary>
        private static List<NeighbourhoodLotRead> FourLots()
        {
            return new List<NeighbourhoodLotRead>
            {
                Read(21, 20, 20, 0x1000u),
                Read(22, 120, 20, 0x2000u),
                Read(23, 20, 120, 0x3000u),
                Read(24, 120, 120, 0x4000u),
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
    }
}
