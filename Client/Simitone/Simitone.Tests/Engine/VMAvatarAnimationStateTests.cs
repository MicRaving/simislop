using FSO.SimAntics;
using FSO.SimAntics.Model;
using FSO.Vitaboy;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// An animation state whose <c>.anim</c> did not resolve, and an avatar with no appearance at
    /// all - the two ways a lot can arrive with nothing to animate - and the guards that keep either
    /// from being dereferenced.
    ///
    /// <para>
    /// <b>The crash.</b> <c>VMAvatar.Tick</c> ran
    /// <c>Animator.SilentFrameProgress(avatar.Avatar, state.Anim, (int)state.CurrentFrame)</c> over
    /// every state, and that method reads <c>animation.NumFrames</c>. <c>state.Anim</c> is
    /// <c>AvatarAnimations.Get(name + ".anim")</c>, which answers null for any animation this
    /// install does not have - so a save written against custom content that is no longer installed
    /// loaded a sim carrying a state with no animation, and the lot died on its first tick, on a
    /// line that is not inside the VM's exception funnel.
    /// </para>
    /// <para>
    /// <c>avatar.Avatar</c> was the other candidate and is not: <c>SilentFrameProgress</c> never
    /// reads its <c>avatar</c> argument at all. <c>Animator</c> is a type, not an instance, so it
    /// cannot be null either.
    /// </para>
    /// <para>
    /// These exercise the decisions as static calls over real <see cref="VMAnimationState"/> and
    /// <see cref="Animation"/> values. A test process has no content database, no graphics device
    /// and cannot build a lot, so it cannot construct a <see cref="VMAvatar"/> and tick one - which
    /// is why the guard is reachable from here at all.
    /// </para>
    /// <para>
    /// The class is in the non-parallel collection because it clears and reads
    /// <c>VMAvatar</c>'s report-once set, which is process-wide for the same reason
    /// <c>FSOEnvironment.UserDir</c> is: it is one object that every missing animation in every
    /// other test would see.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class VMAvatarAnimationStateTests
    {
        private static Animation MakeAnimation(string name, int frames = 30)
        {
            return new Animation
            {
                Name = name,
                NumFrames = frames,
                Motions = new AnimationMotion[0],
            };
        }

        /// <summary>
        /// A state that asked for a named animation and did not get one - the marshal path, and the
        /// one that can say which file is missing. A state built straight from null never knew a
        /// name and is the <see cref="VMAnimationState(Animation, bool)"/> path instead.
        /// </summary>
        private static VMAnimationState UnresolvedState(string name)
        {
            var state = new VMAnimationState(MakeAnimation(name), false);
            state.Anim = null; //as if the content database stopped answering
            Assert.False(state.HasAnimation);
            return state;
        }

        // ---- the crash: a state with no animation is dropped, not dereferenced -----------------

        [Fact]
        public void AStateWithNoAnimationIsDropped()
        {
            // The bug: this state went straight into Animator.SilentFrameProgress, which reads
            // animation.NumFrames, and took the tick with it.
            var states = new List<VMAnimationState>
            {
                new VMAnimationState(MakeAnimation("a2o-walk-grp1.anim"), false),
                new VMAnimationState(null, false), //the .anim this install does not have
                new VMAnimationState(MakeAnimation("a2o-stand-woman.anim"), false),
            };

            var dropped = VMAvatar.DropUnresolvedAnimations(states, _ => { });

            Assert.Equal(1, dropped);
            Assert.Equal(2, states.Count);
            Assert.All(states, s => Assert.True(s.HasAnimation));
        }

        [Fact]
        public void EveryStateThatSurvivesTheDropIsOneTheTickCanAdvance()
        {
            // The shape of the original crash, inverted: whatever the drop leaves behind is
            // handed to the exact call that used to throw. If anything could still get past it,
            // this is where it shows up.
            var states = new List<VMAnimationState>
            {
                new VMAnimationState(null, false),
                new VMAnimationState(MakeAnimation("a2o-walk-grp1.anim"), false),
                new VMAnimationState(MakeAnimation("a2o-walk-grp2.anim"), false),
            };
            var appearance = new SimAvatar((Skeleton)null);

            VMAvatar.DropUnresolvedAnimations(states, _ => { });

            foreach (var state in states)
            {
                var status = Animator.SilentFrameProgress(appearance, state.Anim, (int)state.CurrentFrame);
                Assert.Equal(AnimationStatus.IN_PROGRESS, status);
            }
        }

        [Fact]
        public void ANullStateEntryIsDroppedToo()
        {
            // Animations is a plain List that primitives clear and refill; nothing has ever put a
            // null in it, so this costs nothing to rule out.
            var states = new List<VMAnimationState>
            {
                null,
                new VMAnimationState(MakeAnimation("a2o-stand-woman.anim"), false),
            };

            Assert.Equal(1, VMAvatar.DropUnresolvedAnimations(states, _ => { }));
            Assert.Single(states);
        }

        [Fact]
        public void AnUnresolvedStateDoesNotBlockTheOneThatIsPlaying()
        {
            // Animations[0] is CurrentAnimationState, which VMAnimateSim compares against the
            // animation it is about to start. A dead state left at index 0 makes every request
            // look like a change of animation, forever - so it has to go rather than be skipped.
            var states = new List<VMAnimationState>
            {
                new VMAnimationState(null, false),
                new VMAnimationState(MakeAnimation("a2o-walk-grp1.anim"), false),
            };

            VMAvatar.DropUnresolvedAnimations(states, _ => { });

            Assert.Equal("a2o-walk-grp1.anim", states[0].Anim.Name);
        }

        // ---- the normal path, unchanged -------------------------------------------------------

        [Fact]
        public void AListWhereEverythingResolvedIsLeftExactlyAsItWas()
        {
            var stand = new VMAnimationState(MakeAnimation("a2o-stand-woman.anim"), false);
            var walk = new VMAnimationState(MakeAnimation("a2o-walk-grp1.anim"), false);
            walk.Weight = 0.66f;
            walk.CurrentFrame = 7.5f;
            var states = new List<VMAnimationState> { stand, walk };

            var dropped = VMAvatar.DropUnresolvedAnimations(states, msg => Assert.Fail("nothing is missing: " + msg));

            Assert.Equal(0, dropped);
            Assert.Equal(new[] { stand, walk }, states); //same states, same order, nothing rebuilt
            Assert.Equal(0.66f, walk.Weight);
            Assert.Equal(7.5f, walk.CurrentFrame);
        }

        [Fact]
        public void AnEmptyOrAbsentListIsFine()
        {
            Assert.Equal(0, VMAvatar.DropUnresolvedAnimations(null, _ => { }));
            Assert.Equal(0, VMAvatar.DropUnresolvedAnimations(new List<VMAnimationState>(), _ => { }));
        }

        // ---- reporting a missing animation once ----------------------------------------------

        [Fact]
        public void AMissingAnimationIsNamedInTheLog()
        {
            // A lot of Sims sharing one broken custom .anim, each ticking thirty times a second:
            // the player needs the name of the file once, and it has to be findable.
            VMAvatar.ResetMissingAnimationReports();
            try
            {
                var reported = new List<string>();
                var states = new List<VMAnimationState> { UnresolvedState("some-custom-walk.anim") };

                VMAvatar.DropUnresolvedAnimations(states, reported.Add);

                var single = Assert.Single(reported);
                Assert.Contains("some-custom-walk.anim", single);
                Assert.Contains("unaffected", single);
            }
            finally
            {
                VMAvatar.ResetMissingAnimationReports();
            }
        }

        [Fact]
        public void TheSameMissingAnimationIsOnlyReportedOnce()
        {
            VMAvatar.ResetMissingAnimationReports();
            try
            {
                Assert.True(VMAvatar.ShouldReportMissingAnimation("a2o-walk-grp1"));

                var reported = new List<string>();
                for (var i = 0; i < 5; i++)
                {
                    VMAvatar.DropUnresolvedAnimations(
                        new List<VMAnimationState> { UnresolvedState("a2o-walk-grp1") }, reported.Add);
                }

                Assert.Empty(reported);
            }
            finally
            {
                VMAvatar.ResetMissingAnimationReports();
            }
        }

        [Fact]
        public void DifferentMissingAnimationsAreEachReported()
        {
            VMAvatar.ResetMissingAnimationReports();
            try
            {
                var reported = new List<string>();
                VMAvatar.DropUnresolvedAnimations(
                    new List<VMAnimationState>
                    {
                        UnresolvedState("mod-a-walk.anim"),
                        UnresolvedState("mod-b-idle.anim"),
                    },
                    reported.Add);

                //A save can be missing more than one animation, and collapsing them into one line
                //would hide half of what is wrong.
                Assert.Equal(2, reported.Count);
                Assert.Contains(reported, msg => msg.Contains("mod-a-walk.anim"));
                Assert.Contains(reported, msg => msg.Contains("mod-b-idle.anim"));
            }
            finally
            {
                VMAvatar.ResetMissingAnimationReports();
            }
        }

        [Fact]
        public void AStateThatNeverKnewItsNameStillReportsSomething()
        {
            // The VMRoutingFrame.PlayAnim and default-carry path: the content database answered null,
            // so no name was ever recorded. "(unnamed)" rather than an empty log line.
            VMAvatar.ResetMissingAnimationReports();
            try
            {
                var reported = new List<string>();
                VMAvatar.DropUnresolvedAnimations(
                    new List<VMAnimationState> { new VMAnimationState(null, false) }, reported.Add);

                Assert.Contains("(unnamed)", Assert.Single(reported));
            }
            finally
            {
                VMAvatar.ResetMissingAnimationReports();
            }
        }

        [Fact]
        public void AnAbsentReportSinkStillDropsTheState()
        {
            // A null sink means "use the log file", not "say nothing": a guard whose whole point is
            // that a missing .anim is visible should not be one line of code away from being mute.
            // FileFormatLog has no sink in a test process and falls back to Trace, so this is a
            // line nobody sees.
            VMAvatar.ResetMissingAnimationReports();
            try
            {
                var states = new List<VMAnimationState> { UnresolvedState("default-sink.anim") };

                Assert.Equal(1, VMAvatar.DropUnresolvedAnimations(states, null));
                Assert.Empty(states);
            }
            finally
            {
                VMAvatar.ResetMissingAnimationReports();
            }
        }

        [Fact]
        public void TheMessageSaysWhatIsMissingAndThatItIsNotFatal()
        {
            Assert.Contains("a2o-walk-grp1.anim", VMAvatar.MissingAnimationMessage("a2o-walk-grp1"));
            Assert.Contains("unaffected", VMAvatar.MissingAnimationMessage("a2o-walk-grp1"));
            Assert.Contains("(unnamed)", VMAvatar.MissingAnimationMessage(null));
            Assert.Contains("(unnamed)", VMAvatar.MissingAnimationMessage(""));
        }

        // ---- an avatar with no appearance ------------------------------------------------------

        [Fact]
        public void ASimWithNoAppearanceSkipsTheAnimationUpdate()
        {
            // A pet, an NPC, a sim whose body and head did not load: Avatar is never built, and the
            // whole animation update is the one thing in the tick that reads it. The tick itself
            // does not stop - that is the difference between this and swallowing the exception.
            Assert.False(VMAvatar.CanAnimate(null));

            Assert.True(VMAvatar.CanAnimate(new SimAvatar((Skeleton)null)));
        }

        // ---- the carry animation, looked up by name like everything else ------------------------

        [Fact]
        public void ACarryStateWithNoAnimationIsCleared()
        {
            // VMAnimateSim tests CarryAnimationState == null to decide whether to set the default
            // carry up. A permanently-dead state in the slot would suppress that for the rest of
            // the sim's life, so it is cleared rather than kept.
            VMAvatar.ResetMissingAnimationReports();
            try
            {
                var reported = new List<string>();

                Assert.Null(VMAvatar.UsableCarryState(UnresolvedState("a2o-rarm-carry-loop.anim"), reported.Add));
                Assert.Contains("a2o-rarm-carry-loop.anim", Assert.Single(reported));
            }
            finally
            {
                VMAvatar.ResetMissingAnimationReports();
            }
        }

        [Fact]
        public void ACarryStateThatResolvedIsLeftAlone()
        {
            var carry = new VMAnimationState(MakeAnimation("a2o-rarm-carry-loop.anim"), false);

            var reported = new List<string>();
            var kept = VMAvatar.UsableCarryState(carry, reported.Add);

            Assert.Same(carry, kept);
            Assert.Empty(reported);
            Assert.Null(VMAvatar.UsableCarryState(null, reported.Add)); //no carry is not a problem
            Assert.Empty(reported);
        }

        // ---- the state itself -------------------------------------------------------------------

        [Fact]
        public void ABackwardsStateCanBeBuiltFromNothing()
        {
            // The bug: the constructor read Anim.NumFrames for a backwards state, and both
            // VMRoutingFrame.PlayAnim and VMAvatar's default carry hand it whatever the content
            // database returned - so a missing walk animation threw here, a frame before the one
            // in the report.
            var state = new VMAnimationState(null, true);

            Assert.False(state.HasAnimation);
            Assert.Equal(0f, state.CurrentFrame);
            Assert.False(state.PlayingBackwards);
            Assert.Empty(state.TimePropertyLists);
        }

        [Fact]
        public void ABackwardsStateStillStartsAtTheLastFrame()
        {
            // The normal path: the guard is only for the null case, and must not move the frame a
            // real backwards animation starts on.
            var state = new VMAnimationState(MakeAnimation("a2o-stand-woman.anim", 42), true);

            Assert.True(state.HasAnimation);
            Assert.True(state.PlayingBackwards);
            Assert.Equal(42f, state.CurrentFrame);
        }

        [Fact]
        public void AStateCanBeSavedAfterItsAnimationFailedToLoad()
        {
            // The bug on the other side: Save wrote Anim.Name, so the one place that could have
            // preserved a save with a missing animation was the place that threw on it.
            var state = new VMAnimationState(MakeAnimation("a2o-walk-grp1.anim"), false);
            state.Anim = null; //as if the content database stopped answering

            var marshal = state.Save();

            Assert.Equal("a2o-walk-grp1.anim", marshal.Anim);
        }

        [Fact]
        public void SavingAStateThatNeverHadAnAnimationWritesABlankName()
        {
            var marshal = new VMAnimationState(null, false).Save();

            //Not null: VMAnimationStateMarshal.Anim is written with BinaryWriter.WriteString, and
            //a null there is the next crash along.
            Assert.Equal("", marshal.Anim);
        }

        [Fact]
        public void AnAnimationWithNoMotionsHasNoTimeProperties()
        {
            // A state built from an Animation that never came off disk - which is what a caller
            // assembling one in memory has - has no Motions array at all, and reading it used to
            // throw from the constructor rather than from the tick.
            var state = new VMAnimationState(new Animation { Name = "in-memory.anim", NumFrames = 10 }, false);

            Assert.True(state.HasAnimation);
            Assert.Empty(state.TimePropertyLists);
        }
    }
}
