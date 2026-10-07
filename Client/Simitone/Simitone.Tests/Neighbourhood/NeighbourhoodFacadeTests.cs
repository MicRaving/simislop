/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using FSO.LotView.Components;
using FSO.LotView.Facade;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The facade cache's rules and the placement of a facade in the world. §7.2.
    ///
    /// <para>
    /// A facade is the expensive part of the neighbourhood: building one stands a
    /// <c>FSO.LotView.World</c> and a <c>FSO.SimAntics.VM</c> up for a lot and renders its walls,
    /// which is why <c>SimitoneNeighOBJExporter</c> is a debug-flagged tool rather than something
    /// the game does behind a player's back. None of that can be tested without a GPU - so what is
    /// tested here is everything that decides <em>whether</em> to build one, which is the part
    /// that is a policy rather than a rendering.
    /// </para>
    /// </summary>
    public class NeighbourhoodFacadeTests
    {
        private static NeighbourhoodLot Lot(short number, int worldX, int worldY, uint hash = 0xABCD, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = 32,
                WorldX = worldX,
                WorldY = worldY,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = hash,
                HasHouse = hasHouse
            };
        }

        private static NeighbourhoodLayout LayoutOf(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(4, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion, 512, lots);
        }

        // ---- which lots are drawn -------------------------------------------------------------

        [Fact]
        public void AHouseIsDrawnAndAnEmptyPlotIsNot()
        {
            var house = Lot(11, 0, 0);
            var plot = Lot(12, 64, 0, 0xABCD, false);

            Assert.True(NeighbourhoodFacadePlan.ShouldDraw(house, null));
            // the plan's "lots with no house draw nothing, or a bare plot" - a bare plot is not
            // implemented yet, so a bulldozed lot contributes nothing
            Assert.False(NeighbourhoodFacadePlan.ShouldDraw(plot, null));
            Assert.False(NeighbourhoodFacadePlan.ShouldDraw(null, null));
        }

        [Fact]
        public void TheLotThePlayerIsStandingInIsNotDrawn()
        {
            // §7.2: the active lot is drawn in full by the lot view, so drawing a facade of it as
            // well would put two copies of the same house on screen.
            var house = Lot(11, 0, 0);

            Assert.True(NeighbourhoodFacadePlan.ShouldDraw(house, null));
            Assert.True(NeighbourhoodFacadePlan.ShouldDraw(house, 12));
            Assert.False(NeighbourhoodFacadePlan.ShouldDraw(house, 11));
        }

        // ---- which lots are built -------------------------------------------------------------

        [Fact]
        public void ALotWithNoCacheEntryNeedsBuilding()
        {
            var house = Lot(11, 0, 0);
            Assert.True(NeighbourhoodFacadePlan.NeedsBuild(house, new Dictionary<short, uint>()));
            Assert.True(NeighbourhoodFacadePlan.NeedsBuild(house, null));
        }

        [Fact]
        public void ALotWhoseHashHasNotMovedDoesNotNeedRebuilding()
        {
            // The cache key is the house file's fingerprint, and nothing else. A lot's furniture
            // changing must not cost a re-mesh of its walls; a lot's walls changing must not be
            // drawn with the mesh it had before.
            var house = Lot(11, 0, 0, 0xDEADBEEF);
            var cache = new Dictionary<short, uint> { { 11, 0xDEADBEEF } };

            Assert.False(NeighbourhoodFacadePlan.NeedsBuild(house, cache));
        }

        [Fact]
        public void ALotWhoseFileChangedIsRebuilt()
        {
            // A bulldoze rewrites HouseNN.iff, so its size and timestamp move, so its hash moves.
            // This is what makes "editing a lot is reflected on return" true rather than hoped for.
            var house = Lot(11, 0, 0, 0x0EE0);
            var cache = new Dictionary<short, uint> { { 11, 0x010D } };

            Assert.True(NeighbourhoodFacadePlan.NeedsBuild(house, cache));
        }

        [Fact]
        public void AFacadeIsNeverRebuiltForTheSameFileTwice()
        {
            var layout = LayoutOf(Lot(11, 0, 0, 0xAAAA), Lot(12, 64, 0, 0xBBBB));
            var cache = new Dictionary<short, uint> { { 11, 0xAAAA }, { 12, 0xBBBB } };

            var wanted = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 100);
            Assert.Empty(wanted);
        }

        [Fact]
        public void OnlyTheUncachedLotsAreBuilt()
        {
            var layout = LayoutOf(Lot(11, 0, 0, 0xAAAA), Lot(12, 64, 0, 0xBBBB), Lot(13, 128, 0, 0xCCCC));
            var cache = new Dictionary<short, uint> { { 12, 0xBBBB } };

            var wanted = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 100);
            Assert.Equal(2, wanted.Count);
            Assert.Contains(wanted, l => l.Number == 11);
            Assert.Contains(wanted, l => l.Number == 13);
        }

        // ---- the build order and budget ------------------------------------------------------

        [Fact]
        public void LotsAreBuiltNearestTheCameraFirst()
        {
            // A fifty-lot neighbourhood fills in over a second or so rather than stalling for four,
            // and it fills in from what the player is looking at.
            var layout = LayoutOf(
                Lot(11, 0, 0),
                Lot(12, 64, 0),
                Lot(13, 400, 400));

            var wanted = NeighbourhoodFacadePlan.ToBuild(layout, new Dictionary<short, uint>(), null,
                new Vector2(0, 0), 100);

            Assert.Equal(3, wanted.Count);
            Assert.Equal(11, wanted[0].Number);
            Assert.Equal(12, wanted[1].Number);
            Assert.Equal(13, wanted[2].Number);
        }

        [Fact]
        public void TheBuildListIsBudgeted()
        {
            // One mesh at a time would be tidier and would take a minute to fill a big
            // neighbourhood; the whole list at once would freeze the screen. The budget is the knob
            // between them, and it is a parameter so a test can drive it.
            var layout = LayoutOf(Lot(11, 0, 0), Lot(12, 64, 0), Lot(13, 128, 0), Lot(14, 192, 0));

            Assert.Equal(2, NeighbourhoodFacadePlan.ToBuild(layout, new Dictionary<short, uint>(),
                null, Vector2.Zero, 2).Count);
            Assert.Equal(4, NeighbourhoodFacadePlan.ToBuild(layout, new Dictionary<short, uint>(),
                null, Vector2.Zero, 4).Count);
            Assert.Empty(NeighbourhoodFacadePlan.ToBuild(layout, new Dictionary<short, uint>(),
                null, Vector2.Zero, 0));
        }

        [Fact]
        public void TheBuildOrderIsDeterministic()
        {
            // Two lots at the same distance must not swap places between frames, or the fill-in
            // order is different every frame and the budget starves a different lot each time.
            var layout = LayoutOf(Lot(12, 64, 0), Lot(11, 0, 0), Lot(13, 128, 0));
            var cache = new Dictionary<short, uint>();

            var first = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 1);
            var second = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 1);
            Assert.Equal(first[0].Number, second[0].Number);
        }

        [Fact]
        public void AnEmptyPlotIsNeverQueuedForBuilding()
        {
            // It has nothing to render, so building it would stand a world and a VM up for a lot
            // that will produce no mesh.
            var layout = LayoutOf(Lot(11, 0, 0, 0xAAAA, false), Lot(12, 64, 0, 0xBBBB));

            var wanted = NeighbourhoodFacadePlan.ToBuild(layout, new Dictionary<short, uint>(), null,
                Vector2.Zero, 100);
            Assert.Equal(1, wanted.Count);
            Assert.Equal(12, wanted[0].Number);
        }

        [Fact]
        public void TheActiveLotIsNeverQueuedForBuilding()
        {
            var layout = LayoutOf(Lot(11, 0, 0), Lot(12, 64, 0));

            var wanted = NeighbourhoodFacadePlan.ToBuild(layout, new Dictionary<short, uint>(),
                11, Vector2.Zero, 100);
            Assert.Equal(1, wanted.Count);
            Assert.Equal(12, wanted[0].Number);
        }

        [Fact]
        public void AFailedLotIsNotRetriedForever()
        {
            // A lot whose HouseNN.iff will not parse throws inside the generator. If the failure
            // were not recorded against its hash, the plan would ask for it every frame, and one
            // bad file would become the most expensive thing in the neighbourhood. The cache
            // records the hash either way, so NeedsBuild is false for it next frame.
            var broken = Lot(11, 0, 0, 0xBA0);
            var cache = new Dictionary<short, uint> { { 11, 0xBA0 } };

            Assert.False(NeighbourhoodFacadePlan.NeedsBuild(broken, cache));
        }

        [Fact]
        public void AQueueIsEmptyWithoutALayout()
        {
            Assert.Empty(NeighbourhoodFacadePlan.ToBuild(null, new Dictionary<short, uint>(), null, Vector2.Zero, 100));
        }

        // ---- invalidation ---------------------------------------------------------------------

        [Fact]
        public void ALotWhoseFileChangedIsStale()
        {
            var layout = LayoutOf(Lot(11, 0, 0, 0x0EE0));
            var cache = new Dictionary<short, uint> { { 11, 0x010D } };

            Assert.Equal(new short[] { 11 }, NeighbourhoodFacadePlan.Stale(layout, cache));
        }

        [Fact]
        public void ALotTheLayoutNoLongerMentionsIsStale()
        {
            // A neighbourhood edited under the cache: a lot that has been bulldozed out of the
            // location table is as stale as one whose file changed, and its mesh is now a house
            // standing where the player deleted it.
            var layout = LayoutOf(Lot(11, 0, 0, 0x5A3E));
            var cache = new Dictionary<short, uint> { { 11, 0x5A3E }, { 99, 0x60E5 } };

            Assert.Equal(new short[] { 99 }, NeighbourhoodFacadePlan.Stale(layout, cache));
        }

        [Fact]
        public void ACacheWithNothingStaleIsEmpty()
        {
            var layout = LayoutOf(Lot(11, 0, 0, 0x5A3E), Lot(12, 64, 0, 0xA1505E));
            var cache = new Dictionary<short, uint> { { 11, 0x5A3E }, { 12, 0xA1505E } };

            Assert.Empty(NeighbourhoodFacadePlan.Stale(layout, cache));
            Assert.Empty(NeighbourhoodFacadePlan.Stale(null, cache));
            Assert.Empty(NeighbourhoodFacadePlan.Stale(layout, null));
        }

        [Fact]
        public void StaleLotsComeBackInLotNumberOrder()
        {
            var layout = LayoutOf(Lot(30, 0, 0, 0xA), Lot(10, 64, 0, 0xB), Lot(20, 128, 0, 0xC));
            var cache = new Dictionary<short, uint> { { 30, 0x010D }, { 10, 0x010D }, { 20, 0x010D } };

            Assert.Equal(new short[] { 10, 20, 30 }, NeighbourhoodFacadePlan.Stale(layout, cache));
        }

        // ---- placement in the world -----------------------------------------------------------

        [Fact]
        public void ALotLocalPointBecomesTheLotsRectangleInTheWorld()
        {
            // LotFacadeGenerator emits its vertices in lot tiles from 0 to the lot's size, and the
            // generated world is absolute map tiles, so the placement is a translation by the lot's
            // packed origin. Getting this wrong puts every house in the wrong place, and getting
            // it subtly wrong puts them all the same distance wrong.
            var lot = Lot(11, 200, 300);

            Assert.Equal(new Vector3(200, 4, 300), NeighbourhoodFacadeTransform.ToWorld(lot, Vector3.Zero, 4f));
            Assert.Equal(new Vector3(232, 4, 332), NeighbourhoodFacadeTransform.ToWorld(lot, new Vector3(32, 0, 32), 4f));
            // y is height above the lot's own ground, not absolute
            Assert.Equal(new Vector3(200, 4 + 2.95f, 300), NeighbourhoodFacadeTransform.ToWorld(lot, new Vector3(0, 2.95f, 0), 4f));
        }

        [Fact]
        public void AFacadeStandsOnTheGroundTheMapPutUnderIt()
        {
            // Not on the lot's own altitude: the generated map has already had the seam solver's
            // way with the border rings, and a house standing a few centimetres off its own ground
            // is the single most obvious way a generated neighbourhood looks wrong.
            var map = NeighbourhoodMap.Create(512);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            var lot = Lot(11, 200, 300);

            Assert.Equal(4f, NeighbourhoodFacadeTransform.GroundHeightFor(map, lot));

            map.ElevationData[map.Index(200, 300)] = 120;
            Assert.Equal(10f, NeighbourhoodFacadeTransform.GroundHeightFor(map, lot));
        }

        [Fact]
        public void ALotOffTheMapIsAskedAboutAtItsNearestInMapTile()
        {
            // The packer clamps lots into the map, so this should not happen - but a lot past the
            // edge has to give a height rather than throw, because the alternative is one bad
            // rectangle taking a whole neighbourhood down. The clamp is to the map, not to the lot,
            // so a lot that hangs over the corner still stands on the ground that is there.
            var map = NeighbourhoodMap.Create(512);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 24;

            //wholly inside: the lot's own origin
            var inside = Lot(11, 200, 200);
            Assert.Equal(2f, NeighbourhoodFacadeTransform.GroundHeightFor(map, inside));

            //past the bottom-right corner: the map's last tile, which is height 24
            var overhanging = Lot(12, 508, 508);
            Assert.Equal(2f, NeighbourhoodFacadeTransform.GroundHeightFor(map, overhanging));

            //wholly off: still the map's last tile rather than an exception
            var gone = Lot(13, 600, 600);
            Assert.Equal(2f, NeighbourhoodFacadeTransform.GroundHeightFor(map, gone));

            //negative, from a corrupt sidecar: the map's first tile
            var negative = Lot(14, -40, -40);
            Assert.Equal(2f, NeighbourhoodFacadeTransform.GroundHeightFor(map, negative));

            //a map that cannot be asked at all
            Assert.Equal(0f, NeighbourhoodFacadeTransform.GroundHeightFor(map, null));
            Assert.Equal(0f, NeighbourhoodFacadeTransform.GroundHeightFor(null, inside));
        }

        [Fact]
        public void AFacadesBoundsAreItsOwnRectangle()
        {
            var lot = Lot(11, 200, 300);
            var bounds = NeighbourhoodFacadeTransform.BoundsFor(lot, 4f);

            Assert.Equal(new Vector3(200, 4, 300), bounds.Min);
            Assert.Equal(200 + lot.Size, bounds.Max.X);
            Assert.Equal(300 + lot.Size, bounds.Max.Z);
            Assert.True(bounds.Max.Y > bounds.Min.Y, "a house's bounds should have room for walls");
        }

        [Fact]
        public void ABoundsWithNoLotIsAnEmptyBox()
        {
            // A degenerate box has to still be a box, because the frustum test is handed it
            // unconditionally and an invalid BoundingBox would throw rather than cull.
            var bounds = NeighbourhoodFacadeTransform.BoundsFor(null, 0f);
            Assert.Equal(Vector3.Zero, bounds.Min);
            Assert.Equal(Vector3.Zero, bounds.Max);
        }

        [Fact]
        public void ARotatedHouseStaysInsideItsOwnPlot()
        {
            // CameraDir is a quarter turn, and a square is invariant under one, so a house that
            // faces a different way from its plot still fits the plot - which is what lets the
            // roads and sidewalks around it stay where they are.
            var lot = Lot(11, 200, 300);
            lot.Rotation = 0;

            foreach (var rotation in new[] { 0, 1, 2, 3 })
            {
                lot.Rotation = rotation;
                foreach (var local in new[]
                {
                    new Vector3(0, 0, 0),
                    new Vector3(32, 0, 0),
                    new Vector3(0, 0, 32),
                    new Vector3(32, 0, 32),
                    new Vector3(16, 0, 16)
                })
                {
                    var world = NeighbourhoodFacadeTransform.ToWorld(lot, local, 0f);
                    Assert.InRange(world.X, lot.WorldX - 0.01f, lot.WorldX + lot.Size + 0.01f);
                    Assert.InRange(world.Z, lot.WorldY - 0.01f, lot.WorldY + lot.Size + 0.01f);
                }
            }
        }

        [Fact]
        public void ARotatedHouseIsActuallyRotated()
        {
            var lot = Lot(11, 0, 0);
            var corner = new Vector3(0, 0, 0);

            lot.Rotation = 0;
            Assert.Equal(corner, NeighbourhoodFacadeTransform.ToWorld(lot, corner, 0f));

            // a quarter turn takes the lot's near corner to a different corner, so a rotation that
            // silently did nothing would look like a house that always faces the same way
            lot.Rotation = 1;
            var turned = NeighbourhoodFacadeTransform.ToWorld(lot, corner, 0f);
            Assert.NotEqual(corner, turned);
        }

        [Fact]
        public void AnOutOfRangeRotationFoldsRatherThanMirroring()
        {
            // A corrupt CameraDir must not produce a mirrored or out-of-range house. Four quarter
            // turns is the identity, and a negative one is a backwards quarter turn.
            var centre = 16f;
            NeighbourhoodFacadeTransform.Rotate(0, 0, centre, 0, out var a, out var b);
            NeighbourhoodFacadeTransform.Rotate(0, 0, centre, 4, out var c, out var d);
            NeighbourhoodFacadeTransform.Rotate(0, 0, centre, -4, out var e, out var f);

            Assert.Equal(a, c); Assert.Equal(b, d);
            Assert.Equal(a, e); Assert.Equal(b, f);
            Assert.Equal(0f, a); Assert.Equal(0f, b);
        }

        [Fact]
        public void FourQuarterTurnsComeBackToWhereTheyStarted()
        {
            NeighbourhoodFacadeTransform.Rotate(5, 9, 16, 0, out var x0, out var z0);
            for (int i = 0; i < 4; i++)
            {
                NeighbourhoodFacadeTransform.Rotate(x0, z0, 16, 1, out x0, out z0);
            }
            Assert.Equal(5f, x0, 4);
            Assert.Equal(9f, z0, 4);
        }

        // ---- what a failed build says ----------------------------------------------------------
        //
        // The pipeline itself needs a graphics device, so none of it can be run here. What can be
        // pinned is the part that turned "Neighbourhood 64 has no facade: NullReferenceException:
        // Object reference not set to an instance of an object." into something actionable: which
        // reference is reported, on which lot, and in what words. A NullReferenceException names a
        // type and says nothing else, and the four calls into the lot view that can produce one
        // are indistinguishable from the log line alone.

        private static IList<NeighbourhoodFacadeRequirement> ChecklistWith(
            NeighbourhoodFacadePiece missing, params NeighbourhoodFacadePiece[] present)
        {
            var all = new List<NeighbourhoodFacadePiece>
            {
                NeighbourhoodFacadePiece.GraphicsDevice,
                NeighbourhoodFacadePiece.SceneLayer,
                NeighbourhoodFacadePiece.ContentManager,
                NeighbourhoodFacadePiece.NeighbourhoodContent,
                NeighbourhoodFacadePiece.HouseFile,
                NeighbourhoodFacadePiece.SimulationChunk,
                NeighbourhoodFacadePiece.HouseChunk,
                NeighbourhoodFacadePiece.ObjectChunk,
                NeighbourhoodFacadePiece.ObjectTypeChunk,
                NeighbourhoodFacadePiece.LotRenderEffect,
                NeighbourhoodFacadePiece.LotWorldState
            };
            var list = new List<NeighbourhoodFacadeRequirement>();
            foreach (var piece in all)
            {
                if (piece == missing) continue;
                list.Add(new NeighbourhoodFacadeRequirement(piece, true, null));
            }
            // put the missing one back where the pipeline would have hit it: first
            list.Insert(0, new NeighbourhoodFacadeRequirement(missing, false, "the walls cannot be drawn"));
            foreach (var piece in present) list.Add(new NeighbourhoodFacadeRequirement(piece, true, null));
            return list;
        }

        [Fact]
        public void AMissingReferenceIsNamedRatherThanThrownAs()
        {
            // Every piece the builder checks has to have a sentence, or the pass has only traded
            // one anonymous failure for another.
            foreach (NeighbourhoodFacadePiece piece in Enum.GetValues(typeof(NeighbourhoodFacadePiece)))
            {
                if (piece == NeighbourhoodFacadePiece.None) continue;
                var sentence = NeighbourhoodFacadeDiagnostics.Describe(piece);
                Assert.False(string.IsNullOrWhiteSpace(sentence), "piece " + piece + " has no description");
                Assert.DoesNotContain("NullReference", sentence);
            }
        }

        [Fact]
        public void TheFirstUnsatisfiedReferenceIsTheOneReported()
        {
            Assert.Null(NeighbourhoodFacadeDiagnostics.FirstMissing(null));
            Assert.Null(NeighbourhoodFacadeDiagnostics.FirstMissing(new List<NeighbourhoodFacadeRequirement>()));
            Assert.Null(NeighbourhoodFacadeDiagnostics.FirstMissing(
                new[]
                {
                    NeighbourhoodFacadeRequirement.Have(NeighbourhoodFacadePiece.GraphicsDevice),
                    NeighbourhoodFacadeRequirement.Have(NeighbourhoodFacadePiece.Blueprint)
                }));

            // first, not last, and not whichever the list happens to mention last: the reported
            // cause is the one the pipeline would have hit first
            var list = new List<NeighbourhoodFacadeRequirement>
            {
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.Blueprint, true, null),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.RoofComponent, false, "no roof"),
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.SimulationChunk, false, "no size")
            };
            var missing = NeighbourhoodFacadeDiagnostics.FirstMissing(list);
            Assert.NotNull(missing);
            Assert.Equal(NeighbourhoodFacadePiece.RoofComponent, missing.Value.Piece);

            // and the helper the other assertions use puts the missing one first, so a checklist
            // that is otherwise satisfied does report the one that is not
            var only = NeighbourhoodFacadeDiagnostics.FirstMissing(
                ChecklistWith(NeighbourhoodFacadePiece.LotRenderEffect));
            Assert.NotNull(only);
            Assert.Equal(NeighbourhoodFacadePiece.LotRenderEffect, only.Value.Piece);
        }

        [Fact]
        public void AFailedBuildSaysWhatWasNullAndWhoseLotItWas()
        {
            // The log line this replaces was "Neighbourhood 64 has no facade: NullReferenceException:
            // Object reference not set to an instance of an object." Two things are now on the
            // line: the reference, and the lot it was null for.
            foreach (NeighbourhoodFacadePiece piece in Enum.GetValues(typeof(NeighbourhoodFacadePiece)))
            {
                if (piece == NeighbourhoodFacadePiece.None) continue;
                var missing = new NeighbourhoodFacadeRequirement(piece, false, "the walls cannot be drawn");
                var sentence = NeighbourhoodFacadeDiagnostics.Sentence(missing, 64);
                Assert.Contains("64", sentence);
                Assert.Contains(NeighbourhoodFacadeDiagnostics.Describe(piece), sentence);
                Assert.Contains("the walls cannot be drawn", sentence);
                Assert.DoesNotContain("NullReferenceException", sentence);
            }
        }

        [Fact]
        public void TheFourHouseFileChunksAreTheOnesTheActivatorDereferences()
        {
            // The activator reads simi.GlobalData[23] and [35], hous.CameraDir, objm.Prepare and
            // objt.Entries - none of them guarded. These four are therefore the sentences that
            // stand between a truncated HouseNN.iff and a bare NullReferenceException, and each of
            // them has to name its own chunk rather than "the house file".
            var chunks = new[]
            {
                NeighbourhoodFacadePiece.SimulationChunk,
                NeighbourhoodFacadePiece.HouseChunk,
                NeighbourhoodFacadePiece.ObjectChunk,
                NeighbourhoodFacadePiece.ObjectTypeChunk
            };
            foreach (var piece in chunks)
            {
                var described = NeighbourhoodFacadeDiagnostics.Describe(piece);
                Assert.Contains("house file", described);
                Assert.Contains("chunk", described);
            }
            // the loader needs 36 globals and a retail file has 38, so the bound is not invented
            Assert.Equal(36, NeighbourhoodFacadeBuilder.MinimumSimulationGlobals);
        }

        [Fact]
        public void AThrowIsReportedWithTheStepItCameOutOf()
        {
            // What a residual exception still gets. The stages exist so that the cases the
            // preconditions cannot see - inside the lot view, inside the generator - name the call
            // they came out of.
            var sentence = NeighbourhoodFacadeDiagnostics.StageFailure(64,
                "rendering the lot's walls, floor and roof", new NullReferenceException());
            Assert.Contains("while rendering the lot's walls, floor and roof for lot 64", sentence);
            Assert.Contains("NullReferenceException", sentence);

            // a throw whose message says nothing falls back to its type rather than to an empty
            // clause, which is the case the lot view's own internal exceptions are
            Assert.Equal("SilentException: SilentException",
                NeighbourhoodFacadeDiagnostics.DescribeException(new SilentException()));
            Assert.Contains("IndexOutOfRangeException",
                NeighbourhoodFacadeDiagnostics.StageFailure(7, "loading the lot", new IndexOutOfRangeException()));
            // and no throw at all is still a sentence
            Assert.False(string.IsNullOrWhiteSpace(
                NeighbourhoodFacadeDiagnostics.StageFailure(7, "loading the lot", null)));
            Assert.False(string.IsNullOrWhiteSpace(
                NeighbourhoodFacadeDiagnostics.StageFailure(7, null, new Exception("boom"))));
        }

        /// <summary>A throw with no message, which is what the lot view's own ones look like.</summary>
        private sealed class SilentException : Exception
        {
            public override string Message => string.Empty;
        }

        [Fact]
        public void TheChecklistIsOrderedSoTheFirstCauseIsTheEarliestOne()
        {
            // A checklist whose order is arbitrary reports an arbitrary cause. This asserts the
            // order the pipeline runs in, from the device the walls are rasterised on to the lot
            // view's shared render effect the generator draws them with.
            var checklist = NeighbourhoodFacadeBuilder.Checklist(null, null, null, null);
            var pieces = new List<NeighbourhoodFacadePiece>();
            foreach (var requirement in checklist) pieces.Add(requirement.Piece);

            var expected = new[]
            {
                NeighbourhoodFacadePiece.GraphicsDevice,
                NeighbourhoodFacadePiece.SceneLayer,
                NeighbourhoodFacadePiece.ContentManager,
                NeighbourhoodFacadePiece.NeighbourhoodContent,
                NeighbourhoodFacadePiece.HouseFile,
                NeighbourhoodFacadePiece.SimulationChunk,
                NeighbourhoodFacadePiece.HouseChunk,
                NeighbourhoodFacadePiece.ObjectChunk,
                NeighbourhoodFacadePiece.ObjectTypeChunk,
                NeighbourhoodFacadePiece.LotRenderEffect
            };
            Assert.Equal(expected, pieces);
            // everything is missing here, so the first one is the answer
            var missing = NeighbourhoodFacadeDiagnostics.FirstMissing(checklist);
            Assert.NotNull(missing);
            Assert.Equal(NeighbourhoodFacadePiece.GraphicsDevice, missing.Value.Piece);

            // and the blueprint checklist, which is what a lot that loaded but has no roof or no
            // terrain hits
            var blueprint = NeighbourhoodFacadeBuilder.BlueprintChecklist(null);
            var blueprintPieces = new List<NeighbourhoodFacadePiece>();
            foreach (var requirement in blueprint) blueprintPieces.Add(requirement.Piece);
            Assert.Equal(new[]
            {
                NeighbourhoodFacadePiece.Blueprint,
                NeighbourhoodFacadePiece.RoofComponent,
                NeighbourhoodFacadePiece.TerrainComponent
            }, blueprintPieces);
            var missingBlueprint = NeighbourhoodFacadeDiagnostics.FirstMissing(blueprint);
            Assert.NotNull(missingBlueprint);
            Assert.Equal(NeighbourhoodFacadePiece.Blueprint, missingBlueprint.Value.Piece);
        }

        [Fact]
        public void AHouseFileWithNoSimulationChunkIsNotOneTheLoaderCanRead()
        {
            // The predicate behind the sentence. A file with no SIMI 1 is exactly the bare
            // NullReferenceException: the activator's third line is simi.GlobalData[23].
            Assert.False(NeighbourhoodFacadeBuilder.HasSimulationChunk(null));
        }

        // ---- which of the three generator passes a lot is put through -----------------------

        private static IList<NeighbourhoodFacadeRequirement> BlueprintWith(
            bool present, NeighbourhoodFacadePiece missing, params NeighbourhoodFacadePiece[] also)
        {
            var all = new[]
            {
                NeighbourhoodFacadePiece.Blueprint,
                NeighbourhoodFacadePiece.RoofComponent,
                NeighbourhoodFacadePiece.TerrainComponent
            };
            var list = new List<NeighbourhoodFacadeRequirement>();
            foreach (var piece in all)
            {
                if (piece == missing) continue;
                list.Add(new NeighbourhoodFacadeRequirement(piece, true, null));
            }
            //only the blueprint itself is fatal; the other two gate their own pass
            list.Insert(0, new NeighbourhoodFacadeRequirement(missing, present, null,
                missing == NeighbourhoodFacadePiece.Blueprint));
            foreach (var piece in also) list.Add(new NeighbourhoodFacadeRequirement(piece, true, null));
            return list;
        }

        [Fact]
        public void AHouseWithNoRoofComponentStillHasItsWalls()
        {
            // The whole point of the fix. A null roof component drops the roof pass and nothing
            // else; if it stopped the build, the log would say "no facade" for a lot whose walls
            // were sitting there finished.
            var stages = NeighbourhoodFacadeDiagnostics.RequiredStages(
                BlueprintWith(false, NeighbourhoodFacadePiece.RoofComponent));

            Assert.True(NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Walls));
            Assert.True(NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Floor));
            Assert.False(NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Roof));
        }

        [Fact]
        public void AHouseWithNoTerrainComponentStillHasItsWalls()
        {
            // The same rule the other way round: the ground is a surface, the walls are the house.
            var stages = NeighbourhoodFacadeDiagnostics.RequiredStages(
                BlueprintWith(false, NeighbourhoodFacadePiece.TerrainComponent));

            Assert.True(NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Walls));
            Assert.False(NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Floor));
            Assert.True(NeighbourhoodFacadeDiagnostics.Has(stages, NeighbourhoodFacadeStages.Roof));
        }

        [Fact]
        public void AHouseWithARoofComponentRunsAllThreePasses()
        {
            var stages = NeighbourhoodFacadeDiagnostics.RequiredStages(
                BlueprintWith(true, NeighbourhoodFacadePiece.Blueprint));

            Assert.Equal(NeighbourhoodFacadeStages.Walls | NeighbourhoodFacadeStages.Floor
                | NeighbourhoodFacadeStages.Roof, stages);
        }

        [Fact]
        public void TheWallsPassRunsEvenWhenThereIsNothingElseToRun()
        {
            // Walls are not gated on a component: they are the outline of the house, and no other
            // pass produces one. A checklist that arrives empty, or null, still has to leave the
            // walls in the set, or a degenerate input silently builds nothing.
            Assert.Equal(NeighbourhoodFacadeStages.Walls,
                NeighbourhoodFacadeDiagnostics.RequiredStages(null));
            Assert.Equal(NeighbourhoodFacadeStages.Walls,
                NeighbourhoodFacadeDiagnostics.RequiredStages(new List<NeighbourhoodFacadeRequirement>()));
        }

        [Fact]
        public void OnlyAMissingBlueprintStopsTheBuild()
        {
            // FirstFatal is the rule the builder calls, and it is deliberately not FirstMissing: a
            // checklist with a null roof component has an unsatisfied entry and is still buildable.
            Assert.Null(NeighbourhoodFacadeDiagnostics.FirstFatal(null));
            Assert.Null(NeighbourhoodFacadeDiagnostics.FirstFatal(
                new List<NeighbourhoodFacadeRequirement>()));
            Assert.Null(NeighbourhoodFacadeDiagnostics.FirstFatal(
                BlueprintWith(false, NeighbourhoodFacadePiece.RoofComponent)));
            Assert.Null(NeighbourhoodFacadeDiagnostics.FirstFatal(
                BlueprintWith(false, NeighbourhoodFacadePiece.TerrainComponent)));
            Assert.Null(NeighbourhoodFacadeDiagnostics.FirstFatal(
                BlueprintWith(true, NeighbourhoodFacadePiece.Blueprint)));

            // and the one that is fatal
            var fatal = NeighbourhoodFacadeDiagnostics.FirstFatal(
                BlueprintWith(false, NeighbourhoodFacadePiece.Blueprint));
            Assert.NotNull(fatal);
            Assert.Equal(NeighbourhoodFacadePiece.Blueprint, fatal.Value.Piece);
        }

        [Fact]
        public void TheChecklistItselfSaysWhichPiecesEndTheBuild()
        {
            // The rule lives in the checklist rather than in the caller, so the builder today and
            // the next diagnostic tomorrow get the same answer about whether a missing piece ends
            // the build or only one pass.
            var checklist = NeighbourhoodFacadeBuilder.BlueprintChecklist(null);
            var fatal = new List<NeighbourhoodFacadePiece>();
            foreach (var requirement in checklist)
            {
                Assert.False(requirement.Present);
                if (requirement.Fatal) fatal.Add(requirement.Piece);
            }
            Assert.Equal(new[] { NeighbourhoodFacadePiece.Blueprint }, fatal);

            // and every entry of the pre-device checklist really is fatal: without a device or a
            // house file there is no lot to draw at all
            var preDevice = NeighbourhoodFacadeBuilder.Checklist(null, null, null, null);
            foreach (var requirement in preDevice)
            {
                Assert.True(requirement.Fatal, requirement.Piece + " is not fatal, and should be");
            }
        }

        [Fact]
        public void AStoreyWithNoRoofIsNotAThrowItIsNoGeometry()
        {
            // The predicate the engine fix is built on, and the one that has to stay true if the
            // check is ever taken back out of LotFacadeGenerator.GenerateRoof: null, empty, and
            // vertices-without-indices all have to read as "this storey contributed nothing".
            // A house with rooms on the ground floor and nothing over them has four such storeys,
            // and reading data.Vertices off the first of them is what produced lot 64's
            // NullReferenceException.
            Assert.False(LotFacadeGenerator.HasRoofGeometry((RoofData)null));
            Assert.False(LotFacadeGenerator.HasRoofGeometry(new RoofData()));
            Assert.False(LotFacadeGenerator.HasRoofGeometry(
                new RoofData { Vertices = new TerrainParallaxVertex[4] }));
            Assert.False(LotFacadeGenerator.HasRoofGeometry(
                new RoofData { Indices = new int[6] }));
            Assert.True(LotFacadeGenerator.HasRoofGeometry(
                new RoofData { Vertices = new TerrainParallaxVertex[4], Indices = new int[6] }));

            // and the same rule over what GenerateRoof leaves on the generator, which is how the
            // builder decides a lot has walls and a floor and no roof
            Assert.False(LotFacadeGenerator.HasRoofGeometry(0, 0));
            Assert.False(LotFacadeGenerator.HasRoofGeometry(4, 0));
            Assert.False(LotFacadeGenerator.HasRoofGeometry(0, 6));
            Assert.True(LotFacadeGenerator.HasRoofGeometry(4, 6));
        }

        [Fact]
        public void TheCompletedPassesAreReportedSeparatelyFromTheRequiredOnes()
        {
            // Walls and floor but no roof is a house, and has to be describable as one: which
            // passes produced geometry is a different question from which passes could run.
            Assert.Equal(NeighbourhoodFacadeStages.Walls | NeighbourhoodFacadeStages.Floor,
                NeighbourhoodFacadeDiagnostics.CompletedStages(true, true, false));
            Assert.Equal(NeighbourhoodFacadeStages.None,
                NeighbourhoodFacadeDiagnostics.CompletedStages(false, false, false));
            Assert.Equal(NeighbourhoodFacadeStages.Walls | NeighbourhoodFacadeStages.Floor
                | NeighbourhoodFacadeStages.Roof,
                NeighbourhoodFacadeDiagnostics.CompletedStages(true, true, true));

            // Has is a set test, and None is the absence of a question rather than a member of it
            Assert.False(NeighbourhoodFacadeDiagnostics.Has(
                NeighbourhoodFacadeStages.None, NeighbourhoodFacadeStages.None));
            Assert.True(NeighbourhoodFacadeDiagnostics.Has(
                NeighbourhoodFacadeStages.Walls | NeighbourhoodFacadeStages.Roof,
                NeighbourhoodFacadeStages.Roof));
            Assert.False(NeighbourhoodFacadeDiagnostics.Has(
                NeighbourhoodFacadeStages.Walls | NeighbourhoodFacadeStages.Roof,
                NeighbourhoodFacadeStages.Floor));
        }

        [Fact]
        public void EachGeneratorPassIsNamedOnItsOwnRatherThanAsOneStage()
        {
            // "while rendering the lot's walls, floor and roof" was the stage name for all three
            // calls into the lot view, so it identified none of them. Each pass sets its own, and
            // this is the sentence the next log line is made of.
            var sentences = new[]
            {
                NeighbourhoodFacadeDiagnostics.StageFailure(64,
                    "rendering the lot's walls", new NullReferenceException()),
                NeighbourhoodFacadeDiagnostics.StageFailure(64,
                    "rendering the lot's floor", new NullReferenceException()),
                NeighbourhoodFacadeDiagnostics.StageFailure(64,
                    "rendering the lot's roof", new NullReferenceException())
            };
            foreach (var sentence in sentences)
            {
                Assert.Contains("for lot 64", sentence);
                Assert.DoesNotContain("walls, floor and roof", sentence);
            }
            Assert.NotEqual(sentences[0], sentences[1]);
            Assert.NotEqual(sentences[1], sentences[2]);
        }

        [Fact]
        public void ALotWithNothingAtAllToDrawSaysWhichReferenceWasMissing()
        {
            // "all came back empty" is true of a lot whose roof pass had no storey to mesh and of a
            // lot whose render targets never uploaded, and only one of those is a content problem.
            // The absent list is what tells them apart, so it has to name the reference and the
            // call that produced it rather than the stage the whole pipeline was in.
            var absent = new List<NeighbourhoodFacadeRequirement>
            {
                new NeighbourhoodFacadeRequirement(NeighbourhoodFacadePiece.RoofGeometry, false, null)
            };
            var one = NeighbourhoodFacadeDiagnostics.Absent(absent, 64);
            Assert.Contains("64", one);
            Assert.Contains(NeighbourhoodFacadeDiagnostics.Describe(
                NeighbourhoodFacadePiece.RoofGeometry), one);
            Assert.Contains("MeshRectData", one);

            // two of them read as a list rather than as one sentence twice
            absent.Add(new NeighbourhoodFacadeRequirement(
                NeighbourhoodFacadePiece.TerrainComponent, false, null));
            var both = NeighbourhoodFacadeDiagnostics.Absent(absent, 64);
            Assert.Contains("MeshRectData", both);
            Assert.Contains("terrain component", both);

            // and nothing missing adds no clause at all
            Assert.Null(NeighbourhoodFacadeDiagnostics.Absent(null, 64));
            Assert.Null(NeighbourhoodFacadeDiagnostics.Absent(
                new List<NeighbourhoodFacadeRequirement>(), 64));
        }
    }
}
