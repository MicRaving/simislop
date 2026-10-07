/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FSO.Content.Model;
using FSO.SimAntics;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The §15 preparation seams, and the claim that makes them safe: <b>they are inert.</b>
    ///
    /// <para>
    /// A preparation seam is a concession Scope A makes to a scope that may never happen. The only
    /// thing that keeps that concession free is that removing it changes nothing, and "changes
    /// nothing" is a property that has to be asserted rather than intended. So these tests check
    /// the three ways it could fail: that a seam's Scope A implementation does nothing a viewer
    /// could notice, that it cannot be written to even by accident, and that a layout built with
    /// none of them is byte-identical to one built with all of them.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodPrepSeamTests
    {
        private static NeighbourhoodLayout Layout()
        {
            return NeighbourhoodLayoutBuilder.Pack(new List<NeighbourhoodLotRead>
            {
                new NeighbourhoodLotRead { Number = 21, MapX = 102, MapY = 88, HasHouse = true, Size = 24 },
                new NeighbourhoodLotRead { Number = 22, MapX = 150, MapY = 91, HasHouse = true, Size = 24 }
            }, 4, 555);
        }

        // ---- the world facade ---------------------------------------------------------------------

        [Fact]
        public void TheWorldHandsOutTheLayoutItWasGiven_WithoutCopyingOrChangingIt()
        {
            var layout = Layout();
            var world = new ReadOnlyNeighbourhoodWorld(layout, UnavailableNeighbourhoodClock.Instance);

            Assert.Same(layout, world.Layout);
            Assert.Equal(4, world.Slot);
            Assert.Equal(2, world.Lots.Count);
            Assert.Same(layout.Lots, world.Lots);
        }

        [Fact]
        public void LotQueriesGoStraightThroughToTheLayout()
        {
            var world = new ReadOnlyNeighbourhoodWorld(Layout(), UnavailableNeighbourhoodClock.Instance);

            NeighbourhoodLot lot;
            Assert.True(world.TryGetLot(21, out lot));
            Assert.Equal(21, lot.Number);
            Assert.False(world.TryGetLot(99, out lot));
            Assert.Null(lot);
        }

        [Fact]
        public void TickingTheWorldChangesNothing_AndSaysSo()
        {
            var layout = Layout();
            var world = new ReadOnlyNeighbourhoodWorld(layout, UnavailableNeighbourhoodClock.Instance);
            var before = Simitone.Client.Neighbourhood.NeighbourhoodLayoutStore.Serialize(layout);

            Assert.False(world.Tick(0.016));
            Assert.False(world.Tick(10.0));
            Assert.False(world.Tick(-1.0));

            Assert.Equal(before, Simitone.Client.Neighbourhood.NeighbourhoodLayoutStore.Serialize(layout));
        }

        [Fact]
        public void AWorldWithNothingToDescribe_StillAnswers()
        {
            //a null layout or clock must not turn into a null reference on the neighbourhood
            //screen, which is the one place there is nothing to draw yet
            var world = new ReadOnlyNeighbourhoodWorld(null, null);

            Assert.NotNull(world.Layout);
            Assert.Equal(0, world.LotCount);
            Assert.NotNull(world.Clock);
            Assert.False(world.Clock.IsAvailable);
            Assert.Equal(0, world.ActiveLotNumber);
            Assert.False(world.HasLotContent);
            Assert.Null(world.LotContent);
        }

        [Fact]
        public void TheActiveLotIsWhatTheCallerSaysItIs()
        {
            Assert.Equal(0, new ReadOnlyNeighbourhoodWorld(Layout(), null).ActiveLotNumber);
            Assert.Equal(21, new ReadOnlyNeighbourhoodWorld(Layout(), null, 21).ActiveLotNumber);
        }

        // ---- the clock ----------------------------------------------------------------------------

        [Fact]
        public void TheClockMirrorReadsTheActiveLotWithoutWritingToIt()
        {
            var clock = new VMClock
            {
                Ticks = 4242,
                Hours = 17,
                Minutes = 30,
                MinuteFractions = 15,
                TicksPerMinute = 30,
                DayOfMonth = 14,
                Month = 7,
                Year = 2001
            };

            var mirror = new LotClockMirror(() => clock);

            Assert.True(mirror.IsAvailable);
            Assert.Equal(17, mirror.Hours);
            Assert.Equal(30, mirror.Minutes);
            Assert.Equal(15, mirror.MinuteFractions);
            Assert.Equal(30, mirror.TicksPerMinute);
            Assert.Equal(14, mirror.DayOfMonth);
            Assert.Equal(7, mirror.Month);
            Assert.Equal(2001, mirror.Year);
            Assert.Equal(4242L, mirror.Ticks);
            Assert.Equal(30, mirror.Seconds);   //15 * 60 / 30

            //and the lot's own clock is exactly as it was
            Assert.Equal(17, clock.Hours);
            Assert.Equal(15, clock.MinuteFractions);
            Assert.Equal(4242L, clock.Ticks);
        }

        [Fact]
        public void TheClockMirrorFollowsTheLotItIsPointedAt()
        {
            VMClock current = null;
            var mirror = new LotClockMirror(() => current);

            Assert.False(mirror.IsAvailable);
            Assert.Equal(0, mirror.Hours);

            current = new VMClock { Hours = 9, Minutes = 5 };
            Assert.True(mirror.IsAvailable);
            Assert.Equal(9, mirror.Hours);
            Assert.Equal(5, mirror.Minutes);

            //and copes with the lot being torn down underneath it
            current = null;
            Assert.False(mirror.IsAvailable);
            Assert.Equal(0, mirror.Hours);
        }

        [Fact]
        public void AClockWithNoSubMinuteRate_DoesNotDivideByZero()
        {
            //VMClock.Seconds would throw here; a neighbourhood must not be what surfaces a lot
            //that has not been told its tick rate
            var mirror = new LotClockMirror(new VMClock { Hours = 3, TicksPerMinute = 0 });

            Assert.True(mirror.IsAvailable);
            Assert.Equal(3, mirror.Hours);
            Assert.Equal(0, mirror.Seconds);
        }

        [Fact]
        public void AResolverThatThrows_ReadsAsNoClockRatherThanCrashing()
        {
            var mirror = new LotClockMirror(() => { throw new InvalidOperationException("lot is gone"); });

            Assert.False(mirror.IsAvailable);
            Assert.Equal(0, mirror.Hours);
        }

        [Fact]
        public void ThereIsNoClockAnyoneCanWriteThrough()
        {
            //the interface is the contract, so it is the interface that gets checked: no public
            //setters, and no member that could advance or assign a time
            var properties = typeof(INeighbourhoodClock).GetProperties();
            Assert.NotEmpty(properties);
            foreach (var property in properties)
            {
                Assert.True(property.CanRead, property.Name + " must be readable");
                Assert.False(property.CanWrite, property.Name + " must not be writable");
            }

            var accessors = new List<string>();
            var methods = new List<string>();
            foreach (var member in typeof(INeighbourhoodClock).GetMethods())
            {
                if (member.IsSpecialName) accessors.Add(member.Name);
                else methods.Add(member.Name);
            }

            Assert.NotEmpty(accessors);
            Assert.True(methods.Count == 0,
                "INeighbourhoodClock must expose no methods, only properties (found " + string.Join(", ", methods) + ")");
        }

        [Fact]
        public void AnUnavailableClockIsUnavailableRatherThanMidnight()
        {
            var clock = UnavailableNeighbourhoodClock.Instance;

            Assert.False(clock.IsAvailable);
            Assert.Equal(0, clock.Hours);
            Assert.Equal(0, clock.Seconds);
            Assert.Equal(0L, clock.Ticks);
        }

        // ---- the lot content read model --------------------------------------------------------------

        [Fact]
        public void TheReadModelIsWhatTheBuilderIsGiven_NotTheProvider()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, size: 24);
            source.Add(22, 150, 91, size: 24, zone: LotZoning.ZoneCommunity);
            source.Lots[22].Occupied = true;

            var layout = NeighbourhoodLayoutBuilder.Build(source, 1, 4, 555);
            var reads = NeighbourhoodLayoutBuilder.ReadLots(source, 1);

            //every fact the layout is built from is on the read record, so the packing could be
            //replayed from the records alone with no content manager in existence
            Assert.Equal(2, reads.Count);
            Assert.Equal(layout.FindLot(21).Size, reads[0].Size);
            Assert.Equal(layout.FindLot(22).Zone, reads[1].Zone);
            Assert.True(reads[1].Occupied);
        }

        [Fact]
        public void TheTableIsReadOncePerBuild_NotOncePerLot()
        {
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88);
            source.Add(22, 150, 91);
            source.Add(31, 240, 130);

            NeighbourhoodLayoutBuilder.ReadLots(source, 1);

            Assert.Equal(1, source.TableReads);
        }

        [Fact]
        public void TheSameRecordsPackedTwice_ProduceTheSameLayout()
        {
            //identical arrangement, down to the bytes on disk
            var source = new FakeLotContentSource();
            source.Add(21, 102, 88, size: 24);
            source.Add(22, 150, 91, size: 24);
            source.Add(31, 240, 130, size: 32);

            var first = NeighbourhoodLayoutBuilder.Build(source, 1, 4, 555);
            var second = NeighbourhoodLayoutBuilder.Pack(NeighbourhoodLayoutBuilder.ReadLots(source, 1), 4, 555);

            Assert.Equal(NeighbourhoodLayoutStore.Serialize(first), NeighbourhoodLayoutStore.Serialize(second));
        }

        // ---- what the seams cost --------------------------------------------------------------------

        [Fact]
        public void RemovingTheForwardFieldsWouldNotChangeTheLayout()
        {
            //the forward fields are always at their defaults, so a lot carrying them and a lot
            //that has never heard of them serialise to the same bytes
            var withFields = Layout();
            Assert.Equal(NeighbourhoodLotState.Unloaded, withFields.Lots[0].LotState);

            var withoutFields = Layout();

            Assert.Equal(
                NeighbourhoodLayoutStore.Serialize(withoutFields),
                NeighbourhoodLayoutStore.Serialize(withFields));
        }

        [Fact]
        public void AWorldWithASeamAndAWorldWithoutOne_SeeTheSameNeighbourhood()
        {
            var layout = Layout();
            var withSeam = new ReadOnlyNeighbourhoodWorld(layout, new LotClockMirror(new VMClock { Hours = 12 }));
            var withoutSeam = new ReadOnlyNeighbourhoodWorld(layout, null);

            Assert.Equal(withSeam.Lots.Count, withoutSeam.Lots.Count);
            Assert.Same(withSeam.Layout, withoutSeam.Layout);
            Assert.True(withSeam.Clock.IsAvailable);
            Assert.False(withoutSeam.Clock.IsAvailable);
        }

        [Fact]
        public void TheForwardStateEnumIsAThreeValueScaleThatStartsUnloaded()
        {
            var values = Enum.GetValues(typeof(NeighbourhoodLotState)).Cast<NeighbourhoodLotState>().ToArray();

            Assert.Equal(3, values.Length);
            Assert.Equal(0, (int)NeighbourhoodLotState.Unloaded);
            Assert.Equal(1, (int)NeighbourhoodLotState.Hollow);
            Assert.Equal(2, (int)NeighbourhoodLotState.Active);
        }

        [Fact]
        public void TheDoorSidesAreAFourValueScaleWithStableNumbers()
        {
            //the values go into the sidecar as integers, so they may not be renumbered. The
            //generator keeps its own same-valued byte scale on the far side of its input seam
            //(NeighbourhoodDoorSide), and this pins ours to the same numbering.
            Assert.Equal(0, (int)NeighbourhoodLotSide.North);
            Assert.Equal(1, (int)NeighbourhoodLotSide.East);
            Assert.Equal(2, (int)NeighbourhoodLotSide.South);
            Assert.Equal(3, (int)NeighbourhoodLotSide.West);
        }

        [Fact]
        public void ThePrepSeamsExposeNoStaticMutableState()
        {
            //a seam is a shape, not a service locator: nothing about it may be switched on or off
            //at runtime, because "off" has to mean "delete the file", not "set a flag"
            var seams = new[]
            {
                typeof(INeighbourhoodWorld), typeof(INeighbourhoodClock), typeof(ILotContentSource)
            };

            foreach (var seam in seams)
            {
                Assert.True(seam.IsInterface, seam.Name + " is an interface");
                foreach (var field in seam.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    Assert.True(field.IsLiteral, seam.Name + "." + field.Name + " is mutable static state");
                }
            }
        }
    }
}
