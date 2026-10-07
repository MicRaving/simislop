/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Engine.TSOGlobalLink.Model;
using FSO.SimAntics.Entities;
using FSO.SimAntics.NetPlay.Model.Commands;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Part B5's global link: the money rules, the unchanged no-op surface, and the one thing
    /// this milestone changes - leaving a lot.
    ///
    /// <para>
    /// <b>What can be tested without a lot.</b> A <c>VM</c> needs a <c>VMContext</c>, a content
    /// manager and a graphics device, and a <c>VMAvatar</c> needs a <c>GameObject</c>, so the
    /// members that take either of those are not reachable from here. Everything the type does
    /// <em>decide</em> was therefore lifted out as a function over values:
    /// <see cref="SimitoneGlobalLink.PlanLeaveLot"/> for the leave-lot branch,
    /// <see cref="SimitoneGlobalLink.SelectLeaveDestination"/> for the destination, the
    /// <c>FAMI</c> overloads of the three budget helpers for the money rule, and
    /// <see cref="SimitoneGlobalLink.AllocatePersistId"/> for the id sequence. What is left
    /// untested - <c>Tick</c>'s architecture simulation, and the transaction path's
    /// <c>GetObjectByPersist</c> lookups - is stated as untested here rather than approximated,
    /// because a fake <c>VM</c> would be testing the fake.
    /// </para>
    ///
    /// <para>
    /// <b>Why two tests read the source file.</b> Two of B5's requirements are about text rather
    /// than behaviour: the three "multiple families" TODOs in the budget helpers must survive
    /// this rewrite, and <c>LeaveLot</c> must not call <c>CloseNet</c>. A reflection test can
    /// show that the helpers exist and that the branch returns a transition; neither can show
    /// that a comment was kept or that a call was not made. <c>GlobalMutationGuardTests</c>
    /// already reads this repository's own source for the same reason, so the walk to the
    /// repository root is the established one.
    /// </para>
    /// </summary>
    public sealed class SimitoneGlobalLinkTests
    {
        // ---- fakes ---------------------------------------------------------------------------------

        /// <summary>
        /// A family with a budget, an id and a house number. The id is a <c>ushort</c> on the
        /// chunk and a <c>short</c> in the person data a resolver is asked with, which is the
        /// whole reason the resolver is keyed on the second one.
        /// </summary>
        private static FAMI Family(int budget, short id = 1, int house = 22)
        {
            return new FAMI
            {
                ChunkID = (ushort)id,
                ChunkLabel = "",
                ChunkType = "FAMI",
                Budget = budget,
                HouseNumber = house
            };
        }

        /// <summary>A link over a dictionary of families, and a list of the lots it asked to go to.</summary>
        private static SimitoneGlobalLink Link(Dictionary<short, FAMI> families, List<int> transitions,
            bool worldServices = true)
        {
            return new SimitoneGlobalLink(
                id => families != null && families.TryGetValue(id, out var found) ? found : null,
                lot => { if (transitions != null) transitions.Add(lot); },
                worldServices);
        }

        private static SimitoneGlobalLink BareLink()
        {
            return new SimitoneGlobalLink();
        }

        // ---- the shape of the type -------------------------------------------------------------------

        [Fact]
        public void TheLinkIsASealedImplementationOfTheInterfaceAndNotTheStub()
        {
            var type = typeof(SimitoneGlobalLink);

            Assert.True(type.IsSealed, "SimitoneGlobalLink must be sealed");
            Assert.True(typeof(IVMTSOGlobalLink).IsAssignableFrom(type));

            //not a subclass: every interface member is declared here, so nothing reaches the
            //lot through a base class a test cannot see.
            Assert.Equal(typeof(object), type.BaseType);
            var inherited = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.DeclaringType != type)
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .ToList();
            Assert.True(inherited.All(n => n.StartsWith("Object.")),
                "SimitoneGlobalLink must not inherit behaviour, but it inherits " + string.Join(", ", inherited));
        }

        [Fact]
        public void EveryInterfaceMemberIsDeclaredHere()
        {
            var declared = new HashSet<string>(
                typeof(SimitoneGlobalLink).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.DeclaringType == typeof(SimitoneGlobalLink))
                    .Select(m => m.Name),
                StringComparer.Ordinal);

            var missing = new List<string>();
            foreach (var m in typeof(IVMTSOGlobalLink).GetMethods())
            {
                var wanted = m.Name + "(" + m.GetParameters().Length + ")";
                if (!declared.Contains(m.Name)) missing.Add(wanted);
            }

            Assert.True(missing.Count == 0,
                "these interface members are not implemented by SimitoneGlobalLink: "
                + string.Join(", ", missing));
        }

        [Fact]
        public void TheBudgetHelpersKeepTheStubsPublicSignatures()
        {
            //the stub's three budget helpers are public and are called as such by the
            //build/buy and transfer paths; a rename would compile and break the lot.
            var get = typeof(SimitoneGlobalLink).GetMethod("GetBudgetForFamily",
                new[] { typeof(VM), typeof(VMEntity) });
            var transact = typeof(SimitoneGlobalLink).GetMethod("TransactBudgetForFamily",
                new[] { typeof(VM), typeof(VMEntity), typeof(int) });
            var canTransact = typeof(SimitoneGlobalLink).GetMethod("CanTransactBudgetForFamily",
                new[] { typeof(VM), typeof(VMEntity), typeof(int) });

            Assert.NotNull(get);
            Assert.NotNull(transact);
            Assert.NotNull(canTransact);
            Assert.Equal(typeof(uint), get.ReturnType);
            Assert.Equal(typeof(bool), transact.ReturnType);
            Assert.Equal(typeof(bool), canTransact.ReturnType);
        }

        [Fact]
        public void TheFlagIsCarriedAndReadable()
        {
            var on = Link(new Dictionary<short, FAMI>(), null, true);
            var off = Link(new Dictionary<short, FAMI>(), null, false);

            Assert.True(on.WorldServicesEnabled);
            Assert.False(off.WorldServicesEnabled);
            Assert.Null(BareLink().World);
            Assert.DoesNotContain("no world services", on.ToString());
            Assert.Contains("no world services", off.ToString());
        }

        // ---- budgets ---------------------------------------------------------------------------------

        [Fact]
        public void AFamilysBudgetIsItsOwnAndMaxisHasAnUnlimitedOne()
        {
            var link = BareLink();

            Assert.Equal(500u, link.GetBudgetForFamily(Family(500)));
            Assert.Equal(0u, link.GetBudgetForFamily(Family(0)));
            Assert.Equal(uint.MaxValue, link.GetBudgetForFamily((FAMI)null));
            Assert.Equal(SimitoneGlobalLink.InfiniteBudget, uint.MaxValue);
        }

        [Fact]
        public void ANegativeBudgetReadsAsTheStubReadIt()
        {
            //the stub casts a FAMI budget straight to uint, so a corrupt negative becomes a very
            //large number rather than an exception. Pinned because a "fix" here would let a
            //corrupt family buy the whole catalogue.
            var link = BareLink();

            Assert.Equal(unchecked((uint)-5), link.GetBudgetForFamily(Family(-5)));
        }

        [Fact]
        public void APartyThatIsNotAFamilyIsMaxisAndCanAffordAnything()
        {
            var link = BareLink();

            //a null entity is the "not an avatar" case, and it needs no VM to reach.
            Assert.Equal(uint.MaxValue, link.GetBudgetForFamily(null, null));
            Assert.True(link.CanTransactBudgetForFamily(null, null, -int.MaxValue));
            Assert.True(link.TransactBudgetForFamily(null, null, -int.MaxValue));
            Assert.True(link.CanTransactBudgetForFamily((FAMI)null, -int.MaxValue));
            Assert.True(link.TransactBudgetForFamily((FAMI)null, -int.MaxValue));
        }

        [Fact]
        public void AFamilyIsChargedUpToWhatItHasAndNoFurther()
        {
            var family = Family(500);
            var link = BareLink();

            Assert.True(link.CanTransactBudgetForFamily(family, -500));
            Assert.False(link.CanTransactBudgetForFamily(family, -501));
            Assert.True(link.CanTransactBudgetForFamily(family, 100000));
            Assert.Equal(500, family.Budget); //CanTransact moves nothing

            Assert.True(link.TransactBudgetForFamily(family, -200));
            Assert.Equal(300, family.Budget);
            Assert.True(link.TransactBudgetForFamily(family, 50));
            Assert.Equal(350, family.Budget);

            Assert.False(link.TransactBudgetForFamily(family, -351));
            Assert.Equal(350, family.Budget); //a refused charge moves nothing
            Assert.Equal(350u, link.GetBudgetForFamily(family));
        }

        [Fact]
        public void SpendingExactlyNothingIsAllowedAndSpendingToZeroIsNotARefusal()
        {
            var family = Family(0);
            var link = BareLink();

            Assert.True(link.CanTransactBudgetForFamily(family, 0));
            Assert.True(link.TransactBudgetForFamily(family, 0));
            Assert.Equal(0, family.Budget);
        }

        [Fact]
        public void TheFamilyIsResolvedThroughTheInjectedResolver()
        {
            var one = Family(111, 1, 21);
            var two = Family(222, 2, 22);
            var families = new Dictionary<short, FAMI> { { 1, one }, { 2, two } };
            var link = Link(families, null);

            Assert.Same(one, link.ResolveFamily(null, (short)1));
            Assert.Same(two, link.ResolveFamily(null, (short)2));
            Assert.Null(link.ResolveFamily(null, (short)3)); //unknown: Maxis, as the stub would say
            Assert.Null(link.ResolveFamily(null, (VMEntity)null)); //not an avatar
        }

        [Fact]
        public void AResolverThatKnowsNothingOrThrowsLeavesTheLotToAnswerForItself()
        {
            var silent = new SimitoneGlobalLink(id => null);
            var loud = new SimitoneGlobalLink(id => throw new InvalidOperationException("no families loaded"));

            //with no VM there is no current family either, so the answer is Maxis's - but the
            //point is that neither a shrug nor a throw becomes an exception at the call site.
            Assert.Null(silent.ResolveFamily(null, (short)4));
            Assert.Null(loud.ResolveFamily(null, (short)4));
            Assert.Equal(uint.MaxValue, loud.GetBudgetForFamily(null, null));
        }

        [Fact]
        public void ALinkWithNoResolverStillAnswersEverythingTheLotCanAnswer()
        {
            var link = new SimitoneGlobalLink();

            Assert.Null(link.ResolveFamily(null, (short)1));
            Assert.Null(link.CurrentFamily(null));
            Assert.Equal(SimitoneGlobalLink.GoHomeLot, link.ResolveLeaveDestination(null));
        }

        // ---- transactions ---------------------------------------------------------------------------

        [Fact]
        public void ATransactionBetweenTwoPartiesTheLinkCannotFindSucceeds()
        {
            //uint.MaxValue is Maxis, and an id that is not on this lot is not anybody. The stub
            //succeeds here, and a build/buy gate that failed instead would freeze the game.
            var link = BareLink();

            Assert.True(link.PerformTransaction(null, false, 1, 2, 100));
            Assert.True(link.PerformTransaction(null, true, 1, 2, 100));
        }

        [Fact]
        public void TheTransactionCallbackRunsInlineOnTheCallingThread()
        {
            var link = BareLink();
            var callerThread = Thread.CurrentThread.ManagedThreadId;

            var called = false;
            var returned = false;
            var ranBeforeReturn = false;
            var callbackThread = -1;
            bool success = false;
            var amount = 0;

            link.PerformTransaction(null, false, 11, 22, 100,
                (bool ok, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) =>
                {
                    called = true;
                    ranBeforeReturn = !returned;
                    callbackThread = Thread.CurrentThread.ManagedThreadId;
                    success = ok;
                    amount = transferAmount;
                });
            returned = true;

            Assert.True(called, "the callback must run");
            Assert.True(ranBeforeReturn,
                "the callback must have run before PerformTransaction returned. The TS1 stub "
                + "hands this to a new System.Threading.Thread, which is the race this replaces.");
            Assert.Equal(callerThread, callbackThread);
            Assert.True(success);
            Assert.Equal(100, amount);
        }

        [Fact]
        public void TheTwoShorterTransactionOverloadsReachTheSamePlace()
        {
            var link = BareLink();
            var withType = 0;
            var withoutType = 0;

            link.PerformTransaction(null, false, 1, 2, 50, (short)3,
                (bool ok, int amount, uint uid1, uint budget1, uint uid2, uint budget2) => withType = amount);
            link.PerformTransaction(null, false, 1, 2, 50,
                (bool ok, int amount, uint uid1, uint budget1, uint uid2, uint budget2) => withoutType = amount);

            Assert.Equal(50, withType);
            Assert.Equal(50, withoutType);
        }

        [Fact]
        public void ATransactionWithNoCallbackAndNoLotIsNotAnError()
        {
            var link = BareLink();

            //the stub nests its response and its callback inside `if (callback != null)`, so a
            //caller with no callback silently gets nothing. Nothing here may throw, because the
            //money has already moved by the time a callback would have run.
            link.PerformTransaction(null, false, 1, 2, 100, (short)0, (short)0, null);
            link.PerformTransaction(null, false, 1, 2, 100, (short)0, null);
            link.PerformTransaction(null, false, 1, 2, 100, (VMAsyncTransactionCallback)null);
        }

        [Fact]
        public void ACallbackThatThrowsDoesNotEscapeTheTransaction()
        {
            var link = BareLink();

            var threw = Record.Exception(() => link.PerformTransaction(null, false, 1, 2, 100,
                (bool ok, int amount, uint uid1, uint budget1, uint uid2, uint budget2) =>
                    throw new InvalidOperationException("the caller's own bug")));

            Assert.Null(threw);
        }

        // ---- leaving a lot --------------------------------------------------------------------------

        [Theory]
        [InlineData(5u, 5u, SimitoneLotLeaveAction.TransitionToLot)]
        [InlineData(5u, 6u, SimitoneLotLeaveAction.DepartLot)]
        [InlineData(6u, 5u, SimitoneLotLeaveAction.DepartLot)]
        [InlineData(0u, 0u, SimitoneLotLeaveAction.DepartLot)]
        [InlineData(0u, 5u, SimitoneLotLeaveAction.DepartLot)]
        [InlineData(5u, 0u, SimitoneLotLeaveAction.DepartLot)]
        public void OnlyTheLocalPlayerTransitions(uint avatar, uint myUid, SimitoneLotLeaveAction expected)
        {
            Assert.Equal(expected, SimitoneGlobalLink.PlanLeaveLot(avatar, myUid));
        }

        [Fact]
        public void ThereAreExactlyTwoThingsLeavingALotCanMean()
        {
            var values = Enum.GetValues(typeof(SimitoneLotLeaveAction)).Cast<SimitoneLotLeaveAction>().ToArray();

            Assert.Equal(2, values.Length);
            Assert.Equal(0, (int)SimitoneLotLeaveAction.DepartLot);
            Assert.Equal(1, (int)SimitoneLotLeaveAction.TransitionToLot);
        }

        [Fact]
        public void ADestinationIsTheFamilysHouseOrTheScreensOwnGoHome()
        {
            Assert.Equal(22, SimitoneGlobalLink.SelectLeaveDestination(22));
            Assert.Equal(SimitoneGlobalLink.GoHomeLot, SimitoneGlobalLink.SelectLeaveDestination(null));
            Assert.Equal(SimitoneGlobalLink.GoHomeLot, SimitoneGlobalLink.SelectLeaveDestination(0));
            Assert.Equal(SimitoneGlobalLink.GoHomeLot, SimitoneGlobalLink.SelectLeaveDestination(-3));
            Assert.Equal(-1, SimitoneGlobalLink.GoHomeLot);
        }

        /// <summary>
        /// The frozen-lot guard, in the one place it can be asserted without a screen.
        ///
        /// <para>
        /// <b>Why this is a range test and not a "positive number" test.</b> A destination this
        /// link hands the screen becomes a <c>SwitchLot</c>, and
        /// <c>TS1GameScreen.VMLotSwitch</c> has already paused the lot by the time anybody acts on
        /// it. With <c>NeighbourhoodWorldFlag.Travel</c> on, a number
        /// <see cref="LotTravelService.IsLotNumber"/> refuses is refused by
        /// <c>LotTravelService.TryTransition</c> <em>after</em> that pause, and the lot then sits
        /// at <c>SpeedMultiplier == 0</c> for the rest of the session - frozen clock, no
        /// autonomous sims, and any script caught mid-way through serving a group meal left with
        /// the object's <c>Occupied</c> bit set, which is what makes the pie menu then refuse
        /// every interaction on that object. 99 is the number that does it: the lot-location
        /// parser's own "no lot" sentinel, and a perfectly ordinary thing for a family's house
        /// number to hold.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(1, 1)]
        [InlineData(22, 22)]
        [InlineData(98, 98)]
        [InlineData(99, SimitoneGlobalLink.GoHomeLot)]
        [InlineData(120, SimitoneGlobalLink.GoHomeLot)]
        [InlineData(0, SimitoneGlobalLink.GoHomeLot)]
        [InlineData(-1, SimitoneGlobalLink.GoHomeLot)]
        [InlineData(int.MaxValue, SimitoneGlobalLink.GoHomeLot)]
        public void ADestinationIsOnlyEverNamedWhenASwitchCanActOnIt(int houseNumber, int expected)
        {
            Assert.Equal(expected, SimitoneGlobalLink.SelectLeaveDestination(houseNumber));

            //and the same bound the service applies, so the two cannot drift apart and put the
            //screen back in the state this test exists to keep it out of
            var serviceCanAct = LotTravelService.IsLotNumber(houseNumber);
            Assert.Equal(serviceCanAct, SimitoneGlobalLink.SelectLeaveDestination(houseNumber) != SimitoneGlobalLink.GoHomeLot);
        }

        /// <summary>
        /// The lot range is the service's, not this link's: one authority, so the two answers
        /// cannot disagree about a number and leave the pause behind.
        /// </summary>
        [Fact]
        public void TheLinkAndTheTravelServiceAgreeOnWhatALotNumberIs()
        {
            foreach (var n in new[] { 0, 1, 2, 50, 97, 98, 99, 100, 1000, -1, -99 })
            {
                var service = LotTravelService.IsLotNumber(n);
                var linked = SimitoneGlobalLink.SelectLeaveDestination(n) != SimitoneGlobalLink.GoHomeLot;
                Assert.True(service == linked, n + ": service says " + service + ", link says " + linked);
            }
        }

        [Fact]
        public void ATransitionIsToldToTheInjectedHookBeforeTheVmIsSignalled()
        {
            var transitions = new List<int>();
            var link = Link(new Dictionary<short, FAMI>(), transitions);

            link.RequestLotTransition(null, 22);

            //the VM is null here, so what is asserted is the half that does not need one: the
            //hook was told, and told once. TS1GameScreen.VMLotSwitch reads the same number off
            //the VM signal; the hook is the world's own record of the hop.
            Assert.Equal(new[] { 22 }, transitions.ToArray());
        }

        [Fact]
        public void ATransitionWithNoHookOrNoLotOrNeitherIsStillNotAnError()
        {
            var noHook = new SimitoneGlobalLink();
            var loudHook = new SimitoneGlobalLink(lot => throw new InvalidOperationException("world is closing"));

            noHook.RequestLotTransition(null, 22);
            loudHook.RequestLotTransition(null, SimitoneGlobalLink.GoHomeLot);
            Assert.Null(Record.Exception(() => loudHook.RequestLotTransition(null, 22)));
        }

        [Fact]
        public void WithTheMilestoneOffTheLinkAsksTheScreenToGoHome()
        {
            var families = new Dictionary<short, FAMI> { { 1, Family(500, 1, 22) } };
            var off = Link(families, null, worldServices: false);

            Assert.Equal(SimitoneGlobalLink.GoHomeLot, off.ResolveLeaveDestination(null));
        }

        [Fact]
        public void ALeaveWithNoAvatarOrNoLotIsNotAnError()
        {
            var transitions = new List<int>();
            var link = Link(new Dictionary<short, FAMI>(), transitions);

            Assert.Null(Record.Exception(() => link.LeaveLot(null, null)));
            Assert.Empty(transitions);
        }

        [Fact]
        public void TheLinkNeverClosesTheNetAndKeepsTheStubsThreeTodos()
        {
            var source = ReadSource();

            //not "the word does not appear": the prose in this file explains what CloseNet used
            //to do here and why it must not, and that explanation is worth keeping. What must
            //not exist is a call - so every comment line is dropped first.
            var code = source
                .Select(line => line.Trim())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("*", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("/*", StringComparison.Ordinal))
                .ToList();
            var closeNet = code.Where(line => line.Contains("CloseNet")).ToList();
            Assert.True(closeNet.Count == 0,
                "LeaveLot must transition, not CloseNet(LeaveLot). These lines call it:\n"
                + string.Join("\n", closeNet));

            //the stub's "multiple families playable in the same lot" TODO exists in all three
            //budget helpers, and B5 asks for it to be carried over rather than deleted.
            var todos = source.Count(line => line.Trim().StartsWith("//todo: make this get the appropriate family", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, todos);
            var continuation = source.Count(line => line.Trim().StartsWith("//if multiple families are playable in the same lot", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, continuation);
        }

        [Fact]
        public void TheTsoOnlyMembersAreListedInACommentBesideTheCode()
        {
            //the reason a reader of a no-op method can tell it is deliberate
            var source = ReadSource();
            var listed = new[]
            {
                "ObtainAvatarFromTicket", "LoadPluginPersist", "SavePluginPersist", "ForceInInventory",
                "UpdateObjectPersist", "PurchaseFromOwner", "RetrieveFromInventoryByType",
                "RetrieveFromInventory", "ConsumeInventory", "SetSpotlightStatus", "StockOutfit",
                "GetOutfits", "DeleteOutfit", "UpdateOutfitSalePrice", "PurchaseOutfit",
                "SecureTrade", "GetDynPayouts", "GetBulletinState", "GetObjectGlobalCooldown",
                "GetAccountIDFromAvatar", "FindLotAndValue"
            };

            var missing = listed
                .Where(name => !source.Any(line => line.Contains(name)))
                .ToList();
            Assert.True(missing.Count == 0,
                "these TSO-online-only members are not named in the list comment: " + string.Join(", ", missing));
        }

        // ---- the no-op surface -----------------------------------------------------------------------

        [Fact]
        public void EveryTsoOnlyMemberIsStillANoOpAndSurvivesBeingCalledWithNothing()
        {
            var link = BareLink();
            var offenders = new List<string>();

            foreach (var method in typeof(IVMTSOGlobalLink).GetMethods())
            {
                if (!OnlineOnlyMembers.Contains(method.Name)) continue;

                var args = method.GetParameters()
                    .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                    .ToArray();
                object result = "not called";
                try
                {
                    result = method.Invoke(link, args);
                }
                catch (TargetInvocationException e)
                {
                    offenders.Add(method.Name + " threw " + (e.InnerException?.GetType().Name ?? "?")
                        + ": " + e.InnerException?.Message);
                    continue;
                }
                catch (Exception e)
                {
                    offenders.Add(method.Name + " threw " + e.GetType().Name + ": " + e.Message);
                    continue;
                }
                Assert.Null(result); //they are all void, and none of them may return something
            }

            Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        }

        [Fact]
        public void TheNoOpListIsTheInterfaceMinusTheMembersThatAreReallyImplemented()
        {
            //so that a member added to IVMTSOGlobalLink has to be declared on one side or the
            //other, rather than quietly being assumed to be a no-op.
            var really = new HashSet<string>
            {
                "LeaveLot", "PerformTransaction", "RequestRoommate", "RemoveRoommate",
                "QueueArchitecture", "RegisterNewObject", "MoveToInventory", "DeleteObject",
                "TokenRequest", "Tick"
            };

            var unaccounted = typeof(IVMTSOGlobalLink).GetMethods()
                .Select(m => m.Name)
                .Distinct()
                .Where(name => !really.Contains(name) && !OnlineOnlyMembers.Contains(name))
                .ToList();

            Assert.True(unaccounted.Count == 0,
                "these interface members are neither implemented nor listed as TSO-only: "
                + string.Join(", ", unaccounted));
        }

        [Fact]
        public void TheStubShapedAnswersAreUnchanged()
        {
            var link = BareLink();

            VMInventoryRestoreObject restored = null;
            link.RetrieveFromInventory(null, 0, 0, false, o => restored = o);
            Assert.NotNull(restored);
            Assert.Equal(0u, restored.PersistID);
            Assert.Equal(0u, restored.GUID);

            VMInventoryRestoreObject byType = null;
            link.RetrieveFromInventoryByType(null, 0, 0, 0, false, o => byType = o);
            Assert.NotNull(byType);
            Assert.Equal(0u, byType.GUID);

            VMGLOutfit[] outfits = null;
            link.GetOutfits(null, default(VMGLOutfitOwner), 0, o => outfits = o);
            Assert.NotNull(outfits);
            Assert.Empty(outfits);

            byte[] plugin = new byte[] { 1 };
            link.LoadPluginPersist(null, 0, 0, d => plugin = d);
            Assert.Null(plugin); //no plugin database; the stub answers null

            //a group the link does not have is reported as a failure rather than as the crash
            //the stub's unconditional `obj.BaseObject` would have produced.
            var success = true;
            link.PurchaseFromOwner(null, null, 0, (ok, id) => success = ok, null);
            Assert.False(success);
        }

        [Fact]
        public void ATokenRequestIsAnsweredWithItsOwnAttributesInline()
        {
            var link = BareLink();
            var attributes = new List<int> { 4, 5, 6 };
            var callerThread = Thread.CurrentThread.ManagedThreadId;
            var callbackThread = -1;

            List<int> answered = null;
            var ok = false;
            link.TokenRequest(null, 1, 2, VMTokenRequestMode.GetOrCreate, attributes,
                (bool success, List<int> results) =>
                {
                    answered = results;
                    ok = success;
                    callbackThread = Thread.CurrentThread.ManagedThreadId;
                });

            Assert.True(ok);
            Assert.Same(attributes, answered);
            Assert.Equal(callerThread, callbackThread);

            //a null list and a null callback are both things a caller can do
            link.TokenRequest(null, 1, 2, VMTokenRequestMode.Replace, null, (bool s, List<int> r) => { });
            link.TokenRequest(null, 1, 2, VMTokenRequestMode.Replace, null, null);
        }

        // ---- architecture ----------------------------------------------------------------------------

        [Fact]
        public void QueuedArchitectureCommandsWaitForATick()
        {
            var link = BareLink();
            Assert.Equal(0, link.PendingArchitectureCommands);
            Assert.False(link.WaitingOnArchitecture);

            link.QueueArchitecture(new VMNetArchitectureCmd());
            link.QueueArchitecture(new VMNetArchitectureCmd());
            link.QueueArchitecture(new VMNetArchitectureCmd());

            Assert.Equal(3, link.PendingArchitectureCommands);
            Assert.False(link.WaitingOnArchitecture);
        }

        [Fact]
        public void ANullArchitectureCommandIsDroppedRatherThanQueued()
        {
            //the stub enqueues it, and the queue drains on the game thread into a dereference of
            //nothing.
            var link = BareLink();

            link.QueueArchitecture(null);

            Assert.Equal(0, link.PendingArchitectureCommands);
        }

        [Fact]
        public void ATickWithNoLotDoesNothingAndDrainsNothing()
        {
            //Tick's work is a real architecture simulation, which needs a real lot. What is
            //pinned here is the part that does not: a null VM is a no-op, and it does not throw
            //on its way out of a lot that is already gone.
            var link = BareLink();
            link.QueueArchitecture(new VMNetArchitectureCmd());

            Assert.Null(Record.Exception(() => link.Tick(null)));
            Assert.Equal(1, link.PendingArchitectureCommands);
            Assert.False(link.WaitingOnArchitecture);
        }

        // ---- persist ids ----------------------------------------------------------------------------

        [Fact]
        public void NewObjectsGetIdsFromTheSameRangeTheStubUsedAndTheyDoNotRepeat()
        {
            var link = BareLink();

            Assert.Equal(0x1000000u, SimitoneGlobalLink.FirstSyntheticPersist);
            Assert.Equal(0x1000000u, link.AllocatePersistId());
            Assert.Equal(0x1000001u, link.AllocatePersistId());
            Assert.Equal(0x1000002u, link.NextPersist);
        }

        [Fact]
        public void TwoLinksDoNotShareTheIdCounter()
        {
            //the stub's counter is a public field on each instance, and two lots in one
            //neighbourhood must not hand the same id to two objects.
            var first = BareLink();
            var second = BareLink();
            first.AllocatePersistId();

            Assert.Equal(0x1000000u, second.AllocatePersistId());
            Assert.Equal(0x1000001u, first.AllocatePersistId());
        }

        // ---- shared tables and source ----------------------------------------------------------------

        /// <summary>
        /// The interface members that are TSO-online only and stay no-ops. Kept beside the
        /// reflection sweep that calls every one of them, so the two cannot drift.
        /// </summary>
        private static readonly HashSet<string> OnlineOnlyMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "ObtainAvatarFromTicket", "LoadPluginPersist", "SavePluginPersist", "ForceInInventory",
            "UpdateObjectPersist", "PurchaseFromOwner", "RetrieveFromInventoryByType",
            "RetrieveFromInventory", "ConsumeInventory", "SetSpotlightStatus", "StockOutfit",
            "GetOutfits", "DeleteOutfit", "UpdateOutfitSalePrice", "PurchaseOutfit",
            "SecureTrade", "GetDynPayouts", "GetBulletinState", "GetObjectGlobalCooldown",
            "GetAccountIDFromAvatar", "FindLotAndValue"
        };

        private static string[] ReadSource()
        {
            var root = RepositoryRoot();
            Assert.NotNull(root);
            var path = Path.Combine(root, "Client", "Simitone", "Simitone.Client",
                "Neighbourhood", "SimitoneGlobalLink.cs");
            Assert.True(File.Exists(path), "could not find SimitoneGlobalLink.cs at " + path);
            return File.ReadAllLines(path);
        }

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Client")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }
    }
}
