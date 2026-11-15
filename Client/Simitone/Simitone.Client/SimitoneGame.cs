/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Threading;
using FSO.Common.Rendering.Framework;
using FSO.LotView;
using FSO.HIT;
using FSO.Client.UI;
using FSO.Client.GameContent;
using FSO.Common.Utils;
using FSO.Common;
using Microsoft.Xna.Framework.Audio;
using FSO.HIT.Model;
using FSO.Client;
using FSO.Files;
using FSO.SimAntics;
using MSDFData;
using FSO.LotView.Model;
using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;
using FSO.Files.Formats.IFF.Chunks;

namespace Simitone.Client
{
    /// <summary>
    /// This is the main type for your game
    /// </summary>
    public class SimitoneGame : FSO.Common.Rendering.Framework.Game
    {
        //** if the window is resized below this value, UI elements can attempt to resize to available space below 0px -- resulting in immediate crash
        // we should ensure a minimum width and height of at least the smallest supported resolution by the UI.
        const int MIN_WIDTH = 800, MIN_HEIGHT = 600;

        public UILayer uiLayer;
        public _3DLayer SceneMgr;
        private bool HasUpdated;

        /// <summary>
        /// The current language setting of this Simitone game
        /// </summary>
        public STRLangCode CurrentLanguage { get; private set; } = STRLangCode.EnglishUS;

        public SimitoneGame() : base()
        {
            GameFacade.Game = this;
            GameThread.Game = Thread.CurrentThread;
            if (GameFacade.DirectX) TimedReferenceController.SetMode(CacheType.PERMANENT);
            Content.RootDirectory = FSOEnvironment.GFXContentDir;

            TargetElapsedTime = new TimeSpan(10000000 / GlobalSettings.Default.TargetRefreshRate);
            FSOEnvironment.RefreshRate = GlobalSettings.Default.TargetRefreshRate;
            FSOEnvironment.TexCompress = false;
            UILotControl.ShowSimanticsExceptions = !FSOEnvironment.Args.Contains("nosimantics-exc");

            if (!FSOEnvironment.SoftwareKeyboard)
            {
                Graphics.SynchronizeWithVerticalRetrace = true;
                Graphics.PreferredBackBufferWidth = GlobalSettings.Default.GraphicsWidth;
                Graphics.PreferredBackBufferHeight = GlobalSettings.Default.GraphicsHeight;
                Graphics.HardwareModeSwitch = false;
                Graphics.ApplyChanges();
            }

            this.Window.AllowUserResizing = true;
            this.Window.ClientSizeChanged += new EventHandler<EventArgs>(Window_ClientSizeChanged);

            Thread.CurrentThread.Name = "Game";

        }

        bool newChange = false;
        void Window_ClientSizeChanged(object sender, EventArgs e)
        {
            if (newChange || !GlobalSettings.Default.Windowed || FSOEnvironment.SoftwareKeyboard) return;
            if (Window.ClientBounds.Width == 0 || Window.ClientBounds.Height == 0) return;
            newChange = true;
            var width = Math.Max(MIN_WIDTH, Window.ClientBounds.Width);
            var height = Math.Max(MIN_HEIGHT, Window.ClientBounds.Height);
            Graphics.PreferredBackBufferWidth = width;
            Graphics.PreferredBackBufferHeight = height;
            Graphics.ApplyChanges();

            GlobalSettings.Default.GraphicsWidth = width;
            GlobalSettings.Default.GraphicsHeight = height;

            newChange = false;
            if (uiLayer?.CurrentUIScreen == null) return;

            uiLayer.SpriteBatch.ResizeBuffer(GlobalSettings.Default.GraphicsWidth, GlobalSettings.Default.GraphicsHeight);
            uiLayer.CurrentUIScreen.GameResized();
        }

        /// <summary>
        /// Allows the game to perform any initialization it needs to before starting to run.
        /// This is where it can query for any required services and load any non-graphic
        /// related content.  Calling base.Initialize will enumerate through any components
        /// and initialize them as well.
        /// </summary>
        protected override void Initialize()
        {

            var settings = GlobalSettings.Default;
            if (FSOEnvironment.DPIScaleFactor != 1 || FSOEnvironment.SoftwareDepth)
            {
                settings.GraphicsWidth = (int)(GraphicsDevice.Viewport.Width / FSOEnvironment.DPIScaleFactor);
                settings.GraphicsHeight = (int)(GraphicsDevice.Viewport.Height / FSOEnvironment.DPIScaleFactor);
            }

            var initialMode = (GlobalGraphicsMode)settings.GlobalGraphicsMode;
            if (FSOEnvironment.Enable3D)
            {
                if (initialMode == GlobalGraphicsMode.Full2D) initialMode = GlobalGraphicsMode.Full3D;
            }
            else
            {
                initialMode = GlobalGraphicsMode.Full2D;
            }
            GraphicsModeControl.ChangeMode(initialMode);
            GraphicsModeControl.ModeChanged += SaveGraphicsModePreference;

            // Graphics preferences are turned into renderer values in exactly one place, which
            // the Options page and the in-game settings strip also go through, so what the game
            // starts with cannot drift from what those screens can change. The config instance
            // is created empty and mutated rather than filled by an object initialiser because
            // GraphicsModeControl has already put the player's graphics mode on the previous
            // one; replacing it would drop the player back to the default.
            FSO.LotView.WorldConfig.Current = new FSO.LotView.WorldConfig();
            GraphicsOptions.Apply(FSO.LotView.WorldConfig.Current, GraphicsPreferences.From(settings));
            LogGraphicsConfig(FSO.LotView.WorldConfig.Current);

            OperatingSystem os = Environment.OSVersion;
            PlatformID pid = os.Platform;
            GameFacade.Linux = (pid == PlatformID.MacOSX || pid == PlatformID.Unix);

            FSO.Content.Content.Target = FSO.Content.FSOEngineMode.TS1;
            FSO.Content.Content.TS1HybridBasePath = GlobalSettings.Default.TS1HybridPath;
            if (FSOEnvironment.Enable3D) FSO.Files.RC.DGRP3DMesh.InitRCWorkers();
            //FSO.Content.Content.Init(GlobalSettings.Default.StartupPath, GraphicsDevice);
            FSO.SimAntics.VMAvatar.MissingIconProvider = Simitone.Client.UI.Model.UIIconCache.GetObject;

            // Seed the free-will flag from the saved setting before anything can read it. The
            // flag is a process-global static in the VM (FreeSO PR #290) that defaults to on
            // and is not itself persisted, so without this the ini value would only be
            // honoured from the moment the player first opened the Options menu.
            FreeWillSetting.InitializeFromSettings(settings);
            SimitoneLog.Info($"Free will is {(FreeWillSetting.Enabled ? "on" : "off")} at startup.");


            base.Initialize();

            GameFacade.GameThread = Thread.CurrentThread;

            SceneMgr = new _3DLayer();
            SceneMgr.Initialize(GraphicsDevice);

            GameFacade.Scenes = SceneMgr;
            GameFacade.GraphicsDevice = GraphicsDevice;
            GameFacade.GraphicsDeviceManager = Graphics;
            GameFacade.Cursor = new CursorManager(GraphicsDevice);
            if (!GameFacade.Linux)
            {
                CurLoader.BmpLoaderFunc = ImageLoader.BaseFunction;
                GameFacade.Cursor.Init(GlobalSettings.Default.TS1HybridPath, true);
            }
            //Cursors FreeSO has no bmp for (the eyedropper). Needs the graphics device, so it
            //has to be here rather than alongside the static initialisers.
            SimitoneCursors.Init(GraphicsDevice);

            /** Init any computed values **/
            GameFacade.Init();

            //init audio now
            HITVM.Init();
            // The four levels and the four mute flags, through the one place that knows what a
            // level means. The Options Sound page calls the same method, so a volume cannot be
            // applied one way at startup and another when the player moves a slider.
            SoundVolumeSetting.Apply(settings);

            ChangeLanguage((STRLangCode)settings.LanguageCode);

            GraphicsDevice.RasterizerState = new RasterizerState() { CullMode = CullMode.None };

            try
            {
                var audioTest = new SoundEffect(new byte[2], 44100, AudioChannels.Mono); //initialises XAudio.
                audioTest.CreateInstance().Play();
            }
            catch (Exception e)
            {
                //The game runs without sound rather than failing to start, but the reason is logged.
                Simitone.Client.Utils.SimitoneLog.Warn("Audio device probe failed; continuing without confirmed audio", e);
            }

            this.IsFixedTimeStep = true;

            WorldContent.Init(this.Services, Content.RootDirectory);
            base.Screen.Layers.Add(SceneMgr);
            base.Screen.Layers.Add(uiLayer);
            GameFacade.LastUpdateState = base.Screen.State;

            if (!GlobalSettings.Default.Windowed && !GameFacade.GraphicsDeviceManager.IsFullScreen)
            {
                GameFacade.GraphicsDeviceManager.ToggleFullScreen();
            }
        }

        private void SaveGraphicsModePreference(GlobalGraphicsMode obj)
        {
            GlobalSettings.Default.GlobalGraphicsMode = (int)obj;
            GlobalSettings.Default.Save();
        }

        /// <summary>
        /// Writes the graphics configuration the renderer was actually handed to the log.
        ///
        /// <para>
        /// The values in <c>config.ini</c> are not what the renderer sees - the mapping repairs
        /// and clamps them on the way in - so "the ini says 4" and "the renderer got 8" are
        /// both possible, and only one of them is a bug report worth chasing. This is the one
        /// line that says which.
        /// </para>
        ///
        /// <para>
        /// The 2d/hybrid/3d choice is read from <see cref="GraphicsModeControl"/>, which is
        /// what the renderer actually switches on: <c>WorldConfig.Mode</c> is a mirror nothing
        /// reads, so reporting it would report a default rather than the mode in force.
        /// Invariant formatting throughout, because this line is meant to be read by something
        /// that is not the machine's culture.
        /// </para>
        /// </summary>
        private static void LogGraphicsConfig(FSO.LotView.WorldConfig config)
        {
            SimitoneLog.Info(string.Format(CultureInfo.InvariantCulture,
                "Graphics config: mode={0} lighting={1} shadowDetail={2} waterShininess={3:0.###}" +
                " weather={4} directional={5} complex={6} transitions={7} aa={8} surroundingLots={9}",
                GraphicsModeControl.Mode,
                config.LightingMode,
                config.ShadowDetail,
                config.WaterShininess,
                config.Weather ? "on" : "off",
                config.Directional ? "on" : "off",
                config.Complex ? "on" : "off",
                config.EnableTransitions ? "on" : "off",
                config.AA,
                config.SurroundingLots));
        }

        /// <summary>
        /// Switches the game's UI language, reloading the string tables and remembering the
        /// choice for the next launch.
        /// </summary>
        /// <remarks>
        /// FreeSO's <c>ContentStrings</c> takes the language from
        /// <c>GlobalSettings.CurrentLang</c> rather than as an argument, so the settings are
        /// what has to move first: the name is written before the strings are rebuilt, or the
        /// rebuild would read the old name back and change nothing.
        ///
        /// <para>
        /// Note that TS1's <c>UIText.iff</c> only carries the English string set, so this
        /// currently changes what is <em>recorded</em> rather than what is displayed; the
        /// game still reads its text from the English set. It is here so the language choice
        /// exists and is persisted, and so a non-English string source can be dropped in
        /// without touching any callers.
        /// </para>
        /// </remarks>
        /// <param name="NewLanguage">The language to switch to.</param>
        internal void ChangeLanguage(STRLangCode NewLanguage)
        {
            //check if this language is already loaded.
            if (GameFacade.Strings != null && CurrentLanguage == NewLanguage) return;
            CurrentLanguage = NewLanguage;

            GlobalSettings.Default.LanguageCode = (byte)NewLanguage;
            GlobalSettings.Default.CurrentLang = LanguageDirectoryName(NewLanguage);

            ContentStrings.TS1 = true;
            GameFacade.Strings = new ContentStrings();
        }

        /// <summary>
        /// The directory name <c>ContentStrings</c> looks for a language's UI text in.
        /// English (US) keeps the shipped ini default so an existing config is not rewritten
        /// on every launch; everything else is the enum name lowercased.
        /// </summary>
        private static string LanguageDirectoryName(STRLangCode code)
        {
            if (code == STRLangCode.EnglishUS) return "english";
            return Enum.GetName(typeof(STRLangCode), code)?.ToLowerInvariant() ?? "english";
        }

        /// <summary>
        /// Run this instance with GameRunBehavior forced as Synchronous.
        /// </summary>
        public new void Run()
        {
            Run(GameRunBehavior.Synchronous);
        }

        /// <summary>
        /// Only used on desktop targets. Use extensive reflection to AVOID linking on iOS!
        /// </summary>
        void AddTextInput()
        {
            this.Window.GetType().GetEvent("TextInput").AddEventHandler(this.Window, (EventHandler<TextInputEventArgs>)GameScreen.TextInput);
        }

        void RegainFocus(object sender, EventArgs e)
        {
            GameFacade.Focus = true;
        }

        void LostFocus(object sender, EventArgs e)
        {
            GameFacade.Focus = false;
        }

        /// <summary>
        /// LoadContent will be called once per game and is the place to load
        /// all of your content.
        /// </summary>
        protected override void LoadContent()
        {
            Effect vitaboyEffect = null;
            try
            {
                GameFacade.MainFont = new FSO.Client.UI.Framework.Font();
                //GameFacade.MainFont.AddSize(12, Content.Load<SpriteFont>("Fonts/Mobile_15px"));
                //GameFacade.MainFont.AddSize(15, Content.Load<SpriteFont>("Fonts/Mobile_20px"));
                //GameFacade.MainFont.AddSize(19, Content.Load<SpriteFont>("Fonts/Mobile_25px"));
                //GameFacade.MainFont.AddSize(37, Content.Load<SpriteFont>("Fonts/Mobile_50px"));

                GameFacade.EdithFont = new FSO.Client.UI.Framework.Font();
                //GameFacade.EdithFont.AddSize(12, Content.Load<SpriteFont>("Fonts/Trebuchet_12px"));
                //GameFacade.EdithFont.AddSize(14, Content.Load<SpriteFont>("Fonts/Trebuchet_14px"));

                GameFacade.VectorFont = new FSO.UI.Framework.MSDFFont(Content.Load<FieldFont>("../Fonts/mobile"));
                GameFacade.EdithVectorFont = new FSO.UI.Framework.MSDFFont(Content.Load<FieldFont>("../Fonts/trebuchet"));
                GameFacade.EdithVectorFont.VectorScale = 0.366f;
                GameFacade.EdithVectorFont.Height = 15;
                GameFacade.EdithVectorFont.YOff = 11;

                FSO.UI.Framework.MSDFFont.MSDFEffect = Content.Load<Effect>("Effects/MSDFFont");

                vitaboyEffect = Content.Load<Effect>("Effects/Vitaboy"+((FSOEnvironment.SoftwareDepth)?"iOS":""));
                uiLayer = new UILayer(this);
            }
            catch (Exception e)
            {
                //MessageBox.Windows.Forms.MessageBox.Show("Content could not be loaded. Make sure that the FreeSO content has been compiled! (ContentSrc/TSOClientContent.mgcb)");
                //The game has no attached console, so the log is the only place this is visible.
                Simitone.Client.Utils.SimitoneLog.Error("Content could not be loaded; exiting.", e);
                Console.WriteLine(e.ToString());
                Exit();
            }

            FSO.Vitaboy.Avatar.setVitaboyEffect(vitaboyEffect);
        }

        /// <summary>
        /// UnloadContent will be called once per game and is the place to unload
        /// all content.
        /// </summary>
        protected override void UnloadContent()
        {
            // TODO: Unload any non ContentManager content here
        }


        protected override void OnExiting(object sender, ExitingEventArgs args)
        {
            base.OnExiting(sender, args);
            GameThread.SetKilled();
            args.Cancel = !(GameFacade.Screens.CurrentUIScreen?.CloseAttempt() ?? true);
        }

        /// <summary>
        /// Allows the game to run logic such as updating the world,
        /// checking for collisions, gathering input, and playing audio.
        /// </summary>
        /// <param name="gameTime">Provides a snapshot of timing values.</param>
        protected override void Update(GameTime gameTime)
        {
            if (!HasUpdated)
            {
                this.IsMouseVisible = true;
                if (!FSOEnvironment.SoftwareKeyboard) AddTextInput();
                this.Window.Title = "Simitone";
                HasUpdated = true;
                GameFacade.Screens = uiLayer;
                GameController.EnterLoading();
            }
            GameThread.UpdateExecuting = true;

            if (HITVM.Get() != null) HITVM.Get().Tick();

            base.Update(gameTime);
            GameThread.UpdateExecuting = false;
        }
    }
}
