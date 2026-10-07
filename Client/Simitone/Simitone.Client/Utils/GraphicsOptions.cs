using FSO.Client;
using FSO.LotView;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels.Options;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// A snapshot of the graphics preferences, already validated.
    ///
    /// <para>
    /// The point of taking a copy rather than passing <see cref="GlobalSettings"/> around is
    /// that everything downstream of this type is a pure function of the snapshot: the mapping
    /// from "what the ini says" to "what the renderer is told" lives in exactly one place and
    /// can be tested without a graphics device, a config file or a running game. See
    /// <see cref="GraphicsOptions"/>.
    /// </para>
    /// </summary>
    public readonly struct GraphicsPreferences
    {
        /// <summary>0 off, 1 advanced, 2 +3d wall, 3 ultra. See <see cref="WorldConfig.LightingMode"/>.</summary>
        public int LightingMode { get; }

        /// <summary>0 off, 1 4x MSAA, 2 2x SSAA.</summary>
        public int AntiAlias { get; }

        /// <summary>Light/shadow map pixels per lot tile. Already snapped to the offered ladder.</summary>
        public int ShadowDetail { get; }

        /// <summary>Water/pool surface quality: 0 off, 1 normal, 2 strong.</summary>
        public int WaterReflections { get; }

        public bool Weather { get; }
        public bool DirectionalLight { get; }
        public bool ComplexShaders { get; }
        public bool EnableTransitions { get; }
        public bool SmoothZoom { get; }
        public int SurroundingLots { get; }

        /// <summary>
        /// Whether the sun's position, colour and strength come from
        /// <c>FSO.LotView.Model.SunLightingModel</c> rather than the lot view's fixed-elevation
        /// sweep. Lands in <c>WorldConfig.SunCycle</c>; on is the default, because in 3D it is what
        /// makes the sun move at all.
        /// </summary>
        public bool SunCycle { get; }

        /// <summary>
        /// How the neighbourhood view is drawn: off, the 2D bitmap, or the generated 3D world.
        ///
        /// <para>
        /// Carried on the snapshot rather than read from a static at the point of use, for the same
        /// reason every other option here is: this is the one place that maps "what the player
        /// chose" onto "what the renderer is told", and a renderer that went and read the ini itself
        /// could not be asked what it was told. The default is 3D - the connected neighbourhood -
        /// and there is no engine field for it, because the neighbourhood view has no
        /// <c>WorldConfig</c> to push into: see <see cref="NeighbourhoodViewSetting"/> for where it
        /// is stored and why.
        /// </para>
        /// </summary>
        public NeighbourhoodView NeighbourhoodView { get; }

        /// <summary>
        /// Built through <see cref="From"/>, so a snapshot can only exist already validated.
        /// </summary>
        internal GraphicsPreferences(
            int lightingMode, int antiAlias, int shadowDetail, int waterReflections,
            bool weather, bool directionalLight, bool complexShaders, bool enableTransitions,
            bool smoothZoom, int surroundingLots, bool sunCycle, NeighbourhoodView neighbourhoodView)
        {
            LightingMode = lightingMode;
            AntiAlias = antiAlias;
            ShadowDetail = shadowDetail;
            WaterReflections = waterReflections;
            Weather = weather;
            DirectionalLight = directionalLight;
            ComplexShaders = complexShaders;
            EnableTransitions = enableTransitions;
            SmoothZoom = smoothZoom;
            SurroundingLots = surroundingLots;
            SunCycle = sunCycle;
            NeighbourhoodView = neighbourhoodView;
        }

        /// <summary>
        /// Reads the ini-backed values, repairing anything the renderer could not use.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The repair matters: FreeSO's ini defaults for some of these keys are values the
        /// renderer cannot use (<c>LightingMode</c> is -1, which is "no lighting at all"), and a
        /// config file is user input. Every value that reaches the renderer from here has been
        /// through <see cref="GraphicsOptions"/>, so no call site has to wonder.
        /// </para>
        /// <para>
        /// <paramref name="sunCycle"/> is a parameter rather than something read off
        /// <paramref name="settings"/> because it is not a <c>GlobalSettings</c> property: it
        /// lives in Simitone's own sidecar ini (see
        /// <c>Simitone.Client.UI.Panels.Options.SunCycleSetting</c>, for why). Null means "ask
        /// that store", which is what every real caller wants; passing a bool is how a test says
        /// what it means without touching the filesystem.
        /// </para>
        /// <para>
        /// <paramref name="neighbourhoodView"/> is a parameter for the same reason: the
        /// neighbourhood view choice is not a <c>GlobalSettings</c> property either, and lives in
        /// the same sidecar ini (see <c>NeighbourhoodViewSetting</c>). Null means "ask that
        /// store", and passing a value is how a test says what it means without touching the
        /// filesystem.
        /// </para>
        /// </remarks>
        public static GraphicsPreferences From(
            GlobalSettings settings, bool? sunCycle = null, NeighbourhoodView? neighbourhoodView = null)
        {
            if (settings == null)
            {
                var fallback = GraphicsOptions.Defaults;
                var withSun = sunCycle.HasValue ? fallback.WithSunCycle(sunCycle.Value) : fallback;
                return neighbourhoodView.HasValue
                    ? withSun.WithNeighbourhoodView(neighbourhoodView.Value)
                    : withSun;
            }

            return new GraphicsPreferences(
                StartupSettings.ResolveLightingMode(settings.LightingMode),
                StartupSettings.ResolveAntiAlias(settings.AntiAlias),
                GraphicsOptions.ResolveShadowDetail(settings.ShadowDetail),
                GraphicsOptions.ResolveWaterReflections(settings.WaterReflections),
                settings.Weather,
                settings.DirectionalLight3D,
                settings.ComplexShaders,
                settings.EnableTransitions,
                settings.SmoothZoom,
                settings.SurroundingLotMode,
                sunCycle ?? SunCycleSetting.Enabled,
                NeighbourhoodViewSetting.Resolve(neighbourhoodView ?? NeighbourhoodViewSetting.Current));
        }

        /// <summary>
        /// The same snapshot with the sun cycle choice replaced. The null-settings path and the
        /// tests need it; nothing else should.
        /// </summary>
        public GraphicsPreferences WithSunCycle(bool sunCycle)
        {
            return new GraphicsPreferences(
                LightingMode, AntiAlias, ShadowDetail, WaterReflections,
                Weather, DirectionalLight, ComplexShaders, EnableTransitions,
                SmoothZoom, SurroundingLots, sunCycle, NeighbourhoodView);
        }

        /// <summary>
        /// The same snapshot with the neighbourhood view choice replaced. For the tests, and for a
        /// caller holding a snapshot that needs to try the other view.
        /// </summary>
        /// <param name="neighbourhoodView">The choice to use.</param>
        public GraphicsPreferences WithNeighbourhoodView(NeighbourhoodView neighbourhoodView)
        {
            return new GraphicsPreferences(
                LightingMode, AntiAlias, ShadowDetail, WaterReflections,
                Weather, DirectionalLight, ComplexShaders, EnableTransitions,
                SmoothZoom, SurroundingLots, SunCycle, neighbourhoodView);
        }
    }

    /// <summary>
    /// The graphics quality ladder, and the one place it is mapped onto the renderer.
    ///
    /// <para>
    /// Two of the options here used to be decorative. <c>GlobalSettings.ShadowQuality</c> was
    /// shown on the Graphics page and written to the ini, but the lot view's shadow resolution
    /// was a hard-coded <c>16</c> at the light batch construction site, so nothing read it;
    /// <c>GlobalSettings.SmoothZoom</c> was likewise written and never read, because
    /// <c>WorldConfig.SmoothZoom</c>'s setter is empty. What is left is a small, testable
    /// description of what each quality step actually asks the renderer for.
    /// </para>
    ///
    /// <para>
    /// The two quality defaults are chosen so that a player who changes nothing gets exactly what
    /// the engine produced before: shadow detail 16 (the old literal) and the old water specular
    /// exponent. The rest of <see cref="Defaults"/> is the Simitone first-run set rather than
    /// FreeSO's raw ini defaults, and is named in one place so "what does an untouched install
    /// look like" has one answer.
    /// </para>
    /// </summary>
    public static class GraphicsOptions
    {
        #region Shadow detail

        /// <summary>
        /// Half the default. Halves every dimension of every light and shadow target, so this
        /// is roughly a quarter of the lighting memory - blurry shadows, and the right choice
        /// for a machine that is short of both.
        /// </summary>
        public const int ShadowDetailLow = 8;

        /// <summary>
        /// The resolution the lot view has always used. The default, and the value an existing
        /// config falls back to.
        /// </summary>
        public const int ShadowDetailMedium = 16;

        /// <summary>
        /// Half again as many pixels per tile as <see cref="ShadowDetailMedium"/>. The cost is
        /// quadratic in this number: on a 64x64 lot the largest single lighting target goes from
        /// 2016x2016 to 3024x3024, and there are several.
        /// </summary>
        public const int ShadowDetailHigh = 24;

        /// <summary>The value used when nothing has been chosen.</summary>
        public const int DefaultShadowDetail = ShadowDetailMedium;

        /// <summary>Lowest value the Options page offers.</summary>
        public const int MinShadowDetail = ShadowDetailLow;

        /// <summary>Highest value the Options page offers.</summary>
        public const int MaxShadowDetail = ShadowDetailHigh;

        /// <summary>
        /// Snap a stored <c>ShadowDetail</c> onto the offered ladder.
        ///
        /// <para>
        /// Nearest-step rather than "out of range means default", so a value that is merely
        /// unusual (say 20) keeps the player's apparent intent instead of throwing it away, and
        /// so a corrupt or hand-edited value still lands on something the page can show and the
        /// renderer can allocate. The stored value is never written back here; the next save
        /// from the Options page normalises it.
        /// </para>
        /// </summary>
        public static int ResolveShadowDetail(int stored)
        {
            if (stored <= ShadowDetailLow) return ShadowDetailLow;
            if (stored >= ShadowDetailHigh) return ShadowDetailHigh;
            if (stored <= ShadowDetailMedium) return ShadowDetailMedium;
            return ShadowDetailHigh;
        }

        /// <summary>Resolution for a low/medium/high radio index (0, 1, 2).</summary>
        public static int ShadowDetailFromQuality(int quality)
        {
            switch (ResolveShadowQualityIndex(quality))
            {
                case 0: return ShadowDetailLow;
                case 2: return ShadowDetailHigh;
                default: return ShadowDetailMedium;
            }
        }

        /// <summary>Low/medium/high radio index (0, 1, 2) for a resolution.</summary>
        public static int ShadowDetailToQuality(int shadowDetail)
        {
            switch (ResolveShadowDetail(shadowDetail))
            {
                case ShadowDetailLow: return 0;
                case ShadowDetailHigh: return 2;
                default: return 1;
            }
        }

        /// <summary>Clamp a radio index to 0..2. Shared by the quality mappers above.</summary>
        private static int ResolveShadowQualityIndex(int quality)
        {
            if (quality < 0) return 0;
            if (quality > 2) return 2;
            return quality;
        }

        /// <summary>Name of a shadow detail step, for the settings strip and tooltips.</summary>
        public static string DescribeShadowDetail(int shadowDetail)
        {
            switch (ResolveShadowDetail(shadowDetail))
            {
                case ShadowDetailLow: return "Low";
                case ShadowDetailHigh: return "High";
                default: return "Medium";
            }
        }

        #endregion

        #region Names for the settings that are not ours to map

        /// <summary>
        /// Human readable name for a <c>LightingMode</c> level.
        ///
        /// <para>
        /// The four steps are not three, and the middle two are not the same thing, so the
        /// Options page's radio group uses these for its per-button captions rather than the
        /// generic Low/Medium/High.
        /// </para>
        /// </summary>
        public static string DescribeLightingMode(int lightingMode)
        {
            switch (StartupSettings.ResolveLightingMode(lightingMode))
            {
                case 0: return "Off";
                case 1: return "Basic";
                case 2: return "+ Shadows";
                default: return "Ultra";
            }
        }

        /// <summary>Human readable name for an <c>AntiAlias</c> level (off / 4x MSAA / 2x SSAA).</summary>
        public static string DescribeAntiAlias(int antiAlias)
        {
            switch (StartupSettings.ResolveAntiAlias(antiAlias))
            {
                case 0: return "Off";
                case 1: return "1x";
                default: return "2x";
            }
        }

        #endregion

        #region Water reflections

        /// <summary>Water and pools are drawn completely flat: no specular sheen at all.</summary>
        public const int WaterReflectionsOff = 0;

        /// <summary>The sheen the lot view has always drawn, at the old hard-coded exponent.</summary>
        public const int WaterReflectionsNormal = 1;

        /// <summary>A noticeably brighter, tighter sun/sky sheen on water and pools.</summary>
        public const int WaterReflectionsStrong = 2;

        public const int DefaultWaterReflections = WaterReflectionsNormal;
        public const int MinWaterReflections = WaterReflectionsOff;
        public const int MaxWaterReflections = WaterReflectionsStrong;

        /// <summary>
        /// Specular exponent for <see cref="WaterReflectionsOff"/>: the shader term is
        /// <c>1 - pow(|dot(view, normal)|, n)</c>, so 0 collapses it to nothing.
        /// </summary>
        public const float WaterShininessOff = 0f;

        /// <summary>
        /// The exponent <c>3DFloorGeometry</c> used as a literal for water and pool tiles
        /// before the setting existed.
        /// </summary>
        public const float WaterShininessNormal = 0.02f;

        /// <summary>
        /// The strong step. Still a broad fresnel-style approximation - it does not reflect the
        /// world, it brightens the surface as the view flattens towards it - so this is
        /// deliberately well short of the shader's clamp.
        /// </summary>
        public const float WaterShininessStrong = 0.06f;

        /// <summary>Clamp a stored <c>WaterReflections</c> to the offered range.</summary>
        public static int ResolveWaterReflections(int stored)
        {
            if (stored < MinWaterReflections) return MinWaterReflections;
            if (stored > MaxWaterReflections) return MaxWaterReflections;
            return stored;
        }

        /// <summary>The water specular exponent a quality step asks the floor shader for.</summary>
        public static float WaterShininessFor(int quality)
        {
            switch (ResolveWaterReflections(quality))
            {
                case WaterReflectionsOff: return WaterShininessOff;
                case WaterReflectionsStrong: return WaterShininessStrong;
                default: return WaterShininessNormal;
            }
        }

        /// <summary>Name of a water reflection step, for the settings strip and tooltips.</summary>
        public static string DescribeWaterReflections(int quality)
        {
            switch (ResolveWaterReflections(quality))
            {
                case WaterReflectionsOff: return "Off";
                case WaterReflectionsStrong: return "Strong";
                default: return "On";
            }
        }

        #endregion

        /// <summary>
        /// The preferences Simitone stamps into a brand new <c>config.ini</c>, i.e. what a
        /// player who has changed nothing ends up with.
        ///
        /// <para>
        /// This is the <em>first run</em> set rather than FreeSO's raw ini defaults, and the two
        /// differ on exactly two values: <c>LightingMode</c> (ultra, not the ini's -1, which means
        /// no lighting at all) and <c>ComplexShaders</c> (on, not off).
        /// <see cref="StartupSettings.ApplyGraphicsDefaults"/> writes those two, plus
        /// <c>EnableTransitions</c> and <c>AntiAlias</c> so the intent is stated rather than
        /// inherited, and reads the numbers from here - which is why the first-run block in
        /// <c>Program.Main</c> and this property cannot answer "what does an untouched install
        /// look like" differently.
        /// </para>
        ///
        /// <para>
        /// <b>The single named source for the first-run set.</b> It used to be stated here and
        /// again, as literals, in <c>Program.Main</c> two hundred lines away, and only four of the
        /// eleven values were ever stamped by either. The rest are named from the constants below
        /// rather than re-typed, and <c>StartupSettingsTests</c> compares every value this claims
        /// against a freshly created <c>GlobalSettings</c>, so a value that starts agreeing with
        /// the ini default by accident rather than by decision is visible.
        /// </para>
        /// </summary>
        public static GraphicsPreferences Defaults => new GraphicsPreferences(
            StartupSettings.DefaultLightingMode,
            StartupSettings.MinAntiAlias,
            DefaultShadowDetail,
            DefaultWaterReflections,
            weather: true,
            directionalLight: true,
            complexShaders: StartupSettings.DefaultComplexShaders,
            enableTransitions: StartupSettings.DefaultEnableTransitions,
            smoothZoom: true,
            // Low, the lowest step that still draws surroundings. M4's gate needs
            // SurroundingLotMode > 0, so 0 would leave every lot standing in a vacuum even with
            // the 3D neighbourhood on; see SurroundingLotModeRow.Default for why it is not 2.
            surroundingLots: SurroundingLotModeRow.Default,
            // On: in 3D this is what makes the sun move. The row is still there to turn it off.
            sunCycle: SunCycleSetting.DefaultEnabled,
            // 3D, the connected neighbourhood. 2D is the middle step of the same three-way row.
            neighbourhoodView: NeighbourhoodViewSetting.Default);

        /// <summary>
        /// Push a snapshot onto the live renderer configuration.
        ///
        /// <para>
        /// The config is mutated in place rather than replaced. <c>WorldConfig.Mode</c> (the
        /// 2D/hybrid/3D choice) and the render offsets are owned by the graphics-mode switcher
        /// and live on this instance; a fresh one would silently drop the player back to the
        /// default graphics mode.
        /// </para>
        ///
        /// <para>
        /// This does not allocate render targets. The caller has to follow up with
        /// <c>World.ChangedWorldConfig(device)</c>, which is what notices the new lighting
        /// resolution and rebuilds the light batch.
        /// </para>
        ///
        /// <para>
        /// <c>WorldConfig.SmoothZoom</c> is deliberately not written: its setter is empty, and
        /// the flag it reads back (<c>EnableTransitions</c>) is written above. Simitone eases
        /// its own 2d zoom, so the ini's <c>SmoothZoom</c> is carried in the snapshot for
        /// callers that want it but has no engine field to land in.
        /// </para>
        /// </summary>
        public static void Apply(WorldConfig config, GraphicsPreferences preferences)
        {
            if (config == null) return;

            config.LightingMode = preferences.LightingMode;
            config.AA = preferences.AntiAlias;
            config.ShadowDetail = preferences.ShadowDetail;
            config.WaterShininess = WaterShininessFor(preferences.WaterReflections);
            config.Weather = preferences.Weather;
            config.Directional = preferences.DirectionalLight;
            config.Complex = preferences.ComplexShaders;
            config.EnableTransitions = preferences.EnableTransitions;
            config.SurroundingLots = preferences.SurroundingLots;
            config.SunCycle = preferences.SunCycle;
        }
    }
}
