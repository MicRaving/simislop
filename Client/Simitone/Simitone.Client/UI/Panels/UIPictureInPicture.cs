/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using FSO.LotView;
using FSO.SimAntics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;

namespace Simitone.Client.UI.Panels
{
    /// <summary>Which corner of the screen the picture-in-picture window is parked in.</summary>
    public enum PipCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>What the picture-in-picture window is showing.</summary>
    public enum PipFeed
    {
        /// <summary>
        /// The live cropped lot view when it can be read, otherwise the subject's headline
        /// render, otherwise nothing. This is what the trigger API uses.
        /// </summary>
        Auto,

        /// <summary>A live, magnified crop of the current frame of the lot being played.</summary>
        LotView,

        /// <summary>
        /// The subject's own rendered headline - the balloon / skill / mood sprite the VM
        /// already renders to a texture for the sim in the world.
        /// </summary>
        Headline
    }

    /// <summary>
    /// Which texture the live lot view is cropped out of. The two are not interchangeable: they
    /// have different sizes, and the focus point has to be scaled differently for each, so the
    /// choice is made once per frame by <see cref="PictureInPictureFraming.ResolveLotSource"/>
    /// and only then handed to the crop.
    /// </summary>
    public enum PipLotSource
    {
        /// <summary>Nothing readable: no lot to show, so the window falls back.</summary>
        None,

        /// <summary>
        /// The backbuffer itself. Only legal when the player's anti-aliasing left it
        /// non-multisampled, which is AA 0 and the 3D camera's supersampled modes.
        /// </summary>
        Backbuffer,

        /// <summary>
        /// A copy of the backbuffer taken by <c>PPXDepthEngine.CaptureLotSnapshot</c> into a
        /// plain, non-multisampled render target. This is the normal case and the one that works
        /// at every anti-aliasing setting.
        /// </summary>
        Snapshot
    }

    /// <summary>
    /// The picture-in-picture window, and the pure arithmetic behind it.
    ///
    /// <para>
    /// The idea is a small always-on-top window that spotlights something worth watching
    /// without pausing the game - a sim doing something noteworthy. There is no picture-in-
    /// picture in The Sims 1, so this is a new Simitone feature rather than a port.
    /// </para>
    ///
    /// <para>
    /// <b>What it renders.</b> Two feeds, both read out of the engine's existing render path
    /// rather than a second graphics pipeline of our own:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <see cref="PipFeed.LotView"/> blits a magnified, aspect-correct crop of a copy of
    /// <c>PPXDepthEngine.GetBackbuffer()</c>. That render target is the one the lot view has
    /// already drawn into (<c>World.PreDraw</c> renders it, <c>PPXDepthEngine.DrawBackbuffer</c>
    /// blits it to the screen in <c>World.Draw</c>), and the copy is taken out of it into a
    /// plain non-multisampled target the window owns. The crop is centred on the subject sim's
    /// projected position.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="PipFeed.Headline"/> shows <c>VMEntity.WorldUI.Headline</c> - the texture
    /// <c>UIHeadlineRenderer.DrawFrame</c> already produced for the sim in the world this
    /// tick. It animates, so it is a live view of the event rather than a still.
    /// </description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// The lot feed is fed from a texture chosen every frame by
    /// <see cref="PictureInPictureFraming.ResolveLotSource"/>, and that choice is the whole
    /// reason the live view works at every anti-aliasing setting rather than only at AA 0.
    /// <see cref="PipFeedUnavailableReason"/> records why it could not be had.
    /// </para>
    ///
    /// <para>
    /// <b>Which texture, and why there are two of them.</b> The lot view lives in
    /// <c>PPXDepthEngine.GetBackbuffer()</c>, and <c>World.ChangeAAMode</c> creates that
    /// backbuffer with 4x MSAA for AA 1 and with 8x MSAA for AA 2 in the 2D camera - so in the
    /// default camera it is a multisampled render target for every anti-aliasing setting except
    /// "off". An earlier version of this window took that as "a multisampled render target is not
    /// a legal texture binding", refused the lot feed, and documented that the live view needed AA
    /// off. <b>That was wrong.</b> The engine itself binds the multisampled backbuffer as a
    /// texture every frame - <c>PPXDepthEngine.DrawBackbuffer</c> hands it to a plain
    /// <c>SpriteBatch</c>, and <c>SSAADownsample.Draw</c> hands it to a shader - and MonoGame
    /// resolves it on the bind. So the restriction was never in the device; it was a guess in
    /// this file.
    /// </para>
    ///
    /// <para>
    /// <b>What it does instead.</b> Rather than depend on that, the window asks
    /// <c>PPXDepthEngine.CaptureLotSnapshot</c> for a copy of the backbuffer in a small,
    /// permanently non-multisampled render target, and crops out of that. It is the same blit the
    /// engine already performs on the way to the screen, done once per frame at picture-window
    /// size rather than at screen size, and it buys three things: the sampled texture is a plain
    /// one whatever the player's graphics settings are, the resolve happens at a point the window
    /// chooses rather than wherever a driver decides, and the per-frame sampling cost is bounded
    /// by the window rather than by a 4K screen target. The backbuffer is still read directly as
    /// a second choice, for the case where the copy could not be made, so nothing that used to
    /// work has been taken away.
    /// </para>
    ///
    /// <para>
    /// <b>Where the copy is taken from.</b> The snapshot is captured from the window's update,
    /// not its draw, because that is the only point where no sprite batch is open and no
    /// <c>UISpriteBatch</c> transform is on the stack - <c>UISpriteBatch.Pause</c> /
    /// <c>Resume</c> does not restore a pushed transform matrix, so a copy taken mid-draw would
    /// corrupt the UI around it. The cost is that the snapshot holds the lot as of the previous
    /// frame, under 20ms at 60Hz, which a magnifier of a lot view cannot show.
    /// </para>
    ///
    /// <para>
    /// <b>What the fallback says.</b> The subject's headline is the fallback, because it is the
    /// sim's own live event and the VM already rendered it this tick; the status strip then says
    /// HEADLINE rather than pretending to be live. When there is no headline either - no headline
    /// fired this tick, no sim selected, the lot changed - the window does not sit there blank:
    /// the status strip names the cause (AA ON, NO LOT, FEED ERROR) and the caption strip says
    /// what would fix it where that is knowable ("Try AA off for the live view"). The whole decision is
    /// <see cref="PipFeedFallback.Decide"/>, which is pure and unit tested; this class only reads
    /// the graphics state and draws the answer.
    /// </para>
    ///
    /// <para>
    /// <b>What a real camera still needs.</b> This is a close-up of the lot view the player is
    /// already looking at, not an independent camera. A true event camera - one that can frame
    /// a sim the player has scrolled away from, that follows a subject, or that renders the lot
    /// from a different camera - needs the lot view drawn a second time into a second render
    /// target with a second <c>ICamera</c>. The engine has the pieces
    /// (<c>FSO.Common.Rendering.Framework._3DTargetScene</c> plus an <c>ICamera</c> is exactly
    /// that shape) but not the seam: <c>World.InternalDraw</c> is protected, the world's
    /// <c>State._2D</c> batch and <c>State.Camera2D</c> are shared per-frame mutable state, and
    /// the backbuffer is a process-wide static. Rendering the world twice per frame therefore
    /// needs FreeSO changes, which is out of scope here. Until then the subject is followed by
    /// cropping, so a subject scrolled towards the edge of the screen sits at the edge of the
    /// window rather than in the middle of it.
    /// </para>
    ///
    /// <para>
    /// The one engine call that would take a real step towards it is
    /// <c>World.GetObjectThumb(objects, positions, gd, state)</c>: it renders named world objects
    /// at near zoom, through its own camera, into a dedicated non-multisampled buffer, and is
    /// already reachable from the client. It is deliberately not used here. It invalidates the
    /// whole world camera and every object sprite to do it
    /// (<c>state.WorldSpace.Invalidate</c>, <c>obj.OnRotationChanged</c> /
    /// <c>OnZoomChanged</c>), so it is a once-per-interaction cost rather than something a window
    /// can call every frame, and it has to be issued from a safe point in the update loop, not
    /// from a draw. A picture-in-picture that renders the lot independently every frame is still
    /// a FreeSO change.
    /// </para>
    ///
    /// <para>
    /// <b>Hosting.</b> The window is self-contained: it resolves the current screen itself and
    /// can be added to any <c>UIContainer</c> on the lot screen. The intended line is in
    /// <c>UISimitoneFrontend</c>'s constructor. A static trigger surface is exposed for the
    /// headline / event system, and F9 toggles the window for manual testing.
    /// </para>
    /// </summary>
    public sealed class UIPictureInPicture : UIContainer
    {
        /// <summary>
        /// The live instance, or null when nothing is hosting the window. The static trigger
        /// methods below are how the headline / event system reaches the window without
        /// holding a reference to it.
        /// </summary>
        public static UIPictureInPicture Current { get; private set; }

        // ---- trigger surface --------------------------------------------------------------

        /// <summary>How long <see cref="Notify(VMEntity, string)"/> holds the window open.</summary>
        public const float DefaultHoldSeconds = 5f;

        /// <summary>
        /// Shows the window on <paramref name="subject"/> under <paramref name="caption"/>.
        /// Returns false when no window is being hosted, which is the only way a caller can
        /// tell its event was not picked up; that is logged rather than thrown.
        /// </summary>
        public static bool Show(VMEntity subject, string caption)
        {
            if (Current == null)
            {
                SimitoneLog.Warn("Picture-in-picture show was requested but no window is hosted " +
                    "(caption '" + (caption ?? "") + "'). Add a UIPictureInPicture to the lot screen's UI.");
                return false;
            }
            return Current.Open(subject, caption, 0f);
        }

        /// <summary>
        /// Shows the window for <paramref name="holdSeconds"/> and then takes it down again -
        /// the "a sim did a noteworthy thing, look" call the headline system wants. A
        /// non-positive hold time leaves it up until something hides it, which is what
        /// <see cref="Show"/> does.
        /// </summary>
        /// <returns>False when no window is being hosted.</returns>
        public static bool Notify(VMEntity subject, string caption, float holdSeconds)
        {
            if (Current == null)
            {
                SimitoneLog.Info("Picture-in-picture notification '" + (caption ?? "") +
                    "' had nowhere to go: no window is hosted.");
                return false;
            }
            return Current.Open(subject, caption, holdSeconds);
        }

        /// <summary>Same as <see cref="Notify(VMEntity, string, float)"/> with a five second hold.</summary>
        public static bool Notify(VMEntity subject, string caption)
        {
            return Notify(subject, caption, DefaultHoldSeconds);
        }

        /// <summary>Closes the window. Returns false when no window is being hosted.</summary>
        public static bool Hide()
        {
            return Current != null && Current.Close();
        }

        /// <summary>Opens the window if it is closed, closes it if it is open.</summary>
        public static bool Toggle(VMEntity subject, string caption)
        {
            if (Current == null)
            {
                SimitoneLog.Warn("Picture-in-picture toggle was requested but no window is hosted.");
                return false;
            }
            return Current.ToggleWindow(subject, caption);
        }

        // ---- state -----------------------------------------------------------------------

        /// <summary>
        /// The show/hide state machine, with no UI in it. Split out so the rules - what a
        /// repeated <c>Show</c> does, when a timed show takes itself down - are testable
        /// without a graphics device.
        /// </summary>
        private readonly PictureInPictureFraming.PictureInPictureState State
            = new PictureInPictureFraming.PictureInPictureState();

        private readonly UILabel TitleLabel;
        private readonly UILabel CaptionLabel;
        private readonly UIMouseEventRef MouseRegion;

        private TS1GameScreen Game;
        private PipFeed RequestedFeed = PipFeed.Auto;
        private PipFeed _ActiveFeed = PipFeed.Auto;
        private float _Magnification = 1f;
        private float _HoldSeconds;
        //null until the first Layout, so the label sizes are set even for a window opened without a caption
        private bool? LastHadCaption;
        private PipFeedUnavailableReason LoggedReason;
        private string _StatusText = PipFeedFallback.StatusFor(PipFeed.Auto, PipFeedUnavailableReason.None);
        private string _FallbackCaption;

        /// <summary>
        /// This frame's copy of the lot view: a plain, non-multisampled texture that
        /// <c>PPXDepthEngine.CaptureLotSnapshot</c> produced, and the source the live feed crops.
        /// Null when the copy could not be made, in which case the feed falls back to reading the
        /// backbuffer directly or, failing that, to the headline.
        /// </summary>
        private Texture2D LotSnapshot;

        /// <summary>Which texture the live feed cropped, this frame. For tests and debugging.</summary>
        public PipLotSource LotSource { get; private set; } = PipLotSource.None;

        /// <summary>Which corner the window parks in. Re-placed on show and on resize.</summary>
        public PipCorner Corner = PipCorner.BottomRight;

        /// <summary>
        /// The debug toggle. F8 and F11 are already taken in the lot control (lot thumbnail,
        /// collision verification) and F12 switches graphics mode.
        /// </summary>
        public const Keys ToggleKey = Keys.F9;

        /// <summary>Which feed to ask for. <see cref="PipFeed.Auto"/> takes the best available.</summary>
        public PipFeed Feed
        {
            get { return RequestedFeed; }
            set { RequestedFeed = value; }
        }

        /// <summary>Which feed is being drawn this frame. Drives the status strip.</summary>
        public PipFeed ActiveFeed
        {
            get { return _ActiveFeed; }
        }

        /// <summary>
        /// How much of the lot view the window shows, in UI units. 1 is one window-sized crop,
        /// 2 is a twice-tighter crop, 0.5 a wider one. Clamped, because a crop wider than the
        /// backbuffer is just a stretched copy of it.
        /// </summary>
        public float Magnification
        {
            get { return _Magnification; }
            set { _Magnification = PictureInPictureFraming.ClampMagnification(value); }
        }

        /// <summary>
        /// The hold currently in force, i.e. the <c>holdSeconds</c> of the notification that
        /// opened the window. Zero means sticky. Read-only: it belongs to the open, so it is set
        /// by <see cref="Open(VMEntity, string, float)"/> and cleared by <see cref="Close"/>.
        /// </summary>
        public float HoldSeconds
        {
            get { return _HoldSeconds; }
        }

        /// <summary>Whether the window is up. Mirrors <c>Visible</c>, which also gates drawing.</summary>
        public bool IsOpen
        {
            get { return State.IsVisible; }
        }

        /// <summary>The sim being spotlit, or null for "whatever the player has selected".</summary>
        public VMEntity Subject
        {
            get { return State.Subject; }
        }

        /// <summary>The text under the view: already tidied and truncated to fit the strip.</summary>
        public string Caption
        {
            get { return State.Caption; }
        }

        /// <summary>
        /// The caption actually on show: the caller's, unless the live feed is unavailable and
        /// there is no headline to show either, in which case the window explains itself. The
        /// fallback only ever replaces the caption when there is nothing to draw, so a window with
        /// a real feed never has text about the fallback under it.
        /// </summary>
        public string EffectiveCaption
        {
            get { return _FallbackCaption ?? State.Caption; }
        }

        // ---- construction ----------------------------------------------------------------

        /// <param name="game">
        /// The lot screen. Optional: when null the window resolves the current screen itself
        /// every frame, so it can be hosted anywhere in the screen tree.
        /// </param>
        public UIPictureInPicture(TS1GameScreen game)
        {
            ID = "simitone.pictureinpicture";
            Game = game;

            TitleLabel = new UILabel();
            TitleLabel.Alignment = TextAlignment.Left | TextAlignment.Middle;
            TitleLabel.CaptionStyle = TitleLabel.CaptionStyle.Clone();
            TitleLabel.CaptionStyle.Size = 11;
            TitleLabel.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            TitleLabel.Position = new Vector2(PictureInPictureFraming.Border, PictureInPictureFraming.Border);
            Add(TitleLabel);

            CaptionLabel = new UILabel();
            CaptionLabel.Alignment = TextAlignment.Center | TextAlignment.Middle;
            CaptionLabel.CaptionStyle = CaptionLabel.CaptionStyle.Clone();
            CaptionLabel.CaptionStyle.Size = 12;
            CaptionLabel.CaptionStyle.Color = UIStyle.Current.Text;
            Add(CaptionLabel);

            var full = PictureInPictureFraming.TotalSize(true);
            MouseRegion = ListenForMouse(
                new Rectangle(0, 0, (int)full.X, (int)full.Y), OnMouse);

            Opacity = 0f;
            Visible = false;

            if (Current != null)
            {
                SimitoneLog.Warn("A second picture-in-picture window was constructed while one was " +
                    "already hosted; the newest becomes the trigger target.");
            }
            Current = this;
        }

        // ---- public API ------------------------------------------------------------------

        /// <summary>
        /// Shows the window on <paramref name="subject"/> under <paramref name="caption"/>,
        /// taking it down again after <paramref name="holdSeconds"/> unless it is zero or less.
        /// The hold is a parameter rather than a field because it is a property of one
        /// notification, not of the window: a plain show after a timed one must be sticky again,
        /// or the next manual toggle would disappear on its own.
        /// </summary>
        /// <returns>True when this changed what the window was showing.</returns>
        public bool Open(VMEntity subject, string caption, float holdSeconds)
        {
            if (!State.Show(subject, caption, holdSeconds)) return false;

            _HoldSeconds = holdSeconds;
            // The fallback caption belongs to the feed that was up last frame, not to the
            // notification that is opening the window now: the caption strip underneath a newly
            // opened window has to be sized and filled for *this* subject. The next ResolveFeed
            // re-derives it, and until then the caller's own caption is the honest one to show.
            _FallbackCaption = null;
            Visible = true;
            Layout();
            return true;
        }

        /// <summary>Hides the window.</summary>
        public bool Close()
        {
            if (!State.Hide()) return false;
            _HoldSeconds = 0f;
            Visible = false;
            return true;
        }

        /// <summary>Opens the window if it is closed, closes it if it is open.</summary>
        public bool ToggleWindow(VMEntity subject, string caption)
        {
            if (State.IsVisible) return Close();
            return Open(subject, caption, 0f);
        }

        // ---- layout ----------------------------------------------------------------------

        private void Layout()
        {
            var screen = ResolveScreen();
            var hasCaption = EffectiveCaption != null;
            var total = PictureInPictureFraming.TotalSize(hasCaption);

            var pos = PictureInPictureFraming.Place(
                screen?.ScreenWidth ?? 0, screen?.ScreenHeight ?? 0,
                total, Corner, PictureInPictureFraming.ScreenMargin);
            if (X != pos.X) X = pos.X;
            if (Y != pos.Y) Y = pos.Y;

            if (hasCaption != LastHadCaption)
            {
                LastHadCaption = hasCaption;
                TitleLabel.Size = new Vector2(total.X - PictureInPictureFraming.Border, PictureInPictureFraming.TitleHeight);
                CaptionLabel.Visible = hasCaption;
                CaptionLabel.Size = new Vector2(PictureInPictureFraming.ViewWidth, PictureInPictureFraming.CaptionHeight);
                CaptionLabel.Position = new Vector2(
                    PictureInPictureFraming.Border,
                    PictureInPictureFraming.Border + PictureInPictureFraming.TitleHeight + PictureInPictureFraming.ViewHeight);
            }

            MouseRegion.Region = new Rectangle(0, 0, (int)total.X, (int)total.Y);
        }

        public override void GameResized()
        {
            base.GameResized();
            if (State.IsVisible) Layout();
        }

        private TS1GameScreen ResolveScreen()
        {
            if (Game != null) return Game;
            try
            {
                Game = GameFacade.Screens?.CurrentUIScreen as TS1GameScreen;
            }
            catch (Exception ex)
            {
                // A broken static must not take the frame down: the window just goes to the
                // top left of nothing and reports NO SIGNAL.
                SimitoneLog.Warn("Picture-in-picture could not resolve the current screen.", ex);
                Game = null;
            }
            return Game;
        }

        // ---- update ----------------------------------------------------------------------

        public override void Update(UpdateState state)
        {
            base.Update(state);

            // The window is updated even while closed, so the debug toggle still works.
            // UIContainer.Update ticks children regardless of Visible, so nothing has to be
            // re-armed on show.
            var nofocus = state?.InputManager == null || state.InputManager.GetFocus() == null;
            if (nofocus && state?.NewKeys != null && state.NewKeys.Contains(ToggleKey))
            {
                // Re-showing with whatever it was already showing, so pressing the key twice is
                // a clean close-then-open round trip rather than an open on a stale subject.
                ToggleWindow(State.Subject ?? DefaultSubject(), State.Caption);
            }

            if (!State.IsVisible) return;

            if (State.Tick(FrameSeconds(state)))
            {
                SimitoneLog.Info("Picture-in-picture held window '" + (State.Caption ?? "") +
                    "' for its full " + _HoldSeconds + "s and closed itself.");
                Close();
            }

            // Resolved before the layout, because the fallback can add a caption strip and the
            // window has to be sized for the text it is about to show.
            ResolveFeed();
            Layout();
            UpdateLabels();
        }

        private static float FrameSeconds(UpdateState state)
        {
            var elapsed = state?.Time?.ElapsedGameTime.TotalSeconds;
            if (!elapsed.HasValue) return 1f / 60f;
            // The same clamp the lot screen applies to its frame delta, so one stalled frame
            // cannot swallow the whole hold time in a single step.
            return LotWeatherState.ClampDelta((float)elapsed.Value);
        }

        /// <summary>
        /// Works out which feed can actually be drawn this frame, and takes the lot snapshot the
        /// live feed is drawn from. The lot feed wins whenever there is a readable lot at all,
        /// because it is the one that shows the sim; the headline is the fallback for when there
        /// is not, and a caption naming the cause is the last resort.
        ///
        /// <para>
        /// The decision itself is <see cref="PipFeedFallback.Decide"/>, which is pure: this method
        /// is only the part that has to touch the graphics device, and all it does with the answer
        /// is keep the log from repeating itself. The log is keyed on the
        /// <em>reason</em> rather than on a flag, so a player who toggles anti-aliasing gets one
        /// line per distinct cause and no second copy of the same sentence sixty times a second.
        /// </para>
        ///
        /// <para>
        /// The capture happens here rather than in the draw, for the reason the class remarks give:
        /// this is the one point in the frame where no UI sprite batch is open and no
        /// <c>UISpriteBatch</c> transform is on the stack. The snapshot therefore holds the lot as
        /// of the previous frame.
        /// </para>
        /// </summary>
        private void ResolveFeed()
        {
            var world = ResolveScreen()?.LotControl?.World;
            var bb = PPXDepthEngine.GetBackbuffer();

            // A new backbuffer instance means the engine re-created it - a graphics mode change,
            // a resolution change, a device reset. That is the only thing that can make a
            // previously unreadable one readable again, so it is where the feed that threw gets
            // its second chance.
            if (!ReferenceEquals(bb, LastBackbuffer))
            {
                LastBackbuffer = bb;
                LotFeedDisabled = false;
            }

            var samples = bb?.MultiSampleCount ?? 0;

            // The copy is asked for at every anti-aliasing setting, which is the point of it: it is
            // taken into a target that is not multisampled, so whether the backbuffer could have
            // been read directly does not enter into the question. Twice the picture area so that
            // a magnification above 1 has real source pixels to magnify.
            LotSnapshot = SnapshotSource(PPXDepthEngine.CaptureLotSnapshot(
                PictureInPictureFraming.ViewWidth * 2, PictureInPictureFraming.ViewHeight * 2));

            var source = PictureInPictureFraming.ResolveLotSource(
                LotSnapshot != null,
                PictureInPictureFraming.CanSample(bb != null, samples));

            var reason = PipFeedFallback.DiagnoseLotFeed(
                world != null, world?.Visible ?? false, bb != null, samples, LotFeedDisabled,
                source != PipLotSource.None);

            var decision = PipFeedFallback.Decide(
                RequestedFeed, reason, HeadlineTexture(EffectiveSubject()) != null, samples, LoggedReason);

            if (decision.ShouldReport)
            {
                LoggedReason = decision.Reason;
                SimitoneLog.Info(decision.LogMessage);
            }

            _ActiveFeed = decision.Feed;
            _StatusText = decision.StatusText;
            _FallbackCaption = decision.CaptionOverride;
        }

        /// <summary>
        /// Turns whatever <c>PPXDepthEngine.CaptureLotSnapshot</c> handed back into a texture that
        /// is safe to sample, or null. The engine's answer can be a target that has since been
        /// disposed - a resize from the window thread retires the previous one - and a disposed
        /// render target is the one thing that would take the draw down rather than just the feed.
        /// </summary>
        private static Texture2D SnapshotSource(Texture2D snapshot)
        {
            if (snapshot == null || snapshot.IsDisposed) return null;
            if (snapshot.Width <= 0 || snapshot.Height <= 0) return null;
            return snapshot;
        }

        private void UpdateLabels()
        {
            var caption = EffectiveCaption;
            if (CaptionLabel.Caption != caption) CaptionLabel.Caption = caption;

            if (TitleLabel.Caption != _StatusText) TitleLabel.Caption = _StatusText;
        }

        /// <summary>The named subject, or the player's selection when none was named.</summary>
        private VMEntity EffectiveSubject()
        {
            return State.Subject ?? DefaultSubject();
        }

        private VMEntity DefaultSubject()
        {
            return ResolveScreen()?.LotControl?.ActiveEntity;
        }

        // ---- draw ------------------------------------------------------------------------

        public override void Draw(UISpriteBatch batch)
        {
            if (!Visible) return;

            var white = TextureGenerator.GetPxWhite(batch.GraphicsDevice);
            var total = PictureInPictureFraming.TotalSize(EffectiveCaption != null);
            var viewY = PictureInPictureFraming.Border + PictureInPictureFraming.TitleHeight;
            var view = LocalRect(PictureInPictureFraming.Border, viewY,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight);

            // Frame body, in the chrome colour rather than the plain panel colour so the
            // window reads as a window over the lot.
            batch.Draw(white, LocalRect(0, 0, total.X, total.Y), UIStyle.Current.TitleBg * Opacity);
            batch.Draw(white, view, Color.Black * 0.35f * Opacity);

            if (!DrawFeed(batch))
            {
                // Nothing to show: no lot yet, the subject is gone, the headline is not up this
                // tick, or the lot feed could not be read and there was no headline to fall back
                // to. A flat panel plus the status strip (which now names the cause) and, where
                // there is something to say about it, the fallback caption is honest; last lot's
                // frame would not be.
                batch.Draw(white, view, UIStyle.Current.Bg * Opacity);
            }

            // Border last so it sits over the feed.
            var border = PictureInPictureFraming.Border;
            var frame = UIStyle.Current.Text * 0.35f * Opacity;
            batch.Draw(white, LocalRect(0, 0, total.X, border), frame);
            batch.Draw(white, LocalRect(0, total.Y - border, total.X, border), frame);
            batch.Draw(white, LocalRect(0, 0, border, total.Y), frame);
            batch.Draw(white, LocalRect(total.X - border, 0, border, total.Y), frame);

            base.Draw(batch);
        }

        /// <summary>
        /// Draws whichever feed is active, once, behind a guard.
        ///
        /// <para>
        /// A cosmetic overlay is not worth a lost frame. Both feeds touch a render target the
        /// engine owns - the backbuffer the lot was just drawn into, and a headline texture the
        /// VM disposes from under us - so a failure here has to degrade to an empty panel rather
        /// than propagate out of the draw. The first failure is logged with its exception (a
        /// developer needs to see it) and the lot feed is then switched off for the session so
        /// it is not thrown sixty times a second. The next <see cref="ResolveFeed"/> picks that
        /// up as <see cref="PipFeedUnavailableReason.LotFeedFailed"/>, which is the one reason
        /// that is a fault rather than a graphics setting - so the window says FEED ERROR instead
        /// of blaming the player's anti-aliasing, and says it once.
        /// </para>
        /// </summary>
        private bool DrawFeed(UISpriteBatch batch)
        {
            if (_ActiveFeed == PipFeed.Auto) return false;

            try
            {
                if (_ActiveFeed == PipFeed.LotView)
                {
                    if (LotFeedDisabled) return false;
                    return DrawLotView(batch);
                }
                return DrawHeadline(batch);
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Picture-in-picture failed to draw its " + _ActiveFeed +
                    " feed; showing an empty panel instead.", ex);
                if (_ActiveFeed == PipFeed.LotView) LotFeedDisabled = true;
                return false;
            }
        }

        /// <summary>
        /// Set once the lot feed has thrown, so the failure is reported once rather than every
        /// frame. Cleared when the engine hands us a new backbuffer, which is the only thing
        /// that can make an unreadable one readable again.
        /// </summary>
        private bool LotFeedDisabled;

        private Texture2D LastBackbuffer;

        /// <summary>
        /// Draws the live cropped lot view. Returns false when there was nothing to read, so the
        /// caller can fall back to an empty panel rather than a broken one.
        ///
        /// <para>
        /// The texture comes from <see cref="LotSource"/>, which
        /// <see cref="PictureInPictureFraming.ResolveLotSource"/> settled this frame: the
        /// non-multisampled snapshot where there is one, and the backbuffer itself otherwise. The
        /// two have different sizes, so the crop is taken in the source's own pixels and the focus
        /// point is scaled into that space by <see cref="PictureInPictureFraming.LotSourceScale"/>
        /// - the same arithmetic for both, so there is one rule rather than one per source.
        /// </para>
        /// </summary>
        private bool DrawLotView(UISpriteBatch batch)
        {
            var world = ResolveScreen()?.LotControl?.World;
            if (world == null) return false;

            var bb = PPXDepthEngine.GetBackbuffer();
            if (bb == null) return false;

            // Re-validated here rather than trusted from the update, because a resize between the
            // update and the draw retires the snapshot; a disposed render target is the one failure
            // that would take the whole frame down instead of just this feed.
            var snapshot = SnapshotSource(LotSnapshot);

            // The decision is the tested one, so the reported source and the texture that is
            // about to be sampled cannot drift apart.
            LotSource = PictureInPictureFraming.ResolveLotSource(
                snapshot != null,
                PictureInPictureFraming.CanSample(bb.MultiSampleCount > 0, bb.MultiSampleCount));

            var source = LotSource == PipLotSource.Snapshot ? snapshot
                : LotSource == PipLotSource.Backbuffer ? bb
                : null;
            if (source == null) return false;

            var ssaa = Math.Max(1, PPXDepthEngine.SSAA);
            var logical = new Vector2(bb.Width / (float)ssaa, bb.Height / (float)ssaa);
            var focus = SubjectFocus(world, EffectiveSubject(), logical * 0.5f);

            // The source is supersampled (and, for a snapshot, also a downscale of the backbuffer),
            // so a point in logical viewport coordinates has to be scaled into texture coordinates.
            // CropRect takes the factor rather than a pre-scaled point so that conversion lives in
            // one tested place.
            var crop = PictureInPictureFraming.CropRect(
                source.Width, source.Height,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                focus.X, focus.Y,
                PictureInPictureFraming.LotSourceScale(bb.Width, bb.Height, source.Width, source.Height, ssaa),
                _Magnification);
            if (crop.Width <= 0 || crop.Height <= 0) return false;

            DrawLocalTexture(batch, source, crop,
                new Vector2(PictureInPictureFraming.Border,
                    PictureInPictureFraming.Border + PictureInPictureFraming.TitleHeight),
                Vector2.One, BlendColor);
            return true;
        }

        /// <summary>
        /// The subject's screen position in logical viewport coordinates - the space
        /// <c>AvatarComponent.GetScreenPos</c> reports in - falling back to the middle of the
        /// view for a subject with no world component or a projection that did not come out
        /// finite.
        /// </summary>
        private static Vector2 SubjectFocus(World world, VMEntity subject, Vector2 fallback)
        {
            var component = subject?.WorldUI;
            if (component == null) return fallback;

            try
            {
                var pos = component.GetScreenPos(world.State);
                if (float.IsNaN(pos.X) || float.IsNaN(pos.Y)) return fallback;
                return pos;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("Picture-in-picture could not place its subject on screen.", ex);
                return fallback;
            }
        }

        /// <summary>
        /// The subject's headline texture for this tick, or null. The guard on
        /// <c>VMEntity.Headline</c> matters: the VM disposes the renderer's texture and only
        /// then clears <c>WorldUI.Headline</c>, and drawing a disposed render target would
        /// take the frame down.
        /// </summary>
        private static Texture2D HeadlineTexture(VMEntity subject)
        {
            if (subject == null || subject.Headline == null) return null;
            return subject.WorldUI?.Headline;
        }

        private bool DrawHeadline(UISpriteBatch batch)
        {
            var tex = HeadlineTexture(EffectiveSubject());
            if (tex == null || tex.IsDisposed || tex.Width <= 0 || tex.Height <= 0) return false;

            var fit = PictureInPictureFraming.FitRectangle(
                tex.Width, tex.Height,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight);
            if (fit.Width <= 0 || fit.Height <= 0) return false;

            DrawLocalTexture(batch, tex, null,
                new Vector2(PictureInPictureFraming.Border + fit.X,
                    PictureInPictureFraming.Border + PictureInPictureFraming.TitleHeight + fit.Y),
                Vector2.One, BlendColor);
            return true;
        }

        // ---- input -----------------------------------------------------------------------

        private void OnMouse(UIMouseEventType type, UpdateState state)
        {
            // Clicking the window takes it down, the way clicking a TV's PIP does.
            // MouseOver / MouseOut are deliberately ignored so a pass-over cannot close it.
            if (type == UIMouseEventType.MouseDown && State.IsVisible) Close();
        }

        public override void Removed()
        {
            base.Removed();
            if (ReferenceEquals(Current, this))
            {
                Current = null;
                // The snapshot is a process-wide render target inside the engine, and this window
                // is the only thing that asks for one. Handing it back means a player who closes
                // the feature is not holding a colour and depth buffer for the rest of the
                // session. Harmless if another window still wants one: the next capture builds a
                // fresh target.
                PPXDepthEngine.ReleaseLotSnapshot();
            }
        }
    }

    /// <summary>
    /// The pure half of <see cref="UIPictureInPicture"/>: frame geometry, the crop that picks
    /// what part of the lot view the window shows, caption tidying, the feed fallback order,
    /// and the show/hide state machine. No graphics device, no content, no statics - which is
    /// what makes it testable headlessly and leaves the drawing code down to blits.
    /// </summary>
    public static class PictureInPictureFraming
    {
        /// <summary>Width of the picture area, in UI units.</summary>
        public const int ViewWidth = 208;

        /// <summary>Height of the picture area, in UI units.</summary>
        public const int ViewHeight = 156;

        /// <summary>Thickness of the window's border, in UI units.</summary>
        public const int Border = 2;

        /// <summary>Height of the status strip above the picture, in UI units.</summary>
        public const int TitleHeight = 16;

        /// <summary>Height of the caption strip below the picture, in UI units.</summary>
        public const int CaptionHeight = 18;

        /// <summary>Gap kept between the window and the edge of the screen, in UI units.</summary>
        public const int ScreenMargin = 15;

        /// <summary>
        /// Longest caption the strip can show. The strip is <see cref="ViewWidth"/> UI units
        /// wide and the label is 12pt, so a longer string would be clipped mid-glyph; it is
        /// truncated with an ellipsis instead.
        /// </summary>
        public const int MaxCaptionLength = 30;

        /// <summary>Widest crop the window will ask for.</summary>
        public const float MinMagnification = 0.25f;

        /// <summary>Tightest crop the window will ask for.</summary>
        public const float MaxMagnification = 6f;

        /// <summary>
        /// Outer size of the window. The caption strip is dropped entirely when there is no
        /// caption rather than being drawn empty, so a caption-less window is not a window with
        /// a blank bar under it.
        /// </summary>
        public static Vector2 TotalSize(bool hasCaption)
        {
            return new Vector2(
                ViewWidth + Border * 2,
                TitleHeight + ViewHeight + (hasCaption ? CaptionHeight : 0) + Border * 2);
        }

        /// <summary>
        /// Where the window goes for a given screen size. Picks the requested corner, then pulls
        /// it back on screen if it would hang off an edge - which is what happens on the first
        /// frame after a window shrink, and when a right-hand corner is asked for on a screen
        /// narrower than the window.
        /// </summary>
        public static Vector2 Place(int screenWidth, int screenHeight, Vector2 size, PipCorner corner, int margin)
        {
            float x;
            float y;

            switch (corner)
            {
                case PipCorner.TopLeft:
                    x = margin;
                    y = margin;
                    break;
                case PipCorner.TopRight:
                    x = screenWidth - size.X - margin;
                    y = margin;
                    break;
                case PipCorner.BottomLeft:
                    x = margin;
                    y = screenHeight - size.Y - margin;
                    break;
                default:
                    x = screenWidth - size.X - margin;
                    y = screenHeight - size.Y - margin;
                    break;
            }

            return new Vector2(ClampToScreen(x, screenWidth - size.X), ClampToScreen(y, screenHeight - size.Y));
        }

        private static float ClampToScreen(float position, float maxPosition)
        {
            // A screen smaller than the window gives a negative maximum; 0 is then the only
            // answer that keeps the window's top-left corner on screen, which is the least bad
            // of the three.
            if (maxPosition < 0f) return 0f;
            if (position < 0f) return 0f;
            return Math.Min(position, maxPosition);
        }

        /// <summary>
        /// Whether a render target can be used as a sprite batch source without being copied
        /// first.
        ///
        /// <para>
        /// The lot backbuffer is created with the anti-aliasing sample count
        /// (<c>PPXDepthEngine.CreateRenderTarget</c> passes <c>PPXDepthEngine.MSAA</c> straight
        /// through), and this deliberately only trusts one with no samples; the multisampled
        /// ones are never read directly here.
        /// </para>
        ///
        /// <para>
        /// <see cref="ResolveLotSource"/> only reaches this answer after the snapshot has been
        /// ruled out, and the snapshot is a copy into a target with no samples, so the live view
        /// is available at every setting. This is the second-choice path - the backbuffer read
        /// directly, which is cheaper than copying and is what runs when the copy could not be
        /// made.
        /// </para>
        /// </summary>
        public static bool CanSample(bool targetExists, int multiSampleCount)
        {
            return targetExists && multiSampleCount <= 0;
        }

        /// <summary>
        /// Which texture to crop the live lot view out of this frame.
        ///
        /// <para>
        /// The snapshot wins whenever there is one, including when the backbuffer would also do
        /// the job: it is a plain texture, so the rest of the draw does not depend on the
        /// anti-aliasing setting, and it is small. The backbuffer is the fallback for the case
        /// where the copy could not be made and the samples happen to allow a direct read, and
        /// <see cref="PipLotSource.None"/> is the case that makes the window say why.
        /// </para>
        /// </summary>
        public static PipLotSource ResolveLotSource(bool snapshotUsable, bool backbufferSampleable)
        {
            if (snapshotUsable) return PipLotSource.Snapshot;
            if (backbufferSampleable) return PipLotSource.Backbuffer;
            return PipLotSource.None;
        }

        /// <summary>
        /// How many pixels of <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> the
        /// lot view has, per pixel of logical viewport.
        ///
        /// <para>
        /// Two independent factors, and both matter. The backbuffer is <c>ssaa x</c> the viewport,
        /// because <c>InitScreenTargets</c> scales the viewport by <c>PPXDepthEngine.SSAA</c>. And
        /// the snapshot is a proportional downscale of the backbuffer, so a snapshot smaller than
        /// the backbuffer is fewer source pixels per viewport pixel. Multiplying them is what makes
        /// one crop routine serve both sources, and what stops the window from being in the wrong
        /// part of the lot when a magnification is applied on top.
        /// </para>
        ///
        /// <para>
        /// The width ratio is the one used, which is exact for the backbuffer and within a texel
        /// for a snapshot: <c>ResolveSnapshotSize</c> fits the source's aspect, so the two axes
        /// differ only by the rounding of the fit.
        /// </para>
        /// </summary>
        public static float LotSourceScale(int backbufferWidth, int backbufferHeight,
            int sourceWidth, int sourceHeight, int ssaa)
        {
            var supersample = ssaa < 1 ? 1 : ssaa;
            if (backbufferWidth <= 0 || backbufferHeight <= 0) return supersample;
            if (sourceWidth <= 0 || sourceHeight <= 0) return supersample;

            var scale = (float)sourceWidth / backbufferWidth * supersample;
            return float.IsNaN(scale) || scale <= 0f ? supersample : scale;
        }

        /// <summary>
        /// The crop of the lot texture to blit into the window: the same aspect as the picture
        /// area (so nothing is stretched), scaled by <paramref name="sourceScale"/> and the
        /// magnification, centred on the focus point, and never reaching outside the source.
        ///
        /// <para>
        /// <paramref name="focusX"/>/<paramref name="focusY"/> are in logical viewport
        /// coordinates - the space <c>AvatarComponent.GetScreenPos</c> reports in - and are scaled
        /// by <paramref name="sourceScale"/> here, because the texture being cropped is
        /// <c>sourceScale x viewport</c>. Keeping that conversion here rather than at the call
        /// site means the one place that has to know about the relationship between the viewport,
        /// the backbuffer and the snapshot is covered by a test;
        /// <see cref="LotSourceScale"/> is what computes the factor.
        /// </para>
        ///
        /// <para>
        /// A factor of 1 - or of zero, or of nonsense - means "not scaled", so an unknown or
        /// degenerate source lands on the plain viewport-sized crop rather than on a
        /// zero-area one.
        /// </para>
        /// </summary>
        public static Rectangle CropRect(int sourceWidth, int sourceHeight,
            int viewWidth, int viewHeight,
            float focusX, float focusY, float sourceScale, float magnification)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0) return Rectangle.Empty;
            if (viewWidth <= 0 || viewHeight <= 0) return Rectangle.Empty;

            var scaleFactor = float.IsNaN(sourceScale) || sourceScale <= 0f ? 1f : sourceScale;
            var scale = ClampMagnification(magnification) * scaleFactor;

            var sourceAspect = sourceWidth / (float)sourceHeight;
            var width = viewWidth * scale;
            var height = viewHeight * scale;

            // Match the source's aspect on whichever axis is currently short, so the crop fills
            // the picture area exactly rather than letterboxing inside it.
            if (width / height < sourceAspect) width = height * sourceAspect;
            else height = width / sourceAspect;

            // ...and then never sample outside it.
            if (width > sourceWidth) width = sourceWidth;
            if (height > sourceHeight) height = sourceHeight;
            if (width / height < sourceAspect) width = height * sourceAspect;
            else if (width / height > sourceAspect) height = width / sourceAspect;

            var x = Clamp(focusX * scaleFactor - width / 2f, 0f, sourceWidth - width);
            var y = Clamp(focusY * scaleFactor - height / 2f, 0f, sourceHeight - height);

            return new Rectangle(
                (int)Math.Round(x), (int)Math.Round(y),
                Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)));
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>Keeps a requested magnification inside the supported band.</summary>
        public static float ClampMagnification(float magnification)
        {
            if (float.IsNaN(magnification)) return 1f;
            return Clamp(magnification, MinMagnification, MaxMagnification);
        }

        /// <summary>
        /// Where to draw a texture of <paramref name="textureWidth"/> x
        /// <paramref name="textureHeight"/> so it fits the picture area without distortion:
        /// scaled until it fits - in either direction, because the headline sprites this is
        /// for are often smaller than the window - and then centred. An empty rectangle for a
        /// texture with no area.
        /// </summary>
        public static Rectangle FitRectangle(int textureWidth, int textureHeight, int viewWidth, int viewHeight)
        {
            if (textureWidth <= 0 || textureHeight <= 0) return Rectangle.Empty;
            if (viewWidth <= 0 || viewHeight <= 0) return Rectangle.Empty;

            var scale = Math.Min(viewWidth / (float)textureWidth, viewHeight / (float)textureHeight);
            var width = Math.Max(1, (int)Math.Round(textureWidth * scale));
            var height = Math.Max(1, (int)Math.Round(textureHeight * scale));

            return new Rectangle((viewWidth - width) / 2, (viewHeight - height) / 2, width, height);
        }

        /// <summary>
        /// Tidies a caption: whitespace collapsed, trimmed, truncated with an ellipsis so it
        /// fits the strip. Returns null for a caption with nothing left in it, which is what makes
        /// <see cref="TotalSize"/> drop the strip.
        /// </summary>
        public static string NormalizeCaption(string caption)
        {
            var collapsed = CollapseWhitespace(caption);
            if (collapsed.Length == 0) return null;
            if (collapsed.Length > MaxCaptionLength)
            {
                collapsed = collapsed.Substring(0, MaxCaptionLength - 1) + "\u2026";
            }
            return collapsed;
        }

        /// <summary>
        /// Collapses every run of whitespace to a single space and trims the ends. Captions come
        /// from game strings and from sim names, either of which can carry a newline or a run of
        /// padding, and a label does not lay those out.
        /// </summary>
        public static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var builder = new System.Text.StringBuilder(value.Length);
            var pendingSpace = false;

            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    // Leading whitespace is dropped rather than turned into a leading space.
                    pendingSpace = builder.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }
                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// The feed to draw. The live lot view first, because it is the feed that actually shows
        /// the sim; the headline second; otherwise nothing. An explicitly requested feed that is
        /// unavailable falls back the same way rather than going blank, because a window that
        /// says NO SIGNAL is more useful than one that is showing nothing at all.
        /// </summary>
        public static PipFeed ResolveFeed(PipFeed requested, bool lotViewAvailable, bool headlineAvailable)
        {
            if (lotViewAvailable) return PipFeed.LotView;
            if (headlineAvailable) return PipFeed.Headline;
            return PipFeed.Auto;
        }

        /// <summary>
        /// The window's show/hide state. Two states and a countdown, with no UI in it.
        /// <see cref="Show"/> reports whether it changed anything, which is what lets a repeated
        /// notification of the same event leave the hold timer alone instead of resetting it and
        /// pinning the window open.
        /// </summary>
        public sealed class PictureInPictureState
        {
            private string _Caption;
            private VMEntity _Subject;
            private float _Remaining;

            /// <summary>Whether the window is up.</summary>
            public bool IsVisible { get; private set; }

            /// <summary>The tidied caption, or null when the window has none.</summary>
            public string Caption
            {
                get { return _Caption; }
            }

            /// <summary>The sim being spotlit. May be null, meaning "follow the selection".</summary>
            public VMEntity Subject
            {
                get { return _Subject; }
            }

            /// <summary>Seconds left before the window closes itself. Zero means "stays up".</summary>
            public float Remaining
            {
                get { return _Remaining; }
            }

            /// <summary>
            /// Shows the window on <paramref name="subject"/>. Returns true when the window was
            /// closed or was showing something different, and false when it was already showing
            /// exactly this - in which case the hold countdown is deliberately not restarted.
            /// </summary>
            public bool Show(VMEntity subject, string caption, float holdSeconds = 0f)
            {
                var tidy = NormalizeCaption(caption);
                if (IsVisible && ReferenceEquals(Subject, subject) && _Caption == tidy) return false;

                IsVisible = true;
                _Subject = subject;
                _Caption = tidy;
                _Remaining = holdSeconds > 0f ? holdSeconds : 0f;
                return true;
            }

            /// <summary>Hides the window. Returns true when it was up.</summary>
            public bool Hide()
            {
                if (!IsVisible) return false;
                IsVisible = false;
                _Remaining = 0f;
                return true;
            }

            /// <summary>
            /// Advances the hold countdown. Returns true on the frame the window closes itself, so
            /// the caller has a single place to react to.
            /// </summary>
            public bool Tick(float deltaSeconds)
            {
                if (!IsVisible || _Remaining <= 0f) return false;
                if (float.IsNaN(deltaSeconds) || deltaSeconds <= 0f) return false;

                _Remaining -= deltaSeconds;
                if (_Remaining > 0f) return false;

                Hide();
                return true;
            }
        }
    }
}
