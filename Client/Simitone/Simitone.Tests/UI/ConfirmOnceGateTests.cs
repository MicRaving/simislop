using Simitone.Client.UI.Services;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="ConfirmOnceGate"/>, the pure state machine behind every "ask once,
    /// act on re-entry" confirmation in the game.
    ///
    /// <para>
    /// The screens used to implement the latch inline as a nullable <c>UIMobileAlert</c> field, in
    /// five near-identical copies:
    /// <c>TS1GameScreen.CloseAttempt</c>, <c>TS1GameScreen.ReturnToNeighbourhood</c>,
    /// <c>TS1CASScreen.Accept</c>, <c>TS1CASScreen.GoBack</c> and
    /// <c>TS1CASScreen.FamiliesPanel_OnDeleteFamily</c>. The shape has one non-obvious rule, and
    /// it is the whole reason the latch exists: the dialog's <em>yes</em> handler re-enters the
    /// method that asked for the confirmation, and that re-entry must see the latch <em>still
    /// held</em> so it takes the "otherwise" branch and does the work. Release the latch before
    /// the work and the confirmation recurses until the stack gives out.
    /// </para>
    ///
    /// <para>
    /// That rule is what these tests pin. They are deliberately exhaustive about a two-state
    /// machine rather than clever: what matters is that a second <c>TryBegin</c> without a
    /// <c>Reset</c> declines, that <c>Reset</c> always releases exactly once, and that two gates
    /// never share state (each screen gets its own).
    /// </para>
    ///
    /// Nothing here touches a graphics device, content, audio or the VM - the gate has no
    /// collaborators at all.
    /// </summary>
    public sealed class ConfirmOnceGateTests
    {
        [Fact]
        public void ANewGateIsFree()
        {
            var gate = new ConfirmOnceGate();

            Assert.False(gate.IsLatched);
        }

        [Fact]
        public void TryBeginSucceedsOnAFreeGate()
        {
            var gate = new ConfirmOnceGate();

            Assert.True(gate.TryBegin());
        }

        [Fact]
        public void TryBeginLatchesTheGate()
        {
            var gate = new ConfirmOnceGate();

            gate.TryBegin();

            Assert.True(gate.IsLatched);
        }

        [Fact]
        public void ASecondTryBeginWithoutAResetDeclines()
        {
            // the whole contract: this is the re-entry of the method that opened the dialog, and it
            // must fall through to the real work rather than opening a second dialog.
            var gate = new ConfirmOnceGate();
            gate.TryBegin();

            Assert.False(gate.TryBegin());
        }

        [Fact]
        public void ADeclinedTryBeginLeavesTheGateLatched()
        {
            // a declined begin must not half-claim the gate - otherwise the next real dialog would
            // open over the one already on screen.
            var gate = new ConfirmOnceGate();
            gate.TryBegin();

            gate.TryBegin();
            gate.TryBegin();

            Assert.True(gate.IsLatched);
        }

        [Fact]
        public void ResetReleasesTheGate()
        {
            var gate = new ConfirmOnceGate();
            gate.TryBegin();

            gate.Reset();

            Assert.False(gate.IsLatched);
        }

        [Fact]
        public void TryBeginSucceedsAgainAfterAReset()
        {
            var gate = new ConfirmOnceGate();
            gate.TryBegin();
            gate.Reset();

            Assert.True(gate.TryBegin());
        }

        [Fact]
        public void ResetOnAFreeGateIsHarmless()
        {
            // the button handlers can fire in any order, and a stray release must not lock the
            // screen out of asking again.
            var gate = new ConfirmOnceGate();

            gate.Reset();
            gate.Reset();

            Assert.False(gate.IsLatched);
            Assert.True(gate.TryBegin());
        }

        [Fact]
        public void ResetIsIdempotentWhileLatched()
        {
            // closing and releasing twice is what "-=" on an absent handler is like: no-op, not a
            // re-open.
            var gate = new ConfirmOnceGate();
            gate.TryBegin();

            gate.Reset();
            gate.Reset();

            Assert.True(gate.TryBegin());
            Assert.True(gate.IsLatched);
        }

        [Fact]
        public void AGateAlternatesBeginAndResetForever()
        {
            // the whole lifetime of a screen's confirmations, in miniature: the same gate is
            // reused for every confirmation, one after another.
            var gate = new ConfirmOnceGate();

            for (int i = 0; i < 5; i++)
            {
                Assert.True(gate.TryBegin());
                Assert.False(gate.TryBegin());
                Assert.True(gate.IsLatched);
                gate.Reset();
                Assert.False(gate.IsLatched);
            }
        }

        [Fact]
        public void TwoGatesDoNotShareState()
        {
            // each screen has its own DialogService, and therefore its own gate; a confirmation up
            // on one screen must not latch the other.
            var quit = new ConfirmOnceGate();
            var cas = new ConfirmOnceGate();

            Assert.True(quit.TryBegin());

            Assert.True(cas.TryBegin());
            Assert.False(cas.TryBegin());
            Assert.True(quit.IsLatched);
            Assert.True(cas.IsLatched);
        }

        [Fact]
        public void ResettingOneGateLeavesTheOtherLatched()
        {
            var quit = new ConfirmOnceGate();
            var cas = new ConfirmOnceGate();
            quit.TryBegin();
            cas.TryBegin();

            quit.Reset();

            Assert.False(quit.IsLatched);
            Assert.True(cas.IsLatched);
        }
    }
}
