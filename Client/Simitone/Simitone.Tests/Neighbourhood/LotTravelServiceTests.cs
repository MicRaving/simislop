/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// B3's switch, with no VM, no content and no screen.
    ///
    /// <para>
    /// The ordering is the behaviour: there is one live VM, so a switch that loads the
    /// destination before stashing the origin destroys the state travel is supposed to preserve,
    /// and a switch that restores an entry without consuming it replays a stale snapshot forever.
    /// Neither shows up in a return value - both are visible only in the order and identity of
    /// the calls the host received, so <see cref="RecordingLotTravelHost"/> records every call as
    /// a string and these tests assert on that sequence.
    /// </para>
    /// <para>
    /// <c>new VMMarshal()</c> stands in for a marshalled lot. It is a real instance of the real
    /// type and it never gets deserialised, which is what lets the resume path be tested without
    /// a VM, a content manager or a graphics device.
    /// </para>
    /// </summary>
    public sealed class LotTravelServiceTests
    {
        private const int Home = 21;
        private const int Neighbour = 26;
        private const int Vacation = 42;

        private static readonly LotTravelService Service = new LotTravelService();

        // ---- the fake host -----------------------------------------------------------------------

        /// <summary>
        /// A host that answers from a dictionary and remembers the order it was asked in.
        /// </summary>
        private sealed class RecordingLotTravelHost : ILotTravelHost
        {
            public readonly List<string> Calls = new List<string>();
            public readonly Dictionary<short, VMMarshal> Saved = new Dictionary<short, VMMarshal>();
            public readonly List<NeighbourhoodTransitRecord> Hops = new List<NeighbourhoodTransitRecord>();

            public int CurrentHouseId { get; set; }

            public int? ActiveFamilyHouseNumber { get; set; }

            public string PathPrefix { get; set; }

            public bool? LastDowntown { get; private set; }

            public VMMarshal LastLoadedMarshal { get; private set; }

            public string LastLoadedPath { get; private set; }

            public int? LastLoadedHouseId { get; private set; }

            public RecordingLotTravelHost(int currentHouseId)
            {
                CurrentHouseId = currentHouseId;
                PathPrefix = "C:/Neighbourhoods/Houses/";
            }

            public string GetHousePath(int houseId)
            {
                Calls.Add("getpath:" + houseId);
                //a null prefix is how this fake is told the lot has no house file at all, which is
                //the one case where the screen's real builder answers with nothing
                if (PathPrefix == null) return null;
                return PathPrefix + "House" + houseId + ".iff";
            }

            public VMMarshal SaveCurrentLot()
            {
                Calls.Add("save:" + CurrentHouseId);
                var marshal = new VMMarshal();
                //the screen's own store is keyed by house number; the fake does the same thing
                Saved[(short)CurrentHouseId] = marshal;
                return marshal;
            }

            public bool TryRestoreSaved(short lot, out VMMarshal marshal)
            {
                Calls.Add("restore:" + lot);
                return Saved.TryGetValue(lot, out marshal);
            }

            public void RemoveSaved(short lot)
            {
                Calls.Add("remove:" + lot);
                Saved.Remove(lot);
            }

            public void SetDowntown(bool downtown)
            {
                Calls.Add("downtown:" + (downtown ? "on" : "off"));
                LastDowntown = downtown;
            }

            public void LoadLot(string path, int houseId)
            {
                Calls.Add("load-file:" + houseId);
                LastLoadedPath = path;
                LastLoadedHouseId = houseId;
            }

            public void LoadLot(VMMarshal marshal, int houseId)
            {
                Calls.Add("load-state:" + houseId);
                LastLoadedMarshal = marshal;
                LastLoadedHouseId = houseId;
            }

            public Action<NeighbourhoodTransitRecord> RecordTransit
            {
                get { return record => { Calls.Add("hop:" + record.Origin + "->" + record.Destination); Hops.Add(record); }; }
            }
        }

        private static RecordingLotTravelHost Host(int current)
        {
            return new RecordingLotTravelHost(current) { ActiveFamilyHouseNumber = Home };
        }

        private static int IndexOf(IList<string> calls, string call)
        {
            return calls.IndexOf(call);
        }

        // ---- ordering ---------------------------------------------------------------------------

        [Fact]
        public void TheCurrentLotIsStashedBeforeTheDestinationIsLoaded()
        {
            //§11 item 5. the live VM is destroyed inside the load, so a stash after it is a stash
            //of whatever the new lot happens to be
            var host = Host(Home);

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason));

            Assert.True(IndexOf(host.Calls, "save:21") >= 0, reason);
            Assert.True(IndexOf(host.Calls, "save:21") < IndexOf(host.Calls, "load-file:26"),
                "stash must precede the load, calls were: " + string.Join(", ", host.Calls));
        }

        [Fact]
        public void TheWholeOrderIsStashDowntownThenLoadThenHop()
        {
            //one sequence, asserted once, so a reordering cannot pass by accident on any one pair
            var host = Host(Home);

            string reason;
            Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.VmRequest), out reason);

            Assert.Equal(
                new[] { "save:21", "downtown:on", "restore:26", "getpath:26", "load-file:26", "hop:21->26" },
                host.Calls);
        }

        [Fact]
        public void DowntownIsSetBeforeTheLoadBecauseTheLoadReadsIt()
        {
            //the screen's InitializeLot picks the family and the downtown time from this flag
            var host = Host(Home);

            string reason;
            Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason);

            Assert.True(IndexOf(host.Calls, "downtown:on") < IndexOf(host.Calls, "load-file:26"),
                string.Join(", ", host.Calls));
        }

        [Fact]
        public void TheStashHappensEvenForAMoveInAndForThePicker()
        {
            //the reason is a label, not a behaviour: all three callers share one switch
            foreach (TransitionReason why in new[]
            {
                TransitionReason.VmRequest, TransitionReason.Picker, TransitionReason.MoveIn })
            {
                var host = Host(Home);

                string reason;
                Assert.True(Service.TryTransition(host, new LotTransitionRequest(Neighbour, why), out reason));
                Assert.Equal("save:21", host.Calls[0]);
                Assert.Contains(why.ToString() == "VmRequest" ? "vm request"
                    : why.ToString() == "Picker" ? "picker" : "move in", reason);
            }
        }

        // ---- stash exactly once ------------------------------------------------------------------

        [Fact]
        public void ASameLotSwitchDoesNotStash()
        {
            //stashing here would overwrite the live state the restore is about to load back
            var host = Host(Home);

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Home, TransitionReason.Picker), out reason));

            Assert.DoesNotContain("save:21", host.Calls);
            Assert.Empty(host.Saved);
        }

        [Fact]
        public void NoLotLoadedMeansNothingIsStashed()
        {
            //0 is "no lot", and keying a stash under it would leave an entry no lot ever claims
            var host = Host(0);

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.MoveIn), out reason));

            Assert.DoesNotContain("save:0", host.Calls);
            Assert.Empty(host.Saved);
        }

        [Fact]
        public void ARoundTripStashesEachLotOnceAndRestoresEachLotItsOwnState()
        {
            //the regression this whole area exists for: home -> downtown -> vacation -> home must
            //put every lot back as it was left, not to the one-slot's snapshot
            var host = Host(Home);

            string reason;
            Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason);
            var leftHome = host.Saved[(short)Home];

            host.CurrentHouseId = Neighbour;
            Service.TryTransition(host, new LotTransitionRequest(Vacation, TransitionReason.Picker), out reason);
            var leftNeighbour = host.Saved[(short)Neighbour];

            host.CurrentHouseId = Vacation;
            Service.TryTransition(host, new LotTransitionRequest(Home, TransitionReason.Picker), out reason);
            var leftVacation = host.Saved[(short)Vacation];

            //one stash per lot, no more: a second one would be a duplicate of the same state
            Assert.Equal(1, CountOf(host.Calls, "save:21"));
            Assert.Equal(1, CountOf(host.Calls, "save:26"));
            Assert.Equal(1, CountOf(host.Calls, "save:42"));

            //each lot kept its own state, so downtown's never replaced home's
            Assert.NotSame(leftNeighbour, leftHome);
            Assert.NotSame(leftVacation, leftHome);

            //the first visit to a lot reads its own file; only the way back finds state in memory
            Assert.Equal(1, CountOf(host.Calls, "load-file:26"));
            Assert.Equal(1, CountOf(host.Calls, "load-file:42"));
            Assert.Equal(1, CountOf(host.Calls, "load-state:21"));

            //and the player is standing in the marshal home was left in
            Assert.Same(leftHome, LoadedOnLastLoadOf(host, Home));
        }

        private static VMMarshal LoadedOnLastLoadOf(RecordingLotTravelHost host, int houseId)
        {
            //the host keeps only the last load, so the round trip asserts on the state the third
            //switch produced for home - which is the one the test is about
            Assert.Equal(Home, host.LastLoadedHouseId);
            return host.LastLoadedMarshal;
        }

        [Fact]
        public void ComingHomeResumesTheStateLeftBehindAndNotTheCurrentLot()
        {
            var host = Host(Home);
            var homeState = new VMMarshal();
            host.Saved[(short)Home] = homeState;
            host.CurrentHouseId = Vacation;

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Home, TransitionReason.Picker), out reason));

            Assert.Same(homeState, host.LastLoadedMarshal);
            Assert.Null(host.LastLoadedPath);
        }

        // ---- precedence and consume-once ----------------------------------------------------------

        [Fact]
        public void PreservedStateOutranksTheLotFile()
        {
            //TravelSave states the precedence in one place and both halves depend on it: an
            //in-session snapshot always beats the on-disk travel slot and the shipped template
            var host = Host(Home);
            host.Saved[(short)Neighbour] = new VMMarshal();

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason));

            Assert.Contains("load-state:26", host.Calls);
            Assert.DoesNotContain("load-file:26", host.Calls);
        }

        [Fact]
        public void APreservedEntryIsConsumedSoTheNextVisitReloadsFromDisk()
        {
            //LotTravelTests pins the same half on a bare dictionary; this pins it through the
            //service, where the removal has to happen at the right moment to matter
            var host = Host(Home);
            host.Saved[(short)Neighbour] = new VMMarshal();

            string reason;
            Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason);
            Assert.False(host.Saved.ContainsKey((short)Neighbour));

            //consume-once also means the removal precedes the load, so a load that throws cannot
            //leave a spent snapshot to be replayed on the next visit
            Assert.True(IndexOf(host.Calls, "remove:26") < IndexOf(host.Calls, "load-state:26"),
                string.Join(", ", host.Calls));

            host.CurrentHouseId = Home;
            Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason);
            Assert.Contains("load-file:26", host.Calls);
        }

        [Fact]
        public void AStoreThatClaimsToHoldNothingUsableFallsBackToTheFile()
        {
            //a null marshal handed to a load fails deep inside the lot, so the entry is treated
            //as absent - and is not consumed, because nothing was played
            var host = Host(Home);
            host.Saved[(short)Neighbour] = null;

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason));

            Assert.Contains("load-file:26", host.Calls);
            Assert.DoesNotContain("remove:26", host.Calls);
        }

        [Fact]
        public void AnUnvisitedLotFallsBackToItsOwnHouseFile()
        {
            var host = Host(Home);

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason));

            Assert.Contains("load-file:26", host.Calls);
            Assert.Equal("C:/Neighbourhoods/Houses/House26.iff", host.LastLoadedPath);
            Assert.Equal(Neighbour, host.LastLoadedHouseId);
        }

        // ---- downtown ---------------------------------------------------------------------------

        [Theory]
        [InlineData(21, true, false)]   // the family's own house
        [InlineData(26, true, true)]    // downtown
        [InlineData(42, true, true)]    // vacation
        public void ALotThatIsNotTheFamilysOwnHouseIsDowntown(int houseId, bool hasFamily, bool expected)
        {
            Assert.Equal(expected, LotTravelService.IsDowntownLot(houseId, hasFamily ? (int?)Home : null));
        }

        [Fact]
        public void WithNoFamilyNothingIsDowntown()
        {
            //the original expression's "?? target" makes this false for every lot, and it is what
            //keeps a simless load off the travel-slot path as well
            var host = Host(Home);
            host.ActiveFamilyHouseNumber = null;

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.MoveIn), out reason));

            Assert.Equal(false, host.LastDowntown);
        }

        [Fact]
        public void TheDowntownFlagIsAskedForTheDestinationNotTheOrigin()
        {
            var host = Host(Vacation);

            string reason;
            Service.TryTransition(host, new LotTransitionRequest(Home, TransitionReason.Picker), out reason);

            Assert.Equal(false, host.LastDowntown);
        }

        // ---- the hop ----------------------------------------------------------------------------

        [Fact]
        public void ACompletedHopIsRecordedWithTheOriginAndDestination()
        {
            var host = Host(Home);

            string reason;
            Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.VmRequest), out reason);

            var hop = Assert.Single(host.Hops);
            Assert.Equal(Home, hop.Origin);
            Assert.Equal(Neighbour, hop.Destination);
            Assert.Equal(LotTravelService.DiscreteTravelMode, hop.Mode);
            Assert.True(hop.IsComplete);
            Assert.True(hop.IsHop);
            Assert.Equal(0, hop.Sim); // the record's own "belongs to nobody"
        }

        [Fact]
        public void TheHopIsRecordedLastSoACompletedHopAlwaysFollowsItsLoad()
        {
            //B7's resume path acts on a complete hop, so it may not be written for a lot that
            //never came up
            var host = Host(Home);

            string reason;
            Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason);

            Assert.True(IndexOf(host.Calls, "hop:21->26") > IndexOf(host.Calls, "load-file:26"),
                string.Join(", ", host.Calls));
        }

        [Fact]
        public void ASameLotSwitchRecordsNoHop()
        {
            //origin == destination is not a hop by the record's own test, and the world already
            //has a lot active
            var host = Host(Home);

            string reason;
            Assert.True(Service.TryTransition(host, new LotTransitionRequest(Home, TransitionReason.Picker), out reason));

            Assert.Empty(host.Hops);
        }

        [Fact]
        public void ARefusedRequestRecordsNoHop()
        {
            var host = Host(Home);

            string reason;
            Assert.False(Service.TryTransition(host, new LotTransitionRequest(0, TransitionReason.Picker), out reason));
            Assert.False(Service.TryTransition(host, new LotTransitionRequest(99, TransitionReason.Picker), out reason));

            Assert.Empty(host.Hops);
            Assert.Empty(host.Calls);
        }

        // ---- refusals ----------------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(99)]
        [InlineData(1000)]
        public void ANonLotNumberIsRefusedBeforeTheHostIsTouched(int houseId)
        {
            //0 is "no lot" and 99 is the parser's sentinel; loading either would be loading the
            //wrong lot's contents
            var host = Host(Home);

            string reason;
            Assert.False(Service.TryTransition(host, new LotTransitionRequest(houseId, TransitionReason.Picker), out reason));

            Assert.Empty(host.Calls);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void NoHostIsRefused()
        {
            string reason;
            Assert.False(Service.TryTransition(null, new LotTransitionRequest(Home, TransitionReason.Picker), out reason));
            Assert.Contains("refused", reason);
        }

        [Fact]
        public void ADestinationWithNoHouseFileIsReportedRatherThanLoaded()
        {
            //the stash has already happened, so the switch stops with the player still on the
            //current lot and that lot's state in the store - recoverable, not lost
            var host = Host(Home);
            host.PathPrefix = null;

            string reason;
            Assert.False(Service.TryTransition(host, new LotTransitionRequest(Neighbour, TransitionReason.Picker), out reason));

            Assert.Contains("stashed 21", reason);
            Assert.DoesNotContain("load-file:26", host.Calls);
            Assert.Empty(host.Hops);
            Assert.True(host.Saved.ContainsKey((short)Home));
        }

        [Fact]
        public void EveryReasonIsNamedInTheLogLine()
        {
            Assert.Equal("vm request", LotTravelService.ReasonName(TransitionReason.VmRequest));
            Assert.Equal("picker", LotTravelService.ReasonName(TransitionReason.Picker));
            Assert.Equal("move in", LotTravelService.ReasonName(TransitionReason.MoveIn));
            Assert.Equal("unknown (99)", LotTravelService.ReasonName((TransitionReason)99));
        }

        [Fact]
        public void LotNumbersAreOneThroughNinetyEight()
        {
            Assert.False(LotTravelService.IsLotNumber(0));
            Assert.False(LotTravelService.IsLotNumber(-1));
            Assert.False(LotTravelService.IsLotNumber(99));
            Assert.True(LotTravelService.IsLotNumber(1));
            Assert.True(LotTravelService.IsLotNumber(LotTravelService.MaxLotNumber));
        }

        // ---- the hop ----------------------------------------------------------------------------

        [Fact]
        public void TheHopIsRecordedAfterTheLoad()
        {
            //the record claims arrival, so it may not be written ahead of the lot coming up
            var host = Host(Home);

            string reason;
            Service.TryTransition(
                host, new LotTransitionRequest(Neighbour, TransitionReason.VmRequest), out reason);

            Assert.Equal(
                new[] { "save:21", "downtown:on", "restore:26", "getpath:26", "load-file:26", "hop:21->26" },
                host.Calls);
        }

        [Fact]
        public void ARefusedTransitionRecordsNothing()
        {
            //a refused switch never loaded a lot, so there is nothing to claim arrival at
            var host = Host(Home);
            var request = new LotTransitionRequest(Neighbour, TransitionReason.Picker);

            string reason;
            //not a lot number, and no host at all: both refused before the first call
            Assert.False(Service.TryTransition(
                host, new LotTransitionRequest(0, TransitionReason.Picker), out reason));
            Assert.False(Service.TryTransition(null, request, out reason));

            //the one refusal that follows a stash, and it stops before a lot comes up
            host.PathPrefix = null;
            Assert.False(Service.TryTransition(host, request, out reason));

            Assert.Empty(host.Hops);
        }

        [Fact]
        public void ARequestToStringNamesTheLotAndTheReason()
        {
            Assert.Equal("26 via picker", new LotTransitionRequest(26, TransitionReason.Picker).ToString());
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static int CountOf(IList<string> calls, string call)
        {
            var count = 0;
            for (int i = 0; i < calls.Count; i++)
            {
                if (calls[i] == call) count++;
            }
            return count;
        }
    }
}
