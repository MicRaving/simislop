using FSO.SimAntics;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The array-shaped decisions <see cref="VMAvatar"/> makes from content and from save data:
    /// animation name tables, animation time properties, the person data it inherits from a
    /// neighbour, the blend weights it renders with, and the body-string lists it picks an outfit
    /// from.
    ///
    /// <para>
    /// Every one of these reads something the engine does not control - a global <c>STR</c> in
    /// content, a person-data array in a save, a field in an <c>.anim</c> file - into a fixed-size
    /// array or a bare parse. The rules are exercised here with strings and numbers standing in for
    /// the content, because a test process has no content database, no graphics device and cannot
    /// build a lot.
    /// </para>
    /// </summary>
    public sealed class VMAvatarDataTests
    {
        // ---- animation name tables (a fixed 50 slots, filled from a global STR) ------------

        [Fact]
        public void AnAnimationTableShorterThanTheSlotArrayFillsOnlyWhatItHas()
        {
            var walk = new string[50];
            var read = VMAvatar.CopyAnimationStrings(3, walk, i => "walk" + i);

            Assert.Equal(3, read);
            Assert.Equal(new[] { "walk0", "walk1", "walk2", null }, walk.Take(4));
        }

        [Fact]
        public void AnAnimationTableLongerThanTheSlotArrayIsTruncated()
        {
            // The bug: the copy ran to the source's length, so a global table with more than 50
            // entries - which is why the array is 50, VMRoutingFrame indexes up to anims[25] - was
            // a lot-load crash, once per avatar, rather than a per-sim one.
            var walk = new string[50];
            var read = VMAvatar.CopyAnimationStrings(64, walk, i => "walk" + i);

            Assert.Equal(50, read);
            Assert.Equal(50, walk.Length); //never written past the end
        }

        [Fact]
        public void AnEmptyOrAbsentAnimationTableWritesNothing()
        {
            var walk = new string[50];
            walk[0] = "kept";

            Assert.Equal(0, VMAvatar.CopyAnimationStrings(0, walk, i => "walk" + i));
            Assert.Equal(0, VMAvatar.CopyAnimationStrings(-1, walk, i => "walk" + i));
            Assert.Equal(0, VMAvatar.CopyAnimationStrings(5, null, i => "walk" + i));
            Assert.Equal(0, VMAvatar.CopyAnimationStrings(5, walk, null));

            Assert.Equal("kept", walk[0]);
        }

        [Fact]
        public void ABlankOverrideLeavesTheGlobalAnimationInPlace()
        {
            // The per-object STR 150 override: a blank or absent entry means "no override for this
            // one", so the reader has to leave the slot it is writing alone. For the two global
            // tables this is the same either way - the array starts empty, so "leave it" and
            // "write null" agree.
            var walk = new[] { "global0", "global1", "global2" };

            var read = VMAvatar.CopyAnimationStrings(4, walk, i => i == 1 ? "" : i == 3 ? null : "own" + i);

            Assert.Equal(3, read); //three read, two written
            Assert.Equal(new[] { "own0", "global1", "own2" }, walk);
        }

        // ---- animation time properties (parsed out of an .anim file, in Tick) --------------

        [Fact]
        public void AWellFormedAnimationEventIsRead()
        {
            Assert.Equal(7, VMAvatar.ParseAnimationEvent("7"));
            Assert.Equal(-1, VMAvatar.ParseAnimationEvent("-1"));
        }

        [Fact]
        public void AMalformedAnimationEventIsIgnoredRatherThanThrown()
        {
            // The bug: righthand=/lefthand= used short.Parse, and HandleTimePropsEvent runs from
            // Tick - which is not inside the VM's exception funnel, so a FormatException escaped
            // into the caller of VM.PreDraw and took the lot with it. The xevt line already used
            // TryParse; these two did not.
            Assert.Equal(0, VMAvatar.ParseAnimationEvent("wave"));
            Assert.Equal(0, VMAvatar.ParseAnimationEvent(""));
            Assert.Equal(0, VMAvatar.ParseAnimationEvent(null));
            Assert.Equal(0, VMAvatar.ParseAnimationEvent("1.5"));
            Assert.Equal(0, VMAvatar.ParseAnimationEvent("99999"));
        }

        // ---- inheriting a neighbour's person data (E-35) -----------------------------------

        [Fact]
        public void InheritingKeepsThisAvatarsOwnPersonDataLength()
        {
            // The bug: PersonData was replaced with the neighbour's array outright and then seven
            // slots re-asserted over it. A neighbour array shorter than 101 - exactly what a save
            // with a short person-data table produces, and what VMAvatar.Tick's own length test
            // exists to cope with - left every later read above its length throwing.
            var mine = new short[101];
            mine[100] = 42; //a slot the neighbour's array does not reach
            var theirs = new short[12];
            theirs[0] = 7;

            VMAvatar.CopyPersonData(mine, theirs);

            Assert.Equal(101, mine.Length);
            Assert.Equal(7, mine[0]);
            Assert.Equal(42, mine[100]); //the tail above the neighbour's length is still there
        }

        [Fact]
        public void InheritingCopiesTheSlotsTheNeighbourActuallyHas()
        {
            var mine = new short[101];
            var theirs = new short[] { 1, 2, 3 };

            VMAvatar.CopyPersonData(mine, theirs);

            Assert.Equal(new short[] { 1, 2, 3 }, mine.Take(3));
        }

        [Fact]
        public void InheritingFromALongerNeighbourStopsAtOurOwnLength()
        {
            var mine = new short[4];
            var theirs = new short[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            VMAvatar.CopyPersonData(mine, theirs);

            Assert.Equal(new short[] { 1, 2, 3, 4 }, mine);
        }

        [Fact]
        public void InheritingFromNothingChangesNothing()
        {
            var mine = new short[] { 7, 8 };

            VMAvatar.CopyPersonData(mine, null);
            VMAvatar.CopyPersonData(null, new short[] { 1 });

            Assert.Equal(new short[] { 7, 8 }, mine);
        }

        // ---- the render blend (E-43) --------------------------------------------------------

        [Fact]
        public void ABlendWeightIsItsShareOfTheTotal()
        {
            Assert.Equal(0.25f, VMAvatar.BlendWeight(1f, 4f));
            Assert.Equal(1f, VMAvatar.BlendWeight(2f, 2f));
        }

        [Fact]
        public void ABlendWithNoWeightIsZeroAndNotNotANumber()
        {
            // The bug: 0/0 is NaN on the way into Animator.RenderFrame. During a turn (Turn()
            // leaves one animation) or straight after PreExit (which trims to a single standing
            // pose) the running total is 0 for any state whose own weight is 0.
            var weight = VMAvatar.BlendWeight(0f, 0f);

            Assert.Equal(0f, weight);
            Assert.False(float.IsNaN(weight));
        }

        [Fact]
        public void AZeroWeightStateInAMixedBlendContributesNothing()
        {
            Assert.Equal(0f, VMAvatar.BlendWeight(0f, 3f));
        }

        // ---- body strings (E-54) ------------------------------------------------------------

        [Fact]
        public void AListOfBodyStringAlternativesPicksOneOfThem()
        {
            var alternatives = new[] { "a", "b", "c" };

            for (ulong roll = 0; roll < 6; roll++)
            {
                Assert.True(VMAvatar.TryPickAlternative(alternatives, roll, out var pick));
                Assert.InRange(pick, 0, 2);
            }
        }

        [Fact]
        public void AListThatIsOnlySeparatorsHasNothingToPick()
        {
            // The bug: the raw string was tested for "" and the *filtered* array indexed, so ";"
            // and ";;" passed the test and then threw IndexOutOfRangeException inside a bare
            // catch that turned it into a half-applied avatar - skin tone changed, outfits not,
            // nothing logged.
            Assert.False(VMAvatar.TryPickAlternative(new string[0], 0, out var pick));
            Assert.Equal(-1, pick);

            Assert.False(VMAvatar.TryPickAlternative(null, 0, out pick));
        }
    }
}
