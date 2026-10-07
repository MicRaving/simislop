/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.Utils;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The House panel's hire-a-service rows, exercised through <see cref="HouseServices"/>.
    ///
    /// <para>
    /// Retail's House panel had two halves: the household chore list, and hire-a-service for the
    /// maid, the gardener and the repairman. The first half has no data behind it anywhere - not
    /// in this tree, not in the shipped scripts, and not in a saved lot - and the panel says so
    /// with the reason. The second half is real, and it lives on the lot's NPC controller rather
    /// than in person data.
    /// </para>
    ///
    /// <para>
    /// The panel used to read a per-sim "rehire ticker" out of person data 88, 89 and 90. A
    /// census of every <c>MyPersonData</c> / <c>StackPersonData</c> / <c>NeighborPersonData</c>
    /// expression over all eleven shipped <c>.far</c> cabinets finds 87 distinct slots with a
    /// maximum index of 87 and <b>no access at all</b> to 88, 89 or 90, so all three rows read
    /// zero forever. What is pinned here instead is the lot object the phone's own
    /// <c>'... available?'</c> queries read, and the attribute-per-service mapping that object
    /// carries.
    /// </para>
    /// </summary>
    public sealed class HouseServicesTests
    {
        // ---- the three services -------------------------------------------------------------------

        [Fact]
        public void ThereAreThreeServices()
        {
            Assert.Equal(3, HouseServices.All.Length);
            Assert.Equal(HouseholdService.Maid, HouseServices.All[0]);
            Assert.Equal(HouseholdService.Gardener, HouseServices.All[1]);
            Assert.Equal(HouseholdService.Repairman, HouseServices.All[2]);
        }

        [Fact]
        public void EveryServiceHasAFindableIndex()
        {
            foreach (var service in HouseServices.All)
            {
                Assert.InRange(HouseServices.IndexOf(service), 0, HouseServices.All.Length - 1);
            }
            Assert.Equal(-1, HouseServices.IndexOf((HouseholdService)99));
        }

        [Fact]
        public void EveryServiceHasACaptionAndItsOwnTableId()
        {
            Assert.Equal(3, HouseServices.NameFallback.Length);
            Assert.Equal(3, HouseServices.NameId.Length);
            Assert.Equal(3, HouseServices.NameId.Distinct().Count());
            Assert.Equal(3, HouseServices.NameFallback.Distinct().Count());

            foreach (var service in HouseServices.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(HouseServices.NameOf(service)));
            }
        }

        [Fact]
        public void AnUnknownServiceFallsBackToItsOwnName()
        {
            // The guard for a service added to the enum without a caption: the identifier is
            // better than a blank row, and it is the number rather than a word only because
            // the enum has no member for it.
            Assert.Equal(((HouseholdService)99).ToString(), HouseServices.NameOf((HouseholdService)99));
        }

        // ---- where the hire state actually lives ----------------------------------------------------

        [Fact]
        public void TheHireFlagsAreTheNpcControllersAttributesOneTwoAndThree()
        {
            // Objects/NPCController.iff OBJD 0x41A7, 16 attributes. 'Block Maid' (4097) writes
            // StackAttrs[1] = 5, 'Block Gardener' (4099) StackAttrs[2] = 5, 'Block Repairman'
            // (4103) StackAttrs[3] = 5, and the three sibling queries 4098/4100/4102 test
            // StackAttrs[1]/[2]/[3] against zero. One wrong index and the panel reports the
            // repairman under the maid.
            Assert.Equal(3, HouseServices.HireFlagAttributes.Length);
            Assert.Equal(1, HouseServices.HireFlagAttributeOf(HouseholdService.Maid));
            Assert.Equal(2, HouseServices.HireFlagAttributeOf(HouseholdService.Gardener));
            Assert.Equal(3, HouseServices.HireFlagAttributeOf(HouseholdService.Repairman));
        }

        [Fact]
        public void TheHireFlagAttributesAreDistinctAndInsideTheControllersAttributeArray()
        {
            Assert.Equal(HouseServices.HireFlagAttributes.Length,
                HouseServices.HireFlagAttributes.Distinct().Count());
            // OBJD 0x41A7 declares attrs = 16, and a read past the end is a silently zero read.
            Assert.All(HouseServices.HireFlagAttributes,
                index => Assert.InRange(index, 0, NpcControllerAttributeCount - 1));
        }

        /// <summary>
        /// <c>Objects/NPCController.iff</c> OBJD 0x41A7's <c>NumAttributes</c>, written out here
        /// rather than read so that a test does not need the retail install to exist.
        /// </summary>
        private const int NpcControllerAttributeCount = 16;

        [Fact]
        public void AnUnknownServiceHasNoHireFlagAttribute()
        {
            // -1 rather than 0, so the read helper can tell "no attribute" from "attribute one".
            Assert.Equal(-1, HouseServices.HireFlagAttributeOf((HouseholdService)99));
        }

        [Fact]
        public void TheControllerIsTheGuidThePhoneSearchesFor()
        {
            // PhoneGlobals.iff 8241/8242/8243 ('gardener/maid/repairman available?') do
            // set_to_next with SearchType 4 (ObjectOfType) on this GUID, and then run the three
            // trees by the names in Global.iff STR 0x012F. The panel finds the same object by the
            // same GUID, so the two cannot disagree about which lot is being asked about.
            Assert.Equal(0x70F69082u, HouseServices.ControllerGuid);
        }

        [Fact]
        public void TheRetailQueriesAreTheThreeThePhoneRunsByName()
        {
            // Global.iff STR 0x012F: [61] "Is Gardener Blocked?", [62] "Is Maid Blocked?",
            // [63] "Is Repairman Blocked?". These are the only three places in the shipped data
            // where the three hire flags are named at all, which is why they are recorded here.
            Assert.Equal(new[]
            {
                "Is Maid Blocked?",
                "Is Gardener Blocked?",
                "Is Repairman Blocked?",
            }, HouseServices.RetailQueryTreeNames);
        }

        [Fact]
        public void EachServiceHasItsOwnNpcGuid()
        {
            // Objects/People/{maid,gardener,repairman}.iff OBJD 0x0080. The first two are the
            // GUIDs PhoneGlobals.iff 8295 ('have Maid?') and 8294 ('have Gardener?') search for;
            // the third is the one 8306 ('Interaction - Call Repairman TEST') searches for.
            Assert.Equal(3, HouseServices.ServiceNpcGuids.Length);
            Assert.Equal(3, HouseServices.ServiceNpcGuids.Distinct().Count());
            Assert.Equal(0xC61931DCu, HouseServices.ServiceNpcGuids[0]);
            Assert.Equal(0xD62B91B4u, HouseServices.ServiceNpcGuids[1]);
            Assert.Equal(0x565AA54Bu, HouseServices.ServiceNpcGuids[2]);
        }

        [Fact]
        public void NoServiceNpcIsTheControllerItself()
        {
            // The controller is a lot object that is not a person; a service NPC is a person.
            // The two lists overlapping would mean the panel looked for the controller where it
            // was looking for the maid.
            Assert.DoesNotContain(HouseServices.ControllerGuid, HouseServices.ServiceNpcGuids);
        }

        // ---- the classification -------------------------------------------------------------------

        [Fact]
        public void AZeroFlagMeansNobodyIsOnTheBooks()
        {
            // The predicate is retail's own: 'Is X Blocked?' is `attribute == 0`, and
            // 'init tree' (4101) writes 0 into all three flags, so zero is the fresh-lot state.
            Assert.Equal(HouseholdServiceState.NotHired, HouseServices.Classify(0, false));
            Assert.Equal(HouseholdServiceState.NotHired, HouseServices.Classify(0, true));
        }

        [Fact]
        public void ASetFlagWithTheNpcOnTheLotIsHiredAndOnDuty()
        {
            // 'Block Maid' writes 5, so 5 is the value a hired service actually carries.
            Assert.Equal(HouseholdServiceState.HiredOnDuty, HouseServices.Classify(5, true));
        }

        [Fact]
        public void ASetFlagWithTheNpcOffTheLotIsHiredButNotWorking()
        {
            // The two are different facts and conflating them is how a panel ends up claiming a
            // maid is in the kitchen after she has gone home.
            Assert.Equal(HouseholdServiceState.HiredOffDuty, HouseServices.Classify(5, false));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(1000)]
        [InlineData(-7)]
        public void AnyNonZeroFlagCountsAsHired(int flag)
        {
            // The shipped setter writes exactly 5 and the shipped query tests exactly 0, so the
            // panel classifies on zero-or-not rather than on 5 - anything else would read a
            // value some other path had left as "not 5" as not hired.
            Assert.Equal(HouseholdServiceState.HiredOnDuty, HouseServices.Classify(flag, true));
            Assert.Equal(HouseholdServiceState.HiredOffDuty, HouseServices.Classify(flag, false));
        }

        [Fact]
        public void AMissingFlagIsNotTheSameAsNobodyHired()
        {
            // A null flag means there is no NPC controller on the lot, which is a different claim
            // from "this household has hired nobody" and has to survive being put through the
            // same path.
            Assert.Equal(HouseholdServiceState.NoController, HouseServices.Classify(null, true));
            Assert.Equal(HouseholdServiceState.NoController, HouseServices.Classify(null, false));
        }

        [Fact]
        public void ThereAreFourStatesAndNoRowCanBeBlank()
        {
            var states = new[]
            {
                HouseholdServiceState.NoController,
                HouseholdServiceState.NotHired,
                HouseholdServiceState.HiredOnDuty,
                HouseholdServiceState.HiredOffDuty,
            };

            Assert.Equal(states.Length, states.Distinct().Count());
            Assert.All(states, state => Assert.False(string.IsNullOrWhiteSpace(HouseServices.StateCaption(state))));
        }

        [Fact]
        public void TheStateCaptionsAreTheTableIdsTheyClaimToBe()
        {
            // A test process never builds GameFacade.Strings, so this is the fallback path: the
            // state a stripped install is in, and the state an untranslated language is in.
            Assert.Null(FSO.Client.GameFacade.Strings);

            Assert.Equal("Can be hired", HouseServices.StateCaption(HouseholdServiceState.NotHired));
            Assert.Equal("Hired - on duty now", HouseServices.StateCaption(HouseholdServiceState.HiredOnDuty));
            Assert.Equal("Hired - not on the lot", HouseServices.StateCaption(HouseholdServiceState.HiredOffDuty));
        }

        [Fact]
        public void TheHireableStateReusesTheStringThatWasAlreadyInTheTable()
        {
            // 537 was written for the old ticker's zero case and says exactly the right thing for
            // this state, so the case a lot with nobody hired lands in - the common one - is still
            // translatable without a new entry.
            Assert.Equal(SimitoneStrings.Get(HouseServices.AvailableId, "Can be hired"),
                HouseServices.StateCaption(HouseholdServiceState.NotHired));
        }

        [Fact]
        public void TheRetiredTickerIdIsKeptAndNotUsed()
        {
            // 538 ("Next chance in %s day(s)") is in the shipped table and the table cannot be
            // edited from here. The constant is kept so the id is still named in the source, and
            // nothing calls it - there is no ticker left to caption.
            Assert.Equal("538", HouseServices.TickerId);
            Assert.Null(typeof(HouseServices)
                .GetMethod("TickerCaption", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
        }

        [Fact]
        public void ThePanelNoLongerReadsThePersonDataTickers()
        {
            // The old rows came out of person data 88/89/90, which no shipped script writes. If
            // the arrays come back the panel will be reading dead data again, and the census
            // behind that claim is in the file's own remarks.
            Assert.Null(typeof(HouseServices)
                .GetField("TickerSlots", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            Assert.Null(typeof(HouseServices)
                .GetMethod("DaysUntilHireable", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
        }

        // ---- the reads, with no lot ----------------------------------------------------------------

        [Fact]
        public void ReadingWithNoEngineYieldsNoFlagRatherThanZero()
        {
            // The panel calls these every frame from its update loop, including before a lot has
            // finished loading. Folding a missing lot into a zero would render as "Can be hired"
            // on every row, which is the one thing the panel must never assert without evidence.
            Assert.Null(HouseServices.ReadHireFlag(null, HouseholdService.Maid));
            Assert.Null(HouseServices.ReadHireFlag(null, HouseholdService.Gardener));
            Assert.Null(HouseServices.ReadHireFlag(null, HouseholdService.Repairman));
        }

        [Fact]
        public void AnUnknownServiceIsNotOnTheLotForAnyValue()
        {
            Assert.False(HouseServices.IsNpcOnLot(null, (HouseholdService)99));
        }

        [Fact]
        public void FindingAnObjectWithNoEngineFindsNothing()
        {
            // VMObjectQueries.GetObjectsByGUID returns null rather than an empty list for a GUID
            // nothing carries, so the helper has to survive the null as well as the empty case.
            Assert.Null(HouseServices.FindOnLot(null, HouseServices.ControllerGuid));
        }

        [Fact]
        public void TheStateOfNoLotIsTheNoControllerState()
        {
            // The composition of the three steps, pinned so a reordering cannot quietly turn
            // "no lot" into "nobody hired".
            Assert.Equal(HouseholdServiceState.NoController,
                HouseServices.StateOf(null, HouseholdService.Maid));
        }

        // ---- the chores half ----------------------------------------------------------------------

        [Fact]
        public void TheHousePanelsWordsComeOutOfSimitonesOwnTable()
        {
            Assert.Null(FSO.Client.GameFacade.Strings);

            Assert.Equal("Chores", SimitoneStrings.Get(HouseServices.ChoresTitleId, "Chores"));
            Assert.Equal("Hire a Service", SimitoneStrings.Get(HouseServices.HireTitleId, "Hire a Service"));
            Assert.Equal("Maid", HouseServices.NameOf(HouseholdService.Maid));
            Assert.Equal("Gardener", HouseServices.NameOf(HouseholdService.Gardener));
            Assert.Equal("Repairman", HouseServices.NameOf(HouseholdService.Repairman));
        }

        [Fact]
        public void TheChoresHalfSaysSomethingRatherThanShowingAnEmptyBox()
        {
            // An empty list next to the word "Chores" reads as a household with nothing to do.
            Assert.False(string.IsNullOrWhiteSpace(SimitoneStrings.Get(HouseServices.ChoresBodyId,
                "Chores are assigned by the family, not by the game: the household's chore list "
                + "lives in the lot's own data and nothing here can read it.")));
        }

        [Fact]
        public void TheChoresHalfSaysWhyThereIsNothingToRead()
        {
            // The claim the panel makes is a positive one - the data does not exist - so the
            // sentence has to carry it, not a shrug. "no data" was the old wording and it is
            // wrong: a lot SIMI has 32 globals and the scripts use every one they touch, so
            // there is no place a chore list could have been kept.
            var why = HouseServices.ChoresWhy();

            Assert.False(string.IsNullOrWhiteSpace(why));
            Assert.Contains("chore", why.ToLowerInvariant());
            // A shrug would be a sentence with no subject and no reason in it.
            Assert.True(why.Length > 80, "the chores reason is a stub: " + why);
        }

        [Fact]
        public void TheChoresReasonHasItsOwnTableIdAndDoesNotCollideWithAPinnedOne()
        {
            // 539 is a new id, so the shipped string table has no entry for it and the call
            // site's English is what shows until a translator adds one. The ids the shared
            // LivePanelStringsTests pins are 530 to 538; this must not be one of them.
            Assert.Equal("539", HouseServices.ChoresWhyId);
            Assert.DoesNotContain(HouseServices.ChoresWhyId, new[]
            {
                HouseServices.ChoresTitleId, HouseServices.ChoresBodyId, HouseServices.HireTitleId,
                HouseServices.HireBodyId, HouseServices.AvailableId, HouseServices.TickerId,
            });
        }

        [Fact]
        public void TheThreeHireStateIdsAreNewAndDistinct()
        {
            var ids = new[]
            {
                HouseServices.HiredOnDutyId, HouseServices.HiredOffDutyId, HouseServices.NoControllerId,
            };

            Assert.Equal(ids.Length, ids.Distinct().Count());
            foreach (var id in ids)
            {
                Assert.Equal(3, id.Length);
                Assert.True(int.Parse(id) >= 539, id + " would collide with a pinned table id");
            }
        }
    }
}
