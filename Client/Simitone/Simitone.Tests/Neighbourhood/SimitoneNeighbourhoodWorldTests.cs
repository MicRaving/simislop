/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// Part B1's world plus B6's off-screen wiring: the transit table, the clock, the lot-switch
    /// path, the clock&lt;-&gt;lot handshake, and the two claims that make all of it safe to leave in
    /// the codebase - <b>with the flag off, nothing moves</b>, and <b>with it on, the same seed
    /// reaches the same numbers</b>.
    ///
    /// <para>
    /// Everything here runs with no content manager, no graphics device and no VM, because the world
    /// holds marshals and integers rather than live lots. The properties worth pinning are the ones
    /// a later milestone would break silently: a hop's five fields survive a round-trip through the
    /// sidecar's record type, the clock does not move unless the world is switched on, entering a lot
    /// takes its stored marshal out while leaving a lot puts one in, and the off-screen capability
    /// changes the world's tick only by being on.
    /// </para>
    ///
    /// <para>
    /// <b>Why this class is in the non-parallel collection.</b> The capability switches are
    /// process-wide and cached in a static, and the B6 tests turn one on. The constructor pins them
    /// all off rather than reading the machine's <c>simitone.ini</c>, so a developer who has the
    /// flag on locally still gets the B1 assertions B1 was written for; <see cref="Dispose"/> drops
    /// the cache entirely, which is a complete undo because the next reader reloads from the file.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class SimitoneNeighbourhoodWorldTests : IDisposable
    {
        private const int Slot = 4;
        private const int Seed = 555;

        /// <summary>A second seed, for the claim that the first one is read at all.</summary>
        private const int OtherSeed = 4444;

        /// <summary>
        /// A deliberately small map: a world builds a map and a query over it, and a 512x512 blank
        /// of each is a few megabytes per test for a code path that is size independent.
        /// </summary>
        private const int MapSize = 96;

        public SimitoneNeighbourhoodWorldTests()
        {
            //Pinned rather than read: the B1 assertions below are about a world with every
            //capability off, and that has to be true on the machine running them and not only on the
            //one whose ini says so.
            NeighbourhoodWorldFlags.AllOff();
        }

        public void Dispose()
        {
            //Reset, not AllOff: the cache goes, so whatever the machine's ini says is what the next
            //reader gets, exactly as if this class had never run.
            NeighbourhoodWorldFlags.Reset();
        }

        /// <summary>Two lots, small enough that the world they belong to is cheap to build.</summary>
        private static NeighbourhoodLayout Layout(int seed = Seed)
        {
            return NeighbourhoodLayoutBuilder.PackOnGrid(new List<NeighbourhoodLotRead>
            {
                new NeighbourhoodLotRead { Number = 21, MapX = 40, MapY = 36, HasHouse = true, Size = 24 },
                new NeighbourhoodLotRead { Number = 22, MapX = 60, MapY = 38, HasHouse = true, Size = 24 }
            }, Slot, seed, MapSize);
        }

        private static SimitoneNeighbourhoodWorld World(bool enabled = true)
        {
            return new SimitoneNeighbourhoodWorld(Layout(), NeighbourhoodMap.Create(MapSize), enabled);
        }

        private static SimitoneNeighbourhoodWorld World(int seed, bool enabled = true)
        {
            return new SimitoneNeighbourhoodWorld(Layout(seed), NeighbourhoodMap.Create(MapSize), enabled);
        }

        /// <summary>
        /// Turn one capability on for the duration of a test. Not saved: a test that wrote a
        /// developer's <c>simitone.ini</c> would be a test with a lasting side effect on their
        /// machine, and the value only has to outlive the process that set it.
        /// </summary>
        private static void Capability(NeighbourhoodWorldFlag flag, bool on)
        {
            NeighbourhoodWorldFlags.Set(flag, on, save: false);
        }

        /// <summary>
        /// A game hour in real seconds. Sixty, not 3600: a real second is a game minute at TS1's
        /// rate, so getting this wrong is silent - the numbers still move, at sixtieth the rate.
        /// </summary>
        private static double GameHours(double hours)
        {
            return hours * 60 * OffscreenSimulator.GameMinutesPerRealSecond;
        }

        /// <summary>
        /// A lot the player has just left: a resident marshal holding <paramref name="sims"/> Sims
        /// with every motive at <paramref name="motive"/>. The shape the lot store actually holds.
        /// </summary>
        private static VMMarshal ResidentMarshal(short motive, params short[] sims)
        {
            var entities = new List<VMEntityMarshal>();
            for (int i = 0; i < sims.Length; i++)
            {
                var avatar = new VMAvatarMarshal
                {
                    ObjectID = (short)(i + 1),
                    MotiveData = new short[16],
                    PersonData = new short[101],
                    MotiveDecay = new VMTS1MotiveDecay()
                };
                for (int m = 0; m < avatar.MotiveData.Length; m++) avatar.MotiveData[m] = motive;
                avatar.MotiveData[(int)VMMotive.SleepState] = 0;
                entities.Add(avatar);
            }
            return new VMMarshal { Entities = entities.ToArray() };
        }

        private static short[] Motives(VMMarshal marshal, int index)
        {
            return ((VMAvatarMarshal)marshal.Entities[index]).MotiveData;
        }

        // ---- transit -------------------------------------------------------------------------------

        [Fact]
        public void AStoreNobodyHasAddedTo_HoldsNoHops()
        {
            var transit = new TransitStore();

            Assert.Equal(0, transit.Count);
            Assert.Empty(transit.Active);
            Assert.Empty(transit.Sims);
            Assert.False(transit.InTransit);
            Assert.False(transit.Has(7));

            NeighbourhoodTransitRecord hop;
            Assert.False(transit.TryGet(7, out hop));
            Assert.Null(hop);
        }

        [Fact]
        public void AddingAHopRecordsItsFiveFieldsInStartOrder()
        {
            var transit = new TransitStore();

            var first = transit.Add(7, 21, 22, 3);
            var second = transit.Add(9, 22, 21, 4, 50);

            Assert.Equal(2, transit.Count);
            Assert.Equal(7, transit.Sims[0]);
            Assert.Equal(9, transit.Sims[1]);
            //the caller's own record, not a copy - LotStore hands marshals back the same way
            Assert.Same(first, transit.Active[0]);
            Assert.Equal(0, first.Progress);
            Assert.Equal(50, second.Progress);
            Assert.True(transit.InTransit);

            NeighbourhoodTransitRecord found;
            Assert.True(transit.TryGet(9, out found));
            Assert.Same(second, found);
        }

        [Fact]
        public void OneSimHasOneHop_SoAddingAgainReplacesItInPlace()
        {
            //B3's discrete travel restarts a hop when the destination changes; "where is this Sim"
            //cannot have two answers
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3);
            transit.Add(8, 21, 22, 3);

            var restarted = transit.Add(7, 21, 23, 5, 10);

            Assert.Equal(2, transit.Count);
            Assert.Equal(7, transit.Active[0].Sim);   //the restarted hop, in its first position
            Assert.Equal(8, transit.Active[1].Sim);
            Assert.Equal(23, transit.Active[0].Destination);
            Assert.Equal(5, transit.Active[0].Mode);
            Assert.Equal(10, transit.Active[0].Progress);
            Assert.Same(restarted, transit.Active[0]);
        }

        [Fact]
        public void UpdatingAProgressKeepsWhereTheHopIsGoing()
        {
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3);

            Assert.True(transit.UpdateProgress(7, 42));

            NeighbourhoodTransitRecord hop;
            Assert.True(transit.TryGet(7, out hop));
            Assert.Equal(42, hop.Progress);
            Assert.Equal(21, hop.Origin);
            Assert.Equal(22, hop.Destination);
            Assert.Equal(3, hop.Mode);
        }

        [Fact]
        public void UpdatingSomebodyWhoIsNotTravellingIsNotAnError_ItDoesNothing()
        {
            //an update that had to create the hop it updates would make "which Sim moved" unanswerable
            var transit = new TransitStore();

            Assert.False(transit.Update(7, 21, 22, 3, 10));
            Assert.False(transit.UpdateProgress(7, 10));
            Assert.False(transit.Complete(7));
            Assert.Equal(0, transit.Count);
        }

        [Fact]
        public void AFullUpdateRewritesEveryFieldButNotTheOrder()
        {
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3);
            transit.Add(8, 22, 21, 3);

            Assert.True(transit.Update(7, 21, 31, 6, 75));

            Assert.Equal(7, transit.Active[0].Sim);   //still the first hop started
            Assert.Equal(8, transit.Active[1].Sim);
            Assert.Equal(31, transit.Active[0].Destination);
            Assert.Equal(6, transit.Active[0].Mode);
            Assert.Equal(75, transit.Active[0].Progress);
        }

        [Fact]
        public void CompletingAHopArrivesIt_AndRemovingItForgetsIt()
        {
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3);

            Assert.True(transit.Complete(7));

            //arrived, not forgotten: a resuming caller wants a 100% record, not a missing one
            Assert.Equal(1, transit.Count);
            Assert.False(transit.InTransit);
            NeighbourhoodTransitRecord hop;
            Assert.True(transit.TryGet(7, out hop));
            Assert.True(hop.IsComplete);
            Assert.Equal(NeighbourhoodTransitRecord.MaxProgress, hop.Progress);

            Assert.True(transit.Remove(7));
            Assert.Equal(0, transit.Count);
            Assert.False(transit.Has(7));
            //removing a Sim that is not travelling is the same situation as removing one that is
            Assert.False(transit.Remove(7));
        }

        [Fact]
        public void RemovalKeepsTheIndexHonest()
        {
            //the position index is rebuilt by the writer that shifts positions, so a removal in the
            //middle is the case that would expose a stale index
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3);
            transit.Add(8, 21, 22, 3);
            transit.Add(9, 21, 22, 3);

            transit.Remove(8);
            Assert.True(transit.UpdateProgress(9, 33));

            Assert.Equal(new[] { 7, 9 }, transit.Sims);
            NeighbourhoodTransitRecord hop;
            Assert.True(transit.TryGet(9, out hop));
            Assert.Equal(33, hop.Progress);
            Assert.Equal(7, transit.Active[0].Sim);
        }

        [Fact]
        public void ClearingLeavesNothingBehindForTheNextLookup()
        {
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3);
            transit.Clear();

            Assert.Equal(0, transit.Count);
            Assert.False(transit.Has(7));
            Assert.False(transit.UpdateProgress(7, 50));
        }

        [Fact]
        public void HopsSurviveARoundTripThroughAWorldState()
        {
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3, 25);
            transit.Add(9, 22, 23, 4);

            var saved = NeighbourhoodWorldState.FromClock(new WorldClock(), 0);
            transit.WriteTo(saved);

            Assert.Equal(2, saved.TransitCount);
            Assert.True(saved.InTransit);
            Assert.Equal(7, saved.Transit[0].Sim);
            Assert.Equal(23, saved.Transit[1].Destination);

            var resumed = new TransitStore();
            resumed.LoadFrom(saved);

            Assert.Equal(2, resumed.Count);
            for (int i = 0; i < 2; i++)
            {
                Assert.True(resumed.Active[i].SameAs(saved.Transit[i]),
                    "hop " + i + " did not survive the round trip");
            }
            Assert.Equal(25, resumed.Active[0].Progress);
        }

        [Fact]
        public void WhatGoesIntoASavedStateIsACopy_NotTheLiveRecord()
        {
            //otherwise a hop saved at 10% and advanced afterwards would be a saved hop that moves
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3, 10);

            var saved = NeighbourhoodWorldState.FromClock(new WorldClock(), 0);
            transit.WriteTo(saved);
            transit.UpdateProgress(7, 90);

            Assert.Equal(10, saved.Transit[0].Progress);
            Assert.Equal(90, transit.Active[0].Progress);
        }

        [Fact]
        public void ResumingClearsWhatWasThereBefore()
        {
            //a save is a statement about the whole session, not an addition to one
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3);

            var saved = NeighbourhoodWorldState.CreateDefault();
            transit.LoadFrom(saved);
            Assert.Equal(0, transit.Count);

            saved.Transit.Add(new NeighbourhoodTransitRecord(9, 22, 21, 4, 50));
            transit.LoadFrom(saved);
            Assert.Equal(1, transit.Count);
            Assert.Equal(9, transit.Active[0].Sim);
        }

        [Fact]
        public void AnUnusableStateResumesAsAnEmptyTable()
        {
            //a session saved on the neighbourhood screen has no hops to resume, and that is a
            //normal thing to load rather than a failure
            var transit = new TransitStore();
            transit.Add(7, 21, 22, 3);

            transit.LoadFrom((NeighbourhoodWorldState)null);
            Assert.Equal(0, transit.Count);

            transit.LoadFrom(NeighbourhoodWorldState.CreateDefault());
            Assert.Equal(0, transit.Count);

            var withNulls = NeighbourhoodWorldState.CreateDefault();
            withNulls.Transit.Add(null);
            withNulls.Transit.Add(new NeighbourhoodTransitRecord(9, 22, 21, 4, 50));
            transit.LoadFrom(withNulls);
            Assert.Equal(1, transit.Count);
            Assert.Equal(9, transit.Active[0].Sim);
        }

        // ---- the clock -----------------------------------------------------------------------------

        [Fact]
        public void AnUnswitchedWorldDoesNotTick_AndSaysNothingChanged()
        {
            var world = World(enabled: false);
            var before = world.Clock.Ticks;

            Assert.False(world.Tick(0.016));
            Assert.False(world.Tick(30.0));

            Assert.Equal(before, world.Clock.Ticks);
        }

        [Fact]
        public void ASwitchedWorldTicksOneGameMinutePerSecond()
        {
            var world = World();
            var start = world.Clock.Ticks;

            //thirty sub-minute ticks to the minute, and a TS1 minute to the second
            Assert.True(world.Tick(1.0));

            Assert.Equal(start + WorldClock.TicksPerMinuteTs1, world.Clock.Ticks);
            Assert.Equal(1, world.Clock.Minutes);
            Assert.Equal(0, world.Clock.Hours);
        }

        [Fact]
        public void AFrameTooShortToFillATickDoesNothingAndSaysSo()
        {
            var world = World();
            var start = world.Clock.Ticks;

            //thirty ticks to the second, so a frame under a thirtieth of a second does not move the
            //clock on its own - but its fraction is banked, so the next one completes the tick
            Assert.False(world.Tick(0.02));
            Assert.Equal(start, world.Clock.Ticks);

            Assert.True(world.Tick(0.02));
            Assert.Equal(start + 1, world.Clock.Ticks);
        }

        [Fact]
        public void SixtyFramesOfASixtiethAreOneGameMinute()
        {
            //The defect that kept neighbourhood time from ever moving in a real session: the screen
            //ticks the world once per frame, a 60 fps frame is half a sub-minute tick, and flooring
            //each frame on its own advanced the clock by nothing, for ever.
            var world = World();
            var start = world.Clock.Ticks;

            for (int frame = 0; frame < 60; frame++) world.Tick(1.0 / 60.0);

            Assert.Equal(start + WorldClock.TicksPerMinuteTs1, world.Clock.Ticks);
        }

        [Fact]
        public void FrameSizeDoesNotChangeWhereTheClockEndsUp()
        {
            var fine = World();
            var coarse = World();

            for (int frame = 0; frame < 600; frame++) fine.Tick(0.01);
            for (int frame = 0; frame < 60; frame++) coarse.Tick(0.1);

            //six real seconds either way; the one tick of slack is floating-point noise in the
            //banked fraction (it is carried, not lost, so it shows up on the next call)
            var expected = 6L * WorldClock.TicksPerMinuteTs1;
            Assert.InRange(fine.Clock.Ticks, expected - 1, expected);
            Assert.InRange(coarse.Clock.Ticks, expected - 1, expected);
        }

        [Fact]
        public void ADeltaThatIsNotTimeAtAllIsIgnored()
        {
            //a paused game and a rewound clock are callers with nothing to ask for, and neither
            //should be able to make the neighbourhood's time jump
            var world = World();
            var start = world.Clock.Ticks;

            Assert.False(world.Tick(0.0));
            Assert.False(world.Tick(-5.0));
            Assert.False(world.Tick(double.NaN));
            Assert.False(world.Tick(double.PositiveInfinity));
            Assert.False(world.Tick(double.NegativeInfinity));

            Assert.Equal(start, world.Clock.Ticks);
        }

        [Fact]
        public void AnAbsurdDeltaIsClampedRatherThanObeyed()
        {
            //an infinite delta is a number that compares as positive and casts to the largest tick
            //count there is, so without a bound one call would be a world that never finishes
            var world = World();
            var start = world.Clock.Ticks;

            //500 real seconds is 500 game minutes at TS1's rate
            Assert.True(world.Tick(500.0));
            Assert.Equal(start + 500 * WorldClock.TicksPerMinuteTs1, world.Clock.Ticks);
            Assert.Equal(8, world.Clock.Hours);
            Assert.Equal(20, world.Clock.Minutes);

            Assert.True(world.Tick(1e300));
            Assert.Equal(start + 500 * WorldClock.TicksPerMinuteTs1 + SimitoneNeighbourhoodWorld.MaxTicksPerCall,
                world.Clock.Ticks);
        }

        [Fact]
        public void TheWorldsClockIsTheOneALotAgreesWith()
        {
            //§5.2: the world owns the time, a lot's clock is a projection of it, and the handshake
            //runs on the way in and on the way out
            var world = World();
            world.Clock.SeedFromGlobalState(new short[]
            {
                5, 14, 0, 0, 0, 30, 0, 7, 2001
            });
            var lotClock = new VMClock();

            Assert.True(world.SyncClockTo(lotClock));
            Assert.Equal(5, lotClock.Hours);
            Assert.Equal(30, lotClock.Minutes);
            Assert.Equal(14, lotClock.DayOfMonth);
            Assert.Equal(2001, lotClock.Year);

            //already in step, so nothing changed
            Assert.False(world.SyncClockTo(lotClock));

            lotClock.Minutes = 42;
            Assert.True(world.SyncClockFrom(lotClock));
            Assert.Equal(42, world.Clock.Minutes);
            Assert.False(world.SyncClockFrom(lotClock));

            Assert.Throws<ArgumentNullException>(() => world.SyncClockTo(null));
            Assert.Throws<ArgumentNullException>(() => world.SyncClockFrom(null));
        }

        // ---- the clock handshake --------------------------------------------------------------------

        [Fact]
        public void TheHandshakeIsTheTransferItself_AndItIsExact()
        {
            //Pull from one lot, push to another: the two must agree field for field, tick count
            //included. That is §5.2's "byte-identical" claim, and it is the property that lets a
            //lot be reloaded from its own file and still agree with the neighbourhood.
            var world = World();
            var source = new VMClock
            {
                Hours = 23,
                Minutes = 59,
                MinuteFractions = 17,
                TicksPerMinute = WorldClock.TicksPerMinuteTs1,
                DayOfMonth = 30,
                Month = 12,
                Year = 2004,
                Ticks = 987654
            };

            world.PullClockFrom(source);

            Assert.Equal(23, world.Clock.Hours);
            Assert.Equal(59, world.Clock.Minutes);
            Assert.Equal(17, world.Clock.MinuteFractions);
            Assert.Equal(30, world.Clock.DayOfMonth);
            Assert.Equal(12, world.Clock.Month);
            Assert.Equal(2004, world.Clock.Year);
            Assert.Equal(987654L, world.Clock.Ticks);
            Assert.True(world.Clock.IsAvailable);

            var destination = new VMClock { Hours = 1, Minutes = 2, Ticks = 3 };
            world.PushClockTo(destination);

            Assert.Equal(source.Hours, destination.Hours);
            Assert.Equal(source.Minutes, destination.Minutes);
            Assert.Equal(source.MinuteFractions, destination.MinuteFractions);
            Assert.Equal(source.TicksPerMinute, destination.TicksPerMinute);
            Assert.Equal(source.DayOfMonth, destination.DayOfMonth);
            Assert.Equal(source.Month, destination.Month);
            Assert.Equal(source.Year, destination.Year);
            Assert.Equal(source.Ticks, destination.Ticks);
        }

        [Fact]
        public void TheHandshakeLeavesTheLotsOwnFieldsAlone()
        {
            //FirePercent and UTCStart are the lot's own - one is not a time at all and the other is
            //the anchor its wall clock hangs from. Moving either would move a lot's idea of "now"
            //by an amount nobody asked for.
            var world = World();
            world.PullClockFrom(new VMClock { Hours = 9, Minutes = 9, DayOfMonth = 2 });

            var lotClock = new VMClock { FirePercent = 4321 };
            var utcStart = lotClock.UTCStart;
            world.PushClockTo(lotClock);

            Assert.Equal(4321, lotClock.FirePercent);
            Assert.Equal(utcStart, lotClock.UTCStart);
        }

        [Fact]
        public void TheHandshakeNeedsALotClock()
        {
            //A null here is a caller that has no lot to talk to, and quietly reading it as midnight
            //would move a neighbourhood's time by a day over one bad call.
            var world = World();

            Assert.Throws<ArgumentNullException>(() => world.PullClockFrom(null));
            Assert.Throws<ArgumentNullException>(() => world.PushClockTo(null));
        }

        [Fact]
        public void TheReportingSyncIsTheSameTransferWithAQuestionAsked()
        {
            //Two names, one operation: the reporting pair says whether the world had noticed, the
            //plain pair just does it. They cannot disagree because the reporting pair is the plain
            //one with a comparison in front of it.
            var world = World();
            world.PushClockTo(new VMClock { Hours = 4, DayOfMonth = 6 });

            var lotClock = new VMClock();
            world.SyncClockTo(lotClock);
            //now in step, so the plain transfer changes nothing and the reporting one says so
            world.PullClockFrom(lotClock);
            Assert.False(world.SyncClockFrom(lotClock));

            lotClock.Hours = 5;
            world.SyncClockFrom(lotClock);
            Assert.Equal(5, world.Clock.Hours);
        }

        // ---- B6: the off-screen capability ---------------------------------------------------------

        [Fact]
        public void WithTheFlagOffTheSimulatorIsNeverBuilt()
        {
            //§7: off means the previous stage's behaviour. The strongest statement of that is not
            //"the numbers came out the same" but "there was nothing there to have done them" - a
            //world that has never asked for its simulator has no records, no last tick and nothing
            //in its own description.
            Capability(NeighbourhoodWorldFlag.Offscreen, false);
            var world = World();
            var marshal = ResidentMarshal(100, 1);
            world.SetActiveLotWith(21, marshal);
            world.SetActiveLot(0);
            var before = (short[])Motives(marshal, 0).Clone();
            var start = world.Clock.Ticks;

            Assert.True(world.Tick(60.0));

            Assert.Equal(start + 60 * WorldClock.TicksPerMinuteTs1, world.Clock.Ticks);
            Assert.Equal(before, Motives(marshal, 0));
            Assert.Null(world.LastOffscreenTick);
            OffscreenLotRecord record;
            Assert.False(world.TryGetOffscreenRecord(21, out record));
            Assert.Null(record);
            Assert.DoesNotContain("offscreen", world.ToString());
        }

        [Fact]
        public void WithTheFlagOnAStoredLotDecaysWithinTheEngineBounds()
        {
            //§B6's acceptance line, first clause: time passes and the motives move plausibly and
            //stay bounded. Bounded is the engine's own -100..100, which is where
            //VMAvatar.SetMotiveData would have put them anyway, so a simulator that wrote outside
            //it would be claiming a state a real lot could not hold.
            //
            //A day, not an hour. The engine carries its decay in thousandths and only spends them
            //when they add up to a point, so an hour of absence at TS1's rates is most of a point -
            //a real number that a test asserting "hunger < 100" would be asserting about rounding
            //rather than about the model.
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World();
            var marshal = ResidentMarshal(100, 1);
            world.SetActiveLotWith(21, marshal);
            world.SetActiveLot(0);
            var start = world.Clock.Ticks;

            Assert.True(world.Tick(GameHours(24)));

            Assert.Equal(start + 24 * 60 * WorldClock.TicksPerMinuteTs1, world.Clock.Ticks);

            var motives = Motives(marshal, 0);
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                Assert.InRange(motives[i], OffscreenSimulator.MinMotive, OffscreenSimulator.MaxMotive);
            }
            //a day of nobody is hungry, and is not starving
            Assert.True(motives[(int)VMMotive.Hunger] < 100, "hunger did not decay at all");
            Assert.True(motives[(int)VMMotive.Hunger] > OffscreenSimulator.MinMotive,
                "a day of absence emptied the Sim rather than being a plausible absence");

            //and the world can say what it did
            Assert.NotNull(world.LastOffscreenTick);
            Assert.True(world.LastOffscreenTick.Changed);
            Assert.Equal(1, world.LastOffscreenTick.LotsDecayed);
            Assert.Equal(OffscreenFidelity.Coarse, world.LastOffscreenTick.Fidelity);
            OffscreenLotRecord record;
            Assert.True(world.TryGetOffscreenRecord(21, out record));
            Assert.Equal(21, record.Lot);
            Assert.True(record.HasSimulated);
            Assert.Equal(1, record.AvatarCount);
            Assert.Contains("offscreen", world.ToString());
        }

        [Fact]
        public void ThreeDaysAwayStillLeavesAMotiveTableTheEngineCouldHold()
        {
            //The bound is the claim, so it is tested where it would break: a long absence is the
            //only way to reach the ends of the range, and a model that ran away would do it here
            //rather than after a single frame.
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World();
            var marshal = ResidentMarshal(100, 1);
            world.SetActiveLotWith(21, marshal);
            world.SetActiveLot(0);

            for (int day = 0; day < 3; day++) Assert.True(world.Tick(GameHours(24)));

            var motives = Motives(marshal, 0);
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                Assert.InRange(motives[i], OffscreenSimulator.MinMotive, OffscreenSimulator.MaxMotive);
            }
            Assert.InRange(motives[(int)VMMotive.Mood], OffscreenSimulator.MinMotive,
                OffscreenSimulator.MaxMotive);
            Assert.True(motives[(int)VMMotive.Hunger] > OffscreenSimulator.MinMotive);
        }

        [Fact]
        public void WithTheFlagOnTheLotThePlayerIsInIsNotTouched()
        {
            //§B6 task 4, "applied to a lot's stored state, never to the active lot's live tick". The
            //store does not hold the active lot's marshal, so the only thing enforcing this is the
            //world telling the simulator which lot is active - which is why the assertion is here.
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World();
            var neighbour = ResidentMarshal(100, 1);
            var resident = ResidentMarshal(100, 1);
            world.SetActiveLotWith(21, neighbour);
            world.SetActiveLotWith(22, resident);

            Assert.True(world.Tick(GameHours(2)));

            Assert.True(Motives(neighbour, 0)[(int)VMMotive.Hunger] < 100);
            Assert.Equal(100, Motives(resident, 0)[(int)VMMotive.Hunger]);
            Assert.Equal(22, world.ActiveLotNumber);
            OffscreenLotRecord active;
            Assert.False(world.TryGetOffscreenRecord(22, out active));
        }

        [Fact]
        public void TheSameSeedReachesTheSameNumbers_AndADifferentSeedDoesNot()
        {
            //§B6 acceptance, "same seed => same result", and its converse - the converse being the
            //one that proves the seed is read at all. A simulator that drew the same numbers from
            //every seed would satisfy the first half while being a constant function of its input.
            var first = RunOffscreen(Seed);
            var second = RunOffscreen(Seed);
            var different = RunOffscreen(OtherSeed);

            Assert.Equal(first.motives, second.motives);
            Assert.Equal(first.clock, second.clock);
            //field for field, accumulators included: the record is where the thousandths the seed
            //actually moves live, so this is the half of "same seed, same result" that the integer
            //motives are too coarse to show
            Assert.True(first.record.SameAs(second.record),
                "the same seed reached two different states");
            Assert.False(first.record.SameAs(different.record),
                "a different seed reached the same state, so the seed is not being read");
        }

        private struct OffscreenRun
        {
            public long clock;
            public short[] motives;
            public OffscreenLotRecord record;
        }

        /// <summary>
        /// A day of absence over a world holding one stored lot, from the seed its layout carries.
        /// Fresh every call: a shared world's clock, store and simulator would be shared state, and
        /// the numbers being compared are exactly that.
        /// </summary>
        private static OffscreenRun RunOffscreen(int seed)
        {
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World(seed);
            var resident = ResidentMarshal(100, 1);
            var stored = ResidentMarshal(100, 1, 2);
            world.SetActiveLotWith(21, resident);
            world.LotStore.SetActive(22, stored);

            Assert.True(world.Tick(GameHours(24)));

            OffscreenLotRecord record;
            Assert.True(world.TryGetOffscreenRecord(22, out record));

            return new OffscreenRun
            {
                clock = world.Clock.Ticks,
                //the lot the player is in is in this on purpose: its whole table must be identical
                //across runs, and must be identical to what it was before the tick
                motives = new[]
                {
                    Motives(stored, 0)[(int)VMMotive.Hunger],
                    Motives(stored, 0)[(int)VMMotive.Fun],
                    Motives(stored, 1)[(int)VMMotive.Energy],
                    Motives(resident, 0)[(int)VMMotive.Hunger]
                },
                record = record
            };
        }

        [Fact]
        public void PausedOffscreenMeansNothingMovesAtAll()
        {
            //The dial belongs to the simulator and the world does not turn it, so this is a test
            //that what the world does with the dial is nothing at all - including not advancing the
            //clock, which is the clause that is easiest to get wrong.
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World();
            var marshal = ResidentMarshal(100, 1);
            world.SetActiveLotWith(21, marshal);
            world.SetActiveLot(0);
            world.Offscreen.Fidelity = OffscreenFidelity.Paused;
            var before = (short[])Motives(marshal, 0).Clone();
            var start = world.Clock.Ticks;

            Assert.False(world.Tick(GameHours(8)));

            Assert.Equal(start, world.Clock.Ticks);
            Assert.Equal(before, Motives(marshal, 0));
            Assert.True(world.LastOffscreenTick.Paused);
            Assert.Equal(0, world.LastOffscreenTick.ClockTicks);
        }

        [Fact]
        public void TheOffscreenPathIsBoundedAndSaysHowMuchItDropped()
        {
            //MaxTicksPerCall exists because B6 fast-forwards through this method, so it has to bound
            //the fast-forward on the path the fast-forward actually takes. An infinite delta reaches
            //the simulator, so the clamp has to happen before it does - and the simulator's own
            //daily budget is raised first, because otherwise its cap is the one that fires and this
            //would pass without the world's ever being consulted.
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World();
            world.Offscreen.MaxSecondsPerTick = int.MaxValue;
            var start = world.Clock.Ticks;

            Assert.True(world.Tick(1e300));

            Assert.Equal(start + SimitoneNeighbourhoodWorld.MaxTicksPerCall, world.Clock.Ticks);
        }

        [Fact]
        public void ATooLongAbsenceIsTruncatedLoudlyRatherThanSwallowed()
        {
            //The simulator's own cap is the tighter of the two by default, and its answer to being
            //asked for more than a day is to do a day and report the rest - a silent truncation
            //reads as a bug and a loud one reads as a decision.
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World();
            var marshal = ResidentMarshal(100, 1);
            world.SetActiveLotWith(21, marshal);
            world.SetActiveLot(0);
            var start = world.Clock.Ticks;

            Assert.True(world.Tick(GameHours(72)));

            //three days asked for, the simulator's own day simulated, and the difference reported
            Assert.Equal(OffscreenSimulator.DefaultMaxSecondsPerTick * WorldClock.TicksPerMinuteTs1,
                world.Clock.Ticks - start);
            Assert.True(world.LastOffscreenTick.TruncatedSeconds > 0,
                "two days of the request went missing and the world did not say so");
        }

        [Fact]
        public void AnUnswitchedWorldDoesNothingEvenWithTheCapabilityOn()
        {
            //WorldEnabled is the outer gate and it is still the outer gate: a flag on the tick is
            //never allowed to become a reason for the tick to run.
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World(enabled: false);
            var marshal = ResidentMarshal(100, 1);
            world.SetActiveLotWith(21, marshal);
            world.SetActiveLot(0);
            var before = (short[])Motives(marshal, 0).Clone();
            var start = world.Clock.Ticks;

            Assert.False(world.Tick(GameHours(8)));

            Assert.Equal(start, world.Clock.Ticks);
            Assert.Equal(before, Motives(marshal, 0));
            Assert.Null(world.LastOffscreenTick);
        }

        [Fact]
        public void OneSimulatorPerWorld_BuiltFromTheLayoutsOwnSeed()
        {
            //Two simulators would be two accounts of the same lot, and whichever the caller happened
            //to be holding would be the one that disagreed with the world's own tick.
            var world = World(OtherSeed);

            Assert.Same(world.Offscreen, world.Offscreen);
            Assert.Equal(OtherSeed, world.Offscreen.WorldSeed);
            Assert.Equal(OffscreenFidelity.Coarse, world.Offscreen.Fidelity);
        }

        // ---- session state, for B7 ------------------------------------------------------------------

        [Fact]
        public void TheWorldStateCarriesTheClockTheActiveLotAndTheHops()
        {
            var world = World();
            world.PullClockFrom(new VMClock
            {
                Hours = 13,
                Minutes = 7,
                MinuteFractions = 5,
                TicksPerMinute = WorldClock.TicksPerMinuteTs1,
                DayOfMonth = 9,
                Month = 4,
                Year = 1999,
                Ticks = 4321
            });
            world.SetActiveLot(22);
            world.Transit.Add(7, 22, 21, 3, 40);

            var saved = world.SaveWorldState();

            Assert.Equal(NeighbourhoodWorldState.CurrentWorldVersion, saved.WorldVersion);
            Assert.Equal(22, saved.ActiveLot);
            Assert.True(saved.ClockAvailable);
            Assert.Equal(13, saved.Hours);
            Assert.Equal(7, saved.Minutes);
            Assert.Equal(4321L, saved.Ticks);
            Assert.Equal(1, saved.TransitCount);
            Assert.True(saved.InTransit);
        }

        [Fact]
        public void AResumedWorldIsTheWorldThatWasSaved()
        {
            //B7's "quit and resume": save a world with a time, a lot and a hop, and hand that state
            //to a world that has never run. Everything the state claims about the session has to be
            //the resumed world's own, and re-saving it has to produce the same state - otherwise a
            //second quit would quietly change the session.
            var world = World();
            world.PullClockFrom(new VMClock
            {
                Hours = 22, Minutes = 45, MinuteFractions = 3,
                TicksPerMinute = WorldClock.TicksPerMinuteTs1,
                DayOfMonth = 28, Month = 11, Year = 2003, Ticks = 88888
            });
            world.SetActiveLotWith(21, new VMMarshal());
            world.Transit.Add(7, 21, 22, 3, 65);

            var saved = world.SaveWorldState();
            var resumed = World();
            Assert.False(resumed.Clock.IsAvailable);

            resumed.ApplyWorldState(saved);

            Assert.True(resumed.Clock.IsAvailable);
            Assert.Equal(22, resumed.Clock.Hours);
            Assert.Equal(45, resumed.Clock.Minutes);
            Assert.Equal(3, resumed.Clock.MinuteFractions);
            Assert.Equal(28, resumed.Clock.DayOfMonth);
            Assert.Equal(2003, resumed.Clock.Year);
            Assert.Equal(88888L, resumed.Clock.Ticks);
            Assert.Equal(21, resumed.ActiveLotNumber);
            Assert.Equal(1, resumed.Transit.Count);
            Assert.Equal(65, resumed.Transit.Active[0].Progress);
            Assert.True(resumed.SaveWorldState().SameAs(saved));
        }

        [Fact]
        public void WhatGoesIntoASavedStateIsACopy_OfTheSession()
        {
            //a hop and a clock that keep moving after the save would make the sidecar a live object,
            //which is the one thing a file may not be
            var world = World();
            world.PullClockFrom(new VMClock { Hours = 1, Ticks = 30 });
            world.Transit.Add(7, 21, 22, 3, 10);

            var saved = world.SaveWorldState();
            world.Transit.UpdateProgress(7, 90);
            world.Tick(120.0);

            Assert.Equal(10, saved.Transit[0].Progress);
            Assert.Equal(1, saved.Hours);
            Assert.Equal(30L, saved.Ticks);
        }

        [Fact]
        public void AStateWithNoClockIsNotAdopted_AndAnImpossibleLotBecomesNoLot()
        {
            //"Saved on the neighbourhood screen" is a normal thing to load, not a failure - and a
            //hand-edited lot number is a corrupt file, which is not a reason to refuse the load.
            var world = World();
            world.PullClockFrom(new VMClock { Hours = 6, Ticks = 600 });
            world.SetActiveLot(21);

            var empty = NeighbourhoodWorldState.CreateDefault();
            empty.ActiveLot = -5;
            world.ApplyWorldState(empty);

            Assert.Equal(6, world.Clock.Hours);
            Assert.Equal(600L, world.Clock.Ticks);
            Assert.Equal(0, world.ActiveLotNumber);
            Assert.Equal(0, world.Transit.Count);

            Assert.Throws<ArgumentNullException>(() => world.ApplyWorldState(null));
        }

        [Fact]
        public void SavingAWorldIsWhenTheOffscreenStampsLand()
        {
            //The per-lot "this lot moved as far as here" mark is on the layout, which is the
            //sidecar-bound part of the world, so it is written when the caller says the world is
            //about to be saved and not once per frame.
            Capability(NeighbourhoodWorldFlag.Offscreen, true);
            var world = World();
            var marshal = ResidentMarshal(100, 1);
            world.SetActiveLotWith(21, marshal);
            world.SetActiveLot(0);

            world.Tick(GameHours(1));
            Assert.Equal(0, world.Layout.FindLot(21).LastSimulated);

            var saved = world.SaveWorldState();

            var stamped = world.Layout.FindLot(21).LastSimulated;
            Assert.True(stamped > 0, "the lot was simulated and the save did not say so");
            Assert.True(stamped <= world.Clock.Ticks);
            //and the state itself carries no lot contents, which is §5.6's whole rule
            Assert.Equal(NeighbourhoodWorldState.CurrentWorldVersion, saved.WorldVersion);
        }

        [Fact]
        public void SavingAWorldThatNeverSimulatedStampsNothing()
        {
            //The save must not be what builds a simulator: a world with the capability off has
            //nothing to stamp, and asking it to say so should not cost it a stream derivation.
            Capability(NeighbourhoodWorldFlag.Offscreen, false);
            var world = World();

            var saved = world.SaveWorldState();

            Assert.Equal(0, world.Layout.FindLot(21).LastSimulated);
            Assert.Equal(0, saved.ActiveLot);
            Assert.DoesNotContain("offscreen", world.ToString());
        }

        // ---- switching lots ------------------------------------------------------------------------

        [Fact]
        public void ANewWorldIsInNoLotAtAll()
        {
            var world = World();

            Assert.Equal(0, world.ActiveLotNumber);
            Assert.Null(world.ActiveMarshal);
            Assert.Equal(NeighbourhoodLotState.Unloaded, world.LotStore.StateOf(21));
        }

        [Fact]
        public void LeavingALotStashesItsMarshal_AndEnteringTakesItBack()
        {
            //the rule both halves rest on: the store holds the lots the player is not in, and the
            //world holds the one they are - keyed by lot number, as SavedLots already is
            var world = World();
            var lot21 = new VMMarshal();
            world.SetActiveLotWith(21, lot21);

            Assert.Equal(21, world.ActiveLotNumber);
            Assert.Same(lot21, world.ActiveMarshal);
            Assert.Equal(NeighbourhoodLotState.Unloaded, world.LotStore.StateOf(21));

            //walk to the neighbourhood screen: the lot's contents are kept, under its own number
            world.SetActiveLot(0);
            Assert.Equal(0, world.ActiveLotNumber);
            Assert.Null(world.ActiveMarshal);
            Assert.Equal(NeighbourhoodLotState.Active, world.LotStore.StateOf(21));
            VMMarshal stashed;
            Assert.True(world.LotStore.TryGetActive(21, out stashed));
            Assert.Same(lot21, stashed);

            //and back in, which is a resume rather than a fresh load
            world.SetActiveLot(21);
            Assert.Same(lot21, world.ActiveMarshal);
            Assert.Equal(NeighbourhoodLotState.Unloaded, world.LotStore.StateOf(21));
            Assert.Equal(0, world.LotStore.Count);
        }

        [Fact]
        public void ALotWithNothingStoredIsBuiltFromDisk()
        {
            var world = World();

            world.SetActiveLot(22);
            Assert.Equal(22, world.ActiveLotNumber);
            Assert.Null(world.ActiveMarshal);
            Assert.Equal(0, world.LotStore.Count);
        }

        [Fact]
        public void SwitchingBetweenTwoLotsStashesTheOneLeft()
        {
            var world = World();
            world.SetActiveLotWith(21, new VMMarshal());
            var lot22 = new VMMarshal();
            world.SetActiveLotWith(22, lot22);

            VMMarshal first;
            Assert.True(world.LotStore.TryGetActive(21, out first));
            VMMarshal second;
            Assert.False(world.LotStore.TryGetActive(22, out second));
            Assert.Same(lot22, world.ActiveMarshal);
            Assert.Equal(new short[] { 21 }, world.LotStore.LoadedLots);
        }

        [Fact]
        public void AHandedInMarshalIsAReplacement_NotAFallback()
        {
            //direction is in the method name: a caller that just built a lot from its file hands the
            //marshal over, and does not get the stale one back
            var world = World();
            world.SetActiveLotWith(21, new VMMarshal());
            world.SetActiveLot(0);

            var rebuilt = new VMMarshal();
            world.SetActiveLotWith(21, rebuilt);

            Assert.Same(rebuilt, world.ActiveMarshal);
        }

        [Fact]
        public void ReEnteringTheLotYouAreAlreadyInIsAResumeNotASwitch()
        {
            var world = World();
            var resident = new VMMarshal();
            world.SetActiveLotWith(21, resident);

            world.SetActiveLot(21);

            Assert.Equal(21, world.ActiveLotNumber);
            Assert.Same(resident, world.ActiveMarshal);
            Assert.Equal(0, world.LotStore.Count);

            //and if the lot is on the map with nothing on hand, its stored contents are the resume
            world.SetActiveLot(0);
            world.SetActiveLot(21);
            Assert.Same(resident, world.ActiveMarshal);
        }

        [Fact]
        public void ALotNumberThatNamesNoLotIsACallerBugAndSaysSo()
        {
            var world = World();

            Assert.Throws<ArgumentOutOfRangeException>(() => world.SetActiveLot(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.SetActiveLot(short.MaxValue + 1));
            Assert.Equal(0, world.ActiveLotNumber);
        }

        // ---- what the world owns ---------------------------------------------------------------------

        [Fact]
        public void TheWorldOwnsTheLayoutAndTheMapItWasGiven()
        {
            var layout = Layout();
            var map = NeighbourhoodMap.Create(MapSize);
            var world = new SimitoneNeighbourhoodWorld(layout, map, true);

            Assert.Same(layout, world.Layout);
            Assert.Same(layout.Lots, world.Lots);
            Assert.Same(map, world.Map);
            Assert.Equal(Slot, world.Slot);
            Assert.Equal(Seed, world.Seed);
            Assert.Equal(2, world.LotCount);
            Assert.NotNull(world.Query);
            Assert.Same(world.Query, world.Query);
            Assert.NotNull(world.LotStore);
            Assert.NotNull(world.Transit);
        }

        [Fact]
        public void AWorldBuiltFromNothingIsStillAWorld()
        {
            //the neighbourhood screen before a layout has been read must not have to null-check
            var world = new SimitoneNeighbourhoodWorld(null, null, false);

            Assert.NotNull(world.Layout);
            Assert.NotNull(world.Map);
            Assert.NotNull(world.Query);
            Assert.NotNull(world.LotStore);
            Assert.NotNull(world.Transit);
            Assert.Equal(0, world.ActiveLotNumber);
            Assert.False(world.Tick(1.0));
        }

        [Fact]
        public void TheWorldInterfaceIsStillASmallInertShape()
        {
            //the growth of §3 has to cost nothing: no static state to switch on, no write anywhere
            //except the one method that is supposed to be the one path for it
            var properties = new List<string>();
            var methods = new List<string>();
            foreach (var member in typeof(INeighbourhoodWorld).GetMethods())
            {
                if (member.IsSpecialName) continue;
                methods.Add(member.Name);
            }
            foreach (var property in typeof(INeighbourhoodWorld).GetProperties())
            {
                properties.Add(property.Name);
                Assert.False(property.CanWrite, property.Name + " must not be writable");
            }

            Assert.Contains("Tick", methods);
            Assert.Contains("SetActiveLot", methods);
            Assert.Contains("Clock", properties);
            Assert.Contains("LotStore", properties);
            Assert.Contains("Transit", properties);
            Assert.Contains("Query", properties);
            Assert.Contains("WorldEnabled", properties);

            foreach (var field in typeof(INeighbourhoodWorld)
                .GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                Assert.True(field.IsLiteral, "INeighbourhoodWorld." + field.Name + " is mutable static state");
            }
        }

        // ---- the read-only world is still inert ------------------------------------------------------

        [Fact]
        public void AReadOnlyWorldIsUnswitchedAndDoesNotTick()
        {
            var layout = Layout();
            var world = new ReadOnlyNeighbourhoodWorld(layout, UnavailableNeighbourhoodClock.Instance);
            var before = NeighbourhoodLayoutStore.Serialize(layout);

            Assert.False(world.WorldEnabled);
            Assert.False(world.Tick(0.016));
            Assert.False(world.Tick(3600.0));

            Assert.Equal(before, NeighbourhoodLayoutStore.Serialize(layout));
        }

        [Fact]
        public void AReadOnlyWorldHasTheStoresAndChangesNoneOfThem()
        {
            var world = new ReadOnlyNeighbourhoodWorld(Layout(), UnavailableNeighbourhoodClock.Instance);

            Assert.NotNull(world.LotStore);
            Assert.NotNull(world.Transit);
            Assert.Equal(0, world.LotStore.Count);
            Assert.Equal(0, world.Transit.Count);

            //even a caller that writes to them writes into an empty store of its own
            world.LotStore.SetHollow(21, new FSO.SimAntics.Marshals.Hollow.VMHollowMarshal());
            world.Transit.Add(7, 21, 22, 3);

            Assert.Equal(1, world.LotStore.Count);
            Assert.Equal(1, world.Transit.Count);

            var other = new ReadOnlyNeighbourhoodWorld(Layout(), UnavailableNeighbourhoodClock.Instance);
            Assert.Equal(0, other.LotStore.Count);
            Assert.Equal(0, other.Transit.Count);
        }

        [Fact]
        public void AReadOnlyWorldIgnoresTheLotSwitchItIsToldToMake()
        {
            //its active lot is whatever its constructor was told: a world that is somebody else's
            //data does not get to change it
            var world = new ReadOnlyNeighbourhoodWorld(Layout(), UnavailableNeighbourhoodClock.Instance, 21);

            world.SetActiveLot(22);

            Assert.Equal(21, world.ActiveLotNumber);
        }

        [Fact]
        public void AReadOnlyWorldCanStillAnswerAQueryAboutItsLayout()
        {
            //built on first use, off a blank map - the lot half is real, the road half is not, and
            //no Part A caller ever asks
            var world = new ReadOnlyNeighbourhoodWorld(Layout(), null);

            NeighbourhoodQuery query = world.Query;
            Assert.NotNull(query);
            Assert.Same(query, world.Query);
            Assert.Equal(2, query.Lots.Count);

            NeighbourhoodLot lot;
            Assert.True(query.TryGetLot(21, out lot));
            Assert.False(query.TryGetLot(99, out lot));
        }
    }
}
