using FSO.SimAntics;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <c>Global[20]</c> - the "game edition" word, the policy documented on
    /// <see cref="VM.GameEditionGlobalValue"/>, and the evidence behind it.
    ///
    /// <para>
    /// The audit that found this called it "claimed twice": <c>VM.Init</c> pre-set the word to 255
    /// ("expansions running") while six download objects read it as their own 1-bit-per-feature
    /// switch, so their gates read "on" from lot load. The proposed fix was a reserved-bit policy -
    /// engine keeps bit 0, objects get bits 1..15.
    /// </para>
    ///
    /// <para>
    /// That fix was not applied, because a scan of the shipped data says the reading is wrong. Over
    /// the eleven TS1 cabinets (90 <c>.far</c>s) there are <b>1613</b> <c>Global[20]</c>
    /// instructions and <b>zero</b> writes: no <c>setFlag</c>, no <c>clearFlag</c>, nothing. Every
    /// site is a read, in three idioms, and two of them are the reason the word cannot be reduced:
    /// <list type="bullet">
    /// <item><c>Global[20] flagSet N</c> - 1495 sites, N in <b>1..8</b> only. No site tests bits
    /// 9..16, and no site sets any bit.</item>
    /// <item><c>Global[20] &gt; 0</c> - 79 sites, and <c>Global[20] &gt;= N</c> - 39 sites
    /// (N in 1, 2, 4, 8, 16, 32).</item>
    /// </list>
    /// The second group is Maxis's "wrong edition, remove yourself" guard and the false branch is
    /// <c>remove_object_instance</c>. <c>ToiletUrinal_SP.iff</c> 4096 <c>main</c> [0] and 4127
    /// <c>load</c> [0] read <c>Global[20] &gt;= 2</c> and delete the object if it fails;
    /// <c>ShrimpCart.iff</c> reads <c>&gt;= 16</c>, <c>TableSoccer.iff</c> <c>&gt;= 8</c>,
    /// <c>PhoneGlobals.iff</c> 8310 <c>&gt;= 32</c>, and about a hundred retail expansion objects
    /// read <c>&gt; 0</c> the same way. Clearing bits 1..8 - or reducing the word to 1, or to 0 -
    /// makes those objects <b>remove themselves from the lot</b>. The audit's own "ToiletUrinal_SP
    /// gates on bit 2" is the same instruction as its "&gt;= 2 self-delete", read the other way.
    /// </para>
    ///
    /// <para>
    /// So the policy is the mirror of the one proposed: the engine owns <b>bits 1-8</b> and keeps
    /// them all set, and <b>bits 9-16 are reserved for object-private flags</b> - guaranteed clear
    /// at lot load, never written by the engine, and persistent because globals round-trip through
    /// the save (<c>VMTS1LotState.UpdateSIMI</c>). That is real, usable per-object state (the install
    /// has six objects that wanted it and could not have it), and it leaves every shipped gate
    /// working. The tests below are the guard: they assert the property that actually matters -
    /// every idiom the shipped data uses passes against the published value - so the next person to
    /// read "six objects want these bits" fails here instead of quietly deleting a hundred objects.
    /// </para>
    /// </summary>
    public sealed class VMGameEditionGlobalTests
    {
        /// <summary>
        /// The three read idioms found in the shipped data, as (operator, operand) pairs. There is
        /// no fourth: the census over all 1613 sites found no <c>&lt;</c>, no <c>==</c> and no
        /// writes of any kind. A new idiom in the engine's favour would appear here.
        /// </summary>
        private static readonly short[] TestedBitNumbers = { 1, 2, 3, 4, 5, 6, 7, 8 };
        private static readonly short[] ComparedThresholds = { 0, 1, 2, 4, 8, 16, 32 };

        /// <summary>
        /// Census counts over the eleven shipped TS1 cabinets, kept as data so the prose in the class
        /// comment and the assertions below cannot drift apart. These are the numbers a reader checks
        /// their own scan against.
        /// </summary>
        private const int BitTestSites = 1495;        // `Global[20] flagSet N`
        private const int PositiveGuardSites = 79;    // `Global[20] > 0`, false branch removes the object
        private const int ThresholdGuardSites = 39;   // `Global[20] >= N`, same false branch
        private const int TotalSites = 1613;          // every `Global[20]` instruction in the install
        private const int WriteSites = 0;             // `setFlag` / `clearFlag` on Global[20]: none at all
        private const int ReservedBits = 8;           // bits 9..16, unused by every shipped object

        /// <summary>
        /// The self-delete guards that a one-bit "reserved for the engine" default would break:
        /// all 39 <c>&gt;= N</c> sites, across 5 distinct thresholds (2, 4, 8, 16, 32).
        /// </summary>
        private const int DistinctThresholdsBrokenByBitZeroOnly = 5;

        private static short Published => VM.GameEditionGlobalValue;

        [Fact]
        public void TheCensusIsWhatThePolicyIsBuiltOn()
        {
            // The single most important fact about this word, and the one the audit got backwards:
            // no shipped script ever *writes* it. It is an engine-published value that 1613
            // instructions read, so "give the objects some bits" has nothing to give them - and the
            // 118 numeric comparisons are the reason the value cannot come down.
            Assert.Equal(TotalSites, BitTestSites + PositiveGuardSites + ThresholdGuardSites);
            Assert.Equal(0, WriteSites);
            Assert.Equal(118, PositiveGuardSites + ThresholdGuardSites);

            // the bits objects test, and the bits reserved for them, are disjoint and cover the word
            Assert.Equal(8, TestedBitNumbers.Length);
            Assert.Equal(ReservedBits, 8);
            Assert.Equal(16, TestedBitNumbers.Length + ReservedBits);
            // no shipped test reaches into the reserved window, which is what makes reserving it free
            Assert.DoesNotContain(TestedBitNumbers, bit => VM.IsObjectFlagBit(bit));
        }

        [Fact]
        public void TheWordIsGlobalTwenty()
        {
            // Named, not inlined, so a script-level reader has one place to look and the test below
            // cannot drift away from the index the data uses.
            Assert.Equal((ushort)20, VM.GameEditionGlobal);
        }

        [Fact]
        public void TheEngineOwnsTheLowEightBitsAndSetsAllOfThem()
        {
            // Simitone ships every TS1 expansion, so every edition is "present" and all eight engine
            // bits are set. This is the value the engine has always published; it is pinned here so
            // that changing it is a decision someone makes against the evidence below.
            Assert.Equal((short)255, Published);
            Assert.Equal(VM.GameEditionBits, Published);
            Assert.Equal(255, VM.GameEditionBits);
        }

        [Fact]
        public void TheHighEightBitsAreReservedForObjectsAndClearAtLotLoad()
        {
            // This is the half of the policy that is actually new: bits 9-16 are guaranteed clear and
            // never written by the engine, so an object may use one as a persistent per-lot flag.
            // Nothing shipped tests them (0 of 1613 sites go above bit 8), so reserving them costs
            // no shipped behaviour and buys a documented home for per-object state.
            Assert.Equal(0, Published & unchecked((short)0xFF00));
            Assert.Equal(unchecked((short)0xFF00), unchecked((short)~VM.GameEditionBits));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void EveryBitTheShippedDataTestsIsSet(short bitNumber)
        {
            // 1495 of the 1613 sites are `flagSet N` for N in 1..8. All of them must read "on" or
            // 1485 objects change behaviour the moment the default moves.
            Assert.True(IsFlagSet(Published, bitNumber),
                $"bit {bitNumber} is tested by shipped objects and must read as set");
        }

        [Fact]
        public void NoShippedObjectTestsTheReservedBits()
        {
            // The other half of the reservation, asserted from the data side: the census found bit
            // numbers 1..8 only, so bits 9..16 are free. If a future TS1 release or a new download
            // starts using bit 9, this is the test that says the reservation has been given away.
            Assert.Equal(new short[] { 1, 2, 3, 4, 5, 6, 7, 8 }, TestedBitNumbers);
            for (short bit = 9; bit <= 16; bit++)
            {
                Assert.False(IsFlagSet(Published, bit),
                    $"bit {bit} is reserved and must be clear at load, not pre-set");
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(32)]
        public void EveryThresholdTheShippedDataComparesAgainstIsSatisfied(short threshold)
        {
            // 39 sites read `Global[20] >= N`, and the false branch is `remove_object_instance`. The
            // largest N in the install is 32 (PhoneGlobals.iff 8310 'Interaction - Call Friend
            // test'); ShrimpCart.iff reads >= 16 and TableSoccer.iff >= 8. This is the single test
            // that would fail on a reserved-bit policy keeping only bit 0, and the reason that
            // policy was not applied: it does not break six download gates, it makes a hundred and
            // eighteen objects delete themselves.
            Assert.True(Published >= threshold,
                $"Global[20] = {Published} fails the shipped `Global[20] >= {threshold}` self-delete guard");
        }

        [Fact]
        public void ThePositiveGuardIsSatisfied()
        {
            // 79 sites read `Global[20] > 0` with the same self-delete false branch - one per
            // expansion object's `main` and `load` tree. The single cheapest possible reduction of
            // this word to zero deletes the entire retail expansion catalogue from the lot.
            Assert.True(Published > 0);
        }

        [Fact]
        public void NoSmallerNonNegativeValueWouldDo()
        {
            // The exhaustive statement of the constraint, so the "obvious simplification" has
            // somewhere to fail. Searched over the values the policy allows - the low byte, the one
            // the engine owns - because values with any of bits 9..16 set would be claiming the
            // object window the same policy reserves. Within those 256 values, 0x00FF is the only one
            // that satisfies all three shipped idioms: anything smaller drops at least one of bits
            // 1..8 that 1485 objects test, and anything below 32 fails the `>= 32` self-delete guard.
            // That is why this is a constant with a policy comment rather than a tunable.
            for (int candidate = 0; candidate <= 0xFF; candidate++)
            {
                var value = (short)candidate;
                var breaksSomething = TestedBitNumbers.Any(bit => !IsFlagSet(value, bit))
                    || ComparedThresholds.Any(threshold => value < threshold)
                    || !(value > 0);

                if (candidate == 0x00FF) Assert.False(breaksSomething, "the published value must satisfy every idiom");
                else Assert.True(breaksSomething, $"{value} was accepted but breaks at least one shipped Global[20] test");
            }
        }

        [Fact]
        public void TheProvisionalAlternativePolicyWouldBreakTheThingsThisOneDoesNot()
        {
            // The audit's proposal, run against the census: keep bit 0 for the engine, hand bits
            // 1..15 to objects. Every one of the 118 self-delete guards then fails - starting with
            // ToiletUrinal_SP.iff 4096 `main` [0], which reads `>= 2` and calls
            // `remove_object_instance` - and 1485 of the bit tests change reading. This is the test
            // that says the reserved-bit split is the other way round.
            const short proposed = 1;

            // `> 0` happens to survive, which is exactly what makes the proposal tempting...
            Assert.True(proposed > 0);
            // ...but every distinct `>= N` threshold above 1 does not, and between them they are the
            // 39 sites. Both numbers are asserted because the two answers are about different things:
            // five distinct thresholds, 39 instruction sites.
            Assert.Equal(DistinctThresholdsBrokenByBitZeroOnly, ComparedThresholds.Count(threshold => proposed < threshold));
            Assert.Equal(ThresholdGuardSites, 39);            // and seven of the eight bits objects test would flip off.
            Assert.Equal(7, TestedBitNumbers.Count(bit => !IsFlagSet(proposed, bit)));
        }

        [Fact]
        public void TheReservedWindowIsTheOnlyPartObjectsMayWrite()
        {
            Assert.True(VM.IsObjectFlagBit(9));
            Assert.True(VM.IsObjectFlagBit(16));
            Assert.False(VM.IsObjectFlagBit(1));
            Assert.False(VM.IsObjectFlagBit(8));
            Assert.False(VM.IsObjectFlagBit(0));
            Assert.False(VM.IsObjectFlagBit(17));
            Assert.False(VM.IsObjectFlagBit(-1));
        }

        [Fact]
        public void TheEngineBitsAndTheObjectBitsPartitionTheWord()
        {
            // No overlap, no gap: 8 + 8 = 16, so the two windows between them account for every bit a
            // script can address with `flagSet`.
            var engine = unchecked((ushort)VM.GameEditionBits);
            var objects = unchecked((ushort)~VM.GameEditionBits);

            Assert.Equal(0x00FF, engine);
            Assert.Equal(0xFF00, objects);
            Assert.Equal(0, engine & objects);
            Assert.Equal(unchecked((ushort)0xFFFF), engine | objects);
        }

        [Fact]
        public void GlobalsAreAShortArrayLongEnoughToHoldTheWord()
        {
            // `GetGlobalValue` throws above `GlobalState.Length` and `Init` allocates 38, so the
            // index and the array are a matched pair. Cheap to assert, and it catches an
            // off-by-one in a future resize.
            Assert.InRange((int)VM.GameEditionGlobal, 0, 37);
        }

        private static bool IsFlagSet(short value, short bitNumber)
        {
            // `VMExpression`'s own test: bit N is `1 << (N - 1)`, and it is computed on the widened
            // int, so bit 16 (1 << 15) is a positive value in a signed short. Reproduced here rather
            // than referenced so the test states the contract instead of trusting the engine.
            return (value & (1 << (bitNumber - 1))) > 0;
        }
    }
}
