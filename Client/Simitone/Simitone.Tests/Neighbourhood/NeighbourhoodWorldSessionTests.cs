/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The four repairs that make the live neighbourhood world a session object: it is keyed by the
    /// neighbourhood slot (and does not depend on a 3D surround), the played lot's clock is the
    /// authority while the lot is up, the lot stash is the world's <see cref="LotStore"/>, and the
    /// world's session is read from and written to the sidecar.
    /// </summary>
    /// <remarks>
    /// Everything here is pure: no game content, no graphics device and no VM. The capability
    /// switches are process-wide, so the class is serialised and puts them back.
    /// </remarks>
    [Collection(NonParallelCollection.Name)]
    public sealed class NeighbourhoodWorldSessionTests : IDisposable
    {
        private const int Slot = 4;
        private const int OtherSlot = 5;
        private const int MapSize = 96;

        private readonly string _root;

        public NeighbourhoodWorldSessionTests()
        {
            NeighbourhoodWorldFlags.AllOff();
            NeighbourhoodWorldCache.Clear();
            NeighbourhoodLifecycle.Forget();
            _root = Path.Combine(Path.GetTempPath(), "simitone-world-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            NeighbourhoodWorldFlags.Reset();
            NeighbourhoodWorldCache.Clear();
            NeighbourhoodLifecycle.Forget();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static NeighbourhoodLayout Layout(int slot = Slot)
        {
            return NeighbourhoodLayoutBuilder.PackOnGrid(new List<NeighbourhoodLotRead>
            {
                new NeighbourhoodLotRead { Number = 21, MapX = 40, MapY = 36, HasHouse = true, Size = 24 },
                new NeighbourhoodLotRead { Number = 22, MapX = 60, MapY = 38, HasHouse = true, Size = 24 }
            }, slot, 555, MapSize);
        }

        private static NeighbourhoodMap Map()
        {
            return NeighbourhoodMap.Create(MapSize);
        }

        private static void On(NeighbourhoodWorldFlag flag)
        {
            NeighbourhoodWorldFlags.Set(flag, true, save: false);
        }

        private static VMClock LotClock(int hours, int minutes, long ticks = 1000)
        {
            return new VMClock
            {
                Hours = hours,
                Minutes = minutes,
                TicksPerMinute = WorldClock.TicksPerMinuteTs1,
                DayOfMonth = 7,
                Month = 3,
                Year = 1998,
                Ticks = ticks
            };
        }

        private static VMMarshal ResidentMarshal(short motive)
        {
            var avatar = new VMAvatarMarshal
            {
                ObjectID = 1,
                MotiveData = new short[16],
                PersonData = new short[101],
                MotiveDecay = new VMTS1MotiveDecay()
            };
            for (int m = 0; m < avatar.MotiveData.Length; m++) avatar.MotiveData[m] = motive;
            avatar.MotiveData[(int)VMMotive.SleepState] = 0;
            return new VMMarshal { Entities = new VMEntityMarshal[] { avatar } };
        }

        private static short Hunger(VMMarshal marshal)
        {
            return ((VMAvatarMarshal)marshal.Entities[0]).MotiveData[(int)VMMotive.Hunger];
        }

        private static SimitoneNeighbourhoodWorld Adopt(NeighbourhoodWorldSession session, int slot, LotStore lots = null)
        {
            SimitoneNeighbourhoodWorld replaced;
            bool created;
            return session.Adopt(slot, Layout(slot), Map(), lots, out replaced, out created);
        }

        // ---- 1. the world is a session object keyed by slot --------------------------------------

        [Fact]
        public void AWorldIsKeyedByTheSlotAndNotByTheLayoutInstance()
        {
            var session = new NeighbourhoodWorldSession();
            SimitoneNeighbourhoodWorld replaced;
            bool created;

            var first = session.Adopt(Slot, Layout(), Map(), null, out replaced, out created);
            Assert.True(created);
            Assert.Null(replaced);

            //a late landing hands over a different layout object for the same neighbourhood: the
            //world, and the clock in it, are kept
            first.PullClockFrom(LotClock(14, 30));
            var again = session.Adopt(Slot, Layout(), Map(), null, out replaced, out created);

            Assert.Same(first, again);
            Assert.False(created);
            Assert.Null(replaced);
            Assert.Equal(14, again.Clock.Hours);
            Assert.True(session.IsFor(Slot));
        }

        [Fact]
        public void AWorldNeedsNoSurroundOnlyALayoutAndAMap()
        {
            //the session takes the lifecycle's layout and map and nothing else, so it exists with
            //no 3D surround and under any view setting
            var session = new NeighbourhoodWorldSession();

            var world = Adopt(session, Slot);

            Assert.NotNull(world);
            Assert.True(world.WorldEnabled);
            Assert.Equal(Slot, session.Slot);
        }

        [Fact]
        public void AnotherSlotEndsTheHeldWorldAndHandsItBackToBeSaved()
        {
            var session = new NeighbourhoodWorldSession();
            var home = Adopt(session, Slot);

            var ended = session.Retarget(OtherSlot);

            Assert.Same(home, ended);
            Assert.Null(session.World);
            Assert.Equal(NeighbourhoodWorldSession.NoSlot, session.Slot);

            var downtown = Adopt(session, OtherSlot);
            Assert.NotSame(home, downtown);
            Assert.Equal(OtherSlot, session.Slot);
        }

        [Fact]
        public void AdoptingAnotherSlotReturnsTheWorldItReplaced()
        {
            var session = new NeighbourhoodWorldSession();
            var home = Adopt(session, Slot);
            SimitoneNeighbourhoodWorld replaced;
            bool created;

            var other = session.Adopt(OtherSlot, Layout(OtherSlot), Map(), null, out replaced, out created);

            Assert.True(created);
            Assert.Same(home, replaced);
            Assert.NotSame(home, other);
        }

        [Fact]
        public void RetargetingTheSameSlotKeepsTheWorld()
        {
            var session = new NeighbourhoodWorldSession();
            var world = Adopt(session, Slot);
            var epoch = session.Epoch;

            Assert.Null(session.Retarget(Slot));

            Assert.Same(world, session.World);
            Assert.Equal(epoch, session.Epoch);
        }

        [Fact]
        public void AWorldIsAskedForOncePerSlotUntilTheSessionEnds()
        {
            var session = new NeighbourhoodWorldSession();
            Assert.True(session.NeedsRequest(Slot));

            session.MarkRequested(Slot);
            Assert.False(session.NeedsRequest(Slot));
            Assert.True(session.NeedsRequest(OtherSlot));

            //moving to another neighbourhood forgets the request made for this one
            session.Retarget(OtherSlot);
            Assert.True(session.NeedsRequest(Slot));

            session.MarkRequested(OtherSlot);
            session.End();
            Assert.True(session.NeedsRequest(OtherSlot));
            Assert.False(session.NeedsRequest(NeighbourhoodWorldSession.NoSlot));
        }

        [Fact]
        public void AnAnswerForASlotThatIsGoneCanBeToldFromTheEpoch()
        {
            var session = new NeighbourhoodWorldSession();
            session.MarkRequested(Slot);
            var asked = session.Epoch;

            session.Retarget(OtherSlot);
            Assert.NotEqual(asked, session.Epoch);

            var again = session.Epoch;
            session.End();
            Assert.NotEqual(again, session.Epoch);
        }

        [Fact]
        public void EveryReasonForThereBeingNoWorldIsGivenOnceAndKept()
        {
            var session = new NeighbourhoodWorldSession();

            Assert.True(session.Refuse(Slot, "the road graph would not connect"));
            Assert.False(session.Refuse(Slot, "the road graph would not connect"));
            Assert.Equal("the road graph would not connect", session.UnavailableReason);
            //a refusal is latched, so it is not asked for again
            Assert.False(session.NeedsRequest(Slot));

            Assert.True(session.NoteUnavailable("something else"));
            Assert.Equal("something else", session.UnavailableReason);

            //a world makes the reason moot
            Adopt(session, Slot);
            Assert.Null(session.UnavailableReason);
        }

        [Fact]
        public void TheWorldIsBuiltOverTheSessionsStoreAndTheStoreOutlivesTheWorld()
        {
            var session = new NeighbourhoodWorldSession();
            var lots = new LotStore();
            var marshal = ResidentMarshal(100);
            lots.SetActive(21, marshal);

            var world = Adopt(session, Slot, lots);
            Assert.Same(lots, world.LotStore);
            VMMarshal seen;
            Assert.True(world.LotStore.TryGetActive(21, out seen));
            Assert.Same(marshal, seen);

            //the player crosses into another neighbourhood: the world goes, the stash stays
            session.End();
            Assert.True(lots.TryGetActive(21, out seen));
            var next = Adopt(session, OtherSlot, lots);
            Assert.True(next.LotStore.TryGetActive(21, out seen));
        }

        // ---- 2. the lot clock is the authority while a lot is up --------------------------------

        [Fact]
        public void FollowingALotSeedsAWorldThatHasNoTimeYet()
        {
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true);
            Assert.False(world.Clock.IsAvailable);

            var lot = LotClock(14, 5);
            Assert.True(world.FollowLotClock(lot));

            Assert.True(world.Clock.IsAvailable);
            Assert.Equal(14, world.Clock.Hours);
            Assert.Equal(5, world.Clock.Minutes);
            Assert.Equal(7, world.Clock.DayOfMonth);
            Assert.Equal(lot.Ticks, world.Clock.Ticks);
        }

        [Fact]
        public void TheWorldMovesOnlyWhenTheLotsMinuteDoes()
        {
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true);
            var lot = LotClock(10, 5);
            Assert.True(world.FollowLotClock(lot));

            //a paused lot runs no ticks: asked again, the world has nothing to take
            Assert.False(world.FollowLotClock(lot));
            Assert.Equal(5, world.Clock.Minutes);

            //part of a minute is still the same minute
            for (int i = 0; i < WorldClock.TicksPerMinuteTs1 - 1; i++) lot.Tick();
            Assert.False(world.FollowLotClock(lot));
            Assert.Equal(5, world.Clock.Minutes);

            //the minute turns, and the world is the lot's again, ticks and all
            lot.Tick();
            Assert.True(world.FollowLotClock(lot));
            Assert.Equal(6, world.Clock.Minutes);
            Assert.Equal(lot.Ticks, world.Clock.Ticks);
        }

        [Fact]
        public void TheLotsHourWinsWhateverTheWorldDriftedTo()
        {
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true);
            var lot = LotClock(14, 0);
            world.FollowLotClock(lot);

            //the world's own wall-clock tick is what used to drift away from the lot
            world.Tick(60 * 10);
            Assert.NotEqual(14, world.Clock.Hours);

            for (int i = 0; i < WorldClock.TicksPerMinuteTs1; i++) lot.Tick();
            Assert.True(world.FollowLotClock(lot));
            Assert.Equal(14, world.Clock.Hours);
            Assert.Equal(1, world.Clock.Minutes);
        }

        [Fact]
        public void WithTheOffscreenCapabilityTheLotsOwnMinutesAreSpentOnTheStoredLots()
        {
            On(NeighbourhoodWorldFlag.Offscreen);
            var lots = new LotStore();
            var stored = ResidentMarshal(100);
            lots.SetActive(21, stored);
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true, lots);
            world.SetActiveLot(22);

            var lot = LotClock(8, 0);
            world.FollowLotClock(lot);
            Assert.Equal(100, Hunger(stored));

            //four game hours of the played lot's own clock
            for (int i = 0; i < 4 * 60 * WorldClock.TicksPerMinuteTs1; i++) lot.Tick();
            Assert.True(world.FollowLotClock(lot));

            Assert.True(Hunger(stored) < 100, "the stored lot did not decay while the played lot ran");
            Assert.Equal(12, world.Clock.Hours);
            Assert.Equal(lot.Ticks, world.Clock.Ticks);
        }

        [Fact]
        public void WithTheCapabilityOffTheLotsMinutesDecayNothing()
        {
            var lots = new LotStore();
            var stored = ResidentMarshal(100);
            lots.SetActive(21, stored);
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true, lots);
            var lot = LotClock(8, 0);
            world.FollowLotClock(lot);

            for (int i = 0; i < 4 * 60 * WorldClock.TicksPerMinuteTs1; i++) lot.Tick();
            world.FollowLotClock(lot);

            Assert.Equal(100, Hunger(stored));
            Assert.Equal(12, world.Clock.Hours);
        }

        [Fact]
        public void AWorldThatIsTurnedOffIsNotTickedByAFollowedLot()
        {
            On(NeighbourhoodWorldFlag.Offscreen);
            var lots = new LotStore();
            var stored = ResidentMarshal(100);
            lots.SetActive(21, stored);
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), false, lots);
            var lot = LotClock(8, 0);
            world.FollowLotClock(lot);

            for (int i = 0; i < 4 * 60 * WorldClock.TicksPerMinuteTs1; i++) lot.Tick();
            world.FollowLotClock(lot);

            Assert.Equal(100, Hunger(stored));
        }

        // ---- 3. the stash is the world's lot store ----------------------------------------------

        [Fact]
        public void ThePlayedLotIsNotDecayedAndAStashedOneIs()
        {
            On(NeighbourhoodWorldFlag.Offscreen);
            var lots = new LotStore();
            var stashed = ResidentMarshal(100);
            lots.SetActive(21, stashed);
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true, lots);

            //the player is in lot 22, and something puts a marshal for it in the stash as well
            world.SetActiveLot(22);
            var conflicting = ResidentMarshal(100);
            lots.SetActive(22, conflicting);

            Assert.True(world.Tick(60 * 4 * OffscreenSimulator.GameMinutesPerRealSecond));

            Assert.True(Hunger(stashed) < 100);
            Assert.Equal(100, Hunger(conflicting));
            Assert.Equal(22, world.ActiveLotNumber);
        }

        [Fact]
        public void AStashedLotTakenBackOutIsForgottenByTheSimulatorSoItIsNotDecayedTwice()
        {
            On(NeighbourhoodWorldFlag.Offscreen);
            var lots = new LotStore();
            var marshal = ResidentMarshal(100);
            lots.SetActive(21, marshal);
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true, lots);
            world.SetActiveLot(22);

            world.Tick(60 * 4 * OffscreenSimulator.GameMinutesPerRealSecond);
            OffscreenLotRecord record;
            Assert.True(world.TryGetOffscreenRecord(21, out record));

            //lot 21 is played again: out of the stash, and its record is spent
            VMMarshal taken;
            Assert.True(lots.TryGetActive(21, out taken));
            lots.Remove(21);
            world.NoteLotRestored(21);
            world.SetActiveLot(21);

            Assert.False(world.TryGetOffscreenRecord(21, out record));
            Assert.Equal(NeighbourhoodLotState.Unloaded, lots.StateOf(21));
        }

        [Fact]
        public void NotingARestoreBuildsNoSimulator()
        {
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true);

            world.NoteLotRestored(21);

            Assert.DoesNotContain("offscreen", world.ToString());
        }

        // ---- 4. the session is persisted ----------------------------------------------------------

        private NeighbourhoodLayout WriteSidecar(NeighbourhoodLayout layout)
        {
            Assert.True(NeighbourhoodLayoutStore.Save(_root, layout));
            return layout;
        }

        [Fact]
        public void AWorldsSessionRoundTripsThroughTheSidecar()
        {
            On(NeighbourhoodWorldFlag.Persist);
            var layout = WriteSidecar(Layout());
            var world = new SimitoneNeighbourhoodWorld(layout, Map(), true);
            world.PullClockFrom(LotClock(14, 30, 5000));
            world.SetActiveLot(21);
            world.Transit.Add(1, 21, 22, 0, 40);

            Assert.True(NeighbourhoodWorldPersistence.Save(_root, Slot, world));

            var loaded = NeighbourhoodLayoutStore.Load(_root, Slot);
            Assert.NotNull(loaded.WorldState);
            var resumed = new SimitoneNeighbourhoodWorld(loaded, Map(), true);
            Assert.True(NeighbourhoodWorldPersistence.Restore(resumed, loaded.WorldState));

            Assert.True(resumed.Clock.IsAvailable);
            Assert.Equal(14, resumed.Clock.Hours);
            Assert.Equal(30, resumed.Clock.Minutes);
            Assert.Equal(7, resumed.Clock.DayOfMonth);
            Assert.Equal(3, resumed.Clock.Month);
            Assert.Equal(1998, resumed.Clock.Year);
            Assert.Equal(5000L, resumed.Clock.Ticks);
            Assert.Equal(21, resumed.ActiveLotNumber);
            Assert.Equal(1, resumed.Transit.Count);
            Assert.True(resumed.Transit.InTransit);
        }

        [Fact]
        public void SavingLeavesTheLotRecordsTheRefreshLeftInTheSidecarAlone()
        {
            //S2's per-lot carry: the session is stamped onto the sidecar that is on disk, so each
            //lot's own state, last-simulated tick and entrance survive a session save
            On(NeighbourhoodWorldFlag.Persist);
            var layout = Layout();
            var lot = layout.FindLot(21);
            lot.LotState = NeighbourhoodLotState.Hollow;
            lot.LastSimulated = 123456L;
            lot.Entrance = new NeighbourhoodLotEntrance(lot.WorldX + 2, lot.WorldY + 3, 1);
            WriteSidecar(layout);

            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true);
            world.PullClockFrom(LotClock(9, 0));
            Assert.True(NeighbourhoodWorldPersistence.Save(_root, Slot, world));

            var loaded = NeighbourhoodLayoutStore.Load(_root, Slot);
            var kept = loaded.FindLot(21);
            Assert.Equal(NeighbourhoodLotState.Hollow, kept.LotState);
            Assert.Equal(123456L, kept.LastSimulated);
            Assert.True(kept.Entrance.HasValue);
            Assert.Equal(lot.WorldX + 2, kept.Entrance.Value.WorldX);
            Assert.NotNull(loaded.WorldState);
        }

        [Fact]
        public void TheWorldsOwnLotStampsReachTheFileAndComeBackOnLoad()
        {
            On(NeighbourhoodWorldFlag.Persist);
            WriteSidecar(Layout());

            var live = Layout();
            var lot = live.FindLot(22);
            lot.LotState = NeighbourhoodLotState.Hollow;
            lot.LastSimulated = 98765L;
            lot.Entrance = new NeighbourhoodLotEntrance(lot.WorldX + 1, lot.WorldY + 4, 2);
            var world = new SimitoneNeighbourhoodWorld(live, Map(), true);
            world.PullClockFrom(LotClock(10, 0));

            Assert.True(NeighbourhoodWorldPersistence.Save(_root, Slot, world));

            var loaded = NeighbourhoodLayoutStore.Load(_root, Slot);
            var kept = loaded.FindLot(22);
            Assert.Equal(NeighbourhoodLotState.Hollow, kept.LotState);
            Assert.Equal(98765L, kept.LastSimulated);
            Assert.True(kept.Entrance.HasValue);
            Assert.Equal(lot.WorldY + 4, kept.Entrance.Value.WorldY);
            //the lot the world said nothing about is untouched
            Assert.Equal(NeighbourhoodLotState.Unloaded, loaded.FindLot(21).LotState);
            Assert.Equal(0L, loaded.FindLot(21).LastSimulated);
        }

        [Fact]
        public void ALiveLayoutNeverMovesALotOrMakesALaterStampEarlier()
        {
            On(NeighbourhoodWorldFlag.Persist);
            var disk = Layout();
            disk.FindLot(21).LastSimulated = 500L;
            WriteSidecar(disk);

            var live = Layout();
            live.FindLot(21).LastSimulated = 100L;
            var world = new SimitoneNeighbourhoodWorld(live, Map(), true);
            world.PullClockFrom(LotClock(10, 0));
            Assert.True(NeighbourhoodWorldPersistence.Save(_root, Slot, world));

            Assert.Equal(500L, NeighbourhoodLayoutStore.Load(_root, Slot).FindLot(21).LastSimulated);
        }

        [Fact]
        public void TheWorldIsWantedWheneverTheWorldFlagIsOn()
        {
            var viewWanted = NeighbourhoodLifecycle.WantsWorld;
            On(NeighbourhoodWorldFlag.World);
            Assert.True(NeighbourhoodLifecycle.WantsWorld);

            NeighbourhoodWorldFlags.AllOff();
            Assert.Equal(viewWanted, NeighbourhoodLifecycle.WantsWorld);
        }

        [Fact]
        public void WithTheCapabilityOffNothingIsSavedOrRestored()
        {
            var layout = WriteSidecar(Layout());
            var path = NeighbourhoodLayoutStore.PathFor(_root, Slot);
            var before = File.ReadAllBytes(path);
            var world = new SimitoneNeighbourhoodWorld(layout, Map(), true);
            world.PullClockFrom(LotClock(14, 30));

            Assert.False(NeighbourhoodWorldPersistence.Save(_root, Slot, world));
            Assert.Equal(before, File.ReadAllBytes(path));

            var stored = NeighbourhoodWorldState.CreateDefault();
            Assert.False(NeighbourhoodWorldPersistence.Restore(world, stored));
            Assert.Equal(14, world.Clock.Hours);
        }

        [Fact]
        public void ASlotWithNoSidecarSavesNothing()
        {
            On(NeighbourhoodWorldFlag.Persist);
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true);
            world.PullClockFrom(LotClock(14, 30));

            Assert.False(NeighbourhoodWorldPersistence.Save(_root, Slot, world));
            Assert.False(NeighbourhoodWorldPersistence.Save(_root, Slot, null));
            Assert.False(NeighbourhoodWorldPersistence.Restore(null, NeighbourhoodWorldState.CreateDefault()));
        }

        [Fact]
        public void ASessionWithNoStoredSessionRestoresNothing()
        {
            On(NeighbourhoodWorldFlag.Persist);
            var world = new SimitoneNeighbourhoodWorld(Layout(), Map(), true);

            Assert.False(NeighbourhoodWorldPersistence.Restore(world, null));
            Assert.False(world.Clock.IsAvailable);
        }

        // ---- the screen's wiring, read from its source (it cannot be run without a game) ---------

        private static string ScreenSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Simitone.Client", "UI", "Screens", "TS1GameScreen.cs");
                if (File.Exists(candidate))
                {
                    var text = File.ReadAllText(candidate);
                    var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                        text, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
                    return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not find Simitone.Client above " + AppContext.BaseDirectory);
        }

        private static string Body(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "no member starting with: " + signature);
            var open = source.IndexOf('{', at);
            var depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unterminated body for: " + signature);
        }

        [Fact]
        public void TheScreensWorldIsAskedFromTheLifecycleAndNotFromTheSurround()
        {
            var source = ScreenSource();

            var get = Body(source, "private bool TryGetNeighbourhoodWorld(");
            Assert.DoesNotContain("SurroundingsWorld", get);
            Assert.Contains("RequestNeighbourhoodWorld();", get);

            var request = Body(source, "private void RequestNeighbourhoodWorld()");
            Assert.DoesNotContain("SurroundingsWorld", request);
            Assert.DoesNotContain("Surroundings", request.Replace("SurroundingsSlotFor", string.Empty));
            //whatever the player's 2D/3D setting says, the world needs a layout and a map
            Assert.Contains("NeighbourhoodView.World3D", request);
            Assert.DoesNotContain("RequestedView", request);
            Assert.Contains("Refuse(", request);

            Assert.DoesNotContain("NeighbourhoodWorldSource", source);
            Assert.DoesNotContain("NeighbourhoodWorldUnavailable", source);
        }

        [Fact]
        public void EveryLotLoadNamesThePlayedLotBeforeAnythingAsksForTheWorld()
        {
            var source = ScreenSource();
            var init = Body(source, "public void InitializeLot()");

            var played = init.IndexOf("SetPlayedLot(LoadingHouseNumber ?? 0);", StringComparison.Ordinal);
            var attach = init.IndexOf("AttachSurroundings();", StringComparison.Ordinal);
            Assert.True(played > 0, "the lot load no longer names the played lot");
            Assert.True(played < attach, "the surround is attached before the world knows the lot");

            var set = Body(source, "private void SetPlayedLot(int lot)");
            Assert.Contains("WorldSession.Retarget(", set);
            Assert.Contains("SetActiveLot(lot)", set);
        }

        [Fact]
        public void TheStashIsTheSessionStoreAndATakenLotIsReportedToTheWorld()
        {
            var source = ScreenSource();

            Assert.DoesNotContain("SavedLots", source);
            Assert.Contains("SessionLots.SetActive(", source);
            Assert.Contains("SessionLots.TryGetActive(", source);

            var take = Body(source, "private void TakeStashedLot(short lot)");
            Assert.Contains("SessionLots.Remove(lot);", take);
            Assert.Contains("NoteLotRestored(lot)", take);

            //both ways a lot leaves the stash go through it
            Assert.Contains("screen.TakeStashedLot(lot);", source);
            Assert.Contains("TakeStashedLot((short)SwitchLot);", source);
        }

        [Fact]
        public void ThePersistHalvesRunWhenTheWorldIsBuiltWhenALotIsLeftAndWhenTheGameIsSaved()
        {
            var source = ScreenSource();

            var adopt = Body(source, "private void AdoptNeighbourhoodWorld(");
            var restore = adopt.IndexOf("NeighbourhoodWorldPersistence.Restore(", StringComparison.Ordinal);
            var active = adopt.IndexOf("world.SetActiveLot(PlayedLot);", StringComparison.Ordinal);
            Assert.True(restore > 0, "the stored session is never applied to a new world");
            Assert.True(active > restore, "the played lot must be named after the stored session");

            var save = Body(source, "public void Save()");
            Assert.Contains("SaveNeighbourhoodWorld(WorldSession.World, WorldSession.Slot);", save);
            Assert.True(save.IndexOf("SaveService.Save(", StringComparison.Ordinal)
                < save.IndexOf("SaveNeighbourhoodWorld(", StringComparison.Ordinal),
                "the session is written before the lot is");

            var exit = Body(source, "public void ExitLot()");
            Assert.True(exit.IndexOf("EndNeighbourhoodWorld();", StringComparison.Ordinal)
                < exit.IndexOf("CleanupLastWorld();", StringComparison.Ordinal),
                "the world must be ended while the lot's clock can still be read");

            var end = Body(source, "private void EndNeighbourhoodWorld()");
            Assert.Contains("SaveNeighbourhoodWorld(world, slot);", end);

            var set = Body(source, "private void SetPlayedLot(int lot)");
            Assert.Contains("SaveNeighbourhoodWorld(ended, held);", set);

            var saver = Body(source, "private static void SaveNeighbourhoodWorld(");
            Assert.Contains("NeighbourhoodWorldPersistence.Save(", saver);
        }
    }
}
