/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FSO.SimAntics;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Hollow;
using FSO.SimAntics.Model;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The lot the player is standing in must come out of every Part B path byte-identical, and
    /// these are the tests that say so for the paths where that is checkable with no graphics
    /// device, no content manager and no running lot.
    ///
    /// <para>
    /// <b>What went wrong.</b> Three symptoms on a live lot, all of them with a Part B capability
    /// on and none of them with every flag off: nothing could be placed in build mode ("location out
    /// of bounds"), an interaction on a fridge sat in the queue and never ran, and an object stacked
    /// on another could not be clicked and the pair looked gone. The three read like one lot-level
    /// fault - bounds and object lookup and the scheduler - rather than three unrelated ones, and
    /// every Part B path that stood a VM up was audited against the active lot's
    /// <c>Context.Architecture</c>, <c>Context.Blueprint</c>, <c>Context.Terrain</c>,
    /// <c>Context.Clock</c> and <c>GlobalState</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The one write that was provably not confined to a throwaway VM</b> is pinned here.
    /// <c>VMArchitecture.Save</c> hands <c>Terrain</c>, <c>Walls</c>, <c>Floors</c> and
    /// <c>FineBuildableArea</c> to a marshal <em>by reference</em> and <c>VMArchitecture.Load</c>
    /// takes all four back by reference, so a VM built from a marshal owns the marshal's arrays.
    /// <see cref="SimitoneSurroundingsRestore"/> then writes into all four - in place - four times
    /// over per neighbour (<c>RestoreTerrain</c>, <c>RestoreHeight</c>, <c>EnsureCoreObjects</c>,
    /// <c>ApplySurroundGround</c>). The marshal on the other end is a <see cref="LotStore"/> entry
    /// published by <c>NeighbourHollowLoader</c> and held for the rest of the session, so those
    /// writes landed in a live, shared object rather than in the throwaway VM that made them.
    /// </para>
    ///
    /// <para>
    /// <b>What these can and cannot reach.</b> A <c>VMArchitecture</c> needs
    /// <c>Content.Content.Get()</c>, so the detach itself is pinned the way this repository pins
    /// everything that needs a content manager - by the pure copy helpers it is built out of, run
    /// for real, plus a read of the engine source for the aliasing the helpers exist to break.
    /// </para>
    /// </summary>
    public sealed class SimitoneSurroundingsLiveLotIsolationTests
    {
        // ====================================================================================
        // helpers
        //
        // A VMArchitecture needs Content.Content.Get(), so the parts of the engine this file has to
        // assert about cannot be run here. The repository's convention for those is to read the
        // source with its comments stripped - so a sentence in a doc comment cannot satisfy an
        // assertion about the code - and that is what Read does.
        // ====================================================================================

        private static string RestoreSource() => Read("Client", "Simitone", "Simitone.Client",
            "Neighbourhood", "SimitoneSurroundingsRestore.cs");

        private static string WorldClockSource() => Read("Client", "Simitone", "Simitone.Client",
            "Neighbourhood", "WorldClock.cs");

        private static string SimAntics(string file) => Read("FreeSO", "TSOClient",
            "tso.simantics", file);

        private static string Read(params string[] segments)
        {
            var full = Path.Combine(new[] { Root() }
                .Concat(segments).ToArray());
            Assert.True(File.Exists(full), "could not find " + full);

            var text = File.ReadAllText(full);
            var withoutBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
        }

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO", "TSOClient"))) return dir.FullName;
            }
            throw new DirectoryNotFoundException(
                "Could not find FreeSO/TSOClient above " + AppContext.BaseDirectory);
        }

        /// <summary>Only the body of one method, so an assertion about it cannot match its neighbours.</summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, "no " + signature + " in the source read");
            var depth = 0;
            var started = false;
            for (int i = at; i < source.Length; i++)
            {
                if (source[i] == '{') { depth++; started = true; }
                else if (source[i] == '}')
                {
                    depth--;
                    if (started && depth == 0) return source.Substring(at, i - at + 1);
                }
            }
            return source.Substring(at);
        }

        // ---- the sub-world attach has one gate, and it is the flag -----------------------------------

        /// <summary>
        /// There used to be a process-wide static beside the NeighbourSubWorlds flag. The screen only
        /// ever set it to true and nothing reset it, so it was never a switch; the flag is the gate.
        /// </summary>
        [Fact]
        public void TheSubWorldAttachHasNoSecondStaticGateBesideTheFlag()
        {
            var source = RestoreSource();
            Assert.DoesNotContain("SubWorldAttachEnabled", source);
            Assert.DoesNotContain("SubWorldAttachDisabledReason", source);
        }

        /// <summary>
        /// A refusal with no lot at all is still a refusal, and never reaches the gate or a VM.
        /// </summary>
        [Fact]
        public void TryRestoreWithNoLotIsARefusalAndThrowsNothing()
        {
            string reason;
            Assert.False(SimitoneSurroundingsRestore.TryRestore(null, null, out reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        // ---- the architecture arrays a neighbour must not write through ---------------------------

        /// <summary>
        /// The engine really does hand the four planes over by reference, in both directions. This
        /// is the fact <see cref="SimitoneSurroundingsRestore.DetachFromSharedArrays"/> exists to
        /// break, and it is pinned by source because a <c>VMArchitecture</c> cannot be built here.
        /// </summary>
        [Fact]
        public void TheEngineSharesTheFourArchitecturePlanesWithAMarshalByReference()
        {
            var architecture = SimAntics("VMArchitecture.cs");
            var save = MethodBody(architecture, "public virtual VMArchitectureMarshal Save()");
            var load = MethodBody(architecture, "public virtual void Load(VMArchitectureMarshal input)");

            //Save hands them over...
            foreach (var plane in new[] { "Terrain = Terrain", "Walls = Walls",
                "Floors = Floors", "FineBuildableArea = FineBuildableArea" })
            {
                Assert.Contains(plane, save);
            }
            //...and Load takes the same four back the same way.
            foreach (var plane in new[] { "Terrain = input.Terrain", "Walls = input.Walls",
                "Floors = input.Floors", "FineBuildableArea = input.FineBuildableArea" })
            {
                Assert.Contains(plane, load);
            }
        }

        /// <summary>
        /// A hollow lot's contents are the shared ones, and a blank lot's are not - so the detach
        /// runs on the hollow branch only.
        /// </summary>
        [Fact]
        public void AHollowLotIsTheOneThatSharesItsArchitectureWithAMarshal()
        {
            Assert.False(SimitoneSurroundingsRestore.SharesArchitectureWithMarshal(null));
            //A marshal that is not a lot's contents at all.
            Assert.False(SimitoneSurroundingsRestore.SharesArchitectureWithMarshal(
                new VMHollowMarshal()));
            //A lot's contents, which is what LotStore hands the bridge.
            Assert.True(SimitoneSurroundingsRestore.SharesArchitectureWithMarshal(
                new VMHollowMarshal
                {
                    Context = new VMContextMarshal
                    {
                        Architecture = new VMArchitectureMarshal(19)
                    }
                }));

            var source = RestoreSource();
            //Called after HollowLoad, which is the line that installs the shared arrays...
            var load = source.IndexOf("tempVM.HollowLoad(hollow);", StringComparison.Ordinal);
            var detach = source.IndexOf("DetachFromSharedArrays(tempVM.Context?.Architecture)",
                StringComparison.Ordinal);
            Assert.True(load > 0, "BuildHollow no longer hollow-loads");
            Assert.True(detach > load, "the detach must come after HollowLoad and before any write");
            //...and before the first of the four writers.
            foreach (var writer in new[]
            {
                "VMLotTerrainRestoreTools.RestoreTerrain(",
                "VMLotTerrainRestoreTools.RestoreHeight(",
                "ApplySurroundGround(tempVM, neighbour)",
                "VMLotTerrainRestoreTools.EnsureCoreObjects("
            })
            {
                Assert.True(source.IndexOf(writer, StringComparison.Ordinal) > detach,
                    "'" + writer + "' runs before the architecture is detached");
            }
        }

        /// <summary>
        /// The detach re-signals the terrain as well as the walls. Only
        /// <c>VMArchitecture.Tick</c>'s terrain branch re-points the blueprint's
        /// <c>TerrainComponent</c> - whose <c>GroundHeight</c> has no setter - at this
        /// architecture's terrain, and it is gated on <c>TerrainDirty</c>, which
        /// <c>SignalAllDirty</c> alone does not set. Without it the sub-world goes on drawing the
        /// marshal's array while the restore writes the copy's, and the ground merge stops merging
        /// without anything failing.
        /// </summary>
        [Fact]
        public void TheDetachReSignalsTheTerrainAndNotOnlyTheWalls()
        {
            var body = MethodBody(RestoreSource(),
                "public static void DetachFromSharedArrays(VMArchitecture architecture)");

            Assert.Contains("architecture.Terrain = CloneTerrain(architecture.Terrain);", body);
            Assert.Contains("architecture.Walls = CloneLevels(architecture.Walls);", body);
            Assert.Contains("architecture.Floors = CloneLevels(architecture.Floors);", body);
            Assert.Contains("architecture.FineBuildableArea", body);
            //Derived data rebuilt rather than carried over from the source.
            Assert.Contains("architecture.RegenWallsAt();", body);
            Assert.Contains("architecture.Terrain.RegenerateCenters()", body);
            Assert.Contains("architecture.SignalTerrainRedraw();", body);
            Assert.Contains("architecture.SignalAllDirty();", body);
            //The signal has to come after the terrain is replaced, or the tick re-binds the old one.
            Assert.True(body.IndexOf("SignalTerrainRedraw", StringComparison.Ordinal)
                > body.IndexOf("CloneTerrain", StringComparison.Ordinal));
        }

        // ---- the copies themselves, run for real ----------------------------------------------------

        /// <summary>
        /// Copying a lot's terrain and then writing the copy leaves the original exactly as it was.
        /// This is the whole of the fix for a neighbour's terrain, run rather than described.
        /// </summary>
        [Fact]
        public void WritingACopiedTerrainLeavesTheOriginalAlone()
        {
            var original = new VMArchitectureTerrain(4, 4);
            for (int i = 0; i < original.Heights.Length; i++)
            {
                original.Heights[i] = (short)(100 + i);
                original.GrassState[i] = (byte)(i % 5);
                original.Sloped[i] = i % 2 == 0;
                original.VisHeights[i] = (short)(200 + i);
                original.VisGrass[i] = (byte)(i % 7);
            }
            original.Version = 19;

            var copy = SimitoneSurroundingsRestore.CloneTerrain(original);
            Assert.NotNull(copy);
            Assert.NotSame(original, copy);

            //The shapes and the identity of the lot's terrain, not just its arrays.
            Assert.Equal(original.Width, copy.Width);
            Assert.Equal(original.Height, copy.Height);
            Assert.Equal(original.LightType, copy.LightType);
            Assert.Equal(original.DarkType, copy.DarkType);
            Assert.Equal(original.Version, copy.Version);

            //Every array is a different object. A clone that shared even one of them is the bug
            //this test was written for, so each is asked separately rather than as a group.
            Assert.NotSame(original.Heights, copy.Heights);
            Assert.NotSame(original.GrassState, copy.GrassState);
            Assert.NotSame(original.Sloped, copy.Sloped);
            Assert.NotSame(original.VisHeights, copy.VisHeights);
            Assert.NotSame(original.VisGrass, copy.VisGrass);
            //Centers is derived from Heights and is deliberately not copied.
            Assert.NotSame(original.Centers, copy.Centers);

            //and the contents came across before anything moved.
            Assert.Equal(original.Heights, copy.Heights);
            Assert.Equal(original.GrassState, copy.GrassState);
            Assert.Equal(original.Sloped, copy.Sloped);
            Assert.Equal(original.VisHeights, copy.VisHeights);
            Assert.Equal(original.VisGrass, copy.VisGrass);

            //Now write the copy the way the restore writes a neighbour's ground, in place.
            var field = new short[copy.Heights.Length];
            for (int i = 0; i < field.Length; i++) field[i] = (short)(500 + i);
            Array.Copy(field, copy.Heights, field.Length);
            copy.GrassState[0] = 200;
            copy.Sloped[3] = true;
            copy.RegenerateCenters();

            //The marshal's terrain is untouched, byte for byte.
            for (int i = 0; i < original.Heights.Length; i++)
            {
                Assert.Equal((short)(100 + i), original.Heights[i]);
                Assert.Equal((byte)(i % 5), original.GrassState[i]);
                Assert.Equal(i % 2 == 0, original.Sloped[i]);
                Assert.Equal((short)(200 + i), original.VisHeights[i]);
                Assert.Equal((byte)(i % 7), original.VisGrass[i]);
            }
            //Which is the whole point: a second build of the same lot starts from the lot's own
            //terrain and not from one that has already been restored and re-merged.
            Assert.Equal((short)500, copy.Heights[0]);
            Assert.Equal((short)100, original.Heights[0]);
        }

        /// <summary>
        /// A storey-by-storey copy shares no array with the original at <em>any</em> level - which is
        /// exactly where a shallow copy of the outer array goes wrong and leaves every storey shared.
        /// </summary>
        [Fact]
        public void CopyingAStoreyByStoreyPlaneSharesNoLevelWithTheOriginal()
        {
            var original = new short[5][];
            for (int level = 0; level < original.Length; level++)
            {
                original[level] = new short[4];
                for (int i = 0; i < 4; i++) original[level][i] = (short)(level * 10 + i);
            }

            var copy = SimitoneSurroundingsRestore.CloneLevels(original);
            Assert.NotSame(original, copy);
            Assert.Equal(original.Length, copy.Length);
            for (int level = 0; level < original.Length; level++)
            {
                Assert.NotSame(original[level], copy[level]);
                Assert.Equal(original[level], copy[level]);
            }

            //Write every level of the copy, the way RestoreTerrain stamps walls and floors.
            for (int level = 0; level < copy.Length; level++)
            {
                for (int i = 0; i < copy[level].Length; i++) copy[level][i] = 999;
            }

            for (int level = 0; level < original.Length; level++)
            {
                for (int i = 0; i < original[level].Length; i++)
                {
                    Assert.Equal((short)(level * 10 + i), original[level][i]);
                }
            }

            //Null in, null out - and a null level inside a non-null set is left null rather than
            //invented, so a marshal with a storey it never filled round-trips unchanged.
            Assert.Null(SimitoneSurroundingsRestore.CloneLevels<short>(null));
            var gappy = new short[2][];
            gappy[0] = new short[1];
            gappy[1] = null;
            var gappyCopy = SimitoneSurroundingsRestore.CloneLevels(gappy);
            Assert.NotNull(gappyCopy[0]);
            Assert.Null(gappyCopy[1]);
            Assert.Null(SimitoneSurroundingsRestore.CloneTerrain(null));
        }

        // ---- the clock handshake -------------------------------------------------------------------

        /// <summary>
        /// The §5.2 handshake writes into the live lot's <c>VMClock</c>, and it never hands that lot
        /// a rate of zero - which <c>VMClock.Seconds</c> divides by and <c>VMClock.Tick</c>
        /// compares against, both unguarded.
        /// </summary>
        [Fact]
        public void TheClockHandshakeNeverZeroesTheLotsRate()
        {
            //A world clock that has never seen a lot, so its rate is the class default.
            var world = new WorldClock();
            var lot = new VMClock { TicksPerMinute = WorldClock.TicksPerMinuteTs1 };
            world.PushTo(lot);
            Assert.Equal(WorldClock.TicksPerMinuteTs1, lot.TicksPerMinute);

            //And a world clock whose rate is somehow not positive still will not, which is the
            //whole point of the guard: the write is into somebody else's object on a lot load.
            var source = WorldClockSource();
            Assert.Contains("if (_ticksPerMinute > 0) clock.TicksPerMinute = _ticksPerMinute;",
                MethodBody(source, "public void PushTo(VMClock clock)"));

            //A world clock that has ticked before it had ever pulled from a lot still rolls minutes
            //at the right rate, which is what the scratch clock's own field is initialised for.
            var fresh = new WorldClock();
            for (int i = 0; i < WorldClock.TicksPerMinuteTs1; i++) fresh.Tick();
            Assert.Equal(1, fresh.Minutes);
            Assert.Equal(0, fresh.MinuteFractions);

            //And the handshake reads the lot's rate rather than inventing one, so the two cannot
            //drift: pull from a lot at a rate of its own, push to another, and it is that lot's.
            var source1 = new VMClock { TicksPerMinute = 150, Hours = 4 };
            var source2 = new VMClock { TicksPerMinute = 150, Hours = 5 };
            world.PullFrom(source1);
            world.PushTo(source2);
            Assert.Equal(150, source2.TicksPerMinute);
            Assert.Equal(4, source2.Hours);
        }

        /// <summary>
        /// The handshake reaches the lot's clock and nothing else on the VM: no globals, no
        /// architecture, no blueprint, no terrain. Comments are already stripped by the reader, so
        /// this is an assertion about code rather than about the prose that promises it.
        /// </summary>
        [Fact]
        public void TheHandshakeTouchesNothingOnTheLiveLotButItsClock()
        {
            var source = WorldClockSource();
            foreach (var method in new[] { "public void PullFrom(VMClock clock)",
                "public void PushTo(VMClock clock)" })
            {
                var body = MethodBody(source, method);
                Assert.DoesNotContain("GlobalState", body);
                Assert.DoesNotContain("Architecture", body);
                Assert.DoesNotContain("Blueprint", body);
                Assert.DoesNotContain("Terrain", body);
                Assert.DoesNotContain("Entities", body);
                Assert.DoesNotContain("ObjectQueries", body);
            }
        }
    }
}
