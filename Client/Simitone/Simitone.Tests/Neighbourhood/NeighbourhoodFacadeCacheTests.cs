/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The facade pass's own bookkeeping: how much of a neighbourhood is owed a mesh, how much of a
    /// frame may be spent building one, and what a progress line says.
    ///
    /// <para>
    /// None of this needs a graphics device, which is the point. The player-visible symptom of
    /// this area's bugs was a log line reading <c>0 lot facades</c> and a neighbourhood with no
    /// houses, and the two facts needed to act on it - how many facades were owed, and how many
    /// had failed - were both computed and then thrown away. So the numbers that decide what the
    /// log says are pinned here rather than being left to be observed on a screen.
    /// </para>
    /// </summary>
    public class NeighbourhoodFacadeCacheTests
    {
        private static NeighbourhoodLot Lot(short number, int worldX, int worldY, uint hash = 0xABCD, bool hasHouse = true, int size = 32)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
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
            return new NeighbourhoodLayout(1, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion, 512, lots);
        }

        /// <summary>A layout of <paramref name="count"/> houses on a grid, all with distinct hashes.</summary>
        private static NeighbourhoodLayout Grid(int count)
        {
            var lots = new List<NeighbourhoodLot>(count);
            for (int i = 0; i < count; i++)
            {
                lots.Add(Lot((short)(10 + i), (i % 8) * 64, (i / 8) * 64, (uint)(0x1000 + i)));
            }
            return LayoutOf(lots.ToArray());
        }

        // ---- how much of a neighbourhood is owed a mesh ----------------------------------------

        [Fact]
        public void EveryHouseInTheLayoutIsEligible()
        {
            var layout = Grid(41);

            Assert.Equal(41, NeighbourhoodFacadePlan.Eligible(layout, null));
        }

        [Fact]
        public void AnEmptyPlotIsNotEligible()
        {
            // The plan's "lots with no house draw nothing, or a bare plot": a plot has nothing to
            // stand a World and a VM up for, so it is not in the denominator either.
            var layout = LayoutOf(Lot(11, 0, 0, 0xAAAA), Lot(12, 64, 0, 0xBBBB, false));

            Assert.Equal(1, NeighbourhoodFacadePlan.Eligible(layout, null));
        }

        [Fact]
        public void TheLotThePlayerIsStandingInIsNotEligible()
        {
            // The lot view already draws that house in full, so a facade of it would be a second
            // z-fighting copy of the player's own home.
            var layout = Grid(41);

            Assert.Equal(40, NeighbourhoodFacadePlan.Eligible(layout, 11));
        }

        [Fact]
        public void ANullLayoutHasNothingEligible()
        {
            Assert.Equal(0, NeighbourhoodFacadePlan.Eligible(null, null));
        }

        [Fact]
        public void ProgressReportsWhatIsOwedAndWhatIsEverPossible()
        {
            // "0 of 38 built" and "0 of 38 owed" are the two halves of a diagnosable line. Reported
            // together, a ratio of zero over a non-zero denominator is "not started", and a
            // denominator of zero is "nothing to build" - two states that a bare count cannot tell
            // apart and that need completely different responses.
            var layout = Grid(38);

            int outstanding, eligible;
            NeighbourhoodFacadePlan.Progress(layout, new Dictionary<short, uint>(), null,
                Vector2.Zero, out outstanding, out eligible);

            Assert.Equal(38, outstanding);
            Assert.Equal(38, eligible);
        }

        [Fact]
        public void ProgressFallsAsLotsAreCached()
        {
            var layout = Grid(3);
            var cache = new Dictionary<short, uint> { { 10, 0x1000 }, { 11, 0x1001 } };

            int outstanding, eligible;
            NeighbourhoodFacadePlan.Progress(layout, cache, null, Vector2.Zero, out outstanding, out eligible);

            Assert.Equal(1, outstanding);
            Assert.Equal(3, eligible);
        }

        [Fact]
        public void AFailedLotStopsCountingAsOwed()
        {
            // A lot that could not be built is recorded against its hash, so the queue drains even
            // when nothing was built. Without that, a neighbourhood of unreadable house files would
            // report itself as perpetually mid-build and never say so.
            var layout = Grid(2);
            var cache = new Dictionary<short, uint> { { 10, 0x1000 }, { 11, 0x1001 } };

            int outstanding, eligible;
            NeighbourhoodFacadePlan.Progress(layout, cache, null, Vector2.Zero, out outstanding, out eligible);

            Assert.Equal(0, outstanding);
            Assert.Equal(2, eligible);
        }

        // ---- how much of a frame may be spent building ----------------------------------------

        [Fact]
        public void AFrameMayBuildUpToItsCountBudget()
        {
            Assert.Equal(2, NeighbourhoodFacadePlan.FrameAllowance(2, 0d, 0, 0d));
            Assert.Equal(1, NeighbourhoodFacadePlan.FrameAllowance(2, 0d, 1, 0d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(2, 0d, 2, 0d));
        }

        [Fact]
        public void AFrameStopsWhenItHasSpentItsTimeBudget()
        {
            // A lot's build cost is the lot's, not ours: a plain house is quick and a fully
            // furnished one is not. The count budget alone would let two expensive lots make a
            // frame two hundred milliseconds long on a slow machine.
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(4, 10d, 1, 12d));
            Assert.Equal(3, NeighbourhoodFacadePlan.FrameAllowance(4, 10d, 1, 9d));
        }

        [Fact]
        public void AFrameAlwaysGetsAtLeastOneBuild()
        {
            // The one that matters. A frame that begins over budget - because the frame before it
            // overran, because the clock jumped, because a lot took longer than the whole budget -
            // must still be allowed one build, or the queue never advances again and the symptom
            // is identical to a queue that is broken: houses that never appear and no error.
            Assert.True(NeighbourhoodFacadePlan.FrameAllowance(1, 10d, 0, 5000d) >= 1);
            Assert.True(NeighbourhoodFacadePlan.FrameAllowance(4, 10d, 0, 100000d) >= 1);
        }

        [Fact]
        public void ABuildIsCountedBeforeTheClockIsConsulted()
        {
            // In order: the count budget can exhaust the frame on its own, and the clock only
            // gets a say once something has actually been built this frame.
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(1, 1000d, 1, 0d));
            Assert.Equal(2, NeighbourhoodFacadePlan.FrameAllowance(2, 0d, 0, 0d));
        }

        [Fact]
        public void ANonPositiveCountBudgetBuildsNothing()
        {
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(0, 10d, 0, 0d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(-1, 10d, 0, 0d));
        }

        [Fact]
        public void ANegativeTimeSpentIsTreatedAsNone()
        {
            // A clock that has not started reads as zero, not as a large negative; either way the
            // frame must be allowed to work.
            Assert.Equal(2, NeighbourhoodFacadePlan.FrameAllowance(2, 10d, 0, -1d));
        }

        // ---- the progress line ----------------------------------------------------------------

        [Fact]
        public void AProgressLineSaysWhatIsBuiltAndWhatIsLeft()
        {
            var line = NeighbourhoodFacades.FacadeSummary(5, 1, 32, 38);

            Assert.Contains("5 of 38", line);
            Assert.Contains("1 failed", line);
            Assert.Contains("32 still to build", line);
        }

        [Fact]
        public void AFinishedNeighbourhoodDoesNotClaimAnythingIsStillBuilding()
        {
            // "0 still to build" on a drained queue is noise, and on a line whose job is to be read
            // once it is the kind of noise that makes people stop reading the rest of it.
            var line = NeighbourhoodFacades.FacadeSummary(38, 0, 0, 38);

            Assert.DoesNotContain("still to build", line);
            Assert.Contains("38 of 38 built", line);
        }

        [Fact]
        public void ANeighbourhoodWithNothingToBuildSaysSo()
        {
            // The state that reads most like "the facades are broken" and is not: every lot is an
            // empty plot, so there was never anything to build.
            var line = NeighbourhoodFacades.FacadeSummary(0, 0, 0, 0);

            Assert.Contains("0 of 0", line);
            Assert.DoesNotContain("still to build", line);
        }

        [Fact]
        public void AFailedNeighbourhoodIsDistinguishableFromAnUnstartedOne()
        {
            // The pair the original log could not tell apart. Same built count, same denominator,
            // and a completely different problem: one is a lot of unreadable house files, the other
            // is a pass that never ran.
            var unstarted = NeighbourhoodFacades.FacadeSummary(0, 0, 38, 38);
            var failed = NeighbourhoodFacades.FacadeSummary(0, 38, 0, 38);

            Assert.Contains("38 still to build", unstarted);
            Assert.DoesNotContain("still to build", failed);
            Assert.Contains("38 failed", failed);
        }

        // ---- the cache key --------------------------------------------------------------------

        [Fact]
        public void TheCacheKeyIsTheHouseFileAndNotTheLotNumber()
        {
            // Two lots, same house file fingerprint, different numbers: each is its own cache entry
            // keyed by its own number, and neither is a rebuild of the other. A key that was the
            // lot number alone would re-mesh a neighbourhood whenever the file changed, and a key
            // that was the file alone would give every lot the same mesh.
            var layout = LayoutOf(Lot(11, 0, 0, 0xAAAA), Lot(12, 64, 0, 0xAAAA));
            var cache = new Dictionary<short, uint>();

            var wanted = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 1);
            Assert.Single(wanted);
            Assert.Equal(11, wanted[0].Number);

            cache[11] = 0xAAAA;
            var second = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 1);
            Assert.Equal(12, second[0].Number);
        }

        [Fact]
        public void AHashOfZeroMeansNoHouseAndIsNeverQueued()
        {
            // Zero is what a bulldozed lot reports, and ShouldDraw already excludes it, so the two
            // rules cannot disagree. Asserted anyway, because the two agreeing is the property and
            // one of them changing would otherwise be invisible.
            var bulldozed = Lot(11, 0, 0, 0u, hasHouse: false);

            Assert.False(NeighbourhoodFacadePlan.ShouldDraw(bulldozed, null));
            Assert.Empty(NeighbourhoodFacadePlan.ToBuild(LayoutOf(bulldozed),
                new Dictionary<short, uint>(), null, Vector2.Zero, 10));
        }

        [Fact]
        public void AHashThatMovesInvalidatesTheEntryAndTheMesh()
        {
            // Both halves of the cache's key policy, on one lot. The entry is stale (so the mesh is
            // dropped and rebuilt) and the lot is queued again in the same breath.
            var layout = LayoutOf(Lot(11, 0, 0, 0x0EE0));
            var cache = new Dictionary<short, uint> { { 11, 0x010D } };

            Assert.Equal(new short[] { 11 }, NeighbourhoodFacadePlan.Stale(layout, cache));
            Assert.Single(NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 10));
        }

        // ---- the order the queue drains in -----------------------------------------------------

        [Fact]
        public void TheQueueDrainsNearestTheCameraFirstAndDrainsCompletely()
        {
            // Not just "nearest first": with a budget of one, repeatedly taking the head is the
            // only thing that makes a 41-lot neighbourhood finish at all. A budget that always
            // re-sorted around a moving camera could starve the far lots indefinitely.
            var layout = Grid(41);
            var cache = new Dictionary<short, uint>();
            var focus = NeighbourhoodWorldMapping.PackedCentre(layout.FindLot(10));

            var order = new List<short>();
            for (int frame = 0; frame < 100; frame++)
            {
                var wanted = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, focus, 2);
                if (wanted.Count == 0) break;
                foreach (var lot in wanted)
                {
                    order.Add(lot.Number);
                    cache[lot.Number] = lot.HouseHash;
                }
            }

            Assert.Equal(41, order.Count);
            //every lot exactly once: a lot built twice means the cache key is not being consulted
            Assert.Equal(41, new HashSet<short>(order).Count);
            Assert.Equal(10, order[0]);
        }

        [Fact]
        public void TheOrderIsStableAcrossFramesWithAFixedCamera()
        {
            // The tie-break is what makes this true, and its absence shows up as a different
            // neighbourhood every frame rather than a fill-in.
            var layout = Grid(8);
            var cache = new Dictionary<short, uint>();

            var first = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, new Vector2(32, 32), 3);
            var second = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, new Vector2(32, 32), 3);

            Assert.Equal(first.Count, second.Count);
            for (int i = 0; i < first.Count; i++)
            {
                Assert.Equal(first[i].Number, second[i].Number);
            }
        }

        [Fact]
        public void TheQueueIsEmptyOnceEverythingHasBeenTried()
        {
            var layout = Grid(41);
            var cache = new Dictionary<short, uint>();
            for (int i = 0; i < layout.LotCount; i++)
            {
                cache[layout.Lots[i].Number] = layout.Lots[i].HouseHash;
            }

            int outstanding, eligible;
            NeighbourhoodFacadePlan.Progress(layout, cache, null, Vector2.Zero, out outstanding, out eligible);

            Assert.Equal(0, outstanding);
            Assert.Equal(41, eligible);
        }

        // ---- placement ------------------------------------------------------------------------

        [Fact]
        public void AFacadeLandsInsideItsOwnLotRectangle()
        {
            // LotFacadeGenerator emits the lot's walls, roof and floor in lot-local tiles, 0..Size.
            // Placed, they have to be inside the packed rectangle the map is drawn from - and the
            // wall vertices, the roof vertices and the floor vertices are three separate lists that
            // can each be wrong in a different way, so all three are checked.
            var lot = Lot(11, 200, 300, 0xAAAA);
            var local = new[]
            {
                new VertexPositionTexture(new Vector3(0, 0, 0), new Vector2(0, 0)),
                new VertexPositionTexture(new Vector3(lot.Size, 0, 0), new Vector2(1, 0)),
                new VertexPositionTexture(new Vector3(0, 0, lot.Size), new Vector2(0, 1)),
                new VertexPositionTexture(new Vector3(lot.Size, 0, lot.Size), new Vector2(1, 1))
            };

            var placed = NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 4f, 0f);

            Assert.Equal(local.Length, placed.Length);
            Assert.Equal(new Vector3(200, 4, 300), placed[0].Position);
            Assert.Equal(new Vector3(200 + lot.Size, 4, 300), placed[1].Position);
            Assert.Equal(new Vector3(200, 4, 300 + lot.Size), placed[2].Position);
            Assert.Equal(new Vector3(200 + lot.Size, 4, 300 + lot.Size), placed[3].Position);
        }

        [Fact]
        public void PlacementKeepsTheGeneratorsTextureCoordinates()
        {
            // The facade's textures are render targets with a hand-built atlas layout, so a
            // coordinate that got transformed along with the position would put the wrong part of
            // the wall on the wrong part of the house.
            var lot = Lot(11, 200, 300);
            var local = new[]
            {
                new VertexPositionTexture(new Vector3(5, 1, 6), new Vector2(0.25f, 0.75f))
            };

            var placed = NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 4f, 0f);

            Assert.Equal(new Vector2(0.25f, 0.75f), placed[0].TextureCoordinate);
            Assert.Equal(new Vector3(205, 5, 306), placed[0].Position);
        }

        [Fact]
        public void PlacementLeavesTheGeneratorsOwnVertexListAlone()
        {
            // The failure path reports a reason from the generator's lists, and a caller holding
            // one across the placement must not find it rewritten under them.
            var lot = Lot(11, 200, 300);
            var local = new[]
            {
                new VertexPositionTexture(new Vector3(1, 2, 3), Vector2.Zero)
            };

            NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 4f, 0f);

            Assert.Equal(new Vector3(1, 2, 3), local[0].Position);
        }

        [Fact]
        public void PlacingNothingGivesNothing()
        {
            Assert.Null(NeighbourhoodFacadeTransform.PlacedVertices(Lot(11, 0, 0), null, 0f, 0f));
        }

        [Fact]
        public void AGroundOffsetShiftsEverySurfaceOfTheFacadeTogether()
        {
            // Walls, roof and floor all rise by the same amount, or the roof comes off the house.
            var lot = Lot(11, 200, 300);
            var local = new[]
            {
                new VertexPositionTexture(new Vector3(0, 0, 0), Vector2.Zero)
            };

            var flat = NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 4f, 0f);
            var lifted = NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 4f, 1.5f);

            Assert.Equal(flat[0].Position.Y + 1.5f, lifted[0].Position.Y, 4);
            Assert.Equal(flat[0].Position.X, lifted[0].Position.X, 4);
        }

        [Fact]
        public void AFlatLotsGroundNeedsNoCorrection()
        {
            // The common case, and the one that must not move: a house nudged a tenth of a tile off
            // its own ground is visible as a floating sill on every lot in the neighbourhood.
            Assert.Equal(0f, NeighbourhoodFacadeTransform.GroundOffsetFor(0f), 4);
        }

        [Fact]
        public void ASlopedLotIsLiftedOrDroppedOntoTheGeneratedGround()
        {
            // A lot whose own ARRY(0) sits below its zero plane has a facade whose base is below
            // the generated ground, and the correction is upward.
            Assert.Equal(1.5f, NeighbourhoodFacadeTransform.GroundOffsetFor(-1.5f), 4);
            Assert.Equal(-2f, NeighbourhoodFacadeTransform.GroundOffsetFor(2f), 4);
        }

        [Fact]
        public void AnAbsurdAltitudeIsClampedRatherThanObeyed()
        {
            // One corrupt ARRY(0) plane must not be able to fling a house off the map, which is
            // strictly worse than a house that sits a tile out.
            var cap = NeighbourhoodFacadeTransform.MaxGroundOffset;

            Assert.Equal(-cap, NeighbourhoodFacadeTransform.GroundOffsetFor(1000f), 4);
            Assert.Equal(cap, NeighbourhoodFacadeTransform.GroundOffsetFor(-1000f), 4);
            //NaN is not a huge altitude, it is a missing one, and the two need different answers:
            //a missing altitude means a flat lot, a huge one means a clamped lift
            Assert.Equal(0f, NeighbourhoodFacadeTransform.GroundOffsetFor(float.NaN), 4);
            Assert.Equal(0f, NeighbourhoodFacadeTransform.GroundOffsetFor(float.PositiveInfinity), 4);
            Assert.Equal(0f, NeighbourhoodFacadeTransform.GroundOffsetFor(float.NegativeInfinity), 4);
        }

        [Fact]
        public void AClampedOffsetStillBoundsHowFarTheHouseMoves()
        {
            // The clamp is on the correction, not on the house. A lot claiming a thousand tiles of
            // relief gets the maximum correction and no more, so its base ends up exactly
            // MaxGroundOffset below the generated ground - a visible fault, not a house on the far
            // side of the map. Asserted through the placement rather than the number alone,
            // because a test that only checked the returned float would pass even if the placement
            // ignored it.
            var lot = Lot(11, 200, 300);
            var local = new[] { new VertexPositionTexture(new Vector3(8, 0, 8), Vector2.Zero) };

            var placed = NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 4f,
                NeighbourhoodFacadeTransform.GroundOffsetFor(1000f));

            Assert.Equal(4f - NeighbourhoodFacadeTransform.MaxGroundOffset, placed[0].Position.Y, 3);
        }

        [Fact]
        public void AFlatLotStandsExactlyOnTheGeneratedGround()
        {
            // The other end of the same property, and the one that matters for every stock TS1
            // house: no correction at all, so a wall's base vertex is exactly the map's height.
            var lot = Lot(11, 200, 300);
            var local = new[] { new VertexPositionTexture(new Vector3(8, 0, 8), Vector2.Zero) };

            var placed = NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 7.25f,
                NeighbourhoodFacadeTransform.GroundOffsetFor(0f));

            Assert.Equal(7.25f, placed[0].Position.Y, 3);
        }

        // ---- the world-tile offset, at the edges --------------------------------------------

        [Fact]
        public void ALotInTheLastColumnOfTheMapPlacesEveryVertexInsideTheMap()
        {
            // The packer puts the last lot against the map's far edge, and every vertex of a facade
            // is a world tile. The arithmetic is a sum of the lot's origin and a coordinate inside
            // the lot, so the far edge is where an off-by-one would show up as a house hanging off
            // the map - or, if it were ever used to index anything, as an out-of-range read. It
            // does not, and this is why: placement is arithmetic, and the only array it ever
            // touched was the stopwatch's (see NeighbourhoodFacadeTimings.SlotFor).
            var map = NeighbourhoodMap.Create(64);
            var size = 32;
            var lot = Lot(11, map.Width - size, map.Height - size, 0xABCD, size: size);
            var local = new[]
            {
                new VertexPositionTexture(Vector3.Zero, Vector2.Zero),
                new VertexPositionTexture(new Vector3(size, 0, 0), Vector2.Zero),
                new VertexPositionTexture(new Vector3(0, 0, size), Vector2.Zero),
                new VertexPositionTexture(new Vector3(size, 0, size), Vector2.Zero)
            };

            var placed = NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 3f, 0f);

            Assert.Equal(new Vector3(map.Width - size, 3f, map.Height - size), placed[0].Position);
            Assert.Equal(new Vector3(map.Width, 3f, map.Height), placed[3].Position);
            for (int i = 0; i < placed.Length; i++)
            {
                Assert.InRange(placed[i].Position.X, 0f, map.Width);
                Assert.InRange(placed[i].Position.Z, 0f, map.Height);
            }
        }

        [Fact]
        public void AVertexOutsideItsOwnLotIsPlacedRatherThanClamped()
        {
            // The generator's own coordinates are not guaranteed to be inside 0..Size: a roof
            // rectangle on the outside of an outside wall, a terrain tile the lot view subdivided
            // past its own width. Placement moves a vertex, it does not judge it, and a clamp here
            // would quietly pull an overhanging eave back onto the plot - which is a different
            // house from the one the file describes.
            var lot = Lot(11, 200, 300, 0xABCD, size: 32);
            var local = new[]
            {
                new VertexPositionTexture(new Vector3(-4, 0, -4), Vector2.Zero),
                new VertexPositionTexture(new Vector3(40, 3, 40), Vector2.Zero)
            };

            var placed = NeighbourhoodFacadeTransform.PlacedVertices(lot, local, 1f, 0f);

            Assert.Equal(new Vector3(196, 1f, 296), placed[0].Position);
            Assert.Equal(new Vector3(240, 4f, 340), placed[1].Position);
        }

        [Fact]
        public void RotatedPlacementsStayInsideTheLotTheyStartedIn()
        {
            // A quarter turn about the lot's centre maps the lot's own square onto itself, so a
            // rotated house is still inside its own plot - which is the property that lets the
            // roads and the pavements around it stay where the packer put them. Asserted on the
            // corners, because a rotation that is right in the middle and wrong at the corners is a
            // rotation that is wrong.
            const int size = 32;
            const float centre = size / 2f;
            var corners = new[]
            {
                new Vector2(0f, 0f), new Vector2(size, 0f),
                new Vector2(0f, size), new Vector2(size, size)
            };
            foreach (int rotation in new[] { 0, 1, 2, 3, 4, -1, 9 })
            {
                foreach (var corner in corners)
                {
                    NeighbourhoodFacadeTransform.Rotate(
                        corner.X, corner.Y, centre, rotation, out var x, out var z);
                    Assert.InRange(x, 0f, size);
                    Assert.InRange(z, 0f, size);
                }
            }
        }

        // ---- texture ownership ----------------------------------------------------------------

        [Fact]
        public void ASurfaceOnlyDisposesATextureItOwns()
        {
            // The roof texture is the content system's, one instance shared by every lot with that
            // roof. A surface that freed it would take the lot view's roofs down as well as the
            // neighbourhood's, from a cache that is supposed to be a private thing.
            var shared = new NeighbourhoodFacadeSurface { OwnsTexture = false };
            shared.Dispose();

            Assert.Null(shared.Texture);
            Assert.False(shared.OwnsTexture);
        }

        [Fact]
        public void ADisposableSurfaceIsEmptyAfterItIsDisposed()
        {
            // Draw walks the cache every frame; a surface that reported geometry after its buffers
            // were freed would have the draw loop issuing calls against disposed resources. The
            // buffers cannot be made here - that needs a device - so the arrays are what is
            // asserted, and the rule they feed is asserted directly below.
            var surface = new NeighbourhoodFacadeSurface
            {
                Vertices = new[] { new VertexPositionTexture(Vector3.Zero, Vector2.Zero) },
                Indices = new[] { 0, 0, 0 },
                OwnsTexture = true
            };

            Assert.Equal(1, surface.PrimitiveCount);
            surface.Dispose();

            Assert.Equal(0, surface.PrimitiveCount);
            Assert.Null(surface.Vertices);
            Assert.Null(surface.Indices);
            Assert.False(surface.IsDrawable);
        }

        [Fact]
        public void DrawableGeometryIsNotDrawableWithoutATexture()
        {
            // Not "empty": it has a mesh and a triangle count, so the old IsEmpty test would have
            // let it through. Drawing it would bind nothing to ObjTex and sample whatever the last
            // bind left on the sampler, which on some drivers is the frame that is already there.
            Assert.False(NeighbourhoodFacadeSurface.Drawable(2, 4, true, hasTexture: false));
            Assert.True(NeighbourhoodFacadeSurface.Drawable(2, 4, true, hasTexture: true));
        }

        [Fact]
        public void NothingIsDrawableWithoutItsBuffers()
        {
            // The half of the rule that only a GPU can satisfy, pinned as arithmetic so it is not
            // left entirely untested: a surface whose upload failed has to be skipped, not drawn
            // against a buffer that is not there.
            Assert.False(NeighbourhoodFacadeSurface.Drawable(2, 4, false, true));
            Assert.False(NeighbourhoodFacadeSurface.Drawable(0, 4, true, true));
            Assert.False(NeighbourhoodFacadeSurface.Drawable(2, 0, true, true));
        }

        [Fact]
        public void ACompletelyEmptySurfaceIsNotDrawable()
        {
            var surface = new NeighbourhoodFacadeSurface();

            Assert.True(surface.IsEmpty);
            Assert.False(surface.IsDrawable);
        }
    }
}
