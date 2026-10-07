using FSO.SimAntics;
using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VM.ListenBHAVChanges"/> and <see cref="VM.SuppressBHAVChanges"/>, the pair that
    /// decides whether a tree edited in the IDE is re-read by a running lot.
    ///
    /// <para>
    /// The bug: the two methods were byte-identical, both <c>OnBHAVChange -= VM_OnBHAVChange</c>.
    /// Asking to listen silently unsubscribed, and <see cref="VM.BHAVDirty"/> is only ever set
    /// through this path - so it was never set by anything, and
    /// <c>FSO.SimAntics/VM.Tick</c>'s "re-read every routine whose runtime version moved" pass
    /// never ran.
    /// </para>
    ///
    /// <para>
    /// Five hosts call <c>ListenBHAVChanges</c> (the game screens' enter/exit-lot pairs, plus the
    /// neighbourhood facade builder and OBJ exporter) and three call
    /// <c>SuppressBHAVChanges</c>, so this is not a method nobody reaches. The event is static and
    /// the handler an instance method, which is why "listen" removes before it adds: the two
    /// hosts that call it without a matching suppress must not double-subscribe and run the
    /// Tick-time pass twice per change.
    /// </para>
    ///
    /// <para>
    /// The event is process-global, so these tests own the whole thing and give it back: the VM is
    /// made without running its constructor (which dereferences the content manager) because
    /// nothing here needs a lot, and the class is in the non-parallel collection because the event
    /// is shared state.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class VMBHAVChangeTests : IDisposable
    {
        private readonly VM _vm;

        public VMBHAVChangeTests()
        {
            // VM's constructor reads Content.Get().TS1, so it needs a content manager this suite
            // has no business creating. ListenBHAVChanges only touches a static event and an
            // instance method, neither of which reads a field.
            _vm = (VM)RuntimeHelpers.GetUninitializedObject(typeof(VM));
        }

        public void Dispose()
        {
            // Hand the process-global event back, whatever the test did.
            _vm.SuppressBHAVChanges();
        }

        /// <summary>The notification the IDE's commands raise after rewriting a tree.</summary>
        private static void ATreeWasReassembled() => VM.BHAVChanged(null);

        [Fact]
        public void ListeningGetsTheChange()
        {
            _vm.ListenBHAVChanges();
            _vm.BHAVDirty = false;

            ATreeWasReassembled();

            Assert.True(_vm.BHAVDirty);
        }

        [Fact]
        public void NotListeningDoesNotGetTheChange()
        {
            // The retail half: while suppressed - which is what the game screens do for the lot
            // they are building - a tree edit must not dirty a VM that is not showing it.
            _vm.SuppressBHAVChanges();
            _vm.BHAVDirty = false;

            ATreeWasReassembled();

            Assert.False(_vm.BHAVDirty);
        }

        [Fact]
        public void TheTwoAreNoLongerTheSameMethod()
        {
            // The finding in one line: "listen" and "suppress" used to be the same statement, which
            // is the only reason the bug could be invisible - a reader comparing them saw nothing
            // wrong because there was nothing there to see.
            _vm.ListenBHAVChanges();
            ATreeWasReassembled();
            Assert.True(_vm.BHAVDirty);

            _vm.SuppressBHAVChanges();
            _vm.BHAVDirty = false;
            ATreeWasReassembled();
            Assert.False(_vm.BHAVDirty);
        }

        [Fact]
        public void ListeningIsIdempotent()
        {
            // The two hosts that only ever call Listen must not subscribe twice, or VM.Tick's
            // recache pass runs once per duplicate for the rest of the process.
            _vm.ListenBHAVChanges();
            _vm.ListenBHAVChanges();
            _vm.SuppressBHAVChanges();
            _vm.BHAVDirty = false;

            ATreeWasReassembled();

            Assert.False(_vm.BHAVDirty);
        }

        [Fact]
        public void ListeningAfterSuppressingListens()
        {
            // The game screens' actual pattern, and the one the fix has to keep working.
            _vm.SuppressBHAVChanges();
            _vm.ListenBHAVChanges();
            _vm.BHAVDirty = false;

            ATreeWasReassembled();

            Assert.True(_vm.BHAVDirty);
        }
    }
}
