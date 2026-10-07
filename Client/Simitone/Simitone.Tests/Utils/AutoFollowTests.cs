using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Model;
using Simitone.Client.Utils;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the decisions behind Simitone's auto-follow control, and the engine slots it feeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The feature is the two <c>VMGenericTS1Call</c> calls Downtown ships -
    /// <c>SpawnDowntownDateOfPersonInTemp0</c> (19) and
    /// <c>SpawnTakeBackHomeDataOfPersonInTemp0</c> (20). Both are <em>primitives</em>: they read
    /// their target out of the calling thread's <c>TempRegisters[0]</c> as an in-world object ID,
    /// then read the target's neighbourhood inventory for a request, spawn the sim named in it,
    /// and consume the slot. The two differ only in which slot they read, which is why
    /// "which direction" and "which lot am I on" are two separate questions here.
    /// </para>
    /// <para>
    /// Two things in particular are worth locking in. First, the eligibility rule: offering to
    /// auto-follow yourself, a pet, a ghost, a sim who is not on the lot, a sim the engine
    /// cannot name by object ID, or a sim already travelling with the family are all ways of
    /// producing a button that does nothing. A sim that <em>does</em> have a request standing is
    /// the opposite problem, and the one this file leans on hardest: a sim is booked one way or
    /// the other, so queueing a direction has to withdraw the other, and the direction standing
    /// has to be the direction the panel names and the direction it keeps closed - otherwise a
    /// stale downtown request rides along with a fresh take-home one and the player can neither
    /// withdraw nor re-direct it. Second, the exact shape and replacement behaviour of the slot
    /// write, because that is the byte-level contract with the engine primitive: slot 10 means
    /// downtown, slot 11 means take home, the neighbour ID rides in <c>Count</c>, and the entry
    /// type must be 2. That last part includes refusing an ID that names no neighbour, which
    /// the engine would otherwise have nothing to spawn from.
    /// </para>
    /// <para>
    /// Everything here is exercised through plain data - a <see cref="AutoFollowSubject"/> and
    /// <see cref="List{T}"/> of <see cref="InventoryItem"/> - so no VM, lot, or graphics device
    /// is involved. The only production types touched are the two enums the slots and the calls
    /// are bound to, which is deliberate: binding to <see cref="VMGenericTS1CallMode"/> means a
    /// rename in the engine breaks this file at compile time instead of drifting silently.
    /// </para>
    /// </remarks>
    public sealed class AutoFollowTests
    {
        private static InventoryItem SimData(uint guid, ushort count)
        {
            return new InventoryItem() { Type = AutoFollow.SimDataInventoryType, GUID = guid, Count = count };
        }

        /// <summary>Every state the two engine slots can be in for a sim.</summary>
        private static readonly AutoFollowAction[] PendingStates =
        {
            AutoFollowAction.None,
            AutoFollowAction.InviteToDowntown,
            AutoFollowAction.TakeHome,
            AutoFollowAction.Both,
        };

        /// <summary>Every combination of the five yes/no facts about a sim.</summary>
        private const int SubjectCombinations = 32;

        /// <summary>
        /// A sim described by a five-bit mask (self, pet, ghost, on the lot, travelling) with
        /// <paramref name="pending"/> standing in its slots, so the total tests can walk the whole
        /// cross-product without a VM.
        /// </summary>
        private static AutoFollowSubject Subject(int mask, AutoFollowAction pending)
        {
            return new AutoFollowSubject()
            {
                IsSelf = (mask & 1) != 0,
                IsPet = (mask & 2) != 0,
                IsGhost = (mask & 4) != 0,
                IsOnLot = (mask & 8) != 0,
                InTravellingFamily = (mask & 16) != 0,
                Pending = pending,
            };
        }

        #region the engine contract

        [Fact]
        public void TheSlotsAreTheOnesTheEngineReads()
        {
            // VMGenericTS1Call initialises its `inventoryInd` local to 10 and sets it to 11
            // immediately before the take-home case jumps into the shared body. These two
            // numbers are the whole difference between call 19 and call 20.
            Assert.Equal(10u, AutoFollow.DowntownDateSlot);
            Assert.Equal(11u, AutoFollow.TakeBackHomeSlot);
        }

        [Fact]
        public void TheSlotsAreNotTheTravelMotiveSlots()
        {
            // ChangeToLotInTemp0 writes GUIDs 0-6 for motives and 7-9 for the clock and family
            // size. If either auto-follow slot ever collided with one of those, queueing a
            // request would silently write a neighbour ID over a motive.
            for (uint slot = 0; slot < AutoFollow.DowntownDateSlot; slot++)
            {
                Assert.Equal(AutoFollowAction.None, AutoFollow.ActionForSlot(slot));
            }
        }

        [Fact]
        public void TheEntryTypeIsTheSimDataTypeTheEngineFiltersOn()
        {
            // the engine's lookup is `x.Type == 2 && x.GUID == inventoryInd`
            Assert.Equal(2, AutoFollow.SimDataInventoryType);
        }

        [Fact]
        public void TheDirectionsAreBoundToTheEngineCalls()
        {
            // If FreeSO renumbers or removes either call this stops compiling, which is the
            // point: the two buttons claim to be these two calls, so they must actually be.
            Assert.Equal(VMGenericTS1CallMode.SpawnDowntownDateOfPersonInTemp0, AutoFollow.DowntownCall);
            Assert.Equal(VMGenericTS1CallMode.SpawnTakeBackHomeDataOfPersonInTemp0, AutoFollow.TakeHomeCall);
        }

        [Fact]
        public void ThePersonDataFieldIsTheOneTs1ReservedForThis()
        {
            Assert.Equal(VMPersonDataVariable.TS1AutoFollow, AutoFollow.FollowPersonData);
        }

        #endregion

        #region slot <-> action

        [Theory]
        [InlineData(AutoFollowAction.InviteToDowntown, 10u)]
        [InlineData(AutoFollowAction.TakeHome, 11u)]
        public void EachDirectionHasItsOwnSlot(AutoFollowAction action, uint expected)
        {
            uint slot;
            Assert.True(AutoFollow.TrySlotFor(action, out slot));
            Assert.Equal(expected, slot);
        }

        [Theory]
        [InlineData(AutoFollowAction.None)]
        [InlineData(AutoFollowAction.Both)]
        public void NeitherNoDirectionNorBothResolvesToASlot(AutoFollowAction action)
        {
            // Deliberately not a plain lookup returning 0: slot 0 holds bladder, so "either
            // direction" resolving to 0 would write a motive as a neighbour ID.
            uint slot;
            Assert.False(AutoFollow.TrySlotFor(action, out slot));
        }

        [Fact]
        public void TheTwoDirectionsNeverShareASlot()
        {
            uint downtown, home;
            Assert.True(AutoFollow.TrySlotFor(AutoFollowAction.InviteToDowntown, out downtown));
            Assert.True(AutoFollow.TrySlotFor(AutoFollowAction.TakeHome, out home));
            Assert.NotEqual(downtown, home);
        }

        [Theory]
        [InlineData(10u, AutoFollowAction.InviteToDowntown)]
        [InlineData(11u, AutoFollowAction.TakeHome)]
        public void EachSlotMapsBackToItsDirection(uint slot, AutoFollowAction expected)
        {
            Assert.Equal(expected, AutoFollow.ActionForSlot(slot));
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(9u)]
        [InlineData(12u)]
        [InlineData(99u)]
        [InlineData(uint.MaxValue)]
        public void AnyOtherSlotIsNotAnAutoFollowSlot(uint slot)
        {
            Assert.Equal(AutoFollowAction.None, AutoFollow.ActionForSlot(slot));
        }

        [Theory]
        [InlineData(AutoFollowAction.InviteToDowntown)]
        [InlineData(AutoFollowAction.TakeHome)]
        public void EveryDirectionSurvivesASlotRoundTrip(AutoFollowAction action)
        {
            uint slot;
            Assert.True(AutoFollow.TrySlotFor(action, out slot));
            Assert.Equal(action, AutoFollow.ActionForSlot(slot));
        }

        [Fact]
        public void OnlyTheTwoSingleDirectionsCountAsSingle()
        {
            Assert.True(AutoFollow.IsSingle(AutoFollowAction.InviteToDowntown));
            Assert.True(AutoFollow.IsSingle(AutoFollowAction.TakeHome));
            Assert.False(AutoFollow.IsSingle(AutoFollowAction.Both));
            Assert.False(AutoFollow.IsSingle(AutoFollowAction.None));
        }

        #endregion

        #region the opposite direction

        [Theory]
        [InlineData(AutoFollowAction.InviteToDowntown, AutoFollowAction.TakeHome)]
        [InlineData(AutoFollowAction.TakeHome, AutoFollowAction.InviteToDowntown)]
        public void EachDirectionHasExactlyOneOpposite(AutoFollowAction action, AutoFollowAction expected)
        {
            Assert.Equal(expected, AutoFollow.Opposite(action));
        }

        [Theory]
        [InlineData(AutoFollowAction.InviteToDowntown)]
        [InlineData(AutoFollowAction.TakeHome)]
        public void TheOppositeOfTheOppositeIsTheOriginal(AutoFollowAction action)
        {
            // the read and the write path both go through this, so a request withdrawn to make way
            // for the other direction is undone by the next write the same way round
            Assert.Equal(action, AutoFollow.Opposite(AutoFollow.Opposite(action)));
        }

        [Theory]
        [InlineData(AutoFollowAction.None)]
        [InlineData(AutoFollowAction.Both)]
        public void ADirectionSetHasNoSingleOpposite(AutoFollowAction action)
        {
            // "either" and "both" are not one direction, so neither has the other as an opposite
            Assert.Equal(AutoFollowAction.None, AutoFollow.Opposite(action));
        }

        #endregion

        #region eligibility

        [Fact]
        public void AnOrdinarySimCanBeSentEitherWay()
        {
            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.Both, AutoFollow.Available(AutoFollowSubject.Ordinary, out reason));
            Assert.Equal(AutoFollowReason.Allowed, reason);
        }

        [Fact]
        public void OrdinaryReturnsAFreshSubjectEachTime()
        {
            // Callers mutate what they are given; a shared instance would let one test's flags
            // leak into the next.
            var first = AutoFollowSubject.Ordinary;
            first.IsSelf = true;
            Assert.False(AutoFollowSubject.Ordinary.IsSelf);
        }

        [Fact]
        public void YouCannotAutoFollowYourself()
        {
            var subject = AutoFollowSubject.Ordinary;
            subject.IsSelf = true;

            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.None, AutoFollow.Available(subject, out reason));
            Assert.Equal(AutoFollowReason.IsSelf, reason);
        }

        [Fact]
        public void APetCannotBeAutoFollowed()
        {
            // the phone book - the only interaction the engine routes into these calls - filters
            // pets out, and a pet carries no neighbour record holding a request
            var subject = AutoFollowSubject.Ordinary;
            subject.IsPet = true;

            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.None, AutoFollow.Available(subject, out reason));
            Assert.Equal(AutoFollowReason.IsPet, reason);
        }

        [Fact]
        public void AGhostIsNotTravelling()
        {
            var subject = AutoFollowSubject.Ordinary;
            subject.IsGhost = true;

            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.None, AutoFollow.Available(subject, out reason));
            Assert.Equal(AutoFollowReason.IsGhost, reason);
        }

        [Fact]
        public void ASimWithNoBodyOnTheLotCannotBeAutoFollowed()
        {
            // both calls read TempRegisters[0] as an in-world object ID; no body, no request
            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.None, AutoFollow.Available(new AutoFollowSubject(), out reason));
            Assert.Equal(AutoFollowReason.NotOnLot, reason);
        }

        [Fact]
        public void ASimAlreadyTravellingWithTheFamilyDoesNotNeedAsking()
        {
            // ChangeToLotInTemp0 adds every member of the active family to the travelling party
            // by itself, so inviting one is a no-op
            var subject = AutoFollowSubject.Ordinary;
            subject.InTravellingFamily = true;

            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.None, AutoFollow.Available(subject, out reason));
            Assert.Equal(AutoFollowReason.AlreadyTravelling, reason);
        }

        [Fact]
        public void AQueuedRequestLeavesTheOtherDirectionOnOffer()
        {
            // A sim booked one way is not a sim with nothing left to do. Closing both buttons
            // would leave the request unable to be withdrawn or re-directed at all, which is what
            // a control that only knows "something is pending" always ends up doing.
            foreach (var pending in new[] { AutoFollowAction.InviteToDowntown, AutoFollowAction.TakeHome })
            {
                var subject = AutoFollowSubject.Ordinary;
                subject.Pending = pending;

                AutoFollowReason reason;
                Assert.Equal(AutoFollow.Opposite(pending), AutoFollow.Available(subject, out reason));
                Assert.Equal(AutoFollowReason.PendingInOtherDirection, reason);
            }
        }

        [Fact]
        public void TheDirectionAlreadyQueuedIsNeverOfferedBack()
        {
            // Re-queueing the direction already standing would only rewrite the request the player
            // has, so the queued button is the greyed one - and the live button is the withdrawal.
            foreach (var pending in new[] { AutoFollowAction.InviteToDowntown, AutoFollowAction.TakeHome })
            {
                var subject = AutoFollowSubject.Ordinary;
                subject.Pending = pending;

                AutoFollowReason reason;
                var available = AutoFollow.Available(subject, out reason);
                Assert.Equal(AutoFollowAction.None, available & pending);
            }
        }

        [Fact]
        public void ASimQueuedBothWaysStillHasBothButtonsToCollapseItWith()
        {
            // the one case where a queued direction is offered back: there is no single request to
            // protect, and either button is the fix
            var subject = AutoFollowSubject.Ordinary;
            subject.Pending = AutoFollowAction.Both;

            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.Both, AutoFollow.Available(subject, out reason));
        }

        [Fact]
        public void AQueuedRequestIsStillReportedAsQueued()
        {
            // the panel has to be able to say which direction is standing, not just that one is
            var subject = AutoFollowSubject.Ordinary;
            subject.Pending = AutoFollowAction.TakeHome;

            AutoFollowReason reason;
            AutoFollow.Available(subject, out reason);
            Assert.Equal(AutoFollowReason.PendingInOtherDirection, reason);
        }

        [Fact]
        public void ASimCarryingARequestInBothSlotsKeepsBothButtons()
        {
            // only data this control did not write can leave a request in each slot - an older
            // build's queueing, or hand-edited neighbourhood data. Either button collapses it
            // back to one request, so neither may be closed or the sim is stuck for good.
            var subject = AutoFollowSubject.Ordinary;
            subject.Pending = AutoFollowAction.Both;

            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.Both, AutoFollow.Available(subject, out reason));
            Assert.Equal(AutoFollowReason.AlreadyPending, reason);
        }

        [Fact]
        public void BeingOffTheLotBeatsHavingARequestWaiting()
        {
            // A sim with no body on the lot cannot be named by object ID at all, so a request
            // sitting in their slot is unreachable however new it is. The more fundamental
            // objection is the one that should be reported.
            var subject = AutoFollowSubject.Ordinary;
            subject.IsOnLot = false;
            subject.Pending = AutoFollowAction.TakeHome;

            AutoFollowReason reason;
            AutoFollow.Available(subject, out reason);
            Assert.Equal(AutoFollowReason.NotOnLot, reason);
        }

        [Fact]
        public void BeingYouBeatsEverythingElse()
        {
            // "that is you" is reported ahead of any bookkeeping objection
            var subject = AutoFollowSubject.Ordinary;
            subject.IsSelf = true;
            subject.IsPet = true;
            subject.IsGhost = true;
            subject.InTravellingFamily = true;
            subject.Pending = AutoFollowAction.Both;

            AutoFollowReason reason;
            AutoFollow.Available(subject, out reason);
            Assert.Equal(AutoFollowReason.IsSelf, reason);
        }

        [Fact]
        public void TheDecisionIsTotalAndAlwaysExplainsItself()
        {
            // Every combination of the five facts about a sim, crossed with every state the two
            // slots can be in, has to produce a coherent pair: either a non-empty set of
            // directions, or nothing and a specific complaint. A gap here would be a state the
            // UI cannot render.
            var refusals = 0;
            var offers = 0;

            for (int mask = 0; mask < SubjectCombinations; mask++)
            {
                foreach (var pending in PendingStates)
                {
                    AutoFollowReason reason;
                    var result = AutoFollow.Available(Subject(mask, pending), out reason);

                    if (result == AutoFollowAction.None)
                    {
                        // a refusal must say why, and the reason must be one of the objections
                        Assert.NotEqual(AutoFollowReason.Allowed, reason);
                        Assert.True(reason == AutoFollowReason.IsSelf || reason == AutoFollowReason.IsPet
                            || reason == AutoFollowReason.IsGhost || reason == AutoFollowReason.NotOnLot
                            || reason == AutoFollowReason.AlreadyTravelling, $"unexpected refusal {reason}");
                        refusals++;
                    }
                    else
                    {
                        Assert.True(AutoFollow.IsSingle(result) || result == AutoFollowAction.Both);
                        // the one thing that must never happen is offering a direction that is
                        // already queued: that button would only rewrite the request already there
                        if (AutoFollow.IsSingle(pending)) Assert.Equal(AutoFollowAction.None, result & pending);
                        offers++;
                    }
                }
            }

            // four of the 32 x 4 states offer anything at all, and all four are the same sim: on
            // the lot, alive, human and not travelling with the family. With nothing queued it is
            // offered both directions, with one queued it is offered the other, and with both
            // queued it is offered both so the player can collapse it back to one
            Assert.Equal(4, offers);
            Assert.Equal((SubjectCombinations * PendingStates.Length) - 4, refusals);
        }

        [Fact]
        public void OnlyTheEligibleSimIsOfferedTheDirectionsNotAlreadyQueued()
        {
            // Everything the rule does, stated as one expected value per state: a sim that is on
            // the lot, alive, human and not travelling with the family is offered every direction
            // that is not already queued, and anything else is offered nothing at all. A sim
            // queued both ways is the exception - there is no unqueued direction left, so both are
            // offered, and either press collapses it back to one.
            for (int mask = 0; mask < SubjectCombinations; mask++)
            {
                foreach (var pending in PendingStates)
                {
                    var subject = Subject(mask, pending);
                    var eligible = subject.IsOnLot && !subject.IsSelf && !subject.IsPet
                        && !subject.IsGhost && !subject.InTravellingFamily;
                    var offered = eligible
                        ? (pending == AutoFollowAction.Both ? AutoFollowAction.Both : AutoFollowAction.Both & ~pending)
                        : AutoFollowAction.None;

                    AutoFollowReason reason;
                    Assert.Equal(offered, AutoFollow.Available(subject, out reason));
                }
            }
        }

        [Fact]
        public void ASimIsNeverStrandedWithNothingToPress()
        {
            // The defect this whole file exists for: a request that cannot be withdrawn or
            // re-directed is a request the player can only live with. Whenever the only thing
            // standing in the way is bookkeeping, something has to be on offer.
            var stranded = 0;

            for (int mask = 0; mask < SubjectCombinations; mask++)
            {
                foreach (var pending in PendingStates)
                {
                    var subject = Subject(mask, pending);
                    var eligible = subject.IsOnLot && !subject.IsSelf && !subject.IsPet
                        && !subject.IsGhost && !subject.InTravellingFamily;

                    AutoFollowReason reason;
                    var result = AutoFollow.Available(subject, out reason);
                    if (eligible && result == AutoFollowAction.None) stranded++;
                }
            }

            Assert.Equal(0, stranded);
        }

        [Fact]
        public void ANullSubjectIsRefusedRatherThanThrowing()
        {
            // the panel is polled from Update; a frame where the sim resolved to nothing must
            // not take the lot down
            AutoFollowReason reason;
            Assert.Equal(AutoFollowAction.None, AutoFollow.Available(null, out reason));
            Assert.Equal(AutoFollowReason.NotOnLot, reason);
        }

        #endregion

        #region whether the control is up at all

        [Fact]
        public void TheControlIsOnlyUpForASimItHasSomethingToPress()
        {
            // The rule the panel's visibility hangs on: a panel whose buttons are all dead has
            // nothing to say, so it is not shown. Checked against the eligibility rule itself
            // rather than a list of cases, so the two cannot drift - the control appears exactly
            // when AutoFollow.Available produced a direction.
            for (int mask = 0; mask < SubjectCombinations; mask++)
            {
                foreach (var pending in PendingStates)
                {
                    var offer = AutoFollow.Offer(Subject(mask, pending));

                    AutoFollowReason reason;
                    var available = AutoFollow.Available(Subject(mask, pending), out reason);

                    Assert.Equal(available != AutoFollowAction.None, offer.IsShown);
                    Assert.Equal(available, offer.Available);
                    Assert.Equal(reason, offer.Reason);
                }
            }
        }

        [Fact]
        public void TheInertStatesAreTheOnesTheReportIsAbout()
        {
            // Each of these used to raise a heading, a line of prose and two dead buttons. They
            // are the states a cursor rests on constantly - yourself, the family pet, whoever is
            // coming along with you, the postman on a lot with no neighbourhood loaded - and none
            // of them is anything the player can act on.
            var refusals = new List<AutoFollowSubject>()
            {
                // "that is you"
                new AutoFollowSubject() { IsOnLot = true, IsSelf = true },
                // a dog or a cat
                new AutoFollowSubject() { IsOnLot = true, IsPet = true },
                // dead
                new AutoFollowSubject() { IsOnLot = true, IsGhost = true },
                // ChangeToLotInTemp0 already takes them along
                new AutoFollowSubject() { IsOnLot = true, InTravellingFamily = true },
                // no body on the lot, so no object ID the engine's call could be pointed at
                new AutoFollowSubject() { IsOnLot = false },
            };

            foreach (var subject in refusals)
            {
                var offer = AutoFollow.Offer(subject);
                Assert.False(offer.IsShown, $"raised the control for {offer.Reason}");
                Assert.Equal(AutoFollowAction.None, offer.Available);
                Assert.NotEqual(AutoFollowReason.Allowed, offer.Reason);
            }
        }

        [Fact]
        public void ANullSubjectIsNeitherOfferedNorShown()
        {
            // polled every frame, so a frame where the sim resolved to nothing must come back
            // as "show nothing" rather than throwing
            var offer = AutoFollow.Offer(null);
            Assert.False(offer.IsShown);
            Assert.Equal(AutoFollowAction.None, offer.Available);
            Assert.Equal(AutoFollowReason.NotOnLot, offer.Reason);
        }

        [Fact]
        public void AnEligibleSimIsStillOfferedBothDirectionsAndShown()
        {
            // the other side of the rule: hiding the inert panel must not have cost the feature
            var offer = AutoFollow.Offer(AutoFollowSubject.Ordinary);

            Assert.True(offer.IsShown);
            Assert.Equal(AutoFollowAction.Both, offer.Available);
            Assert.Equal(AutoFollowReason.Allowed, offer.Reason);
        }

        [Fact]
        public void AShownOfferAlwaysLeadsWithSomethingPressable()
        {
            // Being up has to mean there is a button to press, on either lot. A panel that is
            // visible but whose preferred direction is None would be the same dead end wearing a
            // smaller hat.
            for (int mask = 0; mask < SubjectCombinations; mask++)
            {
                foreach (var pending in PendingStates)
                {
                    var offer = AutoFollow.Offer(Subject(mask, pending));
                    if (!offer.IsShown) continue;

                    Assert.NotEqual(AutoFollowAction.None, offer.Preferred(false));
                    Assert.NotEqual(AutoFollowAction.None, offer.Preferred(true));
                }
            }
        }

        [Fact]
        public void HidingTheInertPanelCannotStrandAQueuedRequest()
        {
            // The withdrawal path must survive the new rule. A sim with a request standing always
            // has the other direction on offer, so it is always shown - which is what keeps
            // "queued but unreachable" from being the new failure mode.
            foreach (var standing in PendingStates)
            {
                var subject = AutoFollowSubject.Ordinary;
                subject.Pending = standing;

                var offer = AutoFollow.Offer(subject);
                Assert.True(offer.IsShown, $"{standing} was queued and the control is not there to withdraw it");
            }
        }

        [Fact]
        public void WhatIsOfferedIsUnchangedByWhetherTheControlIsShown()
        {
            // Visibility is a presentation decision, not a filter on the rule: the offer for a
            // shown sim is exactly what Available said, and the sim's own data is left untouched
            // by being asked about.
            var subject = AutoFollowSubject.Ordinary;
            subject.Pending = AutoFollowAction.InviteToDowntown;

            var before = subject.Pending;
            var offer = AutoFollow.Offer(subject);

            Assert.Equal(AutoFollowAction.TakeHome, offer.Available);
            Assert.Equal(before, subject.Pending);
        }

        #endregion

        #region which direction leads

        [Fact]
        public void AwayFromDowntownThePrimaryIsAnInvitationDowntown()
        {
            Assert.Equal(AutoFollowAction.InviteToDowntown, AutoFollow.Preferred(AutoFollowAction.Both, false));
        }

        [Fact]
        public void OnDowntownThePrimaryIsTakingThemHome()
        {
            // a sim standing on Downtown is away from home, so bringing them back is the
            // meaningful move
            Assert.Equal(AutoFollowAction.TakeHome, AutoFollow.Preferred(AutoFollowAction.Both, true));
        }

        [Fact]
        public void AnUnavailableSimHasNoPrimaryDirection()
        {
            Assert.Equal(AutoFollowAction.None, AutoFollow.Preferred(AutoFollowAction.None, false));
            Assert.Equal(AutoFollowAction.None, AutoFollow.Preferred(AutoFollowAction.None, true));
        }

        [Fact]
        public void ASingleDirectionIsUsedWhicheverLotYouAreOn()
        {
            // if the lot implies the direction that is not on offer, the other one is all
            // there is - it is never "nothing"
            Assert.Equal(AutoFollowAction.TakeHome, AutoFollow.Preferred(AutoFollowAction.TakeHome, false));
            Assert.Equal(AutoFollowAction.TakeHome, AutoFollow.Preferred(AutoFollowAction.TakeHome, true));
            Assert.Equal(AutoFollowAction.InviteToDowntown, AutoFollow.Preferred(AutoFollowAction.InviteToDowntown, false));
            Assert.Equal(AutoFollowAction.InviteToDowntown, AutoFollow.Preferred(AutoFollowAction.InviteToDowntown, true));
        }

        [Fact]
        public void ThePrimaryIsAlwaysOneOfTheDirectionsOnOffer()
        {
            var sets = new[]
            {
                AutoFollowAction.None,
                AutoFollowAction.InviteToDowntown,
                AutoFollowAction.TakeHome,
                AutoFollowAction.Both,
            };

            foreach (var available in sets)
            {
                foreach (var downtown in new[] { false, true })
                {
                    var preferred = AutoFollow.Preferred(available, downtown);
                    if (available == AutoFollowAction.None)
                    {
                        Assert.Equal(AutoFollowAction.None, preferred);
                    }
                    else
                    {
                        Assert.True(AutoFollow.IsSingle(preferred));
                        Assert.NotEqual(AutoFollowAction.None, available & preferred);
                    }
                }
            }
        }

        [Fact]
        public void AQueuedRequestDoesNotStealThePrimaryFromTheOtherDirection()
        {
            // a sim booked to downtown is only offered take home, so take home leads, whichever
            // lot the player is on - the queued direction is the greyed button
            var subject = AutoFollowSubject.Ordinary;
            subject.Pending = AutoFollowAction.InviteToDowntown;

            AutoFollowReason reason;
            var available = AutoFollow.Available(subject, out reason);
            Assert.Equal(AutoFollowAction.TakeHome, AutoFollow.Preferred(available, false));
            Assert.Equal(AutoFollowAction.TakeHome, AutoFollow.Preferred(available, true));
        }

        #endregion

        #region finding a standing request

        [Fact]
        public void NoInventoryMeansNoRequest()
        {
            AutoFollowAction action;
            short nid;
            Assert.False(AutoFollow.TryFindRequest(null, out action, out nid));
            Assert.Equal(AutoFollowAction.None, action);
        }

        [Fact]
        public void AnInventoryWithOnlyMotivesHasNoRequest()
        {
            // GUIDs 0-6 are the motives the travel code writes; none of them is an auto-follow
            // slot, so a sim that has just travelled has nothing pending
            var inventory = new List<InventoryItem>();
            for (uint guid = 0; guid < AutoFollow.DowntownDateSlot; guid++) inventory.Add(SimData(guid, 1));

            AutoFollowAction action;
            short nid;
            Assert.False(AutoFollow.TryFindRequest(inventory, out action, out nid));
        }

        [Fact]
        public void ADowntownRequestIsFoundWithItsNeighbourId()
        {
            var inventory = new List<InventoryItem> { SimData(AutoFollow.DowntownDateSlot, 42) };

            AutoFollowAction action;
            short nid;
            Assert.True(AutoFollow.TryFindRequest(inventory, out action, out nid));
            Assert.Equal(AutoFollowAction.InviteToDowntown, action);
            Assert.Equal(42, nid);
        }

        [Fact]
        public void ATakeHomeRequestIsFoundWithItsNeighbourId()
        {
            var inventory = new List<InventoryItem> { SimData(AutoFollow.TakeBackHomeSlot, 7) };

            AutoFollowAction action;
            short nid;
            Assert.True(AutoFollow.TryFindRequest(inventory, out action, out nid));
            Assert.Equal(AutoFollowAction.TakeHome, action);
            Assert.Equal(7, nid);
        }

        [Fact]
        public void ADowntownRequestIsReportedAheadOfATakeHomeOne()
        {
            // matches the order the engine's own first-match lookup would find them in
            var inventory = new List<InventoryItem>()
            {
                SimData(AutoFollow.TakeBackHomeSlot, 7),
                SimData(AutoFollow.DowntownDateSlot, 42),
            };

            AutoFollowAction action;
            short nid;
            Assert.True(AutoFollow.TryFindRequest(inventory, out action, out nid));
            Assert.Equal(AutoFollowAction.InviteToDowntown, action);
            Assert.Equal(42, nid);
        }

        [Fact]
        public void OrderInTheListDoesNotChangeWhichRequestIsReported()
        {
            var downtownFirst = new List<InventoryItem>()
            {
                SimData(AutoFollow.DowntownDateSlot, 42),
                SimData(AutoFollow.TakeBackHomeSlot, 7),
            };
            var takeHomeFirst = new List<InventoryItem>()
            {
                SimData(AutoFollow.TakeBackHomeSlot, 7),
                SimData(AutoFollow.DowntownDateSlot, 42),
            };

            AutoFollowAction action;
            short nid;
            AutoFollow.TryFindRequest(downtownFirst, out action, out nid);
            Assert.Equal(AutoFollowAction.InviteToDowntown, action);
            Assert.Equal(42, nid);

            AutoFollow.TryFindRequest(takeHomeFirst, out action, out nid);
            Assert.Equal(AutoFollowAction.InviteToDowntown, action);
            Assert.Equal(42, nid);
        }

        [Fact]
        public void AnEntryOfTheWrongTypeIsNotARequest()
        {
            // the engine filters on Type == 2 as well as on the GUID; an item that happens to
            // carry GUID 10 in some other type is not one
            var inventory = new List<InventoryItem>()
            {
                new InventoryItem() { Type = 1, GUID = AutoFollow.DowntownDateSlot, Count = 42 },
            };

            AutoFollowAction action;
            short nid;
            Assert.False(AutoFollow.TryFindRequest(inventory, out action, out nid));
        }

        [Fact]
        public void ANullEntryInTheListIsSkippedRatherThanThrowing()
        {
            var inventory = new List<InventoryItem>()
            {
                null,
                SimData(AutoFollow.TakeBackHomeSlot, 7),
            };

            AutoFollowAction action;
            short nid;
            Assert.True(AutoFollow.TryFindRequest(inventory, out action, out nid));
            Assert.Equal(AutoFollowAction.TakeHome, action);
            Assert.Equal(7, nid);
        }

        [Fact]
        public void AnEmptyInventoryHasNoRequest()
        {
            AutoFollowAction action;
            short nid;
            Assert.False(AutoFollow.TryFindRequest(new List<InventoryItem>(), out action, out nid));
        }

        [Fact]
        public void AQueuedRequestIsImmediatelyFindable()
        {
            // one fresh inventory per direction: TryFindRequest deliberately reports a sim
            // carrying *both* requests as a downtown one (see
            // ADowntownRequestIsReportedAheadOfATakeHomeOne), so a single list holding both would
            // only ever find the first. Queueing keeps a sim to one request, so this only
            // overlaps for a list built by hand below.
            foreach (var action in new[] { AutoFollowAction.InviteToDowntown, AutoFollowAction.TakeHome })
            {
                var inventory = new List<InventoryItem>();
                Assert.True(AutoFollow.TryQueueRequest(inventory, action, 13));

                AutoFollowAction found;
                short nid;
                Assert.True(AutoFollow.TryFindRequest(inventory, out found, out nid));
                Assert.Equal(action, found);
                Assert.Equal(13, nid);
            }
        }

        #endregion

        #region which directions are queued

        [Fact]
        public void NothingQueuedIsNoDirections()
        {
            Assert.Equal(AutoFollowAction.None, AutoFollow.FindPendingActions(null));
            Assert.Equal(AutoFollowAction.None, AutoFollow.FindPendingActions(new List<InventoryItem>()));
        }

        [Fact]
        public void MotivesAndOtherSlotsAreNotPending()
        {
            // GUIDs 0-9 hold the travel code's motives, clock and family size; only 10 and 11 are
            // auto-follow slots, so a sim that has just travelled has nothing pending
            var inventory = new List<InventoryItem>();
            for (uint guid = 0; guid < AutoFollow.DowntownDateSlot; guid++) inventory.Add(SimData(guid, 1));
            inventory.Add(new InventoryItem() { Type = 1, GUID = AutoFollow.DowntownDateSlot, Count = 42 });
            inventory.Add(SimData(AutoFollow.TakeBackHomeSlot + 1, 3));

            Assert.Equal(AutoFollowAction.None, AutoFollow.FindPendingActions(inventory));
        }

        [Theory]
        [InlineData(AutoFollow.DowntownDateSlot, AutoFollowAction.InviteToDowntown)]
        [InlineData(AutoFollow.TakeBackHomeSlot, AutoFollowAction.TakeHome)]
        public void OneQueuedRequestIsThatOneDirection(uint slot, AutoFollowAction expected)
        {
            var inventory = new List<InventoryItem> { SimData(slot, 42) };
            Assert.Equal(expected, AutoFollow.FindPendingActions(inventory));
        }

        [Fact]
        public void ARequestInEachSlotIsBothDirections()
        {
            // the state TryQueueRequest cannot produce; a panel that took this for a single
            // direction would name the wrong one
            var inventory = new List<InventoryItem>()
            {
                SimData(AutoFollow.DowntownDateSlot, 42),
                SimData(AutoFollow.TakeBackHomeSlot, 7),
            };

            Assert.Equal(AutoFollowAction.Both, AutoFollow.FindPendingActions(inventory));
        }

        [Fact]
        public void ARequestInEachSlotIsStillReportedInEitherOrder()
        {
            var downtownFirst = new List<InventoryItem>()
            {
                SimData(AutoFollow.DowntownDateSlot, 42),
                SimData(AutoFollow.TakeBackHomeSlot, 7),
            };
            var takeHomeFirst = new List<InventoryItem>()
            {
                SimData(AutoFollow.TakeBackHomeSlot, 7),
                SimData(AutoFollow.DowntownDateSlot, 42),
            };

            Assert.Equal(AutoFollowAction.Both, AutoFollow.FindPendingActions(downtownFirst));
            Assert.Equal(AutoFollowAction.Both, AutoFollow.FindPendingActions(takeHomeFirst));
        }

        [Fact]
        public void ANullEntryIsSkippedRatherThanThrowing()
        {
            var inventory = new List<InventoryItem>() { null, SimData(AutoFollow.TakeBackHomeSlot, 7) };
            Assert.Equal(AutoFollowAction.TakeHome, AutoFollow.FindPendingActions(inventory));
        }

        [Fact]
        public void WhatWasQueuedIsWhatIsReportedAsQueued()
        {
            // the round trip the panel depends on: what the write path wrote is what the read
            // path names, whichever direction was asked for
            foreach (var action in new[] { AutoFollowAction.InviteToDowntown, AutoFollowAction.TakeHome })
            {
                var inventory = new List<InventoryItem>();
                Assert.True(AutoFollow.TryQueueRequest(inventory, action, 13));
                Assert.Equal(action, AutoFollow.FindPendingActions(inventory));
            }
        }

        #endregion

        #region queueing a request

        [Fact]
        public void AQueuedRequestIsShapedTheWayTheEngineReadsIt()
        {
            // `x.Type == 2 && x.GUID == inventoryInd` with the neighbour ID cast back out of
            // Count. If any of the three moved, the primitive would either miss the request or
            // spawn the wrong sim.
            var inventory = new List<InventoryItem>();
            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 42));

            var item = Assert.Single(inventory);
            Assert.Equal(2, item.Type);
            Assert.Equal(AutoFollow.DowntownDateSlot, item.GUID);
            Assert.Equal(42, item.Count);
        }

        [Fact]
        public void ANullInventoryIsRefusedRatherThanCreated()
        {
            // the engine reads GetInventoryByNID and gives up when it is null, so a request
            // parked where it will not be looked at is worse than no request
            Assert.False(AutoFollow.TryQueueRequest(null, AutoFollowAction.InviteToDowntown, 42));
            Assert.False(AutoFollow.TryQueueRequest(null, AutoFollowAction.TakeHome, 42));
        }

        [Theory]
        [InlineData(AutoFollowAction.None)]
        [InlineData(AutoFollowAction.Both)]
        public void ARequestThatIsNotASingleDirectionIsRefused(AutoFollowAction action)
        {
            var inventory = new List<InventoryItem>();
            Assert.False(AutoFollow.TryQueueRequest(inventory, action, 42));
            Assert.Empty(inventory);
        }

        [Fact]
        public void QueueingTwiceReplacesRatherThanAccumulates()
        {
            // the engine's own cleanup drops everything in the slot and puts one entry back;
            // two entries in one slot would leave the engine's FirstOrDefault pick ambiguous
            var inventory = new List<InventoryItem>();

            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 1));
            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 2));

            var item = Assert.Single(inventory);
            Assert.Equal(2, item.Count);
        }

        [Fact]
        public void QueueingClearsPreExistingDuplicatesInTheSameSlot()
        {
            var inventory = new List<InventoryItem>()
            {
                SimData(AutoFollow.DowntownDateSlot, 1),
                SimData(AutoFollow.DowntownDateSlot, 2),
            };

            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 3));

            var item = Assert.Single(inventory);
            Assert.Equal(3, item.Count);
        }

        [Fact]
        public void QueueingOneDirectionWithdrawsTheOther()
        {
            // A sim cannot be on their way to a Downtown date and on their way home at once, and
            // a request left in the other slot is not bookkeeping - it is a live instruction the
            // next lot script to run one of these calls will carry out.
            foreach (var first in new[] { AutoFollowAction.InviteToDowntown, AutoFollowAction.TakeHome })
            {
                var second = AutoFollow.Opposite(first);
                var inventory = new List<InventoryItem>();

                Assert.True(AutoFollow.TryQueueRequest(inventory, first, 1));
                Assert.True(AutoFollow.TryQueueRequest(inventory, second, 2));

                // exactly one entry left, and it is the second direction - not the first
                var item = Assert.Single(inventory);
                Assert.Equal(AutoFollow.Opposite(first), AutoFollow.ActionForSlot(item.GUID));
                Assert.Equal(2, item.Count);
                Assert.Equal(second, AutoFollow.FindPendingActions(inventory));
            }
        }

        [Fact]
        public void TheDirectionJustQueuedIsTheOneFoundAfterwards()
        {
            // The bug this rule exists to kill: queue take home after downtown and a lookup that
            // reports downtown first still names the request that was just withdrawn, so the
            // panel calls it an invitation to Downtown and closes both buttons.
            var inventory = new List<InventoryItem>();
            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 42));
            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.TakeHome, 7));

            AutoFollowAction found;
            short nid;
            Assert.True(AutoFollow.TryFindRequest(inventory, out found, out nid));
            Assert.Equal(AutoFollowAction.TakeHome, found);
            Assert.Equal(7, nid);
        }

        [Fact]
        public void QueueingWithdrawsDuplicatesInTheOtherSlotToo()
        {
            // a slot the engine's own cleanup would drop in one go, however many entries are in it
            var inventory = new List<InventoryItem>()
            {
                SimData(AutoFollow.DowntownDateSlot, 1),
                SimData(AutoFollow.DowntownDateSlot, 2),
            };

            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.TakeHome, 3));

            var item = Assert.Single(inventory);
            Assert.Equal(AutoFollow.TakeBackHomeSlot, item.GUID);
            Assert.Equal(3, item.Count);
        }

        [Fact]
        public void ARefusedQueueLeavesTheOtherDirectionsRequestAlone()
        {
            // validation happens before anything is written, so a refused call cannot withdraw a
            // standing request on its way out
            var inventory = new List<InventoryItem>();
            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 42));

            Assert.False(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.TakeHome, 0));
            Assert.False(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.TakeHome, -1));
            Assert.False(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.Both, 7));

            var item = Assert.Single(inventory);
            Assert.Equal(AutoFollow.DowntownDateSlot, item.GUID);
            Assert.Equal(42, item.Count);
        }

        [Fact]
        public void QueueingLeavesTravelMotiveEntriesAlone()
        {
            // GUIDs 0-6 hold motive values; withdrawing a request must not disturb them
            var inventory = new List<InventoryItem>();
            for (uint guid = 0; guid < AutoFollow.DowntownDateSlot; guid++) inventory.Add(SimData(guid, 5));

            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 42));

            Assert.Equal((int)AutoFollow.DowntownDateSlot + 1, inventory.Count);
            for (uint guid = 0; guid < AutoFollow.DowntownDateSlot; guid++)
            {
                Assert.Equal((ushort)5, inventory[(int)guid].Count);
            }
        }

        [Fact]
        public void AQueuedRequestSurvivesARejectedSecondAttempt()
        {
            var inventory = new List<InventoryItem>();
            Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.TakeHome, 7));
            Assert.False(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.Both, 9));

            var item = Assert.Single(inventory);
            Assert.Equal(7, item.Count);
            Assert.Equal(AutoFollow.TakeBackHomeSlot, item.GUID);
        }

        [Fact]
        public void ASimCarryingBothRequestsIsReducedToTheOneAskedFor()
        {
            // the state an older build could leave behind: either button has to be able to fix it,
            // so queueing either direction drops the other
            foreach (var action in new[] { AutoFollowAction.InviteToDowntown, AutoFollowAction.TakeHome })
            {
                var inventory = new List<InventoryItem>()
                {
                    SimData(AutoFollow.DowntownDateSlot, 42),
                    SimData(AutoFollow.TakeBackHomeSlot, 7),
                };
                Assert.Equal(AutoFollowAction.Both, AutoFollow.FindPendingActions(inventory));

                Assert.True(AutoFollow.TryQueueRequest(inventory, action, 13));

                Assert.Equal(action, AutoFollow.FindPendingActions(inventory));
                var item = Assert.Single(inventory);
                Assert.Equal(13, item.Count);
            }
        }

        #endregion

        #region the neighbour id a request names

        [Fact]
        public void TheLowestNameableNeighbourIsTheFirstOne()
        {
            // TS1 neighbour IDs are 1-based; there is no neighbour 0
            Assert.Equal(1, AutoFollow.MinSpawnNeighbourId);
            Assert.True(AutoFollow.IsSpawnableNeighbourId(1));
        }

        [Fact]
        public void TheLargestNeighbourIdTheEngineCanNameIsSpawnable()
        {
            // the ID rides in a ushort but the engine casts it back to a short, so this is the top
            // of the range that survives the round trip
            Assert.True(AutoFollow.IsSpawnableNeighbourId(short.MaxValue));
        }

        [Theory]
        [InlineData((short)0)]
        [InlineData((short)-1)]
        [InlineData((short)-2)]
        [InlineData(short.MinValue)]
        public void ANeighbourIdThatNamesNobodyIsNotSpawnable(short spawnNid)
        {
            // the engine casts the stored count straight back to a neighbour ID and refuses one
            // that names nobody, so a request like this could never spawn anything
            Assert.False(AutoFollow.IsSpawnableNeighbourId(spawnNid));
        }

        [Theory]
        [InlineData((short)0)]
        [InlineData((short)-1)]
        [InlineData(short.MinValue)]
        public void ARequestNamingNobodyIsRefusedRatherThanWritten(short spawnNid)
        {
            // -1 cast to the ushort the slot stores becomes 65535, which the engine casts straight
            // back to -1. A request that could never spawn is worse than no request at all.
            foreach (var action in new[] { AutoFollowAction.InviteToDowntown, AutoFollowAction.TakeHome })
            {
                var inventory = new List<InventoryItem>();
                Assert.False(AutoFollow.TryQueueRequest(inventory, action, spawnNid));
                Assert.Empty(inventory);
                Assert.Equal(AutoFollowAction.None, AutoFollow.FindPendingActions(inventory));
            }
        }

        [Fact]
        public void EveryRealNeighbourIdStillQueues()
        {
            // the guard is on the lower bound, not on the sims a player can actually pick
            foreach (var spawnNid in new short[] { 1, 2, 42, 1000, short.MaxValue })
            {
                var inventory = new List<InventoryItem>();
                Assert.True(AutoFollow.TryQueueRequest(inventory, AutoFollowAction.TakeHome, spawnNid));

                var item = Assert.Single(inventory);
                Assert.Equal((ushort)spawnNid, item.Count);
            }
        }

        #endregion

        #region clearing a request

        [Fact]
        public void ClearingRemovesOnlyTheNamedSlotsRequest()
        {
            // both slots built by hand: queueing keeps a sim to one request, so only data this
            // control did not write can hold two
            var inventory = new List<InventoryItem>()
            {
                SimData(AutoFollow.DowntownDateSlot, 1),
                SimData(AutoFollow.TakeBackHomeSlot, 2),
            };

            Assert.True(AutoFollow.ClearRequest(inventory, AutoFollowAction.TakeHome));

            var item = Assert.Single(inventory);
            Assert.Equal(AutoFollow.DowntownDateSlot, item.GUID);
            Assert.Equal(1, item.Count);
        }

        [Fact]
        public void ClearingEveryDirectionLeavesNoRequestAtAll()
        {
            // how a queued request is withdrawn for good, rather than replaced
            var inventory = new List<InventoryItem>();
            AutoFollow.TryQueueRequest(inventory, AutoFollowAction.TakeHome, 7);

            Assert.True(AutoFollow.ClearRequest(inventory, AutoFollowAction.TakeHome));
            Assert.Equal(AutoFollowAction.None, AutoFollow.FindPendingActions(inventory));
        }

        [Fact]
        public void ClearingReportsWhetherThereWasAnythingToClear()
        {
            var inventory = new List<InventoryItem>();
            Assert.False(AutoFollow.ClearRequest(inventory, AutoFollowAction.InviteToDowntown));

            AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 1);
            Assert.True(AutoFollow.ClearRequest(inventory, AutoFollowAction.InviteToDowntown));
            Assert.False(AutoFollow.ClearRequest(inventory, AutoFollowAction.InviteToDowntown));
        }

        [Fact]
        public void ClearingANullInventoryOrANonDirectionDoesNothing()
        {
            Assert.False(AutoFollow.ClearRequest(null, AutoFollowAction.InviteToDowntown));

            var inventory = new List<InventoryItem>();
            AutoFollow.TryQueueRequest(inventory, AutoFollowAction.InviteToDowntown, 1);
            Assert.False(AutoFollow.ClearRequest(inventory, AutoFollowAction.Both));
            Assert.False(AutoFollow.ClearRequest(inventory, AutoFollowAction.None));
            Assert.Single(inventory);
        }

        #endregion

        #region what the control does, end to end

        /// <summary>
        /// The half of <c>UILotControl</c> that has no VM in it: read a sim's standing requests
        /// into a subject. Everything the control does around it is this, plus
        /// <see cref="AutoFollow.Available"/> and <see cref="AutoFollow.TryQueueRequest"/>.
        /// </summary>
        private static AutoFollowSubject DescribeSimWith(List<InventoryItem> inventory)
        {
            return new AutoFollowSubject() { IsOnLot = true, Pending = AutoFollow.FindPendingActions(inventory) };
        }

        [Fact]
        public void TheSimIsAlwaysBookedTheWayThePanelSaysItIs()
        {
            // Row 11.2.3 of the test matrix, walked as the player walks it: invite to Downtown
            // from a home lot, take home from a Downtown lot, and back again. Each press is the
            // button the current lot leads with, and three things have to hold every time: the
            // button pressed was live, the direction the panel reports afterwards is the one just
            // queued, and the button that can still change it is the other one.
            var inventory = new List<InventoryItem>();
            var presses = new[]
            {
                AutoFollowAction.InviteToDowntown,
                AutoFollowAction.TakeHome,
                AutoFollowAction.InviteToDowntown,
                AutoFollowAction.TakeHome,
            };

            for (int step = 0; step < presses.Length; step++)
            {
                var lotIsDowntown = (step % 2) == 1;
                var before = DescribeSimWith(inventory);

                AutoFollowReason reason;
                var available = AutoFollow.Available(before, out reason);
                var pressed = AutoFollow.Preferred(available, lotIsDowntown);
                Assert.Equal(presses[step], pressed);

                // the button the player presses is live, and the direction already queued is not
                Assert.NotEqual(AutoFollowAction.None, available & pressed);
                Assert.Equal(AutoFollowAction.None, available & before.Pending);

                Assert.True(AutoFollow.TryQueueRequest(inventory, pressed, 42));

                // what the panel now names is what was just queued - not the direction the
                // engine's first-match lookup would have found
                var after = DescribeSimWith(inventory);
                Assert.Equal(pressed, after.Pending);

                AutoFollowAction found;
                short foundNid;
                Assert.True(AutoFollow.TryFindRequest(inventory, out found, out foundNid));
                Assert.Equal(pressed, found);

                // and the sim is never stuck: the other direction is the withdrawal
                AutoFollowReason afterReason;
                Assert.Equal(AutoFollow.Opposite(pressed), AutoFollow.Available(after, out afterReason));
                Assert.Equal(AutoFollowReason.PendingInOtherDirection, afterReason);
            }
        }

        [Fact]
        public void AQueuedRequestIsReachableByTheNextButtonThePlayerPresses()
        {
            // the withdrawal path in one assertion: whatever is standing, the panel leaves a
            // button that changes it, so a sim can never end up with a request the player has no
            // way of touching
            foreach (var standing in PendingStates)
            {
                var subject = AutoFollowSubject.Ordinary;
                subject.Pending = standing;

                AutoFollowReason reason;
                var available = AutoFollow.Available(subject, out reason);

                Assert.True(available != AutoFollowAction.None, $"{standing} left nothing to press");
            }
        }

        #endregion
    }
}
