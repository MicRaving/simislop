using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// Primitive 45, <c>set_motive_deltas</c>, and the single-motive clear in
    /// <see cref="VMMotiveChange.ClearOne"/> that <see cref="VMSetMotiveChange"/> now calls.
    ///
    /// <para>
    /// The operand is <c>[deltaOwner:u8][maxOwner:u8][motive:u8][flags:u8][delta:i16][max:i16]</c>.
    /// Bit 0 of <c>flags</c> is the clear flag, and the engine's reading of it was "cancel every
    /// motive rate change on this sim" - it called <c>VMAvatar.ClearMotiveChanges()</c>, which loops
    /// all sixteen slots, and discarded the motive byte it had just parsed. A script asking to
    /// cancel one motive's drain also cancelled all fifteen others, including drains installed by
    /// interactions that were still running.
    /// </para>
    ///
    /// <para>
    /// Retail does not constrain the change. All <b>1,629</b> <c>set_motive_deltas</c> instructions
    /// across the eleven shipped TS1 cabinets have <c>flags == 0</c>: <b>not one</b> sets the clear
    /// flag, so the only user of this path in this install is a download
    /// (<c>CopedFlowers.iff</c> 4103 <c>"Stomp on"</c>, operand <c>[FF,06,05,01,...]</c> = "cancel
    /// Energy"), where the blanket read also wiped the sim's Bladder, Hunger and whatever else was
    /// in flight. The other end of the primitive is untouched by this: the between-interactions
    /// reset in <c>VMThread.EndCurrentInteraction</c> genuinely is a blanket clear and still is one,
    /// which is why the two entry points are separate methods rather than one with a flag.
    /// </para>
    /// </summary>
    public sealed class VMSetMotiveChangeTests
    {
        /// <summary>Every motive slot a sim has, 0..15.</summary>
        private static readonly VMMotive[] AllMotiveSlots = Enumerable.Range(0, 16).Cast<VMMotive>().ToArray();

        /// <summary>A rate-change table with every slot in flight, as a sim mid-interaction would have.</summary>
        private static VMMotiveChange[] FullyLoadedTable()
        {
            var changes = new VMMotiveChange[16];
            for (int i = 0; i < changes.Length; i++)
            {
                changes[i] = new VMMotiveChange
                {
                    Motive = (VMMotive)i,
                    PerHourChange = (short)(-(i + 1) * 2),   // a drain, so a clear is visible
                    MaxValue = 100,
                };
            }
            return changes;
        }

        [Fact]
        public void ClearingOneMotiveLeavesTheOtherFifteenInFlight()
        {
            // The bug, stated as a property. Before the fix this was `ClearAll`, and the assertion
            // below failed for all sixteen slots.
            var changes = FullyLoadedTable();

            VMMotiveChange.ClearOne(changes, VMMotive.Energy);

            Assert.Equal(0, changes[(int)VMMotive.Energy].PerHourChange);
            for (var motive = 0; motive < 16; motive++)
            {
                if (motive == (int)VMMotive.Energy) continue;
                Assert.True(changes[motive].PerHourChange != 0,
                    $"{((VMMotive)motive).ToString()} was in flight and must survive clearing Energy");
            }
        }

        [Fact]
        public void ClearingResetsTheRateAndRestoresTheUncappedCeiling()
        {
            // `VMMotiveChange.Clear` is not just a zeroing: it also puts MaxValue back to
            // short.MaxValue. That matters because `Tick` refuses to move a motive that is already
            // past its MaxValue - a stale low ceiling would keep a *different* motive pinned there
            // once its own rate change is set. A clear that only zeroed the rate would leak it.
            var changes = FullyLoadedTable();

            VMMotiveChange.ClearOne(changes, VMMotive.Bladder);

            Assert.Equal(0, changes[(int)VMMotive.Bladder].PerHourChange);
            Assert.Equal(short.MaxValue, changes[(int)VMMotive.Bladder].MaxValue);
        }

        [Fact]
        public void TheClearTargetsTheMotivesOwnSlotNotTheTablesOrder()
        {
            // The table is indexed by the raw motive id, and TS1's needs are at 5,6,7,8,9,13,14,15 -
            // not 0..7 - so a helper that walked the table instead of indexing it would clear the
            // wrong slots. Indexing is the only correct answer and this is what holds it to that.
            var changes = FullyLoadedTable();

            VMMotiveChange.ClearOne(changes, VMMotive.Social);   // 14

            Assert.Equal(0, changes[14].PerHourChange);
            Assert.NotEqual(0, changes[13].PerHourChange);       // Room (13) is adjacent and untouched
            Assert.NotEqual(0, changes[15].PerHourChange);       // Fun (15) likewise
        }

        [Theory]
        [InlineData(VMMotive.Energy)]
        [InlineData(VMMotive.Comfort)]
        [InlineData(VMMotive.Hunger)]
        [InlineData(VMMotive.Hygiene)]
        [InlineData(VMMotive.Bladder)]
        [InlineData(VMMotive.Mood)]
        [InlineData(VMMotive.Room)]
        [InlineData(VMMotive.Social)]
        [InlineData(VMMotive.Fun)]
        [InlineData((VMMotive)10)]
        [InlineData((VMMotive)12)]
        [InlineData((VMMotive)0)]
        public void AnyInRangeMotiveIdIsAnExactSingleSlotClear(VMMotive motive)
        {
            var changes = FullyLoadedTable();

            VMMotiveChange.ClearOne(changes, motive);

            var cleared = Enumerable.Range(0, changes.Length).Count(i => changes[i].PerHourChange == 0);
            Assert.Equal(1, cleared);
            Assert.Equal(0, changes[(int)motive].PerHourChange);
        }

        [Theory]
        [InlineData((VMMotive)16)]
        [InlineData((VMMotive)17)]
        [InlineData((VMMotive)100)]
        [InlineData((VMMotive)255)]
        public void AnOutOfRangeMotiveIdIsRefusedRatherThanThrown(VMMotive motive)
        {
            // The motive byte in the operand is a raw u8, so 16..255 is reachable from corrupt or
            // hand-edited data. A primitive that throws takes the sim's whole thread with it, and
            // there is no slot to clear anyway - so the answer is "do nothing", not "crash the sim
            // and every other thing it was doing".
            var changes = FullyLoadedTable();

            VMMotiveChange.ClearOne(changes, motive);

            Assert.All(changes, change => Assert.NotEqual(0, change.PerHourChange));
        }

        [Fact]
        public void ANullOrPartlyBuiltTableIsRefusedRatherThanThrown()
        {
            // A partially deserialised avatar can present a null array or one with holes in it; the
            // per-slot path has to survive both, because it runs on the autonomy tick.
            VMMotiveChange.ClearOne(null, VMMotive.Energy);
            VMMotiveChange.ClearOne(new VMMotiveChange[0], VMMotive.Energy);

            var holed = new VMMotiveChange[16];
            holed[2] = new VMMotiveChange { Motive = (VMMotive)2, PerHourChange = -4, MaxValue = 50 };
            VMMotiveChange.ClearOne(holed, (VMMotive)2);
            VMMotiveChange.ClearOne(holed, VMMotive.Energy);      // the null slot must not throw
            VMMotiveChange.ClearOne(holed, VMMotive.Fun);        // nor must an empty tail

            Assert.Equal(0, holed[2].PerHourChange);
        }

        [Fact]
        public void TheBlanketClearStillClearsEverySlot()
        {
            // `VMThread.EndCurrentInteraction` resets motive deltas between interactions, and that is
            // a deliberate clear of all sixteen. The two operations share a name and must not share
            // an implementation that is wrong for one of them.
            var changes = FullyLoadedTable();

            VMMotiveChange.ClearAll(changes);

            Assert.All(changes, change =>
            {
                Assert.Equal(0, change.PerHourChange);
                Assert.Equal(short.MaxValue, change.MaxValue);
            });
        }

        [Fact]
        public void RepeatedClearsAreIdempotent()
        {
            // Scripts call these from trees that re-run, so clearing an already-clear slot has to be
            // a no-op rather than a state change.
            var changes = FullyLoadedTable();

            VMMotiveChange.ClearOne(changes, VMMotive.Fun);
            var after = changes[(int)VMMotive.Fun].PerHourChange;
            VMMotiveChange.ClearOne(changes, VMMotive.Fun);

            Assert.Equal(after, changes[(int)VMMotive.Fun].PerHourChange);
            Assert.Equal(0, changes[(int)VMMotive.Fun].PerHourChange);
        }

        [Fact]
        public void ClearingAMotiveWithNoRateChangeDoesNotDisturbTheTable()
        {
            // The common case on the autonomy path: a tree asks to cancel Energy, but nothing
            // installed one. Nothing may move - in particular the other fifteen slots keep their
            // own state, since a clear that reached further than its own slot would be the old bug
            // wearing a different hat.
            var changes = FullyLoadedTable();
            for (int i = 0; i < changes.Length; i++) changes[i].PerHourChange = 0;
            var before = changes.Select(x => new { x.PerHourChange, x.MaxValue }).ToArray();

            VMMotiveChange.ClearOne(changes, VMMotive.Energy);

            for (int i = 0; i < changes.Length; i++)
            {
                if (i == (int)VMMotive.Energy)
                {
                    // its own slot is still normalised by the clear
                    Assert.Equal(short.MaxValue, changes[i].MaxValue);
                    continue;
                }
                Assert.Equal(before[i].PerHourChange, changes[i].PerHourChange);
                Assert.Equal(before[i].MaxValue, changes[i].MaxValue);
            }
        }

        // ---- the operand ----------------------------------------------------------------------

        [Fact]
        public void TheClearFlagAndTheMotiveAreIndependentOperands()
        {
            // The whole fix is that these two bytes are separate: the audit's `CopedFlowers` site is
            // `[FF,06,05,01,...]` - literal 6, literal 5, motive **5** (Energy), flag **1**. Reading
            // the flag as "clear everything" threw the 5 away, which is the byte that says *which*
            // motive. Asserted on the real operand so a reordering of the byte layout fails here.
            var operand = new VMSetMotiveChangeOperand();
            operand.Read(new byte[] { 0xFF, 0x06, 0x05, 0x01, 0x00, 0x00, 0x00, 0x00 });

            Assert.Equal(VMMotive.Energy, operand.Motive);
            Assert.True(operand.ClearAll);
            Assert.False(operand.Once);
        }

        [Fact]
        public void EveryMotiveIdSurvivesTheOperandByte()
        {
            // The motive byte is a raw u8, so all sixteen TS1 slots plus the eight it never uses must
            // come back unchanged - the primitive hands them straight to the avatar.
            foreach (var motive in AllMotiveSlots.Concat(new[] { (VMMotive)16, (VMMotive)200, (VMMotive)255 }))
            {
                var operand = new VMSetMotiveChangeOperand { Motive = motive, ClearAll = true };
                var bytes = new byte[8];
                operand.Write(bytes);

                var read = new VMSetMotiveChangeOperand();
                read.Read(bytes);
                Assert.Equal(motive, read.Motive);
                Assert.True(read.ClearAll);
            }
        }

        [Fact]
        public void TheOnceFlagAndTheClearFlagAreDistinctBits()
        {
            // `Once` (bit 1) and `ClearAll` (bit 0) are both on the same byte and the primitive
            // branches on one before it ever looks at the other. Setting one must not set the other,
            // or a "clear" would also be read as a once-only immediate application.
            var clearOnly = new VMSetMotiveChangeOperand { ClearAll = true };
            Assert.True(clearOnly.ClearAll);
            Assert.False(clearOnly.Once);

            var onceOnly = new VMSetMotiveChangeOperand { Once = true };
            Assert.True(onceOnly.Once);
            Assert.False(onceOnly.ClearAll);

            var both = new VMSetMotiveChangeOperand { ClearAll = true, Once = true };
            var bytes = new byte[8];
            both.Write(bytes);
            var read = new VMSetMotiveChangeOperand();
            read.Read(bytes);
            Assert.True(read.ClearAll);
            Assert.True(read.Once);
        }
    }
}
