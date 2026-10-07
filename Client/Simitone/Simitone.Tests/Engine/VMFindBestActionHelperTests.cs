using FSO.Common.TS1;
using FSO.Content;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMFindBestActionHelper"/> - the two arithmetic halves of autonomy that were inline
    /// in <see cref="VMFindBestAction"/>: turning a scored interaction into the options autonomy may
    /// choose between, and turning a sim's motives into the weights that decide how much each one
    /// counts.
    ///
    /// <para>
    /// Both are pure functions over plain data, which is the only reason either can be pinned here.
    /// The primitive itself needs a live <c>VM</c>, a lot, an avatar with motives and a populated
    /// tree table, and there is no headless harness for that in this tree - so the two defects it
    /// carried were, in effect, untestable until they were pulled out. The comments below give the
    /// engine evidence for each; the tests are the part that stops it regressing.
    /// </para>
    /// </summary>
    public sealed class VMFindBestActionHelperTests
    {
        /// <summary>
        /// The retail Happy Weight table, transcribed from
        /// <c>D:\Program Files\Sims\GameData\Global\Global.far</c> <c>Global.iff</c> STR 0x01F6
        /// (502) - seven curves, in table order. Written out longhand rather than loaded, because a
        /// test that loads the table it is testing proves nothing about the mapping being asserted
        /// and would need a Sims install to run.
        /// </summary>
        private static readonly string[] RetailHappyWeight =
        {
            "(-100;15) (-60;5) (-40;3) (0;1) (100;1)",
            "(-100;10) (-80;3) (-40;1) (100;1)",
            "(-100;5) (-80;3) (-40;1) (100;1)",
            "(-100;10) (-80;3) (-60;1) (100;1)",
            "(-100;5) (-40;2) (40;2) (100;5)",
            "(-100;2) (0;1) (100;2)",
            "(-100;5) (-40;2) (40;2) (100;3)",
        };

        private static TS1Curve[] RetailTable()
        {
            return RetailHappyWeight.Select(x => new TS1Curve(x)).ToArray();
        }

        /// <summary>
        /// A bare object with no tree table and no chunks, so the candidate tests have a real
        /// <c>Callee</c> to point at and can tell two objects apart. <c>VMEntity</c> is abstract, so
        /// the concrete subclass is the one autonomy actually deals in.
        /// </summary>
        private static VMEntity NewEntity(short objectId)
        {
            var resource = new GameObjectResource(new IffFile(), null, null, null, null);
            return new VMGameObject(new GameObject { OBJ = new OBJD(), Resource = resource }, null)
            {
                ObjectID = objectId,
            };
        }

        // ---- the candidate / option list ------------------------------------------------------

        [Fact]
        public void EveryOptionOfAScoredTreeBecomesACandidateNotJustTheFirst()
        {
            // The defect: `foreach (var item in pie) { ...; validActions.Add(first); }`. A check tree
            // that produced three options queued option 0 three times and options 1 and 2 never, so
            // "Sit Down / Sit Down and Watch TV / Sit Down and Chat" was reachable only as "Sit
            // Down" - and the candidate list held three of it. This is the case that failed.
            var pie = new List<VMPieMenuInteraction>
            {
                new VMPieMenuInteraction { Name = "Sit Down", ID = 3, Param0 = 0 },
                new VMPieMenuInteraction { Name = "Sit Down and Watch TV", ID = 3, Param0 = 1 },
                new VMPieMenuInteraction { Name = "Sit Down and Chat", ID = 3, Param0 = 2 },
            };
            var candidates = new List<VMPieMenuInteraction>();
            var sofa = NewEntity(4001);

            var added = VMFindBestActionHelper.AddCandidateOptions(pie, candidates, 0.75f, sofa);

            Assert.Equal(3, added);
            Assert.Equal(3, candidates.Count);
            // in the order the check tree produced them, and each one exactly once
            Assert.Equal(new[] { "Sit Down", "Sit Down and Watch TV", "Sit Down and Chat" },
                candidates.Select(x => x.Name).ToArray());
            Assert.Equal(new short[] { 0, 1, 2 }, candidates.Select(x => x.Param0).ToArray());
        }

        [Fact]
        public void NoOptionIsQueuedTwice()
        {
            // The half of the old bug that is easy to miss: the list had N *copies of one option*, not
            // N options. TakeTopActions keeps the best four, so four copies of one interaction could
            // push every other candidate off the list and out of the weighted random draw entirely.
            var pie = new List<VMPieMenuInteraction>
            {
                new VMPieMenuInteraction { Name = "Eat", ID = 1 },
                new VMPieMenuInteraction { Name = "Drink", ID = 1 },
                new VMPieMenuInteraction { Name = "Read", ID = 1 },
            };
            var candidates = new List<VMPieMenuInteraction>();

            VMFindBestActionHelper.AddCandidateOptions(pie, candidates, 1f, NewEntity(1));

            Assert.Equal(pie.Count, candidates.Distinct().Count());
            Assert.Equal(pie.Count, candidates.Count);
        }

        [Fact]
        public void EveryOptionOfOneTreeCarriesThatTreesScoreAndCallee()
        {
            // The score is per tree, not per option: the whole scoring block reads the TTAB entry's
            // motive advertisements, which say nothing about which pie option was picked. So all
            // options of a tree share a score and a callee, and the old code got that part right -
            // only the identity of the object it added was wrong. Autonomy later calls
            // `selection.Callee.GetAction(selection.ID, ...)`, so a candidate without a callee would
            // be an immediate NullReferenceException.
            var pie = new List<VMPieMenuInteraction>
            {
                new VMPieMenuInteraction { Name = "A", ID = 9 },
                new VMPieMenuInteraction { Name = "B", ID = 9 },
            };
            var candidates = new List<VMPieMenuInteraction>();
            var fridge = NewEntity(1234);

            VMFindBestActionHelper.AddCandidateOptions(pie, candidates, 0.5f, fridge);

            Assert.All(candidates, option =>
            {
                Assert.Equal(0.5f, option.Score);
                Assert.Same(fridge, option.Callee);
            });
        }

        [Fact]
        public void CandidatesFromEarlierTreesSurviveLaterOnes()
        {
            // Autonomy walks every object with a tree table on the lot, so the helper is called once
            // per scored interaction and must only ever append. If it cleared or reordered, the
            // final `OrderByDescending(x => x.Score)` would be ranking a fraction of the lot.
            var candidates = new List<VMPieMenuInteraction>();
            var first = new List<VMPieMenuInteraction> { new VMPieMenuInteraction { Name = "first" } };
            var second = new List<VMPieMenuInteraction>
            {
                new VMPieMenuInteraction { Name = "second-a" },
                new VMPieMenuInteraction { Name = "second-b" },
            };

            VMFindBestActionHelper.AddCandidateOptions(first, candidates, 2f, NewEntity(1));
            VMFindBestActionHelper.AddCandidateOptions(second, candidates, 1f, NewEntity(2));

            Assert.Equal(new[] { "first", "second-a", "second-b" },
                candidates.Select(x => x.Name).ToArray());
        }

        [Fact]
        public void ASingleOptionTreeStillProducesOneCandidate()
        {
            // The overwhelmingly common case: most interactions have one option. It must not
            // regress into producing none.
            var pie = new List<VMPieMenuInteraction> { new VMPieMenuInteraction { Name = "Sleep" } };
            var candidates = new List<VMPieMenuInteraction>();

            Assert.Equal(1, VMFindBestActionHelper.AddCandidateOptions(pie, candidates, 3f, NewEntity(7)));
            Assert.Single(candidates);
        }

        [Fact]
        public void EmptyOrRepeatedOrNullInputIsOfferedAsIsAndNeverThrows()
        {
            // A primitive that throws takes the sim's whole thread with it, and autonomy runs on the
            // per-sim tick for every object on the lot. The primitive already guards with
            // `first != null`, but the helper is the last line and has to be safe on its own.
            var candidates = new List<VMPieMenuInteraction>();

            Assert.Equal(0, VMFindBestActionHelper.AddCandidateOptions(null, candidates, 1f, null));
            Assert.Equal(0, VMFindBestActionHelper.AddCandidateOptions(new List<VMPieMenuInteraction>(), candidates, 1f, null));
            Assert.Equal(0, VMFindBestActionHelper.AddCandidateOptions(new List<VMPieMenuInteraction> { null }, candidates, 1f, null));
            Assert.Equal(0, VMFindBestActionHelper.AddCandidateOptions(new List<VMPieMenuInteraction> { new VMPieMenuInteraction() }, null, 1f, null));
            Assert.Empty(candidates);

            // The same instance twice inside one pie is one option, not two.
            var shared = new VMPieMenuInteraction { Name = "shared" };
            var pie = new List<VMPieMenuInteraction> { shared, shared, shared };
            Assert.Equal(1, VMFindBestActionHelper.AddCandidateOptions(pie, candidates, 1f, NewEntity(3)));
            Assert.Same(shared, Assert.Single(candidates));
        }

        // ---- the motive weights ---------------------------------------------------------------

        [Fact]
        public void TheCurveMapCoversExactlyTheSevenCurvesTheRetailTableShips()
        {
        // STR 502 is SEVEN curves. WeightMotives is NINE long. A positional read
        // (`happyCurve[i]` for i in 0..8) therefore throws on Social and Fun - which is presumably
        // part of why nobody wired it up. This is the structural fact the whole mapping rests on, so
        // it is asserted against both sides independently: the transcribed table's length, and the
        // map's own contents.
            var map = VMFindBestActionHelper.HappyWeightCurveForMotive;

            Assert.Equal(VMFindBestActionHelper.WeightedMotiveCount, map.Length);
            Assert.Equal(VMFindBestAction.WeightMotives.Length, map.Length);
            Assert.Equal(VMFindBestActionHelper.WeightedMotiveCount, VMFindBestActionHelper.UniformWeights().Length);
            Assert.Equal(RetailHappyWeight.Length, map.Count(x => x >= 0));
        }

        [Fact]
        public void NoCurveIsUsedTwiceAndNoCurveIsOutOfRange()
        {
            // A repeated index would weight one motive at another motive's curve and leave a
            // shipped curve dead; an out-of-range one would be an IndexOutOfRangeException on the
            // autonomy path, which is the failure mode this mapping exists to prevent.
            var used = VMFindBestActionHelper.HappyWeightCurveForMotive.Where(x => x >= 0).ToArray();

            Assert.Equal(used.Length, used.Distinct().Count());
            Assert.All(used, curve => Assert.InRange(curve, 0, RetailHappyWeight.Length - 1));
        }

        [Fact]
        public void MoodAndRoomAreTheTwoMotivesTheRetailTableDoesNotWeight()
        {
            // Seven curves for eight needs plus Mood leaves exactly two out. Mood and Room are the
            // pair: Room is a property of the surroundings rather than something an interaction can
            // relieve, and Mood is not a need at all - it is a readout. They are excluded rather
            // than given an invented weight of 1, because a weight the game never asked for would
            // silently re-weight the very average it is being averaged into. Stated as a test so
            // that changing this decision (the one genuinely undocumented call in the mapping) is a
            // deliberate edit rather than a side effect.
            var map = VMFindBestActionHelper.HappyWeightCurveForMotive;

            Assert.Equal(-1, map[VMFindBestActionHelper.WeightIndexOf(VMMotive.Mood)]);
            Assert.Equal(-1, map[VMFindBestActionHelper.WeightIndexOf(VMMotive.Room)]);
            Assert.Equal(7, map.Count(x => x >= 0));
        }

        [Fact]
        public void EveryWeightedMotiveResolvesToADistinctIndexInTheWeightArray()
        {
            // `VMMotive` puts the TS1 needs at 5..9 and 13..15 with Mood back at 3, so a motive's id
            // and its position in `WeightMotives` are different numbers - `Fun` is id 15 but index 8,
            // and `Mood` is id 3 but index 5. Everything that indexes a weight array indexes it by
            // the latter, and this is what holds the two apart.
            foreach (var motive in VMFindBestAction.WeightMotives)
            {
                var index = VMFindBestActionHelper.WeightIndexOf(motive);
                Assert.InRange(index, 0, VMFindBestActionHelper.WeightedMotiveCount - 1);
                Assert.Equal(motive, VMFindBestAction.WeightMotives[index]);
            }

            // the mapping is exactly the reverse of the one the primitive scores with
            for (int motive = 0; motive < VMFindBestAction.MotiveToWeight.Length; motive++)
            {
                var index = VMFindBestActionHelper.WeightIndexOf((VMMotive)motive);
                Assert.Equal(index, VMFindBestAction.MotiveToWeight[motive]);
            }

            // and a motive outside the nine has no weight slot at all
            Assert.Equal(-1, VMFindBestActionHelper.WeightIndexOf(VMMotive.SleepState));
            Assert.Equal(-1, VMFindBestActionHelper.WeightIndexOf(VMMotive.HappyDay));
        }

        [Fact]
        public void TheWeightsAlwaysSumToOne()
        {
            // The primitive normalises by the total before it ever used the weights, and
            // `baseHappy` is a weighted *average* of the effective motives - not a weighted sum. If
            // this stopped holding, every score in the tree would change scale and the 1e-6/1e-7
            // thresholds would stop meaning anything.
            foreach (var value in new short[] { -100, -50, -1, 0, 1, 50, 100 })
            {
                var weights = VMFindBestActionHelper.ResolveMotiveWeights(RetailTable(), Flat(value));
                Assert.Equal(1f, weights.Sum(), 4);
            }
        }

        [Fact]
        public void AStarvingSimsMotivesCountForMoreThanASatisfiedOnes()
        {
            // The whole point of the table. Every shipped curve rises steeply as its motive falls -
            // curve 0 goes 1 at motive 0 to 15 at motive -100 - so a sim whose Energy is at the
            // bottom has that motive's share of "effective motive" multiplied fifteen-fold. Under the
            // hard-wired {1,1,...,1} this engine used before, the two sims scored identically.
            //
            // Only Energy moves, so this measures the curve rather than the renormalisation: with
            // every motive at the same value the shares are w_i/sum(w), which are not equal (curves 4
            // and 6 rise again above +40), so "all nine equal" is not a property of this table and
            // is not what is being claimed here.
            var energy = VMFindBestActionHelper.WeightIndexOf(VMMotive.Energy);
            Assert.Equal(0, energy);   // Energy is the first weighted motive, and owns curve 0

            var full = Flat(0);
            full[energy] = 100;
            var empty = Flat(0);
            empty[energy] = -100;

            var shareFull = VMFindBestActionHelper.ResolveMotiveWeights(RetailTable(), full)[energy];
            var shareEmpty = VMFindBestActionHelper.ResolveMotiveWeights(RetailTable(), empty)[energy];

            // the others hold still at 0, where their seven curves total 9; Energy's own curve value
            // is 1 at motive 100 and 15 at motive -100, so the share goes ~1/9 to ~15/23.
            Assert.Equal(1f / 9f, shareFull, 3);
            Assert.True(shareEmpty > shareFull * 3,
                $"expected Energy to take a much larger share when it is starving, got {shareEmpty} vs {shareFull}");
            Assert.True(shareEmpty > 0.6f, $"a starving Energy should dominate the average, got {shareEmpty}");
        }

        [Fact]
        public void AMotiveTheTableDoesNotWeightContributesNothing()
        {
            // Mood and Room are mapped to -1, so their weights are exactly 0 and they drop out of
            // the average entirely rather than being averaged in with a made-up weight.
            var weights = VMFindBestActionHelper.ResolveMotiveWeights(RetailTable(), Flat(0));

            Assert.Equal(0f, weights[VMFindBestActionHelper.WeightIndexOf(VMMotive.Mood)]);
            Assert.Equal(0f, weights[VMFindBestActionHelper.WeightIndexOf(VMMotive.Room)]);
            Assert.All(weights.Where((_, i) => i != VMFindBestActionHelper.WeightIndexOf(VMMotive.Mood)
                                                 && i != VMFindBestActionHelper.WeightIndexOf(VMMotive.Room)),
                w => Assert.True(w > 0, "every weighted motive must get a positive share"));
        }

        [Theory]
        [InlineData(-100)]
        [InlineData(0)]
        [InlineData(100)]
        public void NoWeightIsEverNegativeNaNOrInfinite(short motive)
        {
            // `baseHappy` feeds a division (score / (1 + atten * distance)) and a comparison against
            // 1e-7, so a single bad weight poisons the whole tick. The shipped curves are all in
            // [1, 15], so this cannot happen with retail data - but a hand-made table can produce a
            // zero or a negative, and that case has to be refused, not propagated.
            var weights = VMFindBestActionHelper.ResolveMotiveWeights(RetailTable(), Flat(motive));

            Assert.All(weights, w =>
            {
                Assert.False(float.IsNaN(w));
                Assert.False(float.IsInfinity(w));
                Assert.True(w >= 0f, $"weight {w} must not be negative");
            });
        }

        [Fact]
        public void AnAbsentTableReproducesTheOldHardWiredWeightsExactly()
        {
            // TSO content has no HappyWeight table at all, and a TS1 lot whose globals failed to load
            // has none either. The old code was `new float[9] {1,1,...,1}` normalised by its own sum,
            // i.e. nine weights of 1/9 - so "fall back" has to mean exactly that, not "something
            // close to that".
            var expected = Enumerable.Repeat(1f / 9f, 9).ToArray();
            var motives = Flat(-20);

            Assert.Equal(expected, VMFindBestActionHelper.ResolveMotiveWeights(null, motives));
            Assert.Equal(expected, VMFindBestActionHelper.ResolveMotiveWeights(new TS1Curve[0], motives));
            Assert.Equal(expected, VMFindBestActionHelper.UniformWeights());
        }

        [Fact]
        public void AShortOrHollowTableFallsBackInsteadOfThrowing()
        {
            // The 7-vs-9 mismatch is exactly this hazard, and a caller that hands over a truncated
            // table (a TS1 lot that loaded only part of Global.iff) must get the old behaviour, not
            // an IndexOutOfRangeException on the autonomy path.
            var motives = Flat(0);
            for (int length = 0; length < RetailHappyWeight.Length; length++)
            {
                var table = RetailTable().Take(length).ToArray();
                Assert.Equal(VMFindBestActionHelper.UniformWeights(),
                    VMFindBestActionHelper.ResolveMotiveWeights(table, motives));
            }

            // a null curve in an otherwise complete table
            var holed = RetailTable();
            holed[3] = null;
            Assert.Equal(VMFindBestActionHelper.UniformWeights(),
                VMFindBestActionHelper.ResolveMotiveWeights(holed, motives));
        }

        [Fact]
        public void AMissingMotiveListFallsBackInsteadOfThrowing()
        {
            var table = RetailTable();
            var expected = VMFindBestActionHelper.UniformWeights();

            Assert.Equal(expected, VMFindBestActionHelper.ResolveMotiveWeights(table, null));
            Assert.Equal(expected, VMFindBestActionHelper.ResolveMotiveWeights(table, new short[0]));
            // a short list is a truncated avatar read; refuse rather than index past the end
            Assert.Equal(expected, VMFindBestActionHelper.ResolveMotiveWeights(table, new short[] { 0, 0 }));
        }

        [Fact]
        public void ATableThatWeighsNothingFallsBackInsteadOfDividingByZero()
        {
            // Every shipped curve is >= 1 across the whole motive range, so the total can never reach
            // zero - but a table of flat zeros, or one that only ever returns negatives, has to be
            // refused rather than normalised, because the normalisation is the first thing that
            // touches the sim's motives.
            var flatZero = new TS1Curve[RetailHappyWeight.Length];
            for (int i = 0; i < flatZero.Length; i++) flatZero[i] = new TS1Curve("(-100;0) (100;0)");

            var negative = new TS1Curve[RetailHappyWeight.Length];
            for (int i = 0; i < negative.Length; i++) negative[i] = new TS1Curve("(-100;-1) (100;-1)");

            var expected = VMFindBestActionHelper.UniformWeights();
            Assert.Equal(expected, VMFindBestActionHelper.ResolveMotiveWeights(flatZero, Flat(0)));
            Assert.Equal(expected, VMFindBestActionHelper.ResolveMotiveWeights(negative, Flat(0)));
        }

        [Fact]
        public void TheChildTableIsUsedTheSameWayAndOnlyDiffersWhereTheRetailDataDiffers()
        {
            // STR 504 ("HappyWeightChild") is the same seven curves as the adult table with one
            // change: curve 4 goes from 5 to 6 at both ends. Children run the same weighting code
            // (the primitive only picks which of the two tables to hand over), so the property is
            // that the change lands on the motive curve 4 belongs to and leaves every other
            // motive's share <i>relative to the others</i> untouched - the absolute shares all shift
            // a little because the total weight grew, which is correct and is why the comparison is
            // made between pairs of unaffected motives rather than against the raw values.
            var adult = RetailTable();
            var child = RetailTable();
            child[4] = new TS1Curve("(-100;6) (-40;2) (40;2) (100;6)");

            var motives = Flat(100);
            var adultWeights = VMFindBestActionHelper.ResolveMotiveWeights(adult, motives);
            var childWeights = VMFindBestActionHelper.ResolveMotiveWeights(child, motives);

            var index = Array.IndexOf(VMFindBestActionHelper.HappyWeightCurveForMotive, 4);
            Assert.True(index >= 0, "curve 4 must be assigned to one of the nine weighted motives");
            Assert.True(childWeights[index] > adultWeights[index],
                "the motive curve 4 weights should gain share for a child");

            // every other motive keeps its weight relative to an unaffected reference motive
            var reference = (VMMotive)VMFindBestAction.WeightMotives
                .First(m => VMFindBestActionHelper.HappyWeightCurveForMotive[VMFindBestActionHelper.WeightIndexOf(m)] == 0);
            var referenceIndex = VMFindBestActionHelper.WeightIndexOf(reference);
            for (int i = 0; i < adultWeights.Length; i++)
            {
                if (i == index || i == referenceIndex) continue;
                var adultRatio = adultWeights[i] / adultWeights[referenceIndex];
                var childRatio = childWeights[i] / childWeights[referenceIndex];
                Assert.Equal(adultRatio, childRatio, 5);
            }
        }

        [Fact]
        public void TheWeightingIsSampledAtTheMotivesOwnValueSoItMovesWithTheSim()
        {
            // A weight is that motive's curve read at that motive's current value - it is not a
            // per-motive constant. Two sims on the same lot, one fed and one starving, must get
            // different weights out of the same table; that is the difference between the curve
            // being applied and merely present.
            //
            // The retail table has two families of curve and this test holds each to its own
            // behaviour rather than pretending they are alike:
            //
            //  * curves 0-3 are one-sided - a motive is only ever worse when it is low. These are
            //    the "a need in trouble should dominate the average" curves, and their share has to
            //    rise as the motive falls.
            //  * curves 4-6 are two-sided - they rise again at the *top* as well as the bottom, so
            //    the motive counts when it is very high too. Curves 4 and 5 are flat-topped and
            //    symmetric (5/5, 2/2); curve 6 is flat-topped but not (5 at -100, 3 at +100). What
            //    they have in common is a minimum around 0, and that is the property worth pinning -
            //    their weight is deliberately near-constant, so their share of a normalised average
            //    shrinks when everything else blows up. Asserting "every motive gains share" would
            //    be asserting the opposite of what the table says.
            var table = RetailTable();
            var desperate = VMFindBestActionHelper.ResolveMotiveWeights(table, Flat(-100));
            var content = VMFindBestActionHelper.ResolveMotiveWeights(table, Flat(100));

            var oneSided = 0;
            var twoSided = 0;
            for (int i = 0; i < desperate.Length; i++)
            {
                var curve = VMFindBestActionHelper.HappyWeightCurveForMotive[i];
                if (curve < 0) continue;

                if (curve <= 3)
                {
                    oneSided++;
                    Assert.True(table[curve].GetPoint(-100) > table[curve].GetPoint(100),
                        $"curve {curve} should weigh an empty motive above a full one");
                    Assert.True(desperate[i] > content[i],
                        $"weight index {i} should gain share when its motive is empty "
                        + $"({desperate[i]} vs {content[i]})");
                }
                else
                {
                    twoSided++;
                    Assert.True(table[curve].GetPoint(-100) > table[curve].GetPoint(0),
                        $"two-sided curve {curve} should weigh its motive at the bottom above the middle");
                    Assert.True(table[curve].GetPoint(100) > table[curve].GetPoint(0),
                        $"two-sided curve {curve} should weigh its motive at the top above the middle");
                    Assert.True(desperate[i] > 0 && content[i] > 0,
                        "every weighted motive keeps a positive share whatever its value");
                }
            }

            // four one-sided curves and three two-sided ones, against the seven in the table
            Assert.Equal(4, oneSided);
            Assert.Equal(3, twoSided);
            Assert.Equal(RetailHappyWeight.Length, oneSided + twoSided);
        }

        private static List<short> Flat(short value)
        {
            var list = new List<short>(VMFindBestActionHelper.WeightedMotiveCount);
            for (int i = 0; i < VMFindBestActionHelper.WeightedMotiveCount; i++) list.Add(value);
            return list;
        }
    }
}
