/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using FSO.Common;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// How the neighbourhood view should be drawn: as the hand-painted TS1 bitmap, as the
    /// generated 3D world, or not at all.
    ///
    /// <para>
    /// <b>Why three values and not a boolean.</b> A boolean would fold "the player asked for the
    /// painted map" into "the generated world could not be built", and the two are worth telling
    /// apart: the first is a preference that should survive a restart, the second is a failure that
    /// has to be visible in the setting afterwards rather than silently becoming the preference.
    /// Keeping an explicit 2D step also keeps the bitmap path a first-class supported choice
    /// rather than the absence of a feature.
    /// </para>
    ///
    /// <para>
    /// <b>The order is fixed and the default is 2D.</b> The values stay Off / 2D / 3D in that order
    /// so an existing <c>simitone.ini</c> keeps meaning what it meant. The connected-mechanics
    /// rework moved the default to 3D and broke the working modes; the revert puts the painted map
    /// back as what a player who has changed nothing gets. 3D is still one click away, and the two
    /// modes are independent again.
    /// </para>
    /// </summary>
    public enum NeighbourhoodView
    {
        /// <summary>Do not build a generated world. The bitmap path, exactly as before.</summary>
        Off = 0,

        /// <summary>The hand-painted TS1 neighbourhood bitmap. The default.</summary>
        Bitmap2D = 1,

        /// <summary>
        /// The generated 3D world: <c>NeighbourhoodTerrainRenderer</c> plus
        /// <c>NeighbourhoodFacades</c>, behind the panel's existing controls. Off by default.
        /// </summary>
        World3D = 2
    }

    /// <summary>
    /// The stored shape of the neighbourhood view choice and the one place that knows how it is
    /// persisted and resolved.
    ///
    /// <para>
    /// <b>Why a Simitone ini key and not a <c>GlobalSettings</c> property.</b> The Options pages
    /// reflect over <c>FSO.UI.GlobalSettings</c> to name a setting, and that class is FreeSO's to
    /// edit. The same reasoning, and the same refusal to borrow a key that already means something
    /// else, is spelled out on <c>SunCycleSetting</c>: adding to <c>GlobalSettings</c> is not
    /// available, and reusing <c>CityShadows</c> or <c>ShadowQuality</c> would quietly change what
    /// somebody else's setting does. So this is a second key in the same sidecar ini, with the
    /// same "garbage in, known-good out, and never block the game starting" contract - and, because
    /// it is the same <em>file</em>, written through the same read-merge-write
    /// <see cref="Utils.SimitoneIniFile"/>, so neither key can lose the other.
    /// </para>
    /// </summary>
    public static class NeighbourhoodViewSetting
    {
        /// <summary>The ini key the choice is stored under.</summary>
        public const string Key = "NeighbourhoodView";

        /// <summary>The sidecar ini it lives in, beside <c>config.ini</c>.</summary>
        public const string FileName = "simitone.ini";

        /// <summary>
        /// The stored token for each value, as written to the ini. Stable strings rather than the
        /// enum's numeric value so a reordering of <see cref="NeighbourhoodView"/> cannot silently
        /// re-interpret an existing config.
        /// </summary>
        public const string OffToken = "off";

        /// <summary>Stored token for the bitmap view.</summary>
        public const string Bitmap2DToken = "2d";

        /// <summary>Stored token for the generated 3D world.</summary>
        public const string World3DToken = "3d";

        /// <summary>
        /// What the choice is when nothing has chosen: the hand-painted 2D map, which is the mode
        /// the game has always shipped and the one the connected-mechanics rework broke. The 3D
        /// generated world is still the third step of the same three-way row and is one click away.
        /// </summary>
        public const NeighbourhoodView Default = NeighbourhoodView.Bitmap2D;

        private static readonly object Gate = new object();
        private static NeighbourhoodView? cached;

        /// <summary>
        /// The live choice, read from disk the first time it is asked for.
        /// </summary>
        /// <remarks>
        /// Lazy for the same reason <c>SunCycleSetting.Enabled</c> is: the panel asks when it builds
        /// a map, the renderer asks when it draws, and neither is a moment where an eager load at
        /// startup would be more correct - and a lazy load is what lets the resolution rules be
        /// tested with no user directory and no game around.
        /// </remarks>
        public static NeighbourhoodView Current
        {
            get
            {
                lock (Gate)
                {
                    if (!cached.HasValue) cached = Load();
                    return cached.Value;
                }
            }
        }

        /// <summary>Records <paramref name="view"/> without saving; the Options page saves.</summary>
        /// <param name="view">The choice to record.</param>
        public static void Set(NeighbourhoodView view)
        {
            lock (Gate)
            {
                cached = Resolve(view);
            }
        }

        /// <summary>Records <paramref name="view"/> and writes it to the sidecar ini.</summary>
        /// <param name="view">The choice to record.</param>
        public static void SetAndSave(NeighbourhoodView view)
        {
            Set(view);
            Save();
        }

        /// <summary>
        /// The choice as a stored token, for the Options row's radio group.
        /// </summary>
        /// <param name="view">The choice to name.</param>
        public static string TokenFor(NeighbourhoodView view)
        {
            switch (Resolve(view))
            {
                case NeighbourhoodView.Off: return OffToken;
                case NeighbourhoodView.World3D: return World3DToken;
                default: return Bitmap2DToken;
            }
        }

        /// <summary>
        /// A stored token turned into a choice. A hand-edited, empty or unknown value is the
        /// default rather than an error, for the same reason every other ini key in the game is:
        /// this is user input, and a config nobody can predict is worse than one that says 2D.
        /// </summary>
        /// <param name="stored">The raw ini value, or <c>null</c>.</param>
        public static NeighbourhoodView Resolve(string stored)
        {
            if (stored == null) return Default;
            var trimmed = stored.Trim();
            if (trimmed.Length == 0) return Default;
            if (string.Equals(trimmed, OffToken, StringComparison.OrdinalIgnoreCase)) return NeighbourhoodView.Off;
            if (string.Equals(trimmed, World3DToken, StringComparison.OrdinalIgnoreCase)) return NeighbourhoodView.World3D;
            if (string.Equals(trimmed, Bitmap2DToken, StringComparison.OrdinalIgnoreCase)) return NeighbourhoodView.Bitmap2D;

            // a bare number is accepted because that is what a player who has read the enum will
            // type, and it costs nothing to be forgiving about a closed three-value set
            int numeric;
            if (int.TryParse(trimmed, out numeric))
            {
                switch (numeric)
                {
                    case 0: return NeighbourhoodView.Off;
                    case 1: return NeighbourhoodView.Bitmap2D;
                    case 2: return NeighbourhoodView.World3D;
                    // a number outside the enum is garbage like any other, so it is the default
                    // rather than a step the row does not have. This used to name 2D literally,
                    // which only agreed with the default while the default was 2D; saying
                    // <see cref="Default"/> is what makes "an unknown value is the default" true
                    // for every unknown value.
                    default: return Default;
                }
            }
            return Default;
        }

        /// <summary>
        /// A choice turned back into a real choice: anything outside the three values is
        /// <see cref="Default"/>, so a cast of arbitrary memory cannot reach the renderer.
        /// </summary>
        /// <param name="view">The raw value.</param>
        public static NeighbourhoodView Resolve(NeighbourhoodView view)
        {
            switch (view)
            {
                case NeighbourhoodView.Off: return NeighbourhoodView.Off;
                case NeighbourhoodView.Bitmap2D: return NeighbourhoodView.Bitmap2D;
                case NeighbourhoodView.World3D: return NeighbourhoodView.World3D;
                default: return Default;
            }
        }

        /// <summary>Every choice, in the order the Options radio group shows them.</summary>
        public static IReadOnlyList<NeighbourhoodView> All
        {
            get
            {
                return new[]
                {
                    NeighbourhoodView.Off,
                    NeighbourhoodView.Bitmap2D,
                    NeighbourhoodView.World3D
                };
            }
        }

        /// <summary>Label for a choice, for the Options row.</summary>
        /// <param name="view">The choice to name.</param>
        public static string Describe(NeighbourhoodView view)
        {
            switch (Resolve(view))
            {
                case NeighbourhoodView.Off: return "Off";
                case NeighbourhoodView.World3D: return "3D";
                default: return "2D";
            }
        }

        /// <summary>What the setting does, for the Options row's tooltip.</summary>
        public static string DescribeEffect()
        {
            return "Draws the neighbourhood as the painted map (2D), the hand-painted TS1 map the game "
                + "has always shown. Picking a lot, the neighbourhood buttons and the zoom all still "
                + "work. The painted map is what you get unless you pick otherwise. 3D draws the "
                + "neighbourhood as a generated world of terrain, roads and houses seen from above, "
                + "and is also what you get if the 3D world cannot be built.";
        }

        /// <summary>
        /// Whether an Options descriptor is the row for this setting. Takes the property name
        /// rather than the descriptor so this file does not have to know about the Options page's
        /// types, and so the rule is testable on its own.
        /// </summary>
        /// <remarks>
        /// Matching on the name rather than on the descriptor's identity, because the descriptor
        /// list is rebuilt every time the page re-lays itself out: there is no stable object to
        /// compare against, and a silent mismatch would not throw, it would leave a radio group
        /// that saves and does nothing.
        /// </remarks>
        /// <param name="attachedPropertyName">The descriptor's attached property name.</param>
        public static bool Describes(string attachedPropertyName)
        {
            return string.Equals(attachedPropertyName, Key, StringComparison.Ordinal);
        }

        /// <summary>Full path of the sidecar ini, or <c>null</c> for a null/empty user directory.</summary>
        /// <param name="userDir">The game's user directory.</param>
        public static string ConfigFilePath(string userDir)
        {
            if (string.IsNullOrEmpty(userDir)) return null;
            return Path.Combine(userDir, FileName);
        }

        private static NeighbourhoodView Load()
        {
            try
            {
                var path = ConfigFilePath(FSOEnvironment.UserDir);
                if (path == null) return Default;
                return Resolve(SunCycleSetting.Ini(path).Get(Key));
            }
            catch (Exception)
            {
                // A settings file that cannot be read is not worth refusing to start over.
                return Default;
            }
        }

        private static void Save()
        {
            string path = null;
            try
            {
                path = ConfigFilePath(FSOEnvironment.UserDir);
                if (path == null) return;
                // Read-merge-write, not a rewrite: SunCycleSetting owns the other key in this
                // file and a write that dropped it would silently reset the player's Dynamic Sun
                // choice back to its default. See SimitoneIniFile for the whole argument.
                SunCycleSetting.Ini(path).Set(Key, TokenFor(Current));
            }
            catch (Exception e)
            {
                // The choice is already live, so a sidecar that cannot be written is not worth
                // refusing to run over - but it must not be silent, or the player picks a view
                // that quietly reverts on the next launch.
                SimitoneLog.Warn("NeighbourhoodViewSetting: could not save the neighbourhood view"
                    + " choice to '" + (path ?? "(no user directory)") + "'. It will reset next launch.", e);
            }
        }
    }

    /// <summary>
    /// Which view the neighbourhood panel will actually draw, and why - the pure decision behind
    /// the <c>NeighbourhoodView</c> setting, the generation attempt, and the 2D bitmap fallback.
    ///
    /// <para>
    /// The reason this is a value with a reason rather than a branch inside the panel: the panel
    /// is a <c>UIContainer</c> built on the game thread with a graphics device, a content provider
    /// and a screen, none of which exist in a test. This type has none of them, so "the setting
    /// is Off so the bitmap is used", "generation threw so the bitmap is used" and "3D was asked
    /// for and worked" are all assertions rather than observations. §11 asks for exactly this test
    /// (<c>NeighbourhoodViewTests</c>: setting off =&gt; 2D path; generation failure =&gt; 2D path).
    /// </para>
    ///
    /// <para>
    /// <b>Every failure is the bitmap, and the reason is kept.</b> A neighbourhood whose
    /// <c>LotLocations</c> is missing, whose road graph could not be connected, whose
    /// <c>HouseNN.iff</c> files are unreadable, or whose mesh the GPU would not take, must still
    /// open - that is the plan's non-negotiable ("the 2D <c>UINeighborhoodSelectionPanel</c>
    /// bitmap path is kept and must still work if generation fails, is disabled, or the GPU cannot
    /// build the scene"), and it is why this type has no "failed" state at all.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodViewSelection
    {
        private NeighbourhoodViewSelection(bool use3D, NeighbourhoodView requested, string reason)
        {
            Use3D = use3D;
            Requested = requested;
            Reason = reason;
        }

        /// <summary>Whether the generated 3D world is drawn behind the panel's controls.</summary>
        public bool Use3D { get; }

        /// <summary>
        /// Whether the bitmap and its animation overlays are drawn. Always the inverse of
        /// <see cref="Use3D"/>, and named rather than left implicit so a caller cannot draw both
        /// by forgetting a branch.
        /// </summary>
        public bool UseBitmap2D
        {
            get { return !Use3D; }
        }

        /// <summary>The setting that was asked for, before generation had its say.</summary>
        public NeighbourhoodView Requested { get; }

        /// <summary>
        /// Why this view, in one sentence, for the log. Never <c>null</c> for a bitmap selection -
        /// a fallback with no stated reason is the case worth chasing.
        /// </summary>
        public string Reason { get; }

        /// <summary>The 3D world is drawn.</summary>
        /// <param name="requested">The setting that asked for it.</param>
        /// <param name="reason">What was built, for the log.</param>
        public static NeighbourhoodViewSelection ThreeD(NeighbourhoodView requested, string reason)
        {
            return new NeighbourhoodViewSelection(true, NeighbourhoodViewSetting.Resolve(requested),
                reason ?? "the 3D world was built");
        }

        /// <summary>The bitmap is drawn, for this reason.</summary>
        /// <param name="requested">The setting that was in force.</param>
        /// <param name="reason">Why the 3D world is not being drawn.</param>
        public static NeighbourhoodViewSelection Bitmap(NeighbourhoodView requested, string reason)
        {
            return new NeighbourhoodViewSelection(false, NeighbourhoodViewSetting.Resolve(requested),
                reason ?? "the 3D world is not being used");
        }

        /// <summary>
        /// The selection for a setting, before anything has been generated. 3D is only ever
        /// <em>requested</em> here; <see cref="ThreeD"/> is what makes it happen.
        /// </summary>
        /// <param name="view">The stored setting.</param>
        public static NeighbourhoodViewSelection ForSetting(NeighbourhoodView view)
        {
            var resolved = NeighbourhoodViewSetting.Resolve(view);
            if (resolved == NeighbourhoodView.World3D)
            {
                return ThreeD(resolved, "the 3D neighbourhood is switched on");
            }
            if (resolved == NeighbourhoodView.Off)
            {
                return Bitmap(resolved, "the 3D neighbourhood is switched off");
            }
            return Bitmap(resolved, "the 2D map is selected");
        }

        /// <summary>
        /// The selection once generation has had its say: requested 3D and a usable map is 3D,
        /// anything else is the bitmap with the reason that stopped it.
        /// </summary>
        /// <param name="requested">The setting that was in force.</param>
        /// <param name="map">The generated map, or <c>null</c> if generation did not produce one.</param>
        /// <param name="failure">Why generation failed, when it did.</param>
        public static NeighbourhoodViewSelection AfterGeneration(
            NeighbourhoodView requested, NeighbourhoodMap map, string failure)
        {
            var resolved = NeighbourhoodViewSetting.Resolve(requested);
            if (resolved != NeighbourhoodView.World3D)
            {
                return ForSetting(resolved);
            }
            if (!string.IsNullOrEmpty(failure))
            {
                return Bitmap(resolved, "the generated world could not be built: " + failure);
            }
            if (map == null)
            {
                return Bitmap(resolved, "the generated world could not be built: no map was produced");
            }
            return ThreeD(resolved, "the generated world was built (" + map.Width + "x" + map.Height + ")");
        }

        public override string ToString()
        {
            return (Use3D ? "3D" : "2D") + " (asked for " + NeighbourhoodViewSetting.Describe(Requested) + "): " + Reason;
        }
    }

    /// <summary>
    /// Which backdrop the neighbourhood panel is drawing right now.
    ///
    /// <para>
    /// <b>Why this is three values and not a boolean.</b> A boolean folds "the generated world is
    /// still uploading its mesh" into "the generated world is broken", and the two need opposite
    /// behaviour. The middle value is the whole point of the type: a world that has been built is
    /// not yet a world that can be looked at, and the painted map is what carries the picture
    /// across that gap. See <see cref="NeighbourhoodBackdropRule"/> for the rule and
    /// <see cref="NeighbourhoodViewDecision"/> for the value it returns.
    /// </para>
    /// </summary>
    public enum NeighbourhoodBackdrop
    {
        /// <summary>
        /// Show 2D. The hand-painted TS1 bitmap and its animation overlays, and nothing else.
        /// </summary>
        PaintedMap = 0,

        /// <summary>
        /// Show both. A generated world is attached and is drawing behind the UI, but it has not
        /// yet demonstrated that it produced anything, so the painted map is still what the player
        /// sees.
        ///
        /// <para>
        /// <b>"Both" is not two pictures superimposed.</b> <c>GameFacade.Scenes</c> is the graphics
        /// layer <em>in front of</em> the UI layer, so the painted map is drawn <em>over</em> the
        /// world, not under it. Both are present in the frame; the painted one is what is on top,
        /// and it is opaque. That is deliberate - the alternative, a half-transparent map over a
        /// half-built world, is two neighbourhoods ghosted through each other, which is harder to
        /// read than either alone.
        /// </para>
        /// </summary>
        PaintedMapAndWorld = 1,

        /// <summary>
        /// Show 3D. The generated world has demonstrably produced geometry and is carrying the
        /// picture.
        /// </summary>
        GeneratedWorld = 2
    }

    /// <summary>
    /// Why a generated world is not carrying the picture, when it is not. <see cref="None"/> is
    /// the only member that is not a failure: it means either that the world is carrying the
    /// picture, or that 3D was never asked for.
    /// </summary>
    public enum NeighbourhoodWorldFault
    {
        /// <summary>Not a fault.</summary>
        None = 0,

        /// <summary>
        /// Generation produced no map: no raster, no layout, a sidecar that would not pack, or a
        /// road graph that would not connect.
        /// </summary>
        GenerationFailed = 1,

        /// <summary>
        /// A map exists but <c>Neighbourhood3DView.TryCreate</c> refused it: no graphics device,
        /// no terrain renderer, no shaders, or the constructor threw.
        /// </summary>
        SceneCreateFailed = 2,

        /// <summary>
        /// The generated map has no ground to draw. A mesh of zero quads is not a neighbourhood,
        /// and this is knowable before the GPU is touched at all - it is a property of the raster,
        /// not of the device.
        /// </summary>
        NoGround = 3,

        /// <summary>
        /// The generated map has ground but no roads, on a neighbourhood that has lots in it. The
        /// painted map's ground <em>between</em> the houses is the thing a house button over an
        /// empty 3D field cannot replace, so a roadless world is the same failure as a groundless
        /// one and is treated as one.
        /// </summary>
        NoRoads = 4,

        /// <summary>
        /// The world attached and was given its warm-up window, and its mesh still has not landed.
        /// The terrain mesh is built on a worker and uploaded on the game thread, so this is the
        /// normal state for the first few frames - it only becomes a fault when it persists past
        /// <see cref="NeighbourhoodBackdropRule.MeshWarmUpSeconds"/>.
        /// </summary>
        MeshNeverUploaded = 5,

        /// <summary>
        /// Drawing the world threw, repeatedly. Consecutive failures, because one bad frame is a
        /// hiccup and a run of them is a world that will never draw.
        /// </summary>
        DrawFailed = 6,

        /// <summary>
        /// The graphics device has been reset since the world attached and the world has not drawn
        /// since. Its buffers went with the old device, so whatever it was showing is gone and it
        /// has to prove itself again before it is allowed to hide the painted map.
        /// </summary>
        DeviceLost = 7,

        /// <summary>
        /// The world is up, its mesh holds geometry, and the frame submitted none of it.
        ///
        /// <para>
        /// <b>This is the field report, named.</b> "315424 terrain triangles and 38 lot facades" is
        /// what the mesh held; the screen was blank. Every chunk culled, or the camera looking away
        /// from the map, or a render state discarding the output - all three leave the mesh's count
        /// untouched and the frame's at zero, and the health check was reading the first. A
        /// separate fault from <see cref="MeshNeverUploaded"/> because it is the more confusing one:
        /// the mesh is there, and the log says it has three hundred thousand triangles in it.
        /// </para>
        /// </summary>
        NothingSubmitted = 8,

        /// <summary>
        /// The ground drew and no house did. The frame submitted terrain and submitted zero facade
        /// triangles, so what the player would be looking at is a plain with a lot button floating
        /// over it.
        ///
        /// <para>
        /// <b>Separate from <see cref="FacadesIncomplete"/> on purpose.</b> Both are about the
        /// houses, but this one is about the <em>frame</em> and that one is about the <em>build</em>.
        /// This fires when there is a house to draw and none of it reached the device - every
        /// surface culled, every draw failed, the cache emptied - which is a working terrain pass
        /// and a broken facade pass. <see cref="FacadesIncomplete"/> fires when the facades are
        /// building perfectly well and simply are not finished.
        /// </para>
        /// </summary>
        NoFacadesOnScreen = 9,

        /// <summary>
        /// The houses are still being built. The frame has house geometry on it, but fewer of the
        /// neighbourhood's lots have a facade than
        /// <see cref="NeighbourhoodBackdropRule.MinimumFacadeFraction"/> of them need, past the
        /// warm-up window.
        ///
        /// <para>
        /// A fraction rather than a count, because a lot whose <c>HouseNN.iff</c> will not read
        /// never gets a facade and never will: demanding every one of them would hold the painted
        /// map up for ever on any install with a bad house file in it.
        /// </para>
        /// </summary>
        FacadesIncomplete = 10
    }

    /// <summary>
    /// Everything <see cref="NeighbourhoodBackdropRule"/> knows about a generated world at one
    /// point in time: what it is, what it has produced, and what has gone wrong with it.
    ///
    /// <para>
    /// <b>Why this is a value and not a query against the scene.</b> The scene is a
    /// <c>_3DScene</c> on a graphics device that a test does not have, and every question this
    /// asks - did the mesh upload, did it upload anything, did a draw throw - is a question about
    /// one frame. Handing the rule the answers turns "a world that renders nothing falls back to
    /// the painted map" into an assertion.
    /// </para>
    ///
    /// <para>
    /// <b>Two halves, deliberately.</b> <see cref="GroundQuads"/> and <see cref="RoadQuads"/> are
    /// <em>static</em>: what the generated raster ought to produce, counted before the GPU is
    /// touched. <see cref="MeshUploaded"/>, <see cref="TerrainTriangles"/> and
    /// <see cref="DrawFailures"/> are <em>live</em>: what the device has actually done with it.
    /// The static half catches a degenerate world at attach time; the live half catches a world
    /// that was fine on paper and did not survive contact with the renderer. A world that passes
    /// both has demonstrably produced something, which is the only thing that earns the right to
    /// hide the painted map.
    /// </para>
    ///
    /// <para>
    /// <b>A third half, added because terrain alone is not a neighbourhood.</b> The field report
    /// was "60660 terrain triangle(s) ... over 38 visible lot(s)" and the player looking at a bare
    /// plain with the houses still streaming in behind it. A count of ground says the world's floor
    /// is on the device; it says nothing about whether there is anything <em>standing on it</em>.
    /// So the snapshot carries what the frame's facade pass submitted
    /// (<see cref="FacadeTriangles"/>, <see cref="FacadesVisible"/>) and how far the build has got
    /// (<see cref="FacadeCount"/> of <see cref="FacadeEligible"/>), which is what the promotion gate
    /// now reads before it lets a world take the screen.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodWorldHealth
    {
        /// <summary>
        /// A count that means "nobody counted this". Distinct from zero, which is the count a
        /// degenerate world actually produces.
        /// </summary>
        public const int Unknown = -1;

        /// <param name="attached">Whether a 3D scene was built and attached at all.</param>
        /// <param name="meshUploaded">Whether its terrain mesh has finished uploading.</param>
        /// <param name="terrainTriangles">Triangles the device currently holds.</param>
        /// <param name="roadTriangles">
        /// Road triangles the device currently holds, or <see cref="Unknown"/> when nothing counts
        /// them. The static <see cref="RoadQuads"/> is what the fallback rule keys on.
        /// </param>
        /// <param name="groundQuads">
        /// Ground quads the generated raster ought to produce, or <see cref="Unknown"/> when the
        /// raster was not probed.
        /// </param>
        /// <param name="roadQuads">
        /// Road quads the generated raster ought to produce, or <see cref="Unknown"/> when the
        /// raster was not probed.
        /// </param>
        /// <param name="lotCount">How many lots the layout holds.</param>
        /// <param name="facadeCount">How many lot facades the world has cached so far.</param>
        /// <param name="drawFailures">Consecutive frames the world's draw threw.</param>
        /// <param name="deviceLost">Whether the device has reset and the world has not drawn since.</param>
        /// <param name="secondsSinceAttach">How long the world has been attached, in seconds.</param>
        /// <param name="failure">Why the world could not be built, when it could not.</param>
        /// <param name="submittedTriangles">
        /// Triangles the last frame actually submitted, or <see cref="Unknown"/> when nobody is
        /// counting. Distinct from <paramref name="terrainTriangles"/>, which is what the mesh
        /// holds - a mesh can hold three hundred thousand triangles and submit none of them, and
        /// that is a blank screen.
        /// </param>
        /// <param name="submittedFrames">
        /// Consecutive frames that submitted geometry, or <see cref="Unknown"/> when nobody is
        /// counting. The promotion gate keys on this rather than on a single frame, so a world that
        /// draws one frame on its way to failing does not take the screen.
        /// </param>
        /// <param name="facadeTriangles">
        /// Triangles the lot facades submitted last frame, or <see cref="Unknown"/> when nobody is
        /// counting them. Zero with a world up and terrain drawn is a plain with no houses on it.
        /// </param>
        /// <param name="facadesVisible">
        /// Lot facades the cull kept last frame, or <see cref="Unknown"/>. This is the fraction's
        /// denominator when the gate wants to know whether the houses that <em>could</em> be drawn
        /// were: facades the cull kept and submitted nothing are facades whose every surface failed.
        /// </param>
        /// <param name="facadeEligible">
        /// How many lots a complete facade pass would build on this map, or <see cref="Unknown"/>
        /// when nobody counted. The denominator of the build fraction; zero means the map has no
        /// lots with houses in it and the facade gate does not apply at all.
        /// </param>
        public NeighbourhoodWorldHealth(
            bool attached = false,
            bool meshUploaded = false,
            int terrainTriangles = 0,
            int roadTriangles = Unknown,
            int groundQuads = Unknown,
            int roadQuads = Unknown,
            int lotCount = 0,
            int facadeCount = 0,
            int drawFailures = 0,
            bool deviceLost = false,
            double secondsSinceAttach = 0d,
            string failure = null,
            int submittedTriangles = Unknown,
            int submittedFrames = Unknown,
            int facadeTriangles = Unknown,
            int facadesVisible = Unknown,
            int facadeEligible = Unknown)
        {
            Attached = attached;
            MeshUploaded = meshUploaded;
            TerrainTriangles = terrainTriangles;
            RoadTriangles = roadTriangles;
            GroundQuads = groundQuads;
            RoadQuads = roadQuads;
            LotCount = lotCount;
            FacadeCount = facadeCount;
            DrawFailures = drawFailures;
            DeviceLost = deviceLost;
            SecondsSinceAttach = secondsSinceAttach;
            Failure = failure;
            SubmittedTriangles = submittedTriangles;
            SubmittedFrames = submittedFrames;
            FacadeTriangles = facadeTriangles;
            FacadesVisible = facadesVisible;
            FacadeEligible = facadeEligible;
        }

        /// <summary>Whether a 3D scene was built and attached at all.</summary>
        public bool Attached { get; }

        /// <summary>Whether the terrain mesh has finished uploading to the device.</summary>
        public bool MeshUploaded { get; }

        /// <summary>Triangles the device currently holds, across every terrain layer and the roads.</summary>
        public int TerrainTriangles { get; }

        /// <summary>
        /// Road triangles the device currently holds, or <see cref="Unknown"/>. Diagnostics: the
        /// fallback keys on the static <see cref="RoadQuads"/> so that a roadless map is caught
        /// before the mesh is even built.
        /// </summary>
        public int RoadTriangles { get; }

        /// <summary>
        /// Ground quads the generated raster ought to produce, or <see cref="Unknown"/> when it
        /// was not probed. Zero is a real answer and means the map is empty.
        /// </summary>
        public int GroundQuads { get; }

        /// <summary>
        /// Road quads the generated raster ought to produce, or <see cref="Unknown"/> when it was
        /// not probed. Zero is a real answer and means the map has no streets in it.
        /// </summary>
        public int RoadQuads { get; }

        /// <summary>How many lots the layout holds.</summary>
        public int LotCount { get; }

        /// <summary>
        /// How many lot facades the world has cached. Now load-bearing rather than a log line: it is
        /// the numerator of <see cref="FacadeProgress"/>, the fraction of the map's lots that
        /// actually have a house standing on them. It is still not, on its own, a reason to hide the
        /// painted map - a lot whose house file will not mesh legitimately has none.
        /// </summary>
        public int FacadeCount { get; }

        /// <summary>Consecutive frames the world's draw threw.</summary>
        public int DrawFailures { get; }

        /// <summary>Whether the device has reset and the world has not drawn since.</summary>
        public bool DeviceLost { get; }

        /// <summary>How long the world has been attached, in seconds.</summary>
        public double SecondsSinceAttach { get; }

        /// <summary>Why the world could not be built, or <c>null</c>.</summary>
        public string Failure { get; }

        /// <summary>
        /// Triangles the last frame actually submitted, or <see cref="Unknown"/> when nobody is
        /// counting.
        ///
        /// <para>
        /// <b>The number the blank screen was hiding behind.</b> <see cref="TerrainTriangles"/> says
        /// what the mesh holds; this says what went to the device. A cull that removed every chunk,
        /// a render state that discarded every fragment, and a camera looking away from the map all
        /// leave the first number at three hundred thousand and this one at zero.
        /// </para>
        /// </summary>
        public int SubmittedTriangles { get; }

        /// <summary>
        /// Consecutive frames that submitted geometry, or <see cref="Unknown"/> when nobody is
        /// counting. The promotion gate.
        /// </summary>
        public int SubmittedFrames { get; }

        /// <summary>
        /// Whether anybody is counting what the world submits, as distinct from what its mesh
        /// holds. False means the gate has nothing to go on and falls back to the mesh's triangle
        /// count, which is the older and much weaker promise.
        /// </summary>
        public bool CountsSubmission
        {
            get { return SubmittedTriangles != Unknown || SubmittedFrames != Unknown; }
        }

        /// <summary>
        /// Triangles the lot facades submitted last frame, or <see cref="Unknown"/> when nobody is
        /// counting them.
        ///
        /// <para>
        /// <b>The number that says whether there are houses on screen.</b> The terrain's count
        /// answers "is there ground", which is not a neighbourhood: the field report was a world
        /// that had submitted 60660 terrain triangles three frames after attaching, promoted over
        /// the painted map, and showing the player a bare plain with the houses still streaming in.
        /// This answers the other half, and the gate reads both.
        /// </para>
        /// </summary>
        public int FacadeTriangles { get; }

        /// <summary>
        /// Lot facades the frustum cull kept last frame, or <see cref="Unknown"/> when nobody is
        /// counting them. Read against <see cref="FacadeTriangles"/> it separates "there are no
        /// houses in this view" from "there were houses in this view and every one of their draws
        /// failed" - the difference between the camera looking at the water and the facade pass
        /// being broken.
        /// </summary>
        public int FacadesVisible { get; }

        /// <summary>
        /// How many lots a complete facade pass would build on this map - the lots that have a
        /// house in them - or <see cref="Unknown"/> when nobody counted. The denominator of the
        /// build fraction, and zero is a real answer: a map with no houses in it has nothing to be
        /// missing.
        /// </summary>
        public int FacadeEligible { get; }

        /// <summary>
        /// Whether anybody is counting what the facades put on screen. False means the gate cannot
        /// see the houses and is skipped, exactly as an un-counted submission is: a signal that
        /// cannot see a thing is not evidence that the thing is missing, and treating it as one
        /// would hold every such world on the painted map for ever.
        /// </summary>
        public bool CountsFacades
        {
            get { return FacadeTriangles != Unknown || FacadesVisible != Unknown; }
        }

        /// <summary>
        /// How much of the map's housing is built, from 0 to 1, or <see cref="Unknown"/> when either
        /// half of the ratio is unknown. A map with no lots that have houses reads as 1, because
        /// there is nothing left to build on it.
        /// </summary>
        public double FacadeProgress
        {
            get
            {
                if (FacadeCount == Unknown || FacadeEligible == Unknown) return Unknown;
                if (FacadeEligible <= 0) return 1d;
                return FacadeCount / (double)FacadeEligible;
            }
        }

        /// <summary>
        /// The same health with the static probe counts filled in.
        /// </summary>
        /// <param name="groundQuads">Ground quads the raster ought to produce.</param>
        /// <param name="roadQuads">Road quads the raster ought to produce.</param>
        public NeighbourhoodWorldHealth Probed(int groundQuads, int roadQuads)
        {
            return new NeighbourhoodWorldHealth(Attached, MeshUploaded, TerrainTriangles, RoadTriangles,
                groundQuads, roadQuads, LotCount, FacadeCount, DrawFailures, DeviceLost,
                SecondsSinceAttach, Failure, SubmittedTriangles, SubmittedFrames, FacadeTriangles,
                FacadesVisible, FacadeEligible);
        }

        /// <summary>
        /// The same health as though no world had been attached. Used to re-report a healthy world
        /// under a setting that has just changed to 2D, where the world is about to be torn down
        /// and its numbers are no longer the interesting ones.
        /// </summary>
        public NeighbourhoodWorldHealth AsUnattached()
        {
            return new NeighbourhoodWorldHealth(false, false, 0, Unknown, GroundQuads, RoadQuads,
                LotCount, FacadeCount, 0, false, 0d, Failure, Unknown, Unknown, Unknown, Unknown,
                Unknown);
        }

        /// <summary>
        /// The draw-failure count for a world, merged from the two places that count one.
        /// </summary>
        /// <remarks>
        /// <b>Why a merge and not a pick.</b> A scene reports its own count so a scene that
        /// swallows a failure it can carry on past can still be believed, and the
        /// <c>NeighbourhoodWorldProxy</c> counts the ones that escaped the scene's draw. Those are
        /// the same event seen from either side, so the honest number is the larger of the two -
        /// reading only the scene's makes the proxy's counter dead, which is exactly the bug this
        /// closes: the proxy raised its count, the rule never saw it, and a world that threw twice
        /// in a row was never put back.
        /// </remarks>
        /// <param name="scene">Failures the scene reported, or zero.</param>
        /// <param name="proxy">Failures the proxy counted, or zero.</param>
        public static int MergeDrawFailures(int scene, int proxy)
        {
            return scene > proxy ? scene : proxy;
        }

        /// <summary>
        /// Whether either witness says the device is gone. A lost device is not always an
        /// exception, so either side seeing it is enough.
        /// </summary>
        /// <param name="scene">The scene's answer.</param>
        /// <param name="proxy">The proxy's answer.</param>
        public static bool MergeDeviceLost(bool scene, bool proxy)
        {
            return scene || proxy;
        }

        /// <summary>
        /// Why the world is in this state, preferring the message that named an exception.
        /// </summary>
        /// <param name="scene">The scene's last failure, or <c>null</c>.</param>
        /// <param name="proxy">The proxy's last failure, or <c>null</c>.</param>
        /// <param name="fallback">What to say when neither has one.</param>
        public static string MergeDrawFailure(string scene, string proxy, string fallback)
        {
            if (!string.IsNullOrEmpty(proxy)) return proxy;
            if (!string.IsNullOrEmpty(scene)) return scene;
            return fallback;
        }

        /// <summary>One line for the log, whether or not anything is wrong.</summary>
        public override string ToString()
        {
            if (!Attached)
            {
                return "no generated world" + (string.IsNullOrEmpty(Failure) ? "" : ": " + Failure);
            }
            return "generated world: " + (MeshUploaded ? "mesh uploaded" : "mesh still uploading")
                + ", " + TerrainTriangles + " triangle(s) in the mesh, " + FacadeCount + " facade(s), "
                + (CountsSubmission
                    ? SubmittedTriangles + " submitted last frame over " + SubmittedFrames
                        + " consecutive frame(s)"
                    : "submission not counted")
                + ", "
                + (CountsFacades
                    //The two halves of the house question in one clause: what reached the device
                    //this frame, and how much of the map has one. "38 facades" on its own reads as
                    //a finished street of houses whether it is 38 of 38 or 38 of 100.
                    ? FacadeTriangles + " facade triangle(s) over " + FacadesVisible + " visible lot(s), "
                        + FacadeCount + " of " + FacadeEligible + " built"
                    : "facades not counted")
                + ", "
                + (GroundQuads == Unknown ? "geometry not probed" : GroundQuads + " ground quad(s)")
                + (RoadQuads == Unknown ? "" : ", " + RoadQuads + " road quad(s)")
                + ", " + DrawFailures + " draw failure(s)"
                + (DeviceLost ? ", device lost" : "")
                + ", " + SecondsSinceAttach.ToString("0.0") + "s attached";
        }
    }

    /// <summary>
    /// What the neighbourhood panel should draw, and why - the pure decision behind
    /// <see cref="NeighbourhoodView"/> for everything that happens after the view exists.
    ///
    /// <para>
    /// <b>The contract this type exists to state.</b> The painted map is the <em>guaranteed</em>
    /// backdrop. The generated world is allowed to take the screen only once it has
    /// demonstrably produced something, and any later failure puts the painted map straight back.
    /// There is no state in which neither is drawn, which is why there is no "neither" member:
    /// <see cref="UseBitmap"/> is true for two of the three <see cref="NeighbourhoodBackdrop"/>
    /// values and the third cannot be reached without it having been true first.
    /// </para>
    ///
    /// <para>
    /// <b>Why it is separate from <see cref="NeighbourhoodViewSelection"/>.</b> That type answers
    /// "was a map generated", which is a question about the neighbourhood's files and is settled
    /// before there is a scene. This one answers "is the thing on the screen working", which is a
    /// question about the renderer and is re-asked every frame. The first cannot see a draw that
    /// throws; the second cannot see a sidecar that would not pack.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodViewDecision
    {
        internal NeighbourhoodViewDecision(
            NeighbourhoodView requested, NeighbourhoodBackdrop backdrop,
            bool proven, NeighbourhoodWorldFault fault, string reason)
        {
            Requested = NeighbourhoodViewSetting.Resolve(requested);
            Backdrop = backdrop;
            Proven = proven;
            Fault = fault;
            Reason = string.IsNullOrWhiteSpace(reason) ? "no reason was given" : reason;
        }

        /// <summary>The setting that was asked for.</summary>
        public NeighbourhoodView Requested { get; }

        /// <summary>Which backdrop to draw.</summary>
        public NeighbourhoodBackdrop Backdrop { get; }

        /// <summary>
        /// Whether the generated world has demonstrably produced something and is carrying the
        /// picture. True only for <see cref="NeighbourhoodBackdrop.GeneratedWorld"/>.
        /// </summary>
        public bool Proven { get; }

        /// <summary>What went wrong, or <see cref="NeighbourhoodWorldFault.None"/>.</summary>
        public NeighbourhoodWorldFault Fault { get; }

        /// <summary>Why this decision, in one sentence, for the log. Never null or blank.</summary>
        public string Reason { get; }

        /// <summary>
        /// Whether the painted map and its overlays are drawn. Always true except when the
        /// generated world is carrying the picture, so a caller cannot end up drawing neither.
        /// </summary>
        public bool UseBitmap
        {
            get { return Backdrop != NeighbourhoodBackdrop.GeneratedWorld; }
        }

        /// <summary>Whether a generated world is attached and drawing behind the controls.</summary>
        public bool UseWorld
        {
            get { return Backdrop != NeighbourhoodBackdrop.PaintedMap; }
        }

        /// <summary>Show 2D: the painted map, no 3D world.</summary>
        public bool Show2D
        {
            get { return Backdrop == NeighbourhoodBackdrop.PaintedMap; }
        }

        /// <summary>Show both: a world is attached but the painted map is still what is on top.</summary>
        public bool ShowBoth
        {
            get { return Backdrop == NeighbourhoodBackdrop.PaintedMapAndWorld; }
        }

        /// <summary>Show 3D: the generated world is carrying the picture.</summary>
        public bool Show3D
        {
            get { return Backdrop == NeighbourhoodBackdrop.GeneratedWorld; }
        }

        /// <summary>
        /// Whether 3D was asked for and the painted map ended up carrying the picture anyway. False
        /// when the player chose 2D - that is a choice, not a failure, and a log that cannot tell
        /// them apart is a log that cannot be read.
        /// </summary>
        public bool FellBack
        {
            get { return Requested == NeighbourhoodView.World3D && Fault != NeighbourhoodWorldFault.None; }
        }

        /// <summary>The decision for a setting, before a world has been tried.</summary>
        /// <param name="view">The stored setting.</param>
        public static NeighbourhoodViewDecision ForSetting(NeighbourhoodView view)
        {
            var selection = NeighbourhoodViewSelection.ForSetting(view);
            var backdrop = selection.Use3D
                ? NeighbourhoodBackdrop.PaintedMapAndWorld
                : NeighbourhoodBackdrop.PaintedMap;
            return new NeighbourhoodViewDecision(view, backdrop, false, NeighbourhoodWorldFault.None,
                selection.Reason);
        }

        public override string ToString()
        {
            return Backdrop + " (asked for " + NeighbourhoodViewSetting.Describe(Requested)
                + (FellBack ? ", fell back: " + Fault : "") + "): " + Reason;
        }
    }

    /// <summary>
    /// The whole of the backdrop contract, as a rule over values: which backdrop to draw given the
    /// setting and one frame's worth of facts about the generated world.
    ///
    /// <para>
    /// <b>The rule, in order.</b>
    /// </para>
    /// <list type="number">
    /// <item>The setting is not 3D. The painted map, for the reason the player chose. No
    /// generation, no scene, nothing read - and no fault, because nothing failed.</item>
    /// <item>No world attached. The painted map, with the reason generation or
    /// <c>TryCreate</c> gave.</item>
    /// <item>The device has been lost, or the world has thrown
    /// <see cref="DrawFailureLimit"/> consecutive draws. The painted map, straight back.</item>
    /// <item>The generated raster has no ground quads, or has lots but no road quads. The painted
    /// map - a world with nothing between the houses is the failure this rule exists for, and it
    /// is knowable before the GPU is touched.</item>
    /// <item>The mesh has not uploaded, or uploaded with nothing in it, and the world is still
    /// inside <see cref="MeshWarmUpSeconds"/>. <b>Both</b>: the painted map stays up as the
    /// guarantee while the worker finishes.</item>
    /// <item>The mesh has not uploaded and the window has closed. The painted map, with the fault
    /// named.</item>
    /// <item>The frame submitted nothing, or submitted for too few consecutive frames. Inside the
    /// warm-up window: <b>both</b>. Beyond it: the painted map, with
    /// <see cref="NeighbourhoodWorldFault.NothingSubmitted"/>.</item>
    /// <item>The world has lots in it, is drawing ground, and has no houses on screen - or not enough
    /// of them built. Inside the warm-up window: <b>both</b>, because they are streaming in and the
    /// world behind the map is filling up. Beyond it: the painted map, with the house fault named.
    /// See <see cref="MinimumFacadeFraction"/>.</item>
    /// <item>Otherwise - the mesh is up, the frame is drawing, and the map has its houses. The
    /// generated world, which has now demonstrably produced a neighbourhood.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Why the warm-up window exists.</b> The terrain mesh is built on a worker and uploaded
    /// on <c>GameThread.NextUpdate</c>, so a freshly attached world has zero triangles for at
    /// least one frame on any machine and for a noticeable while on a slow one. Without a window
    /// every one of those frames would read as a failure and the world would be thrown away before
    /// it started. With one, the painted map covers exactly the interval in which the world has
    /// nothing to show, and the swap happens on the first frame after it does.
    /// </para>
    ///
    /// <para>
    /// <b>Why facades are part of the gate, and the sequence while they build.</b> The previous
    /// version of this rule argued that a facade count was "evidence that the world is early"
    /// rather than "evidence that it is broken", and left the houses out of the gate on that basis.
    /// The field report refuted it: "60660 terrain triangle(s) ... on 3 consecutive frame(s), so it
    /// is carrying the picture", three frames after attach, with the facades still streaming. A
    /// plain is not a neighbourhood, and the ground between the houses is not what the player is
    /// asking to look at - the houses are.
    ///
    /// <para>
    /// The sequence that follows from that is not a cross-fade, and the reason is a property of the
    /// scene graph rather than of this rule: <c>GameFacade.Scenes</c> is the graphics layer
    /// <em>in front of</em> the UI layer, so the painted map is drawn <em>over</em> the world and
    /// cannot be seen behind it. So while the world streams, the map stays up opaque, the world
    /// keeps drawing underneath it and is warm, and the map comes off in a single cut on the first
    /// frame the world has its houses. The player sees the painted neighbourhood and then the
    /// generated one, and never a bare plain in between.
    /// </para>
    ///
    /// <para>
    /// <b>Why the gate is symmetric.</b> Every condition above is read off this frame's snapshot
    /// and nothing latches, so the same predicate that refuses to promote a world also un-promotes
    /// it: a world whose facades are all culled or whose last facade throws drops back to the
    /// painted map on the very next frame, and takes the screen again as soon as its houses are
    /// back. That is deliberate, and it is the same no-hysteresis choice the submission guard and
    /// the device-reset guard already make - a backdrop that has stopped being the neighbourhood has
    /// to be replaced immediately, not averaged back in over the next half second.
    /// </para>
    /// </summary>
    public static class NeighbourhoodBackdropRule
    {
        /// <summary>
        /// How long a freshly attached world gets to upload its mesh before the absence of one
        /// stops being "still building" and starts being a failure.
        ///
        /// <para>
        /// Generous, because the cost of being wrong is only that the painted map stays up a
        /// moment longer - and it is a background, so a late swap is nearly invisible, while an
        /// early one is the bug this whole rule exists to prevent.
        /// </para>
        /// </summary>
        public const double MeshWarmUpSeconds = 8d;

        /// <summary>
        /// How many consecutive failed draws a world gets before the painted map comes back.
        /// More than one, because a single bad frame is a hiccup the next frame recovers from.
        /// </summary>
        public const int DrawFailureLimit = 2;

        /// <summary>
        /// How many consecutive frames must have submitted geometry before the world may hide the
        /// painted map.
        ///
        /// <para>
        /// <b>More than one, and the reason is the bug this whole rule exists for.</b> The field
        /// report was a world that held 315424 triangles, was promoted over the painted map on the
        /// strength of that, and drew nothing at all. The count that promoted it was the mesh's
        /// triangle count, which says nothing about what a frame submits. The gate reads the
        /// submitted count now, and requires a run of frames rather than a single one, so a world
        /// which draws once on its way to failing does not take the screen on the way past.
        /// </para>
        /// <para>
        /// Three rather than two: one frame is a single sample of a device that is still settling,
        /// and the cost of being wrong here is precisely the bug being fixed. The cost of waiting
        /// three frames is a fiftieth of a second of the painted map at the moment of promotion,
        /// over a background nobody has started looking at yet.
        /// </para>
        /// </summary>
        public const int PromotionFrames = 3;

        /// <summary>
        /// How much of a map's housing has to be built before the generated world may take the
        /// screen away from the painted map: three quarters of the lots that have a house in them.
        ///
        /// <para>
        /// <b>Why a fraction and not a count of visible lots.</b> The scene's cull reports the
        /// facades it <em>kept</em>, which is the facades that were already built - so a world whose
        /// houses are all still building reports zero of them in view, and dividing by that would
        /// read 0/0 as either "nothing is wrong" or "everything is wrong" depending on which way
        /// round the division went. <see cref="NeighbourhoodWorldHealth.FacadeEligible"/> is the
        /// honest denominator: how many lots <em>could</em> have a house, counted from the layout.
        /// It is a stricter test than "the lots the camera can see", because a house that is missing
        /// two streets away is still a hole in the neighbourhood, and because the camera can be
        /// panned over water where the honest answer is "no houses here and none missing".
        /// </para>
        ///
        /// <para>
        /// <b>Why not all of them.</b> A lot whose <c>HouseNN.iff</c> will not read never gets a
        /// facade and never will - <see cref="NeighbourhoodFacadePlan.NeedsBuild"/> cannot be
        /// satisfied by a file that is not there - so demanding every lot would pin the painted map
        /// up for the rest of the session over one bad house file. Three quarters is well clear of
        /// the handful of lots that legitimately have nothing to show, and well short of the
        /// "38 of 100" that produced the field report.
        /// </para>
        /// </summary>
        public const double MinimumFacadeFraction = 0.75d;

        /// <summary>
        /// Whether a map's housing is built far enough for the world to be worth looking at.
        ///
        /// <para>
        /// An eligible count of zero is "yes": a neighbourhood of empty plots, or one whose lot
        /// table names nothing, has no house it can be failing to draw, and the terrain pass alone
        /// is the whole of what it has to show.
        /// </para>
        /// </summary>
        /// <param name="cached">Facades built.</param>
        /// <param name="eligible">Lots that have a house in them.</param>
        /// <param name="fraction">The bar to clear. Defaults to <see cref="MinimumFacadeFraction"/>.</param>
        public static bool EnoughFacades(int cached, int eligible,
            double fraction = MinimumFacadeFraction)
        {
            if (eligible <= 0) return true;
            if (cached <= 0) return false;
            return cached >= eligible * fraction;
        }

        /// <summary>
        /// Decides which backdrop to draw. Pure: reads a value, returns a value.
        /// </summary>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="health">What is known about the generated world right now.</param>
        /// <param name="warmUpSeconds">
        /// The mesh upload window. Defaults to <see cref="MeshWarmUpSeconds"/>; a parameter so a
        /// test can drive the boundary without a clock.
        /// </param>
        public static NeighbourhoodViewDecision Decide(
            NeighbourhoodView requested,
            NeighbourhoodWorldHealth health,
            double warmUpSeconds = MeshWarmUpSeconds)
        {
            var view = NeighbourhoodViewSetting.Resolve(requested);

            //1. Not 3D. Nothing above this line has been asked for, so nothing below it can have
            //failed: a 2D neighbourhood with a health report full of zeroes is still just 2D.
            if (view != NeighbourhoodView.World3D)
            {
                return NeighbourhoodViewDecision.ForSetting(view);
            }

            //2. No world. Generation and scene creation are both here, because from the panel's
            //point of view they are one fact - there is no world - and the reason distinguishes
            //them for the log.
            if (!health.Attached)
            {
                return Fallback(view, string.IsNullOrEmpty(health.Failure)
                    ? NeighbourhoodWorldFault.GenerationFailed
                    : NeighbourhoodWorldFault.SceneCreateFailed,
                    health.Failure);
            }

            //3. The renderer is not drawing. Checked before the geometry, because a world that
            //throws every frame is worse than one that is merely empty, and the painted map has to
            //come back either way.
            if (health.DeviceLost)
            {
                return Fallback(view, NeighbourhoodWorldFault.DeviceLost,
                    "the graphics device was reset and the generated world has not drawn since,"
                    + " so the painted map was put back while it rebuilds");
            }

            if (health.DrawFailures >= DrawFailureLimit)
            {
                return Fallback(view, NeighbourhoodWorldFault.DrawFailed,
                    "drawing the generated world failed " + health.DrawFailures + " times in a row"
                    + (string.IsNullOrEmpty(health.Failure) ? "" : " (" + health.Failure + ")")
                    + ", so the painted map was put back");
            }

            //4. The raster itself. Zero ground quads is a mesh of nothing at all, and no roads on a
            //neighbourhood that has lots in it is a world with no streets - which is the painted
            //map's ground between the houses, and the exact thing a house button floating over an
            //empty 3D field cannot replace.
            if (health.GroundQuads == 0)
            {
                return Fallback(view, NeighbourhoodWorldFault.NoGround,
                    "the generated map produced no ground quads, so there is nothing to draw"
                    + " between the houses; the painted map was put back");
            }

            if (health.RoadQuads == 0 && health.LotCount > 0)
            {
                return Fallback(view, NeighbourhoodWorldFault.NoRoads,
                    "the generated map produced no road quads for a neighbourhood of "
                    + health.LotCount + " lot(s), so there is nothing to draw between the houses;"
                    + " the painted map was put back");
            }

            //5. Still coming up. The painted map stays, and the world is still there behind it,
            //so the swap is one frame rather than a rebuild. "Not uploaded" is spelled two ways -
            //the mesh has not landed, or it landed with nothing in it - because both mean the same
            //thing to the player and only the scene can tell them apart.
            if (!health.MeshUploaded || health.TerrainTriangles <= 0)
            {
                if (health.SecondsSinceAttach <= warmUpSeconds)
                {
                    return new NeighbourhoodViewDecision(view, NeighbourhoodBackdrop.PaintedMapAndWorld,
                        false, NeighbourhoodWorldFault.None,
                        "the generated world is still uploading its mesh, so the painted map is"
                        + " still the backdrop (" + health.SecondsSinceAttach.ToString("0.0")
                        + "s of " + warmUpSeconds.ToString("0") + "s)");
                }

                return Fallback(view, NeighbourhoodWorldFault.MeshNeverUploaded,
                    "the generated world's mesh had not uploaded after "
                    + health.SecondsSinceAttach.ToString("0.0") + "s, so the painted map was put"
                    + " back instead");
            }

            //6. A world that submits nothing is a blank screen, and the rule's whole purpose is to
            //make that impossible. This is the half the field report was missing: the count that
            //mattered was the mesh's triangle count, and a world that holds three hundred thousand
            //triangles and culls every one of them is not carrying the picture. So when anybody is
            //counting what is actually submitted - which is the scene, through
            //INeighbourhoodRenderSignal - the gate is on that count, and on a run of frames of it.
            if (health.CountsSubmission)
            {
                if (health.SubmittedTriangles <= 0)
                {
                    //Inside the warm-up window this is still a world coming up rather than a world
                    //that has failed, so the painted map stays and the world keeps its place.
                    if (health.SecondsSinceAttach <= warmUpSeconds)
                    {
                        return new NeighbourhoodViewDecision(view, NeighbourhoodBackdrop.PaintedMapAndWorld,
                            false, NeighbourhoodWorldFault.None,
                            "the generated world is up but this frame submitted no geometry, so the"
                            + " painted map is still the backdrop (" + health.SecondsSinceAttach.ToString("0.0")
                            + "s of " + warmUpSeconds.ToString("0") + "s)");
                    }

                    return Fallback(view, NeighbourhoodWorldFault.NothingSubmitted,
                        "the generated world's mesh holds " + health.TerrainTriangles
                        + " triangle(s) but this frame submitted none of them - every chunk culled, or"
                        + " the camera is not looking at the map, or a render state is discarding it -"
                        + " so the painted map was put back");
                }

                if (health.SubmittedFrames < PromotionFrames)
                {
                    //Still both: the world has submitted something, but not yet for long enough to
                    // believe it. The painted map is up over it, so this costs nothing to look at.
                    return new NeighbourhoodViewDecision(view, NeighbourhoodBackdrop.PaintedMapAndWorld,
                        false, NeighbourhoodWorldFault.None,
                        "the generated world has submitted geometry on " + health.SubmittedFrames
                        + " of the " + PromotionFrames + " consecutive frames it needs before it can"
                        + " hide the painted map");
                }
            }

            //7. The houses. Everything above asks "is the world drawing", and this asks "is it
            //drawing a neighbourhood" - which is a different question, and the one the field report
            //was really about: 60660 terrain triangles on three consecutive frames, promoted, and
            //the player looking at a bare plain with the houses still streaming in behind it.
            //
            //Two conditions, and they are about different things. The first is about the *frame*:
            //are there any house triangles on the screen at all? The second is about the *build*:
            //has enough of the map's housing arrived? Either one alone lets the bad case through -
            //a first facade is on screen while a hundred more are still queued, and a map can be
            //fully built with every one of its facades culled this frame.
            if (health.LotCount > 0 && health.CountsFacades && health.FacadeEligible != 0)
            {
                if (health.FacadeTriangles <= 0)
                {
                    //Still coming up, or broken. The warm-up window is what tells the two apart, and
                    //inside it this is a world whose first houses have not been built yet - the map
                    //stays up over it and the cut is one frame rather than a rebuild.
                    if (health.SecondsSinceAttach <= warmUpSeconds)
                    {
                        return new NeighbourhoodViewDecision(view, NeighbourhoodBackdrop.PaintedMapAndWorld,
                            false, NeighbourhoodWorldFault.None,
                            "the generated world is drawing ground but no lot facade has reached the"
                            + " device yet, so the painted map is still the backdrop ("
                            + health.SecondsSinceAttach.ToString("0.0") + "s of "
                            + warmUpSeconds.ToString("0") + "s)");
                    }

                    return Fallback(view, NeighbourhoodWorldFault.NoFacadesOnScreen,
                        "the generated world is drawing ground ("
                        + (health.CountsSubmission
                            ? health.SubmittedTriangles + " terrain triangle(s) submitted"
                            : health.TerrainTriangles + " terrain triangle(s) in the mesh")
                        + ") and no lot facade at all"
                        + (health.FacadesVisible > 0
                            ? " - " + health.FacadesVisible + " facade(s) were in view and every one of"
                                + " their draws failed -"
                            : health.FacadeCount > 0
                                ? " - and none of the " + health.FacadeCount + " built facade(s) were"
                                    + " in view -"
                                : " - and no facade has been built for any of the "
                                    + health.FacadeEligible + " lot(s) that have one -")
                        + ", so the player would be looking at a plain with no houses on it; the"
                        + " painted map was put back");
                }

                if (!EnoughFacades(health.FacadeCount, health.FacadeEligible))
                {
                    if (health.SecondsSinceAttach <= warmUpSeconds)
                    {
                        return new NeighbourhoodViewDecision(view, NeighbourhoodBackdrop.PaintedMapAndWorld,
                            false, NeighbourhoodWorldFault.None,
                            "the generated world is still building its lot facades (" + health.FacadeCount
                            + " of " + health.FacadeEligible + " built, "
                            + health.FacadeTriangles + " facade triangle(s) on screen this frame), so"
                            + " the painted map stays up until there is a neighbourhood to look at");
                    }

                    return Fallback(view, NeighbourhoodWorldFault.FacadesIncomplete,
                        "the generated world drew " + health.FacadeTriangles
                        + " facade triangle(s) over " + health.FacadesVisible + " visible lot(s), but"
                        + " only " + health.FacadeCount + " of " + health.FacadeEligible
                        + " lot facades are built - fewer than the "
                        + (MinimumFacadeFraction * 100d).ToString("0") + "% it needs - so most of the"
                        + " neighbourhood is a bare plain; the painted map was put back");
                }
            }

            //8. It has produced something a player would call a neighbourhood. Only now may it
            //hide the painted map.
            return new NeighbourhoodViewDecision(view, NeighbourhoodBackdrop.GeneratedWorld, true,
                NeighbourhoodWorldFault.None,
                (health.CountsSubmission
                    ? "the generated world has submitted " + health.SubmittedTriangles
                        + " triangle(s) on " + health.SubmittedFrames + " consecutive frame(s)"
                    : "the generated world has drawn " + health.TerrainTriangles + " terrain triangle(s)")
                + (health.FacadeCount > 0 ? " and " + health.FacadeCount + " lot facade(s)" : "")
                + (health.CountsFacades
                    ? ", " + health.FacadeTriangles + " facade triangle(s) over "
                        + health.FacadesVisible + " visible lot(s) this frame"
                    : "")
                + ", so it is carrying the picture");
        }

        private static NeighbourhoodViewDecision Fallback(
            NeighbourhoodView view, NeighbourhoodWorldFault fault, string detail)
        {
            return new NeighbourhoodViewDecision(view, NeighbourhoodBackdrop.PaintedMap, false, fault,
                string.IsNullOrWhiteSpace(detail) ? "the painted map was put back" : detail);
        }

        /// <summary>Whether the panel needs to build a world, drop one, or leave it alone.</summary>
        /// <param name="requested">The view the player has chosen.</param>
        /// <param name="worldAttached">Whether a world is attached right now.</param>
        /// <returns>What the panel should do this frame.</returns>
        public static NeighbourhoodBackdropAction Reconcile(NeighbourhoodView requested, bool worldAttached)
        {
            var view = NeighbourhoodViewSetting.Resolve(requested);
            if (view != NeighbourhoodView.World3D)
            {
                //2D drops the world even if it is already there: the setting is the player's
                //instruction, and leaving a world attached behind a 2D map would keep spending
                //GPU on something nobody can see.
                return worldAttached ? NeighbourhoodBackdropAction.Detach : NeighbourhoodBackdropAction.Keep;
            }
            return worldAttached ? NeighbourhoodBackdropAction.Keep : NeighbourhoodBackdropAction.Attach;
        }

        /// <summary>
        /// The same decision, re-reported under a setting that has just changed. A healthy world
        /// that the player has just switched away from reports as 2D, for the reason they chose,
        /// rather than as a 3D failure nobody asked about.
        /// </summary>
        /// <param name="decision">The decision as it was.</param>
        /// <param name="requested">The view the player has chosen now.</param>
        public static NeighbourhoodViewDecision AfterSettingChange(
            NeighbourhoodViewDecision decision, NeighbourhoodView requested)
        {
            if (NeighbourhoodViewSetting.Resolve(requested) == NeighbourhoodView.World3D)
            {
                return decision;
            }
            return NeighbourhoodViewDecision.ForSetting(requested);
        }
    }

    /// <summary>What a frame's reconcile decided the panel should do.</summary>
    public enum NeighbourhoodBackdropAction
    {
        /// <summary>Nothing. The world is attached, or not wanted.</summary>
        Keep = 0,

        /// <summary>Build a world and attach it. The painted map does not move until it is ready.</summary>
        Attach = 1,

        /// <summary>Tear the world down and put the painted map back.</summary>
        Detach = 2
    }

    /// <summary>
    /// What the backdrop element and its overlays should be showing, as a value.
    ///
    /// <para>
    /// <b>Why the swap is a value and not two methods.</b> The painted map is emptied to a 1x1
    /// transparent quad and put back to its original size, and the overlays are hidden and shown,
    /// and getting any one of those three wrong produces a screen that is not "2D" or "3D" but
    /// something broken - a postage stamp, or waves floating over a 3D world, or a field of
    /// nothing. Reducing all of it to one value makes "the element is never in a state the
    /// decision did not ask for" an assertion, and it is the assertion that keeps the guarantee.
    /// </para>
    ///
    /// <para>
    /// There are exactly two states. That is the point: there is no third state in which the
    /// backdrop is showing something nobody chose, because there is nothing else it could show.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodBackdropFrame
    {
        private readonly bool _paintedMap;

        private NeighbourhoodBackdropFrame(bool paintedMap)
        {
            _paintedMap = paintedMap;
        }

        /// <summary>
        /// The painted map is on the element, at its own size, with its overlays over it. This is
        /// what "2D" and "3D warming up" both look like, and it is what the element is put back to
        /// whenever anything at all goes wrong.
        /// </summary>
        public static readonly NeighbourhoodBackdropFrame PaintedMap =
            new NeighbourhoodBackdropFrame(true);

        /// <summary>
        /// The element carries a 1x1 fully transparent quad: no picture, no overlays, and still
        /// the element that "clicking the backdrop resets the zoom" is bound to, so the input
        /// behaviour is identical in both views without a second path.
        /// </summary>
        public static readonly NeighbourhoodBackdropFrame OverWorld =
            new NeighbourhoodBackdropFrame(false);

        /// <summary>Whether the painted map is the thing on screen.</summary>
        public bool IsPaintedMap
        {
            get { return _paintedMap; }
        }

        /// <summary>
        /// Whether the overlays are visible. Always the same answer as
        /// <see cref="IsPaintedMap"/>, because an overlay is placed in the painted map's own
        /// 800x600 pixel space and over a 3D world it is a floating label rather than water.
        /// </summary>
        public bool OverlaysVisible
        {
            get { return IsPaintedMap; }
        }

        /// <summary>
        /// Whether the element has been shrunk to the click-only quad. Reading this off the same
        /// value is what stops "put the map back" and "take the map away" from disagreeing about
        /// the element's size.
        /// </summary>
        public bool IsTransparentQuad
        {
            get { return !IsPaintedMap; }
        }

        /// <summary>
        /// The frame a decision asks for, given whether there is a painted map to show at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A world that is carrying the picture, on an install whose bitmap would not load, gets
        /// <see cref="PaintedMap"/> - which draws the clear colour and nothing else. That is not
        /// the empty string it looks like: the alternative is shrinking an element that is
        /// already showing nothing, which buys no pixels and costs the backdrop click its hit
        /// area, so a player could no longer click away from a zoomed-in lot.
        /// </para>
        /// </remarks>
        /// </summary>
        /// <param name="decision">What should be on show.</param>
        /// <param name="hasPaintedMap">
        /// Whether a painted map was actually loaded for this neighbourhood.
        /// </param>
        public static NeighbourhoodBackdropFrame For(NeighbourhoodViewDecision decision, bool hasPaintedMap)
        {
            if (!hasPaintedMap) return PaintedMap;
            return decision.UseBitmap ? PaintedMap : OverWorld;
        }

        /// <summary>Whether two frames are different, and so worth touching the element for.</summary>
        /// <param name="other">The frame being compared against.</param>
        public bool DiffersFrom(NeighbourhoodBackdropFrame other)
        {
            return IsPaintedMap != other.IsPaintedMap;
        }

        public override string ToString()
        {
            return IsPaintedMap ? "painted map and its overlays" : "transparent quad over the 3D world";
        }
    }

    /// <summary>
    /// What a generated world says about itself, so the backdrop rule can be given answers rather
    /// than a scene.
    ///
    /// <para>
    /// <b>Implemented by <c>Neighbourhood3DView</c>, and the two numbers that mattered.</b> The
    /// panel used to derive "uploaded" from "the device holds a triangle" and take draw failures
    /// from the proxy's exception count. Both were blind to the bug this interface now closes: a
    /// mesh can hold three hundred thousand triangles and submit none of them - every chunk culled,
    /// every state wrong, the camera looking at nothing - and the derived answer called that a
    /// world that had "demonstrably produced something". So a world now reports
    /// <see cref="SubmittedTriangles"/> for the last frame and <see cref="SubmittedFrames"/> for
    /// the run of consecutive frames that have submitted geometry, and the promotion gate keys on
    /// the second of those. A count of what a mesh <em>holds</em> is not evidence about what a
    /// frame <em>submitted</em>, and the blank screen was exactly that difference.
    /// </para>
    ///
    /// <para>
    /// Kept in this file rather than next to the scene so that the contract the backdrop depends
    /// on is stated independently of whoever implements it, and so a test can describe a world's
    /// health without a graphics device.
    /// </para>
    ///
    /// <para>
    /// <b>What is deliberately not on here: the houses.</b> The promotion gate also needs to know
    /// how much house geometry a frame submitted and how far the facade build has got, and
    /// <see cref="NeighbourhoodWorldHealth"/> carries both. They are read off the scene's own
    /// <see cref="NeighbourhoodFrameReport"/> and its <c>FacadeCount</c>/<c>FacadeEligible</c>
    /// rather than added here, because extending this interface is a change to every implementer -
    /// the scene included - and the numbers are already published, one layer down, by the same
    /// object that would have to implement them. An interface that grew a member the only
    /// implementer did not add is a compile error, which is at least a loud way to be wrong; a
    /// duplicate count declared in two places is a quiet one.
    /// </para>
    /// </summary>
    public interface INeighbourhoodRenderSignal
    {
        /// <summary>
        /// Whether a terrain mesh has finished uploading at least once. Distinct from
        /// <see cref="TerrainTriangles"/> being non-zero: an upload that produced nothing has
        /// happened, and it is a failure rather than a wait.
        /// </summary>
        bool MeshUploaded { get; }

        /// <summary>Triangles the device currently holds, across every terrain layer.</summary>
        int TerrainTriangles { get; }

        /// <summary>Road triangles the device currently holds, or a negative number for unknown.</summary>
        int RoadTriangles { get; }

        /// <summary>Lot facades cached so far.</summary>
        int FacadeCount { get; }

        /// <summary>
        /// Triangles the last frame actually submitted to the device, across terrain and roads.
        ///
        /// <para>
        /// <b>Zero means the screen is about to go blank</b>, whatever
        /// <see cref="TerrainTriangles"/> says. This is the difference between a mesh that holds
        /// geometry and a frame that drew it, and it is the number that lets the painted map come
        /// back when a cull, a camera or a render state goes wrong.
        /// </para>
        /// </summary>
        int SubmittedTriangles { get; }

        /// <summary>
        /// Consecutive frames that have submitted geometry, reset to zero by a frame that submits
        /// none. The promotion gate is on this rather than on a single frame's count, so a world
        /// that happens to draw one frame on its way to failing does not get the screen.
        /// </summary>
        int SubmittedFrames { get; }

        /// <summary>
        /// Consecutive draws that threw. Kept here as well as on the proxy so that a scene which
        /// wants to swallow its own failures - so it can keep drawing its remaining geometry - can
        /// still report them.
        /// </summary>
        int DrawFailures { get; }

        /// <summary>
        /// Whether the graphics device is gone, or was reset and nothing has been drawn since. A
        /// lost device is not always an exception: Direct3D9 hands back a failed result, and the
        /// device's own state is the only honest witness.
        /// </summary>
        bool DeviceLost { get; }

        /// <summary>Why the world is in this state, for the log. Never null.</summary>
        string LastDrawFailure { get; }
    }

    /// <summary>
    /// What one frame of the generated neighbourhood actually submitted, as a value.
    ///
    /// <para>
    /// <b>This is the report the field did not have.</b> The log said "315424 terrain triangles" and
    /// the screen was blank, and nothing in that line could tell the difference between a mesh
    /// that holds geometry and a frame that drew some of it. So this counts the submission, and
    /// carries the camera it was submitted against: the number of triangles and indices, the draw
    /// calls, how many chunks of the grid were visible out of the whole, the level of detail in
    /// use, the camera's target, position and zoom, the view-projection, and whether the terrain
    /// and facade passes ran at all.
    /// </para>
    ///
    /// <para>
    /// <b>Throttled, not per frame.</b> <see cref="ShouldLog"/> is the whole throttle: the first
    /// few frames, then a slow heartbeat while nothing is being submitted, then on a change. A log
    /// line every frame is a log nobody reads, and the line that has to be there is the one that
    /// says a world has stopped drawing.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodFrameReport
    {
        /// <summary>How many frames of quiet the heartbeat is, in frames. A second or so at 60Hz.</summary>
        public const int HeartbeatFrames = 120;

        /// <summary>How many frames at the start of a world are always logged.</summary>
        public const int OpeningFrames = 4;

        /// <summary>Frames drawn since the report was created, 1-based.</summary>
        public int Frame { get; private set; }

        /// <summary>
        /// Why the frame drew nothing, or <c>null</c> if it got as far as the draw. A frame that
        /// returned early always says why here.
        /// </summary>
        public string SkipReason { get; private set; }

        /// <summary>Whether the draw ran at all, rather than returning before the device.</summary>
        public bool DrawRan { get; private set; }

        /// <summary>Triangles submitted this frame, terrain and roads.</summary>
        public int Submitted { get; internal set; }

        /// <summary>Indices submitted this frame, so three per triangle unless a draw threw.</summary>
        public int Indices { get; internal set; }

        /// <summary>Draw calls issued this frame.</summary>
        public int DrawCalls { get; internal set; }

        /// <summary>Chunks of the grid the cull kept.</summary>
        public int ChunksVisible { get; internal set; }

        /// <summary>Chunks in the grid.</summary>
        public int ChunksTotal { get; internal set; }

        /// <summary>Road triangles submitted this frame.</summary>
        public int RoadTriangles { get; internal set; }

        /// <summary>Triangles in the mesh the device holds, which is not what was submitted.</summary>
        public int MeshTriangles { get; internal set; }

        /// <summary>Whether the mesh has uploaded.</summary>
        public bool MeshReady { get; internal set; }

        /// <summary>Whether the terrain pass submitted anything this frame.</summary>
        public bool TerrainDrawn { get; internal set; }

        /// <summary>Whether there were lot facades for the facade pass to submit this frame.</summary>
        public bool FacadeDrawn { get; internal set; }

        /// <summary>
        /// Triangles the lot facades submitted this frame, and the draw calls they issued.
        /// </summary>
        /// <remarks>
        /// Its own number rather than a bool, because "there were facades cached" and "the facades
        /// put 4,128 triangles on screen" are different sentences and only the second one tells you
        /// whether the houses are there. A neighbourhood whose terrain draws and whose facades do
        /// not is a flat plain with no houses on it, and a report that said "facades drawn" for
        /// that is worse than one that said nothing.
        /// </remarks>
        public int FacadeTriangles { get; internal set; }

        /// <summary>Draw calls the lot facades issued this frame, one per surface.</summary>
        public int FacadeDrawCalls { get; internal set; }

        /// <summary>How many lot facades were inside the frustum this frame.</summary>
        public int FacadesVisible { get; internal set; }

        /// <summary>
        /// Triangles the trees submitted this frame, so a generated neighbourhood that has ground
        /// but no wood on it is one report line rather than a screenshot.
        /// </summary>
        public int FoliageTriangles { get; internal set; }

        /// <summary>The level of detail the terrain pass submitted.</summary>
        public NeighbourhoodMeshLod Lod { get; internal set; }

        /// <summary>Where the camera looked.</summary>
        public Vector3 CameraTarget { get; internal set; }

        /// <summary>Where the camera stood.</summary>
        public Vector3 CameraPosition { get; internal set; }

        /// <summary>How wide the view was, in tiles, before distance turned it into an angle.</summary>
        public float TilesAcross { get; internal set; }

        /// <summary>
        /// How far away the distance fade completes this frame, in world units. Reported because the
        /// fog scales with the camera's distance: a fog distance that is wrong for the zoom is a
        /// white-out or a flat sheet, and neither is diagnosable from a triangle count.
        /// </summary>
        public float FogDistance { get; internal set; }

        /// <summary>The combined camera matrix this frame drew with.</summary>
        public Matrix ViewProjection { get; internal set; }

        /// <summary>The frustum the cull was tested against.</summary>
        public BoundingFrustum Frustum { get; internal set; }

        /// <summary>The tiles the cull found inside the frustum.</summary>
        public Rectangle VisibleRegion { get; internal set; }

        /// <summary>Whether <see cref="VisibleRegion"/> means anything.</summary>
        public bool HasVisibleRegion { get; internal set; }

        /// <summary>How many consecutive frames have submitted geometry.</summary>
        public int ProvenFrames { get; private set; }

        private int _lastLoggedSubmitted = -1;
        private int _lastLoggedVisible = -1;
        private int _lastLoggedLod = -1;

        /// <summary>Starts a new frame's report, carrying the proven-frame run across.</summary>
        /// <param name="frame">The frame number, 1-based.</param>
        /// <param name="placement">The camera placement in force, for the log line.</param>
        public void Reset(int frame, NeighbourhoodCameraPlacement placement)
        {
            //The run is carried across from the frame that just ended, so it has to be read before
            //the counts are cleared. A frame that submitted nothing resets it to zero, which is the
            //whole point: one good frame out of a hundred failures is not a world that has
            //demonstrated anything.
            var previousSubmitted = Submitted;

            Frame = frame;
            DrawRan = false;
            SkipReason = null;
            Submitted = 0;
            Indices = 0;
            DrawCalls = 0;
            ChunksVisible = 0;
            ChunksTotal = NeighbourhoodCityMeshBuilder.ChunkCount;
            RoadTriangles = 0;
            TerrainDrawn = false;
            FacadeDrawn = false;
            FacadeTriangles = 0;
            FacadeDrawCalls = 0;
            FacadesVisible = 0;
            FoliageTriangles = 0;
            Lod = new NeighbourhoodMeshLod(NeighbourhoodMeshLod.Full);
            CameraTarget = placement.Target;
            CameraPosition = placement.Position;
            TilesAcross = placement.TilesAcross;
            FogDistance = placement.FogDistance;
            Frustum = null;
            HasVisibleRegion = false;
            VisibleRegion = new Rectangle();
            ViewProjection = Matrix.Identity;

            ProvenFrames = previousSubmitted > 0 ? ProvenFrames + 1 : 0;
        }

        /// <summary>Records that the frame returned before the device.</summary>
        /// <param name="why">One sentence, for the log.</param>
        public void Skip(string why)
        {
            DrawRan = false;
            SkipReason = string.IsNullOrWhiteSpace(why) ? "the frame did not reach the device" : why;
            Submitted = 0;
            Indices = 0;
            DrawCalls = 0;
            TerrainDrawn = false;
            FacadeDrawn = false;
            FacadeTriangles = 0;
            FacadeDrawCalls = 0;
            FacadesVisible = 0;
            FoliageTriangles = 0;
            //The run ends here, not at the next Reset. A frame that submitted nothing has to take
            //the run to zero immediately, because this is the value the panel reads at the end of
            //this frame - a caller that put the promotion gate on a run that had not been broken
            //yet would let a blank world keep the screen for one more frame.
            ProvenFrames = 0;
        }

        /// <summary>
        /// Records that the frame reached the device and submitted this much.
        /// </summary>
        /// <param name="triangles">Triangles submitted.</param>
        /// <param name="indices">Indices submitted.</param>
        /// <param name="drawCalls">Draw calls issued.</param>
        /// <param name="chunksVisible">Chunks the cull kept.</param>
        /// <param name="terrainDrawn">Whether the terrain pass submitted anything.</param>
        public void Submit(int triangles, int indices, int drawCalls, int chunksVisible,
            bool terrainDrawn = true)
        {
            DrawRan = true;
            SkipReason = null;
            Submitted = triangles;
            Indices = indices;
            DrawCalls = drawCalls;
            ChunksVisible = chunksVisible;
            TerrainDrawn = terrainDrawn;
        }

        /// <summary>
        /// Records what the facade and foliage passes submitted, alongside the terrain's numbers.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Submit"/> because the three passes are counted by three
        /// different owners - the terrain renderer, the facade cache and the foliage pass - and
        /// <see cref="Submit"/> is the terrain's. They are also reported separately rather than
        /// summed into one number: a total of 315,000 triangles is the same whether it is all
        /// ground or mostly ground with the houses missing, and the total is what the field report
        /// was reading.
        /// </remarks>
        /// <param name="facadeTriangles">Triangles the facades submitted.</param>
        /// <param name="facadeDrawCalls">Draw calls the facades issued.</param>
        /// <param name="facadesVisible">Facades inside the frustum.</param>
        /// <param name="foliageTriangles">Triangles the trees submitted.</param>
        public void SubmitProps(int facadeTriangles, int facadeDrawCalls, int facadesVisible,
            int foliageTriangles)
        {
            FacadeTriangles = facadeTriangles < 0 ? 0 : facadeTriangles;
            FacadeDrawCalls = facadeDrawCalls < 0 ? 0 : facadeDrawCalls;
            FacadesVisible = facadesVisible < 0 ? 0 : facadesVisible;
            FoliageTriangles = foliageTriangles < 0 ? 0 : foliageTriangles;
            FacadeDrawn = FacadeTriangles > 0;
        }

        /// <summary>Whether this frame has submitted any geometry at all.</summary>
        public bool SubmittedGeometry
        {
            get { return DrawRan && Submitted > 0; }
        }

        /// <summary>
        /// Whether this report is worth a log line, and marks it as taken so the next call is
        /// judged against it.
        /// </summary>
        public bool ShouldLog
        {
            get
            {
                //The opening: what the first few frames submitted is the first thing worth knowing,
                //and it is when a world that is never going to draw says so.
                var wanted = Frame <= OpeningFrames
                    //A world that has stopped drawing is the case that has to reach a log, so a
                    //silent world gets a heartbeat and a working one does not.
                    || (Submitted <= 0 && Frame % HeartbeatFrames == 0)
                    || Submitted != _lastLoggedSubmitted
                    || ChunksVisible != _lastLoggedVisible
                    || (int)Lod.Factor != _lastLoggedLod;
                if (!wanted) return false;
                _lastLoggedSubmitted = Submitted;
                _lastLoggedVisible = ChunksVisible;
                _lastLoggedLod = Lod.Factor;
                return true;
            }
        }

        /// <summary>One line for the log, whether or not anything is wrong.</summary>
        public override string ToString()
        {
            if (!DrawRan && SkipReason != null)
            {
                return "frame " + Frame + " drew nothing: " + SkipReason
                    + " (the mesh holds " + MeshTriangles + " triangle(s), which is not the same"
                    + " thing as submitting any)";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("frame ").Append(Frame).Append(" submitted ")
              .Append(Submitted).Append(" triangle(s) / ").Append(Indices).Append(" index(es) in ")
              .Append(DrawCalls).Append(" draw call(s)");
            //The three passes, each with its own count. Terrain, facades and trees are drawn by
            //three different owners against three different techniques, and any one of them can
            //fail on its own - so a single "submitted N triangles" is a number that cannot say
            //which of the three the neighbourhood is missing.
            sb.Append("; terrain ").Append(Submitted).Append(" triangle(s) (")
              .Append(TerrainDrawn ? "DREW" : "drew NOTHING").Append(')');
            sb.Append(", facades ").Append(FacadeTriangles).Append(" triangle(s) in ")
              .Append(FacadeDrawCalls).Append(" draw call(s) over ").Append(FacadesVisible)
              .Append(" visible lot(s)");
            sb.Append(", trees ").Append(FoliageTriangles).Append(" triangle(s)");
            sb.Append("; ").Append(ChunksVisible).Append('/').Append(ChunksTotal).Append(" chunks")
              .Append(" at ").Append(Lod);
            sb.Append("; camera target (").Append(CameraTarget.X.ToString("0.0")).Append(", ")
              .Append(CameraTarget.Y.ToString("0.0")).Append(", ").Append(CameraTarget.Z.ToString("0.0"))
              .Append(") position (").Append(CameraPosition.X.ToString("0.0")).Append(", ")
              .Append(CameraPosition.Y.ToString("0.0")).Append(", ").Append(CameraPosition.Z.ToString("0.0"))
              .Append(") across ").Append(TilesAcross.ToString("0.0")).Append(" tiles")
              .Append(" (fog over ").Append(FogDistance.ToString("0")).Append(" units)");
            sb.Append("; view-projection ").Append(ViewProjection);
            if (Frustum != null)
            {
                sb.Append("; frustum ").Append(
                    Frustum.Contains(NeighbourhoodTerrainRenderer.WholeMapBounds()) != ContainmentType.Disjoint
                        ? "covers the whole map"
                        : "does NOT cover the whole map");
            }
            if (HasVisibleRegion)
            {
                sb.Append("; visible tiles ").Append(VisibleRegion.X).Append(',').Append(VisibleRegion.Y)
                  .Append(' ').Append(VisibleRegion.Width).Append('x').Append(VisibleRegion.Height);
            }
            sb.Append("; ").Append(ProvenFrames).Append(" consecutive frame(s) have submitted"
                + " geometry");
            return sb.ToString();
        }

    }
}
