using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Controls;
using System.Threading;
using FSO.Client;
using FSO.Common.Rendering.Framework.Model;
using FSO.Content;
using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;
using FSO.SimAntics;
using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Screens
{
    public class LoadingScreen : FSO.Client.UI.Framework.GameScreen
    {
        public string[] LoadText = new string[] {
            "", //Started = 0,
            "Identifying Works...", //ScanningFiles = 1,
            "Categorising Works...", //InitGlobal = 2,
            "Arranging Wardrobes...", //InitBCF = 3,
            "Reticulating Splines...", //InitAvatars = 4,
            "Performing Sound Check...", //InitAudio = 5,
            "Arranging Furniture...", //InitObjects = 6,
            "Building Concert Hall...", //InitArch = 7,
            " " //Done = 8,
        };

        /// <summary>
        /// The same nine steps as <see cref="LoadText"/>, but as string ids into the
        /// <c>splashprogress</c> table of <c>UIText.dir</c> rather than as hard-coded English.
        ///
        /// <para>
        /// <see cref="LoadText"/> is deliberately the English fallback rather than a second
        /// source of truth: a game with no translated table (or a language whose table has not
        /// been filled in) still shows the original English lines rather than a column of
        /// <c>***MISSING***</c>. <see cref="getSplashTitleText"/> is what picks between the two.
        /// </para>
        ///
        /// <para>
        /// Index 0 and index 8 are blank on purpose - "started" and "done" have no text in
        /// either language, they only bracket the sequence.
        /// </para>
        /// </summary>
        private static readonly string[] LocalizedLoadText =
        {
            "", //Started = 0
            "4", //ScanningFiles = 1
            "5", //InitGlobal = 2
            "6", //InitBCF = 3
            "7", //InitAvatars = 4
            "8", //InitAudio = 5
            "9", //InitObjects = 6
            "10", //InitArch = 7
            " ", //Done = 8
        };

        public UISimitoneBg Bg;
        public UIDiagonalStripe ProgressDiag;
        public UIDiagonalStripe TextDiag;
        public UILoadProgress LoadProgress;
        public UISimitoneLogo Logo;
        public UISimitoneLoadLabel LastLabel;
        public bool LoadingComplete;
        public UITweenInstance LogoTween;

        private bool Closing = false;
        private float _i;
        public float InterpolatedAnimation
        {
            set
            {
                TextDiag.X = (Closing) ? 0 : (ScreenWidth * (1 - value));
                TextDiag.BodySize = new Point((int)(value * ScreenWidth), 75);
                TextDiag.Y = ScreenHeight*0.75f - 37;

                ProgressDiag.X = (Closing) ? (ScreenWidth * (1 - value)) : 0;
                ProgressDiag.BodySize = new Point((int)(value * ScreenWidth), 150);

                LoadProgress.Opacity = value;

                _i = value;
            }

            get
            {
                return _i;
            }
        }

        public LoadingScreen()
        {
            Content.InitBasic(GlobalSettings.Default.StartupPath, GameFacade.GraphicsDevice);

            Bg = new UISimitoneBg();
            Bg.Position = (new Vector2(ScreenWidth, ScreenHeight)) / 2;
            Add(Bg);

            ProgressDiag = new UIDiagonalStripe(new Point(0, 150), UIDiagonalStripeSide.RIGHT, Color.Black * 0.75f);
            ProgressDiag.Position = new Vector2(0, ScreenHeight / 2 - 75);
            Add(ProgressDiag);

            TextDiag = new UIDiagonalStripe(new Point(0, 150), UIDiagonalStripeSide.LEFT, Color.Black * 0.5f);
            TextDiag.Position = new Vector2(0, ScreenHeight * 0.75f - 37);
            Add(TextDiag);

            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "InterpolatedAnimation", 1f } }, TweenQuad.EaseOut);

            LoadProgress = new UILoadProgress();
            LoadProgress.Position = (new Vector2(ScreenWidth, ScreenHeight) - new Vector2(1012, 128))/2;
            Add(LoadProgress);

            Logo = new UISimitoneLogo();
            Logo.Position = new Vector2(ScreenWidth, ScreenHeight) / 2;
            Add(Logo);
            LogoTween = GameFacade.Screens.Tween.To(Logo, 1f, new Dictionary<string, float>() { { "Y", ScreenHeight/4 }, { "ScaleX", 0.5f }, { "ScaleY", 0.5f } }, TweenQuad.EaseOut);

            InterpolatedAnimation = InterpolatedAnimation;

            // Kick content initialization off the game thread. This used to be a raw Thread with
            // no way to stop it: if the player quit (or the screen was otherwise torn down) before
            // content finished, the work kept running against a disposed GraphicsDevice. A
            // cancelable Task lets Removed() stop us cleanly and keeps exceptions (which would
            // otherwise be lost or crash the process) in the log.
            _ = Task.Run(() =>
            {
                try
                {
                    _loadCts.Token.ThrowIfCancellationRequested();
                    VMContext.InitVMConfig(true);
                    FSO.Content.Content.Init(GlobalSettings.Default.StartupPath, GameFacade.GraphicsDevice);
                    _loadCts.Token.ThrowIfCancellationRequested();
                    lock (this)
                    {
                        LoadingComplete = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    //the screen was torn down before loading finished - expected on quit.
                }
                catch (Exception ex)
                {
                    SimitoneLog.Error("Content loading failed on the loading screen.", ex);
                }
            }, _loadCts.Token);
        }

        private readonly CancellationTokenSource _loadCts = new CancellationTokenSource();

        public override void Removed()
        {
            base.Removed();
            // Cancel the in-flight content load so it cannot touch a disposed GraphicsDevice.
            // The CTS is deliberately not disposed here: the (possibly still running) task holds
            // the token, and disposing the source under it would surface as ObjectDisposedException.
            _loadCts.Cancel();
        }

        public void Close()
        {
            Closing = true;
            GameThread.SetTimeout(() =>
            {
                ProgressDiag.DiagSide = UIDiagonalStripeSide.LEFT;
                TextDiag.DiagSide = UIDiagonalStripeSide.RIGHT;
                GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "InterpolatedAnimation", 0f } }, TweenQuad.EaseOut);
                GameFacade.Screens.Tween.To(Logo, 0.5f, new Dictionary<string, float>() { { "Y", -200f } }, TweenQuad.EaseIn);
                GameFacade.Screens.Tween.To(Bg, 0.5f, new Dictionary<string, float>() { { "Opacity", 0f } }, TweenQuad.EaseOut);

                GameThread.SetTimeout(() =>
                {
                    ProgressDiag.Parent.Remove(ProgressDiag);
                    TextDiag.Parent.Remove(TextDiag);
                    Logo.Parent.Remove(Logo);
                    LoadProgress.Parent.Remove(LoadProgress);
                    Bg.Parent.Remove(Bg);
                }, 500);
            }, 750);
        }

        public override void GameResized()
        {
            base.GameResized();
            GameFacade.Screens.Tween.Stop(LogoTween, true);
            Bg.Position = (new Vector2(ScreenWidth, ScreenHeight)) / 2;
            ProgressDiag.Position = new Vector2(0, ScreenHeight / 2 - 75);
            TextDiag.Position = new Vector2(0, ScreenHeight * 0.75f - 37);
            LoadProgress.Position = (new Vector2(ScreenWidth, ScreenHeight) - new Vector2(1012, 128)) / 2;
            Logo.Position = new Vector2(ScreenWidth, ScreenHeight/2) / 2;
            if (LastLabel != null) LastLabel.Position = new Vector2(ScreenWidth / 2, ScreenHeight * 0.75f);

            InterpolatedAnimation = InterpolatedAnimation;
        }

        public ContentLoadingProgress LastProgress = ContentLoadingProgress.Invalid;

        /// <summary>
        /// The line to show for the current <see cref="ContentLoadingProgress"/>.
        ///
        /// <para>
        /// English keeps the hard-coded <see cref="LoadText"/>, because those lines are Simitone's
        /// own ("Reticulating Splines...") and are not the stock client's - a stock English table
        /// would replace them with something else. Every other language is read out of
        /// <c>UIText.dir</c>'s <c>splashprogress</c> table via <see cref="LocalizedLoadText"/>,
        /// falling back to the English line whenever the table, the index, or the translation is
        /// missing. Nothing here can produce <c>***MISSING***</c> on screen.
        /// </para>
        /// </summary>
        private string getSplashTitleText()
        {
            int index = (int)LastProgress;
            if (index < 0 || index >= LoadText.Length) return "";

            STRLangCode? lang = (GameFacade.Game as SimitoneGame)?.CurrentLanguage;
            if (lang == null) lang = STRLangCode.Default;

            switch (lang.Value)
            {
                case STRLangCode.Default:
                case STRLangCode.EnglishUS:
                case STRLangCode.EnglishUK:
                    return LoadText[index];
                default:
                    //the id list is the same length as LoadText, but fall back to English rather
                    //than trust that.
                    if (index >= LocalizedLoadText.Length) return LoadText[index];
                    var id = LocalizedLoadText[index];
                    if (string.IsNullOrWhiteSpace(id)) return id; //intentionally blank
                    var localized = GameFacade.Strings.GetString("155", id);
                    if (localized == "***MISSING***" || string.IsNullOrEmpty(localized))
                        return LoadText[index]; //not translated? english it is.
                    return localized;
            }
        }

        public override void Update(UpdateState state)
        {
            if (LastProgress != Content.LoadProgress)
            {
                LastProgress = Content.LoadProgress;

                if (LastLabel != null)
                {
                    LastLabel.Kill();
                }
                LastLabel = new UISimitoneLoadLabel(getSplashTitleText());
                LastLabel.Position = new Vector2(ScreenWidth/2, ScreenHeight * 0.75f);
                Add(LastLabel);

                LoadProgress.OverallPercent = (float)LastProgress / (float)ContentLoadingProgress.Done;
            }
            lock (this)
            {
                if (LoadingComplete)
                {
                    GameController.EnterGameMode("", false);
                }
            }
            base.Update(state);
        }
    }

    public class UISimitoneLogo : UIElement
    {
        public Texture2D Logo;

        public UISimitoneLogo() : base()
        {
            var ui = Content.Get().CustomUI;
            Logo = ui.Get("load_logo.png").Get(GameFacade.GraphicsDevice);
        }

        public override void Draw(UISpriteBatch batch)
        {
            DrawLocalTexture(batch, Logo, new Vector2(Logo.Width, Logo.Height) / -2);
        }
    }

    public class UISimitoneBg : UIElement
    {
        public Texture2D Bg;

        public UISimitoneBg() : base()
        {
            var ui = Content.Get().CustomUI;
            Bg = ui.Get("load_static_bg.png").Get(GameFacade.GraphicsDevice);
        }

        public override void Draw(UISpriteBatch batch)
        {
            if (!Visible) return;
            var scale = Math.Max(GameFacade.Screens.CurrentUIScreen.ScreenWidth / 1136f, GameFacade.Screens.CurrentUIScreen.ScreenHeight / 640f);
            DrawLocalTexture(batch, Bg, null, new Vector2(Bg.Width, Bg.Height) / -2 * scale, new Vector2(scale));
        }
    }

    public class UISimitoneLoadLabel : UIContainer
    {
        public UILabel Label;

        private float _Alpha;
        public float Alpha
        {
            get
            {
                return _Alpha;
            }
            set
            {
                Label.CaptionStyle.Color = Color.White * value;
                _Alpha = value;
            }
        }

        public UISimitoneLoadLabel(string text)
        {
            Label = new UILabel();
            Label.CaptionStyle = Label.CaptionStyle.Clone();
            Label.CaptionStyle.Size = 37;
            Label.CaptionStyle.Color = Color.White;
            Label.Caption = text;
            Label.Alignment = TextAlignment.Center | TextAlignment.Middle;
            Label.Size = new Vector2(1, 1);
            Label.Y = 100f;
            Add(Label);
            Alpha = Alpha;
            GameFacade.Screens.Tween.To(Label, 0.4f, new Dictionary<string, float>() { { "Y", 0f } }, TweenQuad.EaseOut);
            GameFacade.Screens.Tween.To(this, 0.4f, new Dictionary<string, float>() { { "Alpha", 1f } }, TweenQuad.EaseOut);
        }

        public void Kill()
        {
            GameFacade.Screens.Tween.To(Label, 0.4f, new Dictionary<string, float>() { { "Y", -100f } }, TweenQuad.EaseIn);
            GameFacade.Screens.Tween.To(this, 0.4f, new Dictionary<string, float>() { { "Alpha", 0f } }, TweenQuad.EaseIn);
            GameThread.SetTimeout(() => { Parent.Remove(this); }, 400);
        }
    }
}
