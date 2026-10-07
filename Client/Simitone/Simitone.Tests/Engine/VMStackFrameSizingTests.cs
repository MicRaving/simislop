using FSO.Content;
using FSO.Files.Formats.IFF;
using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Engine.Utils;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMStackFrame.SizeLocals"/> and <see cref="VMStackFrame.SizeArgs"/> - the one
    /// rule for how big a restored or pushed frame's locals and arguments have to be.
    ///
    /// <para>
    /// The bug: the live path and the restore path sized them differently. <c>VMThread.Push</c>
    /// allocated <c>max(Routine.Locals, Routine.Arguments)</c> locals;
    /// <c>VMStackFrame.Load</c> allocated <c>Routine.Locals</c> alone and did not pad
    /// <c>Args</c> at all, even though <c>ExecuteSubRoutine</c> always allocates
    /// <c>max(Routine.Arguments, 4)</c> for a live call and a number of primitives index
    /// <c>Args</c> without asking how long it is.
    /// </para>
    ///
    /// <para>
    /// So a frame restored from a save whose routine declares more parameters than locals came
    /// back with a <c>Locals</c> array one rule too short, and the first <c>Local[N]</c> write -
    /// <c>VMVariableScope.Local</c>, scope 25 - threw <c>IndexOutOfRangeException</c> from inside a
    /// script. TS1 saves make the disagreement certain rather than possible:
    /// <c>VMTS1ActivatorNew.ConvertThread</c> writes the raw TS1 local count into the marshal, so
    /// the two rules see different numbers for the same frame by construction.
    /// </para>
    ///
    /// <para>
    /// The property under test is that the two paths now produce the same shape, and that a
    /// well-formed save is not disturbed: an array already at least this long comes back as the
    /// same instance, so nothing about a normal round trip changes.
    /// </para>
    /// </summary>
    public sealed class VMStackFrameSizingTests
    {
        private static VMRoutine Routine(ushort locals, ushort arguments)
            => new VMRoutine { Locals = locals, Arguments = arguments };

        // ---- locals ----------------------------------------------------------------------

        [Fact]
        public void LocalsAreAsLongAsTheBiggerOfLocalsAndArguments()
        {
            // The live path's rule, now the only rule. The interesting case is arguments > locals:
            // a routine can declare four parameters and no locals, and the first Local[N] write
            // still has to land somewhere.
            Assert.Equal(5, VMStackFrame.SizeLocals(Routine(locals: 5, arguments: 3), null).Length);
            Assert.Equal(6, VMStackFrame.SizeLocals(Routine(locals: 2, arguments: 6), null).Length);
            Assert.Equal(6, VMStackFrame.SizeLocals(Routine(locals: 6, arguments: 6), null).Length);
            Assert.Equal(0, VMStackFrame.SizeLocals(Routine(locals: 0, arguments: 0), null).Length);
        }

        [Fact]
        public void ARestoredFrameIsTheSameSizeAsAPushedOne()
        {
            // The finding, stated as the two paths agreeing. Whatever the routine declares, the
            // array a restored frame gets is the array a pushed frame gets.
            foreach (var locals in new ushort[] { 0, 1, 4, 12 })
            {
                foreach (var arguments in new ushort[] { 0, 1, 4, 9 })
                {
                    var routine = Routine(locals, arguments);
                    var pushed = VMStackFrame.SizeLocals(routine, null);
                    var restored = VMStackFrame.SizeLocals(routine, new short[0]);

                    Assert.Equal(pushed.Length, restored.Length);
                    Assert.Equal(Math.Max((int)locals, (int)arguments), restored.Length);
                }
            }
        }

        [Fact]
        public void AShortRestoredArrayIsPaddedAndKeepsWhatItHad()
        {
            var incoming = new short[] { 7, 8, 9 };

            var padded = VMStackFrame.SizeLocals(Routine(locals: 6, arguments: 0), incoming);

            Assert.Equal(6, padded.Length);
            Assert.Equal(new short[] { 7, 8, 9, 0, 0, 0 }, padded);
        }

        [Fact]
        public void AnArrayThatIsAlreadyLongEnoughComesBackUntouched()
        {
            // So a well-formed save still round-trips to the same array - the fix changes shape
            // only where the shape was wrong.
            var incoming = new short[] { 1, 2, 3, 4, 5 };

            Assert.Same(incoming, VMStackFrame.SizeLocals(Routine(locals: 2, arguments: 1), incoming));
            Assert.Same(incoming, VMStackFrame.SizeArgs(Routine(locals: 2, arguments: 1), incoming));
        }

        [Fact]
        public void ARestoreWithNoLocalsAtAllGetsAZeroedArrayRatherThanNull()
        {
            // A null Locals is an NRE on the first read, and the old Load path left it null.
            var locals = VMStackFrame.SizeLocals(Routine(locals: 3, arguments: 0), null);

            Assert.NotNull(locals);
            Assert.Equal(new short[] { 0, 0, 0 }, locals);
        }

        [Fact]
        public void AnUnresolvedRoutineGetsAnEmptyArrayAndNotACrash()
        {
            // A frame whose routine did not resolve is exactly the E-03 case, and it reaches here
            // too: Load sizes the arrays after resolving, so a null routine is a real input.
            Assert.Empty(VMStackFrame.SizeLocals(null, null));
            Assert.Empty(VMStackFrame.SizeLocals(null, new short[0]));
            Assert.Equal(4, VMStackFrame.SizeArgs(null, null).Length);
        }

        // ---- args ------------------------------------------------------------------------

        [Fact]
        public void ArgsAreAtLeastFourOrTheRoutinesOwnArgumentCount()
        {
            // `max(Routine.Arguments, 4)` is what a live call gets, and the four is load bearing.
            Assert.Equal(4, VMStackFrame.SizeArgs(Routine(locals: 0, arguments: 0), null).Length);
            Assert.Equal(4, VMStackFrame.SizeArgs(Routine(locals: 0, arguments: 2), null).Length);
            Assert.Equal(4, VMStackFrame.SizeArgs(Routine(locals: 0, arguments: 4), null).Length);
            Assert.Equal(11, VMStackFrame.SizeArgs(Routine(locals: 0, arguments: 11), null).Length);
        }

        [Fact]
        public void AShortArgumentArrayIsPaddedToTheSameShapeALiveCallGets()
        {
            // TS1 is stricter than FreeSO about arguments (VMTS1ActivatorNew pads them to 4 on the
            // way out), so a save can arrive with fewer than a live call would have had.
            var padded = VMStackFrame.SizeArgs(Routine(locals: 0, arguments: 6), new short[] { 1, 2 });

            Assert.Equal(new short[] { 1, 2, 0, 0, 0, 0 }, padded);
        }
    }

    /// <summary>
    /// <see cref="VMMemory.GetTuningVariable"/> and <see cref="VMMemory.GetTuningVariableLabel"/> -
    /// the three tuning bands, and what happens when the lot has nothing to answer with.
    ///
    /// <para>
    /// A tuning data word packs a table id and a key id as <c>(tableID &lt;&lt; 7) | keyID</c>. The
    /// table id picks one of three owners: 0..63 the code owner's own tables, 64..127 the
    /// semi-global its <c>GLOB</c> points at, 128..191 the game globals. Each band is stored under
    /// its own base, so the same table number in different bands is a different table.
    /// </para>
    ///
    /// <para>
    /// The bug was not the banding. A table id of 192 or above keeps the local band and is not
    /// reduced, and that is right: the local band's ids are BCON <i>chunk</i> ids, so anything
    /// above 191 is just another of the code owner's own tables, and the base cannot overflow
    /// because a table id is <c>data &gt;&gt; 7</c> on a ushort and so tops out at 511. What was
    /// wrong was the dereference: every branch could hand back a null cache - a frame whose code
    /// owner is not installed, a resource that never recached, a thread whose context has no
    /// globals - and the next line indexed it.
    /// </para>
    /// </summary>
    public sealed class VMTuningLookupTests
    {
        private const int LocalBase = 4096;
        private const int SemiGlobalBase = 8192;
        private const int GlobalBase = 256;

        /// <summary>
        /// A code owner with an empty tuning cache, built over no IFF at all. The real
        /// constructor returns before it recaches when handed a null IFF, so this is a resource
        /// with no content in it - which is half of what these tests are about.
        /// </summary>
        private static GameObjectResource BlankOwner()
        {
            var resource = new GameObjectResource(null, null, null, "stub", null);
            resource.TuningCache = new Dictionary<uint, short>();
            resource.RoutineCache = new Dictionary<ushort, object>();
            return resource;
        }

        /// <summary>A semi-global over an empty IFF, with its tuning cache filled in.</summary>
        private static GameGlobalResource SemiGlobalWith(Dictionary<uint, short> tuning)
        {
            var semiGlobal = new GameGlobalResource(new IffFile(), null);
            foreach (var entry in tuning) semiGlobal.TuningCache[entry.Key] = entry.Value;
            return semiGlobal;
        }

        /// <summary>The game globals, with the given tuning entries.</summary>
        private static GameGlobal GlobalsWith(Dictionary<uint, short> tuning)
        {
            var resource = new GameGlobalResource(new IffFile(), null);
            foreach (var entry in tuning) resource.TuningCache[entry.Key] = entry.Value;
            return new GameGlobal { Resource = resource };
        }

        /// <summary>
        /// A frame whose code owner and globals are whatever the test supplies. The thread and
        /// its context are made without their constructors, which want a lot and a content
        /// manager; the tuning path reads nothing else off either.
        /// </summary>
        private static VMStackFrame FrameIn(GameObjectResource codeOwner, GameGlobal globals)
        {
            var thread = (VMThread)RuntimeHelpers.GetUninitializedObject(typeof(VMThread));
            thread.Context = (VMContext)RuntimeHelpers.GetUninitializedObject(typeof(VMContext));
            thread.Context.Globals = globals;

            return new VMStackFrame
            {
                Thread = thread,
                CodeOwner = codeOwner == null ? null : new GameObject { Resource = codeOwner },
            };
        }

        private static uint Key(int tableID, int keyID) => ((uint)tableID << 16) | (uint)keyID;

        /// <summary>A tuning word, packed the way a bhav packs it.</summary>
        private static ushort Word(int tableID, int keyID) => (ushort)((tableID << 7) | keyID);

        private static VMEntity NoReplacement() => (VMEntity)RuntimeHelpers.GetUninitializedObject(typeof(VMGameObject));

        // ---- the three bands ---------------------------------------------------------------

        [Fact]
        public void EachBandAnswersFromTheOwnerItNames()
        {
            var local = BlankOwner();
            local.TuningCache[Key(LocalBase + 10, 3)] = 111;
            var globals = GlobalsWith(new Dictionary<uint, short> { { Key(GlobalBase + 10, 3), 222 } });

            var frame = FrameIn(local, globals);
            local.SemiGlobal = SemiGlobalWith(new Dictionary<uint, short> { { Key(SemiGlobalBase + 10, 3), 333 } });

            Assert.Equal((short)111, VMMemory.GetTuningVariable(NoReplacement(), Word(10, 3), frame));
            Assert.Equal((short)333, VMMemory.GetTuningVariable(NoReplacement(), Word(74, 3), frame));
            Assert.Equal((short)222, VMMemory.GetTuningVariable(NoReplacement(), Word(138, 3), frame));
        }

        [Fact]
        public void ATuningWordSplitsIntoItsTableAndKey()
        {
            // (tableID << 7) | keyID, with the key in the low seven bits. Pinned because the band
            // arithmetic above is the whole of the decode.
            for (int tableID = 0; tableID < 512; tableID++)
            {
                var word = Word(tableID, 0x7F);
                Assert.Equal(tableID, word >> 7);
                Assert.Equal(0x7F, word & 0x7F);
            }
        }

        [Fact]
        public void ATableAboveTheGlobalBandIsStillTheCodeOwnersOwn()
        {
            // Not a fall-through, and not an overflow: the local band's ids are BCON chunk ids, so
            // 192 and up is just another local table, and the base cannot wrap (a table id is
            // `data >> 7` on a ushort, so it tops out at 511).
            var local = BlankOwner();
            local.TuningCache[Key(LocalBase + 200, 1)] = 55;

            var value = VMMemory.GetTuningVariable(NoReplacement(), Word(200, 1), FrameIn(local, null));

            Assert.Equal((short)55, value);
            Assert.True(LocalBase + 200 <= ushort.MaxValue);
        }

        [Fact]
        public void AGloblessObjectFallsBackToItsOwnTablesForTheSemiGlobalBand()
        {
            // A GLOB-less object has no semi-global table, and the local one is the only thing
            // left that could hold the answer.
            var local = BlankOwner();
            local.TuningCache[Key(SemiGlobalBase + 5, 2)] = 77;

            var value = VMMemory.GetTuningVariable(NoReplacement(), Word(69, 2), FrameIn(local, null));

            Assert.Null(local.SemiGlobal);
            Assert.Equal((short)77, value);
        }

        // ---- nothing to answer with ---------------------------------------------------------

        [Fact]
        public void AConstantThatIsNotThereIsZero()
        {
            var local = BlankOwner();
            Assert.Equal((short)0, VMMemory.GetTuningVariable(NoReplacement(), Word(1, 1), FrameIn(local, null)));
        }

        [Fact]
        public void AFrameWithNoCodeOwnerIsAZeroAndNotACrash()
        {
            // The dereference the finding names. This is a lot that is missing content - an
            // uninstalled master, a save naming a GUID this install does not have - and a tuning
            // constant is never worth resetting a sim's thread over.
            var frame = FrameIn(null, GlobalsWith(new Dictionary<uint, short>()));

            Assert.Equal((short)0, VMMemory.GetTuningVariable(NoReplacement(), Word(1, 1), frame));
            Assert.Equal((short)0, VMMemory.GetTuningVariable(NoReplacement(), Word(70, 1), frame));
            Assert.Equal((short)0, VMMemory.GetTuningVariable(NoReplacement(), Word(130, 1), frame));
        }

        [Fact]
        public void AThreadWithNoGlobalsIsAZeroAndNotACrash()
        {
            Assert.Equal((short)0, VMMemory.GetTuningVariable(NoReplacement(), Word(130, 1), FrameIn(BlankOwner(), null)));
        }

        [Fact]
        public void AnUnresolvedRoutineStillGetsAnAnswerableFrame()
        {
            // Everything missing at once, which is what a half-loaded lot looks like.
            var frame = new VMStackFrame();

            Assert.Equal((short)0, VMMemory.GetTuningVariable(NoReplacement(), Word(1, 1), frame));
            Assert.Equal((short)0, VMMemory.GetTuningVariable(null, Word(1, 1), frame));
        }

        // ---- the label side -----------------------------------------------------------------

        [Fact]
        public void AnObjectWithNoResourceIsLabelledUnknownRatherThanThrowing()
        {
            // The label helper is the one the IDE and the debug views call, and both of its
            // dereferences were unguarded: the object's resource, and the key inside the table.
            Assert.Equal("unknown, probably global or semiglobal", VMMemory.GetTuningVariableLabel(null, 0));
            Assert.Equal("unknown, probably global or semiglobal",
                VMMemory.GetTuningVariableLabel(new GameObject(), 0));
        }

        [Fact]
        public void AnObjectWithNoTablesAtAllIsLabelledUnknown()
        {
            var obj = new GameObject { Resource = BlankOwner() };

            Assert.Equal("unknown, probably global or semiglobal", VMMemory.GetTuningVariableLabel(obj, 0));
            Assert.Equal("unknown, probably global or semiglobal", VMMemory.GetTuningVariableLabel(obj, 0x7FFF));
        }
    }
}
