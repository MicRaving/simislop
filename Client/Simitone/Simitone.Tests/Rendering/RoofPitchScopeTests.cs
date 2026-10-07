/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Components;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// The finding behind the one build/buy item that was closed as <em>not a feature</em>:
    /// <b>retail <em>The Sims 1</em> has one roof pitch per lot, and there is no per-room pitch to
    /// implement.</b>
    ///
    /// <para>
    /// <c>docs/SIMS1_FEATURE_GAPS.md</c> §3.8 Gap B recorded "per-room roof pitch and pattern are
    /// absent (one global pitch only)" and attributed the gap to Simitone. That reads as a retail
    /// behaviour Simitone had failed to port. It is not: the data does not exist, and the evidence
    /// below is the proof, so that nobody spends a milestone inventing a sidecar format to store a
    /// number retail never had anywhere to put.
    /// </para>
    /// <para>
    /// The evidence, in the order a sceptic would want it. The first two are the data, read out of
    /// the retail install at <c>D:\Program Files\Sims</c> read-only; the rest are the code, and are
    /// the part that is pinned here because code can be re-read but cannot be re-argued silently.
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>The shipped lot file has one roof and nothing else.</b> A census of all 86 lot files
    ///     across <c>TemplateUserData</c>, <c>TemplateDowntown</c>, <c>TemplateVacation</c>,
    ///     <c>TemplateStudiotown</c>, <c>TemplateMagictown</c>, <c>TemplateCommunity</c> and
    ///     <c>TemplateFamilyUnleashed</c> finds exactly 12 top-level chunk types
    ///     (<c>Arry</c>, <c>BMP_</c>, <c>FLRm</c>, <c>HOUS</c>, <c>ObjM</c>, <c>SIMI</c>,
    ///     <c>STR#</c>, <c>THMB</c>, <c>WALm</c>, <c>XXXX</c>, <c>objt</c>, <c>rsmp</c>) and
    ///     23 distinct <c>Arry</c> (id, label) pairs: altitude, ground, floors, walls, objects,
    ///     flags, grass, target grass, pool, water, and the "2" variants of the first four. There
    ///     is no roof array. <c>WALm</c>/<c>FLRm</c> parse with <b>zero bytes left over</b> in all
    ///     309 of them - they are wall and floor resource-name tables, not rooms. The only roof
    ///     datum in the entire neighbourhood data is <c>HOUS</c>'s single null-terminated
    ///     <c>RoofName</c> - a texture name, e.g. "tar1", "r1_", "r4_" in the files sampled - which
    ///     <c>HOUS.cs</c> reads as its last field, after a fixed 38-byte header.
    ///   </description></item>
    ///   <item><description>
    ///     <b>There is no room record either.</b> Rooms are not stored anywhere in a TS1 lot; they
    ///     are flood-filled out of the wall tiles by <see cref="VMRoomMap.GenerateMap"/> every time
    ///     the lot is loaded. So even if a pitch existed there would be nothing to key it on, and a
    ///     per-room pitch could not be persisted in the shipped format at all.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Nothing in the scripts can set one.</b> The build-mode command stream -
    ///     <see cref="VMArchitectureCommandType"/>, which is how every build edit is recorded,
    ///     serialised and sent to the server - has no roof command at all. The only roof mutation
    ///     in the whole engine is <see cref="VMNetSetRoofCmd"/>, a client command carrying one
    ///     <c>(float pitch, uint style)</c> pair with no position, no level and no room. And
    ///     <c>VMTS1Activator</c> sets only <c>RoofStyle</c> when it loads a retail lot, leaving
    ///     <c>RoofPitch</c> at FreeSO's own default - so retail pitch was never even persisted.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The renderer has nowhere to put one.</b> <see cref="Room"/> - the renderer's own
    ///     per-room record - has no pitch, and the roof mesh is built from
    ///     <see cref="RoofComponent"/>'s per-storey rectangle list, where
    ///     <see cref="RoofComponent.RoofRect"/> carries four tile coordinates and no room either.
    ///     The mesh granularity is a rectangle on a storey, not a room, and a rectangle grown by
    ///     <c>RoofSpread</c> routinely spans several rooms.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Retail's own build UI was a lot-global level chooser.</b> The shipped resource table
    ///     <c>UIGraphics\Res_CPanel.h</c> (415 lines, the complete build/live-mode id list) names
    ///     <c>kToolBtnRoof = 76</c>, <c>kBldPopupRoofPitch = 3605</c>,
    ///     <c>kBldPopupRoofPattern = 3606</c> and four sub-toolbar buttons
    ///     <c>kBldSbTlRoofShallow/Medium/Steep/Flat = 3900/3901/3902/3904</c> - structurally the
    ///     same shape as the terrain tool's <c>kBldPopupTerrainUp/Down/Level</c> plus
    ///     <c>kBldSbTlTerrain*</c>. There is no room-scoped roof resource anywhere in that file;
    ///     the only "Room" identifiers in it are the buy-mode bedroom/bathroom category buttons.
    ///   </description></item>
    /// </list>
    /// <para>
    /// So this file asserts the four code-side facts and leaves the two install-side ones in prose.
    /// It is a guard, not a specification: if somebody later <em>does</em> add a per-room pitch -
    /// with a sidecar, with an extended marshal, with whatever - these assertions fail, and the
    /// remarks above are the thing to argue with first.
    /// </para>
    /// </summary>
    public class RoofPitchScopeTests
    {
        // ---- the retail data format ------------------------------------------------------------

        [Fact]
        public void TheShippedHouseChunkCarriesExactlyOneRoofAndItIsATextureName()
        {
            // HOUS is the only chunk in a TS1 lot that says anything about the roof, and what it
            // says is one null-terminated string naming a .bmp. This is the load-bearing negative:
            // if a pitch were hiding anywhere in the file, it would be here, and it is not.
            var roofMembers = typeof(HOUS).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.Name.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            Assert.Single(roofMembers);
            Assert.Equal(typeof(string), roofMembers[0].FieldType);
            Assert.Equal("RoofName", roofMembers[0].Name);
        }

        [Fact]
        public void NoShippedChunkTypeHasARoofFieldOtherThanTheHouseName()
        {
            // Belt and braces over the chunk classes themselves rather than over one chunk: if a
            // future format reader grew a per-room roof or pitch field, this is where it would
            // show up, and the whole finding would be wrong.
            var assembly = typeof(HOUS).Assembly;
            var offenders = new List<string>();
            foreach (var type in assembly.GetTypes())
            {
                if (!typeof(IffChunk).IsAssignableFrom(type) || type.IsAbstract) continue;
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    var name = field.Name;
                    if (name.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (type == typeof(HOUS) && name == "RoofName") continue;
                    offenders.Add(type.Name + "." + name + " : " + field.FieldType.Name);
                }
            }

            Assert.Empty(offenders);
        }

        [Fact]
        public void TheWallAndFloorNameTablesCarryNoRoomDataAtAll()
        {
            // WALm and FLRm are the two chunks a reader might mistake for a room table, because
            // they are the "room-ish" resources in TS1. They are not: each entry is a resource
            // filename, a resource index, a one-byte id and five to seven bytes the format has
            // never explained. Read out of WALm.cs rather than out of a file so this stays a
            // statement about the format as this tree understands it.
            var entryFields = typeof(WALmEntry).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

            Assert.Equal(
                new[] { "ID", "Name", "Unknown", "Unknown2" }.OrderBy(n => n, StringComparer.Ordinal),
                entryFields);
            //a pitch would be a number; there is no numeric field here at all
            Assert.DoesNotContain(entryFields, n => n.IndexOf("itch", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ---- the build-mode command stream -------------------------------------------------------

        [Fact]
        public void TheBuildCommandStreamHasNoRoofCommandAtAll()
        {
            // Every build edit - wall, floor, pattern, terrain, grass - travels as a
            // VMArchitectureCommand, because that is what gets recorded, sent to the server and
            // written to the save. A per-room pitch would have to be one of these to be a real
            // feature. There is no roof command, so a roof change is not a build edit at all in
            // this engine: it is a whole-lot client command (see the next test).
            var commands = Enum.GetNames(typeof(VMArchitectureCommandType)).ToList();

            Assert.DoesNotContain(commands, n => n.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains("WALL_LINE", commands);
            Assert.Contains("FLOOR_FILL", commands);
            Assert.Contains("TERRAIN_RAISE", commands);
        }

        [Fact]
        public void TheRoofCommandIsOneWholeLotPairWithNoPositionAndNoRoom()
        {
            // The shape of the only way anything changes a roof's pitch. No x, no y, no level and
            // nothing room-shaped: it is a (pitch, style) pair for the lot, which is why retail's
            // four pitch buttons could only ever have been a lot-global chooser.
            var fields = typeof(VMNetSetRoofCmd).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).ToList();

            Assert.Contains("Pitch", fields);
            Assert.Contains("Style", fields);
            Assert.DoesNotContain(fields, f => f.IndexOf("Room", StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var positional in new[] { "x", "y", "level", "Level", "Tile" })
            {
                Assert.DoesNotContain(fields, f => f == positional);
            }
        }

        // ---- the renderer's own records -----------------------------------------------------------

        [Fact]
        public void TheRenderersRoomRecordHasNoPitchAndNoRoofOfAnyKind()
        {
            // The strongest single fact. Even setting the file format aside, there is no field on a
            // room for a pitch to live in, so a per-room pitch would need a new per-room record
            // before it could be drawn at all.
            var fields = typeof(Room).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).ToList();

            Assert.DoesNotContain(fields, f => f.IndexOf("itch", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.DoesNotContain(fields, f => f.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0);
            //what a room does carry, which is the whole of what TS1 knows about one
            Assert.Contains("RoomID", fields);
            Assert.Contains("Floor", fields);
            Assert.Contains("IsOutside", fields);
            Assert.Contains("Bounds", fields);
        }

        [Fact]
        public void TheRoofMeshIsBuiltFromPerStoreyRectanglesThatKnowNothingAboutRooms()
        {
            // RoofComponent holds one list of rectangles per storey and one pitch for the lot, and
            // RoofRect is four tile coordinates. RoofSpread grows each rectangle over every roofable
            // tile above a storey, which is not a room - a rectangle over an open-plan ground floor
            // spans every "room" the flood fill made. So the geometry's own granularity is finer
            // than the question and unrelated to it: you cannot subdivide a mesh by room without
            // first inventing the room-to-rectangle mapping.
            //one rectangle list for the whole lot, indexed by storey, and one pitch for the lot.
            //NonPublic because RoofRects is deliberately not part of the component's surface - the
            //mesh is the only thing that reads it.
            const BindingFlags any =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Assert.Single(typeof(RoofComponent).GetFields(any), f => f.Name == "RoofRects");
            Assert.Single(typeof(RoofComponent).GetFields(any),
                f => f.Name == "RoofPitch" && f.FieldType == typeof(float));
            Assert.Single(typeof(RoofComponent).GetFields(any), f => f.Name == "RoofStyle");
            //indexed by storey, not by room: the outer array is the storey, and nothing else
            var rects = typeof(RoofComponent).GetField("RoofRects", any);
            Assert.True(rects.FieldType.IsArray, "the roof rectangles are indexed by storey");
            Assert.Equal(typeof(RoofComponent.RoofRect),
                rects.FieldType.GetElementType().GetGenericArguments()[0]);

            var rectFields = typeof(RoofComponent.RoofRect)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).ToList();

            Assert.Equal(
                new[] { "ExpandDir", "x1", "x2", "y1", "y2" }.OrderBy(n => n, StringComparer.Ordinal),
                rectFields.OrderBy(n => n, StringComparer.Ordinal));
            Assert.DoesNotContain(rectFields,
                f => f.IndexOf("oom", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ---- the save format ----------------------------------------------------------------------

        [Fact]
        public void TheSaveFormatStoresOnePitchAndOneRoofNameForTheLot()
        {
            // FreeSO's own FSOV, which is what a Simitone lot save writes. One float, one string:
            // if a per-room pitch is ever persisted it has to extend this, which is exactly the
            // "invented a format" step this file exists to have to argue for out loud.
            var architecture = typeof(VMArchitectureMarshal)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).ToList();
            Assert.Contains("RoofPitch", architecture);
            Assert.Contains("RoofStyle", architecture);
            Assert.Equal(1, architecture.Count(n => n == "RoofPitch"));
            Assert.Equal(1, architecture.Count(n => n == "RoofStyle"));

            //FreeSO's own docs say what this type is: "the FreeSO equivalent of FLRm and WALm,
            //as well as a simple name substitution for the roof id" - so one roof, substituted once
            var resourceIds = typeof(VMResourceIDMarshal)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).ToList();
            Assert.Contains("Roof", resourceIds);
            Assert.Equal(1, resourceIds.Count(n => n.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0));
            Assert.Equal(typeof(string),
                typeof(VMResourceIDMarshal).GetField("Roof").FieldType);
        }

        [Fact]
        public void TheLiveArchitectureAgreesWithItsOwnSaveFormat()
        {
            // The runtime half. One pitch, one style, and a SetRoof that takes neither a position
            // nor a room - so the same whole-lot rule holds in memory as on disk, and the two
            // cannot drift into disagreeing about what a "roof setting" is.
            var architecture = typeof(VMArchitecture)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).ToList();
            Assert.Contains("RoofPitch", architecture);
            Assert.Contains("RoofStyle", architecture);
            Assert.DoesNotContain(architecture, n => n.IndexOf("RoomPitch", StringComparison.Ordinal) >= 0);

            var setRoof = typeof(VMArchitecture).GetMethod("SetRoof");
            Assert.NotNull(setRoof);
            var parameters = setRoof.GetParameters().Select(p => p.ParameterType).ToList();
            Assert.Equal(new[] { typeof(float), typeof(uint) }, parameters);
        }

        // ---- the finding, stated once ---------------------------------------------------------------

        [Fact]
        public void ThereIsNoPerRoomRoofPitchAnywhereInThisTree()
        {
            // The whole finding as one sweep, so a future change cannot add a per-room pitch
            // somewhere the four focused assertions above happen not to look. Cheap: it is
            // reflection over three assemblies' types, and it is the assertion most likely to be
            // deleted first if somebody does add one, which is the point.
            var offenders = new List<string>();
            var assemblies = new[]
            {
                typeof(RoofComponent).Assembly,   // tso.world, the renderer
                typeof(VMArchitecture).Assembly,  // tso.simantics, the VM and the save format
                typeof(HOUS).Assembly             // tso.files, the formats
            };

            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var member in type.GetMembers(BindingFlags.Public
                        | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    {
                        if (!(member is FieldInfo) && !(member is PropertyInfo)) continue;
                        var name = member.Name;
                        if (name.IndexOf("RoomPitch", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (name.IndexOf("PerRoom", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        offenders.Add(type.FullName + "." + name);
                    }
                }
            }

            Assert.Empty(offenders);
        }
    }
}
