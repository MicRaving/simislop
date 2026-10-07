/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FSO.Client;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework.Model;
using FSO.SimAntics;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.UI.Panels.LotControls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels.EODs
{
    /// <summary>
    /// The one seam between Simitone's lot control and FreeSO's EOD panel tree.
    ///
    /// <para>
    /// The panels live in <c>FSO.Client</c> and Simitone.Client deliberately does not reference
    /// that assembly - see the long comment in <c>Simitone.Client.csproj</c> for why: FSO.UI and
    /// FSO.Client both declare <c>UICustomLotControl</c> (a class in one, an interface in the
    /// other) and FSO.Client adds <c>UICatalogElement</c>, so a reference makes ordinary names
    /// ambiguous across a whole UI assembly. The cost of that boundary is that this one class
    /// cannot name <c>UIEODController</c>, and everything else in the port is written so that it
    /// does not have to.
    /// </para>
    ///
    /// <para>
    /// So the surface is resolved reflectively, once, and it is deliberately tiny - a type, a
    /// two-argument constructor, one method, three fields and one virtual method, all of them
    /// public API that has to keep existing for TSO anyway. Everything it returns is either a
    /// <see cref="UIContainer"/> (the panels are FSO.UI controls; this class never needs to know
    /// they are anything else) or a plain delegate.
    /// </para>
    ///
    /// <para>
    /// Reflection is the right tool here for a second reason: it is <em>off-safe by
    /// construction</em>. A trimmed or partially installed output that cannot load FSO.Client
    /// makes <see cref="TryOpen"/> fail with a reason rather than a <c>TypeLoadException</c> from
    /// somewhere inside a lot load, and the caller has nothing to construct at all.
    /// </para>
    /// </summary>
    public sealed class EODPanelBridge
    {
        private const string ControllerTypeName = "FSO.Client.UI.Panels.EODs.UIEODController";
        private const string ControllerAssemblyName = "FSO.Client";

        /// <summary>The <c>UIEODController</c>, as the <see cref="UIContainer"/> it is.</summary>
        public UIContainer Controller { get; private set; }

        /// <summary>Why the panel tree could not be reached, or null when it was.</summary>
        public string UnavailableReason { get; private set; }

        /// <summary>False when nothing was reached; the lot must carry on without EODs.</summary>
        public bool Available { get { return Controller != null; } }

        /// <summary>The panel currently open, as a plain UI container, or null.</summary>
        public UIContainer ActivePanel { get; private set; }

        /// <summary>
        /// The plugin id of the panel that is open, or 0 when none is. Read from the controller
        /// for the same reason <see cref="ActivePanel"/> is: the host needs to know <em>which</em>
        /// minigame it is showing in order to name the window.
        /// </summary>
        public uint ActivePanelID { get; private set; }

        private Action<VMNetEODMessageCmd> Deliver;
        private FieldInfo ActivePanelField;
        private FieldInfo ActiveIDField;
        private MethodInfo CloseMethod;

        private EODPanelBridge()
        {
        }

        /// <summary>
        /// Opens the panel tree, or explains why it could not be opened. Never throws.
        /// </summary>
        /// <param name="vm">The lot's VM.</param>
        /// <param name="lot">The lot control the panels read the active sim from.</param>
        public static EODPanelBridge TryOpen(VM vm, ILotControl lot)
        {
            var bridge = new EODPanelBridge();
            try
            {
                if (vm == null)
                {
                    bridge.UnavailableReason = "there is no VM to talk to";
                    return bridge;
                }

                var type = Type.GetType(ControllerTypeName + ", " + ControllerAssemblyName, false);
                if (type == null)
                {
                    bridge.UnavailableReason = ControllerAssemblyName + ".dll is not in the output folder";
                    return bridge;
                }

                var ctor = type.GetConstructor(new[] { typeof(ILotControl), typeof(VM) });
                if (ctor == null)
                {
                    bridge.UnavailableReason = ControllerTypeName + " does not take an ILotControl and a VM";
                    return bridge;
                }

                var deliver = type.GetMethod("OnEODMessage", new[] { typeof(VMNetEODMessageCmd) });
                var activeField = type.GetField("ActiveEOD");
                if (deliver == null || activeField == null)
                {
                    bridge.UnavailableReason = ControllerTypeName + " has changed shape (OnEODMessage / ActiveEOD moved or vanished)";
                    return bridge;
                }

                //Optional, and deliberately so: a fork that renames or drops ActivePID costs the
                //window its title and nothing else, which is a better outcome than refusing to
                //open the minigames at all.
                var activeID = type.GetField("ActivePID");

                //The gate is what turns EODPluginPolicy's exclusion list into behaviour, and the
                //error sink is what puts a refusal in the log rather than nowhere. Both are
                //statics on the controller, installed here rather than passed in so that they
                //apply to every panel regardless of who opened it.
                var gate = type.GetField("PluginGate");
                var error = type.GetField("PluginError");
                gate?.SetValue(null, new Func<uint, string>(EODPluginPolicy.Gate));
                error?.SetValue(null, new Action<string, Exception>((message, ex) => SimitoneLog.Warn(message, ex)));

                var controller = ctor.Invoke(new object[] { lot, vm });
                bridge.Controller = (UIContainer)controller;
                bridge.Deliver = (Action<VMNetEODMessageCmd>)Delegate.CreateDelegate(typeof(Action<VMNetEODMessageCmd>), controller, deliver);
                bridge.ActivePanelField = activeField;
                bridge.ActiveIDField = activeID;
                bridge.UnavailableReason = null;
                return bridge;
            }
            catch (Exception e)
            {
                //Anything at all: a missing dependency, a trimmed assembly, a rename in a fork
                //update. The feature is optional; the lot is not.
                bridge.Controller = null;
                bridge.UnavailableReason = "the EOD panel assembly could not be loaded (" + e.GetType().Name + ")";
                SimitoneLog.Error("EOD minigame panels unavailable: " + bridge.UnavailableReason, e);
                return bridge;
            }
        }

        /// <summary>
        /// Hands a plugin message to FSO's controller. Safe to call when the tree was never
        /// reached, which is what lets the game screen subscribe unconditionally.
        /// </summary>
        public void OnEODMessage(VMNetEODMessageCmd cmd)
        {
            if (!Available || cmd == null) return;
            try
            {
                Deliver(cmd);
            }
            catch (Exception e)
            {
                //A panel that throws while handling a message must not take the game thread with
                //it. Construction is guarded inside the controller; this guards everything after.
                SimitoneLog.Error("EOD message '" + cmd.EventName + "' (plugin " + cmd.PluginID.ToString("X8") + ") failed", e);
            }
            RefreshActive();
        }

        /// <summary>
        /// Re-reads which panel, if any, the controller currently has open.
        /// </summary>
        /// <remarks>
        /// Called every frame rather than only on a message, because a panel can also go away on
        /// its own: a close button inside the panel calls <c>UIEODController.CloseEOD</c>, which
        /// drops it without any further traffic. Reading one field per frame is cheaper than
        /// leaving a stale panel on screen with the sim's thread still blocked behind it.
        /// </remarks>
        public void RefreshActive()
        {
            if (!Available) return;
            ActivePanel = null;
            ActivePanelID = 0;
            try
            {
                ActivePanel = ActivePanelField.GetValue(Controller) as UIContainer;
            }
            catch (Exception e)
            {
                SimitoneLog.Error("EOD panel state could not be read", e);
                ActivePanel = null;
            }
            if (ActivePanel == null || ActiveIDField == null) return;
            try
            {
                ActivePanelID = Convert.ToUInt32(ActiveIDField.GetValue(Controller));
            }
            catch (Exception e)
            {
                //A missing or renamed ActivePID costs the window its title. Not worth a log line
                //every frame for a field the fork may simply not have.
                ActivePanelID = 0;
            }
        }

        /// <summary>
        /// Closes the open panel the way its own close button would, so the plugin is told
        /// "close" rather than being left with a window that vanished.
        /// </summary>
        public void CloseActive()
        {
            if (ActivePanel == null) return;
            try
            {
                if (CloseMethod == null)
                {
                    CloseMethod = ActivePanel.GetType().GetMethod("OnClose", Type.EmptyTypes);
                }
                if (CloseMethod != null)
                {
                    CloseMethod.Invoke(ActivePanel, null);
                }
            }
            catch (Exception e)
            {
                SimitoneLog.Error("EOD panel failed to close cleanly", e);
            }
            finally
            {
                ActivePanel = null;
            }
        }
    }

    /// <summary>
    /// Simitone's end-of-day minigame host: the window a retail TS1 EOD object (a sign, the dance
    /// floor, a slot machine) puts up while the sim is using it.
    ///
    /// <para>
    /// The minigame framework is two mirrored halves. The authoritative half,
    /// <c>FSO.SimAntics.NetPlay.EODs</c>, already runs inside Simitone: the lot is driven by
    /// FreeSO's <em>server</em> VM, so <c>VM.EODHost</c> is constructed, ticked every frame, and
    /// the plugins for the retail TS1 EODs load when a sim uses one. That half was never
    /// TSO-only. This class is the missing other half - the presentational one - and the reason
    /// none of it was reachable is a single commented-out line in <c>TS1GameScreen</c>:
    /// <c>//vm.OnEODMessage += LotControl.EODs.OnEODMessage;</c>.
    /// </para>
    ///
    /// <para>
    /// What this adds over FSO's own <c>UILotControl.EODs</c> is everything live mode would have
    /// done. FSO shows an EOD inside <c>UILiveMode</c>'s frame, which picks the panel up, gives
    /// it its size, and puts a close button on it. Simitone's live mode is a different control
    /// with a different job, so the show/hide and the placement live here instead. The rest -
    /// building the panel, routing its events, the exclusion list - is FSO.Client's and is used
    /// unmodified.
    /// </para>
    ///
    /// <para>
    /// <b>Off-safe.</b> Constructing this never throws. It asks
    /// <see cref="EODPluginPolicy.Decide"/> whether the framework can start and opens
    /// <see cref="EODPanelBridge"/> to see whether the panels can be reached; if either answer is
    /// no, <see cref="Started"/> is false, <see cref="OnEODMessage"/> drops everything, the
    /// container stays invisible, the lot is what it was before, and the reason is in the log.
    /// </para>
    /// </summary>
    public class EODHost : UIContainer
    {
        private readonly EODPanelBridge Bridge;
        private readonly EODPanelFrame Frame;

        /// <summary>Whether the framework came up. See <see cref="EODStartDecision.CanStart"/>.</summary>
        public bool Started { get { return Bridge != null && Bridge.Available; } }

        /// <summary>
        /// Why the framework did not start, in one sentence, for the log. Null when it did.
        /// </summary>
        public string StartRefusal { get; private set; }

        /// <summary>How many plugins this lot may open, and which layouts were missing.</summary>
        public EODStartDecision Decision { get; private set; }

        private bool WasShowing;
        private int LastWidth;
        private int LastHeight;
        private UIContainer LastPlaced;

        /// <summary>
        /// Builds a host for a lot, or an inert one if the framework cannot start here.
        /// </summary>
        /// <param name="vm">The lot's VM. Null is treated as "no host available".</param>
        /// <param name="lot">
        /// The lot control the panels should read the active sim from. It only has to be an
        /// <see cref="ILotControl"/>, which is what keeps this working without an FSO.Client
        /// <c>IGameScreen</c> anywhere in the path.
        /// </param>
        public EODHost(VM vm, ILotControl lot)
        {
            //assigned up front because both refusal paths below return before the bridge is
            //opened, and an unstarted host still has to be a usable object.
            Bridge = null;
            Frame = null;
            Decision = EODPluginPolicy.Decide(vm?.EODHost != null, EODPluginPolicy.LayoutExistsOnDisk);

            if (!Decision.CanStart)
            {
                Refuse(Decision.Reason, Decision.MissingLayouts);
                return;
            }

            Bridge = EODPanelBridge.TryOpen(vm, lot);
            if (Bridge == null || !Bridge.Available)
            {
                Refuse(Bridge?.UnavailableReason ?? "the EOD panel bridge could not be built", Decision.MissingLayouts);
                return;
            }

            //Order matters: the controller holds the panel, so the frame has to sit between this
            //host and the controller to be able to draw a title over it - but the frame is a
            //sibling, not a parent, because re-parenting a panel out of FSO.Client's controller
            //would take it out of the tree the controller's own show/hide and close paths walk.
            Frame = new EODPanelFrame();
            Frame.OnCloseRequested += () => Bridge.CloseActive();
            Add(Bridge.Controller);
            Add(Frame);
            Visible = false;

            SimitoneLog.Info("EOD minigame framework is on: " + Decision.EligiblePlugins
                + " of " + EODPluginPolicy.KnownPlugins.Count + " plugins available, "
                + (Decision.MissingLayouts.Count == 0
                    ? "all layouts present"
                    : Decision.MissingLayouts.Count + " of " + EODPluginPolicy.RequiredLayouts.Count + " layouts missing")
                + "; " + EODPluginPolicy.AuthoredLayouts.Count + " of "
                + EODPluginPolicy.RequiredLayouts.Count + " layouts authored by this build");
        }

        private void Refuse(string reason, IEnumerable<string> missing)
        {
            StartRefusal = reason;
            Visible = false;

            var missingList = (missing ?? Enumerable.Empty<string>()).ToList();
            SimitoneLog.Warn("EOD minigame framework is off on this lot: " + reason
                + (missingList.Count == 0
                    ? ""
                    : " (missing " + string.Join(", ", missingList.Take(4).ToArray())
                        + (missingList.Count > 4 ? ", ..." : "") + ")"));
        }

        /// <summary>
        /// Hands a plugin message to FSO's controller, which builds or tears down the panel.
        /// </summary>
        /// <remarks>
        /// The subscription lives on the game screen rather than here, because
        /// <c>VM.OnEODMessage</c> is the screen's to own - see
        /// <c>TS1GameScreen.CleanupLastWorld</c> - and this host is the screen's child. This
        /// method is null-safe and silent when the framework is off, so the screen can subscribe
        /// unconditionally.
        /// </remarks>
        public void OnEODMessage(VMNetEODMessageCmd cmd)
        {
            Bridge?.OnEODMessage(cmd);
        }

        /// <summary>
        /// Closes the open panel the way its own close button would, so the plugin is told
        /// "close" rather than being left with a window that vanished. The game screen calls this
        /// on the way off a lot: the sim's interaction thread is blocked on the plugin until
        /// something tells it the interaction is over, and leaving the lot is that something.
        /// </summary>
        public void CloseActive()
        {
            Bridge?.CloseActive();
        }

        /// <summary>
        /// Shows the active panel when a plugin has one, hides it when the plugin leaves, keeps it
        /// centred, and lets the player back out with Escape.
        /// </summary>
        public override void Update(UpdateState state)
        {
            base.Update(state);

            if (Bridge == null) return;

            Bridge.RefreshActive();
            var active = Bridge.ActivePanel;
            var showing = active != null;

            if (showing != WasShowing)
            {
                WasShowing = showing;
                Visible = showing;
                //The frame is a sibling of the panel, not its parent, so it does not follow the
                //panel out of view by itself; a title and a close button for a minigame that has
                //ended would be a button that closes nothing.
                if (Frame != null && !showing) Frame.Wrap(null, null);
                if (showing)
                {
                    //An EOD is modal over the whole lot: the sim is mid-interaction and the
                    //frontend bars would sit on top of it otherwise. The host re-raises itself
                    //rather than trusting the order it happened to be added in - Add() on a child
                    //already in the list moves it to the end, which is the top - because the two
                    //paths that build the frontend (the template load and the marshal resume) each
                    //add it after this host.
                    if (Parent is UIContainer parent) parent.Add(this);
                }
            }

            if (showing && state != null && state.NewKeys != null && state.NewKeys.Contains(Keys.Escape))
            {
                //The retail panels carry their own close button in their layout, but a layout
                //Simitone does not ship may not have one, and the sim's interaction thread stays
                //blocked on the plugin until something closes it. OnClose is the protocol's own
                //"close" event, so this is the same path that button takes. The frame's own close
                //button goes through exactly this path, which is why the frame is not a second
                //way out - it is the same one.
                Bridge.CloseActive();
                return;
            }

            if (showing) Place(active);
        }

        private void Place(UIContainer active)
        {
            var width = GlobalSettings.Default.GraphicsWidth;
            var height = GlobalSettings.Default.GraphicsHeight;
            if (width == LastWidth && height == LastHeight && ReferenceEquals(active, LastPlaced)) return;
            LastWidth = width;
            LastHeight = height;
            LastPlaced = active;
            if (active == null) return;

            Size = new Vector2(width, height);

            //The panel's own Size is always zero - UIContainer does not maintain one, it inherits
            //UIElement's, which is a stub - so the extent has to be walked. Getting this wrong is
            //not cosmetic: centring a zero-sized panel puts its top-left corner in the middle of
            //the screen and pushes most of a wide panel off the right-hand edge.
            var extent = EODPanelFrame.Extent(active);
            var topLeft = EODPluginPolicy.Centre(new Vector2(width, height), extent);
            active.Position = topLeft;

            if (Frame == null) return;
            Frame.Wrap(active, TitleFor(Bridge.ActivePanelID));
            //The frame's origin is its own, and the title strip sits above the panel, so the
            //panel's own top-left moves down by the strip's height once wrapped.
            Frame.Position = topLeft;
            active.Position = topLeft + new Vector2(0, EODPanelFrame.TitleHeight + EODPanelFrame.Padding);
        }

        /// <summary>
        /// What to call the panel that is open: the title from the layout this build authored
        /// under the name the panel asked for, or null for a panel whose layout is somebody
        /// else's.
        /// </summary>
        private string TitleFor(uint pluginID)
        {
            foreach (var layout in EODPluginPolicy.LayoutsFor(pluginID))
            {
                var spec = EODLayouts.For(layout);
                if (spec != null) return spec.Title;
            }
            return null;
        }

        /// <summary>
        /// Re-centres after a resize. A <c>.uis</c> panel is authored at absolute coordinates, so
        /// nothing else in the tree would move it.
        /// </summary>
        public override void GameResized()
        {
            base.GameResized();
            LastWidth = 0;
            LastHeight = 0;
            LastPlaced = null;
            Frame?.Reflow();
            if (Bridge?.ActivePanel != null) Place(Bridge.ActivePanel);
        }
    }
}
