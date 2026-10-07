/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FSO.LotView.Facade;
using FSO.LotView.Model;
using FSO.LotView.RC;
using Microsoft.Xna.Framework;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Where the wall pass's "wall geometry" second actually went, and the three things that were
    /// supposed to be it.
    ///
    /// <para><b>The measurement.</b> The wall pass reported 1.03s in a phase named "wall geometry"
    /// for a twenty-wall, one-storey house, and 4.38s for the worst lot in the neighbourhood.
    /// <c>WallComponentRC.Generate</c> is what that phase was naming, and it is four things: free
    /// the last build's device buffers, sweep the lot's tiles, turn wall lines into vertices, and
    /// hand each textured group to the device. Measured headlessly on a 64x64 five-storey lot with
    /// a one-storey house on it, the tile sweep - the whole <c>Floors.Length</c> by
    /// <c>Height</c> by <c>Height</c> walk, twenty thousand and eighty tile reads - is
    /// <b>44 microseconds</b>, and the sparse walk that replaced it is <b>2.5 microseconds</b>.
    /// That is four ten-thousandths of the reported second. So the sweep was worth doing and it was
    /// never the cost.</para>
    ///
    /// <para><b>What was.</b> <c>WallObjectBatch.Gather</c>, which sat between the same two
    /// marks: two hundred and forty-three calls to <c>ObjectComponent.GetBounds</c>, and on a lot
    /// view that has not asked before, the first one for each object builds that object's 3d mesh
    /// from its sprites. That is <see cref="WallPassPhase.ObjectGather"/>, it is charged to its
    /// own phase, and it is bounded.</para>
    ///
    /// <para><b>What is pinned here.</b> The storey limit, the sparse walk being the same set as
    /// the dense one, the gather's budget and its order, and the lines that say all of it.</para>
    /// </summary>
    public class LotFacadeWallGeometryTests
    {
        // ---- the storey limit -------------------------------------------------------------------

        [Fact]
        public void AOneStoreyTs1HouseIsOneStoreyNotTheFiveItDeclares()
        {
            // Blueprint.Stories is five for every lot the TS1 activator builds, because nothing in
            // a house file rewrites it. Everything downstream that needed a storey count was being
            // told five: the walk of the lot's tiles, the level the facade pass silently held the
            // lot view at, and the level it asked the wall component for by name.
            var bp = House(occupiedStoreys: 1);

            Assert.Equal(5, (int)bp.Stories);
            Assert.Equal(1, bp.GetWallStoreys());
        }

        [Fact]
        public void TheStoreyCountIsTheHighestStoreyWithWallOnIt()
        {
            // Not the number of storeys with walls - a five-storey house with a room on three and
            // four is three, because the geometry's dictionary is indexed by storey and leaving a
            // hole in it would move every storey above the hole.
            Assert.Equal(3, House(occupiedStoreys: 3).GetWallStoreys());
            Assert.Equal(5, House(occupiedStoreys: 5).GetWallStoreys());
        }

        [Fact]
        public void ALotWhoseWallListWasNeverBuiltIsWalkedEntirelyRatherThanNotAtAll()
        {
            // "No list" and "an empty list" are the same answer, and the answer is "cannot say".
            // A caller that read an all-empty list as "this lot has no walls" would drop every
            // wall off any blueprint whose list was never built - a different path through the
            // loader, behind the netplay restore, rather than a hypothetical. A limit that walks
            // too much costs a tile sweep; one that walks too little costs a wall.
            var bp = new Blueprint(8, 8);

            Assert.False(bp.HasWallList());
            Assert.False(bp.StoreyHasWalls(1));
            Assert.Equal(bp.Walls.Length, bp.GetWallStoreys());
        }

        [Fact]
        public void AStoreyTheBlueprintCannotSpeakForIsWalkedAsAGrid()
        {
            // The fallback at the level of one storey, which is the other half of the same rule.
            var bp = new Blueprint(8, 8);
            bp.WallsAt = null;

            Assert.False(bp.StoreyHasWalls(1));
            Assert.Equal(bp.Walls.Length, bp.GetWallStoreys());
        }

        [Fact]
        public void TheStoreyCountCannotExceedTheStoreysTheWallArraysHave()
        {
            // An unbuilt WallsAt on a five-storey lot is five empty lists, and reading past
            // Stories is how a caller ends up asking for a storey with no array behind it.
            var bp = new Blueprint(8, 8);
            var storeys = bp.Walls.Length;
            bp.WallsAt = new List<int>[storeys + 3];
            for (int i = 0; i < bp.WallsAt.Length; i++) bp.WallsAt[i] = new List<int>();
            for (int i = 0; i < storeys; i++) bp.WallsAt[i].Add(0);

            Assert.Equal(storeys, bp.GetWallStoreys());
        }

        // ---- the sparse walk ---------------------------------------------------------------------

        [Fact]
        public void TheSparseWalkVisitsExactlyTheTilesTheDenseWalkWouldHaveActedOn()
        {
            // The whole safety argument for WallsAt in one assertion. The dense walk tested
            // `Segments != 0` on every tile of the storey; the sparse walk visits the offsets in
            // WallsAt and tests the same thing. For a storey the blueprint describes correctly the
            // two visit the same tiles and find the same walls, so the walk can be the only change
            // and cannot be the reason a wall went missing.
            var bp = House(occupiedStoreys: 1);
            var rc = new WallComponentRC { blueprint = bp };

            var dense = new List<int>();
            for (int off = 0; off < bp.Walls[0].Length; off++)
            {
                if (bp.Walls[0][off].Segments != 0) dense.Add(off);
            }

            var sparse = new List<int>();
            var tiles = rc.TilesAt(1);
            for (int v = 0; v < tiles.Count; v++)
            {
                var off = tiles.OffsetAt(v);
                Assert.True(off >= 0);
                if (tiles.Walls[off].Segments != 0) sparse.Add(off);
            }

            //and the walk is 68 times shorter for the same answer, which is the reason for it
            Assert.Equal(60, tiles.Count);
            Assert.True(tiles.Count < bp.Walls[0].Length / 10,
                "a house's walls are a small fraction of a 64x64 lot's tiles");
            Assert.Equal(dense, sparse);
            Assert.NotEmpty(dense);
        }

        [Fact]
        public void AStoreyWithNoWallListIsWalkedAsAGrid()
        {
            // The fallback, and it is the one that makes the sparse path safe to have at all: a
            // blueprint loaded by a path that never built WallsAt gets the old walk rather than an
            // empty one. Both shapes of "cannot say" - a null list and an all-empty one.
            var rc = new WallComponentRC { blueprint = House(occupiedStoreys: 1) };
            rc.blueprint.WallsAt = null;

            var noList = rc.TilesAt(1);
            Assert.Null(noList.Sparse);
            Assert.Equal(rc.blueprint.Width * rc.blueprint.Height, noList.Count);
            Assert.Equal(0, noList.OffsetAt(0));
            Assert.Equal(rc.blueprint.Width * rc.blueprint.Height - 1,
                noList.OffsetAt(noList.Count - 1));

            var empty = new WallComponentRC { blueprint = House(occupiedStoreys: 1) };
            empty.blueprint.Walls = new WallTile[empty.blueprint.Stories][];
            empty.blueprint.Walls[0] = new WallTile[64 * 64];
            empty.blueprint.WallsAt = new List<int>[empty.blueprint.Stories];
            for (int i = 0; i < empty.blueprint.WallsAt.Length; i++)
            {
                empty.blueprint.WallsAt[i] = new List<int>();
            }
            var emptyList = empty.TilesAt(1);
            Assert.Null(emptyList.Sparse);
            Assert.Equal(64 * 64, emptyList.Count);
        }

        [Fact]
        public void AVisitOutsideTheStoreyHasNoOffsetRatherThanTheFirstTilesOffset()
        {
            // Both shapes. Returning zero would silently re-emit the corner tile's walls at every
            // extra visit, which is the sort of thing that only shows up as a house with a
            // duplicated corner.
            var bp = House(occupiedStoreys: 1);
            var rc = new WallComponentRC { blueprint = bp };

            var sparse = rc.TilesAt(1);
            Assert.Equal(-1, sparse.OffsetAt(-1));
            Assert.Equal(-1, sparse.OffsetAt(sparse.Count));
            Assert.Equal(-1, sparse.OffsetAt(int.MaxValue));

            var dense = new WallComponentRC.StoreyTiles(bp.Walls[0], bp.Floors[0], null,
                bp.Width, bp.Height);
            Assert.Equal(-1, dense.OffsetAt(-1));
            Assert.Equal(-1, dense.OffsetAt(dense.Count));
        }

        [Fact]
        public void AStaleOffsetInTheWallListIsSkippedRatherThanThrowing()
        {
            // A list built against a different array is a list that cannot be trusted with an
            // index. It must cost a visit, not an IndexOutOfRangeException - which the old walk
            // would also have avoided, because it never used a list at all.
            var bp = House(occupiedStoreys: 1);
            var walls = bp.Walls[0];
            var stale = new List<int> { 0, walls.Length + 10, -1, 5 };
            var tiles = new WallComponentRC.StoreyTiles(walls, bp.Floors[0], stale, bp.Width, bp.Height);

            Assert.Equal(4, tiles.Count);
            Assert.Equal(0, tiles.OffsetAt(0));
            Assert.Equal(-1, tiles.OffsetAt(1));
            Assert.Equal(-1, tiles.OffsetAt(2));
            Assert.Equal(5, tiles.OffsetAt(3));
        }

        [Fact]
        public void TheNeighbourOffsetsAWallTileReadsAreItsOwnOffsetPlusAndMinusOneAndTheStride()
        {
            // The rewrite turned x/y plus GetWall into an offset and neighbour arithmetic, and the
            // only way that is safe is if those two are the same number. This is that claim, on the
        // lot the wall pass actually walks.
            var bp = House(occupiedStoreys: 1);
            var walls = bp.Walls[0];
            foreach (var off in bp.WallsAt[0])
            {
                var x = off % bp.Width;
                var y = off / bp.Width;
                Assert.Equal(off, y * bp.Width + x);
                if (x > 0) Assert.Equal(bp.GetWall((short)(x - 1), (short)y, 1).Segments, walls[off - 1].Segments);
                if (x < bp.Width - 1) Assert.Equal(bp.GetWall((short)(x + 1), (short)y, 1).Segments, walls[off + 1].Segments);
                if (y > 0) Assert.Equal(bp.GetWall((short)x, (short)(y - 1), 1).Segments, walls[off - bp.Width].Segments);
                if (y < bp.Height - 1) Assert.Equal(bp.GetWall((short)x, (short)(y + 1), 1).Segments, walls[off + bp.Width].Segments);
            }
        }

        // ---- the object gather: the second that was the wall pass's "wall geometry" -------------

        [Fact]
        public void TheAtlasRegionContainsEveryWallLineItWasBuiltFrom()
        {
            // The gather orders itself by this box, so the only property it has to have is that it
            // is a superset: an object that could be in a cell has to be near a wall line. That is
            // asserted here, on the axes the claim is made about - the object ones, which is X and
            // Z for the lot's ground axes at three world units a tile and Y for height.
            var lines = new List<Vector2[]>
            {
                new[] { new Vector2(16 * 10, 16 * 10), new Vector2(16 * 20, 16 * 10) },
                new[] { new Vector2(16 * 10, 16 * 30), new Vector2(16 * 10, 16 * 40) },
            };
            var region = WallObjectBatch.RegionOf(lines, 1);

            Assert.True(WallObjectBatch.IsUsable(region));
            foreach (var line in lines)
            {
                foreach (var point in line)
                {
                    //sixteenths of a tile in, times three, which is the conversion the object bounds
                    //and the cell cameras both live in
                    var world = point * (3f / 16f);
                    Assert.True(region.Min.X <= world.X && world.X <= region.Max.X, "x " + world.X);
                    Assert.True(region.Min.Z <= world.Y && world.Y <= region.Max.Z, "z " + world.Y);
                }
            }
            //and the whole height of the house, so a tall object on any storey is inside it
            Assert.True(region.Min.Y < 0f);
            Assert.True(region.Max.Y > 2.95f * 3f);
        }

        [Fact]
        public void AHouseWithNoWallLinesHasNoAtlasRegion()
        {
            // No region means the gather cannot sort itself by one, which is the whole of what it
            // would have used it for - and it must be a region it can recognise as absent rather
            // than a box that sorts everything to the same distance.
            Assert.False(WallObjectBatch.IsUsable(WallObjectBatch.RegionOf(new List<Vector2[]>(), 1)));
            Assert.False(WallObjectBatch.IsUsable(WallObjectBatch.RegionOf(null, 1)));
            //a line with fewer than two points is not a line
            Assert.False(WallObjectBatch.IsUsable(WallObjectBatch.RegionOf(
                new List<Vector2[]> { new[] { new Vector2(0, 0) } }, 1)));
        }

        [Fact]
        public void DistanceToTheAtlasRegionIsZeroInsideItAndTheGapOutside()
        {
            // The gather's ordering key. Chebyshev because a cell camera closes on one axis
            // without moving in another, and because it needs no square root on a path taken a
            // hundred and fifty times a lot.
            var box = new BoundingBox(new Vector3(0f, 0f, 0f), new Vector3(10f, 10f, 10f));

            Assert.Equal(0f, WallObjectBatch.Chebyshev(new Vector3(5f, 5f, 5f), box));
            Assert.Equal(0f, WallObjectBatch.Chebyshev(new Vector3(0f, 0f, 0f), box));
            Assert.Equal(0f, WallObjectBatch.Chebyshev(new Vector3(10f, 10f, 10f), box));
            Assert.Equal(4f, WallObjectBatch.Chebyshev(new Vector3(-4f, 5f, 5f), box), 4);
            Assert.Equal(3f, WallObjectBatch.Chebyshev(new Vector3(5f, 13f, 5f), box), 4);
            //and the worst axis wins, which is what makes it "nearest" rather than "nearest in sum"
            Assert.Equal(6f, WallObjectBatch.Chebyshev(new Vector3(-1f, -2f, -6f), box), 4);
        }

        [Fact]
        public void ABatchWithEveryBoundIsOneThatWasNotTruncated()
        {
            // The default answer, so that a caller reading the counts on a healthy lot does not
            // have to know what a truncated gather is.
            var batch = WallObjectBatch.FromBounds(
                new[] { 1, 1, 1 },
                new[] { Box(0f), Box(1f), Box(2f) });

            Assert.Equal(3, batch.BoundsRead);
            Assert.Equal(0, batch.BoundsSkipped);
            Assert.False(batch.GatherTruncated);
        }

        [Fact]
        public void AnEmptyGatherIsNotATruncatedOne()
        {
            // A lot with no objects is the honest zero, and a gather that asked for nothing because
            // there was nothing must not read as a gather that gave up.
            var batch = WallObjectBatch.Gather(null);
            Assert.Equal(0, batch.BoundsRead);
            Assert.Equal(0, batch.BoundsSkipped);
            Assert.False(batch.GatherTruncated);
        }

        [Fact]
        public void ATruncatedGatherLeavesTheRestUnselectableRatherThanAtTheOrigin()
        {
            // What a skipped object actually is, and the reason it is not an empty box.
            //
            // default(BoundingBox) is the origin at zero extent, and a cell whose camera looks at
            // the origin intersects it - so an object left with one would be drawn into a cell it
            // has nothing to do with, furniture showing through a wall, which reads as a bug in
            // the facade rather than as a gather that gave up. It is given a storey no cell can
            // ask for instead, and the exclusion happens before any geometry is touched.
            var bounds = new BoundingBox[2];
            bounds[0] = Box(0f);
            bounds[1] = default(BoundingBox);
            var truncated = WallObjectBatch.FromBounds(
                new[] { 1, WallObjectBatch.NoObjectLevel }, bounds);
            truncated.Select(Camera(), 0);
            Assert.Equal(1, truncated.SelectedCount);
            //and no cell can ask low enough to reach it
            truncated.Select(Camera(), int.MinValue + 1);
            Assert.Equal(1, truncated.SelectedCount);

            bounds[1] = Box(1f);
            var full = WallObjectBatch.FromBounds(new[] { 1, 1 }, bounds);
            full.Select(Camera(), 0);
            Assert.Equal(2, full.SelectedCount);
        }

        /// <summary>
        /// A camera that can see a few tiles of the lot's ground plane, built the way the wall pass
        /// builds a cell's: an orthographic box looking along the lot's Z axis.
        /// </summary>
        private static BoundingFrustum Camera()
        {
            var look = Matrix.CreateLookAt(new Vector3(30f, -20f, 0f), Vector3.Zero, Vector3.Up);
            return new BoundingFrustum(look * Matrix.CreateOrthographic(60f, 60f, 0.1f, 100f));
        }

        [Fact]
        public void TheGathersBudgetDefaultsToSomethingATwoHundredAndFiftyMillisecondFrameCanAfford()
        {
            // Sixty of two hundred and fifty. The point is not sixty - it is that it is a number,
            // that it is a fraction rather than a multiple, and that a caller can say otherwise.
            Assert.True(WallObjectBatch.DefaultGatherBudgetMs > 0d);
            Assert.True(WallObjectBatch.DefaultGatherBudgetMs < 250d,
                "the gather's default budget has to fit inside the frame it is charged to");
        }

        // ---- the lines that say all of it --------------------------------------------------------

        [Fact]
        public void TheObjectGatherHasItsOwnPhaseAndIsNotCalledWallGeometry()
        {
            // The bug this whole change exists for. Two hundred and forty-three mesh builds were
            // reported under a stage name that sounds like a vertex loop, so "wall geometry 1.03s"
            // sent the next reader after the tile walk - which is forty-four microseconds. The
            // phases are separate now, and each says what it is.
            Assert.Equal("object gather", WallPassTimings.DescribePhase(WallPassPhase.ObjectGather));
            Assert.Equal("wall geometry", WallPassTimings.DescribePhase(WallPassPhase.Generate));
            //and it is charged, not just named: the gather is marked between the atlas allocation
            //and the wall geometry build
            var body = BodyOf(Stripped(Generator()), "GenerateWalls", "bool justTexture");
            Assert.Contains("batch = WallObjectBatch.Gather(bp.Objects, atlasRegion, GatherBudgetMs)", body);
            Assert.Contains("clock.Mark(WallPassPhase.ObjectGather)", body);
            var gather = body.IndexOf("clock.Mark(WallPassPhase.ObjectGather)", StringComparison.Ordinal);
            var geometry = body.IndexOf("clock.Mark(WallPassPhase.Generate)", StringComparison.Ordinal);
            Assert.True(gather < geometry, "the gather is charged before the geometry, not after it");
        }

        [Fact]
        public void ThePassAsksTheAtlasRegionForTheGatherAndTheOccupiedStoreyCountForEverythingElse()
        {
            // Both halves of "the pipeline thought the house was five storeys". The region comes from
            // the wall lines the pass already has, so the gather can order itself; and the level the
            // pass silently holds the lot view at, the level it asks the component for, and the
            // storey count the wall build walks are all the *occupied* count rather than
            // Blueprint.Stories.
            var body = BodyOf(Stripped(Generator()), "GenerateWalls", "bool justTexture");

            Assert.Contains("var occupiedStoreys = Math.Max(1, bp.GetWallStoreys())", body);
            Assert.Contains("var atlasRegion = WallObjectBatch.RegionOf(WallLinesOf(), occupiedStoreys)", body);
            Assert.Contains("var plan = new WallPassPlan(occupiedStoreys)", body);
            Assert.Contains("state.SilentLevel = (sbyte)occupiedStoreys", body);
            Assert.Contains("var storeyLimit = Math.Min(occupiedStoreys, bp.WCRC?.Storeys ?? 0)", body);
            //and no more of the declared count anywhere in the pass
            Assert.DoesNotContain("bp.Stories", body);
        }

        [Fact]
        public void TheWallBuildWalksTheOccupiedStoreysAndSaysHowManyItSkipped()
        {
            // The two halves of the sparse walk. The storey limit is taken from the blueprint, and
            // the group slot is still created and cleared for a storey that is skipped - because
            // GroupsByTexture is indexed by storey, and a slot that was not cleared would keep the
            // last build's groups in a storey that is now empty.
            var text = Stripped(RC());
            var body = BodyOf(text, "Generate", "bool cutaway", "bool allowCut");

            Assert.Contains("var storeyLimit = StoreyLimit()", body);
            Assert.Contains("if (level > storeyLimit)", body);
            Assert.Contains("counts.StoreysSkipped++", body);
            Assert.Contains("counts.StoreysWalked++", body);
            //the slot is settled before the skip, not after
            var slot = body.IndexOf("var grp = GroupsByTexture[level - 1];", StringComparison.Ordinal);
            var skip = body.IndexOf("if (level > storeyLimit)", StringComparison.Ordinal);
            Assert.True(slot < skip, "the group slot is created and cleared for a skipped storey too");
            //and the old whole-grid walk is gone
            Assert.Contains("var off = tiles.OffsetAt(visit)", body);
            Assert.DoesNotContain("for (short y = 0; y < blueprint.Height; y++)", body);
            Assert.DoesNotContain("blueprint.GetWall(x, y, level)", body);
        }

        [Fact]
        public void TheWallBuildLineNamesEveryPhaseAndSaysWhatItWalked()
        {
            // The line the next reader gets instead of "wall geometry 1.03s". The counts lead
            // because they are what makes the timings legible: "20,480 tile(s) read where 90
            // carried a wall" is the whole argument for the sparse walk, and it is true of every
            // ordinary house, so a reader who has measured nothing can still check it.
            var line = Generate().Describe();

            foreach (WallGeneratePhase phase in Enum.GetValues(typeof(WallGeneratePhase)))
            {
                Assert.Contains(WallGenerateTimings.DescribePhase(phase), line);
            }
            Assert.Contains("1 storey(s) walked of 5, 4 empty and skipped", line);
            Assert.Contains("90 tile(s) read where 60 carried a wall", line);
            Assert.Contains("178 line(s) into 8 group(s)", line);
        }

        [Fact]
        public void ABuildThatNeverRanSaysZeroRatherThanSayingNothing()
        {
            // Written from a finally-adjacent place, after something has gone wrong, off a struct
            // that may be default-constructed. A line that throws is a line nobody reads.
            var none = WallGenerateTimings.None;
            Assert.Equal(0d, none.TotalMs);
            Assert.Equal(0, none.StoreysWalked);
            Assert.False(string.IsNullOrWhiteSpace(none.Describe()));
            foreach (WallGeneratePhase phase in Enum.GetValues(typeof(WallGeneratePhase)))
            {
                Assert.Equal(0d, none[phase]);
            }
            var missing = default(WallGenerateTimings);
            Assert.Equal(0d, missing[WallGeneratePhase.Walk]);
            Assert.False(string.IsNullOrWhiteSpace(missing.Describe()));
        }

        [Fact]
        public void TheWallPassLineSaysHowManyBoundsItDidNotAskFor()
        {
            // The price of the bound, on the line. A gather that gives up is a facade with less
            // furniture behind its windows, which reads as a bug in the facade rather than as a
            // decision the pass made - and a price nobody can see is a price nobody can judge.
            var ms = new double[WallPassTimings.PhaseCount];
            var counts = new WallPassCounts { Walls = 20, ObjectBoundsRead = 87, ObjectBoundsSkipped = 156 };
            var line = new WallPassTimings(ms, counts, 40d).Describe();

            Assert.Contains("87 bound(s) read, 156 over budget", line);
            //and a pass that asked for everything does not say the word at all
            counts.ObjectBoundsSkipped = 0;
            Assert.DoesNotContain("over budget", new WallPassTimings(ms, counts, 40d).Describe());
        }

        [Fact]
        public void TheWallPassTotalStillExceedsTheSumOfItsPhases()
        {
            // Because the world, the throwaway render target and the readback's stall sit outside
            // every phase. A line whose phases add up to its total is a line that is not
            // measuring the whole of what the game thread lost.
            var ms = new double[WallPassTimings.PhaseCount];
            ms[(int)WallPassPhase.ObjectGather] = 1020d;
            ms[(int)WallPassPhase.Generate] = 1d;
            var timings = new WallPassTimings(ms, new WallPassCounts(), 1030d);
            double sum = 0d;
            foreach (WallPassPhase phase in Enum.GetValues(typeof(WallPassPhase)))
            {
                sum += timings[phase];
            }
            Assert.NotEqual(timings.TotalMs, sum, 3);
        }

        // ---- fixtures and helpers ---------------------------------------------------------------

        private static WallGenerateTimings Generate()
        {
            var ms = new double[WallGenerateTimings.PhaseCount];
            ms[(int)WallGeneratePhase.Walk] = 0.04d;
            ms[(int)WallGeneratePhase.Emit] = 1d;
            ms[(int)WallGeneratePhase.Upload] = 2d;
            return new WallGenerateTimings(ms, new WallGenerateCounts
            {
                StoreysWalked = 1,
                StoreysSkipped = 4,
                TilesRead = 90,
                WallTiles = 60,
                Lines = 178,
                Groups = 8,
                Primitives = 556
            }, 3.1d);
        }

        private static BoundingBox Box(float at)
        {
            return new BoundingBox(new Vector3(at, 0f, 0f), new Vector3(at + 1f, 1f, 1f));
        }

        /// <summary>
        /// A 64x64 five-storey lot with wall tiles on the bottom <paramref name="occupiedStoreys"/>
        /// storeys, which is what the TS1 activator produces for an ordinary house: the arrays are
        /// all five storeys long and only the bottom one has anything in it.
        /// </summary>
        private static Blueprint House(int occupiedStoreys)
        {
            var bp = new Blueprint(64, 64);
            bp.Altitude = new short[64 * 64];
            for (int storey = 0; storey < occupiedStoreys; storey++)
            {
                for (int y = 20; y < 26; y++)
                {
                    for (int x = 24; x < 34; x++)
                    {
                        var off = x + y * 64;
                        bp.Walls[storey][off] = new WallTile
                        {
                            Segments = WallSegments.TopLeft | WallSegments.TopRight,
                            TopLeftPattern = 12,
                            TopRightPattern = 12,
                            BottomLeftPattern = 12,
                            BottomRightPattern = 12,
                            TopLeftStyle = 1,
                            TopRightStyle = 1
                        };
                        bp.WallsAt[storey].Add(off);
                    }
                }
            }
            return bp;
        }

        private static string Generator() => File.ReadAllText(Path.Combine(Root(),
            "FreeSO/TSOClient/tso.world/Facade/LotFacadeGenerator.cs"));

        private static string RC() => File.ReadAllText(Path.Combine(Root(),
            "FreeSO/TSOClient/tso.world/RC/WallComponentRC.cs"));

        private static string BlueprintSource() => File.ReadAllText(Path.Combine(Root(),
            "FreeSO/TSOClient/tso.world/Model/Blueprint.cs"));

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FreeSO")))
            {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "could not find the repository root from " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        /// <summary>
        /// Comments and string literals replaced with spaces, keeping every offset, so a brace or a
        /// word inside one cannot move the end of a method body.
        /// </summary>
        private static string Stripped(string source)
        {
            var chars = source.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
                {
                    while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
                }
                else if (chars[i] == '"' || chars[i] == '\'')
                {
                    var quote = chars[i];
                    chars[i++] = ' ';
                    while (i < chars.Length && chars[i] != quote)
                    {
                        if (chars[i] == '\\') chars[i++] = ' ';
                        if (i < chars.Length) chars[i++] = ' ';
                    }
                    if (i < chars.Length) chars[i] = ' ';
                }
            }
            return new string(chars);
        }

        /// <summary>
        /// The body of the first method whose header contains every one of
        /// <paramref name="fragments"/>, from its opening brace to the one that closes it.
        /// </summary>
        private static string BodyOf(string text, params string[] fragments)
        {
            var found = -1;
            foreach (Match header in Regex.Matches(text, @"[\w<>,\[\]\?\. ]+\([^;{]*\)\s*\{"))
            {
                var all = true;
                foreach (var fragment in fragments)
                {
                    if (header.Value.IndexOf(fragment, StringComparison.Ordinal) < 0) { all = false; break; }
                }
                if (all) found = header.Index + header.Length - 1;
            }
            Assert.True(found > 0, "no method taking " + string.Join(" and ", fragments) + " was found");

            var depth = 0;
            for (int i = found; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(found, i - found + 1);
            }
            throw new InvalidOperationException("unbalanced braces from offset " + found);
        }
    }
}
