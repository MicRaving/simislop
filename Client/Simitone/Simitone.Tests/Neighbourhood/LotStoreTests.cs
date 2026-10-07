/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Reflection;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Hollow;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Part B's lot store: the three states of §5.3 and the transitions between them.
    ///
    /// <para>
    /// A store holds marshals rather than VMs, so every one of these tests runs with no content
    /// manager, no graphics device and no game thread - which is the reason this half of B1 is
    /// worth having as a type of its own. The transitions are the part that has to be right: a lot
    /// that was entered and left must come back as what it was, a neighbour that was hollowed must
    /// not still be answering as active, and a store must be able to return to empty without
    /// leaving anything behind that would still be found by the next <c>TryGet</c>.
    /// </para>
    /// </summary>
    public sealed class LotStoreTests
    {
        private static VMMarshal Active()
        {
            return new VMMarshal();
        }

        private static VMHollowMarshal Hollow()
        {
            return new VMHollowMarshal();
        }

        // ---- the three states -----------------------------------------------------------------------

        [Fact]
        public void AStoreThatHasBeenGivenNothingSaysEveryLotIsUnloaded()
        {
            var store = new LotStore();

            Assert.Equal(0, store.Count);
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(21));
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(0));
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(-7));
            Assert.Empty(store.LoadedLots);

            VMMarshal active;
            Assert.False(store.TryGetActive(21, out active));
            Assert.Null(active);

            VMHollowMarshal hollow;
            Assert.False(store.TryGetHollow(21, out hollow));
            Assert.Null(hollow);
        }

        [Fact]
        public void AnUnloadedLotGoesHollowThenActiveThenGone()
        {
            //The whole of §5.3 in order: a lot is nothing, then a neighbour's contents, then a
            //resident lot with a live VM behind it, then nothing again once the house file holds
            //what was in memory.
            var store = new LotStore();
            var resident = Active();
            var neighbour = Hollow();

            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(21));

            store.SetHollow(21, neighbour);
            Assert.Equal(NeighbourhoodLotState.Hollow, store.StateOf(21));
            Assert.Equal(1, store.Count);

            store.SetActive(21, resident);
            Assert.Equal(NeighbourhoodLotState.Active, store.StateOf(21));
            Assert.Equal(1, store.Count);

            store.Remove(21);
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(21));
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void AFailedGetLeavesNothingBehindToBeFoundLater()
        {
            //An out parameter that is only sometimes written is a store that hands a caller a stale
            //marshal from a previous lot. Both getters must clear it on the way out.
            var store = new LotStore();
            store.SetActive(21, Active());

            VMMarshal active;
            Assert.False(store.TryGetActive(22, out active));
            Assert.Null(active);

            VMHollowMarshal hollow;
            Assert.False(store.TryGetHollow(21, out hollow));
            Assert.Null(hollow);
        }

        [Fact]
        public void WhatGoesInIsWhatComesBackOut()
        {
            var store = new LotStore();
            var resident = Active();
            var neighbour = Hollow();
            store.SetActive(21, resident);
            store.SetHollow(22, neighbour);

            VMMarshal active;
            Assert.True(store.TryGetActive(21, out active));
            Assert.Same(resident, active);

            VMHollowMarshal hollow;
            Assert.True(store.TryGetHollow(22, out hollow));
            Assert.Same(neighbour, hollow);
        }

        [Fact]
        public void ResidentAndHollowAreDifferentAnswersToTheSameQuestion()
        {
            //One entry, one payload: a lot is either the one being simulated or a neighbour's
            //contents, and asking the wrong question has to be a clean no rather than a maybe.
            var store = new LotStore();
            store.SetHollow(21, Hollow());

            VMMarshal active;
            Assert.False(store.TryGetActive(21, out active));

            var resident = Active();
            store.SetActive(21, resident);

            VMHollowMarshal hollow;
            Assert.False(store.TryGetHollow(21, out hollow));
        }

        [Fact]
        public void AHollowedLotStopsAnsweringAsActiveAndAnActivatedOneStopsAnsweringAsHollow()
        {
            //The two transitions that have to hold in each direction. §5.6 is what makes them
            //lossless: the lot's contents are committed to its own HouseNN.iff before the store is
            //asked to remember a different shape for it, so the displaced marshal has somewhere
            //else to live.
            var store = new LotStore();
            store.SetActive(21, Active());
            store.SetHollow(21, Hollow());
            Assert.Equal(NeighbourhoodLotState.Hollow, store.StateOf(21));
            Assert.Equal(1, store.Count);

            store.SetActive(21, Active());
            Assert.Equal(NeighbourhoodLotState.Active, store.StateOf(21));
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void NothingToRememberIsTheSameAsNothingToStore()
        {
            //A caller that has no marshal in hand should not need a second way of saying "forget
            //this lot", and a store that kept an entry with nothing in it would report a count that
            //disagrees with its own state function.
            var store = new LotStore();
            store.SetActive(21, Active());
            store.SetActive(21, null);
            Assert.Equal(0, store.Count);
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(21));

            store.SetHollow(22, Hollow());
            store.SetHollow(22, null);
            Assert.Equal(0, store.Count);
            Assert.Empty(store.LoadedLots);
        }

        [Fact]
        public void ForgettingALotThatIsNotThereIsNotAnError()
        {
            //The caller asking for an empty store and the caller that already has one are the same
            //situation, and neither is worth a log line.
            var store = new LotStore();

            store.Remove(21);
            store.Remove(21);

            Assert.Equal(0, store.Count);
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(21));
        }

        [Fact]
        public void ClearingForgetsEveryLotAtOnce()
        {
            //What leaving a neighbourhood does, including the lots that were only neighbours.
            var store = new LotStore();
            store.SetActive(21, Active());
            store.SetHollow(22, Hollow());
            store.SetHollow(23, Hollow());
            Assert.Equal(3, store.Count);

            store.Clear();

            Assert.Equal(0, store.Count);
            Assert.Empty(store.LoadedLots);
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(21));
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(22));
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(23));
        }

        [Fact]
        public void OneLotsStateSaysNothingAboutAnother()
        {
            var store = new LotStore();
            var resident = Active();
            store.SetActive(21, resident);

            store.SetHollow(22, Hollow());

            VMMarshal active;
            Assert.True(store.TryGetActive(21, out active));
            Assert.Same(resident, active);
            Assert.Equal(NeighbourhoodLotState.Hollow, store.StateOf(22));
            Assert.Equal(2, store.Count);
        }

        // ---- the lot list ---------------------------------------------------------------------------

        [Fact]
        public void TheLoadedLotsAreInAscendingOrderAndSkipTheOnesWithNothingInThem()
        {
            //Ascending, like NeighbourhoodLayout.Lots, so "the next lot" means the same thing in
            //both. Absent rather than listed: a lot with no contents is not something a caller can
            //do anything with, and listing it would make the count and the list disagree.
            var store = new LotStore();
            store.SetHollow(23, Hollow());
            store.SetActive(7, Active());
            store.SetHollow(11, Hollow());
            //22 is deliberately never stored

            Assert.Equal(new short[] { 7, 11, 23 }, store.LoadedLots);
            Assert.Equal(3, store.Count);
        }

        [Fact]
        public void TheLoadedLotsAreASnapshotTheStoreCanBeWrittenBehind()
        {
            //A lot switching mid-loop is ordinary - B6 ticks stored lots - so the list a caller is
            //walking cannot be the store's own working set.
            var store = new LotStore();
            store.SetActive(21, Active());
            var lots = store.LoadedLots;

            store.SetHollow(22, Hollow());
            store.Remove(21);

            Assert.Equal(new short[] { 21 }, lots);
            Assert.Equal(new short[] { 22 }, store.LoadedLots);
        }

        [Fact]
        public void TheStoresStatesAreTheOnesTheLayoutAlreadyNames()
        {
            //§15 declared NeighbourhoodLotState for exactly this, and the values are written into
            //the schema-2 sidecar as integers, so the store has to answer with those three and no
            //others - a store that invented a fourth would be writing a sidecar nobody can read.
            var store = new LotStore();
            store.SetActive(21, Active());
            store.SetHollow(22, Hollow());

            Assert.Equal(0, (int)NeighbourhoodLotState.Unloaded);
            Assert.Equal(1, (int)NeighbourhoodLotState.Hollow);
            Assert.Equal(2, (int)NeighbourhoodLotState.Active);

            Assert.Equal(NeighbourhoodLotState.Active, store.StateOf(21));
            Assert.Equal(NeighbourhoodLotState.Hollow, store.StateOf(22));
            Assert.Equal(NeighbourhoodLotState.Unloaded, store.StateOf(23));
        }

        // ---- what the store is not -------------------------------------------------------------------

        [Fact]
        public void TheStoreExposesNoStaticMutableState()
        {
            //A world owns one store. A second one reachable from anywhere would be two authorities
            //for which lots are resident, which is the failure this type exists to prevent.
            foreach (var field in typeof(LotStore).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                Assert.True(field.IsLiteral, "LotStore." + field.Name + " is mutable static state");
            }
        }

        [Fact]
        public void TheStoreIsAPlainDictionaryOfNumbersAndMarshals()
        {
            //No level, no world, no engine type: the keys are shorts and the values are the two
            //marshal types the file format already defines, so a store can be built and asserted on
            //with no content manager in existence - which is what every test above just did.
            foreach (var method in typeof(LotStore).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName) continue;
                foreach (var parameter in method.GetParameters())
                {
                    //an out parameter arrives as VMMarshal&; the payload is the element type
                    var type = parameter.ParameterType.IsByRef
                        ? parameter.ParameterType.GetElementType()
                        : parameter.ParameterType;
                    Assert.True(type.IsPrimitive || type == typeof(string) || type == typeof(VMMarshal)
                        || type == typeof(VMHollowMarshal),
                        "LotStore." + method.Name + " takes a " + type.Name);
                }
            }
        }

        [Fact]
        public void AStoreDescribesItselfInBothStates()
        {
            var store = new LotStore();
            store.SetActive(21, Active());
            store.SetHollow(22, Hollow());
            store.SetHollow(23, Hollow());

            //a ToString is how a failure in a log or an assert message says what the store held
            Assert.Contains("1 active", store.ToString());
            Assert.Contains("2 hollow", store.ToString());
        }
    }
}
