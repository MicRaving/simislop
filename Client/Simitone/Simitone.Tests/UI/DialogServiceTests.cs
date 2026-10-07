using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Services;
using System.Runtime.CompilerServices;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the latch bookkeeping <see cref="DialogService"/> adds on top of
    /// <see cref="ConfirmOnceGate"/>.
    ///
    /// <para>
    /// The parts of the service that cannot be exercised here are the ones that put a dialog on
    /// the screen: constructing a <c>UIMobileAlert</c> lays out text, loads the button textures and
    /// needs a live <c>UIScreen.Current</c>, so the three <c>Show*</c> methods end in a
    /// <c>UIScreen.GlobalShowDialog</c> a test cannot reach. Everything they do to the
    /// service's own state is separated out behind <c>RecordDialogShown</c> and
    /// <c>ReleaseFor</c> precisely so it can be - which is where the interesting part lives: that
    /// the service's gate and the service's remembered dialog stay in step, that
    /// <c>TryBeginConfirm</c> is exactly <c>ConfirmGate.TryBegin</c>, and that releasing without a
    /// dialog (which is what a confirmation with no "no" answer does on its positive path) is a
    /// clean no-op rather than a crash or a stuck latch.
    /// </para>
    ///
    /// No graphics device, content, audio or VM.
    /// </summary>
    public sealed class DialogServiceTests
    {
        [Fact]
        public void ANewServiceHasNoOpenDialog()
        {
            var dialogs = new DialogService();

            Assert.False(dialogs.HasOpenDialog);
        }

        [Fact]
        public void ANewServiceHasNoCurrentDialog()
        {
            var dialogs = new DialogService();

            Assert.Null(dialogs.CurrentDialog);
        }

        [Fact]
        public void TryBeginConfirmOpensTheDialogOnce()
        {
            var dialogs = new DialogService();

            Assert.True(dialogs.TryBeginConfirm());
            Assert.False(dialogs.TryBeginConfirm());
        }

        [Fact]
        public void HasOpenDialogTracksTheGate()
        {
            // the screen's "is a confirmation already up?" question, which is what CloseAttempt
            // answers to decide whether the close is already handled.
            var dialogs = new DialogService();

            dialogs.TryBeginConfirm();
            Assert.True(dialogs.HasOpenDialog);

            dialogs.CloseAndRelease();
            Assert.False(dialogs.HasOpenDialog);
        }

        [Fact]
        public void CloseAndReleaseFreesTheGateForTheNextConfirmation()
        {
            var dialogs = new DialogService();
            dialogs.TryBeginConfirm();

            dialogs.CloseAndRelease();

            Assert.True(dialogs.TryBeginConfirm());
        }

        [Fact]
        public void CloseAndReleaseWithNoDialogOpenIsHarmless()
        {
            // a confirmation whose positive answer has no matching negative (the CAS "are you
            // sure" dialogs) still runs the same close-then-release tail, and there is no dialog to
            // close on the very first pass.
            var dialogs = new DialogService();

            dialogs.CloseAndRelease();

            Assert.Null(dialogs.CurrentDialog);
            Assert.False(dialogs.HasOpenDialog);
        }

        [Fact]
        public void CloseAndReleaseIsIdempotent()
        {
            var dialogs = new DialogService();
            dialogs.TryBeginConfirm();

            dialogs.CloseAndRelease();
            dialogs.CloseAndRelease();

            Assert.False(dialogs.HasOpenDialog);
            Assert.True(dialogs.TryBeginConfirm());
        }

        [Fact]
        public void TheGateIsAStableInstance()
        {
            // the service's own ConfirmGate is handed to callers and to tests; handing back a new
            // one each read would make TryBeginConfirm and HasOpenDialog disagree.
            var dialogs = new DialogService();

            Assert.Same(dialogs.ConfirmGate, dialogs.ConfirmGate);
        }

        [Fact]
        public void TwoServicesDoNotShareALatch()
        {
            // one DialogService per screen, which is what keeps a confirmation on the CAS screen
            // from suppressing one on the game screen.
            var game = new DialogService();
            var cas = new DialogService();

            game.TryBeginConfirm();

            Assert.True(game.HasOpenDialog);
            Assert.False(cas.HasOpenDialog);
            Assert.True(cas.TryBeginConfirm());
        }

        [Fact]
        public void OneServiceCanCycleThroughManyConfirmations()
        {
            // the screen keeps a single DialogService for its whole life, so a long session has to
            // be able to open and close confirmations over and over.
            var dialogs = new DialogService();

            for (int i = 0; i < 5; i++)
            {
                Assert.True(dialogs.TryBeginConfirm());
                Assert.False(dialogs.TryBeginConfirm());
                Assert.True(dialogs.HasOpenDialog);
                dialogs.CloseAndRelease();
                Assert.False(dialogs.HasOpenDialog);
            }
        }

        // ---- the latch and the dialog are one fact ---------------------------------------------
        //
        // The service used to write them in two places: TryBeginConfirm latched, ShowModal
        // recorded. A caller that showed without latching - which the neighbourhood picker's "buy
        // this lot?" does on purpose, because it has no re-entry to guard - left HasOpenDialog
        // answering false while a modal was on screen. CloseAttempt asks exactly that question, so
        // the quit confirmation landed on top of the buy one, and CurrentDialog pointed at the
        // newest element, so the first dialog's own close tore down the second.
        //
        // Recording a dialog claims the latch now, and these are the assertions for it. The pure
        // part of showing a dialog is separated from the UIScreen.GlobalShowDialog call for
        // exactly this reason: a real UIMobileAlert lays out text, loads button textures and needs
        // a live UIScreen.Current, and none of that is what is under test.

        [Fact]
        public void ShowingADialogClaimsTheConfirmationEvenWithoutTryBeginConfirm()
        {
            // The live bug. HasOpenDialog is what CloseAttempt asks, so it has to be true for
            // every dialog on this service - not only the ones that went through the latch.
            var dialogs = new DialogService();

            dialogs.RecordDialogShown(Alert());

            Assert.True(dialogs.HasOpenDialog);
        }

        [Fact]
        public void ShowingADialogStopsASecondConfirmationFromStacking()
        {
            var dialogs = new DialogService();

            dialogs.RecordDialogShown(Alert());

            // The other half of the same defect: the quit confirmation on the same service
            // declining because one was already up.
            Assert.False(dialogs.TryBeginConfirm());
        }

        [Fact]
        public void ShowingADialogOnTopOfAnAlreadyHeldClaimChangesNothing()
        {
            // The re-entry path, unchanged: the caller claimed, showed, and its "yes" handler
            // re-entered the method that asked. Recording the dialog must not release anything.
            var dialogs = new DialogService();

            Assert.True(dialogs.TryBeginConfirm());
            dialogs.RecordDialogShown(Alert());
            dialogs.RecordDialogShown(Alert());

            Assert.True(dialogs.HasOpenDialog);
            Assert.False(dialogs.IsClaimedWithoutDialog);
        }

        [Fact]
        public void TheLatchAndTheDialogAgreeAfterEveryTransition()
        {
            // The invariant the two halves of the state used to be able to break, walked through
            // every transition the service exposes. Asserted after each step, so a regression
            // names the transition it broke at rather than failing the end of the walk.
            //
            // The dialogs are the stand-ins from Alert(), which were never constructed - and
            // CloseAndRelease would call Close() on one, which needs the stripes a constructor
            // builds. The release-with-a-dialog path is therefore walked through ReleaseFor,
            // which is the same Release() with the close left to the dialog that owns it;
            // CloseAndRelease-with-something-to-close is the part a playtest covers.
            var dialogs = new DialogService();
            Agrees(dialogs, "fresh", claimWithoutDialog: false);

            //the one transition that legitimately leaves the claim with nothing behind it, which
            //is the state the next test is about
            Assert.True(dialogs.TryBeginConfirm());
            Agrees(dialogs, "claimed", claimWithoutDialog: true);

            var first = Alert();
            dialogs.RecordDialogShown(first);
            Agrees(dialogs, "shown", claimWithoutDialog: false);

            //A second show, which is what a double-clicked button produces.
            var second = Alert();
            dialogs.RecordDialogShown(second);
            Agrees(dialogs, "shown again", claimWithoutDialog: false);

            //the older dialog going away must not release the newer one's claim
            dialogs.ReleaseFor(first);
            Agrees(dialogs, "stale teardown", claimWithoutDialog: false);

            dialogs.ReleaseFor(second);
            Agrees(dialogs, "torn down", claimWithoutDialog: false);

            //and round it, because a screen's service lives for its whole life.
            Assert.True(dialogs.TryBeginConfirm());
            Agrees(dialogs, "second round claimed", claimWithoutDialog: true);

            dialogs.CloseAndRelease();
            Agrees(dialogs, "second round released", claimWithoutDialog: false);
        }

        [Fact]
        public void AClaimWithNoDialogIsReportedRatherThanHidden()
        {
            // The state a caller reaches by returning between claiming and showing. It was
            // unobservable, which is why it survived: the symptom - every later confirmation
            // running its work with nothing on screen - had no name to be looked for by.
            var dialogs = new DialogService();

            Assert.False(dialogs.IsClaimedWithoutDialog);

            dialogs.TryBeginConfirm();

            Assert.True(dialogs.IsClaimedWithoutDialog);
            Assert.True(dialogs.HasOpenDialog);
        }

        [Fact]
        public void ShowingADialogClearsTheClaimWithoutOne()
        {
            var dialogs = new DialogService();
            dialogs.TryBeginConfirm();
            Assert.True(dialogs.IsClaimedWithoutDialog);

            dialogs.RecordDialogShown(Alert());

            Assert.False(dialogs.IsClaimedWithoutDialog);
        }

        [Fact]
        public void AClaimWithNoDialogIsUndoneByCloseAndRelease()
        {
            // The recovery, and the reason the state is safe to make observable: the same call
            // that closes a dialog closes a claim that never got one, because it was already safe
            // with nothing to close.
            var dialogs = new DialogService();
            dialogs.TryBeginConfirm();

            dialogs.CloseAndRelease();

            Assert.False(dialogs.IsClaimedWithoutDialog);
            Assert.False(dialogs.HasOpenDialog);
            Assert.True(dialogs.TryBeginConfirm());
        }

        [Fact]
        public void ATornDownDialogOnlyReleasesTheConfirmationItHolds()
        {
            // The plain confirm's dialog closes itself, so it releases the confirmation from its
            // own teardown. Two of them can be torn down out of order, and the older one must not
            // release a confirmation the newer one is still holding - that would let a third
            // confirmation stack on top of a dialog that is still up.
            var dialogs = new DialogService();
            var first = Alert();
            var second = Alert();

            dialogs.RecordDialogShown(first);
            dialogs.RecordDialogShown(second);
            Assert.True(dialogs.HasOpenDialog);

            dialogs.ReleaseFor(first);
            Assert.True(dialogs.HasOpenDialog);

            dialogs.ReleaseFor(second);
            Assert.False(dialogs.HasOpenDialog);
        }

        [Fact]
        public void ReleasingForADialogThisServiceNeverRecordedIsANoOp()
        {
            // Which also covers "was never shown": a null dialog and a stranger both leave the
            // state exactly as it was.
            var dialogs = new DialogService();
            dialogs.RecordDialogShown(Alert());

            dialogs.ReleaseFor(null);
            dialogs.ReleaseFor(Alert());

            Assert.True(dialogs.HasOpenDialog);
        }

        [Fact]
        public void ShowingNothingDoesNotClaimTheConfirmation()
        {
            // ShowModal refuses a null dialog rather than claiming for an element that will never
            // be dismissed. A claim with nothing behind it cannot be released by the player, so
            // taking one would lock the screen out of asking anything ever again.
            var dialogs = new DialogService();

            dialogs.ShowModal(null);

            Assert.False(dialogs.HasOpenDialog);
            Assert.True(dialogs.TryBeginConfirm());
        }

        /// <summary>
        /// A <c>UIMobileAlert</c> that was never constructed. The service only ever stores the
        /// reference and compares it, so what is under test is the bookkeeping rather than XNA -
        /// and building a real one would need a graphics device and a live <c>UIScreen</c>.
        /// </summary>
        private static UIMobileAlert Alert()
        {
            return (UIMobileAlert)RuntimeHelpers.GetUninitializedObject(typeof(UIMobileAlert));
        }

        /// <summary>
        /// The one assertion the region is about: the dialog and the latch are one fact, so they
        /// cannot be held apart except in the one state that is reported - and
        /// <paramref name="claimWithoutDialog"/> says which state that is at this step.
        /// </summary>
        private static void Agrees(DialogService dialogs, string step, bool claimWithoutDialog)
        {
            //a remembered dialog always holds the claim; the reverse failure is what the property
            //below reports rather than hides
            if (dialogs.CurrentDialog != null)
            {
                Assert.True(dialogs.HasOpenDialog, "a dialog was remembered with nothing holding the claim after " + step);
            }

            Assert.Equal(dialogs.HasOpenDialog, dialogs.ConfirmGate.IsLatched);
            Assert.Equal(claimWithoutDialog, dialogs.IsClaimedWithoutDialog);
        }
    }
}
