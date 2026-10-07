using FSO.SimAntics.Entities;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// Containment-slot addressing, the rule <see cref="VMGameObject"/> and <see cref="VMAvatar"/>
    /// share.
    ///
    /// <para>
    /// The two were written as a pair and then drifted: the object one bounds-checked and
    /// null-checked every slot access, and the avatar one indexed <c>Contained</c> outright. The
    /// slot number arrives in a register, so it is script data, and an avatar's
    /// <c>ContainerSlot</c> goes stale whenever a contained object is removed by a path that does
    /// not go through <c>ClearSlot</c> - so out of range and already-empty are both ordinary
    /// states rather than corruption. <c>VMAvatar.PrePositionChange</c> and TS1 global call
    /// <c>SwapMyAndStackObjectsSlots</c> both call <c>ClearSlot</c> with whatever that value says.
    /// </para>
    /// </summary>
    public sealed class VMEntitySlotTests
    {
        [Fact]
        public void AContainerWithNoSlotArrayHasNoSlots()
        {
            // A VMEntity's Contained starts as an empty array and an avatar's is only sized in Init.
            Assert.False(VMEntitySlots.IsValidIndex<string>(null, 0));
            Assert.Null(VMEntitySlots.GetOrDefault<string>(null, 0));
        }

        [Fact]
        public void EverySlotTheContainerActuallyHasIsValid()
        {
            var contained = new[] { "hand", "head", "pelvis" };

            for (var slot = 0; slot < contained.Length; slot++)
            {
                Assert.True(VMEntitySlots.IsValidIndex(contained, slot));
                Assert.Same(contained[slot], VMEntitySlots.GetOrDefault(contained, slot));
            }
        }

        [Fact]
        public void ASlotNumberTheContainerDoesNotHaveIsRefused()
        {
            // A negative slot comes straight from a register read; one past the end comes from a
            // stale ContainerSlot. Both used to be an array index.
            var contained = new[] { "hand", "head", "pelvis" };

            Assert.False(VMEntitySlots.IsValidIndex(contained, -1));
            Assert.False(VMEntitySlots.IsValidIndex(contained, 3));
            Assert.False(VMEntitySlots.IsValidIndex(contained, int.MaxValue));

            Assert.Null(VMEntitySlots.GetOrDefault(contained, -1));
            Assert.Null(VMEntitySlots.GetOrDefault(contained, 3));
        }

        [Fact]
        public void AnEmptySlotReadsAsEmptyRatherThanThrowing()
        {
            // The state ClearSlot is reached in when a contained object was taken out by a path that
            // did not clear the slot. Before the guard this was a null dereference.
            var contained = new string[3];

            Assert.True(VMEntitySlots.IsValidIndex(contained, 0));
            Assert.Null(VMEntitySlots.GetOrDefault(contained, 0));
        }

        [Fact]
        public void AContainerWithNoSlotsAtAllRefusesEvenSlotZero()
        {
            Assert.False(VMEntitySlots.IsValidIndex(new string[0], 0));
            Assert.Null(VMEntitySlots.GetOrDefault(new string[0], 0));
        }

        // ---- the group meal's slot 0 round trip ---------------------------------------------
        //
        // Objects/Food.iff 0x106A [3] and 0x1038 [1] both read
        //     StackSlot[0] == 0   T->next   F->return false
        // and VMMemory answers that scope with `StackObject.GetSlot(n)?.ObjectID ?? 0`. So the
        // whole "Eat" and "Clean Up" prologue of the Regular Group Meal turns on slot 0 reading
        // empty - and the only thing that ever puts something there is a single serving, created in
        // slot 0 by 0x106D 'create a regular meal from a group meal' and taken away again by
        // 0x1024 'generate and pickup plate', which grabs it into the sim's hand.
        //
        // The rule is the two halves of one round trip, so it is exercised as one: place, read,
        // clear, read. A clear that leaves the entry standing is the wedge - the meal keeps a
        // ghost portion in slot 0 and refuses Eat and Clean Up forever, with nothing on the lot to
        // explain why.

        [Fact]
        public void TheGroupMealsSlotZeroIsEmptyAgainOnceThePortionIsPickedUp()
        {
            // The meal: Objects/Food.iff SLOT 0x80 describes slot 0 with one entry, so the object
            // has exactly one slot and it starts empty.
            var meal = new object[1];
            Assert.Equal(0, MealSlotZero(meal));

            // 0x106D: the portion is created in the meal's slot 0. `StackSlot[0]` now names it, and
            // that is the refusal the test tree reports as "slot 0 is occupied".
            var portion = new object();
            meal[0] = portion;
            Assert.Same(portion, VMEntitySlots.GetOrDefault(meal, 0));
            Assert.Equal(1, MealSlotZero(meal));

            // 0x1024: the portion is grabbed into the sim's hand, which is the contained object's
            // PrePositionChange reaching the container's ClearSlot.
            ClearSlotZero(meal);
            Assert.Null(VMEntitySlots.GetOrDefault(meal, 0));
            Assert.Equal(0, MealSlotZero(meal));
        }

        [Fact]
        public void AMealWhoseSlotZeroHasNoObjectHasNoObjectIdToReport()
        {
            // `GetSlot(n)?.ObjectID ?? 0` is the read, and the two halves have to agree: an empty
            // slot has to be a null entity, because a non-null entity with object id 0 would read
            // as the same 0 to the expression while still being an occupant to
            // find_best_object_for_function's `GetSlot(0) == null` and to PlaceInSlot's
            // "already occupied" refusal.
            var meal = new object[1];

            Assert.Null(VMEntitySlots.GetOrDefault(meal, 0));
            Assert.Equal(0, MealSlotZero(meal));
        }

        [Fact]
        public void AMealThatNeverHadTheSlotStillReadsSlotZeroAsEmpty()
        {
            // The degenerate containers: no array at all, and an empty one. Both must read 0, so a
            // check tree that asks is never refused by an object that cannot hold a portion.
            Assert.Equal(0, MealSlotZero(null));
            Assert.Equal(0, MealSlotZero(new object[0]));
        }

        /// <summary>
        /// The object id <c>StackSlot[0]</c> answers, as <c>VMMemory</c> computes it from the
        /// entity rule: the entity in slot 0, or nothing.
        /// </summary>
        private static int MealSlotZero(IList<object> meal) => Id(VMEntitySlots.GetOrDefault(meal, 0));

        /// <summary>
        /// Clear slot 0 the way <c>VMGameObject.ClearSlot</c> does: take the entity out, empty the
        /// entry. Written out rather than called because a test process cannot stand up a
        /// <c>VMEntity</c> - no lot, no content database, no graphics device - and the entry this
        /// guards is <c>Contained</c>, which is what both of them are a rule about.
        /// </summary>
        private static void ClearSlotZero(object[] meal)
        {
            var contained = VMEntitySlots.GetOrDefault(meal, 0);
            if (contained == null) return;
            for (var i = 0; i < meal.Length; i++)
            {
                if (ReferenceEquals(meal[i], contained)) meal[i] = null;
            }
        }

        private static int Id(object entity) => entity == null ? 0 : 1;
    }
}
