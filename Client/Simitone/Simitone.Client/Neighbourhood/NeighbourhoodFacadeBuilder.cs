/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using FSO.Client;
using FSO.Common.Rendering.Framework;
using FSO.Content;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView;
using FSO.LotView.Facade;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// One reference the facade pipeline needs before it can render a lot.
    ///
    /// <para><b>Why an enum and not a stack trace.</b> A <c>NullReferenceException</c> says that
    /// something was null. It does not say what, and the pipeline that threw is four calls deep in
    /// the lot view: <c>LotFacadeGenerator.GenerateWalls</c> dereferences the content system's
    /// shared render effect, <c>GenerateRoof</c> dereferences the blueprint's roof component and
    /// then the per-storey mesh data behind it, and
    /// <c>VMTS1Activator.LoadFromIff</c> dereferences four chunks of an untrusted house file
    /// without checking any of them. So the builder names each of them here, checks each of them
    /// before it is used, and says which one it was - see
    /// <see cref="NeighbourhoodFacadeDiagnostics"/>.</para>
    /// </summary>
    public enum NeighbourhoodFacadePiece
    {
        /// <summary>Nothing is missing.</summary>
        None = 0,

        /// <summary>The graphics device the facade is rendered on.</summary>
        GraphicsDevice,

        /// <summary>The 3D scene layer the throwaway lot world is added to.</summary>
        SceneLayer,

        /// <summary>The content manager, i.e. <c>FSO.Content.Content.Get()</c>.</summary>
        ContentManager,

        /// <summary>The neighbourhood content provider, i.e. <c>Content.Neighborhood</c>.</summary>
        NeighbourhoodContent,

        /// <summary>The lot's <c>HouseNN.iff</c>.</summary>
        HouseFile,

        /// <summary>The house file's <c>SIMI 1</c> chunk: the lot's size, globals and version.</summary>
        SimulationChunk,

        /// <summary>The house file's <c>HOUS 0</c> chunk: the camera direction and roof name.</summary>
        HouseChunk,

        /// <summary>The house file's <c>OBJM 1</c> chunk: the lot's placed objects.</summary>
        ObjectChunk,

        /// <summary>The house file's <c>OBJT 0</c> chunk: the lot's object type records.</summary>
        ObjectTypeChunk,

        /// <summary>
        /// The lot view's shared object render effect, i.e. <c>WorldContent.RCObject</c>, which
        /// <c>LotFacadeGenerator</c> uses to draw every wall and every object on it.
        /// </summary>
        LotRenderEffect,

        /// <summary>
        /// The throwaway <c>World</c>'s <c>State</c>, which only exists once the scene layer has
        /// initialised it - so a world added to a layer with no device is a world with no state,
        /// and its <c>LoadFromIff</c> dies three lines in.
        /// </summary>
        LotWorldState,

        /// <summary>The blueprint <c>VMTS1Activator</c> built out of the house file.</summary>
        Blueprint,

        /// <summary>The blueprint's roof component, which <c>GenerateRoof</c> dereferences.</summary>
        RoofComponent,

        /// <summary>The blueprint's terrain component, which <c>GenerateFloor</c> draws with.</summary>
        TerrainComponent,

        /// <summary>
        /// A storey's roof geometry: what <c>RoofComponent.MeshRectData</c> hands back for it.
        ///
        /// <para><b>This is the one that was null for lot 64.</b> It is <c>null</c> - not empty,
        /// null - for every storey the roof component never meshed, and
        /// <c>RoofComponent.RegenRoof</c> only ever meshes a storey that has indoor rooms under
        /// it. A house with rooms on the ground floor and nothing built above it therefore has
        /// four unmeshed storeys, and <c>LotFacadeGenerator.GenerateRoof</c> used to read
        /// <c>data.Vertices</c> off the first of them. It is its own piece rather than a repeat of
        /// <see cref="RoofComponent"/> precisely because the component was there, had a texture,
        /// and still produced nothing for three quarters of the house.</para>
        /// </summary>
        RoofGeometry,

        /// <summary>
        /// The lot's own declared size, in tiles, as read out of <c>SIMI.GlobalData[23]</c>.
        ///
        /// <para><b>Checked before the throwaway world exists</b>, and that is the whole of it.
        /// The activator reads that word with no check of its own and hands it to the lot view,
        /// which sizes its light-map render targets as
        /// <c>3 * shadow-detail * (size - 1)</c> pixels across. That product has two ends, and
        /// this is the piece for both: a file claiming one tile asks D3D for a zero-wide texture,
        /// and a file claiming sixty asks for 4248 pixels of it on a machine whose shadow detail
        /// is 24 - past the 4096 this device accepts. Either way the answer is
        /// <c>SharpDXException: E_INVALIDARG</c>, thrown from inside the house-file load,
        /// seconds after a <c>World</c> and a <c>VM</c> were stood up for it.</para>
        ///
        /// <para>The far end is not only checked, it is answered: the shadow detail is brought
        /// down to what the lot and the device can hold for the length of the load, so a lot that
        /// merely is too big at the current setting is built rather than skipped. See
        /// <see cref="LightMapFits"/> and <see cref="MinimumLotSize"/>.</para>
        /// </summary>
        LotSize,

        /// <summary>
        /// The mesh a pass produced, when it produced one and something on the way to the device
        /// threw anyway.
        ///
        /// <para><b>This is the one that was hiding the wall bug.</b> The surface upload catches
        /// everything, because a half-uploaded surface draws nothing and there is nothing else in
        /// that method for it to do - and the consequence was that a device which refused all
        /// three uploads produced exactly the same log line as a lot with no walls, no roof and no
        /// floor: "the lot's walls, roof and floor all came back empty". That sentence is about
        /// the <em>geometry</em>, and it was being printed for an <em>upload</em> failure, so
        /// nothing in it pointed at the wall pass. The catch now hands the throw up and this is
        /// what it becomes, which costs nothing when the upload works and names the device when
        /// it does not.</para>
        /// </summary>
        SurfaceUpload
    }

    /// <summary>
    /// Which of the generator's three passes a lot is being put through.
    ///
    /// <para><b>Why the roof is a stage and not a requirement.</b> <c>LotFacadeGenerator.Generate</c>
    /// ran three calls in a row - walls, floor, roof - and the builder reported all of them as one
    /// stage, so a throw in any of them came out of the log as the same sentence. Splitting them
    /// is what lets the next log line say which call, and <see cref="NeighbourhoodFacadeDiagnostics.RequiredStages"/>
    /// is what lets a lot run the two it can: a house with no roof component, or with no storey
    /// the roof component meshed, gets walls and floor and is drawn.</para>
    /// </summary>
    [Flags]
    public enum NeighbourhoodFacadeStages
    {
        /// <summary>Nothing.</summary>
        None = 0,

        /// <summary>
        /// <c>LotFacadeGenerator.GenerateWalls</c>. Always run: the walls are the outline of the
        /// house, and no other pass produces them. A lot with no outside rooms comes back with an
        /// empty wall surface, which is a lot with no house on it rather than a failed build.
        /// </summary>
        Walls = 1,

        /// <summary>
        /// <c>LotFacadeGenerator.GenerateFloor</c>. Needs the blueprint's terrain component, which
        /// the TS1 activator builds for every lot it loads.
        /// </summary>
        Floor = 2,

        /// <summary>
        /// <c>LotFacadeGenerator.GenerateRoof</c>. Needs the blueprint's roof component, and needs
        /// it to have a storey it meshed - see <see cref="NeighbourhoodFacadePiece.RoofGeometry"/>.
        /// </summary>
        Roof = 4
    }

    /// <summary>
    /// One thing the pipeline needs, whether it is there, and what wanted it.
    /// </summary>
    /// <remarks>
    /// <para>A requirement rather than a bare boolean so the failure sentence can say what the
    /// missing piece was <em>for</em>. "The roof component was null" is a fact; "the roof
    /// component was null, so the lot has no roof to draw" is a bug report.</para>
    /// <para><b>Not every missing piece is fatal.</b> <see cref="Fatal"/> separates "the pipeline
    /// cannot go on" from "this pass has nothing to do". The roof and the ground are the latter: a
    /// house with neither still has walls, and a neighbourhood of roofless houses is a
    /// neighbourhood of visible houses rather than an empty one.</para>
    /// </remarks>
    public readonly struct NeighbourhoodFacadeRequirement
    {
        /// <summary>Which reference this is about.</summary>
        public NeighbourhoodFacadePiece Piece { get; }

        /// <summary>Whether it is there.</summary>
        public bool Present { get; }

        /// <summary>What needed it, in a clause.</summary>
        public string Because { get; }

        /// <summary>
        /// Whether the build cannot continue without it. A piece that is only fatal when it is
        /// missing still gates its own stage; it just does not gate the other two.
        /// </summary>
        public bool Fatal { get; }

        public NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece piece, bool present, string because)
            : this(piece, present, because, true)
        {
        }

        public NeighbourhoodFacadeRequirement(
            NeighbourhoodFacadePiece piece, bool present, string because, bool fatal)
        {
            Piece = piece;
            Present = present;
            Because = because;
            Fatal = fatal;
        }

    }

    /// <summary>
    /// Turns a missing reference, or a throw at a known point in the pipeline, into a sentence
    /// that names what was null and which lot it was null for.
    ///
    /// <para><b>Pure on purpose.</b> The whole facade pipeline needs a graphics device, so none of
    /// it can be asserted from a test. The rule that turns "something was null" into "this was
    /// null" has no device in it and therefore can be, and is: the checklist is a list of pieces
    /// and a yes/no each, and the sentence is a lookup. What the pieces are is decided by the
    /// builder, which is the only place that can see them; what the sentence says about the first
    /// one it finds is decided here, which is the only part worth pinning.</para>
    /// </summary>
    public static class NeighbourhoodFacadeDiagnostics
    {
        /// <summary>
        /// The first requirement on the list that is not satisfied, or <c>null</c> when they all
        /// are. Order is the order of the pipeline, so the answer is the earliest thing that would
        /// have thrown, not merely the first one anybody thought to ask about.
        /// </summary>
        /// <param name="requirements">The checklist, in pipeline order.</param>
        public static NeighbourhoodFacadeRequirement? FirstMissing(
            IList<NeighbourhoodFacadeRequirement> requirements)
        {
            if (requirements == null) return null;
            for (int i = 0; i < requirements.Count; i++)
            {
                if (!requirements[i].Present) return requirements[i];
            }
            return null;
        }

        /// <summary>
        /// The first unsatisfied requirement the pipeline cannot go on without, or <c>null</c> when
        /// it can.
        /// </summary>
        /// <remarks>
        /// <para><c>FirstMissing</c> answers "what is the first thing that would have thrown";
        /// this answers "is there anything here that stops the build". They differ for exactly the
        /// two requirements that gate a single pass - the roof and the ground - and they have to
        /// differ, because the alternative is the rule this bug was reported under: a lot with no
        /// roof is treated as a lot that cannot be built, so its walls are thrown away too.</para>
        /// </remarks>
        /// <param name="requirements">The checklist, in pipeline order.</param>
        public static NeighbourhoodFacadeRequirement? FirstFatal(
            IList<NeighbourhoodFacadeRequirement> requirements)
        {
            if (requirements == null) return null;
            for (int i = 0; i < requirements.Count; i++)
            {
                if (!requirements[i].Present && requirements[i].Fatal) return requirements[i];
            }
            return null;
        }

        /// <summary>
        /// Which of the generator's three passes a checklist leaves runnable.
        /// </summary>
        /// <remarks>
        /// <para>This is the decision the log line never made explicit, and it is the one that
        /// decides whether a house appears at all. The walls are always run: nothing else draws
        /// the outline. The floor runs when the blueprint has a terrain component to draw it
        /// through. The roof runs when the blueprint has a roof component - and the component
        /// being present is not the same as the roof having geometry, which is why
        /// <see cref="NeighbourhoodFacadePiece.RoofGeometry"/> is reported separately, after the
        /// pass, rather than asked for here.</para>
        /// <para>Pure, so it is testable: this is the only part of the facade pipeline that can
        /// be, because everything around it needs a graphics device.</para>
        /// </remarks>
        /// <param name="requirements">The blueprint checklist, in pipeline order.</param>
        public static NeighbourhoodFacadeStages RequiredStages(
            IList<NeighbourhoodFacadeRequirement> requirements)
        {
            //walls unconditionally, then each optional pass gated on its own component
            var stages = NeighbourhoodFacadeStages.Walls;
            if (requirements == null) return stages;
            for (int i = 0; i < requirements.Count; i++)
            {
                if (!requirements[i].Present) continue;
                switch (requirements[i].Piece)
                {
                    case NeighbourhoodFacadePiece.TerrainComponent:
                        stages |= NeighbourhoodFacadeStages.Floor;
                        break;
                    case NeighbourhoodFacadePiece.RoofComponent:
                        stages |= NeighbourhoodFacadeStages.Roof;
                        break;
                }
            }
            return stages;
        }

        /// <summary>
        /// The passes whose surfaces came back with geometry, as a set.
        /// </summary>
        /// <param name="wallGeometry">Whether the wall pass produced triangles.</param>
        /// <param name="floorGeometry">Whether the floor pass produced triangles.</param>
        /// <param name="roofGeometry">Whether the roof pass produced triangles.</param>
        public static NeighbourhoodFacadeStages CompletedStages(
            bool wallGeometry, bool floorGeometry, bool roofGeometry)
        {
            var stages = NeighbourhoodFacadeStages.None;
            if (wallGeometry) stages |= NeighbourhoodFacadeStages.Walls;
            if (floorGeometry) stages |= NeighbourhoodFacadeStages.Floor;
            if (roofGeometry) stages |= NeighbourhoodFacadeStages.Roof;
            return stages;
        }

        /// <summary>
        /// Whether every one of <paramref name="stage"/>'s passes is in <paramref name="stages"/>.
        /// A single pass is the usual question; a set is a set question answered the same way.
        /// <c>None</c> is never in anything, including itself.
        /// </summary>
        /// <param name="stages">The set.</param>
        /// <param name="stage">The pass, or passes, to ask about.</param>
        public static bool Has(NeighbourhoodFacadeStages stages, NeighbourhoodFacadeStages stage)
        {
            return stage != NeighbourhoodFacadeStages.None && (stages & stage) == stage;
        }

        /// <summary>
        /// What a lot is missing across the passes that produced nothing, as one clause, for a
        /// sentence that also has to say what <em>did</em> come back.
        /// </summary>
        /// <remarks>
        /// A house with walls and a floor is not a failure and does not get one of these. This is
        /// for the lot where all three passes ran and all three came back empty, and where the
        /// answer "all came back empty" on its own would send the next person looking for a
        /// rendering bug rather than for the storey that has no rooms under it.
        /// </remarks>
        /// <param name="absent">The unsatisfied requirements, in pipeline order.</param>
        /// <param name="lotNumber">The lot being built.</param>
        public static string Absent(IList<NeighbourhoodFacadeRequirement> absent, int lotNumber)
        {
            if (absent == null || absent.Count == 0) return null;
            var parts = new string[absent.Count];
            for (int i = 0; i < absent.Count; i++) parts[i] = Describe(absent[i].Piece);
            return "lot " + lotNumber + ": " + string.Join("; and ", parts);
        }

        /// <summary>
        /// What a piece is, in words a player can act on. The engine's own name is in brackets
        /// because the person fixing this next is reading the lot view's source, not the log.
        /// </summary>
        /// <param name="piece">The piece.</param>
        public static string Describe(NeighbourhoodFacadePiece piece)
        {
            switch (piece)
            {
                case NeighbourhoodFacadePiece.GraphicsDevice:
                    return "there is no graphics device";
                case NeighbourhoodFacadePiece.SceneLayer:
                    return "the 3D scene layer (GameFacade.Scenes) is not up, or has no graphics"
                        + " device, so a throwaway lot world cannot be initialised in it";
                case NeighbourhoodFacadePiece.ContentManager:
                    return "the content manager (FSO.Content.Content.Get()) is not up yet";
                case NeighbourhoodFacadePiece.NeighbourhoodContent:
                    return "the content manager has no neighbourhood provider (Content.Neighborhood),"
                        + " so there is nowhere to read a house file from";
                case NeighbourhoodFacadePiece.HouseFile:
                    return "the neighbourhood has no file for this lot";
                case NeighbourhoodFacadePiece.SimulationChunk:
                    return "the lot's house file has no simulation chunk (SIMI 1), or it has no"
                        + " globals array of 36 or more entries, so the lot's own size, version and"
                        + " global values cannot be read";
                case NeighbourhoodFacadePiece.HouseChunk:
                    return "the lot's house file has no house chunk (HOUS 0), so it has no camera"
                        + " direction and no roof name";
                case NeighbourhoodFacadePiece.ObjectChunk:
                    return "the lot's house file has no placed-object chunk (OBJM 1), so the objects"
                        + " standing in it cannot be read";
                case NeighbourhoodFacadePiece.ObjectTypeChunk:
                    return "the lot's house file has no object-type chunk (OBJT 0), so its objects"
                        + " have nothing to be looked up in";
                case NeighbourhoodFacadePiece.LotRenderEffect:
                    return "the lot view's object render effect (WorldContent.RCObject) is not"
                        + " loaded, so the lot's walls and the objects on them cannot be drawn";
                case NeighbourhoodFacadePiece.LotWorldState:
                    return "the throwaway lot world has no state, which means the scene layer added"
                        + " it without initialising it, so there is no lot view to render into";
                case NeighbourhoodFacadePiece.Blueprint:
                    return "the lot's blueprint could not be built from its house file";
                case NeighbourhoodFacadePiece.RoofComponent:
                    return "the lot's blueprint has no roof component, so the lot has no roof to draw";
                case NeighbourhoodFacadePiece.TerrainComponent:
                    return "the lot's blueprint has no terrain component, so the ground under the"
                        + " lot cannot be drawn";
                case NeighbourhoodFacadePiece.RoofGeometry:
                    return "the lot's roof component has no meshed storey - the house has rooms on"
                        + " one floor and nothing built over them, so RoofComponent.MeshRectData"
                        + " has no geometry and the lot has no roof to draw";
                case NeighbourhoodFacadePiece.LotSize:
                    return "the lot's house file declares a lot size the lot view cannot be stood up"
                        + " for - the light map is sized as 3 x shadow-detail x (size - 1) pixels"
                        + " across, so a lot of zero or one tiles asks the graphics device for a"
                        + " zero-sized render target, and a lot past what this device will"
                        + " allocate asks it for one that is too wide; either is refused with"
                        + " SharpDX E_INVALIDARG after the whole lot view has already been loaded";
                case NeighbourhoodFacadePiece.SurfaceUpload:
                    return "the facade's mesh was produced and the graphics device refused it - the"
                        + " vertex or index buffer could not be created, which is a device that is"
                        + " out of memory or in a removed state, and not a lot with no house on it";
                default:
                    return "something the facade pipeline needs is missing";
            }
        }

        /// <summary>
        /// The failure sentence for a lot whose pipeline is missing a piece: what was null, and
        /// what that costs, on a lot named by its number.
        /// </summary>
        /// <remarks>
        /// The lot number is in here as well as in the caller's log line on purpose. The caller
        /// prefixes "Neighbourhood 64 has no facade:", the summary list prefixes "lot 64:", and a
        /// reason that is worth reading on its own - quoted into a bug report, or read out of a
        /// middle of a log - has to say whose house it is about.
        /// </remarks>
        /// <param name="missing">The first unsatisfied requirement.</param>
        /// <param name="lotNumber">The lot being built.</param>
        public static string Sentence(NeighbourhoodFacadeRequirement missing, int lotNumber)
        {
            var what = Describe(missing.Piece);
            var because = string.IsNullOrEmpty(missing.Because) ? null : ", " + missing.Because;
            return "lot " + lotNumber + ": " + what + because;
        }

        /// <summary>
        /// The failure sentence for a lot whose pipeline threw: which step, which lot, and the
        /// throw itself.
        /// </summary>
        /// <remarks>
        /// A bare <c>NullReferenceException</c> names a type and says nothing else, and "which of
        /// the four calls" is the only question worth answering. Every stage of the pipeline sets
        /// <paramref name="stage"/> before it runs, so this can always say more than
        /// <c>ex.Message</c> could - and the preconditions above it mean the residual cases are
        /// the ones inside the lot view, which the stage names exactly.
        /// </remarks>
        /// <param name="lotNumber">The lot being built.</param>
        /// <param name="stage">What the pipeline was doing, in a clause.</param>
        /// <param name="ex">What it threw.</param>
        public static string StageFailure(int lotNumber, string stage, Exception ex)
        {
            var what = string.IsNullOrEmpty(stage) ? "building the facade" : stage;
            var thrown = DescribeException(ex);
            return "while " + what + " for lot " + lotNumber + ": " + thrown;
        }

        /// <summary>
        /// A throw's type and message, as a clause. <c>ex.Message</c> alone was what the pass used
        /// to record, and for the failures that actually happen - an index out of range in a house
        /// file's wall lines, an unreadable render target - the message alone is either empty or
        /// says nothing about which step failed.
        /// </summary>
        /// <param name="ex">The throw.</param>
        public static string DescribeException(Exception ex)
        {
            if (ex == null) return "the lot's facade could not be built, for an unknown reason";
            var message = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
            return ex.GetType().Name + ": " + message;
        }
    }

    /// <summary>
    /// Builds one lot's facade: stands a throwaway <c>World</c> and <c>VM</c> up for the lot,
    /// loads its <c>HouseNN.iff</c> through <c>VMTS1Activator</c>, lets
    /// <c>LotFacadeGenerator</c> render its walls, roof and floor, and places the result in the
    /// generated world.
    ///
    /// <para><b>This is the OBJ exporter's pipeline, unchanged.</b>
    /// <c>SimitoneNeighOBJExporter.SaveOBJ</c> already does exactly this per lot, and it is the
    /// only proven code in the tree that turns a TS1 house into a facade. Copying it rather than
    /// reinventing it is deliberate: the sequence (world into the scene graph, VM over it,
    /// activator, facade generate, time of day forced, lighting prepared, world removed) is
    /// load-bearing in ways that are easy to get subtly wrong, and the exporter is the only place
    /// in the tree where it is known to work.
    /// </para>
    ///
    /// <para><b>What is different, and why.</b> Two things. The headline provider is
    /// <c>VMNullHeadlineProvider</c> rather than the client UI's, because a facade render wants no
    /// money balloons and the UI provider is in <c>FSO.Client</c>, which this project cannot
    /// reference. And the three surfaces come back in lot-local tile coordinates, so they are
    /// translated and rotated into the generated world by
    /// <see cref="NeighbourhoodFacadeTransform"/> on the way out rather than by an ad-hoc offset
    /// computed here - the exporter computes a <c>voff/3</c> to place an obj in a modelling
    /// package, which is not a placement in a game world.
    /// </para>
    /// <para><b>Never throws, and never says nothing.</b> Every step of the pipeline is inside one
    /// try, and a failure becomes a sentence for the log. A neighbourhood with one unreadable house
    /// file has to open with the rest of its houses drawn. And the sentence is never a bare
    /// <c>NullReferenceException</c>: the four chunks of the house file the TS1 activator
    /// dereferences without checking, the lot view's shared render effect, the blueprint's roof
    /// and terrain components and the throwaway world's own state are all checked by name before
    /// they are used - <see cref="Checklist"/> and <see cref="BlueprintChecklist"/> - and anything
    /// that does throw anyway is reported with the step it threw in, by
    /// <see cref="NeighbourhoodFacadeDiagnostics.StageFailure"/>.
    /// </para>
    /// <para><b>The roof is a pass, not a requirement.</b> The three passes into the lot view are
    /// run one at a time rather than through <c>LotFacadeGenerator.Generate</c>, so a throw names
    /// the call it came out of rather than a stage that was all three, and so a lot that cannot run
    /// one of them runs the other two - see
    /// <see cref="NeighbourhoodFacadeDiagnostics.RequiredStages"/>. That distinction is the whole
    /// of the bug this pass closed: a house with rooms on the ground floor and nothing built over
    /// them has no meshed roof storey at all, and reading <c>MeshRectData</c>'s null for it used to
    /// throw away the walls and the floor of every ordinary house in the neighbourhood.
    /// </para>
    /// </summary>
    public static class NeighbourhoodFacadeBuilder
    {
        /// <summary>
        /// Time of day forced while the facade renders. Noon, because that is the only time of day
        /// whose lighting is a constant: anything else would make the facade depend on a wall-clock
        /// time a generated neighbourhood has no business having.
        /// </summary>
        public const float FacadeTimeOfDay = 0.5f;

        /// <summary>
        /// The highest storey the facade draws light for, counted from the ground floor: every cell
        /// in the light map's own 3x2 layout, which is the same bound
        /// <c>LMapBatch.ParseInvalidated</c> passes to <c>RedrawAll</c>.
        /// </summary>
        /// <remarks>
        /// <b>Written down rather than trimmed, and the trimming was tried.</b> A storey above this
        /// draws with no light map of its own, and the facade's wall pass does draw every storey the
        /// geometry has - <c>WallPassPlan.LevelFor</c> clamps to the component's storey count, which
        /// is five for every lot the TS1 activator builds - so a lower number here is not a saving,
        /// it is the top of a five storey house coming back unlit. The saving is
        /// <see cref="FacadeShadowDetail"/> and not the number of storeys.
        /// </remarks>
        public const int FacadeLightingFloors = 6;

        /// <summary>
        /// The shadow detail a facade's throwaway lot is built at, which is the lowest the engine
        /// allows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every pixel the light map has is a pixel nothing will ever look at.</b> The light map
        /// is a throwaway lot's, sampled by three render passes into an atlas that is a few dozen
        /// pixels tall per storey, and its resolution is
        /// <see cref="LightMapResolutionFor"/> - so at the default setting of 16 a sixty tile lot
        /// builds a 2088x1392 light map and a 464x464 shadow map to shade a house that is about
        /// sixty pixels tall on screen. At the minimum the same lot is 348x232, which is still
        /// several times the size of the house it lights.
        /// </para>
        /// <para>
        /// <b>The previous ceiling was the player's own setting, and that is where the seconds
        /// went.</b> <see cref="ShadowDetailFor"/> walks the setting <em>downwards</em> looking for
        /// one whose targets fit the device, so on any machine with a modern render-target limit it
        /// returned the setting unchanged and lowered nothing - correct for a check about fitting,
        /// and no help at all for a check about time. The light map pass that follows is fill-bound,
        /// so its cost scales with the square of this number: the default is sixteen times the area
        /// of the minimum.
        /// </para>
        /// <para>
        /// The engine's own minimum rather than a number chosen here, because
        /// <c>WorldConfig.ShadowDetail</c>'s setter clamps to it and a search that went below would
        /// be handed back a value it had asked not to have. See <see cref="ShadowDetailFor"/>, which
        /// still answers "does it fit at this ceiling".
        /// </para>
        /// </remarks>
        public static int FacadeShadowDetail
        {
            get
            {
                return WorldConfig.MinShadowDetail;
            }
        }

        /// <summary>How many pixels of floor texture per lot tile.</summary>
        public const int FloorResolutionPerTile = 2;

        /// <summary>
        /// The number of floor tiles across the rendered floor. The lot view uses 64; the
        /// generator is told the lot's own width instead, so a small lot does not spend a 64-tile
        /// render target on itself.
        /// </summary>
        public const int DefaultFloorTiles = 64;

        /// <summary>How finely the ground under the walls is subdivided.</summary>
        public const int DefaultGroundSubdiv = 10;

        /// <summary>
        /// How many pixels per wall tile the generator renders at, and then halves.
        ///
        /// <para><b>Why a neighbourhood's facades are built at one rather than the retail
        /// default of two.</b> <c>LotFacadeGenerator.SUPERSAMPLE_COUNT</c> is the single biggest
        /// cost in the wall pass and it is quadratic in the thing that matters: the wall render
        /// target is <c>MAX_WALL_WIDTH * WALL_WIDTH * SUPERSAMPLE_COUNT</c> pixels wide and
        /// <c>CeilToFour(wallBins * 24) * SUPERSAMPLE_COUNT</c> tall, so doubling it does not
        /// double the cost of one wall, it quadruples the area of the target and then runs
        /// <c>TextureUtils.Decimate</c> over every pixel of it - which is a full
        /// GPU-to-CPU readback followed by a managed average over four samples per output pixel
        /// and a fresh upload. A forty-bin house renders a 1024x1920 target and averages two
        /// million pixels, and then <c>FinishWalls</c> reads and writes the halved result again.
        /// The floor pass does the same thing to a smaller target.</para>
        ///
        /// <para>At one there is no supersample to remove, so <c>Decimate</c> is skipped entirely
        /// by the generator's own <c>SUPERSAMPLE_COUNT &gt; 1</c> guard: a quarter of the pixels,
        /// one readback instead of three, and no managed per-pixel loop. The cost of that is a
        /// wall atlas rendered at its final resolution rather than averaged down to it, which is
        /// visible only when a facade fills the screen - and in a neighbourhood overview it is a
        /// house a few dozen pixels tall. This is a neighbourhood-view setting, deliberately
        /// applied here and restored afterwards rather than left changed for the OBJ exporter.</para>
        ///
        /// <para><b>It is set, and put back.</b> <c>SUPERSAMPLE_COUNT</c> is a
        /// <see langword="static"/> on a class this project does not own, and the honest options
        /// are to set it around our own call or to leave it alone. Set and restore: the value is
        /// only read inside the three generator passes, all of which run inside the using below,
        /// and the exporter - the only other caller - is an offline tool that is not running.</para>
        /// </summary>
        public const int FacadeSupersample = 1;

        /// <summary>
        /// The divisor the wall atlas is drawn at for a neighbourhood facade, and the second half
        /// of what makes a build affordable.
        ///
        /// <para><b>Why a neighbourhood does not need the retail 8 pixels per wall tile.</b> A
        /// neighbourhood facade is a low-detail silhouette seen from above. The generated
        /// neighbourhood draws a whole 64 tile lot as a house a few dozen pixels tall, so the
        /// atlas it samples is resolved several times past what the screen can show - and every
        /// part of the wall pass is proportional to the atlas's area rather than to the house:
        /// the render target it allocates and clears, the pixels the device rasterises, the
        /// <c>GetData</c> in <c>ReadTarget</c> that waits for the whole pass's queued draws and
        /// copies every pixel back, the managed per-cell edge bleed over that buffer, and the
        /// upload of the result.</para>
        ///
        /// <para>Two is deliberately not four. At two the atlas is four times smaller, the cells
        /// are four pixels per wall tile, and a two storey house is twenty two pixels of atlas
        /// tall - which is still more than it is on screen. At four a storey would be five pixels
        /// and the walls of a whole house would be visibly soft, and this constant is the one
        /// thing here that is a judgement about how a house looks rather than about how long it
        /// takes. The cost of being wrong in that direction is a slightly blurry house; the cost
        /// of being wrong the other way is a neighbourhood with one house in it.</para>
        ///
        /// <para>Set and put back around the three passes, for the same reason and with the same
        /// reasoning as <see cref="FacadeSupersample"/>: this is a
        /// <see langword="static"/> on a class the OBJ exporter shares, and the exporter wants the
        /// retail size. See <c>LotFacadeGenerator.WALL_TARGET_SCALE</c> for the arithmetic and
        /// <c>WallAtlasPixels</c> for the number it produces.</para>
        /// </summary>
        public const int FacadeWallTargetScale = 2;

        //The stages of the pipeline, in the order they run. Set on the way into each one so that
        //whatever throws can say which of the calls into the lot view it came out of, which is the
        //only thing a NullReferenceException out of there does not already say.
        private const string StageHouseFile = "reading the lot's house file";
        private const string StageWorld = "standing the throwaway lot world up";
        private const string StageLoad = "loading the lot's house file into the throwaway lot";
        private const string StageLighting = "preparing the lot's lighting";
        private const string StageWalls = "rendering the lot's walls";
        private const string StageFloor = "rendering the lot's floor";
        private const string StageRoof = "rendering the lot's roof";
        private const string StageAssemble = "placing the lot's facade into the generated world";

        /// <summary>
        /// The shortest <c>SIMI.GlobalData</c> the TS1 loader can read.
        /// </summary>
        /// <remarks>
        /// Not a guess about the format. <c>VMTS1Activator.LoadFromIff</c> indexes
        /// <c>GlobalData[35]</c> for the game edition and <c>GlobalData[23]</c> for the lot size,
        /// clones the array as the VM's global state and then writes indices 25 and 17 into it, so
        /// anything shorter than 36 is a file the loader cannot read. A retail house file has 38.
        /// Checking it here turns an <c>IndexOutOfRangeException</c> from inside the activator into
        /// a sentence about the file that caused it.
        /// </remarks>
        public const int MinimumSimulationGlobals = 36;

        /// <summary>Where in <c>SIMI.GlobalData</c> the lot's own size in tiles lives.</summary>
        public const int LotSizeGlobalIndex = 23;

        /// <summary>
        /// The smallest lot size, in tiles, the throwaway lot can be stood up for.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the check that turns a five-second crash into a free one.</b>
        /// <c>VMTS1Activator.LoadFromIff</c> reads the lot's size out of
        /// <c>simi.GlobalData[23]</c> with no check of its own, and that number is the width of
        /// the lot view's own light map: <c>LMapBatch.Init</c> sizes its render targets as
        /// <c>resPerTile * (Width - borderSize)</c> with <c>borderSize</c> of 1. A file that says
        /// its lot is 1 tile wide therefore asks D3D for a <b>zero-pixel-wide</b> render target,
        /// and D3D answers a zero-sized texture description with
        /// <c>SharpDXException: E_INVALIDARG</c> - which is the exact throw two lots in a
        /// forty-one lot neighbourhood were dying of, after a whole <c>World</c>, a whole
        /// <c>VM</c> and a whole house-file load had already been paid for.</para>
        ///
        /// <para>Two is the smallest size that leaves the lot view a pixel to work with, and it is
        /// derived rather than chosen: retail TS1 lots are 64, so every genuine house file is far
        /// above it, and a file below it is one this loader cannot draw rather than one this
        /// loader is being awkward about.</para>
        /// </remarks>
        public const int MinimumLotSize = 2;

        /// <summary>
        /// The largest lot size, in tiles, the throwaway lot can be stood up for.
        /// </summary>
        /// <remarks>
        /// The same arithmetic at the other end, and the same class of failure. A hand-edited
        /// <c>SIMI</c> that claims 4000 tiles asks for a light map of
        /// <c>16 * 3999 * 3</c> pixels across, which is past what the device will allocate and
        /// fails the same way; and the floor pass would be asked for a render target of
        /// <c>2 * 4000 * 3 * 2</c> across for the same lot. Retail is 64, and the largest size any
        /// TS1 content uses is 64, so this is generous by a factor of eight.
        ///
        /// <para><b>It is not the whole of the upper bound, and deliberately so.</b> A lot inside
        /// this range can still be one the device cannot light: 64 tiles at a shadow detail of 24
        /// is 4536 pixels of light map, and the machine this was reported on holds 4096. That is
        /// not a range problem - the file is a real retail-sized house - so it is answered by
        /// lowering the shadow detail rather than by refusing the file. See
        /// <see cref="ShadowDetailFor"/>, which is asked first and refuses only what it cannot
        /// answer.</para>
        /// </remarks>
        public const int MaximumLotSize = 512;

        /// <summary>
        /// <c>LMapBatch.borderSize</c>: the light map is one tile narrower than the lot on each
        /// side, so a lot of one tile is a light map of no tiles at all.
        /// </summary>
        public const int LotViewLightMapBorder = 1;

        /// <summary>
        /// The light map is three tiles of shadow-map resolution across and two down -
        /// <c>LightMap = new RenderTarget2D(gd, wl * 3, wh * 2)</c> - so the width is the axis the
        /// lot size runs out of first, by a factor of three.
        /// </summary>
        public const int LightMapWidthTiles = 3;

        /// <summary>The light map's height multiplier. See <see cref="LightMapWidthTiles"/>.</summary>
        public const int LightMapHeightTiles = 2;

        /// <summary>
        /// The directional light map is not scaled by the shadow detail at all -
        /// <c>(Width - 1) * 3 * 4</c> pixels across - so a lot can be too wide for it while the
        /// light map itself still fits, and a check that only looked at the light map would miss
        /// that. Four bytes per pixel, in each of the three tile-widths and two tile-heights of
        /// the atlas.
        /// </summary>
        public const int DirectionalLightMapWidthPixels = 12;

        /// <summary>The directional light map's height multiplier. See <see cref="DirectionalLightMapWidthPixels"/>.</summary>
        public const int DirectionalLightMapHeightPixels = 8;

        /// <summary>
        /// The shadow-map resolution, in pixels per tile, the lot view will use.
        /// </summary>
        /// <remarks>
        /// <c>LMapBatch.Init</c>'s own first line of arithmetic: the ultra lighting setting takes
        /// the whole setting and the other half takes half of it, by integer division - which is
        /// why this is not simply "the setting, halved": an odd setting halves downwards, and a
        /// setting of one halves to nothing, which is a zero-pixel target and the same
        /// <c>E_INVALIDARG</c> this whole check exists to refuse.
        /// </remarks>
        /// <param name="shadowDetail">The shadow-detail setting the light batch is built with.</param>
        /// <param name="ultraLighting">Whether the ultra lighting mode is on.</param>
        public static int LightMapResolutionFor(int shadowDetail, bool ultraLighting)
        {
            return ultraLighting ? shadowDetail : shadowDetail / 2;
        }

        /// <summary>
        /// Whether every render target the lot view will build for a lot of this size fits inside
        /// what the device accepts.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the E_INVALIDARG, and it is arithmetic rather than a guess.</b>
        /// <c>LMapBatch.Init</c> builds four render targets from a lot's tile count: the light map
        /// at <c>(wl * 3) x (wh * 2)</c>, the shadow map at <c>wl x wh</c>, the object shadow map
        /// at the same size doubled when ultra lighting is on, and - with directional lighting -
        /// the directional light map at <c>(w - 1) * 12</c> across. None of them is clamped
        /// against the device's limit, and a description past that limit is answered by D3D with
        /// <c>SharpDXException: E_INVALIDARG</c> rather than by a smaller texture.</para>
        ///
        /// <para><b>The number that makes it a real bug on a real lot.</b> A D3D device that caps
        /// render targets at 4096 in a dimension is common, and the game logs its own screen
        /// target being clamped to exactly that. A 60 tile lot at the default 24 pixels of shadow
        /// detail is a 4248 pixel wide light map; a 56 tile lot is 3960 and is fine. The two lots
        /// that could not be built in a forty-one lot neighbourhood were the two whose house files
        /// declare 60 tiles, and nothing about their furniture, their roofs or their walls was
        /// wrong.</para>
        ///
        /// <para>Pure, so it can be pinned without a device: the limit arrives as a
        /// <see cref="Point"/> and the whole rule is multiplication and comparison.</para>
        /// </remarks>
        /// <param name="lotSize">The lot's declared size, in tiles.</param>
        /// <param name="shadowDetail">The shadow-detail setting the light batch is built with.</param>
        /// <param name="ultraLighting">Whether the ultra lighting mode is on.</param>
        /// <param name="directionalLighting">Whether directional lighting is on.</param>
        /// <param name="maxTarget">
        /// The largest render target dimension the device accepts, or a non-positive point when
        /// nothing has been asked - in which case there is no limit to check against and the lot
        /// is left alone.
        /// </param>
        public static bool LightMapFits(int lotSize, int shadowDetail, bool ultraLighting,
            bool directionalLighting, Point maxTarget)
        {
            //no device, no limit, and therefore no opinion: the checklist's own sentences for a
            //missing device are better than anything this could say about a lot size
            if (maxTarget.X < 1 || maxTarget.Y < 1) return true;
            var resolution = LightMapResolutionFor(shadowDetail, ultraLighting);
            var side = (lotSize - LotViewLightMapBorder) * resolution;
            //a side of no pixels is a zero-sized render target, which is the refusal itself
            if (side < 1) return false;
            var width = side * LightMapWidthTiles;
            var height = side * LightMapHeightTiles;
            //the object shadow map is the light map's own resolution, doubled, with ultra on
            if (ultraLighting)
            {
                width = Math.Max(width, side * 2);
                height = Math.Max(height, side * 2);
            }
            //and the directional atlas is sized from the lot alone, so it can outrun the rest
            if (directionalLighting)
            {
                width = Math.Max(width, (lotSize - LotViewLightMapBorder) * DirectionalLightMapWidthPixels);
                height = Math.Max(height, (lotSize - LotViewLightMapBorder) * DirectionalLightMapHeightPixels);
            }
            return width <= maxTarget.X && height <= maxTarget.Y;
        }

        /// <summary>
        /// The largest shadow detail, at or below the one the player has chosen, whose light map
        /// fits inside what this device accepts - or 0 when not even the smallest one does.
        /// </summary>
        /// <remarks>
        /// <para><b>The answer, where the previous pass only had the refusal.</b> The old check
        /// compared the declared lot size against a fixed range and skipped a 60 tile lot, which
        /// is a real retail-sized house file and skips a house out of a neighbourhood for a
        /// reason that is not the house's. This walks the setting downwards instead and hands
        /// back the first one that fits, so the same lot is built - with a coarser shadow map, in
        /// a throwaway world whose light map nothing ever looks at.</para>
        ///
        /// <para><b>The floor is the engine's, not a number chosen here.</b>
        /// <c>WorldConfig.ShadowDetail</c>'s setter clamps to
        /// <c>[MinShadowDetail, MaxShadowDetail]</c>, so a search that went below the minimum
        /// would be answered by a value the setter raises again - and that raised value might not
        /// fit, which is the bug this is here to remove. Zero therefore means "refuse", and the
        /// refusal is a free array read away in <see cref="HouseFileRefusal"/>.</para>
        /// </remarks>
        /// <param name="lotSize">The lot's declared size, in tiles.</param>
        /// <param name="shadowDetail">The shadow-detail setting the player has chosen.</param>
        /// <param name="ultraLighting">Whether the ultra lighting mode is on.</param>
        /// <param name="directionalLighting">Whether directional lighting is on.</param>
        /// <param name="maxTarget">The largest render target dimension the device accepts.</param>
        public static int ShadowDetailFor(int lotSize, int shadowDetail, bool ultraLighting,
            bool directionalLighting, Point maxTarget)
        {
            if (maxTarget.X < 1 || maxTarget.Y < 1) return shadowDetail;
            var top = Math.Min(shadowDetail, WorldConfig.MaxShadowDetail);
            for (var detail = top; detail >= WorldConfig.MinShadowDetail; detail--)
            {
                if (LightMapFits(lotSize, detail, ultraLighting, directionalLighting, maxTarget)) return detail;
            }
            return 0;
        }

        /// <summary>
        /// The largest render target dimension this device accepts, on each axis, or a
        /// non-positive point when there is no device to ask.
        /// </summary>
        /// <remarks>
        /// Asked of the engine's own probe rather than guessed here, because the answer is a
        /// property of the machine the game is on and it is already cached for the process: the
        /// same question the screen target is clamped against, which is how a log line saying
        /// "clamped to 4096x4096, which is the largest this graphics device accepts" and this
        /// check can never disagree.
        ///
        /// <para>Fully qualified on purpose. <c>FSO.Common.Utils</c> carries a dozen names the
        /// rest of this file already has a different meaning for, and one <c>using</c> for a
        /// single call is how that ambiguity gets introduced.</para>
        /// </remarks>
        /// <param name="device">The graphics device, or <c>null</c>.</param>
        public static Point DeviceMaxTarget(GraphicsDevice device)
        {
            if (device == null) return Point.Zero;
            return FSO.Common.Utils.PPXDepthEngine.ClampToDevice(device, int.MaxValue, int.MaxValue);
        }

        /// <summary>
        /// The lot's own size in tiles, as its house file declares it, or 0 when the file cannot
        /// be asked.
        /// </summary>
        /// <remarks>
        /// Index 23 of the <c>SIMI</c> globals, which is the word
        /// <c>VMTS1Activator.LoadFromIff</c> reads into its <c>Size</c> field and then sizes the
        /// whole lot view from. A short array, a missing chunk and a null file all read as 0,
        /// which is below <see cref="MinimumLotSize"/> and therefore refused - the file is
        /// unreadable either way, and refusing it here is free.
        /// </remarks>
        /// <param name="house">The lot's <c>HouseNN.iff</c>, or <c>null</c>.</param>
        public static int LotSizeFor(IffFile house)
        {
            var simi = house?.Get<SIMI>(1);
            var globals = simi?.GlobalData;
            if (globals == null || globals.Length <= LotSizeGlobalIndex) return 0;
            return globals[LotSizeGlobalIndex];
        }

        /// <summary>
        /// Whether the throwaway lot can be stood up for a house file of this size at all, judged
        /// against the fixed range only.
        /// </summary>
        /// <remarks>
        /// The range, and nothing about this machine. A size inside it can still be one this
        /// device cannot light - that is <see cref="LightMapFits"/>, and it needs a device to
        /// ask - so a caller with a device in hand wants the two checks rather than this one.
        /// </remarks>
        /// <param name="lotSize">The lot's declared size, in tiles.</param>
        public static bool LotSizeIsUsable(int lotSize)
        {
            return lotSize >= MinimumLotSize && lotSize <= MaximumLotSize;
        }

        /// <summary>
        /// The house file checks, in the order they are worth doing: the three that are free and
        /// that no amount of retrying will change, before a world and a VM are stood up for.
        /// </summary>
        /// <remarks>
        /// <para>Separate from <see cref="Checklist"/> on purpose, and the separation is the point.
        /// The checklist's entries all describe things that are either present or not; this one
        /// describes a file that is <em>readable but undrawable</em>, and a house file with a
        /// nonsensical lot size in it is readable, parses, has all four of the chunks the
        /// activator dereferences, and still cannot be rendered. Putting it in the checklist
        /// would work; keeping it out makes the ordering the reason it is there rather than a
        /// position in a list - a check that is free must run before the thing that costs
        /// seconds, and every later check is behind the world.</para>
        /// </remarks>
        /// <param name="house">The lot's <c>HouseNN.iff</c>, or <c>null</c>.</param>
        /// <param name="lotNumber">The lot being built.</param>
        /// <returns>A reason, or <c>null</c> when the file is worth loading.</returns>
        public static string HouseFileRefusal(IffFile house, int lotNumber)
        {
            return HouseFileRefusal(house, lotNumber, Point.Zero);
        }

        /// <summary>
        /// <see cref="HouseFileRefusal(IffFile, int)"/> with the device's own render target
        /// limit in hand, which is what catches a lot that is a real size and still cannot be lit.
        /// </summary>
        /// <remarks>
        /// <para><b>Asked in the order that answers as much as it can.</b> The range first, because
        /// a size of one tile is refused whatever the device can do. Then the light map, and
        /// <b>only</b> if <see cref="ShadowDetailFor"/> cannot be brought down far enough to make
        /// it fit - the whole point of asking that first is that a 60 tile lot on a 4096 device is
        /// answered, not skipped. What arrives here as a refusal is a lot no shadow detail this
        /// engine will accept can light, which on this machine means a lot of more than 342 tiles
        /// with ultra lighting on.</para>
        /// <para>A zero <paramref name="maxTarget"/> means no device was available, and
        /// the light map half then has no opinion: the checklist's sentences about a missing
        /// device are better ones.</para>
        /// </remarks>
        /// <param name="house">The lot's <c>HouseNN.iff</c>, or <c>null</c>.</param>
        /// <param name="lotNumber">The lot being built.</param>
        /// <param name="maxTarget">The largest render target dimension the device accepts.</param>
        /// <returns>A reason, or <c>null</c> when the file is worth loading.</returns>
        public static string HouseFileRefusal(IffFile house, int lotNumber, Point maxTarget)
        {
            //a file with no usable SIMI chunk is already named by the checklist, with a better
            //sentence than this one could write, so this is only about the files it accepts and
            //the lot view still cannot draw
            if (!HasSimulationChunk(house)) return null;
            var size = LotSizeFor(house);
            if (!LotSizeIsUsable(size))
            {
                return NeighbourhoodFacadeDiagnostics.Sentence(new NeighbourhoodFacadeRequirement(
                    NeighbourhoodFacadePiece.LotSize, false,
                    "the lot view sizes its light map as (size - 1) pixels, so a lot of "
                        + size + " tile" + (size == 1 ? "" : "s") + " asks the graphics device for a"
                        + " render target it cannot allocate"), lotNumber);
            }
            //in range. The remaining question is whether this device can hold the light map for a
            //lot this size, and the answer is the lowest shadow detail that fits - so the question
            //is only a refusal when even the lowest one does not fit.
            var config = WorldConfig.Current;
            var detail = ShadowDetailFor(size, config.ShadowDetail, config.UltraLighting,
                config.Directional, maxTarget);
            if (detail > 0) return null;
            //the sentence has to carry the arithmetic that refused it, and the arithmetic is the
            //lot's own tile count multiplied by the resolution the lot view will actually use -
            //which is half the setting when ultra lighting is off, so it is asked for rather than
            //written down twice
            var resolution = LightMapResolutionFor(config.ShadowDetail, config.UltraLighting);
            return NeighbourhoodFacadeDiagnostics.Sentence(new NeighbourhoodFacadeRequirement(
                NeighbourhoodFacadePiece.LotSize, false,
                "at " + size + " tiles and a shadow detail of " + config.ShadowDetail + " the lot"
                    + " view's light map is " + (size - LotViewLightMapBorder) + " x " + resolution
                    + " x " + LightMapWidthTiles + " = "
                    + ((size - LotViewLightMapBorder) * resolution * LightMapWidthTiles)
                    + " pixels across, and this graphics device accepts at most " + maxTarget.X
                    + " - not even the smallest shadow detail this engine will take brings it"
                    + " inside that"), lotNumber);
        }

        /// <summary>
        /// Everything a lot is asked before a throwaway world is stood up for it, in the order it
        /// is asked: a reason, or <c>null</c> when the lot is worth loading.
        /// </summary>
        /// <remarks>
        /// <para>Its own method so that the <em>order</em> is a thing a test can call.</para>
        /// <para>
        /// The order is the whole of this check, and it is not a detail: <see cref="HouseFileRefusal"/>
        /// is a couple of array reads on a file that is already in memory, and the first entry of
        /// <see cref="Checklist"/> asks whether there is a graphics device and a scene layer at all.
        /// Every lot the two of them can refuse costs a house-file read and nothing else; every lot
        /// that gets past them has already paid for a <c>World</c>, a <c>VM</c> and a whole
        /// <c>HouseNN.iff</c> load. So a lot whose <c>SIMI</c> names a size the lot view cannot be
        /// sized for is refused here, and that is where the E_INVALIDARG question is asked of the
        /// two lots in a forty-one lot neighbourhood that were dying of it seconds after it looked
        /// like they were dying of a graphics driver.
        /// </para>
        /// <para><b>Asked here does not mean refused here.</b> One of the two was a lot size of
        /// zero or one tile and is refused; the other was a real 60 tile house whose light map no
        /// device on this machine would allocate, and the check answers that by naming the shadow
        /// detail the lot will be loaded at rather than by skipping the lot. What arrives here as
        /// a reason is a lot that is too big at <em>every</em> detail the engine will take.</para>
        /// </remarks>
        /// <param name="device">The graphics device, or <c>null</c>.</param>
        /// <param name="sceneLayer">The 3D scene layer, or <c>null</c>.</param>
        /// <param name="provider">The content manager, or <c>null</c>.</param>
        /// <param name="house">The lot's <c>HouseNN.iff</c>, or <c>null</c>.</param>
        /// <param name="lotNumber">The lot being built.</param>
        /// <returns>A reason, or <c>null</c> when the file is worth loading.</returns>
        public static string PreFlight(
            GraphicsDevice device, _3DLayer sceneLayer, Content provider, IffFile house, int lotNumber)
        {
            //the free check on the file, first: it is a pair of array reads and it is the one that
            //catches the lot size the lot view would turn into a render target the device will
            //not take. Asked with the device's own limit, so the lot-size half of the answer is
            //asked to solve the problem rather than to report it - see ShadowDetailFor.
            var refusal = HouseFileRefusal(house, lotNumber, DeviceMaxTarget(device));
            if (refusal != null) return refusal;
            //and only then the checklist, whose first two entries are about the device rather than
            //about the file
            var missing = NeighbourhoodFacadeDiagnostics.FirstMissing(
                Checklist(device, sceneLayer, provider, house));
            return missing == null
                ? null
                : NeighbourhoodFacadeDiagnostics.Sentence(missing.Value, lotNumber);
        }

        /// <summary>
        /// Builds one lot's facade, or returns <c>null</c> with the reason.
        ///
        /// <para><b>The reason always names a cause.</b> Either it names the reference that was
        /// null, on this lot, or it names the step of the pipeline that threw. The pass used to
        /// hand the log a bare <c>NullReferenceException: Object reference not set to an instance
        /// of an object</c> and nothing else, which is three calls into the lot view and no closer
        /// to an answer than not looking at all. See <see cref="NeighbourhoodFacadeDiagnostics"/>
        /// for the sentences, and <see cref="Checklist"/> and <see cref="BlueprintChecklist"/> for
        /// the lists of references that are checked before they are used.</para>
        /// <para><b>Not every missing reference is a reason.</b> A lot whose roof pass found no
        /// storey to mesh, or whose blueprint has no roof component at all, is built from its
        /// walls and its floor and is not logged at all; the missing pass is remembered and only
        /// said out loud if nothing came back - see
        /// <see cref="NeighbourhoodFacadeDiagnostics.Absent"/>.</para>
        /// </summary>
        /// <param name="device">The graphics device to render onto.</param>
        /// <param name="lot">The lot, which must have a house.</param>
        /// <param name="map">The generated map, for the lot's ground height.</param>
        /// <param name="reason">Why it could not be built, when it could not be.</param>
        public static NeighbourhoodFacade TryBuild(
            GraphicsDevice device, NeighbourhoodLot lot, NeighbourhoodMap map, out string reason)
        {
            return TryBuild(device, lot, map, out reason, out _);
        }

        /// <summary>
        /// Builds one lot's facade and says what each stage cost.
        /// </summary>
        /// <remarks>
        /// <para><b>The timings are the reason this overload exists.</b> The pass was budgeted at
        /// two meshes and ten milliseconds a frame and the neighbourhood still froze for eleven
        /// seconds, because a single build costs about five seconds and the budget was checked
        /// before a build rather than around it. Nothing in the log said so, because nothing
        /// measured it: the line said a lot had been built and the eleven seconds of silence in
        /// between were the cost, unattributed. This overload hands the caller the per-stage
        /// breakdown so the pass can log it once per lot and the next run names the stage.</para>
        ///
        /// <para>The zero-argument overload above is this one, because a caller that does not care
        /// about the cost should not have to say so.</para>
        /// </remarks>
        /// <param name="device">The graphics device to render onto.</param>
        /// <param name="lot">The lot, which must have a house.</param>
        /// <param name="map">The generated map, for the lot's ground height.</param>
        /// <param name="reason">Why it could not be built, when it could not.</param>
        /// <param name="timings">
        /// How long each stage took, whether the build succeeded or not. Never null.
        /// </param>
        public static NeighbourhoodFacade TryBuild(
            GraphicsDevice device, NeighbourhoodLot lot, NeighbourhoodMap map,
            out string reason, out NeighbourhoodFacadeTimings timings)
        {
            reason = null;
            //The stopwatch runs across the whole method, including the throwaway world's teardown,
            //so that "total" is what the game thread actually lost rather than the sum of the
            //stages it managed to attribute.
            var clock = Stopwatch.StartNew();
            //named for the array rather than for the pipeline, because "stages" inside the try
            //block is NeighbourhoodFacadeStages and the two are not the same thing
            var stageMs = NeighbourhoodFacadeTimings.NewStages();
            timings = NeighbourhoodFacadeTimings.None;
            long mark = clock.ElapsedTicks;
            //charges the time since the previous mark to one stage and re-marks
            Action<NeighbourhoodFacadeStage> charge = stage =>
            {
                var now = clock.ElapsedTicks;
                //into the slot that stage owns. This used to index the array with the stage's own
                //enum value, which is one past the last slot for Assemble - and Assemble is charged
                //immediately after the facade has been built and placed, so every lot in the
                //neighbourhood died of an IndexOutOfRangeException in the timing bookkeeping with
                //a finished mesh sitting on the GPU. See NeighbourhoodFacadeTimings.SlotFor.
                NeighbourhoodFacadeTimings.TryCharge(
                    stageMs, stage, (now - mark) * 1000d / Stopwatch.Frequency);
                mark = now;
            };

            //No device check here: the checklist below names it, and a checklist whose first entry
            //is decided by an early return above is a checklist that can only ever be right.
            if (lot == null) { reason = "there is no lot"; return null; }
            if (!lot.HasHouse)
            {
                reason = "lot " + lot.Number + ": the lot has no house file";
                return null;
            }

            var stage = StageHouseFile;
            World world = null;
            VM vm = null;
            Blueprint blueprint = null;
            LotFacadeGenerator generator = null;
            NeighbourhoodFacade facade = null;
            try
            {
                //Everything the pipeline touches, checked by name before any of it is used, in the
                //order the pipeline would have failed in. A checklist rather than a run of early
                //returns, so one ordering rule decides both which cause is reported and which
                //throw path reports it.
                var provider = Content.Get();
                var sceneLayer = GameFacade.Scenes;
                IffFile house = null;
                string houseReadFailure = null;
                if (provider != null && provider.Neighborhood != null)
                {
                    try
                    {
                        house = provider.Neighborhood.GetHouse(lot.Number);
                    }
                    catch (Exception ex)
                    {
                        //a named failure rather than a bare one: which file, and why it would not open
                        houseReadFailure = "the house file could not be read: " + ex.Message;
                    }
                }
                charge(NeighbourhoodFacadeStage.HouseFile);
                //Free, and before the world exists. Everything below this line has already paid
                //for a World, a VM and a whole house-file load, so a file this pipeline cannot
                //draw is refused here rather than discovered as a device exception five seconds
                //later. See MinimumLotSize for what "cannot draw" means, and PreFlight for the
                //order these two checks run in.
                var preflight = PreFlight(device, sceneLayer, provider, house, lot.Number);
                if (preflight != null)
                {
                    reason = preflight;
                    return null;
                }
                if (houseReadFailure != null)
                {
                    //everything above passed, so the provider is there and the file is a house
                    //file; the file itself would not open
                    reason = "lot " + lot.Number + ": " + houseReadFailure;
                    return null;
                }
                stage = StageWorld;
                //The world and the house-file load inside the lighting budget. That window is the
                //whole extent of it, and it is exactly right: the light map is built once, in
                //LMapBatch.Init, which runs inside World.InitBlueprint at the tail of the load, and
                //every render target it will ever own is sized from the setting in that one
                //constructor. Nothing downstream re-reads it - the three generator passes and the
                //lighting step both draw into targets that already exist - so the budget has to be
                //open across this window and nowhere else, and restoring the player's setting the
                //moment after cannot make this lot's light map any sharper.
                using (new FacadeLightingBudget(device, LotSizeFor(house)))
                {
                    world = new World(device);
                    world.Opacity = 1f;
                    //Add initialises the world, and only initialises it when the layer has a device.
                    //A world with no State is a world whose LoadFromIff dies on its third line, so the
                    //state is checked here rather than discovered there.
                    GameFacade.Scenes.Add(world);
                    if (world.State == null)
                    {
                        reason = NeighbourhoodFacadeDiagnostics.Sentence(
                            new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.LotWorldState, false,
                                "there is no lot view to render the walls into"),
                            lot.Number);
                        return null;
                    }

                    var link = new VMTS1GlobalLinkStub();
                    var driver = new VMServerDriver(link);
                    vm = new VM(new VMContext(world), driver, new VMNullHeadlineProvider());
                    vm.ListenBHAVChanges();
                    vm.Init();
                    vm.SetGlobalValue(11, (short)lot.Number);
                    charge(NeighbourhoodFacadeStage.World);

                    stage = StageLoad;
                    var activator = new VMTS1Activator(vm, vm.Context.World, (short)lot.Number);
                    //out of the block, because the three generator passes below are the ones that
                    //read the blueprint and they run with the player's own lighting settings
                    blueprint = activator.LoadFromIff(house);
                    charge(NeighbourhoodFacadeStage.Load);
                }
                var blueprintRequirements = BlueprintChecklist(blueprint);
                //Only a missing blueprint stops the build. A missing roof component or terrain
                //component does not: it removes that one pass and the lot is still a house. See
                //RequiredStages, and the RoofGeometry note on NeighbourhoodFacadePiece.
                var fatalBlueprint = NeighbourhoodFacadeDiagnostics.FirstFatal(blueprintRequirements);
                if (fatalBlueprint != null)
                {
                    reason = NeighbourhoodFacadeDiagnostics.Sentence(fatalBlueprint.Value, lot.Number);
                    return null;
                }
                var stages = NeighbourhoodFacadeDiagnostics.RequiredStages(blueprintRequirements);
                var absent = new List<NeighbourhoodFacadeRequirement>();
                for (int i = 0; i < blueprintRequirements.Count; i++)
                {
                    if (!blueprintRequirements[i].Present) absent.Add(blueprintRequirements[i]);
                }

                //The exporter's two lines, which this pass documented but did not do. Time of day
                //is forced to FacadeTimeOfDay and the lighting prepared before anything is
                //rasterised, so a facade is a render of a house at a fixed hour rather than one
                //that depends on what the lot's own SIMI globals happen to say.
                stage = StageLighting;
                PrepareFacadeLighting(blueprint, vm, world);
                charge(NeighbourhoodFacadeStage.Lighting);

                generator = new LotFacadeGenerator
                {
                    FLOOR_TILES = blueprint.Width > 0 ? blueprint.Width : DefaultFloorTiles,
                    GROUND_SUBDIV = DefaultGroundSubdiv,
                    FLOOR_RES_PER_TILE = FloorResolutionPerTile,
                    LotName = "lot" + lot.Number
                };
                // The three calls LotFacadeGenerator.Generate makes, run one at a time so that
                // each sets its own stage name. "while rendering the lot's walls, floor and roof"
                // was true of all three and useless for all three.
                //
                // The supersample count is lowered for the duration: it is the difference between
                // a 1024x1920 render target averaged down pixel by pixel and a 512x960 one that
                // is already the right size. See FacadeSupersample.
                //
                // The wall atlas is drawn at half its retail resolution for the same duration, and
                // for the same reason of ownership: a 64 tile lot is a few dozen pixels tall on
                // screen here, and every pixel-counted part of the wall pass - the target, the
                // rasterisation, the readback that waits for the whole pass, the managed edge
                // bleed and the upload - scales with the atlas's area rather than with the house.
                // See FacadeWallTargetScale.
                using (new FacadeRenderQuality(FacadeSupersample, FacadeWallTargetScale))
                {
                    if (NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Walls))
                    {
                        stage = StageWalls;
                        generator.GenerateWalls(device, world, blueprint, false);
                        charge(NeighbourhoodFacadeStage.Walls);
                    }
                    if (NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Floor))
                    {
                        stage = StageFloor;
                        generator.GenerateFloor(device, world, blueprint, false, blueprint.GetFloorsUsed());
                        charge(NeighbourhoodFacadeStage.Floor);
                    }
                    if (NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Roof))
                    {
                        stage = StageRoof;
                        generator.GenerateRoof(device, world, blueprint);
                        charge(NeighbourhoodFacadeStage.Roof);
                        //The roof pass ran and the house still has no roof. This is lot 64: the roof
                        //component was there, it loaded a texture, and RoofComponent.RegenRoof meshes
                        //only the storeys with rooms under them, so MeshRectData had nothing to hand
                        //back for the four storeys above a one-storey house. It is not a failure -
                        //the walls and the floor are a house - but it is the answer to a log line
                        //that used to be a NullReferenceException, and it is only knowable here.
                        if (!LotFacadeGenerator.HasRoofGeometry(generator.RoofVerts?.Length ?? 0, generator.RoofIndices?.Length ?? 0))
                        {
                            absent.Add(new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.RoofGeometry,
                                false, "every storey above the ground floor is empty, so the roof pass had"
                                    + " nothing to mesh"));
                        }
                    }
                }
                //GenerateRoof reads the texture after RegenRoof, which is what loads it, so this
                //is belt and braces rather than the fix. Still not ours to free: see Assemble.
                if (generator.RoofTexture == null) generator.RoofTexture = blueprint.RoofComp?.Texture;
                //The lot's own altitude plane, read once. The generator's heights are relative to
                //it, and the generated ground is not, so this is the correction that keeps a house
                //on a sloping plot from being buried or floating. See GroundOffsetFor.
                var ownGround = 0f;
                try
                {
                    var centre = Math.Max(1, blueprint.Width / 2);
                    ownGround = blueprint.InterpAltitude(new Vector3(centre, 0f, centre));
                }
                catch (Exception)
                {
                    //a blueprint that cannot be asked about its own altitude is a flat lot
                    ownGround = 0f;
                }
                stage = StageAssemble;
                facade = Assemble(device, lot, map, generator, ownGround, absent, out reason);
                //the facade now owns the wall and floor targets, and must not be given them back
                generator = null;
                charge(NeighbourhoodFacadeStage.Assemble);
                return facade;
            }
            catch (Exception ex)
            {
                reason = NeighbourhoodFacadeDiagnostics.StageFailure(lot.Number, stage, ex);
                return null;
            }
            finally
            {
                //Before the world goes, and not with it: the throwaway VM subscribed itself to
                //VM.OnBHAVChange, which is a static event, and unsubscribing is the only thing
                //that lets go of it. A disposed world does not get it back - the event is holding
                //the VM, the VM is holding the context, and the context is holding the world - so
                //every lot this pass built left a whole throwaway lot alive for the life of the
                //process, and every BHAV recache since has run one handler per lot. Forty-one lots
                //is forty-one ghosts; a neighbourhood visited all evening is thousands. See
                //VM.ListenBHAVChanges, which is explicit that the pair is meant to be balanced.
                try
                {
                    vm?.SuppressBHAVChanges();
                }
                catch (Exception)
                {
                    //an event this VM is not subscribed to is not a failure worth reporting
                }
                //whatever happened, the throwaway world must not stay in the scene graph, or the
                //neighbourhood view will start simulating an empty lot behind the UI
                if (world != null)
                {
                    //two tries rather than one: a Remove that throws must not skip the Dispose
                    try
                    {
                        GameFacade.Scenes.Remove(world);
                    }
                    catch (Exception)
                    {
                        //a world the layer will not let go of is a leak, not a crash
                    }
                    try
                    {
                        world.Dispose();
                    }
                    catch (Exception)
                    {
                        //a world that will not dispose is a leak, not a crash
                    }
                }
                //Only reached when the build did not hand its textures to a facade: a throw above,
                //or a null blueprint. Disposing a texture the returned facade is holding is what
                //made every house invisible while the log said it had been built, and disposing
                //RoofTexture is worse still - that one is the content system's shared roof texture
                //(RoofComponent hands out roofs.Get(name).Get(device)), so freeing it breaks every
                //other lot's roof for the rest of the session.
                if (generator != null)
                {
                    generator.WallTarget?.Dispose();
                    generator.FloorTexture?.Dispose();
                    //RoofTexture deliberately not disposed: it belongs to the content system.
                }
                //the last word on the cost, after the world has been torn down. See the note on
                //TotalMs: this is the number the game thread actually lost.
                timings = Finish(clock, stageMs);
            }
        }

        /// <summary>
        /// Stops the clock and returns the timings, with the whole-build total filled in.
        /// </summary>
        /// <remarks>
        /// Called from the <c>finally</c> and nowhere else, so every exit from the build - the
        /// early refusals, a throw, and the one success - is measured by the same line and gets
        /// the same total. The total is taken after the throwaway world has been removed and
        /// disposed, because that teardown is part of what the game thread lost and leaving it
        /// out of the number is what made the cost look smaller than it was.
        /// </remarks>
        private static NeighbourhoodFacadeTimings Finish(Stopwatch clock, double[] stages)
        {
            clock.Stop();
            var total = clock.ElapsedTicks * 1000d / Stopwatch.Frequency;
            return new NeighbourhoodFacadeTimings(stages, total);
        }

        /// <summary>
        /// A render-quality setting for the length of one build, put back afterwards.
        /// </summary>
        /// <remarks>
        /// A using block rather than an assignment, because a throw from any of the three
        /// generator passes - which is a normal outcome for a corrupt house file, and the whole
        /// reason the pipeline is inside one try - must not leave a global rendering setting
        /// changed for the rest of the session. Two settings rather than one for the same reason:
        /// they are restored together, so a throw between the two assignments cannot leave one of
        /// them applied. See <see cref="FacadeSupersample"/> and
        /// <see cref="FacadeWallTargetScale"/> for what each buys and what it costs.
        /// </remarks>
        private sealed class FacadeRenderQuality : IDisposable
        {
            private readonly int _supersample;
            private readonly int _wallTargetScale;

            public FacadeRenderQuality(int supersample, int wallTargetScale)
            {
                _supersample = LotFacadeGenerator.SUPERSAMPLE_COUNT;
                _wallTargetScale = LotFacadeGenerator.WALL_TARGET_SCALE;
                LotFacadeGenerator.SUPERSAMPLE_COUNT = supersample;
                LotFacadeGenerator.WALL_TARGET_SCALE = wallTargetScale;
            }

            public void Dispose()
            {
                LotFacadeGenerator.SUPERSAMPLE_COUNT = _supersample;
                LotFacadeGenerator.WALL_TARGET_SCALE = _wallTargetScale;
            }
        }

        /// <summary>
        /// The lot view's light map, sized to what this device can actually hold, for the length of
        /// one throwaway world and its house-file load - and the player's own setting afterwards.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a lot file at all ends up in a device error.</b> The lot view is told how
        /// wide the lot is, and it turns that into a light map of
        /// <c>3 * shadow-detail * (size - 1)</c> pixels across without ever asking the device
        /// whether it can have one that size. Both factors are outside the house file - the tile
        /// count is in it, the shadow detail is a graphics setting - so the product is a number
        /// neither of them owns, and it is the product that has to fit. A machine whose device
        /// caps render targets at 4096 takes every lot in this neighbourhood and refuses exactly
        /// the two that declare 60 tiles.</para>
        ///
        /// <para><b>Set and put back, over as small a window as the engine allows.</b> The
        /// resolution is captured once, by <c>ChangedWorldConfig</c>, when the world is added to
        /// the scene graph; it is read again in <c>LMapBatch.Init</c>, which runs from
        /// <c>World.InitBlueprint</c> at the tail of the house-file load. So the window is the
        /// world and the load, and the three generator passes run outside it with the player's own
        /// setting - they are what draws the facade, and their cost is the performance budget.
        /// Nothing here is a static on a class this project does not own, but it is a static on a
        /// <em>configuration</em> the whole game reads, and a throw from inside the load is the
        /// reason it is a using block rather than a pair of assignments: the setting goes back
        /// whether the load returns a blueprint or dies trying.</para>
        ///
        /// <para><b>What it costs.</b> A smaller shadow map in a world whose light map exists only
        /// because the lot view insists on building one: the facade's three surfaces come from
        /// <c>LotFacadeGenerator</c>'s own render targets, and the only thing that reads this
        /// resolution is the light map's own sharpness. On this report the change is 24 to 23 for
        /// a 60 tile lot, which is a light map 4071 pixels across instead of 4248.</para>
        /// </remarks>
        private sealed class FacadeLightingBudget : IDisposable
        {
            private readonly int _previous;
            private readonly bool _lowered;

            public FacadeLightingBudget(GraphicsDevice device, int lotSize)
            {
                var config = WorldConfig.Current;
                _previous = config.ShadowDetail;
                _lowered = false;
                //no device is the checklist's business, and a size outside the range has already
                //been refused before this was ever constructed
                if (device == null || !LotSizeIsUsable(lotSize)) return;
                //The ceiling, not the player's setting: this budget is a speed decision about a
                //throwaway light map, not a fit decision about a render target. ShadowDetailFor
                //still walks down from here, so a lot whose targets do not fit even at the engine's
                //minimum is still answered with a refusal - it just comes back as zero rather than
                //as the player's sixteen. See FacadeShadowDetail for why the ceiling is the floor.
                var detail = ShadowDetailFor(lotSize, FacadeShadowDetail, config.UltraLighting,
                    config.Directional, DeviceMaxTarget(device));
                //zero means the pre-flight should have refused the lot, and a value the setting
                //already holds is not ours to write. Either way the setter's clamp is never asked
                //to raise a value back over the limit it was lowered to get under.
                if (detail <= 0 || detail == config.ShadowDetail) return;
                config.ShadowDetail = detail;
                _lowered = true;
            }

            public void Dispose()
            {
                if (_lowered) WorldConfig.Current.ShadowDetail = _previous;
            }
        }

        /// <summary>
        /// Everything the pipeline needs before it loads a lot, as a checklist in the order it
        /// would have failed in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The four house-file chunks are here, and not left to the activator, because
        /// <c>VMTS1Activator.LoadFromIff</c> dereferences all four with no check of its own:
        /// <c>simi.GlobalData[23]</c>, <c>simi.GlobalData[35]</c>, <c>hous.CameraDir</c>,
        /// <c>objm.Prepare</c> and <c>objt.Entries[typeID - 1]</c>. A <c>HouseNN.iff</c> that is
        /// short one of them - a lot that was bulldozed, an overlay, a truncated save - is a bare
        /// <c>NullReferenceException</c> out of the middle of the load, which is what the log was
        /// saying for lot 64.
        /// </para>
        /// <para>
        /// The four chunks the layout already reads - SIMI, HOUS, and the two the facade does not
        /// use - are the same four the layout's own house reader
        /// (<c>TS1LotContentSource.ReadLot</c>) touches, so a file that gets this far is a file
        /// the rest of the pass has already had some success with.
        /// </para>
        /// </remarks>
        /// <param name="device">The graphics device.</param>
        /// <param name="sceneLayer">The 3D scene layer, or <c>null</c>.</param>
        /// <param name="provider">The content manager, or <c>null</c>.</param>
        /// <param name="house">The lot's <c>HouseNN.iff</c>, or <c>null</c>.</param>
        public static IList<NeighbourhoodFacadeRequirement> Checklist(
            GraphicsDevice device, _3DLayer sceneLayer, Content provider, IffFile house)
        {
            return new[]
            {
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.GraphicsDevice,
                    device != null, "the lot's walls have to be rasterised somewhere"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.SceneLayer,
                    sceneLayer != null && sceneLayer.Device != null,
                    "the throwaway world is added to it to be initialised"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.ContentManager,
                    provider != null, "the lot's house file comes out of it"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.NeighbourhoodContent,
                    provider != null && provider.Neighborhood != null,
                    "there is nowhere to read a house file from"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.HouseFile,
                    house != null, "there is nothing to build a lot out of"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.SimulationChunk,
                    HasSimulationChunk(house), "the lot's size, version and globals are read out of it"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.HouseChunk,
                    house != null && house.Get<HOUS>(0) != null, "the roof name and camera direction are read out of it"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.ObjectChunk,
                    house != null && house.Get<OBJM>(1) != null, "the objects standing in the lot are read out of it"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.ObjectTypeChunk,
                    house != null && house.Get<OBJT>(0) != null, "the lot's objects are looked up in it"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.LotRenderEffect,
                    WorldContent.RCObject != null,
                    "LotFacadeGenerator draws every wall and every object on it")
            };
        }

        /// <summary>
        /// What the loaded lot has to have before the generator is pointed at it.
        /// </summary>
        /// <remarks>
        /// <para><c>LotFacadeGenerator.GenerateRoof</c> opens with <c>bp.RoofComp.Texture</c> and
        /// <c>GenerateFloor</c> draws through <c>bp.Terrain</c>, and neither checks what it is
        /// about to dereference. The blueprint's constructor creates both, so a null here means the
        /// activator handed back something that is not the blueprint it built - which is worth a
        /// sentence naming one of them rather than an NRE four frames into a render target.</para>
        /// <para><b>Only the blueprint is fatal.</b> The other two gate their own pass and nothing
        /// else - <see cref="NeighbourhoodFacadeDiagnostics.RequiredStages"/> reads this list and
        /// drops the pass whose component is missing - because a house with no roof component is a
        /// house with no roof, and a house is its walls. Treating either of them as fatal is what
        /// turned a missing roof into no house at all.</para>
        /// </remarks>
        /// <param name="blueprint">The blueprint, or <c>null</c>.</param>
        public static IList<NeighbourhoodFacadeRequirement> BlueprintChecklist(Blueprint blueprint)
        {
            return new[]
            {
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.Blueprint,
                    blueprint != null, "there is no lot to render"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.RoofComponent,
                    blueprint != null && blueprint.RoofComp != null, "the lot has no roof to draw", false),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.TerrainComponent,
                    blueprint != null && blueprint.Terrain != null,
                    "the ground under the lot cannot be drawn", false)
            };
        }

        /// <summary>
        /// Whether a house file has the simulation chunk the TS1 loader reads its lot size, its
        /// game edition and its globals out of, with an array long enough to index.
        /// </summary>
        /// <param name="house">The lot's <c>HouseNN.iff</c>, or <c>null</c>.</param>
        public static bool HasSimulationChunk(IffFile house)
        {
            var simi = house?.Get<SIMI>(1);
            return simi != null && simi.GlobalData != null
                && simi.GlobalData.Length >= MinimumSimulationGlobals;
        }

        /// <summary>
        /// The one lighting step this pipeline needs, taken directly instead of through
        /// <c>World.Force2DPredraw</c>: put the throwaway lot at <see cref="FacadeTimeOfDay"/>,
        /// hand the room colours and the per-floor room maps to the state, draw the light map once,
        /// and prepare the lighting.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What this replaces, and why it was the whole cost of a facade.</b>
        /// <c>Force2DPredraw</c> is <c>BlueprintChanges.PreDraw</c>, and PreDraw is the lot view's
        /// <em>2D</em> frame: it recaches the 2D wall sprite cache through
        /// <c>WorldPlatform2D.RecacheWalls</c> - every wall tile of a sixty tile lot rasterised into
        /// a sprite buffer no facade ever samples - and then drains the light batch's invalidated
        /// rooms with <c>LMapBatch.ParseInvalidated</c>. The live log charged eight of a facade's
        /// eight-point-eight seconds to that one call, on five lots in a row.
        /// </para>
        /// <para>
        /// <b>What is actually needed from it</b> is a fifth of it, and this is that fifth:
        /// <c>PrepareLighting</c> reads the light map's textures, the ambient light and the wall
        /// offset, and the wall pass samples the light map through them - so the light map has to
        /// have been drawn. What is not needed is the 2D wall cache, the object draw-order rebuild
        /// and the floor-geometry reset that share the same call. So the room colours, the room maps
        /// and one light map pass are done here by name, and nothing else is asked of the lot view.
        /// </para>
        /// <para>
        /// <b>The floor limit is written down rather than trimmed.</b>
        /// <c>ParseInvalidated</c> passes a literal six, and the first version of this passed three,
        /// on the reasoning that most houses are three storeys. That reasoning is wrong for this
        /// pass specifically: the facade's wall pass walks every storey the geometry has - five, for
        /// every lot the TS1 activator builds - and a storey with no light map behind it draws
        /// unlit. <see cref="FacadeLightingFloors"/> is therefore the light map's own cell count, and
        /// the saving is <see cref="FacadeShadowDetail"/> instead of a smaller number of storeys.
        /// </para>
        /// <para>
        /// Never throws: this is the lot view's own state machine, and a facade is worth having
        /// without its lighting - <c>LotFacadeGenerator</c> calls <c>PrepareLighting</c> itself
        /// before every wall it draws, so a failure here costs the walls their fixed hour and
        /// nothing else.
        /// </para>
        /// </remarks>
        private static void PrepareFacadeLighting(Blueprint blueprint, VM vm, World world)
        {
            try
            {
                vm.Context.Architecture?.SetTimeOfDay(FacadeTimeOfDay);
                //the lot's own clock back before anything is rasterised, which is the order the
                //pre-draw used to give it and is kept deliberately: the hour is pinned for the
                //outside colour and the sun direction, and the lot view is left reading a clock
                //rather than a number this pass made up.
                vm.Context.Architecture?.SetTimeOfDay();

                var state = world?.State;
                if (state == null || blueprint == null) return;

                //the ROOM_CHANGED half of BlueprintChanges.PreDraw, and only that half
                blueprint.GenerateRoomLights();
                state.OutsideColor = blueprint.RoomColors[1];
                if (state.AmbientLight != null) state.AmbientLight.SetData(blueprint.RoomColors);
                var roomMaps = blueprint.RoomMap;
                var gpuMaps = state.Rooms?.RoomMaps;
                if (roomMaps != null && gpuMaps != null)
                {
                    //both arrays are indexed by storey and both are the engine's, so the shorter
                    //one bounds the loop rather than either being trusted to fit
                    var floors = Math.Min(roomMaps.Length, gpuMaps.Count);
                    for (sbyte i = 0; i < floors; i++) state.Rooms.SetRoomMap(i, roomMaps[i]);
                }

                //and the light map itself, which is what the wall shader samples
                var light = state.Light;
                if (light != null)
                {
                    light.InvalidateAll();
                    light.RedrawAll(state, FacadeLightingFloors);
                }

                state.PrepareLighting();
            }
            catch (Exception e)
            {
                //best-effort (the facade is still built), but logged so lighting that silently
                //stops working is findable
                Simitone.Client.Utils.SimitoneLog.Warn("NeighbourhoodFacadeBuilder: facade lighting setup failed; the facade is built without it.", e);
            }
        }

        /// <summary>
        /// The generator's three surfaces, placed into the generated world.
        /// </summary>
        /// <param name="absent">
        /// The passes that ran and produced nothing, or could not run at all. A lot with walls and
        /// a floor and no roof is a house, so this is only read when nothing at all came back -
        /// and then it is what turns "all came back empty" into the reference that was missing.
        /// </param>
        private static NeighbourhoodFacade Assemble(
            GraphicsDevice device, NeighbourhoodLot lot, NeighbourhoodMap map,
            LotFacadeGenerator generator, float ownGround,
            IList<NeighbourhoodFacadeRequirement> absent, out string reason)
        {
            reason = null;
            var ground = NeighbourhoodFacadeTransform.GroundHeightFor(map, lot);
            var offset = NeighbourhoodFacadeTransform.GroundOffsetFor(ownGround);
            var facade = new NeighbourhoodFacade
            {
                Lot = lot,
                HouseHash = lot.HouseHash,
                GroundHeight = ground,
                Bounds = NeighbourhoodFacadeTransform.BoundsFor(lot, ground)
            };

            //A device that refused a buffer, named. Empty when every upload worked, and the first
            //one that did not is the whole of the story - see SurfaceUpload.
            var uploadFailure = (string)null;

            //Assigned before the first one is built, so a throw part way through can free what the
            //earlier ones already uploaded. The generator is still the caller's until Assemble
            //returns, so the wall and floor textures of a surface that never got built are freed by
            //TryBuild's finally; the ones a built surface owns are freed by facade.Dispose.
            facade.Surfaces = new NeighbourhoodFacadeSurface[3];
            try
            {
                facade.Surfaces[0] = Surface(device, generator.WallVerts, generator.WallIndices,
                    generator.WallTarget, lot, ground, offset, ownsTexture: true, index: 0, ref uploadFailure);
                //the roof texture is the content system's, not ours: RoofComponent hands out
                //roofs.Get(name).Get(device), one instance shared by every lot with that roof.
                //Disposing it would take the lot view's roofs down with it.
                facade.Surfaces[1] = Surface(device, generator.RoofVerts, generator.RoofIndices,
                    generator.RoofTexture, lot, ground, offset, ownsTexture: false, index: 1, ref uploadFailure);
                facade.Surfaces[2] = Surface(device, generator.FloorVerts, generator.FloorIndices,
                    generator.FloorTexture, lot, ground, offset, ownsTexture: true, index: 2, ref uploadFailure);
            }
            catch (Exception)
            {
                facade.Dispose();
                throw;
            }

            //which of the three passes actually produced something. One surface with no geometry is
            //normal - a one-storey house has no roof - and three of them with none is a lot that
            //did not load, which is the case the reason below is for.
            var completed = NeighbourhoodFacadeDiagnostics.CompletedStages(
                HasGeometry(facade.Surfaces[0]), HasGeometry(facade.Surfaces[2]),
                HasGeometry(facade.Surfaces[1]));
            string failure = uploadFailure;
            for (int i = 0; i < facade.Surfaces.Length; i++)
            {
                var surface = facade.Surfaces[i];
                if (surface == null || surface.IsEmpty) continue;
                if (surface.Texture == null && failure == null)
                {
                    //a surface with no texture is not a degraded surface, it is an unbound sampler:
                    //it would draw whatever the last bind left there, which is the whole screen on
                    //some drivers and nothing at all on others
                    failure = "the facade's " + SurfaceName(i) + " texture was missing";
                }
                if (surface.VertexBuffer == null && failure == null)
                {
                    failure = "the facade mesh could not be uploaded to the graphics device";
                }
            }
            //A refused upload is reported before the "all came back empty" verdict, because it is
            //the more specific of the two: it says the mesh was built and the device would not take
            //it, where the other says the mesh was never built. Both used to produce the second
            //sentence.
            if (failure != null)
            {
                facade.Dispose();
                reason = failure;
                return null;
            }
            if (completed == NeighbourhoodFacadeStages.None)
            {
                facade.Dispose();
                //"all came back empty" on its own sends the next person looking for a rendering
                //bug. The absent list is the reference that was missing, and for a lot like this
                //one it is the storey the roof component never meshed. The wall pass's own counts
                //go on with it, because "the walls came back empty" and "the wall pass built a
                //wall and it was lost on the way to the device" are different bugs with the same
                //sentence, and the wall pass is the only one that can say which it was.
                var named = NeighbourhoodFacadeDiagnostics.Absent(absent, lot.Number);
                reason = "the lot's walls, roof and floor all came back empty"
                    + " (the wall pass produced " + generator.LastWallVertexCount
                    + " wall vertex/vertices and " + generator.LastWallIndexCount
                    + " wall index/indices)"
                    + (named == null ? "" : "; " + named);
                return null;
            }
            return facade;
        }

        private static string SurfaceName(int index)
        {
            switch (index)
            {
                case 0: return "wall";
                case 1: return "roof";
                default: return "floor";
            }
        }

        /// <summary>
        /// Whether one of the three surfaces came back with anything to draw, which is what decides
        /// whether its pass ran at all.
        /// </summary>
        private static bool HasGeometry(NeighbourhoodFacadeSurface surface)
        {
            return surface != null && !surface.IsEmpty;
        }

        private static NeighbourhoodFacadeSurface Surface(
            GraphicsDevice device, VertexPositionTexture[] verts, int[] indices, Texture2D texture,
            NeighbourhoodLot lot, float ground, float groundOffset, bool ownsTexture, int index,
            ref string uploadFailure)
        {
            //No surface means nobody to own the texture, and the caller lets go of the generator
            //once Assemble returns - so an owned texture that gets no surface is freed here, or it
            //is a render target (the wall atlas, the floor) that lives for the rest of the process.
            if (verts == null || indices == null || indices.Length == 0)
            {
                if (ownsTexture) texture?.Dispose();
                return null;
            }
            var placed = NeighbourhoodFacadeTransform.PlacedVertices(lot, verts, ground, groundOffset);
            if (placed == null || placed.Length == 0)
            {
                if (ownsTexture) texture?.Dispose();
                return null;
            }

            var surface = new NeighbourhoodFacadeSurface
            {
                Vertices = placed,
                Indices = indices,
                Texture = texture,
                OwnsTexture = ownsTexture && texture != null
            };
            try
            {
                //Uploaded as the object vertex, not as the VertexPositionTexture the generator
                //produced: RenderCityObj's input struct is ObjVertexIn (float4 position, float2
                //texture coordinate, float3 in TEXCOORD1) and the device rejects an input layout
                //that does not carry all three. The placement above is unchanged and
                //surface.Vertices still holds it for the obj exporter; only the copy that reaches
                //the GPU is widened. See NeighbourhoodFacadeVertex.
                var objectVertices = NeighbourhoodFacadeVertex.ToObjectVertices(placed, indices);
                if (objectVertices == null)
                {
                    if (ownsTexture) texture?.Dispose();
                    return null;
                }
                surface.VertexBuffer = new VertexBuffer(device, typeof(DGRP3DVert), objectVertices.Length, BufferUsage.None);
                surface.VertexBuffer.SetData(objectVertices);
                surface.IndexBuffer = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.None);
                surface.IndexBuffer.SetData(indices);
            }
            catch (Exception ex)
            {
                //The buffers are all or nothing; a half-uploaded surface draws nothing, and the
                //caller's next line turns that into a sentence rather than a blank house. What
                //changed is where the sentence comes from: the throw used to be swallowed here and
                //the caller - which has just been told "the lot's walls, roof and floor all came
                //back empty" - had no way to tell a device that refused the upload from a lot with
                //no house on it. It is handed up, and Assemble turns it into a SurfaceUpload
                //requirement naming the surface and the device.
                surface.VertexBuffer?.Dispose();
                surface.VertexBuffer = null;
                surface.IndexBuffer?.Dispose();
                surface.IndexBuffer = null;
                uploadFailure = NeighbourhoodFacadeDiagnostics.Sentence(
                    new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.SurfaceUpload,
                        false, "the " + SurfaceName(index) + " surface (" + placed.Length
                            + " vertices, " + indices.Length + " indices): "
                            + NeighbourhoodFacadeDiagnostics.DescribeException(ex)),
                    lot.Number);
            }
            return surface;
        }
    }
}
