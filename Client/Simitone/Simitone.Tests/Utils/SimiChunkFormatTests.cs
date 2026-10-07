using System;
using System.IO;
using System.Linq;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The SIMI chunk's own wire format: the budget-day record and the version/length rule
    /// that frames it. Synthetic data only - no TS1 install, no <c>Content</c>, no graphics
    /// device, no VM.
    ///
    /// <para>
    /// SIMI is the chunk a lot save is judged on. <c>VMTS1LotState.UpdateSIMI</c> hands it the
    /// VM's <c>GlobalState</c> and the lot's three values, <c>SaveService</c> writes it into
    /// <c>House{NN}.iff</c>, and <c>TS1GameScreen.GetLotPurchaseValue</c> reads the values back
    /// on eviction to refund the lot. Every byte of it is therefore load-bearing and none of it
    /// was covered: every SIMI in the test suite was default-constructed, so
    /// <c>SIMIBudgetDay.Valid</c> was 0 and <c>Write</c> returned before writing a single
    /// expense. The two defects below both lived in the branches that no test could reach.
    /// </para>
    ///
    /// <para>
    /// Two things make these cheap to pin. A SIMI can be written and re-read through a bare
    /// <c>MemoryStream</c> - <c>Read</c> never touches its <c>IffFile</c> - so the field order
    /// is testable without building a file; and a real <c>House{NN}.iff</c> is still exercised,
    /// because the framing is where a chunk can disagree with itself even when its fields are
    /// right.
    /// </para>
    /// </summary>
    public sealed class SimiChunkFormatTests
    {
        /// <summary>Length of <c>SIMI.GlobalData</c>, and of the VM's <c>GlobalState</c>.</summary>
        private const int GlobalsLength = 38;

        /// <summary>
        /// How many globals a 0x3E SIMI carries on the wire. That is the format - Read and Write
        /// both use 32 - rather than a defect, so the assertions below check the 32 and say so
        /// where the remaining six are expected to read 0.
        /// </summary>
        private const int GlobalsOnWireAtCurrentVersion = 0x20;

        #region helpers

        /// <summary>
        /// A budget day with <c>Valid</c> set and every one of the eight money fields given a
        /// value no other field in the record has, so a field that comes back holding a
        /// neighbour's number is caught by name rather than by luck.
        /// </summary>
        private static SIMI.SIMIBudgetDay BudgetDay(int seed)
        {
            return new SIMI.SIMIBudgetDay
            {
                Valid = 1,
                MiscIncome = 1000 + seed,
                JobIncome = 2000 + seed,
                ServiceExpense = 3000 + seed,
                FoodExpense = 4000 + seed,
                BillsExpense = 5000 + seed,
                MiscExpense = 6000 + seed,
                HouseholdExpense = 7000 + seed,
                ArchitectureExpense = 8000 + seed,
            };
        }

        private static SIMI NewSimi(uint version, short[] globals = null)
        {
            return new SIMI
            {
                ChunkID = 1,
                ChunkType = "SIMI",
                ChunkLabel = "Lot Information",
                ChunkProcessed = true,
                Version = version,
                LotValue = 12000,
                ObjectsValue = 3400,
                ArchitectureValue = 5700,
                Unknown1 = 0x7E1E,
                Unknown2 = 2,
                Unknown3 = 3,
                GUID1 = 0x11223344,
                GUID2 = unchecked((int)0xDEADBEEF),
                Unknown4 = 4,
                GlobalData = globals ?? Enumerable.Range(0, GlobalsLength).Select(i => (short)(1000 + i)).ToArray(),
                BudgetDays = Enumerable.Range(0, SIMI.BudgetDayCount).Select(BudgetDay).ToArray(),
            };
        }

        private static short[] GlobalsWithNoZeros()
        {
            var globals = new short[GlobalsLength];
            for (int i = 0; i < globals.Length; i++) globals[i] = (short)(2000 + i * 7);
            return globals;
        }

        /// <summary>Writes a chunk to bytes and reads it straight back.</summary>
        private static SIMI RoundTrip(SIMI simi)
        {
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                Assert.True(simi.Write(null, stream));
                bytes = stream.ToArray();
            }
            var read = new SIMI();
            using (var stream = new MemoryStream(bytes))
            {
                read.Read(null, stream);
            }
            return read;
        }

        private static byte[] Write(SIMI simi)
        {
            using (var stream = new MemoryStream())
            {
                simi.Write(null, stream);
                return stream.ToArray();
            }
        }

        #endregion

        #region F-01 - the budget-day record

        /// <summary>
        /// The whole reason this class exists. <c>SIMIBudgetDay.Write</c> emitted
        /// <c>MiscIncome</c> in the slot <c>Read</c> fills from <c>MiscExpense</c>, so every
        /// save stored the day's miscellaneous income twice and threw the day's miscellaneous
        /// expense away; the value came back under the wrong name, silently, on a file the game
        /// had just told the player was safely committed.
        /// </summary>
        [Fact]
        public void EveryBudgetDayFieldSurvivesAWriteAndRead()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION);

            var read = RoundTrip(simi);

            Assert.Equal(SIMI.BudgetDayCount, read.BudgetDays.Length);
            for (int i = 0; i < SIMI.BudgetDayCount; i++)
            {
                var expected = BudgetDay(i);
                var actual = read.BudgetDays[i];

                Assert.Equal(1, actual.Valid);
                Assert.Equal(expected.MiscIncome, actual.MiscIncome);
                Assert.Equal(expected.JobIncome, actual.JobIncome);
                Assert.Equal(expected.ServiceExpense, actual.ServiceExpense);
                Assert.Equal(expected.FoodExpense, actual.FoodExpense);
                Assert.Equal(expected.BillsExpense, actual.BillsExpense);
                //the field that was never written at all
                Assert.Equal(expected.MiscExpense, actual.MiscExpense);
                Assert.Equal(expected.HouseholdExpense, actual.HouseholdExpense);
                Assert.Equal(expected.ArchitectureExpense, actual.ArchitectureExpense);
            }
        }

        /// <summary>
        /// The same defect, named narrowly: <c>MiscExpense</c> came back holding
        /// <c>MiscIncome</c>. A single assertion on the two fields side by side is the cheapest
        /// possible guard, because the two are adjacent and a writer that transposes them
        /// satisfies nothing else.
        /// </summary>
        [Fact]
        public void MiscellaneousExpenseIsNotWrittenAsMiscellaneousIncome()
        {
            var day = new SIMI.SIMIBudgetDay
            {
                Valid = 1,
                MiscIncome = 11,
                MiscExpense = 99,
            };

            var simi = NewSimi(SIMI.CURRENT_VERSION);
            simi.BudgetDays = new[] { day };

            var read = RoundTrip(simi);

            Assert.Equal(11, read.BudgetDays[0].MiscIncome);
            Assert.NotEqual(read.BudgetDays[0].MiscIncome, read.BudgetDays[0].MiscExpense);
            Assert.Equal(99, read.BudgetDays[0].MiscExpense);
        }

        /// <summary>
        /// A day with <c>Valid == 0</c> is a four-byte record of zeroes, and that is all: the
        /// reader stops there, so writing the eight fields behind it would shift every later
        /// day. Every default-constructed fixture in the suite depends on this being true.
        /// </summary>
        [Fact]
        public void AnEmptyBudgetDayIsFourBytesOfZeroesAndNothingElse()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION);
            simi.BudgetDays = new SIMI.SIMIBudgetDay[SIMI.BudgetDayCount];

            var read = RoundTrip(simi);

            for (int i = 0; i < SIMI.BudgetDayCount; i++)
            {
                Assert.Equal(0, read.BudgetDays[i].Valid);
                Assert.Equal(0, read.BudgetDays[i].MiscIncome);
                Assert.Equal(0, read.BudgetDays[i].MiscExpense);
            }
        }

        /// <summary>
        /// A day with only some fields set must not drag the record's length around - the
        /// <c>Valid</c> flag, not which fields happen to be non-zero, is what decides it.
        /// </summary>
        [Fact]
        public void AValidDayWithNoMoneyInItStillWritesTheWholeRecord()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION);
            simi.BudgetDays = new[]
            {
                new SIMI.SIMIBudgetDay { Valid = 1 },
                new SIMI.SIMIBudgetDay { Valid = 0 },
                new SIMI.SIMIBudgetDay { Valid = 1, MiscExpense = 42 },
            };

            var read = RoundTrip(simi);

            Assert.Equal(1, read.BudgetDays[0].Valid);
            Assert.Equal(0, read.BudgetDays[0].MiscExpense);
            Assert.Equal(0, read.BudgetDays[1].Valid);
            Assert.Equal(1, read.BudgetDays[2].Valid);
            Assert.Equal(42, read.BudgetDays[2].MiscExpense);
        }

        #endregion

        #region F-02 - one version, one payload length

        /// <summary>
        /// The framing contract. A chunk asking for a version this build does not write must
        /// still produce a file that describes itself: the version stamped in the header and
        /// the number of globals actually written have to agree, or a reader shifts every
        /// value that follows the globals.
        /// <para>
        /// It used to disagree for any chunk above 0x3E, because <c>Write</c> stamped 0x3E but
        /// sized the payload from the instance's own version against a different threshold
        /// (<c>&gt; 0x3E</c> where <c>Read</c> used <c>&gt; 0x3F</c>). 0x3F was the sharpest
        /// case: both thresholds agreed the payload was 64 globals and both read it as 32, so
        /// nothing threw and <c>LotValue</c>, <c>ObjectsValue</c> and <c>ArchitectureValue</c>
        /// came back out of the middle of the globals - and <c>PurchaseValue</c>, the number a
        /// player is refunded on eviction, was garbage.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(0x00u)]
        [InlineData(0x3Du)]
        [InlineData(0x3Eu)]
        [InlineData(0x3Fu)]
        [InlineData(0x40u)]
        [InlineData(0x41u)]
        [InlineData(0xFFu)]
        public void AChunkAtAnyVersionIsWrittenSelfConsistently(uint version)
        {
            var simi = NewSimi(version, GlobalsWithNoZeros());

            var read = RoundTrip(simi);

            //the header, the layout, and the instance all agree afterwards
            Assert.Equal(SIMI.CURRENT_VERSION, read.Version);
            Assert.Equal(SIMI.CURRENT_VERSION, simi.Version);
            Assert.Equal(SIMI.GlobalsOnWire(read.Version), SIMI.GlobalsOnWire(SIMI.CURRENT_VERSION));

            //and none of the values that sit after the globals drifted
            Assert.Equal(simi.Unknown1, read.Unknown1);
            Assert.Equal(simi.Unknown2, read.Unknown2);
            Assert.Equal(simi.Unknown3, read.Unknown3);
            Assert.Equal(simi.GUID1, read.GUID1);
            Assert.Equal(simi.GUID2, read.GUID2);
            Assert.Equal(simi.Unknown4, read.Unknown4);
            Assert.Equal(12000, read.LotValue);
            Assert.Equal(3400, read.ObjectsValue);
            Assert.Equal(5700, read.ArchitectureValue);
            Assert.Equal(12000 + 3400 + (5700 * 7) / 10, read.PurchaseValue);

            //and the globals a 0x3E chunk carries came through, in order
            var globals = GlobalsWithNoZeros();
            Assert.Equal(globals.Take(GlobalsOnWireAtCurrentVersion).ToArray(),
                read.GlobalData.Take(GlobalsOnWireAtCurrentVersion).ToArray());
        }

        /// <summary>
        /// The payload length is a function of the stamped version and of nothing else, and the
        /// stamped version is the one constant. Pinning both together means the two cannot be
        /// edited apart again.
        /// </summary>
        [Fact]
        public void TheGlobalsRuleIsOneFunctionSharedByReadAndWrite()
        {
            Assert.Equal(0x20, SIMI.GlobalsOnWire(0x00));
            Assert.Equal(0x20, SIMI.GlobalsOnWire(0x3E));
            //0x3F is the boundary the old two-threshold rule disagreed about: Read and Write
            //must now say the same thing about it.
            Assert.Equal(0x20, SIMI.GlobalsOnWire(0x3F));
            Assert.Equal(0x40, SIMI.GlobalsOnWire(0x40));

            //and the shipped version is a narrow layout, which is the whole point of pinning it
            Assert.Equal(0x20, SIMI.GlobalsOnWire(SIMI.CURRENT_VERSION));
        }

        /// <summary>
        /// A lot is saved, loaded and saved again, arbitrarily many times. Every one of those
        /// writes has to produce the same bytes, or the file drifts a little on every pass -
        /// the defect this whole finding was: the instance's version disagreed with the header,
        /// so the second save of a chunk read at 0x3F was a different file from the first.
        /// </summary>
        [Fact]
        public void WritingTheSameChunkTwiceProducesTheSameBytes()
        {
            var simi = NewSimi(0x40, GlobalsWithNoZeros());

            var first = Write(simi);
            var second = Write(simi);

            Assert.Equal(first, second);
        }

        /// <summary>
        /// The globals the save was handed are the globals that reach the file, and the chunk
        /// keeps the caller's array. <c>UpdateSIMI</c> assigns the live <c>vm.GlobalState</c>
        /// here, so a writer that replaced or reordered the array would lose the lot's
        /// globals on every save.
        /// </summary>
        [Fact]
        public void TheCallersGlobalArrayIsWrittenAsIsAndKept()
        {
            var globals = GlobalsWithNoZeros();
            var simi = NewSimi(SIMI.CURRENT_VERSION, globals);

            var read = RoundTrip(simi);

            Assert.Same(globals, simi.GlobalData);
            Assert.Equal(globals, simi.GlobalData);
            Assert.Equal(globals.Take(GlobalsOnWireAtCurrentVersion).ToArray(),
                read.GlobalData.Take(GlobalsOnWireAtCurrentVersion).ToArray());
        }

        /// <summary>
        /// A chunk that arrives with no globals array is still written, as a zeroed one, rather
        /// than taking the save down. Only a genuinely absent array is defaulted - a present
        /// array is never overwritten, because that would discard the lot's globals.
        /// </summary>
        [Fact]
        public void AChunkWithNoGlobalArrayIsWrittenRatherThanThrowing()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION);
            simi.GlobalData = null;
            Assert.Null(simi.GlobalData);

            var read = RoundTrip(simi);

            Assert.NotNull(read.GlobalData);
            Assert.Equal(GlobalsLength, read.GlobalData.Length);
            Assert.Equal(12000, read.LotValue);
            Assert.Equal(3400, read.ObjectsValue);
            Assert.Equal(5700, read.ArchitectureValue);
        }

        /// <summary>
        /// A globals array shorter than the layout still fills the frame, and the values
        /// beside the globals are unaffected by the padding.
        /// </summary>
        [Fact]
        public void AShortGlobalArrayIsPaddedRatherThanTruncatingTheValuesAfterIt()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION, new short[] { 7, 8, 9 });

            var read = RoundTrip(simi);

            Assert.Equal(7, read.GlobalData[0]);
            Assert.Equal(8, read.GlobalData[1]);
            Assert.Equal(9, read.GlobalData[2]);
            Assert.Equal(0, read.GlobalData[3]);
            Assert.Equal(0, read.GlobalData[GlobalsOnWireAtCurrentVersion - 1]);
            Assert.Equal(12000, read.LotValue);
            Assert.Equal(3400, read.ObjectsValue);
            Assert.Equal(5700, read.ArchitectureValue);
        }

        #endregion

        #region the six budget days are a constant, not a count

        /// <summary>
        /// <c>Write</c> indexed <c>BudgetDays[0..5]</c> unconditionally, so a chunk that
        /// arrived with no array - or a short one, or a null day in it - threw an
        /// <c>NullReferenceException</c> or an <c>IndexOutOfRangeException</c> out of the
        /// middle of a save. Both test helpers worked around it by hand-building a six-element
        /// array, which is a test suite shaped around a defect.
        /// </summary>
        [Fact]
        public void AChunkWithNoBudgetDaysIsStillWritten()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION);
            simi.BudgetDays = null;

            var read = RoundTrip(simi);

            Assert.Equal(SIMI.BudgetDayCount, read.BudgetDays.Length);
            for (int i = 0; i < SIMI.BudgetDayCount; i++)
            {
                Assert.Equal(0, read.BudgetDays[i].Valid);
            }
            Assert.Equal(12000, read.LotValue);
        }

        [Fact]
        public void AChunkWithTooFewBudgetDaysStillWritesSix()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION);
            simi.BudgetDays = new[] { BudgetDay(0), BudgetDay(1) };

            var read = RoundTrip(simi);

            Assert.Equal(SIMI.BudgetDayCount, read.BudgetDays.Length);
            Assert.Equal(1000, read.BudgetDays[0].MiscIncome);
            Assert.Equal(1001, read.BudgetDays[1].MiscIncome);
            //the slots the chunk never had are empty days, not a short file
            Assert.Equal(0, read.BudgetDays[2].Valid);
            Assert.Equal(0, read.BudgetDays[5].Valid);
        }

        [Fact]
        public void ANullBudgetDayIsWrittenAsAnEmptyDay()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION);
            simi.BudgetDays = new SIMI.SIMIBudgetDay[SIMI.BudgetDayCount];
            simi.BudgetDays[2] = BudgetDay(2);

            var read = RoundTrip(simi);

            Assert.Equal(SIMI.BudgetDayCount, read.BudgetDays.Length);
            Assert.Equal(2002, read.BudgetDays[2].JobIncome);
            Assert.Equal(0, read.BudgetDays[1].Valid);
            Assert.Equal(0, read.BudgetDays[3].Valid);
        }

        /// <summary>
        /// More days than the format has is not an error, and must not lengthen the file: the
        /// frame is six records and <c>Read</c> will only ever produce six.
        /// </summary>
        [Fact]
        public void ExtraBudgetDaysAreNotWritten()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION);
            simi.BudgetDays = Enumerable.Range(0, 10).Select(BudgetDay).ToArray();

            var read = RoundTrip(simi);

            Assert.Equal(SIMI.BudgetDayCount, read.BudgetDays.Length);
            Assert.Equal(1000, read.BudgetDays[0].MiscIncome);
            Assert.Equal(1005, read.BudgetDays[5].MiscIncome);
        }

        #endregion

        #region inside a real house file

        /// <summary>
        /// The same chunk through <c>IffFile</c> rather than a bare stream, because the framing
        /// is what a chunk can disagree with: the point is that the values a lot save exists to
        /// preserve - the globals and the three money values - come back off a file on disk.
        /// </summary>
        [Fact]
        public void AValidBudgetDayAndTheLotValuesSurviveAWholeHouseFileRoundTrip()
        {
            var simi = NewSimi(SIMI.CURRENT_VERSION, GlobalsWithNoZeros());
            var iff = new IffFile();
            iff.AddChunk(simi);

            var path = Path.Combine(Path.GetTempPath(),
                "simitone-simi-" + Guid.NewGuid().ToString("N") + ".iff");
            try
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    iff.Write(stream);
                }

                var read = new IffFile(path);
                read.MarkThrowaway();
                var back = read.Get<SIMI>(1);

                Assert.NotNull(back);
                Assert.Equal(SIMI.CURRENT_VERSION, back.Version);
                Assert.Equal(12000, back.LotValue);
                Assert.Equal(3400, back.ObjectsValue);
                Assert.Equal(5700, back.ArchitectureValue);
                Assert.Equal(GlobalsWithNoZeros().Take(GlobalsOnWireAtCurrentVersion).ToArray(),
                    back.GlobalData.Take(GlobalsOnWireAtCurrentVersion).ToArray());
                for (int i = 0; i < SIMI.BudgetDayCount; i++)
                {
                    var expected = BudgetDay(i);
                    Assert.Equal(expected.MiscIncome, back.BudgetDays[i].MiscIncome);
                    Assert.Equal(expected.BillsExpense, back.BudgetDays[i].BillsExpense);
                    Assert.Equal(expected.MiscExpense, back.BudgetDays[i].MiscExpense);
                    Assert.Equal(expected.ArchitectureExpense, back.BudgetDays[i].ArchitectureExpense);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException)
                {
                    //best effort: a leftover temp file must never fail the run
                }
            }
        }

        #endregion
    }
}
