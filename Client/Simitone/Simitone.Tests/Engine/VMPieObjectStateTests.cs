using FSO.SimAntics;
using FSO.SimAntics.Model;
using System;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMPieObjectState"/> - the object's own half of a pie-menu refusal, which is the
    /// half that is decided by the thing that was clicked rather than by the sim.
    ///
    /// <para>
    /// The case this exists for is <c>Objects/Food.iff</c> <c>TTAB 0x0082</c>, "Food - Regular
    /// Group Meal" (OBJD <c>0x41A9</c>, GUID <c>0x15866694</c>). Its rows 0 and 1 - "Eat" and "Clean
    /// Up" - share a three-instruction prologue that can turn both down at once, and the game
    /// shipped with both of them turned down for a freshly served meal. "Check tree returned false"
    /// does not say which of the three did it, and the three are not remotely the same problem.
    /// </para>
    /// <para>
    /// Two of the three carry a sign trap that this pins down, because getting either wrong makes
    /// the diagnosis actively misleading rather than merely incomplete. <c>StackSlot[0] == 0</c>
    /// <b>passes when the slot is empty</b> - a filled slot 0 is the refusal - and
    /// <c>StackObj[8] flagSet 6</c> tests <b>bit 5</b> (<c>flags &amp; 32</c>,
    /// <see cref="VMEntityFlags.Occupied"/>), not <c>flags &amp; 6</c>.
    /// </para>
    /// </summary>
    public sealed class VMPieObjectStateTests
    {
        /// <summary>A served group meal on a table, with nothing wrong with it.</summary>
        private static VMPieObjectFacts ServedOnATable => new VMPieObjectFacts
        {
            Flags = 0,
            ContainerId = 0,
            ContainerType = -1,
            Room = 3,
            Slot0ObjectId = 0,
            TotalSlots = 1,
            Attribute1 = 6,
            Attribute2 = 4
        };

        // ---- the sign traps ----------------------------------------------------------------

        [Fact]
        public void TheOccupiedFlagIsBitFiveNotTheMaskSix()
        {
            // flagSet N is bit N, i.e. 1 << (N - 1). The tree writes `flagSet 6`, so the mask is
            // 1 << 5 = 32 = VMEntityFlags.Occupied. Reading it as `flags & 6` would instead name
            // DisallowPersonIntersection (2) and HasZeroExtent (4) - two flags the food scripts
            // never touch here - and would report the object as clean while the test tree is
            // turning it down.
            Assert.Equal(32, VMPieObjectState.OccupiedMask);
            Assert.Equal(VMEntityFlags.Occupied, (VMEntityFlags)VMPieObjectState.OccupiedMask);

            Assert.True(VMPieObjectState.IsOccupied(0x0020));
            Assert.False(VMPieObjectState.IsOccupied(0x0006));
        }

        [Fact]
        public void TheOtherTwoFlagsInTheSixMaskDoNotReadAsOccupied()
        {
            // The two flags a mistaken `& 6` would pick up, each on its own and together, all read
            // as "not occupied" - which is what they are.
            foreach (var flag in new[] { VMEntityFlags.DisallowPersonIntersection, VMEntityFlags.HasZeroExtent })
            {
                Assert.False(VMPieObjectState.IsOccupied((short)(int)flag));
            }
            Assert.False(VMPieObjectState.IsOccupied(
                (short)((int)VMEntityFlags.DisallowPersonIntersection | (int)VMEntityFlags.HasZeroExtent)));
        }

        [Fact]
        public void AnEmptySlotZeroIsWhatTheGroupMealTestTreeWants()
        {
            // Objects/Food.iff 0x106A [3] and 0x1038 [1] are
            //     StackSlot[0] == 0   T->next   F->return false
            // so slot 0 being EMPTY is the pass and slot 0 holding a single serving is the
            // refusal. Slot 0 is where 0x106D creates a portion and 0x1024 takes it away again; it
            // is not where the plates live.
            Assert.True(VMPieObjectState.IsSlotZeroFree(0));
            Assert.False(VMPieObjectState.IsSlotZeroFree(412));
        }

        [Fact]
        public void AFreshlyServedMealOnATableIsNotRefusedByAnyOfTheThree()
        {
            // The state a working serve produces: on a table, slot 0 empty, Occupied clear,
            // attribute 1 at 6 (served) and attribute 2 counting portions down.
            var facts = ServedOnATable;

            Assert.False(VMPieObjectState.IsHeldByPerson(facts.ContainerId, facts.ContainerType, facts.Room));
            Assert.False(VMPieObjectState.IsOccupied(facts.Flags));
            Assert.True(VMPieObjectState.IsSlotZeroFree(facts.Slot0ObjectId));
            Assert.Equal(VMPieObjectCondition.None, VMPieObjectState.Refusing(facts, out var why));
            Assert.Null(why);
        }

        // ---- each gate, one at a time -------------------------------------------------------

        [Fact]
        public void ASimCarryingTheObjectIsTheFirstRefusal()
        {
            // Food.iff 0x1045 'held by person': a container exists, it is on the lot, and its own
            // definition is a person (OBJD object type 2). A sim's OBJD really is type 2 -
            // TemplateUserData/Characters/User00000.iff OBJD 0x0080 'user00000 - Bob' is
            // `type=Person` - so this is reachable, not theoretical.
            var facts = ServedOnATable;
            facts.ContainerId = 417;
            facts.ContainerType = 2;
            facts.Room = 3;

            Assert.Equal(VMPieObjectCondition.HeldByPerson, VMPieObjectState.Refusing(facts, out var why));
            Assert.Contains("carrying", why);
            Assert.Contains("417", why);
        }

        [Fact]
        public void AContainerThatIsNotAPersonIsNotBeingHeld()
        {
            // A meal standing on a table has a container too; the table is just not a person.
            var facts = ServedOnATable;
            facts.ContainerId = 300;
            facts.ContainerType = 4; // OBJDType.Normal
            facts.Room = 3;

            Assert.False(VMPieObjectState.IsHeldByPerson(facts.ContainerId, facts.ContainerType, facts.Room));
            Assert.Equal(VMPieObjectCondition.None, VMPieObjectState.Refusing(facts, out _));
        }

        [Fact]
        public void NoContainerAtAllIsNotBeingHeld()
        {
            var facts = ServedOnATable;
            facts.ContainerType = -1;
            Assert.False(VMPieObjectState.IsHeldByPerson(facts.ContainerId, facts.ContainerType, facts.Room));
        }

        [Fact]
        public void AContainerOffTheLotIsNotBeingHeld()
        {
            // The tree's second instruction: `StackObj[29] < 0` answers false, i.e. a meal being
            // carried through the inventory is not "held by person" as far as the food scripts are
            // concerned.
            var facts = ServedOnATable;
            facts.ContainerId = 417;
            facts.ContainerType = 2;
            facts.Room = -5; // what GetValue(Room) answers for an object out of world

            Assert.False(VMPieObjectState.IsHeldByPerson(facts.ContainerId, facts.ContainerType, facts.Room));
        }

        [Fact]
        public void AStaleContainerIdIsStillNotHeldWhenThereIsNoContainerType()
        {
            // ContainerId non-zero with a container type of -1 is the "gone" reading the reporter
            // produces for an object whose container could not be resolved. It must not be reported
            // as held, because the tree re-points its stack object at the container and finds
            // nothing there.
            Assert.False(VMPieObjectState.IsHeldByPerson(417, -1, 3));
        }

        [Fact]
        public void ABusySlotZeroIsTheSecondRefusal()
        {
            var facts = ServedOnATable;
            facts.Slot0ObjectId = 412;

            Assert.Equal(VMPieObjectCondition.SlotOccupied, VMPieObjectState.Refusing(facts, out var why));
            Assert.Contains("slot 0", why);
            Assert.Contains("412", why);
        }

        [Fact]
        public void TheOccupiedBitIsTheThirdRefusal()
        {
            // Fridges.iff 4127 [6] and 4108 [11] set it on the meal the moment they create it
            // (`StackObj[8] setFlag 6`), and Food.iff 4203 [12]/[14] clear it when the meal has
            // finished being served. A meal whose flag is still set is refused by both the test
            // tree and the action's own first instruction (4133 'food entry test' [1]), so it is
            // the one gate that can lock a meal out on its own.
            var facts = ServedOnATable;
            facts.Flags = 0x0020;

            Assert.Equal(VMPieObjectCondition.OccupiedFlag, VMPieObjectState.Refusing(facts, out var why));
            Assert.Contains("Occupied", why);
        }

        [Fact]
        public void AFinishedMealIsRefusedOnItsAttributeRatherThanOnTheThree()
        {
            // Food.iff 0x106A [4]: `StackAttrs[1] == Tuning[11]`, and BCON 0x1000 key 11 is 12.
            Assert.Equal(12, VMPieObjectState.FinishedAttributeValue);

            var facts = ServedOnATable;
            facts.Attribute1 = VMPieObjectState.FinishedAttributeValue;

            Assert.Equal(VMPieObjectCondition.MealFinished, VMPieObjectState.Refusing(facts, out var why));
            Assert.Contains("finished", why);
        }

        [Fact]
        public void AmealWithNoPortionsLeftIsRefusedOnItsSecondAttribute()
        {
            // Food.iff 0x106A [12]: `StackAttrs[2] > 0`, reached once attribute 1 is at least
            // Tuning[3] = 6, i.e. once the meal has been served.
            Assert.Equal(6, VMPieObjectState.ServedAttributeValue);

            var facts = ServedOnATable;
            facts.Attribute2 = 0;
            Assert.Equal(VMPieObjectCondition.NoPortionsLeft, VMPieObjectState.Refusing(facts, out var why));
            Assert.Contains("portions", why);
        }

        [Fact]
        public void APortionCountIsNotCheckedBeforeTheMealIsServed()
        {
            // The same instruction is only reached at attribute 1 >= 6. Below that a zero count is
            // not a refusal, and reporting it as one would send the reader after the wrong gate.
            var facts = ServedOnATable;
            facts.Attribute1 = 4;
            facts.Attribute2 = 0;

            Assert.Equal(VMPieObjectCondition.None, VMPieObjectState.Refusing(facts, out _));
        }

        [Fact]
        public void TheGatesAreReportedInTheOrderTheTreeReadsThem()
        {
            // An object that is in a sim's hand AND occupied AND has a busy slot 0 is reported as
            // the one the tree would have hit first, because a reader who fixes that one and
            // re-runs should be sent to the next, not to all three at once.
            var facts = ServedOnATable;
            facts.ContainerId = 417;
            facts.ContainerType = 2;
            facts.Room = 3;
            facts.Slot0ObjectId = 412;
            facts.Flags = 0x0020;

            Assert.Equal(VMPieObjectCondition.HeldByPerson, VMPieObjectState.Refusing(facts, out _));

            facts.ContainerId = 0;
            facts.ContainerType = -1;
            Assert.Equal(VMPieObjectCondition.SlotOccupied, VMPieObjectState.Refusing(facts, out _));

            facts.Slot0ObjectId = 0;
            Assert.Equal(VMPieObjectCondition.OccupiedFlag, VMPieObjectState.Refusing(facts, out _));

            facts.Flags = 0;
            facts.Attribute1 = 12;
            Assert.Equal(VMPieObjectCondition.MealFinished, VMPieObjectState.Refusing(facts, out _));
        }

        // ---- the report itself -------------------------------------------------------------

        [Fact]
        public void TheReportNamesTheNumbersItWasDecidedFrom()
        {
            // "check tree returned false" plus the caller's person data answers which sim, not
            // which object. These are the numbers the object half of the line has to carry, so a
            // second run is not needed to see which gate fired.
            var facts = ServedOnATable;
            facts.Flags = 0x0020;

            var line = VMPieObjectState.Describe(facts);

            Assert.Contains("flags=0x0020", line);
            Assert.Contains("Occupied=1", line);
            Assert.Contains("slot0=0/1", line);
            Assert.Contains("container=0", line);
            Assert.Contains("room=3", line);
            Assert.Contains("attr1=6", line);
            Assert.Contains("attr2=4", line);
            Assert.Contains("refused by:", line);
        }

        [Fact]
        public void AReportWithNoGateToNameStillCarriesEveryNumber()
        {
            // The worst case is "all the gates I know about passed", and that is only useful if the
            // snapshot is in the line - otherwise the next guess starts from nothing again.
            var line = VMPieObjectState.Describe(ServedOnATable);

            Assert.DoesNotContain("refused by:", line);
            Assert.Contains("flags=0x0000", line);
            Assert.Contains("Occupied=0", line);
            Assert.Contains("attr1=6", line);
        }

        [Fact]
        public void AnObjectWithNoSlotsAndNoAttributesIsStillDescribed()
        {
            // Most objects in the game are not food. They must produce the same clause with zeroes
            // rather than being skipped, so a line is never "caller facts and nothing else".
            var bare = new VMPieObjectFacts { ContainerType = -1, Room = 1, TotalSlots = 0 };
            var line = VMPieObjectState.Describe(bare);

            Assert.Contains("slot0=0/0", line);
            Assert.Contains("attr1=0", line);
            Assert.Equal(VMPieObjectCondition.None, VMPieObjectState.Refusing(bare, out _));
        }

        [Fact]
        public void TheReportIsAPureFunctionOfItsArgument()
        {
            // The reporter runs on every refused interaction of every mouse move, and its output
            // goes into a throttled log line whose key does not include the detail. Two reads of
            // the same state therefore have to be the same string, or the throttle is a lie.
            var a = VMPieObjectState.Describe(ServedOnATable);
            var b = VMPieObjectState.Describe(ServedOnATable);

            Assert.Equal(a, b);
        }
    }
}
