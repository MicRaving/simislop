/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using FSO.Client;
using FSO.Common.Rendering.Framework.Camera;
using FSO.Common.Utils;
using FSO.LotView.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The generated neighbourhood, drawn around the lot the player is standing in. §7.4.
    ///
    /// <para><b>What it is.</b> An <see cref="IRCSurroundings"/>, which is the lot view's own
    /// "draw the world behind this lot" hook: <c>WorldStatic.DrawBg</c> calls
    /// <see cref="DrawSurrounding"/> with the lot's own camera and fog just before it clears the
    /// depth buffer and starts the lot itself. Nothing here is drawn at any other time and
    /// nothing here is drawn in the lot's 2D camera mode, because the engine does not call the
    /// hook in 2D mode either - which is why this type has no camera-mode branch and no opinion
    /// about which mode the player is in.
    /// </para>
    ///
    /// <para><b>What it reuses.</b> Its own generated-map mesh: a <see cref="NeighbourhoodCityMesh"/>
    /// built from the same <see cref="NeighbourhoodMap"/> the neighbourhood screen draws and cropped
    /// to the region the surround shows, plus M3's <see cref="NeighbourhoodContent"/> textures and M3's
    /// <see cref="NeighbourhoodFacades"/> cache. This is the bespoke
    /// <c>NeighbourhoodCityMeshBuilder</c>/<c>SimitoneSurroundingsCrop</c> path. The other things this type owns are the placement (<see cref="SimitoneSurroundingsPlacement"/>),
    /// the region of the map the band and the fog are measured from
    /// (<see cref="SimitoneSurroundingsCull"/>) and the decision to be here at all
    /// (<see cref="SimitoneSurroundingsSelection"/>).
    /// </para>
    ///
    /// <para><b>The alignment, in one sentence.</b> The generated map is measured in map tiles, a
    /// lot is 64 lot tiles square, so a lot in the generated map is the same size as the lot the
    /// player is standing in; scaling the map by the lot view's three world units per tile and
    /// moving the active lot's packed corner to the origin puts the neighbourhood's road, the
    /// next lot's driveway and the lot's own front path in the same place. Everything after that
    /// is the active camera and the lot's own fog and light.
    /// </para>
    ///
    /// <para><b>What is drawn, and what is not.</b> The generated neighbourhood around the lot,
    /// built from the same <see cref="NeighbourhoodMap"/> the neighbourhood screen draws and cropped
    /// to <see cref="DrawnRegion"/>, with the lot view's own <c>RestoreRoad</c> band laid over the
    /// active lot's frontage. The player's own rectangle is drawn <em>under</em> the lot rather than
    /// cut out of it, so the two grounds meet instead of the neighbourhood's stopping dead at the
    /// property line, and the fog is pinned to the region's own corner.
    /// <para>
    /// What is <em>not</em> the whole map is the neighbour budget: a real neighbouring sub-world
    /// costs a whole <c>World</c> and a <c>VM</c> and a facade mesh costs seconds, so
    /// <see cref="SimitoneSurroundingsPlacement.Cull"/> is still
    /// <see cref="SimitoneSurroundingsPlacement.CullRadiusTiles"/> tiles past the property and only
    /// the lots inside it are stood up as sub-worlds. Beyond it the ground and streets are the map's
    /// own, and the lot view draws none of the map view's houses, so the houses out there are
    /// facades - the nearest <see cref="SimitoneSurroundingsFacadeFeed.FarFacadeCap"/> of them; any
    /// lot beyond that cap shows as a bare plot.</para>
    /// </para>
    ///
    /// <para><b>The facade queue is the region plus a capped far ring.</b> The cache is handed the
    /// lots the region covers and the nearest few beyond it (<see cref="SimitoneSurroundingsFacadeFeed"/>);
    /// they are built one at a time, nearest first, so the houses in view come up before the far
    /// ones. Every line the pass writes about itself - including the sentence for a house file that
    /// would not load - is handed to the log as it appears rather than being left in a list nobody
    /// reads.
    /// </para>
    ///
    /// <para><b>Depth.</b> The hook runs after the lot has cleared the frame's colour and before
    /// it clears the depth buffer, so the surround is drawn against last frame's depth and then
    /// everything - including the surround - is cleared. That is the engine's arrangement and
    /// TSO's city terrain lives in it, so this does too: it sets the states its own draw needs,
    /// restores them, and lets the lot's depth clear do the rest. The lot is never occluded by
    /// the surround because the surround is gone before the lot is drawn.
    /// </para>
    ///
    /// <para><b>Failure is the lot as it is.</b> Every refusal - no device, no map, a lot that is
    /// not on the generated map, a mesh the GPU would not take, missing city shaders - returns
    /// <c>null</c> with a sentence, and the caller leaves <c>World.Surroundings</c> null. There is
    /// no half-built state a caller has to unwind and no exception to catch.
    /// </para>
    /// </summary>
    public sealed class SimitoneSurroundings : IRCSurroundings, IDisposable
    {
        /// <summary>
        /// The view-space depth squash the city backdrop is drawn with, verbatim from TSO's own
        /// <c>Terrain.DrawSurrounding</c>.
        /// </summary>
        /// <remarks>
        /// It leaves x and y alone and compresses z after the projection, which buys the backdrop
        /// its own depth distribution in a buffer it is about to be cleared out of anyway. Kept
        /// because TSO's city surround looks right with it and there is no way to tell from here
        /// whether it looks right without it.
        /// </remarks>
        public const float BackdropDepthSquash = 0.3f;

        /// <summary>
        /// The fog distance used when there is no drawn region to take one from, in lot-view
        /// world units. A lot that has just come up has a weather with a zero <c>FogColor.W</c>,
        /// and a zero fog distance would put every vertex on the near plane.
        /// </summary>
        public const float DefaultFogDistance = 900f;

        /// <summary>
        /// How much further than its own fog the lot's weather is allowed to push the surround's
        /// fog, as a multiple of the drawn region's corner distance - which is now the far
        /// corner of the whole neighbourhood.
        /// </summary>
        /// <remarks>
        /// Clear weather may see further than the default - that is what clear weather means - but
        /// not so much further that the cull becomes the only thing holding the map back, and never
        /// further than the whole generated neighbourhood, which is the thing the cull exists to
        /// stop drawing.
        /// </remarks>
        public const float MaxFogStretch = 2f;

        private readonly NeighbourhoodMap _map;
        private readonly NeighbourhoodLayout _layout;
        private readonly int _activeLot;
        private readonly SimitoneSurroundingsPlacement _placement;
        private readonly SimitoneSurroundingsCull _cull;
        //where the live lot's own ground is, in blueprint altitude units, and the altitude array it
        //was read from. The lot keeps BaseAlt at zero, so the surround is lifted to meet it. Zero
        //(the surround exactly where it was) until a blueprint with a field has been seen.
        private int _lotGroundAltitude;
        private short[] _lotGroundSource;
        //the rectangle of the shared map the terrain and the streets are drawn for. The whole
        //neighbourhood, so this is not a second world and not a second generation: it is the same
        //raster as _map, and the only thing that changed about it is that the crop no longer throws
        //away the far side of it. See SimitoneSurroundingsPlacement.DrawnRegion.
        private readonly SimitoneSurroundingsCull _drawn;
        //the lots of this neighbourhood whose base tile carries no drawn ground, and which are
        //therefore drawn by neither a facade nor a sub-world. Counted rather than guessed, because
        //"a house is missing and nothing says why" is the report this number is the answer to.
        private int _lotsWithoutGround;
        private readonly List<short> _groundlessLots = new List<short>();

        private SurroundTerrain _terrain;
        private NeighbourhoodFacades _facades;
        //B2 task 5: the plan the bridge stood this lot's neighbours up from. Null unless
        //AttachSubWorlds has been called, which is the capability-off state.
        private SimitoneSurroundingsPlan _subWorlds;
        private Effect _vertexShader;
        private Effect _pixelShader;

        private Vector3 _lightDirection = SimitoneSurroundingsPlacement.LightDirectionFor((Vector3?)null);
        private Vector4 _lightColor = SimitoneSurroundingsPlacement.LightColorFor((Color?)null);
        private string _failure;
        private bool _summaryLogged;
        private bool _facadeDrainLogged;
        private int _facadeLogDrained;
        private int _lastSurroundNumber = -1;
        private bool _disposed;

        /// <summary>
        /// The neighbourhood - and therefore the generated world, the sidecar slot and the seed -
        /// a lot number belongs to. §0 decision 4: one generated world per mode, matching the
        /// per-mode 2D maps, so this is the same table the neighbourhood screen opens a map with
        /// and standing on a lot is looking at the same neighbourhood that map is.
        /// </summary>
        /// <param name="houseNumber">The lot the player is standing on.</param>
        public static int SlotForLot(int houseNumber)
        {
            return Simitone.Client.UI.Services.NeighborhoodService.NeighbourhoodIdForHouse((short)houseNumber);
        }

        /// <summary>Whether there is a surround at all, for a caller that only wants to know.</summary>
        public bool HasSurroundings
        {
            get { return _terrain != null; }
        }

        /// <summary>The lot the player is standing in.</summary>
        public int ActiveLot
        {
            get { return _activeLot; }
        }

        /// <summary>Where the generated map has been put, for a log line.</summary>
        public SimitoneSurroundingsPlacement Placement
        {
            get { return _placement; }
        }

        /// <summary>How much of the shared map is drawn for neighbouring lots, for a log line.</summary>
        public SimitoneSurroundingsCull Cull
        {
            get { return _cull; }
        }

        /// <summary>
        /// How much of the shared map the ground and the streets are drawn for - the whole
        /// connected neighbourhood, which is the same map the neighbourhood view draws.
        /// </summary>
        public SimitoneSurroundingsCull DrawnRegion
        {
            get { return _drawn; }
        }

        /// <summary>
        /// How many of this neighbourhood's lots stand on a tile the mesh emits no ground for, and
        /// so are drawn by neither a facade nor a sub-world.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A lot can only land outside the drawn ground if a hand-written sidecar, a modded lot or
        /// the pixel transform puts it in one of the island's cut corners, and it is a refusal with a
        /// reason rather than a crash: the surround draws, the rest of the neighbourhood draws, and
        /// the lot in the corner is simply not on the map. Before this it was drawn anyway, which is
        /// §9.1's "disconnected houses floating in a void, without lighting".
        /// </para>
        /// <para>
        /// Read from the same list <see cref="LotsWithoutGroundNumbers"/> hands back, so the count
        /// and the numbers cannot come from two walks of the layout.
        /// </para>
        /// </remarks>
        public int LotsWithoutGround
        {
            get { return _lotsWithoutGround; }
        }

        /// <summary>The lot numbers of <see cref="LotsWithoutGround"/>, ascending.</summary>
        public IReadOnlyList<short> LotsWithoutGroundNumbers
        {
            get { return _groundlessLots; }
        }

        /// <summary>Why there is no surround, or <c>null</c> when there is one.</summary>
        public string Failure
        {
            get { return _failure; }
        }

        /// <summary>
        /// How many triangles the surround's own generated-map mesh submits per frame: the cropped
        /// ground, the map's own connectors and the lot-view road band, all in the surround's own
        /// buffers. This is the surround's whole terrain draw - it does not borrow the map view's.
        /// </summary>
        public int TerrainPrimitives
        {
            get { return _terrain == null ? 0 : _terrain.DrawnPrimitives; }
        }

        /// <summary>
        /// The whole generated map's triangle count, for a log line: what the surround's mesh holds
        /// before the region crop keeps a subset of it.
        /// </summary>
        public int FullMapPrimitives
        {
            get { return _terrain == null ? 0 : _terrain.SourcePrimitives; }
        }

        /// <summary>
        /// Whether the surround draws the map's own connectors as well as the lot-view band. Always
        /// true now that the surround builds its own mesh from the generated map; kept because it is
        /// a useful thing to say in the log and a test asks it.
        /// </summary>
        public bool DrawsMapConnectors
        {
            get { return _terrain != null && _terrain.MapRoadPrimitives > 0; }
        }

        /// <summary>
        /// The plan this surround's neighbours were built as sub-worlds from, or <c>null</c> when
        /// there are none. Read by the facade cache through <see cref="AttachSubWorlds"/>, and
        /// exposed so a caller can say what is decided without keeping its own reference.
        /// </summary>
        public SimitoneSurroundingsPlan SubWorldPlan
        {
            get { return _subWorlds; }
        }

        /// <summary>How many neighbouring lots are drawn as real sub-worlds, not facades.</summary>
        public int SubWorldCount
        {
            get { return _facades?.WithheldCount ?? 0; }
        }

        /// <summary>
        /// How many lots inside the neighbour region are facades rather than real lots, and why
        /// there are any: <c>0</c> means the region is fully real.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The count a player means when they say "some of the houses round here aren't real". It
        /// is <b>not</b> the deferred count alone: a lot can also be flat because the plan was
        /// refused, because the restore never ran, or because the plan's radius was narrower than
        /// this surround's own. Asking this count is how all four are told apart from the outside.
        /// </para>
        /// <para>
        /// It is computed from the same decision the facade cache acts on
        /// (<see cref="NeighbourhoodRenderDecision"/>), not from the cache's own state, so it
        /// answers the question before the queue has drained rather than after.
        /// </para>
        /// </remarks>
        public int FacadeFallbackCount
        {
            get
            {
                if (_facades == null) return 0;
                return NeighbourhoodRenderDecision.FacadeFallbackCount(
                    _facades.Layout, _activeLot, _subWorlds, _cull);
            }
        }

        /// <summary>
        /// Whether every lot inside the neighbour region is a real sub-world, so nothing close
        /// enough to stand in is a facade. The answer the coverage change exists to be true.
        /// </summary>
        public bool IsRegionFullyReal
        {
            get { return FacadeFallbackCount == 0; }
        }

        /// <summary>How many neighbouring houses have a facade, for a log line.</summary>
        public int FacadeCount
        {
            get { return _facades?.CachedCount ?? 0; }
        }

        /// <summary>How many neighbouring houses are still waiting for a facade.</summary>
        public int OutstandingFacades
        {
            get { return _facades == null ? 0 : _facades.Outstanding(_activeLot, Focus); }
        }

        /// <summary>Everything the facade pass has said about itself, for a log line.</summary>
        public IReadOnlyList<string> FacadeLog
        {
            get { return _facades?.Log ?? (IReadOnlyList<string>)new string[0]; }
        }

        /// <summary>Everything the content loader could not load, for a log line.</summary>
        public IReadOnlyList<string> ContentWarnings
        {
            get { return _terrain?.Content?.Warnings ?? (IReadOnlyList<string>)new string[0]; }
        }

        /// <summary>
        /// The last value the lot view passed as <c>surroundNumber</c>: 1 while the player is
        /// standing in a sub-world, 0 otherwise. Recorded because it is the only part of the
        /// engine's call this type has no use for, and an unexplained argument is a question
        /// somebody will ask.
        /// </summary>
        public int LastSurroundNumber
        {
            get { return _lastSurroundNumber; }
        }

        /// <summary>
        /// The light the surround is drawn with: a unit vector pointing towards the sun and the
        /// light's colour and strength. Set from the lot's own lighting by the screen; until it
        /// is, the fixed afternoon sun the neighbourhood view uses, so the first frames of a lot
        /// are lit like the map rather than like nothing.
        /// </summary>
        public Vector3 LightDirection
        {
            get { return _lightDirection; }
        }

        /// <summary>The light colour and strength. See <see cref="LightDirection"/>.</summary>
        public Vector4 LightColor
        {
            get { return _lightColor; }
        }

        /// <summary>
        /// Builds the surround, or returns <c>null</c> with the reason. Never throws: this runs
        /// while a lot is being brought up, and a surround that will not build is a reason in the
        /// log, not a failed lot load.
        /// </summary>
        /// <param name="device">The graphics device, or <c>null</c>.</param>
        /// <param name="source">The generated world to draw, or <c>null</c>.</param>
        /// <param name="lotNumber">The lot the player is standing in.</param>
        /// <param name="reason">Why there is no surround, when there is not one.</param>
        public static SimitoneSurroundings TryCreate(
            GraphicsDevice device, INeighbourhoodSceneSource source, int lotNumber, out string reason)
        {
            if (device == null)
            {
                reason = "there is no graphics device";
                return null;
            }
            if (source == null)
            {
                reason = "there is no generated neighbourhood";
                return null;
            }
            if (source.Map == null)
            {
                //the Scene Source's own sentence when it has one. It is asked for through a cast
                //rather than added to INeighbourhoodSceneSource because "why did generation fail"
                //is a question about a generation attempt, and the interface is deliberately the
                //four questions a renderer asks about a world that exists.
                var generated = source as NeighbourhoodSceneSource;
                reason = "the generated neighbourhood has no map"
                    + (string.IsNullOrEmpty(generated?.Failure) ? string.Empty : ": " + generated.Failure);
                return null;
            }

            SimitoneSurroundingsPlacement placement;
            string placementReason;
            if (!SimitoneSurroundingsPlacement.TryFor(
                source.Layout, source.Map, lotNumber, out placement, out placementReason))
            {
                reason = placementReason;
                return null;
            }
            if (!placement.Cull.IsValid)
            {
                reason = "lot " + lotNumber + " has no map region around it to draw";
                return null;
            }

            SimitoneSurroundings surroundings = null;
            try
            {
                surroundings = new SimitoneSurroundings(device, source, lotNumber, placement);
                if (surroundings._terrain == null)
                {
                    //half built, and it now owns device resources: take them with it rather than
                    //leaving them for the garbage collector to find on a graphics device
                    var failure = surroundings._failure ?? "the surrounding terrain could not be prepared";
                    surroundings.Dispose();
                    reason = failure;
                    return null;
                }
                reason = null;
                return surroundings;
            }
            catch (Exception ex)
            {
                surroundings?.Dispose();
                reason = ex.Message;
                return null;
            }
        }

        private SimitoneSurroundings(
            GraphicsDevice device, INeighbourhoodSceneSource source, int lotNumber,
            SimitoneSurroundingsPlacement placement)
        {
            _map = source.Map;
            _layout = source.Layout;
            _activeLot = lotNumber;
            _placement = placement;
            _cull = placement.Cull;
            _drawn = placement.DrawnRegion;
            CountLotsWithoutGround();

            //the two city effects, under the same names FSO.Client.Rendering.City.Terrain loads
            //them. They ship in the output. A missing one leaves the surround drawing nothing
            //rather than throwing on every frame of a live lot, which is what the null check in
            //DrawSurrounding is for.
            try
            {
                _vertexShader = GameFacade.Game?.Content?.Load<Effect>("Effects/VerShader");
                _pixelShader = GameFacade.Game?.Content?.Load<Effect>("Effects/PixShader");
            }
            catch (Exception ex)
            {
                _failure = "the city shaders could not be loaded: " + ex.Message;
            }
            //No shaders means DrawSurrounding returns on every frame and draws nothing, while the
            //lot's log says it is "surrounded". Refuse instead: TryCreate turns a null _terrain into
            //the reason, and the lot is left as it is.
            if (_vertexShader == null || _pixelShader == null)
            {
                if (_failure == null) _failure = "the city shaders are not available";
                return;
            }

            //the surround's own generated-map mesh, built on a worker and uploaded on the game
            //thread, via NeighbourhoodCityMeshBuilder and the crop.
            _terrain = SurroundTerrain.TryCreate(device, _map, _layout, _cull, _drawn, _activeLot, out var terrainReason);
            if (_terrain == null)
            {
                _failure = "the surrounding terrain could not be prepared: " + terrainReason;
                return;
            }

            //the mesh is built on a worker and uploaded on the game thread, so the surround fills
            //in over the first frames of the lot rather than stalling the load
            _terrain.BeginMeshBuild();

            //The facade cache is handed the lots of the neighbour region plus the nearest few
            //beyond it (SimitoneSurroundingsFacadeFeed). The terrain above is drawn for the whole
            //map, and a *house* is a different cost - a mesh stands a World and a VM up on the
            //game thread - so the far lots are capped rather than the whole island. The cache
            //builds nearest first, so the near houses are up before any far one starts.
            _facades = new NeighbourhoodFacades(device, FedLotsLayout(), _map);
        }

        /// <summary>
        /// The layout handed to the facade cache: the neighbour region's lots and the nearest
        /// <see cref="SimitoneSurroundingsFacadeFeed.FarFacadeCap"/> beyond it, without the lot the
        /// player is standing in, which the lot view is already drawing.
        /// </summary>
        /// <remarks>
        /// A layout the neighbourhood itself does not have is fine here: the cache asks a layout which
        /// lots need meshes and where to stand them, and the answer is the same for a subset. See
        /// <see cref="SimitoneSurroundingsCull.LotsInRange"/> for why the player's own lot is left
        /// out - a whole <c>World</c> and a <c>VM</c> stood up to build the house the player is
        /// standing in is a cost with nothing to show for it.
        /// </remarks>
        private NeighbourhoodLayout FedLotsLayout()
        {
            if (_layout == null) return NeighbourhoodLayout.Empty(0, 0, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion);
            return new NeighbourhoodLayout(_layout.Slot, _layout.Seed, _layout.GeneratorVersion,
                _layout.SchemaVersion, _layout.MapSize,
                SimitoneSurroundingsFacadeFeed.LotsToFeed(_layout, _cull, (short)_activeLot));
        }

        /// <summary>
        /// Walks the layout once for the lots that stand on ground the mesh never emits, so the log
        /// can name them instead of the player wondering where a house went.
        /// </summary>
        /// <remarks>
        /// Deliberately the <em>whole</em> layout and not the neighbour cut: a lot past
        /// <see cref="SimitoneSurroundingsPlacement.CullRadiusTiles"/> is a facade or nothing
        /// whatever its ground, so a count taken over the cut would report "no missing houses" for a
        /// neighbourhood that is missing one two hundred tiles away. Cheap - one predicate per lot,
        /// no device and no mesh - and it is the answer to "some of these houses are not here".
        /// </remarks>
        private void CountLotsWithoutGround()
        {
            _groundlessLots.Clear();
            _lotsWithoutGround = 0;
            if (_layout == null) return;
            for (int i = 0; i < _layout.LotCount; i++)
            {
                var lot = _layout.Lots[i];
                if (lot == null) continue;
                if (SimitoneSurroundingsPlacement.GroundIsDrawnUnder(_map, lot)) continue;
                _groundlessLots.Add(lot.Number);
            }
            _lotsWithoutGround = _groundlessLots.Count;
            if (_lotsWithoutGround == 0) return;
            _groundlessLots.Sort();
            SimitoneLog.Warn("Lot " + _activeLot + "'s neighbourhood: " + _lotsWithoutGround
                + " lot(s) stand on ground the map never draws and are shown by neither a house nor"
                + " a lot: " + DescribeNumbers(_groundlessLots) + ".");
        }

        /// <summary>One line naming a list of lot numbers, for a warning.</summary>
        private static string DescribeNumbers(List<short> numbers)
        {
            if (numbers == null || numbers.Count == 0) return "none";
            var sb = new StringBuilder();
            for (int i = 0; i < numbers.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(numbers[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// B2 task 5, the surround's half: tells the facade cache which lots the bridge has stood
        /// up as real sub-worlds, so no facade is built or drawn for them. One decision per lot -
        /// see <see cref="NeighbourhoodRenderDecision"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called with the same <see cref="SimitoneSurroundingsPlan"/> the screen handed to
        /// <c>SimitoneSurroundingsRestore.TryRestore</c>, so both halves of the decision are made
        /// from one object and cannot disagree about which lots are real. A caller with no plan,
        /// or an invalid one, passes <c>null</c> and gets the pre-B2 behaviour exactly: every lot
        /// in the neighbour region is a facade.
        /// </para>
        /// <para>
        /// <b>What is left over is facades only because the budget said so.</b> The plan is built at
        /// the neighbour region's own radius, so every lot inside that region is a candidate, and
        /// <see cref="SimitoneSurroundingsPlan.MaxSubWorlds"/> is the only thing that can turn one
        /// back into a flat mesh. The two cases are told apart in the log - "the whole neighbour
        /// region is real" versus "N lot(s) past the budget" - because "some of these houses aren't
        /// real" is a report a player makes about the street they are standing in, and the answer is
        /// either a count or a layout.
        /// </para>
        /// <para>
        /// <b>Order does not matter.</b> It may be called before the first <see cref="Update"/>,
        /// after it, more than once, or never - the cache reads the set on every build and every
        /// draw rather than acting on it once, so a mesh that was built before the plan landed stops
        /// being submitted as soon as it does.
        /// </para>
        /// </remarks>
        /// <param name="plan">The plan the neighbours were built from, or <c>null</c>.</param>
        /// <returns>How many lots are now drawn as sub-worlds rather than as facades.</returns>
        public int AttachSubWorlds(SimitoneSurroundingsPlan plan)
        {
            if (_disposed) return 0;
            _subWorlds = plan;
            if (_facades == null) return 0;

            //The invariant, checked rather than trusted: every lot the plan builds has to be
            //inside the region the surround draws, or withholding its facade opens a gap in the
            //ground rather than closing a double draw. A plan radius wider than the cull's is the
            //only way that can happen, and it is a number two files own.
            if (!_subWorlds.SubWorldsInside(_cull))
            {
                SimitoneLog.Warn("Lot " + _activeLot + "'s neighbour plan reaches "
                    + _subWorlds.RadiusTiles + " tile(s) past the property but the surround only"
                    + " draws " + _cull.RadiusTiles + ", so those lots would stand on ground that"
                    + " was never drawn. They are being left as facades instead.");
                return 0;
            }

            //And the weaker shape of the same question, said out loud rather than left to be
            //discovered on screen: a plan inside the cut but tighter than it leaves the ring of
            //facades that the shared radius exists to remove. Nothing is withheld for it - the
            //lots the plan does not name are drawn normally - but it is a fact about the street the
            //player is standing in and belongs in the log next to the count.
            var shared = _subWorlds.RadiusSharedWith(_cull);

            var withheld = _facades.UseSubWorlds(_subWorlds);
            if (withheld > 0)
            {
                SimitoneLog.Info("Lot " + _activeLot + ": " + withheld
                    + " neighbouring lot(s) are real sub-worlds and are not drawn as facades:"
                    + " " + _subWorlds.Describe()
                    + (shared
                        ? ". The whole neighbour region is real."
                        : ", but the plan's radius is narrower than the neighbour region, so the"
                          + " lots between the two are still facades."));
            }
            return withheld;
        }

        /// <summary>
        /// The lift currently applied to the surround, in blueprint altitude units. See
        /// <see cref="FollowLotGround"/>.
        /// </summary>
        public int LotGroundAltitude
        {
            get { return _lotGroundAltitude; }
        }

        /// <summary>
        /// Keeps the surround level with the live lot's ground. Cheap to call every frame: the
        /// median is only recomputed when the blueprint's altitude <em>array</em> is a different
        /// object, which is what a lot load, a save reload and a template reset all produce. A
        /// blueprint with no field leaves the previous lift alone.
        /// </summary>
        /// <param name="blueprint">The live lot's blueprint, or <c>null</c>.</param>
        public void FollowLotGround(FSO.LotView.Model.Blueprint blueprint)
        {
            var altitude = blueprint?.Altitude;
            if (altitude == null || altitude.Length == 0) return;
            if (ReferenceEquals(altitude, _lotGroundSource)) return;
            _lotGroundSource = altitude;
            _lotGroundAltitude = SimitoneSurroundingsPlacement.LotGroundAltitudeFor(blueprint);
        }

        /// <summary>
        /// The altitude a lot's own ground has to be measured from for this surround to meet it, or
        /// <see cref="int.MinValue"/> when there is nothing to measure.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Prefer the lot's own field.</b> A blueprint that carries an altitude field knows
        /// exactly where its ground is - that is the array its own terrain component draws from -
        /// so the median of it is the level its middle sits at, and that is
        /// <c>Blueprint.MedianAltitude</c>'s answer. The map's answer
        /// (<see cref="SimitoneSurroundingsPlacement.BaseAltitudeFor"/>) is the fallback, and it is
        /// the surround's own plane restated, so a blueprint with no field still lands on the
        /// surface the surround is drawing.
        /// </para>
        /// <para>
        /// A pure function of its two arguments and no graphics device, so the one number both
        /// halves of the join are measured from is a thing a test can state rather than a comment:
        /// the surround's plane is
        /// <c>MapTileOrigin.Y * LotViewUnitsPerTile / LotTerrainFactor</c> altitude units, and this
        /// is that number to within the generated map's own quantisation.
        /// </para>
        /// </remarks>
        /// <param name="blueprint">The live lot's blueprint, or <c>null</c>.</param>
        /// <param name="placement">The active lot's placement.</param>
        public static int GroundPlaneAltitudeFor(
            FSO.LotView.Model.Blueprint blueprint, SimitoneSurroundingsPlacement placement)
        {
            if (blueprint != null && blueprint.Altitude != null && blueprint.Altitude.Length > 0)
            {
                return FSO.LotView.Model.Blueprint.MedianAltitude(blueprint.Altitude);
            }
            if (placement.Cull.Map == null || placement.Lot == null) return int.MinValue;
            return placement.ActiveBaseAltitude;
        }

        /// <summary>
        /// Hands the surround the lot's own light. Called every frame by the screen, which is the
        /// only thing that can see a <c>World</c>; a null or a degenerate value leaves the
        /// previous light in place rather than blanking the surround.
        /// </summary>
        /// <param name="lotLightVector">The lot's <c>LightVec</c>, or <c>null</c>.</param>
        /// <param name="lotOutsideColor">The lot's <c>OutsideColor</c>, or <c>null</c>.</param>
        public void SetLighting(Vector3? lotLightVector, Color? lotOutsideColor)
        {
            if (lotLightVector.HasValue) _lightDirection = SimitoneSurroundingsPlacement.LightDirectionFor(lotLightVector);
            if (lotOutsideColor.HasValue) _lightColor = SimitoneSurroundingsPlacement.LightColorFor(lotOutsideColor.Value);
        }

        /// <summary>
        /// A one-line summary of what is around the lot, for the log: the rectangle of generated
        /// map that was drawn, where it was put, how many triangles that turned out to be, and how
        /// the neighbouring houses are getting on. Read after the first <see cref="Update"/>:
        /// before the mesh upload lands the triangle count is zero.
        /// </summary>
        public string Describe()
        {
            if (_terrain == null) return "no in-lot surroundings: " + _failure;
            return "in-lot surroundings: " + TerrainPrimitives + " terrain triangle(s)"
                + (FullMapPrimitives > 0 ? " of " + FullMapPrimitives + " in the generated map" : string.Empty)
                + " (the surround's own generated-map mesh)"
                + ", drawing " + _drawn.Describe()
                + ", its streets: " + _terrain.RoadSource
                + ", " + FacadeCount + " neighbouring facades up, " + OutstandingFacades + " still building"
                + (SubWorldCount > 0
                    ? ", " + SubWorldCount + " drawn as real sub-worlds instead"
                    : string.Empty)
                + (_lotsWithoutGround > 0
                    ? ", " + _lotsWithoutGround + " lot(s) of this neighbourhood stand on ground the"
                      + " map never draws and so are not shown at all: "
                      + DescribeNumbers(_groundlessLots)
                    : string.Empty)
                //"the whole drawn region is real" is the sentence this feature is for, and the
                //count that says it is false is the one a player is describing when they say some
                //of these houses are not real. It counts over the neighbour region, not over the
                //ground: a facade past CullRadiusTiles is the same thing the map view draws there.
                + (_subWorlds != null && _subWorlds.IsValid
                    ? FacadeFallbackCount > 0
                        ? ", " + FacadeFallbackCount + " lot(s) in the neighbour region still facades"
                          + (_subWorlds.DeferredCount > 0
                              ? " (" + _subWorlds.DeferredCount + " past the sub-world budget of "
                                + _subWorlds.SubWorldBudget + ")"
                              : string.Empty)
                        : ", the whole neighbour region is real"
                    : string.Empty)
                + ", neighbours from " + _cull.Describe()
                + ", world offset " + _placement.MapTileOrigin
                + " scaled by " + SimitoneSurroundingsPlacement.LotViewUnitsPerTile
                + (_terrain.BuildFailure != null
                    ? " (the terrain mesh failed: " + _terrain.BuildFailure + ")"
                    : string.Empty)
                + (_terrain.IsBuilding ? " (still building the mesh)" : string.Empty)
                + (_terrain.MeshWasCached ? " (whole-map mesh reused from the slot cache)" : string.Empty);
        }

        /// <summary>
        /// Per-frame upkeep: builds whatever neighbouring facades are due, and logs once the
        /// terrain has landed and again once the facade queue has drained.
        /// </summary>
        /// <remarks>
        /// A facade mesh costs a whole <c>FSO.LotView.World</c> and a <c>VM</c> stood up and torn
        /// down, so this is the same budgeted queue the neighbourhood view uses
        /// (<see cref="NeighbourhoodFacades.BuildsPerFrame"/>, nearest the active lot first) and
        /// it is empty once every lot in range has been tried - including the ones that failed,
        /// which are recorded with their hash so a bad house file is not retried fifty times a
        /// second. Standing on a lot is a worse place to hitch than looking at a map, but it is
        /// the same work and the same answer.
        /// </remarks>
        public void Update()
        {
            if (_disposed) return;

            if (!_summaryLogged && _terrain != null && !_terrain.IsBuilding)
            {
                _summaryLogged = true;
                DrainContentWarnings();
                SimitoneLog.Info("Lot " + _activeLot + " has " + Describe() + ".");
            }

            if (_facades != null)
            {
                var outstanding = _facades.Outstanding(_activeLot, Focus);
                if (outstanding > 0)
                {
                    _facades.BuildStep(_activeLot, Focus);
                }
                else if (!_facadeDrainLogged && _summaryLogged)
                {
                    //the first log line lands on the frame the mesh arrives, which is before the
                    //first house has been built, and "0 facades, 38 still building" on its own
                    //reads as a broken surround rather than as one still filling in
                    _facadeDrainLogged = true;
                    SimitoneLog.Info("Lot " + _activeLot + " is surrounded by " + Describe() + ".");
                }
                DrainFacadeLog();
            }
        }

        private void DrainContentWarnings()
        {
            var warnings = ContentWarnings;
            for (int i = 0; i < warnings.Count; i++) SimitoneLog.Warn(warnings[i]);
        }

        /// <summary>
        /// Hands the facade pass's own log to the game's log as it is written. Every sentence in
        /// it is something a player or a bug report needs - a house file that would not load is
        /// currently recorded in a list this class exposes as a property and that nothing ever
        /// reads, which makes a broken neighbour completely silent.
        /// </summary>
        private void DrainFacadeLog()
        {
            if (_facades == null) return;
            var log = _facades.Log;
            for (int i = _facadeLogDrained; i < log.Count; i++)
            {
                SimitoneLog.Warn("Lot " + _activeLot + "'s surroundings: " + log[i]);
            }
            _facadeLogDrained = log.Count;
        }

        /// <summary>
        /// Where the facade queue builds nearest-first: the centre of the active lot's packed
        /// rectangle, which is also where the player is standing.
        /// </summary>
        private Vector2 Focus
        {
            get
            {
                var centre = NeighbourhoodWorldMapping.PackedCentre(_placement.Lot);
                return new Vector2(centre.X, centre.Y);
            }
        }

        /// <summary>
        /// Draws the generated terrain and the neighbouring facades with the lot's own camera
        /// and fog. Called by <c>WorldStatic.DrawBg</c>; see the class remarks for when.
        /// </summary>
        /// <param name="gfx">The lot's graphics device.</param>
        /// <param name="cam">The lot's active camera.</param>
        /// <param name="fogColor">
        /// The lot's weather fog. Its alpha is the fog distance in world units, which is where
        /// TSO's city terrain puts it too; <see cref="FogDistanceFor"/> decides how much of it is
        /// actually used.
        /// </param>
        /// <param name="surroundNumber">1 while the player is in a sub-world, 0 otherwise.</param>
        public void DrawSurrounding(GraphicsDevice gfx, ICamera cam, Vector4 fogColor, int surroundNumber)
        {
            if (_disposed || _terrain == null) return;
            if (gfx == null || cam == null) return;
            if (_vertexShader == null || _pixelShader == null) return;
            _lastSurroundNumber = surroundNumber;

            var fog = fogColor;
            var fogDistance = FogDistanceFor(fog.W, _drawn);
            fog.W = 1f;

            var world = _placement.WorldMatrixAt(_lotGroundAltitude);
            var mv = world * cam.View;
            var mvp = mv * (cam.Projection * Matrix.CreateScale(1f, 1f, BackdropDepthSquash));
            //the facade bounds are in generated-map space, so the frustum has to be the one the
            //placed world is seen through - which is the same matrix the terrain is drawn with
            var frustum = new BoundingFrustum(mvp);

            var previousRast = gfx.RasterizerState;
            var previousDepth = gfx.DepthStencilState;
            try
            {
                //neither pass sets a rasterizer or a depth state of its own that suits here:
                //the terrain's winding depends on which side of the diamond the tile is on, and
                //the backdrop is the first thing written into this frame's depth
                gfx.RasterizerState = RasterizerState.CullNone;
                gfx.DepthStencilState = DepthStencilState.Default;

                //facades first: they are opaque and the terrain behind them is not going to show
                //through, and the terrain's own pass is alpha blended, so the other order would
                //draw the ground over the houses it is behind
                //
                //mv, not mvp, for the facade pass: its fog is length(vPos)/FogMaxDist over
                //CityObjFogVS's vPos, which is a distance from the camera only in view space. The
                //surround terrain below is given both for exactly this reason, and the two passes
                //have to agree or the ground fades and the houses it is behind do not.
                _facades?.Draw(_vertexShader, _pixelShader, mvp, mv, _lightColor, fog, fogDistance, frustum);

                //the surround's own generated-map mesh last: the cropped ground, the map's own
                //connectors and the lot-view band, positioned at this lot by the placement folded
                //into mvp/mv above. It owns its buffers, so a lot load never touches the map view's
                //renderer - the two modes are independent.
                _terrain.Draw(gfx, _vertexShader, _pixelShader, mvp, mv,
                    _lightDirection, _lightColor, fog, fogDistance, frustum);
            }
            finally
            {
                gfx.RasterizerState = previousRast;
                gfx.DepthStencilState = previousDepth;
            }
        }

        /// <summary>
        /// How far away the surround's fog finishes, in lot-view world units.
        ///
        /// <remarks>
        /// <para>
        /// The lot's weather supplies the number, and its alpha is where TSO's city terrain puts
        /// it too - but on its own it is wrong here in both directions. A lot that has just come
        /// up reports zero, which puts every vertex on the near plane; and a clear-weather value
        /// of several hundred units is several hundred map tiles, which is most of a 512-tile map
        /// and puts the far edge, its fade skirt and its isometric diamond edge back in view.
        /// </para>
        /// <para>
        /// So the drawn region's own corner distance is the floor - far enough that the cut at the
        /// region's edge is always fully faded and therefore invisible - and the lot's weather may
        /// push it further, up to <see cref="MaxFogStretch"/> times that. Nothing beyond it is
        /// drawn anyway, so letting the weather ask for a longer view would only cost fill rate.
        /// </para>
        /// <para>
        /// <b>CHANGED: the region is now the whole map</b>, so this floor is the far corner of the
        /// island - about 1,140 world units from a lot at the middle of it, which is the same floor
        /// the neighbourhood view's own fog is built from and is why the two views show the same
        /// weather. The rule is unchanged and so are the tests: a region that really is a crop still
        /// hides its own edge, and the lot's weather can still see further than a crop's corner.
        /// </para>
        /// </remarks>
        /// </summary>
        /// <param name="lotFogDistance">The lot's <c>FogColor.W</c>, in world units.</param>
        public static float FogDistanceFor(float lotFogDistance, SimitoneSurroundingsCull cull)
        {
            var floor = cull.IsValid ? cull.FogDistance : DefaultFogDistance;
            if (floor <= 1f) floor = DefaultFogDistance;
            var ceiling = floor * MaxFogStretch;
            if (float.IsNaN(lotFogDistance) || lotFogDistance <= floor) return floor;
            return lotFogDistance < ceiling ? lotFogDistance : ceiling;
        }

        /// <summary>
        /// Rebuilds the textures after a device reset. The meshes are still valid; only the
        /// content that was uploaded to the old device is not.
        /// </summary>
        /// <remarks>
        /// Not wired to anything yet, deliberately. The lot view has no device-reset hook of its
        /// own to hang it off, and guessing at one - a window resize, say, which is not a reset and
        /// would throw the content away on every window move - would be a worse answer than M3's
        /// arrangement, which is the same exposure and says so on
        /// <see cref="Neighbourhood3DView.DeviceReset"/>. A device reset mid-lot costs a
        /// surround; it does not cost the lot.
        /// </remarks>
        /// <param name="device">The new device.</param>
        public void OnDeviceReset(GraphicsDevice device)
        {
            if (_disposed || device == null) return;
            _terrain?.SetContent(new NeighbourhoodContent(device));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            //the two city effects are shared content loaded once by the content provider and are
            //not ours to dispose; the terrain's textures and every facade mesh are
            _facades?.Dispose();
            _facades = null;
            _terrain?.Dispose();
            _terrain = null;
        }

        public override string ToString()
        {
            return "in-lot surroundings for lot " + _activeLot + ": " + _placement;
        }

        /// <summary>
        /// The surround's own generated-map mesh: the ground, the map's own connector quads and the
        /// active lot's <c>RestoreRoad</c> band, built from <see cref="NeighbourhoodCityMeshBuilder"/>
        /// and <see cref="SimitoneSurroundingsCrop"/> on a worker and uploaded on the game thread.
        ///
        /// <para>
        /// The surround owns its buffers, so a lot load never reaches into the neighbourhood's
        /// renderer. The mesh is the whole generated map
        /// cropped to <see cref="_drawn"/>, with the lot-view band laid over the active lot's frontage
        /// - the one street grammar the generated map does not carry.
        /// </para>
        /// </summary>
        private sealed class SurroundTerrain : IDisposable
        {
            private readonly GraphicsDevice _device;
            private readonly NeighbourhoodMap _map;
            //the region the band is measured over: the neighbour region, not the drawn region. See
            //the call site in the outer constructor and SimitoneLotViewRoadBand for why.
            private readonly SimitoneSurroundingsCull _bandRegion;
            //the region the generated-map mesh is cropped to. The surround's own mesh is a crop of
            //the whole map, so this is how much of it is drawn.
            private readonly SimitoneSurroundingsCull _drawn;
            //the lot the player is standing in: the one lot the band is built for. Every other lot
            //in the region is drawn by the map's connectors and by its own sub-world.
            private readonly short _activeLot;
            private readonly NeighbourhoodLayout _layout;
            private NeighbourhoodContent _content;

            private readonly VertexBuffer[] _layerVertices = new VertexBuffer[NeighbourhoodCityMeshBuilder.LayerCount];
            private readonly IndexBuffer[] _layerIndices = new IndexBuffer[NeighbourhoodCityMeshBuilder.LayerCount];
            private readonly int[] _layerPrims = new int[NeighbourhoodCityMeshBuilder.LayerCount];
            private VertexBuffer _roadVertices;
            private IndexBuffer _roadIndices;
            private int _roadPrims;

            private volatile bool _ready;
            private volatile bool _building;
            //Set by Dispose, which the lot view can call while the worker is still building the
            //mesh - a travel switch inside the build window - so the game-thread callback that
            //uploads the result has to be able to tell that the buffers it would create belong to a
            //surround that no longer exists. Without it the upload allocates vertex buffers that
            //nothing will ever dispose, once per lot switch inside the window.
            private volatile bool _disposed;
            private string _failure;

            public NeighbourhoodContent Content
            {
                get { return _content; }
            }

            public bool IsBuilding
            {
                get { return _building; }
            }

            public string BuildFailure
            {
                get { return _failure; }
            }

            /// <summary>Whether the whole-map mesh came out of <see cref="SimitoneSurroundingsMeshCache"/>.</summary>
            public volatile bool MeshWasCached;

            /// <summary>Triangles the last draw actually submitted, after the chunk cut.</summary>
            public int LastSubmittedPrimitives { get; private set; }

            //the kept index position of each map chunk, per layer and for the map's own roads (see
            //SimitoneSurroundingsCropResult.LayerChunkStarts); null draws the layer whole
            private int[][] _layerChunkStarts = new int[NeighbourhoodCityMeshBuilder.LayerCount][];
            private int[] _roadChunkStarts;
            private int _roadAlwaysIndices;
            private readonly List<int> _visibleChunks = new List<int>(NeighbourhoodCityMeshBuilder.ChunkCount);
            private readonly List<NeighbourhoodMeshDrawRange> _drawRanges = new List<NeighbourhoodMeshDrawRange>(64);

            /// <summary>Triangles held per frame if nothing were cut, across the five layers and the roads.</summary>
            public int DrawnPrimitives
            {
                get
                {
                    var total = _roadPrims;
                    for (int i = 0; i < _layerPrims.Length; i++) total += _layerPrims[i];
                    return total;
                }
            }

            /// <summary>What the whole generated map held before the region cut it down.</summary>
            public int SourcePrimitives { get; private set; }

            /// <summary>Which grammar laid the street that was uploaded, for the log.</summary>
            public string RoadSource { get; private set; }

            /// <summary>Triangles the crop dropped for being past the region's edge.</summary>
            public int OutsideRegionPrimitives { get; private set; }

            /// <summary>
            /// Triangles that are inside the player's own property. The surround draws them and the
            /// lot's own ground is drawn over them - see
            /// <see cref="SimitoneSurroundingsCull.KeepsTile"/>.
            /// </summary>
            public int UnderLotPrimitives { get; private set; }

            /// <summary>Triangles the crop made: the bank along the property line.</summary>
            public int BankPrimitives { get; private set; }

            /// <summary>Road triangles the map's own connectors contributed.</summary>
            public int MapRoadPrimitives { get; private set; }

            /// <summary>Road triangles the lot view's <c>RestoreRoad</c> band contributed.</summary>
            public int BandRoadPrimitives { get; private set; }

            /// <summary>How many triangles a ground layer holds, or 0.</summary>
            /// <param name="terrainType">A <see cref="NeighbourhoodTerrain"/> value.</param>
            public int LayerPrimitives(int terrainType)
            {
                if (terrainType < 0 || terrainType >= _layerPrims.Length) return 0;
                return _layerPrims[terrainType];
            }

            /// <summary>How many road triangles survive the crop.</summary>
            public int RoadPrimitives
            {
                get { return _roadPrims; }
            }

            public static SurroundTerrain TryCreate(
                GraphicsDevice device, NeighbourhoodMap map, NeighbourhoodLayout layout,
                SimitoneSurroundingsCull bandRegion, SimitoneSurroundingsCull drawn, int activeLot,
                out string reason)
            {
                if (device == null)
                {
                    reason = "there is no graphics device";
                    return null;
                }
                if (!NeighbourhoodCityMeshBuilder.CanMesh(map, out reason)) return null;
                if (!bandRegion.IsValid || !drawn.IsValid)
                {
                    reason = "there is no map region around the lot to draw";
                    return null;
                }
                try
                {
                    reason = null;
                    return new SurroundTerrain(device, map, layout, bandRegion, drawn, activeLot);
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                    return null;
                }
            }

            private SurroundTerrain(GraphicsDevice device, NeighbourhoodMap map,
                NeighbourhoodLayout layout, SimitoneSurroundingsCull bandRegion,
                SimitoneSurroundingsCull drawn, int activeLot)
            {
                _device = device;
                _map = map;
                _layout = layout;
                _bandRegion = bandRegion;
                _drawn = drawn;
                _activeLot = (short)activeLot;
                _content = new NeighbourhoodContent(device);
            }

            /// <summary>
            /// Builds the lot-view road band over the band region and uploads it. The build is on a
            /// worker; the upload is on the game thread, which is where a <c>GraphicsDevice</c> has
            /// to be touched. The buffers are swapped in one go inside that callback, so a draw in
            /// between sees either nothing or the finished band and never half of one.
            ///
            /// <para>
            /// <b>The whole map is built and cropped, not just the band.</b> The surround owns its
            /// generated-map mesh: the ground, the map's own connector quads and the lot-view band,
            /// cropped to the drawn region, and never touches the neighbourhood's renderer.
            /// </para>
            /// </summary>
            public void BeginMeshBuild()
            {
                if (_building || _ready || _disposed) return;
                _building = true;
                _failure = null;
                var map = _map;
                var cull = _bandRegion;
                var drawn = _drawn;
                var layout = _layout;
                var activeLot = _activeLot;
                //the cache key is read here, on the game thread, before the worker starts: the
                //slot's invalidation epoch is what says the layout or terrain has changed
                var slot = layout?.Slot ?? -1;
                var epoch = slot >= 0 ? NeighbourhoodWorldCache.EpochOf(slot) : 0;
                Task.Run(() =>
                {
                    SimitoneSurroundingsCropResult crop = null;
                    string reason = null;
                    try
                    {
                        bool cached;
                        var mesh = SimitoneSurroundingsMeshCache.GetOrBuild(slot, map, epoch,
                            m => NeighbourhoodCityMeshBuilder.Build(m), out cached);
                        MeshWasCached = cached;
                        crop = SimitoneSurroundingsCrop.Crop(
                            mesh, drawn, BuildRoadBand(map, layout, cull, activeLot));
                    }
                    catch (Exception ex)
                    {
                        reason = ex.Message;
                    }
                    var built = crop;
                    var failure = reason;
                    GameThread.NextUpdate(state =>
                    {
                        try
                        {
                            //A lot switch can dispose this while the worker is still meshing, and
                            //the callback is already queued by then. Uploading here would allocate
                            //buffers nothing owns, so the disposed state is the whole of the check.
                            if (_disposed) return;
                            if (built != null) Upload(built);
                            else _failure = failure;
                        }
                        catch (Exception ex)
                        {
                            //an upload that throws (device lost, out of memory) would otherwise
                            //escape into the game thread's callback queue; a surround that cannot
                            //upload is a reason in the log, not a failed frame. Dispose frees whatever
                            //was uploaded before the throw.
                            _failure = "the terrain mesh could not be uploaded: " + ex.Message;
                        }
                        finally
                        {
                            _building = false;
                        }
                    });
                });
            }

            /// <summary>
            /// The lot view's own street over the band region - the neighbour region, not the whole
            /// map - for the active lot alone, or <c>null</c> when there is no layout to take the
            /// lot's frontage from, in which case the band is empty and the surround is the shared
            /// map's own connectors.
            /// </summary>
            /// <remarks>
            /// <b>The active lot alone, because that is where the band's grammar is needed.</b> The
            /// band exists to lay the lot view's own <c>RestoreRoad</c> tiles on the seam along the
            /// active lot's borders and in the corridors immediately around it, so that the active
            /// lot's own ground and the surround meet on the same street. Every other lot in the
            /// region is drawn by the shared lifted <c>CityGeometry</c>'s own connector quads - which
            /// are the real streets the player was shown on the map - and by its own sub-world, which
            /// stamps its own half-street; a second band under all of them is the black tarmac
            /// tapestry over the surrounding plots. Built for every lot in the region it also filled
            /// the corridor beside every plot, which is what made the surround read as a grid of
            /// tarmac rather than as ground with streets on it.
            /// </remarks>
            private static SimitoneLotViewRoadBand BuildRoadBand(
                NeighbourhoodMap map, NeighbourhoodLayout layout, SimitoneSurroundingsCull cull,
                short activeLot)
            {
                if (map == null || layout == null || !cull.IsValid) return null;
                var active = layout.FindLot(activeLot);
                if (active == null)
                {
                    var region = new Rectangle(
                        cull.MinX, cull.MinZ, cull.WidthTiles, cull.HeightTiles);
                    return SimitoneLotViewRoadBand.For(map, cull.LotsInRange(layout), region);
                }
                //The active lot's own rectangle, not the neighbour region. The band's grammar is the
                //lot view's RestoreRoad seam - the half-street stamped inside the lot and the
                //corridor immediately off it - and every tile past that is the shared map's own
                //connector quads, which are the real streets the player was shown. Building over the
                //neighbour region stamped the seam onto every surrounding plot and filled the
                //corridor beside each of them, which is the black tarmac grid; the lot's own
                //rectangle keeps it to the one lot this lot view is actually standing in.
                var lotRegion = new Rectangle(
                    Math.Max(0, active.WorldX), Math.Max(0, active.WorldY),
                    Math.Max(0, active.Size), Math.Max(0, active.Size));
                return SimitoneLotViewRoadBand.For(map, new[] { active }, lotRegion);
            }

            private void Upload(SimitoneSurroundingsCropResult crop)
            {
                for (int i = 0; i < _layerVertices.Length; i++)
                {
                    DisposeLayer(i);
                    _layerVertices[i] = UploadLayer(
                        crop.LayerVertices[i], crop.LayerIndices[i], out _layerIndices[i]);
                    _layerPrims[i] = crop.LayerPrimitives[i];
                }
                DisposeRoad();
                _roadVertices = UploadLayer(crop.RoadVertices, crop.RoadIndices, out _roadIndices);
                _roadPrims = crop.RoadPrimitives;
                _layerChunkStarts = crop.LayerChunkStarts ?? new int[NeighbourhoodCityMeshBuilder.LayerCount][];
                _roadChunkStarts = crop.RoadChunkStarts;
                _roadAlwaysIndices = crop.RoadAlwaysIndices;

                SourcePrimitives = crop.SourcePrimitives;
                RoadSource = crop.RoadSource;
                OutsideRegionPrimitives = crop.OutsideRegion;
                UnderLotPrimitives = crop.UnderActiveLot;
                BankPrimitives = crop.BankPrimitives;
                MapRoadPrimitives = crop.MapRoadPrimitives;
                BandRoadPrimitives = crop.BandRoadPrimitives;
                _ready = DrawnPrimitives > 0;
            }

            private VertexBuffer UploadLayer(
                NeighbourhoodTerrainVertex[] vertices, int[] indices, out IndexBuffer indexBuffer)
            {
                indexBuffer = null;
                if (vertices == null || indices == null || indices.Length == 0) return null;
                var vertexBuffer = new VertexBuffer(_device, typeof(NeighbourhoodTerrainVertex),
                    vertices.Length, BufferUsage.None);
                vertexBuffer.SetData(vertices);
                indexBuffer = new IndexBuffer(_device, IndexElementSize.ThirtyTwoBits,
                    indices.Length, BufferUsage.None);
                indexBuffer.SetData(indices);
                return vertexBuffer;
            }

            private void DisposeLayer(int index)
            {
                _layerVertices[index]?.Dispose();
                _layerVertices[index] = null;
                _layerIndices[index]?.Dispose();
                _layerIndices[index] = null;
                _layerPrims[index] = 0;
            }

            private void DisposeRoad()
            {
                _roadVertices?.Dispose();
                _roadVertices = null;
                _roadIndices?.Dispose();
                _roadIndices = null;
                _roadPrims = 0;
            }

            /// <summary>Replaces the textures, e.g. after a device reset.</summary>
            /// <param name="content">The new content; the old one is disposed.</param>
            public void SetContent(NeighbourhoodContent content)
            {
                if (ReferenceEquals(content, _content)) return;
                _content?.Dispose();
                _content = content ?? new NeighbourhoodContent(null);
            }

            /// <summary>
            /// Draws the cropped terrain and its roads.
            /// </summary>
            /// <param name="device">The lot's graphics device.</param>
            /// <param name="vertexShader">The city's <c>Effects/VerShader</c>.</param>
            /// <param name="pixelShader">The city's <c>Effects/PixShader</c>.</param>
            /// <param name="viewProjection">The combined camera matrix, with the depth squash.</param>
            /// <param name="modelView">
            /// The model-view matrix, which the city's pixel shader wants separately: its
            /// <c>Fog()</c> measures <c>length(vPos)</c> against <c>FogMaxDist</c>, and
            /// <c>vPos</c> is <c>VertexPosition * MV</c>. TSO binds <c>MV</c> to
            /// <c>world * view</c> so that length is a view-space distance; binding the combined
            /// matrix there makes it a clip-space quantity and the fog stops meaning anything.
            /// </param>
            /// <param name="lightDirection">A unit vector pointing towards the light.</param>
            /// <param name="lightColor">The light's colour and strength.</param>
            /// <param name="fogColor">The colour the far edge of the drawn region fades into.</param>
            /// <param name="fogDistance">How far away the fade completes, in world units.</param>
            /// <param name="frustum">
            /// What the camera sees, in the mesh's own space (the same matrix as
            /// <paramref name="viewProjection"/>). Map chunks outside it are not submitted; null
            /// submits everything.
            /// </param>
            public void Draw(
                GraphicsDevice device, Effect vertexShader, Effect pixelShader,
                Matrix viewProjection, Matrix modelView,
                Vector3 lightDirection, Vector4 lightColor, Vector4 fogColor, float fogDistance,
                BoundingFrustum frustum = null)
            {
                if (!_ready || _content == null || device == null) return;
                var content = _content;

                //the chunk cut: one box test per map chunk (1024), which is far cheaper than
                //submitting the ~276k triangles of the whole island every frame
                _visibleChunks.Clear();
                if (frustum != null) NeighbourhoodCityMeshBuilder.ChunksInFrustum(frustum, _visibleChunks);
                var cutting = frustum != null && _visibleChunks.Count < NeighbourhoodCityMeshBuilder.ChunkCount;
                var submitted = 0;

                var vsPass = NeighbourhoodShaderState.BeginCity(
                    vertexShader, pixelShader, viewProjection, lightDirection, lightColor, fogColor, fogDistance);
                if (vsPass == null) return;
                //after BeginCity, which cannot know about the model-view matrix because it only
                //ever sees one matrix; the fog is measured from this one
                vertexShader.Parameters["MV"].SetValue(modelView);

                var previousBlend = device.BlendState;
                try
                {
                    //The fade skirt past the diamond is written as falling alpha, and the fog pass
                    //multiplies it in, so the ground has to be blended rather than opaque. The
                    //vertex declaration needs no setting: it rides on the buffer, because
                    // NeighbourhoodTerrainVertex is an IVertexType.
                    device.BlendState = BlendState.AlphaBlend;
                    device.SamplerStates[0] = SamplerState.LinearWrap;
                    device.SamplerStates[1] = SamplerState.LinearClamp;

                    for (int i = 0; i < _layerVertices.Length; i++)
                    {
                        if (_layerPrims[i] <= 0 || _layerVertices[i] == null) continue;
                        pixelShader.Parameters["TextureAtlasTex"].SetValue(
                            i < content.Ground.Length ? content.Ground[i] : null);
                        //Every vertex here carries MaskTextureCoord (-1,-1), which GetNCityColor reads
                        //as "no blend mask", so the blend sampler is never read and is left unbound.
                        pixelShader.Parameters["TransAtlasTex"].SetValue((Texture2D)null);
                        NeighbourhoodShaderState.ApplyCity(vsPass, vertexShader, pixelShader);
                        device.SetVertexBuffer(_layerVertices[i]);
                        device.Indices = _layerIndices[i];
                        submitted += SubmitChunks(device,
                            cutting ? _layerChunkStarts[i] : null, 0, _layerPrims[i]);
                    }

                    if (content.RoadAtlas != null && _roadPrims > 0 && _roadVertices != null)
                    {
                        //Opaque, not blended. The ground layers below are alpha blended because the
                        //fade skirt past the isometric diamond is written as falling alpha, and a
                        //road quad is never on the skirt - Transparency is 0 for every one of them,
                        //because a road only ever exists strictly inside the diamond. So the blend
                        //state buys the road pass nothing and costs the street: TSO's road atlas is
                        //authored with per-pixel alpha, and blending it over the grass layer lets
                        //the grass show through every semi-transparent texel in a road cell, which
                        //reads as a green street. Opaque is also what the road pass is drawn with
                        //on the neighbourhood screen, so the same atlas behaves the same in both
                        //views.
                        device.BlendState = BlendState.Opaque;
                        pixelShader.Parameters["TextureAtlasTex"].SetValue(content.RoadAtlas);
                        device.SamplerStates[0] = NeighbourhoodTerrainRenderer.RoadSampler;
                        NeighbourhoodShaderState.ApplyCity(vsPass, vertexShader, pixelShader);
                        device.SetVertexBuffer(_roadVertices);
                        device.Indices = _roadIndices;
                        if (cutting && _roadChunkStarts != null)
                        {
                            //the band's block first, always, then the map's own road chunks after it
                            var always = _roadAlwaysIndices / 3;
                            if (always > 0)
                            {
                                device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, always);
                                submitted += always;
                            }
                            submitted += SubmitChunks(device, _roadChunkStarts, _roadAlwaysIndices,
                                _roadPrims - always);
                        }
                        else
                        {
                            submitted += SubmitChunks(device, null, 0, _roadPrims);
                        }
                    }
                }
                finally
                {
                    device.BlendState = previousBlend;
                    LastSubmittedPrimitives = submitted;
                }
            }

            /// <summary>
            /// Submits one buffer: the whole of it when there is no chunk table, otherwise only the
            /// runs of visible chunks. Returns the triangles submitted.
            /// </summary>
            /// <param name="device">The device.</param>
            /// <param name="chunkStarts">The buffer's chunk table, or null to draw it whole.</param>
            /// <param name="startOffset">Index position the table's zero sits at.</param>
            /// <param name="wholePrimitives">The triangle count to draw when drawing whole.</param>
            private int SubmitChunks(GraphicsDevice device, int[] chunkStarts, int startOffset, int wholePrimitives)
            {
                if (chunkStarts == null)
                {
                    if (wholePrimitives <= 0) return 0;
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, wholePrimitives);
                    return wholePrimitives;
                }
                NeighbourhoodCityMeshBuilder.VisibleDrawRanges(chunkStarts, _visibleChunks, _drawRanges);
                var total = 0;
                for (int r = 0; r < _drawRanges.Count; r++)
                {
                    var call = NeighbourhoodDrawCall.ForRange(_drawRanges[r]);
                    if (call.IsEmpty) continue;
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, call.BaseVertex,
                        startOffset + call.StartIndex, call.PrimitiveCount);
                    total += call.PrimitiveCount;
                }
                return total;
            }

            public void Dispose()
            {
                //first, so a game-thread callback that was already queued cannot upload into a
                //surround that has just gone away - see the field's own remarks
                _disposed = true;
                for (int i = 0; i < _layerVertices.Length; i++) DisposeLayer(i);
                DisposeRoad();
                _content?.Dispose();
                _content = null;
                _ready = false;
            }
        }
    }
}
