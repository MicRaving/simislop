/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using System;

namespace Simitone.Client.UI.Services
{
    /// <summary>
    /// The one place the "ask once, act on re-entry" confirm idiom is written.
    ///
    /// <para>
    /// Five sites in the game used the same shape, each with its own copy of a nullable
    /// <c>UIMobileAlert</c> field standing in for "is a dialog already up?":
    /// <c>TS1GameScreen.CloseAttempt</c>, <c>TS1GameScreen.ReturnToNeighbourhood</c>,
    /// <c>TS1CASScreen.Accept</c>, <c>TS1CASScreen.GoBack</c> and
    /// <c>TS1CASScreen.FamiliesPanel_OnDeleteFamily</c>, plus the plain
    /// <c>UIMobileAlert</c> that <c>ShowSaveError</c> puts up. The shape is:
    /// </para>
    ///
    /// <list type="number">
    /// <item>if the latch is free, build a <c>UIMobileAlert</c>, latch it, show it modally, and
    /// return;</item>
    /// <item>otherwise run the real work - which is what the dialog's <em>yes</em> handler reaches
    /// by calling the same method again while the latch is still held;</item>
    /// <item>every button handler closes the dialog <em>first</em>, then runs the work, and only
    /// then releases the latch.</item>
    /// </list>
    ///
    /// <para>
    /// Step 3's order is load-bearing and is preserved exactly. The work runs with the latch still
    /// held, because that is precisely what makes the re-entry in step 2 take the "otherwise"
    /// branch; releasing first would make the confirmation recurse forever. A service instance owns
    /// one gate and the one dialog that currently holds it, so a screen gets the same
    /// single-latch semantics its single field had.
    /// </para>
    ///
    /// <para>
    /// The service does not own any game state and has no knowledge of what the confirmations are
    /// for - only of the latch and the dialog. Strings, titles and actions stay with the caller, so
    /// the localisation is unchanged.
    /// </para>
    ///
    /// <para>
    /// <b>The latch and the dialog are one fact, not two.</b> They used to be written in two
    /// places - <see cref="TryBeginConfirm"/> latched, <see cref="ShowModal(UIMobileAlert)"/>
    /// recorded - and a caller that showed without latching produced a dialog on screen that
    /// <see cref="HasOpenDialog"/> reported as absent. That is not hypothetical: the
    /// neighbourhood picker deliberately shows its "buy this lot?" without taking the claim,
    /// because there is nothing for it to re-enter, and while it was up the quit confirmation on
    /// the same service saw a free latch and put a second modal on top of it. Recording a dialog
    /// now claims the latch too, so the two can only ever agree, and the only way to reach the bad
    /// state is to begin and never show - which <see cref="IsClaimedWithoutDialog"/> reports and
    /// <see cref="CloseAndRelease"/> undoes.
    /// </para>
    /// </summary>
    public sealed class DialogService
    {
        /// <summary>
        /// The latch. One per service, matching the one nullable dialog field each screen used to
        /// carry - so <c>TS1CASScreen</c> still treats its three confirmations as mutually
        /// exclusive, and <c>TS1GameScreen</c> still treats "quit" and "return to neighbourhood" as
        /// mutually exclusive.
        /// </summary>
        public ConfirmOnceGate ConfirmGate { get; private set; }

        /// <summary>
        /// The dialog currently holding <see cref="ConfirmGate"/>, or <c>null</c> when the gate is
        /// free. Kept so <see cref="CloseAndRelease"/> can close the right element and so a caller
        /// can answer "is a dialog up?" without walking the gate.
        /// </summary>
        public UIMobileAlert CurrentDialog { get; private set; }

        /// <summary>
        /// True while a confirmation is showing. Equivalent to the old <c>dialogField != null</c>
        /// test, and equal to <see cref="CurrentDialog"/> being non-null by construction: both
        /// <see cref="TryBeginConfirm"/> and <see cref="ShowModal(UIMobileAlert)"/> take the
        /// claim and only <see cref="CloseAndRelease"/> gives it up.
        /// </summary>
        /// <remarks>
        /// This is the answer <c>TS1GameScreen.CloseAttempt</c> gives to "is the player already
        /// looking at something they have to dismiss?", so it has to be true for <em>every</em>
        /// dialog on this service - not just the ones that went through the latch.
        /// </remarks>
        public bool HasOpenDialog
        {
            get { return ConfirmGate.IsLatched; }
        }

        /// <summary>
        /// A claim taken but no dialog shown: the one state a well-behaved caller cannot reach,
        /// kept observable because it is a mistake rather than a design.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every <c>TryBeginConfirm</c> call site shows its dialog immediately afterwards, so this
        /// is false throughout normal use. It becomes true when a caller returns between taking
        /// the claim and showing the dialog - an early <c>return</c> on a guard, an exception, a
        /// future <c>ShowConfirm</c> guarded by a condition. The consequence is that every later
        /// confirmation on this service takes the "already confirming" branch and runs its work
        /// with nothing on screen, so the player never gets asked.
        /// </para>
        /// <para>
        /// Nothing polls this, deliberately: it is a diagnosis, not a hot path. The fix is
        /// <see cref="CloseAndRelease"/>, which is already safe with no dialog to close, and which
        /// a caller can reach from its own <c>Removed</c> teardown.
        /// </para>
        /// </remarks>
        public bool IsClaimedWithoutDialog
        {
            get { return ConfirmGate.IsLatched && !_dialogShown; }
        }

        /// <summary>
        /// Whether a dialog has been shown and not yet closed, tracked separately from
        /// <see cref="CurrentDialog"/> so the "claim without a dialog" state is a fact about the
        /// flow rather than about whether somebody happens to have handed us an element yet.
        /// </summary>
        private bool _dialogShown;

        /// <summary>
        /// Creates a service with a free gate.
        /// </summary>
        public DialogService()
        {
            ConfirmGate = new ConfirmOnceGate();
        }

        /// <summary>
        /// The <c>if (Latch == null)</c> test: claims the latch for a dialog the caller is about to
        /// build.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the caller should build and show its dialog and then return;
        /// <c>false</c> when a dialog already holds the latch, in which case the caller should run
        /// its real work.
        /// </returns>
        /// <remarks>
        /// Only needed by a confirmation whose "yes" answer <b>re-enters</b> the method that asked
        /// for it - that re-entry is what needs the latch. A confirmation with nothing to re-enter
        /// can go straight to <see cref="ShowConfirm(string,string,Action,Action)"/>, which claims
        /// the latch as it shows (see <see cref="ShowModal(UIMobileAlert)"/>), so it is not left
        /// invisible to <see cref="HasOpenDialog"/>.
        /// </remarks>
        public bool TryBeginConfirm()
        {
            return ConfirmGate.TryBegin();
        }

        /// <summary>
        /// Builds and shows a modal Yes/No confirmation.
        /// </summary>
        /// <remarks>
        /// Both handlers close the dialog before running their action and release the gate
        /// afterwards, so <paramref name="onYes"/> - which typically re-enters the method that
        /// asked for the confirmation - still sees the latch held. A <c>null</c> action is a
        /// close-and-release and nothing more, which is what a confirmation with no "no" answer
        /// needs; <c>UIMobileAlert</c> also gives that button its own internal close, and
        /// <c>Close</c> is idempotent, so the two do not conflict.
        /// </remarks>
        /// <param name="title">Already-localised title text.</param>
        /// <param name="message">Already-localised message text.</param>
        /// <param name="onYes">Runs when Yes is pressed, with the latch still held.</param>
        /// <param name="onNo">Runs when No is pressed.</param>
        public void ShowConfirm(string title, string message, Action onYes, Action onNo)
        {
            ShowModal(title, message, UIAlertButton.YesNo(
                (ybtn) => CloseAndReleaseThen(onYes),
                (nbtn) => CloseAndReleaseThen(onNo)));
        }

        /// <summary>
        /// Builds and shows a modal Yes/No/Cancel confirmation. All three handlers close the
        /// dialog first and release the gate afterwards.
        /// </summary>
        /// <param name="title">Already-localised title text.</param>
        /// <param name="message">Already-localised message text.</param>
        /// <param name="onYes">Runs when Yes is pressed, with the latch still held.</param>
        /// <param name="onNo">Runs when No is pressed.</param>
        /// <param name="onCancel">Runs when Cancel is pressed.</param>
        public void ShowConfirm(string title, string message, Action onYes, Action onNo, Action onCancel)
        {
            ShowModal(title, message, UIAlertButton.YesNoCancel(
                (ybtn) => CloseAndReleaseThen(onYes),
                (nbtn) => CloseAndReleaseThen(onNo),
                (cbtn) => CloseAndReleaseThen(onCancel)));
        }

        /// <summary>
        /// Shows a modal alert with a single OK button, closing itself.
        /// </summary>
        /// <param name="title">Already-localised title text.</param>
        /// <param name="message">Already-localised message text.</param>
        public void ShowAlert(string title, string message)
        {
            ShowAlert(title, message, null);
        }

        /// <summary>
        /// Shows a modal alert with a single OK button, closing itself.
        /// </summary>
        /// <remarks>
        /// <paramref name="detail"/> is appended to the message after two newlines, separated out
        /// from the localised sentence because technical detail is not translatable. A
        /// <c>null</c> or empty detail leaves the message exactly as it was.
        /// </remarks>
        /// <param name="title">Already-localised title text.</param>
        /// <param name="message">Already-localised message text.</param>
        /// <param name="detail">Untranslated technical detail, or <c>null</c> for none.</param>
        /// <remarks>
        /// An alert is not part of the confirm idiom and takes no claim, so it does not answer
        /// <see cref="HasOpenDialog"/> either. That is left as it was rather than changed here:
        /// a claim needs a release, this dialog has no handler to release it from, and a claim
        /// that is never given back locks the screen out of every later confirmation. The
        /// residual is that a confirmation can be put up on top of an alert - the save-error
        /// dialog is the one a player is most likely to meet - so the two are documented as
        /// separate rather than pretended to be one.
        /// </remarks>
        public void ShowAlert(string title, string message, string detail)
        {
            var text = string.IsNullOrEmpty(detail) ? message : message + "\n\n" + detail;
            //the OK button gets no handler, so the alert closes itself (UIMobileAlert's internal
            //handler calls Close). An alert is not part of the confirm idiom: nothing is latched
            //and nothing needs releasing.
            UIScreen.GlobalShowDialog(new UIMobileAlert(new UIAlertOptions()
            {
                Title = title,
                Message = text,
                Buttons = UIAlertButton.Ok(null)
            }), true);
        }

        /// <summary>
        /// The one Yes/No that needs no <c>TryBeginConfirm</c> first: a width of its own and no
        /// cancel, so there is no re-entry for the latch to suppress. It still takes the claim,
        /// because it is a dialog on the same service as every other one and
        /// <see cref="HasOpenDialog"/> has to be true while it is up.
        /// </summary>
        /// <remarks>
        /// The neighbourhood page's Delete confirmation is that case, and it used to build its own
        /// <c>UIMobileAlert</c> here rather than go through the service - a second copy of the
        /// "both buttons handler-free, Yes gets the work, let <c>UIMobileAlert</c> do the closing"
        /// rule, which is exactly the kind of copy that drifts from the one above. It is here now,
        /// next to the latched idiom it is deliberately unlike, so the difference between them is
        /// one screen of documentation rather than two implementations.
        ///
        /// <para>
        /// Order is "close, then act" and it is <c>UIMobileAlert</c>'s own internal handler that
        /// closes: the action is attached <em>afterwards</em>, so it runs second. Adding it to the
        /// button map up front would run it before the dialog went away.
        /// </para>
        ///
        /// <para>
        /// Nothing here can call <see cref="CloseAndRelease"/>, because the buttons are
        /// handler-free and the alert closes itself - which means nothing would ever give the
        /// claim back and the next confirmation on this service would decline for the rest of the
        /// session. <see cref="SelfClosingAlert"/> is what closes that loop: it releases the claim
        /// when it is torn down, whichever way it went away.
        /// </para>
        /// </remarks>
        /// <param name="title">Already-localised title text.</param>
        /// <param name="message">Already-localised message text.</param>
        /// <param name="width">The dialog's width, for the one message that needs a wide plate.</param>
        /// <param name="onYes">Runs when Yes is pressed, after the dialog has closed.</param>
        public void ShowPlainConfirm(string title, string message, int width, Action onYes)
        {
            UIMobileAlert alert = null;
            alert = new SelfClosingAlert(new UIAlertOptions()
            {
                Title = title,
                Message = message,
                Width = width,
                Buttons = UIAlertButton.YesNo(null, null)
            }, () => ReleaseFor(alert));
            alert.ButtonMap[UIAlertButtonType.Yes].OnButtonClick += (b) => onYes?.Invoke();

            RecordDialogShown(alert);
            UIScreen.GlobalShowDialog(alert, true);
        }

        /// <summary>
        /// A <see cref="ShowPlainConfirm"/> plate that hands the claim back when it leaves the
        /// screen, because the shape it replaces has nothing to hang a release on.
        /// </summary>
        private sealed class SelfClosingAlert : UIMobileAlert
        {
            private readonly Action _onRemoved;

            public SelfClosingAlert(UIAlertOptions options, Action onRemoved) : base(options)
            {
                _onRemoved = onRemoved;
            }

            public override void Removed()
            {
                base.Removed();
                //Any exit, not just a button: the dialog can be dismissed by the backdrop, by a
                //screen change, or by a caller removing it. All of them end the dialog, so all of
                //them end the claim.
                _onRemoved?.Invoke();
            }
        }

        /// <summary>
        /// Shows an already-built dialog modally, claiming the gate if the caller has not already
        /// done so, for the one confirm shape whose buttons manage the dialog themselves:
        /// <c>TS1GameScreen.CloseAttempt</c>'s "quit" dialog, whose Yes and No answers call
        /// <c>Game.Exit()</c> and deliberately leave the dialog on screen and the latch held,
        /// because the process is on its way out. Only its Cancel answer closes and releases.
        ///
        /// <para>
        /// Use <see cref="TryBeginConfirm"/> to take the gate first, exactly as the inline
        /// <c>if (CloseAlert == null)</c> did - but it is not required for the claim to be
        /// correct, only for the re-entry guard.
        /// </para>
        /// </summary>
        /// <param name="dialog">The dialog to show. Becomes <see cref="CurrentDialog"/>.</param>
        public void ShowModal(UIMobileAlert dialog)
        {
            if (dialog == null)
            {
                // Nothing to show means nothing can be dismissed, so claiming the gate here would
                // lock the screen out of asking again with no way back. A caller that means to
                // show a dialog and passes null is a bug; losing the confirmation is the cheap
                // half of it.
                SimitoneLog.Warn("DialogService.ShowModal was given no dialog; the confirmation was not shown.");
                return;
            }

            RecordDialogShown(dialog);
            UIScreen.GlobalShowDialog(dialog, true);
        }

        /// <summary>
        /// The state half of <see cref="ShowModal(UIMobileAlert)"/>, split out so it can be
        /// exercised without a graphics device and a <c>UIScreen.Current</c>.
        /// </summary>
        /// <remarks>
        /// Claiming the gate here rather than only in <see cref="TryBeginConfirm"/> is the whole
        /// point: a dialog on screen is a claim on the confirmation, whoever asked for it. Before
        /// this, a caller that showed without calling <see cref="TryBeginConfirm"/> - which the
        /// neighbourhood picker's "buy this lot?" does on purpose, because it has no re-entry to
        /// guard - left <see cref="HasOpenDialog"/> answering false while a modal was up, and every
        /// other confirmation on the same service would stack on top of it.
        /// </remarks>
        /// <param name="dialog">The dialog now on screen.</param>
        internal void RecordDialogShown(UIMobileAlert dialog)
        {
            CurrentDialog = dialog;
            _dialogShown = true;
            // TryBegin rather than an assignment: the gate is the shared object, and it declines a
            // second claim, which is the right answer for the re-entry path.
            ConfirmGate.TryBegin();
        }

        /// <summary>
        /// Gives the claim up if <paramref name="dialog"/> is the one holding it.
        /// </summary>
        /// <remarks>
        /// Guarded because a dialog can be torn down after something else has taken its place -
        /// the plain confirm's teardown, a screen change removing a stale element - and a
        /// teardown must never release a claim it does not own. A null or unmatched dialog is a
        /// no-op, which also makes it safe to wire to a dialog that was never shown.
        /// </remarks>
        internal void ReleaseFor(UIMobileAlert dialog)
        {
            if (dialog == null || !ReferenceEquals(CurrentDialog, dialog)) return;
            Release();
        }

        /// <summary>
        /// Closes <see cref="CurrentDialog"/> and releases the gate, without running anything.
        /// This is the "close the dialog, clear the latch" tail the button handlers end with.
        /// </summary>
        public void CloseAndRelease()
        {
            if (CurrentDialog != null) CurrentDialog.Close();
            Release();
        }

        /// <summary>
        /// The standard button-handler body: close the dialog, run <paramref name="action"/> with
        /// the latch still held, then release the gate. Written once here instead of in six
        /// near-identical lambdas, and the order is the behaviour - see the class remarks.
        /// </summary>
        private void CloseAndReleaseThen(Action action)
        {
            if (CurrentDialog != null) CurrentDialog.Close();
            if (action != null) action();
            Release();
        }

        /// <summary>
        /// Gives the claim up. Unconditional by design and used by both release paths: there is
        /// no ordering in which a dialog is remembered without the claim, or a claim without a
        /// dialog, so one call clears the whole state and there is nothing left to keep in step.
        /// </summary>
        private void Release()
        {
            CurrentDialog = null;
            _dialogShown = false;
            ConfirmGate.Reset();
        }

        private void ShowModal(string title, string message, UIAlertButton[] buttons)
        {
            ShowModal(new UIMobileAlert(new UIAlertOptions()
            {
                Title = title,
                Message = message,
                Buttons = buttons
            }));
        }
    }
}
