using FSO.LotView.Model;
using FSO.SimAntics.Entities;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// A multitile group rebuilt from a save, and the rotation applied when a second group of the
    /// same object is folded into it.
    ///
    /// <para>
    /// Both bugs here were of the same kind: a group holds two parallel lists -
    /// <c>Objects</c> and <c>Offsets</c> - that have to stay the same length and stay paired, and
    /// the load code stopped maintaining that for the entries it could not resolve. A save written
    /// where a custom multitile was installed can name a sub-object that no longer loads, and
    /// <c>VM.LoadAsync</c> builds one group per marshalled entry with no catch on the path, so that
    /// is a lot-load crash rather than a degraded object.
    /// </para>
    ///
    /// <para>
    /// The rules are exercised directly, with strings standing in for entities: a test process has
    /// no content database, no graphics device and cannot build a lot, which is the same reason
    /// the rules live in static helpers rather than inline in the load.
    /// </para>
    /// </summary>
    public sealed class VMMultitileGroupTests
    {
        // ---- rebuilding a group from a save (E-01, E-33) ------------------------------------

        [Fact]
        public void AGroupThatFullyResolvesKeepsEveryEntryInOrder()
        {
            var offsets = new[] { Off(0, 0, 0), Off(16, 0, 0), Off(0, 16, 0) };

            var resolved = VMMultitileGroup.ResolveOffsets<string>(3, offsets, i => "obj" + i);

            Assert.Equal(new[] { "obj0", "obj1", "obj2" }, resolved.Select(x => x.Key));
            Assert.Equal(offsets, resolved.Select(x => x.Value));
        }

        [Fact]
        public void AnObjectThatDidNotLoadTakesItsOffsetWithIt()
        {
            // The bug: the load loop skipped ids that failed to resolve, so Objects came back
            // shorter than the id list - and everything that walks the group by index could then
            // read past the end, or read the *wrong* entity's offset.
            var offsets = new[] { Off(0, 0, 0), Off(16, 0, 0), Off(32, 0, 0) };

            var resolved = VMMultitileGroup.ResolveOffsets<string>(3, offsets, i => i == 1 ? null : "obj" + i);

            Assert.Equal(new[] { "obj0", "obj2" }, resolved.Select(x => x.Key));
            //and the survivor keeps its own offset, not the missing entry's.
            Assert.Equal(Off(32, 0, 0), resolved[1].Value);
        }

        [Fact]
        public void AGroupIsNeverLongerThanTheEntriesThatResolved()
        {
            // The specific shape that used to throw: every id but the first is gone, and the code
            // after the loop still walked three of them.
            var resolved = VMMultitileGroup.ResolveOffsets<string>(3,
                new[] { Off(0, 0, 0), Off(16, 0, 0), Off(32, 0, 0) },
                i => i == 0 ? "lead" : null);

            Assert.Single(resolved);
            Assert.Equal("lead", resolved[0].Key);
        }

        [Fact]
        public void AMissingOffsetReadsAsTheOriginRatherThanThrowing()
        {
            // A save whose offset array is short of its id list is a third way to be one entry
            // behind, and it used to be an IndexOutOfRangeException on the lot load.
            var resolved = VMMultitileGroup.ResolveOffsets<string>(2, new[] { Off(16, 0, 0) }, i => "obj" + i);

            Assert.Equal(Off(16, 0, 0), resolved[0].Value);
            Assert.Equal(new LotTilePos(), resolved[1].Value);
        }

        [Fact]
        public void AnEmptySaveRebuildsNothing()
        {
            Assert.Empty(VMMultitileGroup.ResolveOffsets<string>(0, new[] { Off(16, 0, 0) }, i => "obj" + i));
        }

        [Fact]
        public void ThereIsNoResolverToAskSoNothingIsResolved()
        {
            Assert.Empty(VMMultitileGroup.ResolveOffsets<string>(2, new[] { Off(16, 0, 0) }, null));
        }

        [Fact]
        public void EntriesWithNoOffsetStillResolveToTheOrigin()
        {
            // An absent offset array is not a reason to drop the objects, only to place them at 0,0.
            var resolved = VMMultitileGroup.ResolveOffsets<string>(2, null, i => "obj" + i);

            Assert.Equal(new[] { "obj0", "obj1" }, resolved.Select(x => x.Key));
            Assert.All(resolved, x => Assert.Equal(new LotTilePos(), x.Value));
        }

        [Fact]
        public void OnlyTheGameObjectsInAGroupGetTheBrokenMultitileSmoke()
        {
            // The other half of the same line: it cast whatever the group held. A group can hold a
            // person - in the sandbox and in saved blueprints - and the cast was unconditional.
            var group = new Base[]
            {
                new Base { Name = "chair" },
                new Sub { Name = "table" },
                new Base { Name = "person" },
                null,
                new Sub { Name = "lamp" },
            };

            var lit = new List<string>();
            var count = VMMultitileGroup.ForEachGameObject<Base, Sub>(group, sub => lit.Add(sub.Name));

            Assert.Equal(2, count);
            Assert.Equal(new[] { "table", "lamp" }, lit);
        }

        [Fact]
        public void WalkingAnEmptyOrAbsentGroupDoesNothing()
        {
            var count = 0;
            count += VMMultitileGroup.ForEachGameObject<Base, Sub>(new Base[0], sub => count++);
            count += VMMultitileGroup.ForEachGameObject<Base, Sub>(null, sub => count++);

            Assert.Equal(0, count);
        }

        // ---- merging two groups of the same object (E-19) ----------------------------------
        //
        // The rule is one equation, and both halves of it are in the engine:
        //
        //   world = basePos + ForwardRotate(local - baseOffset, baseDir)   (ChangePosition, SetVisualPosition)
        //   local = baseOffset + InverseRotate(world - basePos, baseDir)   (VMMultitileGroup.LocalOffset)
        //
        // The tests below ask for a local offset and put it back through the *engine's own*
        // forward transform - VMMultitileGroup.ForwardRotate, the one ChangePosition calls - rather
        // than through a restatement of it written here. A restatement would have agreed with a
        // wrong one, which is how the two halves drifted apart in the first place: the old merge
        // called Vector3.Transform and threw the result away, so a dynamic multitile assembled
        // facing anything but north combined at un-rotated offsets and put its tiles a quarter turn
        // from where the player put them. That is the "placing elements on other elements only
        // sometimes works" report, and it depended on the base object's direction and on which of
        // the four rotations the player had applied.

        /// <summary>Every direction the forward transform distinguishes, plus the four it folds.</summary>
        public static TheoryData<Direction> AllDirections => new TheoryData<Direction>
        {
            Direction.NORTH, Direction.EAST, Direction.SOUTH, Direction.WEST,
            //the four diagonals. DirectionRotation has always folded these to north, so both
            //halves of the transform have to fold them the same way or they stop being inverses.
            Direction.NORTHWEST, Direction.NORTHEAST, Direction.SOUTHWEST, Direction.SOUTHEAST,
        };

        /// <summary>Whole-tile world deltas, in every compass direction and one off each axis.</summary>
        private static readonly LotTilePos[] WorldDeltas =
        {
            Off(32, 0, 0), Off(0, 32, 0), Off(-32, 0, 0), Off(0, -32, 0),
            Off(32, 32, 0), Off(-32, 32, 0), Off(32, -32, 0), Off(-32, -32, 0),
            //and a non-multiple of a tile, which is where a wrong rounding step shows up
            Off(11, -5, 0), Off(0, 0, 0),
        };

        [Theory]
        [MemberData(nameof(AllDirections))]
        public void AMergeOffsetIsTheExactInverseOfThePlacementTransform(Direction direction)
        {
            // The bug's whole shape: two adjacent identical objects merge, and the offset the group
            // stores has to survive the next ChangePosition - which rotates every offset forward by
            // the base's direction. If the merge stored a world delta, that delta gets rotated a
            // second time and the merged tile is seated on a different tile than it was placed on.
            var basePos = Pos(64, 48, 1);

            foreach (var worldDelta in WorldDeltas)
            {
                var local = VMMultitileGroup.LocalOffset(basePos, direction, basePos + worldDelta);
                var back = PlaceAt(basePos, direction, local, new LotTilePos()) - basePos;

                Assert.Equal(worldDelta.x, back.x);
                Assert.Equal(worldDelta.y, back.y);
                Assert.Equal(worldDelta.Level, back.Level);
            }
        }

        [Theory]
        [MemberData(nameof(AllDirections))]
        public void AMergeOffsetIsTheExactInverseEvenWhenTheBaseTileIsItselfOffTheOrigin(Direction direction)
        {
            // The `baseOffset` term. A group's base is the member with MyLeadObject set, and
            // nothing in the OBJ format promises that member's SubIndex is the origin - so the
            // forward transform subtracts leadOff before it rotates, and the merge has to add the
            // same leadOff back. Dropping it seats the merged tile R(leadOff) from where it stood.
            var basePos = Pos(200, 144, 1);
            var leadOff = Off(-16, 48, 0);

            foreach (var worldDelta in WorldDeltas)
            {
                var local = VMMultitileGroup.LocalOffset(basePos, direction, basePos + worldDelta, leadOff);
                var back = PlaceAt(basePos, direction, local, leadOff) - basePos;

                Assert.Equal(worldDelta.x, back.x);
                Assert.Equal(worldDelta.y, back.y);
                Assert.Equal(worldDelta.Level, back.Level);
            }
        }

        [Theory]
        [MemberData(nameof(AllDirections))]
        public void ABaseOffsetOfZeroIsTheSameAsNotMentioningOne(Direction direction)
        {
            // The three-argument overload is not a second rule; it is the case where the base sits
            // at the origin, which is what FindBaseObject prefers and what every single-tile
            // group's base is.
            var basePos = Pos(16, 16, 1);
            var worldDelta = Off(32, -32, 0);

            Assert.Equal(
                VMMultitileGroup.LocalOffset(basePos, direction, basePos + worldDelta, new LotTilePos()),
                VMMultitileGroup.LocalOffset(basePos, direction, basePos + worldDelta));
        }

        [Fact]
        public void TheForwardTransformOnlyKnowsTheFourCardinals()
        {
            // Pinned rather than assumed, because the diagonals are the case where an "obvious"
            // 45-degree turn would be silently wrong on one side of the pair and not the other.
            Assert.Equal(0, VMMultitileGroup.DirectionRotation(Direction.NORTH));
            Assert.Equal(2, VMMultitileGroup.DirectionRotation(Direction.EAST));
            Assert.Equal(4, VMMultitileGroup.DirectionRotation(Direction.SOUTH));
            Assert.Equal(6, VMMultitileGroup.DirectionRotation(Direction.WEST));

            foreach (var diagonal in new[]
            {
                Direction.NORTHWEST, Direction.NORTHEAST, Direction.SOUTHEAST, Direction.SOUTHWEST
            })
            {
                Assert.Equal(0, VMMultitileGroup.DirectionRotation(diagonal));
            }
        }

        [Theory]
        [InlineData(Direction.NORTH)]
        [InlineData(Direction.EAST)]
        [InlineData(Direction.SOUTH)]
        [InlineData(Direction.WEST)]
        [InlineData(Direction.NORTHWEST)]
        [InlineData(Direction.SOUTHEAST)]
        public void TheForwardAndInverseRotationsAreActuallyInverses(Direction direction)
        {
            foreach (var delta in WorldDeltas)
            {
                var there = VMMultitileGroup.ForwardRotate(new Vector3(delta.x, delta.y, 0), direction);
                var back = VMMultitileGroup.InverseRotate(there, direction);

                Assert.Equal((double)delta.x, back.X, 3);
                Assert.Equal((double)delta.y, back.Y, 3);
            }
        }

        [Fact]
        public void AnUnrotatedMergeOffsetIsTheWorldDeltaItself()
        {
            // Retail semantics preserved: a base tile facing north needs no rotation at all.
            var offset = VMMultitileGroup.LocalOffset(Pos(0, 0, 1), Direction.NORTH, Pos(32, 16, 1));

            Assert.Equal(Off(32, 16, 0), offset);
        }

        [Fact]
        public void AMergedOffsetIsRotatedIntoTheBaseTilesOwnFrame()
        {
            // The concrete failures under the old code, which returned the world delta unchanged
            // for every direction: a base facing east, one tile to the east of the merged tile, has
            // that tile one tile SOUTH of itself in its own frame.
            var basePos = Pos(0, 0, 1);

            Assert.Equal(Off(0, -32, 0),
                VMMultitileGroup.LocalOffset(basePos, Direction.EAST, Pos(32, 0, 1)));
            Assert.Equal(Off(-32, 0, 0),
                VMMultitileGroup.LocalOffset(basePos, Direction.SOUTH, Pos(32, 0, 1)));
            Assert.Equal(Off(0, 32, 0),
                VMMultitileGroup.LocalOffset(basePos, Direction.WEST, Pos(32, 0, 1)));
        }

        [Fact]
        public void AMergeOffsetRoundTripsThroughAGroupWhoseOffsetsAreTheOnesAlreadyStored()
        {
            // The end-to-end shape of a placement, in the order it happens: a second identical
            // object is put next to an already-rotated one, Combine stores an offset for it, and
            // the merged group is then repositioned. Every member has to come back to the tile it
            // was merged from - including the ones the group already had, whose offsets came out
            // of the OBJ's SubIndex and were never rotated on the way in.
            //
            // WEST is three quarter turns, so a merged tile's local offset is nothing like its
            // world delta, which is what the pre-fix code got wrong on three of the four rotations.
            var basePos = Pos(320, 320, 1);
            var direction = Direction.WEST;
            var leadOff = Off(0, 0, 0);

            var existing = new List<LotTilePos> { leadOff, Off(16, 32, 0) }; //the base, and one sub-tile
            //a second dishwasher, dropped one tile north of the first.
            var incoming = Pos(320, 288, 1);
            existing.Add(VMMultitileGroup.LocalOffset(basePos, direction, incoming,
                VMMultitileGroup.LeadOffsetAt(existing, 0)));

            Assert.Equal(new LotTilePos(320, 320, 1), PlaceAt(basePos, direction, existing[0], leadOff));
            Assert.Equal(new LotTilePos(352, 304, 1), PlaceAt(basePos, direction, existing[1], leadOff));
            Assert.Equal(incoming, PlaceAt(basePos, direction, existing[2], leadOff));
        }

        [Fact]
        public void AMergeOffsetKeepsTheLevelDelta()
        {
            // The level is not rotated, only carried.
            var offset = VMMultitileGroup.LocalOffset(Pos(0, 0, 2), Direction.EAST, Pos(0, 0, 4));

            Assert.Equal(2, offset.Level);
        }

        [Fact]
        public void AnOffsetTheBaseTileItselfWouldHaveIsZero()
        {
            Assert.Equal(new LotTilePos(), VMMultitileGroup.LocalOffset(Pos(80, 80, 1), Direction.WEST, Pos(80, 80, 1)));
        }

        [Fact]
        public void TheBaseTilesOwnOffsetIsSubtractedBeforeTheForwardRotation()
        {
            // The term the placement paths subtract. ChangePosition does
            // `Transform(Offsets[i] - leadOff, rot)`, so a merge that does not add it back is off by
            // R(leadOff) - and, unlike the discarded Transform, that error does not vanish for a
            // north-facing base.
            var leadOff = Off(0, 32, 0);
            var local = VMMultitileGroup.LocalOffset(Pos(0, 0, 1), Direction.EAST, Pos(32, 0, 1), leadOff);

            //the local offset, measured from the base's own entry, is the inverse-rotated world delta
            Assert.Equal(new LotTilePos(0, -32, 0), local - leadOff);
            Assert.Equal(new LotTilePos(32, 0, 1), PlaceAt(Pos(0, 0, 1), Direction.EAST, local, leadOff));
        }

        [Fact]
        public void ABaseIndexThatIsNotThereReadsAsTheOrigin()
        {
            // `Objects.IndexOf(BaseObject)` answers -1 whenever the base is not a member, and after
            // a merge that is exactly what a reader of the group it was folded into sees. Reading
            // off the end of the list there is a crash on the placement path.
            var offsets = new List<LotTilePos> { Off(0, 0, 0), Off(16, 0, 0) };

            Assert.Equal(new LotTilePos(), VMMultitileGroup.LeadOffsetAt(offsets, -1));
            Assert.Equal(new LotTilePos(), VMMultitileGroup.LeadOffsetAt(offsets, 2));
            Assert.Equal(new LotTilePos(), VMMultitileGroup.LeadOffsetAt(offsets, int.MaxValue));
            Assert.Equal(new LotTilePos(), VMMultitileGroup.LeadOffsetAt(null, 0));
            Assert.Equal(new LotTilePos(), VMMultitileGroup.LeadOffsetAt(new List<LotTilePos>(), 0));

            //and the answers that are not degenerate still come through.
            Assert.Equal(Off(16, 0, 0), VMMultitileGroup.LeadOffsetAt(offsets, 1));
        }

        // ---- helpers ----------------------------------------------------------------------

        /// <summary>Tile coordinates, which is what an entity's <c>Position</c> holds.</summary>
        private static LotTilePos Pos(int x, int y, int level) => new LotTilePos((short)x, (short)y, (sbyte)level);

        /// <summary>Group offsets, which are a 1/16th-of-a-tile vector rather than a tile.</summary>
        private static LotTilePos Off(int x, int y, int level) => new LotTilePos((short)x, (short)y, (sbyte)level);

        /// <summary>
        /// Where <c>ChangePosition</c> puts the member at local offset <paramref name="local"/> when
        /// the group is seated with its base at <paramref name="basePos"/> facing
        /// <paramref name="direction"/>.
        ///
        /// <para>
        /// The line this restates is <c>Transform(Offsets[i] - leadOff, rotMat)</c>, added to
        /// <c>pos</c> and rounded - and the transform itself is
        /// <see cref="VMMultitileGroup.ForwardRotate"/>, the engine's. Restating the <em>shape</em>
        /// is unavoidable without a lot; restating the <em>rotation</em> is not, and the rotation is
        /// the half that was wrong.
        /// </para>
        /// </summary>
        private static LotTilePos PlaceAt(LotTilePos basePos, Direction direction, LotTilePos local, LotTilePos leadOff)
        {
            var off = VMMultitileGroup.ForwardRotate(
                new Vector3(local.x - leadOff.x, local.y - leadOff.y, 0), direction);

            return new LotTilePos(
                (short)Math.Round(basePos.x + off.X),
                (short)Math.Round(basePos.y + off.Y),
                (sbyte)(basePos.Level + local.Level));
        }

        private class Base
        {
            public string Name;
        }

        private sealed class Sub : Base
        {
        }
    }
}
