using FSO.Content;
using FSO.Files.Formats.IFF;
using FSO.SimAntics;
using FSO.SimAntics.Engine;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMRoutineResolver"/> - the one routine-resolution rule, and the
    /// <c>SemiGlobal</c> null-guard that used to be in one of its three copies.
    ///
    /// <para>
    /// A bhav id is banded: below 4096 the game globals, 4096..8191 the code owner's own trees,
    /// 8192 and up the semi-globals reached through the owner's <c>GLOB</c> chunk. That rule was
    /// written out three times - <c>VMThread.ExecuteSubRoutine</c>, <c>VMStackFrame.Load</c> and
    /// <c>VMTS1ActivatorNew.GetRoutine</c> - and the guard that makes the semi-global band safe
    /// was only ever applied to the first.
    /// </para>
    ///
    /// <para>
    /// The guard matters because <c>SemiGlobal</c> is assigned only by the <c>GLOB</c> lookup in
    /// <c>GameObjectResource</c>'s constructor, so it is null for every GLOB-less object, and the
    /// retail trees call the 0x7FFF/0xFFFF "no such tree" sentinel from GLOB-less objects in bulk:
    /// 131 sites over 46 objects across the 11 TS1 fars. The two unguarded copies made a
    /// lot-load <c>NullReferenceException</c> out of it - one of them on every stack frame of
    /// every restored entity, the other on a save naming a semi-global tree.
    /// </para>
    ///
    /// <para>
    /// A missing routine - missing <c>GLOB</c>, missing resource, or a sentinel - is answered with
    /// null, which is the path the private and global bands have always taken and what
    /// <c>ExecuteSubRoutine</c> does with it. These tests drive the rule with stub resources, so
    /// no lot, no content manager and no graphics device is needed; the retail meaning of the
    /// sentinel is not recoverable from static data and is not what is being asserted.
    /// </para>
    /// </summary>
    public sealed class VMRoutineResolverTests
    {
        /// <summary>A resource that owns exactly the routine ids it is handed.</summary>
        private sealed class StubResource : GameIffResource
        {
            public StubResource(params ushort[] routineIds)
            {
                RoutineCache = new Dictionary<ushort, object>();
                foreach (var id in routineIds)
                {
                    RoutineCache[id] = new VMRoutine { ID = id };
                }
            }

            public override IffFile MainIff => null;
            public override T Get<T>(ushort id) => default;
            public override List<T> List<T>() => null;
        }

        /// <summary>
        /// A semi-global. <c>SemiGlobal</c> is typed as the concrete <see cref="GameGlobalResource"/>
        /// rather than the abstract base, so the only way to make one headlessly is a real one over
        /// an empty IFF - which recaches to an empty <c>RoutineCache</c> and is then filled in.
        /// </summary>
        private static GameGlobalResource SemiGlobalOwning(params ushort[] routineIds)
        {
            var semiGlobal = new GameGlobalResource(new IffFile(), null);
            foreach (var id in routineIds)
            {
                semiGlobal.RoutineCache[id] = new VMRoutine { ID = id };
            }
            return semiGlobal;
        }

        // ---- the bands -------------------------------------------------------------------

        [Theory]
        [InlineData(0, VMRoutineResolver.Band.Global)]
        [InlineData(4095, VMRoutineResolver.Band.Global)]
        [InlineData(4096, VMRoutineResolver.Band.Private)]
        [InlineData(8191, VMRoutineResolver.Band.Private)]
        [InlineData(8192, VMRoutineResolver.Band.SemiGlobal)]
        [InlineData(0x7FFF, VMRoutineResolver.Band.SemiGlobal)]
        [InlineData(0xFFFF, VMRoutineResolver.Band.SemiGlobal)]
        public void EachIdResolvesInTheBandItNames(int routineID, VMRoutineResolver.Band expected)
        {
            // The two edges are the whole rule: 4096 is the first private tree, 8192 the first
            // semi-global one. Stated as data so the next edit cannot quietly move a boundary.
            Assert.Equal(expected, VMRoutineResolver.BandOf((ushort)routineID));
        }

        [Fact]
        public void TheSentinelsAreInTheSemiGlobalBandAndSoCanOnlyEverBeAMiss()
        {
            // This is why "a sentinel resolves to null" needs no sentinel-specific case: there is
            // no band in which a sentinel could succeed.
            Assert.True(VMRoutineResolver.IsSentinel(0x7FFF));
            Assert.True(VMRoutineResolver.IsSentinel(0xFFFF));
            Assert.Equal(VMRoutineResolver.Band.SemiGlobal, VMRoutineResolver.BandOf(0x7FFF));
            Assert.Equal(VMRoutineResolver.Band.SemiGlobal, VMRoutineResolver.BandOf(0xFFFF));

            // ... and 8192, the first real semi-global id, is not a sentinel.
            Assert.False(VMRoutineResolver.IsSentinel(8192));
            Assert.False(VMRoutineResolver.IsSentinel(VMRoutineResolver.PrivateBand));
        }

        // ---- each band answers from the resource that owns it ---------------------------

        [Fact]
        public void APrivateTreeComesFromTheCodeOwner()
        {
            var owner = new StubResource(4096, 4097);

            Assert.Equal((ushort)4096, VMRoutineResolver.Resolve(owner, new StubResource(4096), 4096).ID);
            Assert.Null(VMRoutineResolver.Resolve(owner, new StubResource(4096), 4098));
        }

        [Fact]
        public void AGlobalTreeComesFromTheGlobalsAndNotFromTheCodeOwner()
        {
            var owner = new StubResource(100);
            var globals = new StubResource(100, 200);

            // The same id owned by both must resolve to the globals' copy - the banding is the
            // point, and a resolver that merged the two would make every private tree reachable
            // by its number.
            var resolved = VMRoutineResolver.Resolve(owner, globals, 100);
            Assert.Equal((ushort)100, resolved.ID);
            Assert.Same(globals.RoutineCache[100], resolved);
            Assert.Equal((ushort)200, VMRoutineResolver.Resolve(owner, globals, 200).ID);
        }

        [Fact]
        public void ASemiGlobalTreeComesFromTheGlobChunkAndNotFromTheCodeOwner()
        {
            var owner = new StubResource(8200);
            owner.SemiGlobal = SemiGlobalOwning(8200);

            var resolved = VMRoutineResolver.Resolve(owner, new StubResource(8200), 8200);
            Assert.Equal((ushort)8200, resolved.ID);
            Assert.Same(owner.SemiGlobal.RoutineCache[8200], resolved);
        }

        // ---- the bug: the SemiGlobal guard ----------------------------------------------

        [Fact]
        public void ASemiGlobalCallOnAnObjectWithNoGlobIsAMissingRoutineNotACrash()
        {
            // The finding. A GLOB-less object's resource is exactly `SemiGlobal == null`, and the
            // old copies of this rule dereferenced it unconditionally. Every one of the 131 shipped
            // sentinel sites in a GLOB-less object reached that line.
            var owner = new StubResource(4096, 4097);

            Assert.Null(owner.SemiGlobal);
            Assert.Null(VMRoutineResolver.Resolve(owner, null, 0x7FFF));
            Assert.Null(VMRoutineResolver.Resolve(owner, null, 0xFFFF));
            Assert.Null(VMRoutineResolver.Resolve(owner, null, 8192));
        }

        [Fact]
        public void AGloblessObjectStillGetsItsPrivateTrees()
        {
            // The guard must not become "no GLOB, no routines at all": 4096..8191 is answered by
            // the object's own IFF and does not touch SemiGlobal.
            var owner = new StubResource(4096);

            Assert.Equal((ushort)4096, VMRoutineResolver.Resolve(owner, null, 4096).ID);
        }

        [Theory]
        [InlineData(0x7FFF)]
        [InlineData(0xFFFF)]
        public void ASentinelOnAnObjectWhoseGlobDoesNotHaveItIsStillJustAMiss(int sentinel)
        {
            // The other half: 51 of the shipped sentinel sites are in objects that DO carry a
            // GLOB, and already took the missing-routine path. Both halves must answer null.
            var owner = new StubResource();
            owner.SemiGlobal = SemiGlobalOwning(8300);

            Assert.Null(VMRoutineResolver.Resolve(owner, null, (ushort)sentinel));
        }

        // ---- absent resources are a miss, never a throw ---------------------------------

        [Fact]
        public void NoScopeResourceIsAMissInEveryBand()
        {
            // A save naming a GUID this install does not have gives a null code owner. That is an
            // uninstalled master, not a broken engine.
            Assert.Null(VMRoutineResolver.Resolve(null, null, 0));
            Assert.Null(VMRoutineResolver.Resolve(null, null, 4096));
            Assert.Null(VMRoutineResolver.Resolve(null, null, 8192));
            Assert.Null(VMRoutineResolver.Resolve(null, null, 0x7FFF));
        }

        [Fact]
        public void NoGlobalsIsAMissInTheGlobalBandAndIrrelevantAboveIt()
        {
            Assert.Null(VMRoutineResolver.Resolve(new StubResource(), null, 0));
            // Above the private band the globals are not consulted at all, so their absence cannot
            // change the answer - which is why the frame overload may pass null for them.
            Assert.Equal((ushort)4096, VMRoutineResolver.Resolve(new StubResource(4096), null, 4096).ID);
        }
    }
}
