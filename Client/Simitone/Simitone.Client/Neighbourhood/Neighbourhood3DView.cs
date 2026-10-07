/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using FSO.Client;
using FSO.Common;
using FSO.Common.Rendering;
using FSO.Common.Rendering.Framework;
using FSO.Common.Rendering.Framework.Camera;
using FSO.Common.Rendering.Framework.Model;
using FSO.LotView.Components;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The generated neighbourhood, as a scene: the terrain, the trees, and one facade per lot.
    ///
    /// <para><b>Behind the UI, not instead of it.</b> The scene is added to
    /// <c>GameFacade.Scenes</c>, which <c>SimitoneGame</c> installs as the first of the two
    /// graphics layers, ahead of the UI layer. Everything in
    /// <c>UINeighborhoodSelectionPanel</c> - the house buttons, the neighbourhood switcher, the lot
    /// panel - is therefore drawn on top of the world, which is §7.3's "drawn behind the existing
    /// buttons/switcher" and needs no coordinate agreement between the two to be true.
    /// </para>
    ///
    /// <para><b>What it owns.</b> A <see cref="NeighbourhoodTerrainRenderer"/>, a
    /// <see cref="NeighbourhoodFoliage"/>, a <see cref="NeighbourhoodFacades"/> cache, and a
    /// <c>BasicCamera</c> it drives from a <see cref="NeighbourhoodCameraPlacement"/>. The scene
    /// reads the world through an <see cref="INeighbourhoodSceneSource"/>, so it reads no game
    /// state of its own.
    /// </para>
    ///
    /// <para><b>How the camera is driven.</b> Not by the scene. The panel computes a
    /// <c>NeighCameraTarget</c> - the same target the 2D bitmap view tweens to - and hands it over
    /// through <see cref="Focus"/>. The scene converts that into a placement with
    /// <see cref="NeighbourhoodCameraPlacement.For"/> and moves, so the 2D zoom and the 3D zoom
    /// are the same tween of the same numbers rather than two features that happen to both be
    /// called zoom.
    /// </para>
    ///
    /// <para><b>Depth, and what the background is.</b> The engine's frame clear is
    /// <c>Device.Clear(ClearColor)</c>, which clears colour only, and this layer draws before the
    /// lot view's own targets are set up. So the frame is cleared here, at the start of the first
    /// draw, colour and depth together. The colour is not arbitrary: it is the horizon row of the
    /// sky dome that is drawn immediately afterwards, read from the same
    /// <c>Content/Textures/skycol.png</c> strip the dome itself samples, so the pixels the dome
    /// does not cover - and the map's own clipped edge - sit against the sky rather than against a
    /// grey void. See <see cref="NeighbourhoodSkyStrip"/>.
    /// </para>
    ///
    /// <para><b>Sky and light are the city's, not this layer's.</b> The scene holds a
    /// <see cref="AbstractSkyDome"/> - the same type <c>Terrain</c> stands up at
    /// <c>Terrain.cs:213</c> and draws at <c>:1556</c> - and its light, fog and sun all come from
    /// <see cref="NeighbourhoodCityLighting"/>, which is <c>Terrain.SetTimeOfDay</c>
    /// (<c>:1273-1327</c>) and <c>Terrain.Draw</c>'s shader setup (<c>:1481-1519</c>) lifted into
    /// a pure value so it can be reasoned about without a device. What that buys is the point of
    /// the exercise: the tint, the sun's direction, the shadow strength and the fog are all
    /// functions of one number, so a neighbourhood at dusk is dim and orange because the clock said
    /// so rather than because someone picked a beige once.
    /// </para>
    ///
    /// <para><b>And when there is no clock, there is a fixed afternoon.</b>
    /// <see cref="SetTimeOfDay"/> is the seam a caller with a clock pushes through, and a scene
    /// nobody has told sits at <see cref="DefaultTimeOfDay"/> - a
    /// deliberate, bright, readable afternoon, because a map screen is a screen and not a moment
    /// in a day (the same reasoning <c>TS1GameScreen.UpdateNeighbourhoodWorldLighting</c> gives
    /// for not letting the world clock dim this screen at dusk).
    /// </para>
    ///
    /// <para><b>What this layer decides about the level of detail.</b> Two things, both of them
    /// the camera's to decide and neither of them this layer's arithmetic. How much of the map is
    /// in frame picks the level, through a <see cref="NeighbourhoodMeshLodSelector"/> - one per
    /// view, because the selector's whole state is the camera's history and a history belongs to a
    /// camera. And where the camera is looking decides which slices of the close level the terrain
    /// renderer refines, which is a work queue rather than a draw decision: the renderer builds them
    /// on a worker and uploads them a slice at a time, and the ground under any chunk whose slice
    /// has not landed yet is drawn from the whole-map level, which is coarser rather than absent.
    /// Neither call can stall a frame, which is the property that lets them be made every frame.
    /// </para>
    /// </summary>
    public sealed class Neighbourhood3DView : _3DScene, INeighbourhoodRenderSignal
    {
        /// <summary>
        /// The time of day a scene draws when nothing has told it one: 13:12.
        /// </summary>
        /// <remarks>
        /// A fixed afternoon, and deliberately so. The alternative - reading the neighbourhood's
        /// world clock - is what <c>TS1GameScreen.UpdateNeighbourhoodWorldLighting</c> explicitly
        /// refuses to do for the map screen, for a reason worth repeating here: a clock that runs
        /// from sunrise to sunset dims this screen to an unreadable blue at dusk and keeps it there
        /// all night, and a map has to stay readable. So the clock is a seam
        /// (<see cref="SetTimeOfDay"/>) and this is what happens until somebody pushes through it.
        /// </remarks>
        public const double DefaultTimeOfDay = 0.55;

        /// <summary>
        /// The light direction a scene with no time of day uses, which is the city's own answer at
        /// <see cref="DefaultTimeOfDay"/> rather than a number of its own.
        /// </summary>
        /// <remarks>
        /// Kept as a public constant because <c>SimitoneSurroundingsPlacement.LightDirectionFor</c>
        /// falls back to it when a lot's light batch has not been built yet - a fall-back that has
        /// to agree with what the map view draws, or a lot is briefly lit from a different sun than
        /// the neighbourhood around it.
        /// </remarks>
        public static readonly Vector3 LightDirection =
            NeighbourhoodCityLighting.At(DefaultTimeOfDay).LightDirection;

        /// <summary>
        /// The light colour and strength a scene with no time of day uses: the city's own at
        /// <see cref="DefaultTimeOfDay"/>. See <see cref="LightDirection"/>.
        /// </summary>
        public static readonly Vector4 LightColor =
            NeighbourhoodCityLighting.At(DefaultTimeOfDay).LightColour;

        /// <summary>
        /// The colour the far edge of the map fades into when the fog cannot be derived from a time
        /// of day. See <see cref="NeighbourhoodCityLighting.FogColour"/>.
        /// </summary>
        public static readonly Vector4 FogColor =
            NeighbourhoodCityLighting.At(DefaultTimeOfDay).FogColour;

        /// <summary>
        /// The fade distance used before the camera has a placement, in tiles.
        /// </summary>
        /// <remarks>
        /// A scene with a placement does not use this: the fade is
        /// <see cref="NeighbourhoodCameraPlacement.FogDistance"/> - three times the camera's own
        /// distance to its target - which is the aerial perspective the placement was designed for
        /// and the value the frame report already records. <c>NeighbourhoodCityLighting.FogMaxDistFor</c>
        /// is the same question asked of the city's zoom curve and is kept as the reference for it,
        /// but it is not what the scene draws with: its width-to-zoom mapping put the whole map on
        /// the city's near curve and fogged the world flat.
        /// </remarks>
        public const float FogDistance = 900f;

        private GraphicsDevice _device;
        private readonly INeighbourhoodSceneSource _source;
        private readonly BasicCamera _camera;
        private readonly NeighbourhoodWorldMapping _mapping;
        private readonly int _mapSize;

        private NeighbourhoodTerrainRenderer _terrain;
        private NeighbourhoodFoliage _foliage;
        private NeighbourhoodFacades _facades;
        private Effect _vertexShader;
        private Effect _pixelShader;
        private string _failure;
        private bool _depthClearedThisFrame;
        private bool _summaryLogged;
        private bool _facadeSummaryLogged;
        private bool _shaderFailureLogged;
        private bool _skyFailureLogged;
        private NeighbourhoodCameraPlacement _placement;

        /// <summary>The city's light, fog and sun for <see cref="TimeOfDay"/>.</summary>
        private NeighbourhoodCityLighting _lighting =
            NeighbourhoodCityLighting.At(DefaultTimeOfDay);

        /// <summary>
        /// The sky dome, or <c>null</c> when the sky strip could not be loaded.
        /// </summary>
        /// <remarks>
        /// Owned here and disposed here. <c>AbstractSkyDome</c> holds a vertex buffer, an index
        /// buffer and a texture on the graphics device, so it is one of the few things in this scene
        /// that a device reset genuinely kills - see <see cref="DeviceReset"/>.
        /// </remarks>
        private AbstractSkyDome _skyDome;

        /// <summary>
        /// The weather the dome is handed, held still and never updated.
        /// </summary>
        /// <remarks>
        /// <c>AbstractSkyDome.Draw</c> reads exactly one field off it,
        /// <c>WeatherIntensity</c>, and that is left at zero: the city screen has no weather, and
        /// <c>WeatherController.Update</c> is not called because it reads the wall clock, rolls an
        /// RNG against it and wants a <c>Blueprint</c> for the particle component it builds - none of
        /// which belongs on a map screen that must look the same on every machine at every hour.
        /// </remarks>
        private WeatherController _weather;

        /// <summary>
        /// Picks the level of detail from the camera's width, and remembers where it has been.
        ///
        /// <para>
        /// Per view rather than static, because the whole of it is history: a stateless rule answers
        /// the same width the same way every time, which is what makes it flicker as a camera
        /// tweenes across a boundary. One selector per view is one camera's history, which is the
        /// unit that means anything.
        /// </para>
        /// </summary>
        private readonly NeighbourhoodMeshLodSelector _lodSelector = new NeighbourhoodMeshLodSelector();

        private NeighbourhoodFrameReport _report = new NeighbourhoodFrameReport();

        /// <summary>
        /// How many facades the last draw found inside the frustum, whatever it then managed to
        /// submit.
        /// </summary>
        /// <remarks>
        /// Kept apart from <c>NeighbourhoodFacades.DrawnPrimitives</c> on purpose. A frustum that
        /// touches three lots and submits three thousand triangles and a frustum that touches three
        /// lots and submits nothing because every draw threw are the same frame from the cull's
        /// point of view, and the report is only useful if it can tell them apart - which is the
        /// whole reason the facades carry a submitted count rather than a cached count.
        /// </remarks>
        public int FacadesVisibleThisFrame
        {
            get; private set;
        }

        /// <summary>
        /// What the last <see cref="Draw(GraphicsDevice)"/> actually submitted, for the log and for
        /// the backdrop rule's promotion gate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the number the whole bug was missing.</b> The health check counted the
        /// <em>mesh's</em> triangles, so a world whose draw culled everything, drew into a culled
        /// state or looked at nothing still reported hundreds of thousands of triangles and was
        /// promoted over the painted map - producing exactly the blank screen in the field. The
        /// report counts what went to the device, so "the mesh holds 315424 triangles" and "this
        /// frame submitted 0" are now two different sentences.
        /// </para>
        /// </remarks>
        public NeighbourhoodFrameReport LastFrame
        {
            get { return _report; }
        }

        /// <summary>How many draw reports have been produced, for throttling and for tests.</summary>
        public int FrameCount
        {
            get { return _report.Frame; }
        }

        private void BeginFrameReport()
        {
            _report.Reset(_report.Frame + 1, _placement);
        }

        private void Note(string why)
        {
            _report.Skip(why);
        }

        private void EndFrameReport(int terrainBefore, Matrix viewProjection, BoundingFrustum frustum,
            float fogDistance)
        {
            //The terrain's own submission, against the same count the previous frame left behind,
            //so a frame where the terrain draw returned early reads as zero rather than as the last
            //frame's number carried forward.
            _report.Submit(
                _terrain.DrawnPrimitives, _terrain.DrawnIndices, _terrain.DrawnDrawCalls,
                _terrain.DrawnChunks,
                terrainDrawn: _terrain.DrawRan && _terrain.DrawnPrimitives > terrainBefore);
            //The facades and the trees, counted by the passes that own them rather than by a
            //"did we have any cached" bool. That bool was the gap: a neighbourhood with 38 lots
            //whose facade draw threw every frame reported "facades drawn" every frame, and the
            //report could not tell a street of houses from a bare field. FacadeDrawn is now
            //derived from a submitted count, so it cannot be true without triangles behind it.
            _report.SubmitProps(
                _facades?.DrawnPrimitives ?? 0,
                _facades?.DrawnSurfaces ?? 0,
                FacadesVisibleThisFrame,
                _foliage?.DrawnPrimitives ?? 0);
            _report.RoadTriangles = _terrain.DrawnRoadPrimitives;
            _report.Lod = _terrain.Lod;
            _report.MeshTriangles = _terrain.TotalPrimitives;
            _report.MeshReady = _terrain.MeshReady;
            _report.CameraTarget = _camera.Target;
            _report.CameraPosition = _camera.Position;
            _report.TilesAcross = _placement.TilesAcross;
            _report.ViewProjection = viewProjection;
            _report.Frustum = frustum;
            _report.FogDistance = fogDistance;
            _report.VisibleRegion = _terrain.VisibleRegion;
            _report.HasVisibleRegion = _terrain.HasVisibleRegion;
        }

        /// <summary>
        /// Writes the frame report to the log, on the frames where it can say something new.
        ///
        /// <para>
        /// The first few frames, because that is when "nothing has been submitted" is the
        /// interesting answer; then a slow heartbeat while a frame is submitted nothing, so a world
        /// that has quietly stopped drawing says so in the log rather than needing a screenshot;
        /// and then on a change, so a zoom or a cull that starts working is visible. Never every
        /// frame: a per-frame line in a log the player sends in is noise that buries the one line
        /// that matters.
        /// </para>
        /// </summary>
        private void LogFrameReport()
        {
            if (!_report.ShouldLog) return;
            SimitoneLog.Info("The generated neighbourhood's frame: " + _report);
        }

        /// <summary>Why the scene could not be built, or <c>null</c> if it could.</summary>
        public string Failure
        {
            get { return _failure; }
        }

        /// <summary>Whether there is a world to draw.</summary>
        public bool HasWorld
        {
            get { return _terrain != null; }
        }

        /// <summary>The camera, exposed so the panel can read the placement for diagnostics.</summary>
        public NeighbourhoodCameraPlacement Placement
        {
            get { return _placement; }
        }

        /// <summary>How many triangles the terrain holds, for a log line.</summary>
        public int TerrainPrimitives
        {
            get { return _terrain == null ? 0 : _terrain.TotalPrimitives; }
        }

        /// <summary>How many trees are on the map, for a log line.</summary>
        public int TreeCount
        {
            get { return _foliage?.TreeCount ?? 0; }
        }

        /// <summary>How many lot facades are cached, for a log line.</summary>
        public int FacadeCount
        {
            get { return _facades?.CachedCount ?? 0; }
        }

        /// <summary>Everything the facade pass has said, for a log line.</summary>
        public IReadOnlyList<string> FacadeLog
        {
            get { return _facades?.Log ?? (IReadOnlyList<string>)new string[0]; }
        }

        /// <summary>Every lot the facade pass could not build, and why, for a log line.</summary>
        public IReadOnlyList<string> FacadeFailures
        {
            get { return _facades?.Failures ?? (IReadOnlyList<string>)new string[0]; }
        }

        /// <summary>How many lots the facade pass has tried, built or not.</summary>
        public int FacadeAttemptedCount
        {
            get { return _facades?.AttemptedCount ?? 0; }
        }

        /// <summary>How many lots still have no facade, for a log line.</summary>
        public int FacadeOutstanding
        {
            get { return _facades?.Outstanding(_source?.ActiveLot, _focus) ?? 0; }
        }

        /// <summary>Everything the content loader could not load, for a log line.</summary>
        public IReadOnlyList<string> ContentWarnings
        {
            get { return _terrain?.Content?.Warnings ?? (IReadOnlyList<string>)new string[0]; }
        }

        /// <summary>
        /// Builds a scene over a generated neighbourhood, or returns <c>null</c> with the reason.
        /// </summary>
        /// <param name="device">The graphics device.</param>
        /// <param name="source">The generated world to draw.</param>
        /// <param name="reason">Why it could not be built, when it could not be.</param>
        public static Neighbourhood3DView TryCreate(
            GraphicsDevice device, INeighbourhoodSceneSource source, out string reason)
        {
            if (device == null)
            {
                reason = "there is no graphics device";
                return null;
            }
            if (source == null || source.Map == null)
            {
                reason = "there is no generated map";
                return null;
            }
            try
            {
                var scene = new Neighbourhood3DView(device, source);
                if (scene.HasWorld)
                {
                    reason = null;
                    return scene;
                }
                //the scene half-built before it found out it could not finish; take its buffers with
                //it rather than leaving them for the garbage collector to find on the device
                var failure = scene._failure ?? "the generated world could not be prepared";
                scene.Dispose();
                reason = failure;
                return null;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return null;
            }
        }

        private Neighbourhood3DView(GraphicsDevice device, INeighbourhoodSceneSource source)
            : base(device)
        {
            _device = device;
            _source = source;
            _mapping = source.Mapping;
            _mapSize = source.Layout?.MapSize ?? NeighbourhoodLayout.DefaultMapSize;

            //the two city effects, by the same names FSO.Client.Rendering.City.Terrain loads them
            //under. They ship in the output; a missing one leaves the scene drawing nothing rather
            //than throwing every frame, which is what the null check in Draw is for.
            try
            {
                _vertexShader = GameFacade.Game?.Content?.Load<Effect>("Effects/VerShader");
                _pixelShader = GameFacade.Game?.Content?.Load<Effect>("Effects/PixShader");
            }
            catch (Exception ex)
            {
                _failure = "the city shaders could not be loaded: " + ex.Message;
            }

            BuildSky(device);

            var map = source.Map;
            _terrain = NeighbourhoodTerrainRenderer.TryCreate(device, map, out var terrainReason);
            if (_terrain == null)
            {
                _failure = "the terrain could not be prepared: " + terrainReason;
            }
            else
            {
                _terrain.BeginMeshBuild();
                _foliage = new NeighbourhoodFoliage(device, map, _terrain.Content);
                _facades = new NeighbourhoodFacades(device, source.Layout, map);
            }

            var centre = NeighbourhoodCameraPlacement.ResolveCentre(source.Layout, _mapSize);
            _camera = new BasicCamera(device, Vector3.Zero, new Vector3(centre.X, 0f, centre.Y), Vector3.Up)
            {
                NearPlane = 1f,
                FarPlane = 4000f
            };
            Camera = _camera;

            Focus(NeighCameraMachine.FullScreen(), null);
        }

        // ---- the sky ----------------------------------------------------------------------------------
        //
        // Terrain stands its dome up in its constructor (Terrain.cs:213) with a time of zero and
        // lets Draw move it, so the same shape here: the dome is built once against the device and
        // the time only ever arrives through Draw, which is also where AbstractSkyDome decides
        // whether the strip has to be rebuilt at all (AbstractSkyDome.cs:251-252). What is different
        // is that Terrain can afford a dome whose gradient texture silently failed to load -
        // BuildSkyDomeForStrip dereferences GradTex.Height and throws - because TSO ships the sky
        // with the game. Simitone has to be told.

        /// <summary>
        /// Stands the sky dome up against <paramref name="device"/>, or explains why it could not.
        /// </summary>
        /// <remarks>
        /// The sky strip is checked first and independently of the dome, because the strip is what
        /// the frame clear colour is sampled from
        /// (<see cref="NeighbourhoodSkyStrip.HorizonColourFor"/>) and the map still needs a
        /// background when the dome itself cannot be built. A missing
        /// <c>Content/Textures/skycol.png</c> is a packaging problem rather than a code one, so it
        /// is said once per process and the scene carries on with the tint as its background.
        /// </remarks>
        private void BuildSky(GraphicsDevice device)
        {
            //never updated, and never to be: see the remark on the field
            _weather = new WeatherController(new List<ParticleComponent>());
            _weather.OutsideWeatherTint = Color.White;
            _weather.TintColor = _lighting.TintColour.ToVector4();

            if (!NeighbourhoodSkyStrip.Load())
            {
                NoteSkyFailure("Content/Textures/skycol.png could not be read, so the generated"
                    + " neighbourhood is drawn without a sky: the map's clipped edge will sit against"
                    + " a flat colour. " + _lighting.TintColour
                    + " at " + DefaultTimeOfDay.ToString("0.00") + " of the day.");
                return;
            }

            try
            {
                _skyDome = new AbstractSkyDome(device, (float)DefaultTimeOfDay);
            }
            catch (Exception ex)
            {
                _skyDome = null;
                NoteSkyFailure("the sky dome could not be built (" + ex.Message + "), so the generated"
                    + " neighbourhood is drawn without a sky.");
            }
        }

        private void NoteSkyFailure(string message)
        {
            SkyFailure = message;
            if (_skyFailureLogged) return;
            _skyFailureLogged = true;
            SimitoneLog.Warn("The generated neighbourhood's sky: " + message);
        }

        /// <summary>Why the scene has no sky dome, or <c>null</c> when it has one.</summary>
        public string SkyFailure { get; private set; }

        /// <summary>Whether the scene has a sky dome to draw.</summary>
        public bool HasSky
        {
            get { return _skyDome != null; }
        }

        /// <summary>
        /// The time of day the scene is drawing, as a fraction of the day: 0 is midnight, 0.5 is
        /// noon. <see cref="DefaultTimeOfDay"/> until somebody says otherwise.
        /// </summary>
        public double TimeOfDay { get; private set; } = DefaultTimeOfDay;

        /// <summary>The city's light, fog and sun for <see cref="TimeOfDay"/>.</summary>
        public NeighbourhoodCityLighting Lighting
        {
            get { return _lighting; }
        }

        /// <summary>
        /// Moves the scene's sun, tint and fog to <paramref name="timeOfDay"/>.
        /// </summary>
        /// <remarks>
        /// The seam a caller with a clock pushes through, and TSO's own signature -
        /// <c>Terrain.SetTimeOfDay(double)</c> at <c>Terrain.cs:1273</c> - because everything it
        /// does here is what that does there: recolour the scene, work out where the sun is over the
        /// middle of the map, and note how strong its shadow is. There is no caller yet, and that is
        /// not an oversight: nothing on this screen has a clock to offer (see
        /// <see cref="DefaultTimeOfDay"/>), and a dead seam that says who should fill it is worth
        /// more than a guess at a clock that would make the screen unreadable at night.
        /// <para>
        /// Out-of-range times are clamped rather than trusted. <c>Terrain</c> clamps the top only
        /// (<c>time = Math.Min(0.999999999, time)</c>, <c>:1275</c>) because its one caller has
        /// already guaranteed a fraction; a caller that hands this a wall-clock hour, or a negative
        /// from an unseeded clock, would index
        /// <c>TimeOfDayConfig.ColorFromTime</c>'s table off the end and throw inside a draw.
        /// </para>
        /// </remarks>
        /// <param name="timeOfDay">A fraction of the day, 0 to 1.</param>
        public void SetTimeOfDay(double timeOfDay)
        {
            TimeOfDay = Clamp01(timeOfDay);
            _lighting = NeighbourhoodCityLighting.At(TimeOfDay);
            if (_weather != null) _weather.TintColor = _lighting.TintColour.ToVector4();
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return DefaultTimeOfDay;
            if (value < 0.0) return 0.0;
            if (value > 0.999999999) return 0.999999999;
            return value;
        }

        /// <summary>
        /// Points the camera at a <c>NeighCameraMachine</c> target.
        /// </summary>
        /// <param name="target">
        /// The target. <c>NeighCameraMachine.FullScreen()</c> and
        /// <c>NeighCameraMachine.HouseFocus(...)</c> are the two the panel produces.
        /// </param>
        /// <param name="houseBitmapPosition">
        /// Where the focused lot's button is on the current bitmap, from
        /// <c>UINeighborhoodSelectionPanel.HousePositions</c>, or <c>null</c>. Only consulted for
        /// a lot the generated layout does not contain.
        /// </param>
        public void Focus(NeighCameraTarget target, Vector2? houseBitmapPosition)
        {
            _placement = NeighbourhoodCameraPlacement.For(
                target, _mapping, _source?.Layout, _mapSize, Aspect(), GroundHeight(), houseBitmapPosition);

            _camera.Position = _placement.Position;
            _camera.Target = _placement.Target;
            _camera.Up = Vector3.Up;
        }

        /// <summary>
        /// A one-line summary of what was built, for the log. Read after the first
        /// <see cref="Update"/>: before the mesh upload lands the triangle count is zero.
        /// </summary>
        public string Describe()
        {
            if (_terrain == null) return "no generated world: " + _failure;
            //The facades are a count of a queue that is still filling when this runs, so it is
            //given as a fraction with what is left to do. A bare "0 lot facades" reads as a
            //neighbourhood with no houses when it means a neighbourhood whose first two have not
            //been built yet, and those two need very different fixes.
            return "generated world: " + TerrainPrimitives + " terrain triangles, "
                + TreeCount + " trees, " + FacadeCount + " of " + FacadeEligible + " lot facades"
                + ", sky " + (HasSky ? "on at " + TimeOfDay.ToString("0.00") : "OFF (" + SkyFailure + ")")
                + (FacadeOutstanding > 0 ? " (" + FacadeOutstanding + " still building)" : string.Empty)
                + (_terrain.BuildFailure != null ? " (terrain mesh failed: " + _terrain.BuildFailure + ")" : string.Empty);
        }

        /// <summary>How many lots a complete facade pass would build here, for a log line.</summary>
        public int FacadeEligible
        {
            get { return NeighbourhoodFacadePlan.Eligible(_source?.Layout, _source?.ActiveLot); }
        }

        private float GroundHeight()
        {
            if (_terrain != null) return _terrain.MeanGroundHeight;
            return NeighbourhoodCityMeshBuilder.MeanGroundHeight(_source?.Map);
        }

        private float Aspect()
        {
            //GraphicsDevice.Viewport is a plain struct in MonoGame, so the null check is on the
            //device rather than on the viewport
            if (_device == null) return NeighbourhoodCameraPlacement.DefaultAspect;
            var viewport = _device.Viewport;
            return viewport.Height > 0 ? viewport.AspectRatio : NeighbourhoodCameraPlacement.DefaultAspect;
        }

        /// <summary>
        /// The lot the player is standing inside, or <c>null</c> on the neighbourhood screen.
        /// </summary>
        /// <remarks>
        /// Set on the scene source rather than passed in per call, because it is a property of the
        /// <em>world</em> and not of the view: the neighbourhood screen has no active lot, and M4's
        /// in-lot surroundings has exactly one. The facade cache reads it to skip the lot the lot
        /// view is already drawing in full.
        /// </remarks>
        /// <param name="lotNumber">The lot, or <c>null</c>.</param>
        public void SetActiveLot(int? lotNumber)
        {
            if (_source is NeighbourhoodSceneSource scene) scene.ActiveLot = lotNumber;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            //The mesh is built on a worker and uploaded on the game thread, so the first frame is
            //not the frame that knows how big it turned out. The summary is logged here, once,
            //rather than from the constructor - where it would always say zero triangles and be
            //the number in the log forever.
            if (!_summaryLogged && _terrain != null && !_terrain.IsBuilding)
            {
                _summaryLogged = true;
                var warnings = ContentWarnings;
                if (warnings.Count > 0)
                {
                    foreach (var warning in warnings) SimitoneLog.Warn(warning);
                }
                SimitoneLog.Info("The generated neighbourhood is ready: " + Describe());
            }

            //A facade mesh costs a whole World and VM, so the cache builds a couple a frame nearest
            //the camera first, within a wall-clock budget as well: a fifty-lot neighbourhood fills
            //in over a second rather than stalling the screen for four, and one expensive lot
            //cannot make a frame long.
            if (_facades != null)
            {
                _facades.BeginFrame();
                var step = _facades.Step(_source?.ActiveLot, _focus);
                //The queue is empty once every drawable lot has been tried, built or not, and that
                //is the only moment at which "N facades" is an answer rather than a running
                //count. Logging it here - once, with the reasons - is what turns a neighbourhood
                //with no houses into a sentence saying which lots failed and why.
                if (step.Drained && !_facadeSummaryLogged)
                {
                    _facadeSummaryLogged = true;
                    SimitoneLog.Info("The generated neighbourhood has " + _facades.Describe(_source?.ActiveLot, _focus) + ".");
                    foreach (var failure in _facades.Failures)
                    {
                        SimitoneLog.Warn("  " + failure);
                    }
                }
            }
        }

        private Vector2 _focus
        {
            get { return new Vector2(_placement.Target.X, _placement.Target.Z); }
        }

        public override void Draw(GraphicsDevice device)
        {
            base.Draw(device);
            BeginFrameReport();

            if (_terrain == null)
            {
                Note("no generated world");
                return;
            }
            if (_vertexShader == null || _pixelShader == null)
            {
                //Named, once. A missing city effect is a silent blank screen otherwise, because the
                //scene draws nothing and everything else keeps working.
                if (!_shaderFailureLogged)
                {
                    _shaderFailureLogged = true;
                    SimitoneLog.Warn("The generated neighbourhood has no city shader, so it is not"
                        + " drawing anything: Effects/VerShader and Effects/PixShader did not load."
                        + " The painted map is the backdrop until that is fixed.");
                }
                Note("the city shaders did not load");
                return;
            }

            if (!_depthClearedThisFrame)
            {
                //Colour as well as depth, and the colour is the sky's own horizon: the horizon row
                //of the strip the dome is about to draw, sampled through NeighbourhoodSkyStrip.
                //That is the answer to "what is behind the map", and the reason it is not the fog
                //colour - which is what this used to clear to, on the grounds that there was no sky
                //to speak of - is that the fog colour is a distance-based blend towards haze, not a
                //sky: it is what the far edge of the ground fades into, and the ground above its own
                //edge is not ground. Terrain clears to its tint instead (Terrain.cs:1549), which is
                //equally a flat colour under a dome; the strip is better because it is the colour
                //that is actually one row away from where the dome's colours meet.
                device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, HorizonColour(), 1f, 0);
                _depthClearedThisFrame = true;
            }
            device.DepthStencilState = DepthStencilState.Default;

            //The camera, the frustum and the view-projection all come from the same placement, so
            //the cull is tested against the exact matrix the vertices are transformed by. That is
            //not a tidiness point: a frustum built from a different matrix is the failure that
            //culls every chunk at full screen, and a full-screen cull is a blank screen.
            var view = _camera.View;
            var projection = _camera.Projection;
            var viewProjection = view * projection;
            var frustum = new BoundingFrustum(viewProjection);
            var lighting = _lighting;
            var fogColor = lighting.FogColour;
            var lightColor = lighting.LightColour;
            //The fog distance is the placement's, not NeighbourhoodCityLighting.FogMaxDistFor's.
            //The city's formula scales the clear-sky distance by the camera's zoom curve, but the
            //neighbourhood mapped its framing width onto that curve backwards: the whole map in
            //frame landed on Zoom3D = 0, which is the city's *near* view, and gave a fade that
            //completed in ~360 tiles - shorter than the ~490 the camera stands from the middle of
            //the map at 16:9. Every visible tile was therefore beyond the fade and the ground drew
            //as one flat sheet of FogColor, hiding both its texture and its relief.
            //NeighbourhoodCameraPlacement.FogDistance is a function of the camera's own distance
            //(three times it), which is the aerial perspective the placement was built for: at the
            //full-screen view the near ground is barely hazed and the far corner is about half
            //faded. See NeighbourhoodCameraPlacement.FogDistanceFactor.
            var tilesAcross = _placement.TilesAcross;
            var fog = _placement.FogDistance;
            if (!(fog > 1f)) fog = FogDistance;

            //The level of detail is the camera's decision, made from how much of the map is in
            //frame, and it is a buffer swap rather than a rebuild. The selector rather than the
            //stateless rule, because a camera tweening through a level boundary crosses it twice -
            //once on the way out and once on the way back - and a pop either way is the one artefact
            //of a level of detail that a player always notices. See NeighbourhoodMeshLodSelector.
            _terrain.SetLod(_lodSelector.Select(tilesAcross));

            //And the close level's ground, a slice at a time for the region the camera is in. The
            //region is last frame's, which is a frame of lag on a work queue that is already a
            //slice ahead of the camera; the focus is this frame's, so the ground under the player
            //is the first thing refined. Cheap enough to ask every frame and it is how the queue
            //knows where to go.
            _terrain.UpdateSubdivSlices(_focus, _terrain.VisibleRegion);

            //The sky, first and before anything else, exactly where Terrain puts it
            //(Terrain.cs:1549 clear, :1556 dome, :1578 geometry onwards). The dome neither tests nor
            //writes depth - DepthStencilState.None - so this is not a depth-ordering requirement but
            //a painter's one: it is the background, and anything drawn over a background is the
            //foreground. Drawing it last would paint the sky over the houses and the wood.
            DrawSky(device, lighting, view, projection);

            //Order, and the reason for it. The facades draw first because they are the opaque
            //surface: the ground is alpha blended for its fade skirt, so a terrain-first order
            //draws the ground over the houses standing on it. And the facades go first even though
            //that means a facade failure takes the terrain with it - which is exactly what happened
            //for as long as the facade draw threw, and is the reason both are fixed rather than
            //one being left to be found later.
            //
            //view, not viewProjection, on the facade and foliage calls: both are on RenderCityObj
            //and both take their fog distance from length(vPos) over CityObjFogVS's view-space
            //vPos. Handed the view-projection that length is a clip-space quantity, the whole map
            //clamps to fully fogged, and the houses and the trees draw as a flat sheet of
            //FogColor while the terrain - which is given the view - fades correctly. The three
            //passes have to agree or they are not drawing the same world.
            var terrainBefore = _terrain.DrawnPrimitives;
            FacadesVisibleThisFrame =
                _facades?.Draw(_vertexShader, _pixelShader, viewProjection, view,
                    lightColor, fogColor, fog, frustum) ?? 0;
            _terrain.Draw(_vertexShader, _pixelShader, viewProjection, view, frustum,
                lighting.LightDirection, lightColor, fogColor, fog);
            _foliage?.Draw(_vertexShader, _pixelShader, viewProjection, view, _terrain.Content,
                lightColor, fogColor, fog, frustum);

            EndFrameReport(terrainBefore, viewProjection, frustum, fog);
            LogFrameReport();
        }

        /// <summary>
        /// Draws the sky dome, with the world state it leaves behind put back.
        /// </summary>
        /// <remarks>
        /// <c>AbstractSkyDome.Draw</c> is written for a caller that owns the whole frame: it sets
        /// <c>DepthStencilState.None</c>, <c>RasterizerState.CullNone</c>,
        /// <c>BlendState.AlphaBlend</c> and <c>SamplerStates[0] = LinearWrap</c>, and it restores
        /// only the depth state and the blend state before it returns
        /// (<c>AbstractSkyDome.cs:279-335</c>). Everything after it in this frame draws alpha-blended
        /// geometry through city effects of its own, so the depth and blend states are put back
        /// explicitly here rather than relied upon - and the rasterizer and sampler, which the dome
        /// leaves behind, with them.
        /// </remarks>
        private void DrawSky(GraphicsDevice device, NeighbourhoodCityLighting lighting,
            Matrix view, Matrix projection)
        {
            if (_skyDome == null || _weather == null) return;

            var blend = device.BlendState;
            var depth = device.DepthStencilState;
            var rast = device.RasterizerState;
            var sampler = device.SamplerStates[0];
            try
            {
                _skyDome.Draw(device, lighting.TintColour, view, projection, (float)lighting.TimeOfDay,
                    _weather, lighting.SunVector, 1f);
            }
            finally
            {
                device.BlendState = blend;
                device.DepthStencilState = depth;
                device.RasterizerState = rast;
                device.SamplerStates[0] = sampler;
            }
        }

        public override void PreDraw(GraphicsDevice device)
        {
            base.PreDraw(device);
            //the engine clears colour once per frame but not depth, and this layer is the first
            //thing to draw into it, so the depth buffer is cleared here rather than left over
            //from whatever drew last
            _depthClearedThisFrame = false;
        }

        /// <summary>
        /// What the frame is cleared to: the dome's own horizon, or the tint when there is no dome
        /// to have a horizon.
        /// </summary>
        private Color HorizonColour()
        {
            var horizon = NeighbourhoodSkyStrip.HorizonColourFor(TimeOfDay);
            if (horizon.HasValue) return horizon.Value;
            return _lighting.TintColour;
        }

        // -------------------------------------------------------------- the render signal
        //
        // The scene now publishes its own numbers, so the panel and the proxy both stop deriving
        // them. Two of the things they used to guess at are the ones this bug lived in:
        // "uploaded" is now the mesh's own answer rather than "the device holds a triangle", and
        // the promotion gate reads SubmittedFrames, which only a scene that has actually drawn can
        // raise. See INeighbourhoodRenderSignal.

        /// <inheritdoc />
        public bool MeshUploaded
        {
            get { return _terrain != null && _terrain.MeshReady; }
        }

        /// <inheritdoc />
        public int TerrainTriangles
        {
            get { return _terrain == null ? 0 : _terrain.TotalPrimitives; }
        }

        /// <inheritdoc />
        public int RoadTriangles
        {
            get { return _terrain == null ? 0 : _terrain.RoadPrimitives; }
        }

        /// <inheritdoc />
        public int SubmittedTriangles
        {
            get { return _report.Submitted; }
        }

        /// <inheritdoc />
        public int SubmittedFrames
        {
            get { return _report.ProvenFrames; }
        }

        /// <inheritdoc />
        public int DrawFailures => 0;

        /// <inheritdoc />
        public bool DeviceLost => false;

        /// <inheritdoc />
        public string LastDrawFailure => _failure;

        public override void DeviceReset(GraphicsDevice device)
        {
            base.DeviceReset(device);
            _device = device;
            //The terrain's textures belong to the old device; rebuilding them is enough for it
            //because the mesh is still valid and the next BuildStep-free frame can re-upload from
            //it. In practice a device reset mid-neighbourhood is a minimise, and the scene is
            //rebuilt when the map is opened again.
            _terrain?.SetContent(new NeighbourhoodContent(device));

            //The sky dome and the trees are different: both hold buffers the device owned and
            //neither has a CPU-side copy the draw can fall back on. AbstractSkyDome builds its
            //gradient texture and its geometry against the device in its constructor
            //(:37-50, :218-225) and has no reload; NeighbourhoodFoliage's chunk buffers are
            //uploaded per chunk and its obj geometry is already reduced to plain arrays, so a
            //rebuild is the same bounded walk again. Both are rebuilt here rather than left dead,
            //because a device reset is a minimised window and the neighbourhood screen is exactly
            //the one a player minimises from.
            _skyDome?.Dispose();
            _skyDome = null;
            BuildSky(device);
            if (_foliage != null)
            {
                _foliage.Rebuild();
                SimitoneLog.Info("The generated neighbourhood's trees after a device reset: "
                    + _foliage.Describe());
            }
        }

        public override void Dispose()
        {
            _terrain?.Dispose();
            _terrain = null;
            _foliage?.Dispose();
            _foliage = null;
            _facades?.Dispose();
            _facades = null;
            _skyDome?.Dispose();
            _skyDome = null;
            base.Dispose();
        }
    }

    /// <summary>
    /// The generated neighbourhood's light, fog and sun, as a function of one number.
    ///
    /// <para><b>This is TSO's, not a look.</b> It is
    /// <c>Terrain.SetTimeOfDay(double)</c> (<c>Terrain.cs:1273-1327</c>) and the shader setup in
    /// <c>Terrain.Draw</c> (<c>:1481-1519</c>) lifted out of the scene and made pure, so that every
    /// number the neighbourhood is lit by is one expression away from the number the city is lit by.
    /// The transformations are reproduced rather than re-derived - the four-matrix chain at
    /// <c>:1310-1313</c>, the <c>modTime</c> piecewise map at <c>:1296-1306</c>, the shadow ramp at
    /// <c>:1317-1326</c>, the <c>LightCol</c> scale at <c>:1489</c>, the sun vector's axis swap at
    /// <c>:1552-1556</c>, and the fog derivation from
    /// <c>WeatherController.Update</c> (<c>WeatherController.cs:82-87</c>). A different spline
    /// through the same inputs is a different time of day.</para>
    ///
    /// <para><b>Pure, and that is the point.</b> No device, no textures, no clock, no state between
    /// calls - so the sun really does rise in the east at <c>tod = 0.25</c> and the fog really does
    /// thin out as the camera comes in, and both of those are things a test can say rather than
    /// something a screenshot has to.</para>
    ///
    /// <para><b>The one thing that is not TSO's.</b> TSO's <c>SetTimeOfDay</c> takes its tint
    /// straight from <c>TimeOfDayConfig</c> and its fog from a <see cref="WeatherController"/> the
    /// city screen owns. This is the same arithmetic with the weather's contribution at zero and the
    /// zoom curve driven by the neighbourhood's own camera width, because a map screen has no
    /// weather and its zoom is not a <c>CityCamera3D</c>'s.</para>
    /// </summary>
    public readonly struct NeighbourhoodCityLighting
    {
        // ---- TSO's constants, by name -------------------------------------------------------------
        // Terrain.cs:149-150, the day's start offset and its length as fractions of the day. The
        // day is [DayOffset, DayOffset + DayDuration] and everything outside it is night; there is
        // no second sunset constant, the one end is the start of the next day.

        /// <summary>
        /// When the day starts, as a fraction of the day. <c>Terrain.cs:149</c>.
        /// </summary>
        public const double DayOffset = 0.25;

        /// <summary>
        /// How long the day is, as a fraction of the day. <c>Terrain.cs:150</c>.
        /// </summary>
        public const double DayDuration = 0.60;

        /// <summary>
        /// The middle of the map, in tile coordinates: the pivot the sun's arc turns about.
        /// <c>Terrain.cs:1313</c> writes <c>(256, 0, 256)</c> for the city's 512x512 mesh, which is
        /// the same point for ours - <see cref="NeighbourhoodFoliage.MapSize"/> is 512 and the mesh
        /// runs 0..511 in x and z.
        /// </summary>
        public static readonly Vector3 MapCentre = new Vector3(256f, 0f, 256f);

        /// <summary>
        /// How long the sun's arm is, in tiles. <c>Terrain.cs:1292</c>'s
        /// <c>new Vector3(0, 0, -263)</c>.
        /// </summary>
        public const float LightArmLength = 263f;

        /// <summary>
        /// The multiplier on the tint that becomes <c>LightCol</c>. <c>Terrain.cs:1489</c>.
        /// </summary>
        public const float LightColourStrength = 1.25f;

        /// <summary>
        /// The fog distance a clear sky gets, in tiles: <c>300f * 75f</c> from
        /// <c>WeatherController.cs:87</c>, which is 300 lots at seventy-five units to the lot.
        /// </summary>
        public const float ClearFogDistance = 300f * 75f;

        /// <summary>
        /// The fog distance heavy rain gets, in tiles: <c>15f * 75f</c> from the same line. Not used
        /// here - there is no weather on this screen - and named because the pair is one line of the
        /// reference and a reader who has only seen half of it will assume the number is made up.
        /// </summary>
        public const float ObscuredFogDistance = 15f * 75f;

        /// <summary>
        /// The 3D camera's fog multiplier at the far view, from <c>(Zoom3D + 1) / 21</c> with
        /// <c>Zoom3D = 0</c> (<c>CityCamera3D.cs:91-100</c>).
        /// </summary>
        private const float FarFogTerm = 1f / 21f;

        // ---- the values ---------------------------------------------------------------------------

        /// <summary>The time of day these are for, as a fraction of the day.</summary>
        public double TimeOfDay { get; }

        /// <summary>
        /// The scene's tint: <c>TimeOfDayConfig.ColorFromTime</c>
        /// (<c>Terrain.cs:1276</c>), which is what the city clears the frame to as well as what
        /// everything is multiplied by.
        /// </summary>
        public Color TintColour { get; }

        /// <summary>
        /// <c>LightCol</c> for the pixel shader: the tint's channels over 255, times
        /// <see cref="LightColourStrength"/>. <c>Terrain.cs:1489</c>.
        /// </summary>
        public Vector4 LightColour { get; }

        /// <summary>
        /// <c>LightVec</c> for the pixel shader: the unit vector from the middle of the map towards
        /// the sun. <c>Terrain.cs:1490</c>.
        /// </summary>
        public Vector3 LightDirection { get; }

        /// <summary>
        /// Where the sun (or the moon) is, for the sky dome's billboard. <c>Terrain.cs:1552-1556</c>:
        /// the light direction stretched by 1.5 in y - which is what puts the disc higher in the
        /// sky than the light that shades the ground - with x and z exchanged.
        /// </summary>
        public Vector3 SunVector { get; }

        /// <summary>
        /// <c>ShadowMult</c> for the pixel shader: 0.25 for most of the day, rising to 1 in the
        /// half-hour either side of the horizon so that moonlight takes over from sunlight without a
        /// step. <c>Terrain.cs:1317-1326</c>.
        /// </summary>
        public float ShadowStrength { get; }

        /// <summary>
        /// Whether the dome should draw the moon rather than the sun, which is the same test
        /// <c>AbstractSkyDome.Draw</c> makes (<c>AbstractSkyDome.cs:109-121</c>): outside the day.
        /// </summary>
        public bool IsNight { get; }

        /// <summary>
        /// The texture coordinate along the sky strip for this time of day - the same value the dome
        /// computes as <c>AbstractSkyDome.OutsideSkyP</c> (<c>AbstractSkyDome.cs:92-104</c>).
        /// </summary>
        /// <remarks>
        /// Exposed because the frame clear samples the strip at exactly this coordinate, and a
        /// sample at a slightly different one is a different colour - the strip's columns are eight
        /// across a whole day.
        /// </remarks>
        public float SkyStripU { get; }

        /// <summary>
        /// <c>FogColor</c> for the pixel shader: <c>FogColourFor</c> at the tint, with the alpha
        /// forced to 1 as <c>Terrain.cs:1517</c> does before handing it over.
        /// </summary>
        public Vector4 FogColour
        {
            get { return FogColourFor(TintColour); }
        }

        private NeighbourhoodCityLighting(double timeOfDay, Color tint, Vector4 lightColour,
            Vector3 lightDirection, Vector3 sunVector, float shadowStrength, bool isNight, float skyStripU)
        {
            TimeOfDay = timeOfDay;
            TintColour = tint;
            LightColour = lightColour;
            LightDirection = lightDirection;
            SunVector = sunVector;
            ShadowStrength = shadowStrength;
            IsNight = isNight;
            SkyStripU = skyStripU;
        }

        /// <summary>
        /// The lighting for a time of day, clamped into 0..1 first.
        /// </summary>
        /// <param name="timeOfDay">A fraction of the day; 0 is midnight and 0.5 is noon.</param>
        public static NeighbourhoodCityLighting At(double timeOfDay)
        {
            var time = timeOfDay;
            if (double.IsNaN(time)) time = 0.0;
            if (time < 0.0) time = 0.0;
            //Terrain clamps the top to just under one (Terrain.cs:1275) because
            //TimeOfDayConfig indexes its fourteen-stop table with (int)(time * 13) and reads one
            //past that, so 1.0 itself would read off the end.
            if (time > 0.999999999) time = 0.999999999;

            var tint = TimeOfDayConfig.ColorFromTime(time);
            var modTime = FinaleUtils.BiasSunTime(ModTimeFor(time));
            var position = LightPositionFor(modTime);
            var direction = DirectionTowards(position);
            return new NeighbourhoodCityLighting(
                time,
                tint,
                new Vector4(tint.R / 255f, tint.G / 255f, tint.B / 255f, 1f) * LightColourStrength,
                direction,
                SunVectorFor(direction),
                ShadowStrengthFor(modTime),
                time < DayOffset || time > DayOffset + DayDuration,
                SkyStripUFor(time));
        }

        /// <summary>
        /// The sun's position on its arc: the fraction of the day mapped onto 0..0.5 across the day
        /// and onto the same range backwards across the night. <c>Terrain.cs:1296-1306</c>.
        /// </summary>
        /// <param name="time">A fraction of the day.</param>
        public static double ModTimeFor(double time)
        {
            var offStart = 1.0 - (DayOffset + DayDuration);
            if (time < DayOffset) return (offStart + time) * 0.5 / (1.0 - DayDuration);
            if (time > DayOffset + DayDuration) return (time - (1.0 - offStart)) * 0.5 / (1.0 - DayDuration);
            return (time - DayOffset) * 0.5 / DayDuration;
        }

        /// <summary>
        /// Where the sun's arm points for a <see cref="ModTimeFor"/> value: the four rotations of
        /// <c>Terrain.cs:1310-1313</c>, about the middle of the map.
        /// </summary>
        /// <param name="modTime">The sun's own day phase, 0 to 0.5.</param>
        public static Vector3 LightPositionFor(double modTime)
        {
            var transform = Matrix.Identity;
            //Controls the rotation of the sun/moon around the city.
            transform *= Matrix.CreateRotationY((float)((modTime + 0.5) * Math.PI * 2.0));
            //Sun is at an angle of 45 degrees to horizon at it's peak.
            transform *= Matrix.CreateRotationZ((float)(Math.PI * (45.0 / 180.0)));
            //Offset from front-back a little.
            transform *= Matrix.CreateRotationY((float)(Math.PI * 0.3));
            //Move pivot center to center of mesh.
            transform *= Matrix.CreateTranslation(MapCentre);
            return Vector3.Transform(new Vector3(0f, 0f, -LightArmLength), transform);
        }

        /// <summary>
        /// The unit vector from the middle of the map towards the sun.
        /// <c>Terrain.cs:1490</c>.
        /// </summary>
        /// <param name="lightPosition">Where the sun's arm points.</param>
        public static Vector3 DirectionTowards(Vector3 lightPosition)
        {
            var offset = lightPosition - MapCentre;
            //The four rotations preserve the arm's length, so this can only degenerate if the
            //caller hands in something that is not a rotation of the arm - and a NaN into a shader
            //is a black screen rather than an exception.
            if (offset.LengthSquared() < 1e-8f || float.IsNaN(offset.X)) return Vector3.UnitY;
            return Vector3.Normalize(offset);
        }

        /// <summary>
        /// The sky dome's sun vector: the light direction stretched by 1.5 in y with x and z
        /// exchanged. <c>Terrain.cs:1552-1556</c>.
        /// </summary>
        /// <param name="lightDirection">The unit vector towards the sun.</param>
        public static Vector3 SunVectorFor(Vector3 lightDirection)
        {
            var dir = lightDirection * new Vector3(1f, 1.5f, 1f);
            var swap = dir.X;
            dir.X = -dir.Z;
            dir.Z = swap;
            if (dir.LengthSquared() < 1e-8f || float.IsNaN(dir.X)) return Vector3.UnitY;
            return Vector3.Normalize(dir);
        }

        /// <summary>
        /// The shadow strength, which crosses over between the sun and the moon at the horizon.
        /// <c>Terrain.cs:1317-1326</c>.
        /// </summary>
        /// <param name="modTime">The sun's own day phase.</param>
        public static float ShadowStrengthFor(double modTime)
        {
            if (Math.Abs(modTime) >= 0.05) return 0.25f;
            return (float)(1.0 - (Math.Abs(modTime) * 20.0)) * 0.75f + 0.25f;
        }

        /// <summary>
        /// The fog colour the city derives from its outside colour, with no weather on it.
        /// <c>WeatherController.cs:82-87</c>.
        /// </summary>
        /// <remarks>
        /// The whole of the city's derivation, and it is not a tint of the outside colour: it goes
        /// to sRGB, subtracts <c>0.35 * 1.5</c>, adds <c>0.35</c> back - which is a contrast pull
        /// around mid grey - and comes back, then multiplies by the sky's own half-blue
        /// <c>0x80C0FF</c>. The two sRGB round trips cancel for the luminance and leave the hue,
        /// which is why the far edge of a city at noon is pale blue rather than white and at dusk is
        /// orange rather than brown.
        /// <para>
        /// <see cref="WeatherIntensity"/> is fixed at zero rather than taken from a weather
        /// controller: there is no weather on this screen, and at zero the two ends of the blend
        /// collapse to this one.
        /// </para>
        /// <para>
        /// <b>The one place this is not the city's code, and why.</b> The contrast pull can push a
        /// very dark channel below zero - anything under about five units of 255 does - and
        /// <c>Math.Pow</c> of a negative number at a fractional exponent is NaN, which is a fog
        /// colour of NaN and a black screen. The city's darkest tint is (32, 48, 107) so it never
        /// gets there; a caller passing an arbitrary outside colour would. The argument is floored
        /// at zero, which for every colour the model actually produces is the same number.
        /// </para>
        /// </remarks>
        /// <param name="outsideColour">The scene's tint, or any outside colour.</param>
        public static Vector4 FogColourFor(Color outsideColour)
        {
            var ocolor = outsideColour.ToVector4();
            var colour = Linear(Floor(Srgb(ocolor) - new Vector4(0.35f) * 1.5f + new Vector4(0.35f)));
            colour.W = 1f;
            const float wint = 0f;
            var fog = colour * new Color(0x80, 0xC0, 0xFF, 0xFF).ToVector4() * (1f - wint * 0.75f)
                + Srgb(ocolor) * (wint * 0.75f);
            fog.W = 1f;
            return fog;
        }

        /// <summary>
        /// The 3D camera's fog multiplier for a width in frame.
        /// <c>CityCamera3D.cs:91-100</c>: <c>1 - (1 - (Zoom3D + 1) / 21)^(1/3)</c>.
        /// </summary>
        /// <remarks>
        /// <c>Zoom3D</c> is TSO's own 3D zoom, and in <c>CityCamera3D</c> it runs from 0 at the
        /// nearest view to 12 at the far one (<c>CityCamera3D.cs:283-309</c>: the far
        /// <c>TargetZoom</c> of 0.25 drives <c>Zoom3D</c> to 12, the near end to 0). This camera has
        /// a width in frame instead of a zoom scalar, so <c>Zoom3D</c> is derived from it as
        /// <c>20 * (tilesAcross / MapSize)</c>: the whole map in frame is the far view and a
        /// fraction of it is a nearer one. That mapping is this file's, and it is the one invented
        /// part of the model - the curve either side of it is the city's. <b>The direction matters
        /// and used to be reversed here</b>: mapping the whole map to <c>Zoom3D = 0</c> put the
        /// full-screen view on the city's <em>near</em> curve, so the fade completed in ~360 tiles -
        /// shorter than the camera stands from the map's middle - and every visible tile drew as
        /// one sheet of <c>FogColor</c>.
        /// </remarks>
        /// <param name="tilesAcross">How much of the map is in frame, in tiles.</param>
        public static float FogMultiplierFor(float tilesAcross)
        {
            var span = (float)NeighbourhoodCityMeshBuilder.MapSize;
            //a width that is not a number is the far view, which is the reading the shader gets
            //anyway (it has nothing to divide by and clamps to fully fogged)
            float zoom;
            if (float.IsNaN(tilesAcross)) zoom = 1f;
            else if (tilesAcross >= span) zoom = 1f;
            else if (tilesAcross <= 0f) zoom = 0f;
            else zoom = tilesAcross / span;
            //the city's (Zoom3D + 1) / 21 with Zoom3D = 20 * zoom, which reduces to this
            var m = FarFogTerm + (1f - FarFogTerm) * zoom;
            return 1f - (float)Math.Pow(1f - m, 1f / 3f);
        }

        /// <summary>
        /// How far away the fade completes, in tiles: the weather controller's clear-sky distance
        /// times the camera's fog multiplier. <c>Terrain.cs:1516</c>.
        /// </summary>
        /// <param name="tilesAcross">How much of the map is in frame, in tiles.</param>
        public static float FogMaxDistFor(float tilesAcross)
        {
            var distance = ClearFogDistance * FogMultiplierFor(tilesAcross);
            //the shader divides by it, so a zero is a black screen and a negative one is worse
            return distance > 1f ? distance : 1f;
        }

        /// <summary>
        /// Where a time of day sits along the sky strip, as the texture's u coordinate -
        /// <c>AbstractSkyDome.OutsideSkyP</c> (<c>AbstractSkyDome.cs:92-104</c>).
        /// </summary>
        /// <remarks>
        /// The period is <c>SkyStops.Length - 1</c> and not <c>SkyStops.Length</c>, because the
        /// strip's last stop and its first are the same column
        /// (<c>SunLightingModel.cs:559-575</c>). Written as the dome writes it rather than inlined,
        /// so a change to the stop table cannot move the neighbourhood's clear colour and not the
        /// dome's.
        /// </remarks>
        /// <param name="timeOfDay">A fraction of the day.</param>
        public static float SkyStripUFor(double timeOfDay)
        {
            var period = SunLightingModel.SkyStops.Length - 1;
            return SunLightingModel.StripValue((float)timeOfDay * period) + 1f / 16f;
        }

        // WeatherController's two conversions, :55-70. Both round-trip to the identity, so the pair
        // in FogColourFor is about contrast and hue rather than about gamma.
        private static Vector4 Srgb(Vector4 col)
        {
            return new Vector4(
                (float)Math.Pow(col.X, 1 / 2.2f),
                (float)Math.Pow(col.Y, 1 / 2.2f),
                (float)Math.Pow(col.Z, 1 / 2.2f),
                col.W);
        }

        /// <summary>Per channel, clamped at zero - see the remark on <see cref="FogColourFor"/>.</summary>
        private static Vector4 Floor(Vector4 col)
        {
            return new Vector4(
                Math.Max(0f, col.X),
                Math.Max(0f, col.Y),
                Math.Max(0f, col.Z),
                col.W);
        }

        private static Vector4 Linear(Vector4 col)
        {
            return new Vector4(
                (float)Math.Pow(col.X, 2.2f),
                (float)Math.Pow(col.Y, 2.2f),
                (float)Math.Pow(col.Z, 2.2f),
                col.W);
        }

        public override string ToString()
        {
            return "t=" + TimeOfDay.ToString("0.000")
                + " tint " + TintColour
                + (IsNight ? " (night)" : string.Empty)
                + " sun (" + LightDirection.X.ToString("0.00") + ", "
                + LightDirection.Y.ToString("0.00") + ", " + LightDirection.Z.ToString("0.00") + ")";
        }
    }

    /// <summary>
    /// The sky strip - <c>Content/Textures/skycol.png</c>, the fourteen-stop gradient the sky dome
    /// is drawn from - read as pixels rather than as a texture, so that the colour the frame is
    /// cleared to is the dome's own.
    ///
    /// <para><b>Why read it at all.</b> The dome covers the whole background, so the clear colour is
    /// only ever visible where it does not: on the first frame, on a dome that failed to build, and -
    /// mostly - in the pixels either side of the isometric diamond's clipped edge. The old clear was
    /// a flat fog colour, which is why the neighbourhood read as a void with houses in it. The
    /// horizon row of the strip is what is actually one row away from where the dome's own colours
    /// meet, so clearing to it costs one pixel fetch and makes the seam invisible.</para>
    ///
    /// <para><b>Why not ask the dome.</b> <c>AbstractSkyDome.GradTex</c> is private and there is no
    /// accessor. The alternative - rebuilding the arithmetic from nothing and hoping it agrees - is
    /// worse than reading the same eight-by-one-hundred-and-twenty-eight pixels.</para>
    ///
    /// <para><b>The path is the default the dome falls back to.</b> <c>AbstractSkyDome</c> will
    /// prefer <c>Textures/skycol_alt{n}.png</c> when the server's tuning says so and
    /// <c>skycolfinal.png</c> during the finale (<c>AbstractSkyDome.cs:37-46</c>). This reads
    /// <c>skycol.png</c> and so can be a shade out on a tuned server; the difference is confined to
    /// pixels the dome does not cover, and reading the alternatives would mean duplicating the
    /// dome's own selection to keep up with it.</para>
    ///
    /// <para><b>Once per process, and never throws.</b> A missing strip is a packaging problem: it
    /// is said once, every sample returns nothing, and the scene falls back to its tint.</para>
    /// </summary>
    public static class NeighbourhoodSkyStrip
    {
        /// <summary>
        /// The strip's path under <c>FSOEnvironment.ContentDir</c>, which is
        /// <c>AbstractSkyDome</c>'s own default (<c>AbstractSkyDome.cs:17</c>).
        /// </summary>
        public const string RelativePath = "Textures/skycol.png";

        /// <summary>
        /// Which row of the strip is the horizon: <c>0.9</c>, from
        /// <c>AbstractSkyDome.BuildSkyDomeForStrip</c>'s <c>tpos</c> at a ring of height zero
        /// (<c>AbstractSkyDome.cs:193</c>). Above it the dome runs to 0 at the zenith and below it
        /// the last tenth of the texture is the underside of the hemisphere, which no ring reaches.
        /// </summary>
        public const float HorizonRow = 0.9f;

        private static readonly object Gate = new object();
        private static byte[] _pixels;
        private static int _width;
        private static int _height;
        private static bool _attempted;

        /// <summary>Whether the strip was found and read.</summary>
        public static bool Available
        {
            get { lock (Gate) { return _pixels != null; } }
        }

        /// <summary>The strip's width in pixels, or 0.</summary>
        public static int Width
        {
            get { lock (Gate) { return _width; } }
        }

        /// <summary>The strip's height in pixels, or 0.</summary>
        public static int Height
        {
            get { lock (Gate) { return _height; } }
        }

        /// <summary>
        /// The strip's full path, which is where the log should point when it is missing.
        /// </summary>
        public static string Path
        {
            get { return System.IO.Path.Combine(FSOEnvironment.ContentDir, RelativePath); }
        }

        /// <summary>
        /// Reads the strip if it has not been read already. Safe to call every frame.
        /// </summary>
        /// <returns>Whether the strip is available.</returns>
        public static bool Load()
        {
            lock (Gate)
            {
                if (_attempted) return _pixels != null;
                _attempted = true;

                var path = Path;
                try
                {
                    Tuple<byte[], int, int> decoded;
                    using (var file = File.OpenRead(path))
                    {
                        decoded = BitmapDecoder.Read(file);
                    }
                    if (decoded != null && decoded.Item2 > 0 && decoded.Item3 > 0)
                    {
                        _pixels = decoded.Item1;
                        _width = decoded.Item2;
                        _height = decoded.Item3;
                    }
                }
                catch (Exception ex)
                {
                    //Named once, and not thrown: the strip is a background, and a neighbourhood
                    //without one is a flat-coloured one rather than a broken one.
                    SimitoneLog.Warn("Could not read the sky strip " + path + " (" + ex.Message
                        + "), so the generated neighbourhood is cleared to a flat colour.");
                }

                if (_pixels == null)
                {
                    SimitoneLog.Warn("The sky strip " + path + " could not be decoded, so the"
                        + " generated neighbourhood is cleared to a flat colour.");
                }
                return _pixels != null;
            }
        }

        /// <summary>
        /// One pixel of the strip, or <c>null</c> when the strip is not available.
        /// </summary>
        /// <param name="u">Across the strip, 0 to 1; wraps.</param>
        /// <param name="v">Down the strip, 0 at the zenith and 1 at the underside.</param>
        public static Color? At(float u, float v)
        {
            lock (Gate)
            {
                if (_pixels == null || _width <= 0 || _height <= 0) return null;
                var x = Wrap((int)(u * _width), _width);
                var y = (int)(v * _height);
                if (y < 0) y = 0;
                if (y > _height - 1) y = _height - 1;
                var at = (y * _width + x) * 4;
                if (at + 3 >= _pixels.Length) return null;
                return new Color(_pixels[at], _pixels[at + 1], _pixels[at + 2], (byte)255);
            }
        }

        /// <summary>
        /// The dome's horizon colour at this time of day, or <c>null</c> when the strip is not
        /// available.
        /// </summary>
        /// <param name="timeOfDay">A fraction of the day.</param>
        public static Color? HorizonColourFor(double timeOfDay)
        {
            return At(NeighbourhoodCityLighting.SkyStripUFor(timeOfDay), HorizonRow);
        }

        private static int Wrap(int value, int count)
        {
            var wrapped = value % count;
            return wrapped < 0 ? wrapped + count : wrapped;
        }
    }
}
