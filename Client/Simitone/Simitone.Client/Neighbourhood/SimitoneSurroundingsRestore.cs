/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.LotView;
using FSO.LotView.Components;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Marshals.Hollow;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.Marshals.Hollow;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Builds the lots around the one the player is standing in as real FreeSO sub-worlds, so that
    /// the neighbours are lots rather than facades. B2's FreeSO bridge, and the only file in this
    /// feature that touches a <c>VM</c> or a graphics device.
    ///
    /// <para><b>It is a sibling of <c>VMLotTerrainRestoreTools.RestoreSurroundings</c>, not a
    /// replacement for it.</b> §6 B2 task 1 and §11 landmine 1 say the same thing: that method is
    /// TSO-shaped - a 3x3 grid keyed on a packed <c>vm.TSOState.LotID</c>, with
    /// <c>(size - 2)</c> spacing and its terrain read out of <c>vm.TSOState.Terrain</c>, which a
    /// TS1 lot does not have (landmine 2) - and that it must not be edited. So this file reuses its
    /// <em>helpers</em> - <c>RestoreTerrain</c>, <c>RestoreHeight</c>, <c>GetBaseLevel</c>,
    /// <c>EnsureCoreObjects</c> - against a terrain this codebase fills in from the generated map,
    /// and does the selection and the placement itself.</para>
    ///
    /// <para><b>Game thread only, and it says so.</b> §7 of the handoff and the remarks on
    /// <see cref="LotStore"/> both make it a rule rather than a convention: a <c>VM</c> is not
    /// thread-safe, this stands one up per neighbour, and the only thing entitled to do that is the
    /// thread already bringing a lot up. There is no lock here and no assertion, because this type
    /// is called from exactly one place and either would be a claim nobody maintains.</para>
    ///
    /// <para><b>It never throws, and a neighbour that cannot be built costs that
    /// neighbour.</b> B2's acceptance criterion is "a missing/broken neighbour IFF is skipped, not
    /// fatal", so every neighbour is built inside its own <c>try</c>: on a failure the half-built
    /// sub-world is disposed, one warning naming the lot goes to the log, and the loop carries on.
    /// A <c>false</c> return means the bridge could not start at all - no VM, no plan, no device -
    /// and even then the reason is a sentence and not an exception.</para>
    ///
    /// <para><b>Before the loop, a neighbour's contents are read out of its own house file.</b> A
    /// neighbour is hollow at plan time only if the <see cref="LotStore"/> holds a marshal for it,
    /// and §5.3's store only holds lots somebody has entered - so in a generated neighbourhood every
    /// neighbour of a lot the player has never entered arrives blank, and the street is full of
    /// empty plots where the houses should be. <see cref="ResolveHollows"/> reads them out first,
    /// through <see cref="NeighbourHollowLoader"/>, and hands the answers back as a plan, so
    /// nothing below this point can tell the difference between a marshal out of memory and a
    /// marshal out of a <c>HouseNN.iff</c>. A lot that cannot be read that way is built blank with
    /// a line naming it, which is the same lot, at the same height, facing the same way, with
    /// nothing on it - and that is still the outcome the blank branch below produces, unchanged.</para>
    ///
    /// <para><b>What it builds a neighbour's ground out of, and where that answer comes
    /// from.</b> A neighbour's own ground is a graded, per-tile height field, and it lives in that
    /// lot's own <c>HouseNN.iff</c> as its <c>ARRY(0)</c> plane and nowhere else: the generated
    /// neighbourhood pins every tile of a lot's footprint to that lot's <em>median</em> altitude
    /// (<see cref="TerrainSeamSolver"/>), which is a correct boundary condition and a flat answer to
    /// "what shape is this lot". So the plan reads the plane -
    /// <see cref="SimitoneArryPlanes"/> - and hands this file two things already computed from it:
    /// <see cref="SimitoneSurroundingsNeighbour.Terrain"/>, whose sixteen samples are the graded
    /// field the helpers hang the lot at through <c>RestoreHeight</c> /
    /// <c>GetBaseLevel</c>, and <see cref="SimitoneSurroundingsNeighbour.SubWorldAltitudes"/>, the
    /// size-squared field <see cref="ApplySurroundGround"/> copies over the sub-world's own
    /// <c>Terrain.Heights</c> - which <em>is</em> the neighbour's drawn ground. The plane itself
    /// rides along as <see cref="SimitoneSurroundingsNeighbour.ArryAltitude"/> so that this file,
    /// which has no generated map and no content manager, can still say which neighbours are graded
    /// and which are pinned; see <see cref="DescribeHeights"/>.</para>
    ///
    /// <para><b>Two levels of cleanup, because there are two levels of failure.</b> A neighbour
    /// that throws inside <see cref="BuildOne"/> is disposed there - it is the only holder of the
    /// reference to a sub-world that failed before it came back - and again by the caller's
    /// <c>try</c>. A failure <em>outside</em> a neighbour - the list add, or
    /// <c>InitSubWorlds</c> - is the other case, and it is handled by tracking what this call put
    /// on the blueprint and taking all of it back down. Leaving those behind would leak a light
    /// map, an ambient light texture and a world architecture per failure across a long session,
    /// and would draw sub-worlds that no plan accounts for; the list is shared across lot switches
    /// (<c>VMContext</c> hands the old blueprint's list to the new one), so nothing else would ever
    /// reach them.</para>
    ///
    /// <para><b><c>InitSubWorlds</c> is called once, after the loop.</b> It measures the lot view's
    /// sky bounds from the whole set of sub-worlds and walks every one of their altitude arrays, so
    /// a per-neighbour call would be quadratic and would still be stale at the end.</para>
    ///
    /// <para><b>Two places this file deliberately does something the TSO original does
    /// not</b>, both because the helpers were written for TSO lots and this is a TS1 one:</para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>The temporary VM is built with <c>TS1 = false</c>.</b> <c>VM.Init</c> chooses its lot
    ///     state with <c>PlatformState = TS1 ? VMTS1LotState : VMTSOLotState</c>, and every helper
    ///     here reads <c>vm.TSOState</c> unconditionally - <c>RestoreTerrain</c> for
    ///     <c>ObjectLimit</c> and <c>Size</c>, <c>RestoreHeight</c> for <c>Size</c> and
    ///     <c>OwnerID</c>. <c>vm.TSOState</c> is null on a TS1 lot state, so the helpers would
    ///     throw on the first neighbour. The flag is set before <c>Init()</c> and the lot state is
    ///     then ours for good: <c>HollowLoad</c> rebuilds the <c>VMContext</c>, but the platform
    ///     state is a field of the <c>VM</c>, not of the context. Entity states are unaffected -
    ///     those are chosen by the static <c>VM.GlobTS1</c>, which still says TS1, so a hollow
    ///     neighbour's objects load as the TS1 objects they were marshalled as.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A non-zero object limit.</b> <c>RestoreTerrain</c> ends with
    ///     <c>if (vm.GetGlobalValue(11) == -1 || vm.TSOState.ObjectLimit == 0)</c> and then
    ///     overwrites the high half of <c>TSOState.Size</c> with <c>PickRoadDir(roads)</c> - which
    ///     reads TSO's road byte as a four-corner fan rather than the layout's
    ///     <see cref="NeighbourhoodLot.DoorSide"/>. A fresh VM has global 11 at 0 and an object
    ///     limit at 0, so that condition is true and the direction this file set two lines earlier
    ///     would be thrown away, putting every neighbour's mailbox, bin and car portal on the wrong
    ///     edge. A lot with no limit is given <see cref="BlankObjectLimit"/>; a hollow lot keeps
    ///     whatever its own context had.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A <c>BuildableSizes</c> index, not a tile count, in the low half of
    ///     <c>TSOState.Size</c>.</b> <c>GetTSOBuildableArea</c> does
    ///     <c>BuildableSizes[lotSInfo &amp; 255]</c> against an eleven-entry table, and this file
    ///     used to write the lot's side in tiles - so a 16- or 64-tile lot indexed it at 16 or 64.
    ///     It happens not to have thrown, because <c>RestoreHeight</c> replaces the whole of
    ///     <c>lotSInfo</c> with 10 when <c>OwnerID</c> is 0, but that is luck rather than a
    ///     contract and <c>UpdateTSOBuildableArea</c> makes no such override. See
    ///     <see cref="SizeFor"/> and <see cref="BuildableSizeIndexFor"/>.
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>What it does not do.</b> It does not call
    /// <c>VMLotTerrainRestoreTools.PopulateBlankTerrain</c>, which the TSO original does for a
    /// neighbour with no contents. That method scatters fifteen to thirty-five objects chosen from
    /// <c>BlankTerrainObjects</c>, a table of TSO world-object GUIDs picked for TSO's five terrain
    /// types, and a TS1 install need not have any of them - on a GUID the content manager cannot
    /// resolve, the create is at best a silent no-op and at worst a throw from inside object
    /// construction. A bare, correctly shaped, correctly levelled lot is the honest fallback for a
    /// neighbour whose house file could not be read, and it is the one thing on this path that
    /// cannot need content the player might not own.</para>
    ///
    /// <para><b>The street, and who owns which half of it.</b> A neighbour's own
    /// <c>RestoreRoad</c> band reaches <see cref="SimitoneLotViewRoad.HalfStreetDepth"/> tiles into
    /// the <c>Size x Size</c> blueprint this file builds for it, and the packer reserves
    /// <see cref="SimitoneLotViewRoad.StreetCorridorTiles"/> of corridor between two lots - so the
    /// street a player sees is the two half-streets the engine draws, either side of the corridor
    /// <see cref="SimitoneLotViewRoadBand"/> draws, and the two meet at local tile
    /// <see cref="SimitoneLotViewRoad.TarmacFromEdge"/> of each lot with nothing in between and
    /// nothing drawn twice. <see cref="HalfStreetAgreesWithTheBand"/> is that as one question: the
    /// road byte handed to <c>RestoreTerrain</c> here and read by the band on the surround's side is
    /// the same byte, so the edges cannot drift apart, and the question checks the tiles.</para>
    /// </summary>
    public static class SimitoneSurroundingsRestore
    {
        /// <summary>
        /// The object limit a neighbour lot is given when its own is zero, so that
        /// <c>RestoreTerrain</c> leaves the lot's direction alone. See the type remarks.
        /// </summary>
        /// <remarks>
        /// A large-ish number rather than a specific one, because nothing on this path counts user
        /// objects - the lot is a backdrop holding a mailbox, a bin and maybe a car portal - and
        /// the number exists only to be non-zero. It is a named constant rather than an inline
        /// literal so the next reader does not assume it means something.
        /// </remarks>
        public const int BlankObjectLimit = 1000;

        /// <summary>
        /// The level a sub-world's own state sits at, the one <c>RestoreSurroundings</c> uses.
        /// Five floors is a TS1 lot's eight storeys; the lot view passes it as
        /// <c>SilentLevel</c> while drawing a sub-world, so it decides which floors of the
        /// neighbour's furniture are visible.
        /// </summary>
        public const sbyte SubWorldLevel = 5;

        /// <summary>
        /// Whether a neighbour may be attached to the lot the player is standing in as a sub-world
        /// of that lot's blueprint. <b>False by default</b>, and the default is the point.
        ///
        /// <para>
        /// <b>Why attaching a neighbour to a live lot is gated rather than assumed safe.</b>
        /// Everything below this line writes to the lot the player is standing in, and it cannot
        /// all be made not to:
        /// <list type="bullet">
        ///   <item><description>
        ///     <c>Blueprint.SubWorlds.Add</c> puts an object on the <em>live</em> lot's blueprint -
        ///     the one <c>VMContext.IsUserOutOfBounds</c> and the lot view's own culling and
        ///     lighting read - and <c>World.InitSubWorlds</c> then rewrites the live lot's
        ///     <c>SkyBounds</c> from the whole set. That is the design of TSO's own
        ///     <c>RestoreSurroundings</c>, and it is additive only for as long as the set really is
        ///     a set of siblings on that one blueprint.
        ///   </description></item>
        ///   <item><description>
        ///     The list is not per-lot in the engine: <c>VMContext</c>'s marshal constructor hands
        ///     the previous blueprint's <c>SubWorlds</c> to the next one, so the list outlives the
        ///     lot that first created it and a resume or a blueprint reset re-attaches whatever it
        ///     still holds. A neighbour built against the wrong blueprint is then drawn, lit and
        ///     preloaded against lot views that have nothing to do with it.
        ///   </description></item>
        ///   <item><description>
        ///     The lot view draws each sub-world by moving the <em>parent's</em>
        ///     <c>WorldState</c> - <c>CenterTile</c>, <c>_2D</c>'s scroll, <c>SilentLevel</c>,
        ///     and the parent light batch through <c>PrepareLighting</c>/<c>ClearLighting</c>.
        ///     Every one of those is the live lot's own state, and the restores are not in a
        ///     position to know whether the frame they ran in had already left one of them moved.
        ///   </description></item>
        /// </list>
        /// With the gate off none of that is reached: <see cref="TryRestore"/> refuses with a
        /// sentence before it reads a field of the live lot, the caller withholds no facades
        /// because nothing was built, and the surrounding lots are drawn the Part A way - which is
        /// the behaviour that was already known not to break the lot.
        /// </para>
        /// <para>
        /// <b>It is a plain field rather than an ini key on purpose.</b> The seven
        /// <see cref="NeighbourhoodWorldFlag"/>s are a product's feature switches and are meant to
        /// be flipped by a player; this one is an engineering safety switch for a write into a live
        /// lot that has not been shown to be contained, so it is deliberately not something a
        /// hand-edited <c>simitone.ini</c> turns on by accident.
        /// </para>
        /// </summary>
        public static bool SubWorldAttachEnabled { get; set; }

        /// <summary>
        /// The sentence <see cref="TryRestore"/> refuses with while
        /// <see cref="SubWorldAttachEnabled"/> is off. A named constant so a caller can recognise
        /// this particular refusal and stay quiet about it rather than logging one line per lot.
        /// </summary>
        public const string SubWorldAttachDisabledReason =
            "neighbour sub-worlds are not attached to a live lot, so the surrounding lots are drawn"
            + " as facades - this is the whole of the street the lot view draws, and the lot itself"
            + " is untouched";

        /// <summary>
        /// Why a sub-world's architecture must not be written through the marshal it was built from.
        /// Named so the log and this file agree on the one word for it.
        /// </summary>
        public const string SharedArchitectureReason =
            "a hollow lot's architecture arrays are shared by reference with the marshal it was built"
            + " from, so this sub-world took private copies of them before writing to its own terrain";

        /// <summary>
        /// Stands up every neighbour in <paramref name="plan"/> as a sub-world of
        /// <paramref name="vm"/>, and asks the lot view to measure them into its sky bounds.
        ///
        /// <para>
        /// Sub-worlds already on the blueprint are left alone; <see cref="DisposeSubWorlds"/> is
        /// the caller's to make, because a lot switch wants the old ones gone and a re-plan of the
        /// same lot does not. Either order runs - adding to a list that still holds the previous
        /// plan's sub-worlds draws both, which is exactly why the switch path has to dispose
        /// first.
        /// </para>
        /// </summary>
        /// <param name="vm">The lot the player is standing in. Its context's world is where the
        /// sub-worlds are created and its blueprint's list is where they are added.</param>
        /// <param name="plan">The plan, which decides everything. An invalid plan is a refusal.</param>
        /// <param name="reason">Why nothing was built, when nothing was.</param>
        /// <param name="loader">
        /// Where to read a neighbour's contents from when the store held none. <c>null</c> - the
        /// default - means this call owns a loader for its own length, bounded by
        /// <see cref="NeighbourHollowLoader.DefaultMaxBuilds"/>; pass one with a budget of zero to
        /// get the older behaviour of building every such neighbour blank. See
        /// <see cref="ResolveHollows"/>.
        /// </param>
        /// <returns>Whether the bridge ran. A plan with no neighbours still returns
        /// <c>true</c>: there was nothing to refuse.</returns>
        public static bool TryRestore(VM vm, SimitoneSurroundingsPlan plan, out string reason,
            NeighbourHollowLoader loader = null)
        {
            if (vm == null)
            {
                reason = "there is no lot to put the neighbours into";
                return false;
            }
            if (vm.Context == null)
            {
                reason = "the lot has no context";
                return false;
            }
            if (vm.Context.Blueprint == null)
            {
                reason = "the lot has no blueprint, so there is nowhere to put the neighbours";
                return false;
            }
            if (plan == null)
            {
                reason = "there is no surroundings plan";
                return false;
            }
            if (!plan.IsValid)
            {
                reason = "the surroundings plan is not usable: " + plan.Failure;
                return false;
            }

            // The blueprint, architecture, terrain component and world-state work below all sit
            // behind this flag, and false here means a host with no lot view at all - a dedicated
            // server, a test - where none of them have anywhere to draw. A refusal with a sentence
            // beats a null dereference three frames down.
            if (!VM.UseWorld)
            {
                reason = "this host has no lot view, so sub-worlds have nothing to be drawn in";
                return false;
            }

            // The live-lot gate, and it is asked before a single field of the lot the player is
            // standing in is read, let alone written. See SubWorldAttachEnabled for why attaching
            // a neighbour to a live lot is not on by default; off, the caller keeps its facades,
            // which is the whole of Part A and the whole of a lot that behaves.
            if (!SubWorldAttachEnabled)
            {
                reason = SubWorldAttachDisabledReason;
                return false;
            }

            var world = vm.Context.World;
            if (world == null || world.State == null || world.State.Device == null)
            {
                reason = "the lot view has no graphics device, so sub-worlds cannot be created";
                return false;
            }
            //The sub-worlds' own device, which is also the device a neighbour's contents are read
            //with: the throwaway world ResolveHollows stands up lives in the same lot view and is
            //drawn by the same device, and a second device for it would be a second lot view to
            //initialise for the sake of a few hundred milliseconds.
            var device = world.State.Device;

            // What this call has added, so that a failure anywhere below can take it all back down
            // rather than leaving a street half full of houses and the lot switch holding a light
            // map per one of them. Nothing outside this method disposes what it did not build.
            var added = new List<SubWorldComponent>();
            var blueprint = vm.Context.Blueprint;
            try
            {
                // A neighbour the store held no contents for is read out of its own house file
                // before anything is built, so the plan the loop below consumes is the resolved
                // one. Inside the try because it stands a World and a VM up per lot and a failure
                // outside BuildOne's own try has to unwind the same way; outside the loop because
                // every neighbour has to be resolved before the first one is built, or a lot would
                // be built blank because a lot earlier in the list was still being read.
                //
                // A caller that supplies no loader gets one for the length of this call, so that a
                // neighbour showing its real contents is what the bridge does rather than
                // something a screen has to know to ask for - and only when the plan actually has
                // a neighbour to read one for, so the ordinary case allocates nothing. This whole
                // method is behind the Surroundings capability, so with the flag off none of it is
                // reached.
                var source = loader;
                if (source == null && plan.NeedsHollows())
                {
                    source = new NeighbourHollowLoader(device);
                }
                var resolved = ResolveHollows(plan, source);

                for (int i = 0; i < resolved.Warnings.Count; i++)
                {
                    SimitoneLog.Warn("Lot " + resolved.ActiveLot.Number + "'s surroundings: "
                        + resolved.Warnings[i]);
                }

                // The plan as it was planned said how many neighbours were hollow out of the store,
                // which in a neighbourhood nobody has visited is none of them. This is the line
                // that says what they are made of now, and it is here rather than at the caller
                // because the caller holds the plan as it was planned and this is the half that
                // changed it.
                //
                // And the other half of what a lot the plan read out of: how many of these
                // neighbours are standing on their own graded ARRY(0) and how many are still on the
                // generated map's flat median, with the reason for each of those. The plan carries
                // both - the source on ArryPlanes, the answers on each neighbour - so the bridge,
                // which has no generated map and no content manager, can say what the street is
                // made of without owning either.
                if (resolved.Neighbours.Count > 0)
                {
                    SimitoneLog.Info("Lot " + resolved.ActiveLot.Number + "'s surroundings: "
                        + resolved.Describe()
                        + (source != null ? ", " + source.Describe() : string.Empty)
                        + ", " + DescribeHeights(resolved));
                }

                for (int i = 0; i < resolved.Neighbours.Count; i++)
                {
                    var neighbour = resolved.Neighbours[i];
                    if (!neighbour.IsValid) continue;

                    SubWorldComponent subworld = null;
                    try
                    {
                        subworld = BuildOne(world, resolved, neighbour);
                        if (subworld == null) continue;
                        blueprint.SubWorlds.Add(subworld);
                        added.Add(subworld);
                    }
                    catch (Exception ex)
                    {
                        // The acceptance criterion, in one place: this neighbour is lost, its
                        // half-built world is taken down so it does not leak a graphics device and
                        // a light map per switch, and the street has one gap in it that the log
                        // explains.
                        //
                        // BuildOne has already disposed it and rethrew - it is the only holder of
                        // the reference to a sub-world that failed before it came back - so this is
                        // the belt to that braces, and a no-op in the ordinary case. It matters for
                        // a failure *after* the return: an Add that throws leaves a live sub-world
                        // that nothing else will ever reach.
                        TryDispose(subworld);
                        SimitoneLog.Warn("Lot " + resolved.ActiveLot.Number + "'s surroundings: neighbour "
                            + neighbour.Lot.Number + " could not be built and was skipped: "
                            + ex.Message, ex);
                    }
                }

                // Exactly once, after the loop, never per neighbour. The lot view's sky bounds are
                // measured from the whole set of sub-worlds, so they are stale the moment a
                // neighbour is added, and each call walks every sub-world's own altitude - so a
                // per-neighbour call would be quadratic for no benefit. This is the call TSO's own
                // path ends on. It is inside the try, so if it throws the sub-worlds this call
                // added are taken back down below rather than left on the blueprint with bounds
                // that were never measured.
                world.InitSubWorlds();
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                // Unwind everything this call put on the blueprint. The list is shared across lot
                // switches (VMContext hands the old blueprint's list to the new one), so a failure
                // that left entries behind would leak a device resource per failure and would draw
                // a sub-world that no plan accounts for.
                for (int i = 0; i < added.Count; i++)
                {
                    TryDispose(added[i]);
                    if (blueprint.SubWorlds.Remove(added[i]))
                    {
                        SimitoneLog.Warn("Lot " + plan.ActiveLot.Number + "'s surroundings: a "
                            + "neighbour sub-world built before the failure was taken back down");
                    }
                }
                reason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// What the sub-worlds of this plan are standing on: how many are on their own lot's real
        /// graded <c>ARRY(0)</c> and how many are on the generated map's flat pin over the lot's
        /// rectangle, followed by the sentences naming any lot that fell back and why.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the line that makes the flat ones a fact rather than a look.</b> The
        /// <see cref="SimitoneSurroundingsNeighbour.SubWorldAltitudes"/> the bridge copies is the
        /// neighbour's drawn ground either way, so "the surrounding lots are flat" is a shape and
        /// "the surrounding lots are flat because nothing could read their house files" is a bug -
        /// and the second is only distinguishable from the first in the log.
        /// </para>
        /// <para>
        /// Nothing here decides anything. The grading happened once, in
        /// <see cref="SimitoneSurroundingsPlan"/>'s per-lot step, where the generated map and the
        /// height source are both in scope; this reads the answers the plan carries and asks the
        /// source for its own sentence about the ones it could not give.
        /// </para>
        /// </remarks>
        /// <param name="plan">The plan the sub-worlds are about to be built from.</param>
        public static string DescribeHeights(SimitoneSurroundingsPlan plan)
        {
            if (plan == null || plan.Neighbours.Count == 0) return "no sub-world ground to report";
            var graded = plan.GradedGroundCount;
            var text = graded + " of " + plan.Neighbours.Count + " sub-world"
                + (plan.Neighbours.Count == 1 ? "" : "s") + " on their own lot's graded ARRY(0), "
                + (plan.Neighbours.Count - graded)
                + " on the generated map's pinned median";
            var planes = plan.ArryPlanes;
            if (planes == null || planes.PinReasons.Count == 0) return text;
            for (int i = 0; i < planes.PinReasons.Count; i++)
            {
                text += "\n  " + planes.PinReasons[i];
            }
            return text;
        }

        /// <summary>
        /// Takes down every sub-world the lot view is currently drawing, and empties the list so
        /// the next plan starts from nothing.
        ///
        /// <para>
        /// What a lot switch and <c>CleanupLastWorld</c>/<c>DisposeOnLot</c> call (B2 task 6). A
        /// <c>SubWorldComponent</c> owns a light map, an ambient light texture and a world
        /// architecture, all of them graphics resources, so leaving a handful alive across a
        /// hundred lot switches is a device exhaustion with nothing to attribute it to. Each
        /// disposal is its own <c>try</c> for the reason the build is: one sub-world that will not
        /// dispose must not keep the other twenty from being cleaned up.
        /// </para>
        /// </summary>
        /// <param name="vm">The lot, or <c>null</c>.</param>
        public static void DisposeSubWorlds(VM vm)
        {
            var blueprint = vm?.Context?.Blueprint;
            if (blueprint?.SubWorlds == null) return;
            for (int i = 0; i < blueprint.SubWorlds.Count; i++)
            {
                TryDispose(blueprint.SubWorlds[i]);
            }
            blueprint.SubWorlds.Clear();
        }

        private static void TryDispose(SubWorldComponent subworld)
        {
            if (subworld == null) return;
            try
            {
                subworld.Dispose();
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("A neighbourhood sub-world could not be disposed: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// The plan with every neighbour whose contents could be read given them, and a warning
        /// for every one that could not.
        /// </summary>
        /// <remarks>
        /// <para><b>This is what turns "0 hollow, 4 blank" into real houses.</b> A neighbour's
        /// contents are hollow at plan time only if the <see cref="LotStore"/> holds a marshal for
        /// it, and §5.3's store only holds lots somebody has entered - so in a generated
        /// neighbourhood every neighbour arrives blank and the player looks at empty plots where
        /// the houses should be. The contents are not missing: they are in each lot's own
        /// <c>HouseNN.iff</c>. <see cref="NeighbourHollowLoader"/> stands a throwaway world up per
        /// lot, runs that file through the retail loader, takes a <c>HollowSave</c> of the result
        /// and throws the world away - which is exactly the sequence
        /// <see cref="NeighbourhoodFacadeBuilder"/> already runs to draw the same house as a
        /// facade.</para>
        ///
        /// <para><b>Where it happens, and why it is here.</b> It cannot be in the plan, which is
        /// pure and testable with no engine at all: reading a lot's contents costs a
        /// <see cref="World"/>, a <see cref="VM"/> and a whole house-file load, and the decision of
        /// <em>which lots are real neighbours</em> must not cost that. It cannot be in the caller
        /// either, which is this feature's screen and holds no device. So it is here, in the one
        /// file in this feature that already stands a world up per neighbour, and it hands its
        /// answers back as a plan - <see cref="SimitoneSurroundingsPlan.WithHollows"/> - so the
        /// loop below, <see cref="BuildHollow"/> and <c>VM.HollowLoad</c> cannot tell the
        /// difference between a marshal out of the store and a marshal out of a house file, which is
        /// the only place that difference should ever stop mattering.</para>
        ///
        /// <para><b>A lot that cannot be read is a blank lot and a sentence.</b> Never fatal, never
        /// an exception: the whole of this runs inside <see cref="TryRestore"/>'s try, and a lot
        /// that has no house file, no fingerprint, no budget or a corrupt file is built blank with
        /// a line naming it - the same lot, at the same height, facing the same way, with nothing
        /// on it, and an explanation in the log.</para>
        ///
        /// <para><b>What the store is for afterwards.</b> A marshal built here is published back
        /// into the store, because the next lot load over the same lots is answered out of it for
        /// free - and <see cref="NeighbourHollowLoader.ShouldPublish"/> refuses to write over a lot
        /// the store is holding an active marshal for, which is the resident lot's own state.</para>
        /// </remarks>
        /// <param name="plan">The plan as it was made.</param>
        /// <param name="loader">
        /// Where to read contents from. Null is a backstop rather than a mode: the caller has
        /// already decided, and a null here is the one thing that would leave a neighbour blank
        /// without a reason.
        /// </param>
        /// <returns>
        /// The same plan with the contents filled in, or the plan itself when there was nothing to
        /// fill in - which is the whole of the flag-off and store-holding-everything case.
        /// </returns>
        private static SimitoneSurroundingsPlan ResolveHollows(
            SimitoneSurroundingsPlan plan, NeighbourHollowLoader loader)
        {
            //No loader is a backstop, and no neighbour is hollow is the whole of the case where there is
            //nothing to read: either way the plan that comes back is the one that went in, and
            //nothing below this line runs.
            if (loader == null || !plan.NeedsHollows()) return plan;

            loader.BeginPass();
            var marshals = new Dictionary<short, VMHollowMarshal>();
            var warnings = new List<string>();
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                var neighbour = plan.Neighbours[i];
                if (!neighbour.IsValid || neighbour.IsHollow) continue;

                VMHollowMarshal marshal;
                string why;
                if (!loader.TryLoad(neighbour.Lot, out marshal, out why))
                {
                    warnings.Add("neighbour " + neighbour.Lot.Number + " was built blank: " + why);
                    continue;
                }
                marshals[neighbour.Lot.Number] = marshal;
                //into the store as well as into the plan, so that the next lot load over this
                //street answers out of memory rather than out of four more house files. The store
                //refuses to be written over a lot it is holding a resident marshal for.
                loader.Publish(plan.Store, neighbour.Lot.Number, marshal);
            }

            if (marshals.Count == 0 && warnings.Count == 0) return plan;
            return plan.WithHollows(marshals, warnings);
        }

        /// <summary>
        /// One neighbour, start to finish: a sub-world, a <c>VM</c> on it, its contents or a blank
        /// lot, its terrain out of the generated map, and its place in the parent world. Returns
        /// null for a neighbour with nothing to build, which is a refusal rather than a failure -
        /// the caller skips it silently, because there is nothing wrong.
        /// </summary>
        /// <remarks>
        /// Takes its own <c>try</c> for one reason: a failure after <c>MakeSubWorld</c> leaves a
        /// sub-world holding a light map, an ambient light texture and a world architecture, and
        /// the caller cannot dispose it because the reference never came back. So the half-built
        /// world is taken down here and the exception is rethrown for the caller to log.
        /// </remarks>
        private static SubWorldComponent BuildOne(
            World world, SimitoneSurroundingsPlan plan, SimitoneSurroundingsNeighbour neighbour)
        {
            var size = neighbour.SubWorldSize;
            if (size <= 0) return null;

            var device = world.State.Device;

            // The sub-world is a World of its own: its own state, its own light batch, its own
            // architecture. MakeSubWorld rather than `new SubWorldComponent` because it is virtual
            // and the lot view is entitled to hand back its own kind of sub-world.
            SubWorldComponent subworld = null;
            try
            {
                subworld = world.MakeSubWorld(device);
                subworld.Initialize(device);

                var tempVM = new VM(
                    new VMContext(subworld),
                    new VMServerDriver(new VMTSOGlobalLinkStub()),
                    new VMNullHeadlineProvider());
                // Before Init, which is where the lot state is chosen. See the type remarks.
                tempVM.TS1 = false;
                tempVM.Init();

                // A one-off 4x4 surrounding terrain, filled from the generated map. The helpers read
                // BlendN[1,1] and Roads[1,1] out of it and the whole 4x4 of Height, and nothing
                // else ever sees it: it exists because GetBaseLevel and RestoreHeight take a
                // VMTSOSurroundingTerrain rather than a height array.
                var terrain = TerrainContainerFor(neighbour.Terrain);
                tempVM.TSOState.Terrain = terrain;

                if (neighbour.IsHollow)
                {
                    BuildHollow(tempVM, subworld, plan, neighbour, terrain);
                }
                else
                {
                    BuildBlank(tempVM, subworld, plan, neighbour, terrain, size);
                }

                subworld.State.Level = SubWorldLevel;
                // The layout's tile delta, not TSO's (size - 2) grid spacing - the whole point of
                // this file - and unsigned, because that is what SubDraw reads: it does
                // `parentState.CenterTile += GlobalPosition` in 2D and sets
                // `Cameras.ModelTranslation` to `GlobalPosition * 3` in 3D, so the sub-lot's own
                // tile (0, 0) lands at the parent's tile GlobalPosition. PreDraw agrees
                // (`SubworldOff = GlobalPosition * 3`).
                //
                // `UpdateBounds` builds its bounding box at `-GlobalPosition * 3` instead. That is
                // not a placement: the result is merged into `World.SkyBounds` and read by the 3D
                // sky box only, while the parent's own box in `InitSubWorlds` is
                // `[0, Width*3] x [0, Height*3]` - the parent being at GlobalPosition 0 under the
                // same positive convention. It is an inconsistency in FSO's bounds code, not a
                // mirrored axis, and negating here would put every neighbour on the opposite plot.
                // See SimitoneSurroundingsNeighbour.SubWorldPosition for the whole argument.
                subworld.GlobalPosition = neighbour.SubWorldPosition;
                return subworld;
            }
            catch
            {
                TryDispose(subworld);
                throw;
            }
        }

        /// <summary>
        /// A neighbour that has stored contents: load them, then make its terrain match the
        /// neighbourhood. TSO's own order, and each step depends on the one before it - the
        /// architecture has to exist before terrain can be drawn into it, the terrain before the
        /// base altitude that hangs the lot in the parent world, and the base altitude before
        /// anything is re-seated onto the finished height field.
        /// </summary>
        private static void BuildHollow(
            VM tempVM, SubWorldComponent subworld, SimitoneSurroundingsPlan plan,
            SimitoneSurroundingsNeighbour neighbour, VMTSOSurroundingTerrain terrain)
        {
            var hollow = neighbour.Hollow;

            // HollowLoad rebuilds the VMContext from the marshal and, through VMContext.Load,
            // builds the sub-world's blueprint, its terrain component and its architecture, and
            // calls World.InitBlueprint on it. The lot state is ours and survives that.
            tempVM.HollowLoad(hollow);

            // And then the arrays above are private, before a single one of them is written.
            // VMArchitecture.Load hands the sub-world the marshal's own Terrain, Walls, Floors and
            // FineBuildableArea *by reference*, and everything below - RestoreTerrain, RestoreHeight,
            // EnsureCoreObjects and ApplySurroundGround - writes into all four in place. The marshal
            // in question is the LotStore's cached copy of that lot's contents, published by
            // NeighbourHollowLoader and held for the rest of the session, so without this the store
            // is rewritten under every later build of the same lot and under every later lot load
            // over the same street. See DetachFromSharedArrays.
            DetachFromSharedArrays(tempVM.Context?.Architecture);

            tempVM.TSOState.LotID = (uint)neighbour.Lot.Number;
            tempVM.TSOState.Size = SizeFor(neighbour);
            EnsureObjectLimit(tempVM);

            // A hollow lot is a real lot: its landmark objects exist, and the
            // PositionLandmarkObjects inside RestoreTerrain has to put them back on the edge this
            // lot's road is actually on - which is what the layout's DoorSide is for.
            //
            // The road byte is handed over verbatim and is not re-derived here. It has been checked
            // against RestoreRoad, which is the only reader: the four low bits are a street along
            // that edge of the lot (8 west, 4 north, 2 east, 1 south - the vertical strip it lays
            // at x=1 is 8 and the horizontal band at y=1 is 4), and the high nibble is the corner
            // tilemaps, read as `corners = roads >> 4` where bit 1 is the bottom-left corner, 2
            // bottom-right, 4 top-right and 8 top. That is exactly
            // SimitoneNeighbourTerrain's four edge and four corner constants, so the pass-through
            // is the whole contract. The probe depth that fills the byte is
            // SimitoneNeighbourTerrain.RoadProbeDepth and is a policy decision made there.
            VMLotTerrainRestoreTools.RestoreTerrain(tempVM, neighbour.Terrain.Blend,
                neighbour.Terrain.Roads, RestoreLotType.Normal);

            // From marshal version 19 a hollow lot's own altitude field is authoritative and the
            // surrounding terrain only says where to hang it, so the base level is read rather
            // than re-derived. Before that the height field has to be rebuilt from the terrain,
            // and the value it returns is the one to use. TSO's own branch, kept because a hollow
            // lot written by an older build is still a file a player can have.
            int height;
            if (hollow.Version < 19)
            {
                height = VMLotTerrainRestoreTools.RestoreHeight(tempVM, terrain, 1, 1);
            }
            else
            {
                height = VMLotTerrainRestoreTools.GetBaseLevel(tempVM, terrain, 1, 1);
            }

            tempVM.Context.Blueprint.BaseAlt = BaseAltitudeFor(plan.ActiveBaseLevel, height);

            // The height field the lot is built ON, not the one it was built with. RestoreHeight
            // leaves a second surface over the same rectangle - a smoothstep bicubic of sixteen
            // samples, with noise in the interpolation fraction - and the surround is drawing the
            // generated map's own per-tile ground there instead. One of them has to go, and the
            // surround's does not move, so this overwrites the helpers' field with the surround's
            // corner-for-corner. See ApplySurroundGround.
            ApplySurroundGround(tempVM, neighbour);

            foreach (var obj in tempVM.Entities)
            {
                // The assignment is the whole point: reading an entity's Position re-seats it on
                // the terrain it now stands on, so a hollow lot's furniture does not float above
                // the height field the neighbourhood just gave it. It has to follow the merge, not
                // the restore, or the furniture stands on the surface that was just discarded.
                obj.Position = obj.Position;
            }

            // After the base altitude and the height field, not before: RestoreHeight flattens the
            // buildable area to the mailbox, and a mailbox EnsureCoreObjects would have created
            // first would then be flattened to a ground the lot did not have. The landmark objects
            // are put on this lot's road edge last, on the terrain they are actually going to stand
            // on.
            VMLotTerrainRestoreTools.EnsureCoreObjects(tempVM, RestoreLotType.Normal);

            tempVM.Tick();
            subworld.CalculateFloorsUsed();
        }

        /// <summary>
        /// A neighbour with no stored contents: an empty blueprint of the lot's own size, and the
        /// same terrain work. TSO's blank branch, in the same order.
        /// </summary>
        private static void BuildBlank(
            VM tempVM, SubWorldComponent subworld, SimitoneSurroundingsPlan plan,
            SimitoneSurroundingsNeighbour neighbour, VMTSOSurroundingTerrain terrain, int size)
        {
            var blueprint = new Blueprint(size, size);
            tempVM.Context.Blueprint = blueprint;
            subworld.InitBlueprint(blueprint);

            // LotID is the lot number rather than a packed grid coordinate. The helpers use it as
            // a seed and nothing else, and the layout's lot number is the one identity a hollow
            // lot, a blank lot and a log line can all be given - which is what makes a blank
            // neighbour reproducible: same layout, same ground, same lot.
            tempVM.TSOState.LotID = (uint)neighbour.Lot.Number;
            tempVM.TSOState.Size = SizeFor(neighbour);

            tempVM.Context.Architecture = new VMArchitecture(size, size, blueprint, tempVM.Context);
            tempVM.Context.Architecture.EmptyRoomMap();
            tempVM.Context.RegeneratePortalInfo();

            // The sub-world's terrain component, attached to its blueprint. SubWorldComponent
            // never makes one itself, and a blueprint without it draws no ground at all.
            var terrainComponent = new TerrainComponent(new Rectangle(0, 0, size, size), blueprint);
            terrainComponent.Initialize(subworld.State.Device, subworld.State);
            blueprint.Terrain = terrainComponent;

            EnsureObjectLimit(tempVM);

            VMLotTerrainRestoreTools.RestoreTerrain(tempVM, neighbour.Terrain.Blend,
                neighbour.Terrain.Roads, RestoreLotType.Blank);

            var height = VMLotTerrainRestoreTools.RestoreHeight(tempVM, terrain, 1, 1);
            tempVM.Context.Blueprint.BaseAlt = BaseAltitudeFor(plan.ActiveBaseLevel, height);

            // Before anything is placed on it, and for the same reason as in the hollow branch: the
            // landmark row has to be put on the ground the lot is actually going to be drawn on.
            ApplySurroundGround(tempVM, neighbour);

            // The blank branch's landmark pass. RestoreLotType.Blank is what makes it safe on a lot
            // with no content: PositionLandmarkObjects only *creates* an object when the type is
            // not Blank, so this can hide and place what exists without needing a GUID the install
            // may not have. It is called after the height field for the same reason as in the
            // hollow branch.
            VMLotTerrainRestoreTools.EnsureCoreObjects(tempVM, RestoreLotType.Blank);

            // The rest of a blank lot's first tick: the room map and the portal list were built
            // against an architecture that terrain has since rewritten, and lighting was never
            // built at all.
            tempVM.Context.Architecture.ClearDirty();
            tempVM.Context.Architecture.RegenRoomMap();
            tempVM.Context.RegeneratePortalInfo();
            tempVM.Context.RefreshAllLighting();
            tempVM.Tick();
        }

        /// <summary>
        /// Takes private copies of every architecture array this VM shares with the marshal it was
        /// loaded from, so nothing written to this sub-world's lot can reach that marshal.
        ///
        /// <remarks>
        /// <para>
        /// <b>The aliasing this exists to break, as three lines of engine.</b>
        /// <c>VMArchitecture.Save</c> hands <c>Terrain</c>, <c>Walls</c>, <c>Floors</c> and
        /// <c>FineBuildableArea</c> to the marshal <b>by reference</b> - it says so itself, and
        /// calls the capture "not a freeze" - and <c>VMArchitecture.Load</c> takes all four back
        /// <b>by reference</b> again. So a VM built from a marshal owns the same arrays the marshal
        /// does, and every in-place write one of them makes is a write to the other.
        /// </para>
        /// <para>
        /// <b>Why that matters here and not on the lot.</b> On a lot that is being loaded, nothing
        /// writes to those arrays afterwards. In this file there are four writers in a row -
        /// <c>VMLotTerrainRestoreTools.RestoreTerrain</c> (walls, floors, the fine buildable plane),
        /// <c>RestoreHeight</c> (terrain heights, then the buildable area flattened to the mailbox),
        /// <c>EnsureCoreObjects</c>, and <see cref="ApplySurroundGround"/> (terrain heights again) -
        /// and the marshal on the other end is a <see cref="LotStore"/> entry, published by
        /// <see cref="NeighbourHollowLoader.Publish"/> and read again by
        /// <see cref="SimitoneSurroundingsPlan"/>'s <c>Consider</c> for every later lot load over
        /// the same street. So without this the store's copy is rewritten in place by the first
        /// neighbour build and every build after it starts from terrain that has already been
        /// restored, re-merged and re-flattened, against a layout that never changed.
        /// </para>
        /// <para>
        /// <b>Cheap, and only paid for a hollow lot.</b> Four array copies over one lot's tiles,
        /// once per neighbour that has stored contents - against a whole house-file load and a
        /// <c>HollowSave</c> standing next to it. <see cref="BuildBlank"/> does not call this: a
        /// blank lot's architecture is built by <c>new VMArchitecture(size, size, ...)</c> and has
        /// nothing shared to detach from.
        /// </para>
        /// <para>
        /// <b>The derived data is rebuilt rather than copied.</b> <c>WallsAt</c> is an index over
        /// <c>Walls</c> and <c>Terrain.Centers</c> over <c>Heights</c>, so both are regenerated
        /// after the copies rather than copied with them - and the architecture is told it is all
        /// dirty, which is what a load does and what a set of new arrays deserves.
        /// </para>
        /// </remarks>
        /// <param name="architecture">The sub-world's architecture, or <c>null</c>.</param>
        public static void DetachFromSharedArrays(VMArchitecture architecture)
        {
            if (architecture == null) return;
            architecture.Walls = CloneLevels(architecture.Walls);
            architecture.Floors = CloneLevels(architecture.Floors);
            architecture.Terrain = CloneTerrain(architecture.Terrain);
            architecture.FineBuildableArea = architecture.FineBuildableArea == null
                ? null
                : (bool[])architecture.FineBuildableArea.Clone();
            architecture.RegenWallsAt();
            if (architecture.Terrain != null) architecture.Terrain.RegenerateCenters();
            //Both flags, and the terrain one is not optional. VMArchitecture.Tick's terrain branch
            //is what re-points WorldUI.Altitude, WorldUI.AltitudeCenters and the blueprint's own
            //TerrainComponent (whose GroundHeight has no setter) at this architecture's terrain, and
            //SignalAllDirty alone leaves TerrainDirty false - so without SignalTerrainRedraw the
            //sub-world would go on drawing the marshal's array while this file wrote the copy's, and
            //the ground merge would silently stop merging. RestoreHeight and ApplySurroundGround
            //both re-point the blueprint themselves; this is what re-points the terrain component.
            architecture.SignalTerrainRedraw();
            architecture.SignalAllDirty();
        }

        /// <summary>
        /// A storey-by-storey copy of a jagged architecture plane, so the copy shares no array with
        /// the original at any level.
        /// </summary>
        /// <remarks>
        /// A jagged array is where a "copy" goes wrong quietly: <c>levels.Clone()</c> on the outer
        /// array copies the eight references and nothing else, so every storey is still shared and
        /// the first write is still a write to the marshal. Public and generic so the rule is one
        /// function for walls and for floors rather than two near-identical loops.
        /// </remarks>
        /// <typeparam name="T">The tile type - <c>WallTile</c> or <c>FloorTile</c>, both
        /// structs.</typeparam>
        /// <param name="levels">The planes, or <c>null</c>.</param>
        /// <returns>A deep copy, or <c>null</c> for <c>null</c>.</returns>
        public static T[][] CloneLevels<T>(T[][] levels)
        {
            if (levels == null) return null;
            var copy = new T[levels.Length][];
            for (int i = 0; i < levels.Length; i++)
            {
                copy[i] = levels[i] == null ? null : (T[])levels[i].Clone();
            }
            return copy;
        }

        /// <summary>
        /// A copy of one lot's terrain that shares no array with the original, keeping the lot's
        /// terrain types and format version.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every array, because every one of them is written.</b> <c>Heights</c> by
        /// <c>RestoreHeight</c> and then by <see cref="ApplySurroundGround"/>;
        /// <c>GrassState</c> by <c>RestoreTerrain</c>; <c>Sloped</c> and <c>VisHeights</c> by
        /// <c>SignalTerrainRedraw</c>'s dirty pass and by the lot view's own draw;
        /// <c>Centers</c> derived from <c>Heights</c>. <c>OldHeights</c>/<c>OldGrass</c> are the
        /// vis-mode scratch pair and are private, so they are not copied - a terrain outside vis
        /// mode has never set them, and one inside it is regenerated by <c>ExitVis</c>.
        /// </para>
        /// <para>
        /// Public and pure over its own argument, with no <c>VM</c>, no content manager and no
        /// device, because that is the only way the rule can be pinned by a headless test: build a
        /// terrain, copy it, write the copy, and the original must not have moved.
        /// </para>
        /// </remarks>
        /// <param name="source">The terrain to copy, or <c>null</c>.</param>
        /// <returns>A copy, or <c>null</c> for <c>null</c>.</returns>
        public static VMArchitectureTerrain CloneTerrain(VMArchitectureTerrain source)
        {
            if (source == null) return null;
            var copy = new VMArchitectureTerrain(source.Width, source.Height)
            {
                LightType = source.LightType,
                DarkType = source.DarkType,
                Version = source.Version
            };
            Copy(source.Heights, copy.Heights);
            Copy(source.GrassState, copy.GrassState);
            Copy(source.Sloped, copy.Sloped);
            Copy(source.VisHeights, copy.VisHeights);
            Copy(source.VisGrass, copy.VisGrass);
            //Centers on purpose: it is derived from Heights, and RegenerateCenters re-derives it
            //from the copy's own heights rather than from the ones the source had.
            return copy;
        }

        private static void Copy<T>(T[] source, T[] target)
        {
            if (source == null || target == null || source.Length != target.Length) return;
            Array.Copy(source, target, source.Length);
        }

        /// <summary>
        /// Whether a lot's own contents came from a marshal that is shared with somebody else, and
        /// therefore whether its arrays have to be detached before anything writes to them.
        /// </summary>
        /// <remarks>
        /// <b>True for every hollow lot and false for a blank one.</b> A blank neighbour's
        /// architecture is built from nothing, so there is nothing to share; a hollow one's is the
        /// marshal's own arrays, and that marshal is a <see cref="LotStore"/> entry that outlives
        /// the build. Exposed so the rule is one comparison a test can make and a caller can log.
        /// </remarks>
        /// <param name="hollow">The lot's stored contents, or <c>null</c>.</param>
        public static bool SharesArchitectureWithMarshal(VMHollowMarshal hollow)
        {
            return hollow?.Context?.Architecture != null;
        }

        /// <summary>
        /// Overwrites a sub-world's height field with the surround's own ground, so the drawn region
        /// has exactly one surface over it. Silent, and a no-op for a neighbour with no field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The bug this closes.</b> A sub-world is a whole <c>World</c> with its own
        /// <c>TerrainComponent</c>, and it drew its own ground over the surround's: a smoothstep
        /// bicubic of sixteen <c>round(elevation / 22.5)</c> samples spread over the lot's rectangle,
        /// with up to a fifth of a tile of perlin noise perturbing the interpolation fraction. The
        /// surround draws the generated map's per-tile elevation raster in the same rectangle. Both
        /// agreed on the <em>plane</em> - that is what
        /// <see cref="BaseAltitudeFor"/> and
        /// <see cref="SimitoneSurroundingsPlacement.GroundOriginFor"/> are for - and on the
        /// <em>rectangle</em>, and on nothing else, so the two surfaces crossed each other: measured
        /// over a 64x64 lot on a gentle five-unit slope, a mean step of two world units and a worst
        /// step of five - a tile and two thirds of vertical displacement - and still a third of a
        /// unit on a perfectly flat map. That is the step a player sees at the edge of every plot
        /// with the surround's own tile grid showing through around it.
        /// </para>
        /// <para>
        /// <b>Why overwrite rather than suppress the sub-world's draw.</b> Because this way there is
        /// one surface rather than one surface and a gap: the sub-world's own grass, its
        /// <c>RestoreRoad</c> pavement, its walls and its furniture are all drawn against this field
        /// and all still work, and because the two surfaces become the same triangles rather than
        /// nearly the same ones. Both are quads over the same 2x2 lattice of corner heights in the
        /// same index order, and the field is written in the lot view's own drawn-corner order, so
        /// corner for corner they are one surface.
        /// </para>
        /// <para>
        /// <b>What it has to happen after.</b> After <c>RestoreHeight</c> and after
        /// <c>BaseAlt</c>, because the field is written in altitude units that only mean anything
        /// once the base altitude is the one the surround is measured to; and before anything is
        /// placed or re-seated, because both read the height field they are standing on.
        /// </para>
        /// <para>
        /// <b>What is refreshed, and why by hand.</b> <c>Blueprint.Altitude</c> is the same array
        /// object as <c>Terrain.Heights</c> and <c>TerrainComponent</c>'s own <c>GroundHeight</c> is
        /// that same array again, so copying in place moves all three. The derived data is not
        /// shared, though: the tile centres the lot view reads object heights off are
        /// <c>Terrain.Centers</c>, which <c>RestoreHeight</c> computed from the field it has just
        /// written, so they are recomputed here; and the terrain component is told to regen its
        /// geometry, which it does from the array it already holds. Writing a fresh array instead
        /// would have meant reassigning all of <c>Altitude</c>, <c>AltitudeCenters</c> and
        /// <c>GroundHeight</c>, and <c>GroundHeight</c> has no setter - so in place is not only the
        /// smaller change, it is the only one available from here.
        /// </para>
        /// </remarks>
        /// <param name="tempVM">The neighbour's VM, mid-build.</param>
        /// <param name="neighbour">The neighbour being built.</param>
        private static void ApplySurroundGround(VM tempVM, SimitoneSurroundingsNeighbour neighbour)
        {
            var field = neighbour.SubWorldAltitudes;
            if (field == null || field.Length == 0) return;
            var arch = tempVM.Context?.Architecture;
            var target = arch?.Terrain;
            var blueprint = tempVM.Context?.Blueprint;
            if (target == null || target.Heights == null) return;
            //a field of the wrong shape is a plan and a lot that disagree about the lot's size, and
            //copying it would write past the end of the architecture rather than merge anything
            if (field.Length != target.Heights.Length) return;

            //in place, deliberately: Blueprint.Altitude is Terrain.Heights is TerrainComponent's own
            //GroundHeight, one array seen three ways, and GroundHeight has no setter to replace it
            //with. See the remarks.
            Array.Copy(field, target.Heights, field.Length);
            target.RegenerateCenters();
            if (blueprint != null)
            {
                blueprint.Altitude = target.Heights;
                blueprint.AltitudeCenters = target.Centers;
            }
            //and the terrain component is told to regen its geometry off the array it already holds,
            //which is the same call RestoreHeight's own SignalTerrainRedraw makes
            arch.SignalTerrainRedraw();
        }

        /// <summary>
        /// <c>vm.TSOState.Size</c> for a neighbour: the high half is this lot's direction, the low
        /// half is a <see cref="VMBuildableAreaInfo.BuildableSizes"/> <em>index</em>.
        ///
        /// <remarks>
        /// <para>
        /// The direction is the reason this is set at all. <c>PositionLandmarkObjects</c> and
        /// <c>GetTSOBuildableArea</c> both read <c>Size &gt;&gt; 16</c>, and
        /// <c>RestoreTerrain</c> would otherwise overwrite it with <c>PickRoadDir(roads)</c> - see
        /// <see cref="EnsureObjectLimit"/>. This used to put the lot's <em>tile</em> count in the
        /// low half, which is not what the helpers want and is a live landmine:
        /// <c>GetTSOBuildableArea</c> does <c>BuildableSizes[lotSInfo &amp; 255]</c> and that table
        /// has eleven entries, so a 16- or 64-tile lot indexed it at 16 or 64 and threw. It is only
        /// reached with <c>OwnerID == 0</c> overridden to 10 on the restore path today, so nothing
        /// has crashed - but <c>UpdateTSOBuildableArea</c> does not make that override, and "only
        /// reached by luck" is not a thing to leave standing.
        /// </para>
        /// <para>
        /// So the low half is the largest buildable area that fits the sub-world, chosen by walking
        /// the table: a 64-tile lot gets the table's own 64-tile entry, index 10, which is what a
        /// TS1 lot's own state carries, and the sixteen-tile floor gets index 0. <b>These are
        /// indices, so a smaller blueprint gets a smaller index too</b> - a sixteen-tile sub-world is
        /// built at sixteen tiles and given the twelve-tile buildable area, which is what fits;
        /// giving it the 64-tile entry would put a buildable rectangle four times the width of the
        /// lot it is in. The floors field (bits 8..15) is left at zero, as TSO's blank branch leaves
        /// it, and the buildable area is therefore the one-floor default that every backdrop
        /// neighbour wants.
        /// </para>
        /// </remarks>
        /// </summary>
        private static int SizeFor(SimitoneSurroundingsNeighbour neighbour)
        {
            return BuildableSizeIndexFor(neighbour.SubWorldSize) | (neighbour.LotDirection << 16);
        }

        /// <summary>
        /// The <see cref="VMBuildableAreaInfo.BuildableSizes"/> index for a sub-world of
        /// <paramref name="tiles"/> tiles: the largest buildable area that still fits inside it.
        /// Twelve for a sixteen-tile lot, sixty-four for a full TS1 one.
        /// </summary>
        /// <remarks>
        /// Public and separate from <see cref="SizeFor"/> so a test can assert the index without a
        /// VM - the thing that matters is that it is a valid index for that table, and that a
        /// 64-tile lot gets the 64-tile entry rather than something a player would notice as a
        /// neighbour with a stripe of unbuildable ground across it.
        /// </remarks>
        /// <param name="tiles">The sub-world's side, in tiles.</param>
        public static int BuildableSizeIndexFor(int tiles)
        {
            var sizes = VMBuildableAreaInfo.BuildableSizes;
            if (sizes == null || sizes.Length == 0) return 0;
            var size = Math.Max(0, tiles);
            var index = 0;
            for (int i = 1; i < sizes.Length; i++)
            {
                if (sizes[i] > size) break;
                index = i;
            }
            return index;
        }

        /// <summary>
        /// The <c>Blueprint.BaseAlt</c> that hangs a sub-world at the right height, from the two
        /// lots' base levels in the helpers' own units. Zero when the two lots are level, which is
        /// the whole of the common case.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a difference and why these units.</b> A sub-world's ground is drawn at
        /// <c>(altitude - BaseAlt) * TerrainFactor * 3</c> (<c>TerrainComponent</c>), and
        /// <c>RestoreHeight</c> leaves the lot's own footprint at <c>altitude ~= 0</c> because it
        /// writes <c>h * 100 - baseLevel</c>. So the sub-world's ground lands at
        /// <c>-BaseAlt * 0.05625</c> world units, and the surround's ground under the same lot is
        /// at <c>22.5 * (neighbourBase - activeBase) / 12 * 3</c>. Equating the two gives
        /// <c>BaseAlt = activeBase - neighbourBase</c> - and the two sides only cancel because both
        /// sides of it are hundredths of the helpers' <c>ARRY(0)</c> height byte, which the
        /// helpers multiply by 100 to become a blueprint altitude unit, and
        /// <c>BaseAlt</c> is in blueprint altitude units. That is why the conversion is nowhere in
        /// this file: there is none to do.
        /// </para>
        /// <para>
        /// The 22.5 is <see cref="SimitoneNeighbourTerrain.ElevationBytesPerHeightByte"/>, composed
        /// through <see cref="NeighbourhoodCityMeshBuilder.TilesPerArryUnit"/>,
        /// <see cref="NeighbourhoodCityMeshBuilder.ElevationScale"/> and
        /// <see cref="SimitoneNeighbourTerrain.LotAltitudeUnitsPerArryUnit"/>: a height byte is 100
        /// altitude units, an <c>ARRY(0)</c> unit is
        /// <see cref="SimitoneNeighbourTerrain.LotAltitudeUnitsPerArryUnit"/> of those, an
        /// <c>ARRY(0)</c> unit is <see cref="NeighbourhoodCityMeshBuilder.TilesPerArryUnit"/> of a
        /// tile and a tile is <see cref="NeighbourhoodCityMeshBuilder.ElevationScale"/> of them, so
        /// 100 / 10 * 3/16 * 12 = 22.5. <b>Those are the unified numbers and this file holds no
        /// copy of them</b>, which is the point of the unification.
        /// </para>
        /// <para>
        /// A plan that took the level from <c>NeighbourhoodLot.BaseHeight</c> instead would be in
        /// <c>ARRY(0)</c> units - a hundredth of what the helpers work in, since one helper height
        /// byte is a hundred altitude units and ten of them make an <c>ARRY(0)</c> unit - and every
        /// neighbour would hang a hundredth of the right height off, which across a 64-tile lot is
        /// a visible step at the property line.
        /// </para>
        /// <para>
        /// Named and public rather than inlined at the two call sites so the arithmetic is
        /// something a test can state: this is the number that decides whether a neighbour stands
        /// on the same plane as the lot the player is in.
        /// </para>
        /// </summary>
        /// <param name="activeBaseLevel">The active lot's base level.</param>
        /// <param name="neighbourBaseLevel">The neighbour lot's base level.</param>
        public static int BaseAltitudeFor(int activeBaseLevel, int neighbourBaseLevel)
        {
            return activeBaseLevel - neighbourBaseLevel;
        }

        /// <summary>
        /// Whether a sub-world's own ground is the surround's ground at every corner of its
        /// rectangle, to within the half an altitude unit of rounding that
        /// <see cref="SimitoneSurroundingsPlacement.SubWorldAltitudeFor"/> leaves behind.
        /// </summary>
        /// <remarks>
        /// <b>The alignment invariant, as one question.</b> It asks the field the way the lot view
        /// asks it - through <c>(altitude - BaseAlt) * LotTerrainFactor * LotViewUnitsPerTile</c>,
        /// which is the sum of <c>TerrainComponent.GetElevationPoint</c> and the <c>BaseAlt</c> its
        /// world matrix subtracts - and the surround's through
        /// <see cref="SimitoneSurroundingsPlacement.ToLotView"/>, which is the mesh's own
        /// translation. One side is the real conversion, not a copy of it, and the other is the real
        /// one too, so a change to either shows up here.
        ///
        /// <para><b>A graded neighbour deliberately fails this, and that is the point.</b> When the
        /// plan could read the lot's own <c>ARRY(0)</c> plane, its field is that lot's real
        /// per-tile ground measured from its own median
        /// (<see cref="SimitoneSurroundingsNeighbour.IsGradedGround"/>) while the surround's raster
        /// under it is still the generated map's flat pin over the same rectangle - so the two differ
        /// by exactly the lot's own grading, inside its property line and not outside it. Asking
        /// this question is therefore a question about the <em>pinned</em> path: it holds to the
        /// rounding tolerance for every neighbour the plan could not read a plane for, and a false
        /// answer for a graded one says the lot view and the plan disagree, which is the thing worth
        /// catching. The lot view clears the depth buffer after the surround and draws the sub-world
        /// over it, so the graded ground is the one a player sees inside the property line.</para>
        /// </remarks>
        /// <param name="placement">The active lot's placement.</param>
        /// <param name="neighbour">The neighbour whose field is asked about.</param>
        /// <param name="baseAltitude">The sub-world's <c>Blueprint.BaseAlt</c>.</param>
        /// <param name="map">The generated map, for the surround's own heights.</param>
        public static bool GroundIsTheSurroundsGround(
            SimitoneSurroundingsPlacement placement, NeighbourhoodMap map,
            SimitoneSurroundingsNeighbour neighbour, int baseAltitude)
        {
            if (!neighbour.HasMergedGround) return false;
            var size = neighbour.SubWorldSize;
            var lot = neighbour.Lot;
            if (map == null || lot == null) return false;
            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    var drawn = placement.ToLotView(new Vector3(lot.WorldX + x,
                        NeighbourhoodCityMeshBuilder.WorldHeightFor(
                            SimitoneSurroundingsPlacement.ElevationByteAt(map,
                                lot.WorldX + x, lot.WorldY + z)),
                        lot.WorldY + z)).Y;
                    var subWorld = (neighbour.AltitudeAt(x, z) - baseAltitude)
                        * SimitoneNeighbourTerrain.LotTerrainFactor
                        * SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
                    if (Math.Abs(subWorld - drawn) > SubWorldGroundTolerance) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether the street this neighbour's sub-world will draw is the street the surround's
        /// band draws, tile for tile, on every edge the lot has a road on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The whole of the seam, as one question.</b> A sub-world's own band is
        /// <c>VMLotTerrainRestoreTools.RestoreRoad</c> stamping four edge templates on a
        /// <c>Size x Size</c> architecture at fixed offsets from that architecture's own edges, and
        /// the surround's band is <see cref="SimitoneLotViewRoadBand"/> stamping the same grammar on
        /// the generated map's tile grid. They agree on the tiles only if three things are all true,
        /// and each of them is a place the two could drift apart:
        /// </para>
        /// <list type="number">
        ///   <item><description>
        ///     the same rectangle - the sub-world's own tile (0, 0) is at the parent's tile
        ///     <c>GlobalPosition</c>, which is the raw rectangle delta
        ///     (<see cref="SimitoneSurroundingsPlan.SubWorldPositionFor"/>);
        ///   </description></item>
        ///   <item><description>
        ///     the same road byte, so both ends pick the same edges - and there is only one road
        ///     byte, <see cref="SimitoneNeighbourTerrain.Roads"/>, handed verbatim to
        ///     <c>RestoreTerrain</c> here and read by
        ///     <see cref="SimitoneLotViewRoad.RoadByteFor"/> on the surround's side;
        ///   </description></item>
        ///   <item><description>
        ///     the same grammar, which is <see cref="SimitoneLotViewRoad"/>'s - the north edge's
        ///     four tarmac tiles and its pavement at six included, because <c>TopRightRoadTile</c>
        ///     is one tile deeper than the west's.
        ///   </description></item>
        /// </list>
        /// <para>
        /// A tile outside the band is not a disagreement: the band is one drawn region and a lot on
        /// its edge has half its street outside it, so only the tiles the band actually holds are
        /// asked about. A <c>null</c> or empty band is a refusal rather than a pass - it is a
        /// surround with no layout behind it, where the map's own road tiles are drawn instead and
        /// there is nothing here to agree with.
        /// </para>
        /// </remarks>
        /// <param name="neighbour">The neighbour about to be built.</param>
        /// <param name="band">The surround's own street over the drawn region.</param>
        public static bool HalfStreetAgreesWithTheBand(
            SimitoneSurroundingsNeighbour neighbour, SimitoneLotViewRoadBand band)
        {
            if (neighbour.Lot == null || band == null || band.IsEmpty) return false;
            var lot = neighbour.Lot;
            var size = Math.Max(0, lot.Size);
            if (size <= 0) return true;
            var roads = neighbour.Terrain.Roads;
            if (roads == 0) return true;

            //the four edges, each as the corner its offsets are measured from, the step outwards
            //across it, and whether those offsets run inwards (a far edge) or outwards (a near one)
            var minX = lot.WorldX;
            var minZ = lot.WorldY;
            var maxX = minX + size - 1;
            var maxZ = minZ + size - 1;
            if ((roads & SimitoneNeighbourTerrain.RoadWest) != 0
                && !EdgeAgrees(band, new Vector2(minX, minZ), new Vector2(1, 0), new Vector2(0, 1),
                    size, roads, SimitoneNeighbourTerrain.RoadWest)) return false;
            if ((roads & SimitoneNeighbourTerrain.RoadNorth) != 0
                && !EdgeAgrees(band, new Vector2(minX, minZ), new Vector2(0, 1), new Vector2(1, 0),
                    size, roads, SimitoneNeighbourTerrain.RoadNorth)) return false;
            if ((roads & SimitoneNeighbourTerrain.RoadEast) != 0
                && !EdgeAgrees(band, new Vector2(maxX, minZ), new Vector2(-1, 0), new Vector2(0, 1),
                    size, roads, SimitoneNeighbourTerrain.RoadEast)) return false;
            if ((roads & SimitoneNeighbourTerrain.RoadSouth) != 0
                && !EdgeAgrees(band, new Vector2(minX, maxZ), new Vector2(0, -1), new Vector2(1, 0),
                    size, roads, SimitoneNeighbourTerrain.RoadSouth)) return false;
            return true;
        }

        /// <summary>
        /// One edge of one lot's own half-street, asked of the band: the three or four tiles of
        /// tarmac, the tile of nothing and the tile of pavement <c>RestoreRoad</c> would stamp on
        /// it, at <see cref="SimitoneLotViewRoad"/>'s own offsets.
        /// </summary>
        /// <param name="band">The surround's street over the drawn region.</param>
        /// <param name="start">The lot's corner tile the edge's offsets are measured from.</param>
        /// <param name="run">The unit step outwards across the edge.</param>
        /// <param name="along">The unit step along the edge, always from its low end.</param>
        /// <param name="count">The lot's side, in tiles.</param>
        /// <param name="roads">The road byte both ends of the seam were given.</param>
        /// <param name="edge">The edge bit being asked about.</param>
        private static bool EdgeAgrees(
            SimitoneLotViewRoadBand band, Vector2 start, Vector2 run, Vector2 along, int count,
            byte roads, byte edge)
        {
            //The middle of the edge only: a band on a neighbouring edge reaches
            //HalfStreetDepth tiles along this one from either end, and that is also where
            //RestoreRoad's corner templates are, so the ends of an edge are a crossing rather than
            //a place two grammars have to agree about. Empty for a lot too small to have a middle,
            //which is a lot too small to be a sub-world - see
            //SimitoneSurroundingsPlan.MinSubWorldSize.
            var from = SimitoneLotViewRoad.HalfStreetDepth;
            var to = count - SimitoneLotViewRoad.HalfStreetDepth;
            if (to <= from) return true;

            var tarmac = SimitoneLotViewRoad.TarmacCountFor(edge);
            var gap = SimitoneLotViewRoad.GapOffsetFor(roads, edge);
            var pavement = SimitoneLotViewRoad.PavementOffsetFor(roads, edge);
            for (var t = from; t < to; t++)
            {
                var line = start + along * t;
                for (var k = 0; k < tarmac; k++)
                {
                    if (!Agrees(band, line + run * (SimitoneLotViewRoad.TarmacFromEdge + k),
                        SimitoneLotViewRoadBand.Tarmac)) return false;
                }
                if (!Agrees(band, line + run * gap, SimitoneLotViewRoadBand.None)) return false;
                if (!Agrees(band, line + run * pavement,
                    SimitoneLotViewRoadBand.Pavement)) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether the band's own tile at a map position is the kind of street the sub-world's
        /// grammar puts there. A tile the band does not cover is a yes: it is outside the drawn
        /// region, not a disagreement.
        /// </summary>
        private static bool Agrees(SimitoneLotViewRoadBand band, Vector2 mapTile, byte kind)
        {
            var x = (int)mapTile.X;
            var z = (int)mapTile.Y;
            if (x < band.MinX || z < band.MinZ || x >= band.MaxX || z >= band.MaxZ) return true;
            return band.TileAt(x, z) == kind;
        }

        /// <summary>
        /// How far the merged ground may be from the surround's own, in lot-view world units: half
        /// an altitude unit, the rounding in
        /// <see cref="SimitoneSurroundingsPlacement.SubWorldAltitudeFor"/>. Six hundredths of a
        /// tile, and well under a pixel at any lot-view zoom.
        /// </summary>
        public const float SubWorldGroundTolerance =
            SimitoneNeighbourTerrain.LotTerrainFactor * SimitoneSurroundingsPlacement.LotViewUnitsPerTile * 0.5f;

        /// <summary>
        /// Gives the temporary VM a non-zero object limit if it has none, so that
        /// <c>RestoreTerrain</c> does not overwrite this lot's direction with
        /// <c>PickRoadDir</c>. See the type remarks.
        /// </summary>
        private static void EnsureObjectLimit(VM tempVM)
        {
            if (tempVM.TSOState != null && tempVM.TSOState.ObjectLimit == 0)
            {
                tempVM.TSOState.ObjectLimit = BlankObjectLimit;
            }
        }

        /// <summary>
        /// A throwaway <c>VMTSOSurroundingTerrain</c> holding one lot's terrain, in the shape the
        /// helpers read: the lot's blend and road byte at the centre of the 3x3, and all sixteen
        /// height samples.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only <c>[1, 1]</c> is ever read, and that is not an accident of the layout: the helpers
        /// address the lot they are building as the centre of a 3x3, so
        /// <c>RestoreHeight(tempVM, terrain, 1, 1)</c> and <c>GetBaseLevel(tempVM, terrain, 1, 1)</c>
        /// both look there. Writing the whole grid is not extra work - sixteen byte writes - and it
        /// makes the one-off container a faithful copy of what a TSO lot's own state would have
        /// held, so the next person to read a stack trace through here finds what they expect.
        /// </para>
        /// <para>
        /// <b>The two halves are passed through, not converted.</b> The road byte is
        /// <see cref="SimitoneNeighbourTerrain.Roads"/> exactly as
        /// <c>RestoreRoad</c> reads it - the four low bits are the four edges, the high nibble the
        /// four corners - and the heights are already in the helpers' own
        /// <c>ARRY(0)</c>-hundredth bytes, so the hundredths never appear here. Nothing in this
        /// method touches an elevation byte, which is the right place for that to be true: the
        /// conversion is <see cref="SimitoneNeighbourTerrain.HeightByteFor"/>, it is one function,
        /// and it composes
        /// <see cref="NeighbourhoodCityMeshBuilder.TilesPerArryUnit"/> with
        /// <see cref="NeighbourhoodCityMeshBuilder.ElevationScale"/>. A second conversion here
        /// would be a second answer to a question that already has one, and the two would not be
        /// the same answer.
        /// </para>
        /// <para>
        /// A default <paramref name="terrain"/> - no grid - produces a container of zeros rather
        /// than a throw, because the only caller can pass one on a lot the map could not answer
        /// for, and a flat lot at elevation zero is a worse answer than none but a better one than
        /// a neighbour that takes the whole surround down with it. The plan does not select such a
        /// lot (<see cref="SimitoneSurroundingsNeighbour.IsValid"/>), so this is a backstop.
        /// </para>
        /// </remarks>
        /// <param name="terrain">A neighbour's terrain, as the plan built it.</param>
        public static VMTSOSurroundingTerrain TerrainContainerFor(SimitoneNeighbourTerrain terrain)
        {
            var container = new VMTSOSurroundingTerrain();
            container.BlendN[1, 1] = terrain.Blend;
            container.Roads[1, 1] = terrain.Roads;
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    container.Height[x, y] = terrain.HeightAt(x, y);
                }
            }
            return container;
        }
    }
}
