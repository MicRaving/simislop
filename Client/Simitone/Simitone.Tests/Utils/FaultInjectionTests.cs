using System;
using System.IO;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The Phase 1 "fault-injection checklist" as headless regression tests.
    ///
    /// Each scenario that previously could only be confirmed by hand against a real install is
    /// pinned here through the guard the production code actually calls. The live re-run is still
    /// tracked in <c>docs/PHASE3_TEST_MATRIX.md</c>, but a regression in any of these guards now
    /// fails the build rather than waiting for a manual pass.
    ///
    /// Scenario map:
    /// <list type="bullet">
    /// <item>corrupt save file -> <see cref="AtomicFileCommit"/>, <see cref="TravelSave.IsValidHeader"/>, <see cref="BitmapDecoder.Read"/></item>
    /// <item>bad lot name/location -> <see cref="LotLocationParser.TryParseLotLocation"/>, <see cref="LotTravel.TryRestore{T}"/></item>
    /// <item>empty/invalid CAS selection -> <see cref="CasValidation"/></item>
    /// <item>over-budget buy -> <see cref="BuildBudget"/></item>
    /// <item>family delete / eviction -> <see cref="CasEdit.MergeFamilyGuids"/>, <see cref="LotPersistence.IsPersistableLot"/></item>
    /// </list>
    /// </summary>
    public sealed class FaultInjectionTests
    {
        // ---- corrupt save file ------------------------------------------------------------

        [Fact]
        public void ACommitOntoAPathHeldByADirectoryDoesNotSilentlyEatThePayload()
        {
            var dir = Path.Combine(Path.GetTempPath(), "simitone-fault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "House01.iff");
            Directory.CreateDirectory(target); // a directory where the file should go

            try
            {
                // `Assert.ThrowsAny<Exception>` is what this used to be, and it answers nothing:
                // any exception satisfies it, so a change that made the commit fail for an
                // unrelated reason - a null argument check, a relative-path guard - would still
                // pass while the actual contract went untested. The two things the contract says
                // are that the filesystem refused (an IOException, not an ArgumentException from
                // our own validation) and that the payload is recoverable.
                var thrown = Record.Exception(() => AtomicFileCommit.Write(target, new byte[] { 1, 2, 3 }));

                Assert.IsAssignableFrom<IOException>(thrown);
                Assert.True(Directory.Exists(target), "the directory that was in the way is untouched");
                Assert.True(File.Exists(target + ".tmp"), "the payload is staged beside it");
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target + ".tmp"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ACorruptTravelSlotIsRejectedRatherThanThrowing()
        {
            // too short / no magic / wrong version must all answer "not valid".
            Assert.False(TravelSave.IsValidHeader(new byte[0], out _, out _));
            Assert.False(TravelSave.IsValidHeader(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B }, out _, out _));
            // truncated garbage of a plausible length must not throw either
            var junk = new byte[TravelSave.HeaderLength + 8];
            Assert.False(TravelSave.IsValidHeader(junk, out _, out _));
        }

        [Fact]
        public void AnUndecodableImageReturnsNullInsteadOfThrowing()
        {
            using (var garbage = new MemoryStream(new byte[] { 0x42, 0x4D, 0xFF, 0xFF, 0x00, 0x01 }))
            {
                Assert.Null(BitmapDecoder.Read(garbage));
            }
        }

        // ---- bad lot name / location ------------------------------------------------------

        [Theory]
        [InlineData("bad")]
        [InlineData("1,2")]
        [InlineData("x,1,2")]
        [InlineData("99,1,2")]
        public void ABadLotLocationIsRejected(string raw)
        {
            Assert.False(LotLocationParser.TryParseLotLocation(raw, out _, out _, out _));
        }

        [Fact]
        public void RestoringALotThatWasNeverStashedFailsCleanly()
        {
            var saved = new System.Collections.Generic.Dictionary<short, string>();
            Assert.False(LotTravel.TryRestore(saved, 5, out var marshal));
            Assert.Null(marshal);
        }

        // ---- empty / invalid CAS selection ------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnEmptyCasFamilyNameIsInvalid(string name)
        {
            Assert.False(CasValidation.IsValidFamilyName(name));
        }

        [Fact]
        public void TheCasFamilyCapAndBodyParserNeverThrowOnBadInput()
        {
            Assert.False(CasValidation.CanAddFamilyMember(CasValidation.MaxSimsPerFamily));
            Assert.True(CasValidation.CanAddFamilyMember(CasValidation.MaxSimsPerFamily - 1));
            Assert.Equal(string.Empty, CasValidation.BodyTypeCode(null));
            Assert.Equal(string.Empty, CasValidation.BodyTypeCode(""));
            Assert.NotNull(CasValidation.BodyTypeCode("no_underscore"));
        }

        // ---- over-budget buy --------------------------------------------------------------

        [Fact]
        public void BudgetGatingRejectsPurchasesOverTheFamilyBudget()
        {
            // affordable, exact, over, and the unlimited no-family sentinel
            Assert.True(BuildBudget.CanAfford(500, 1000));
            Assert.True(BuildBudget.CanAfford(1000, 1000));
            Assert.False(BuildBudget.CanAfford(1001, 1000));
            Assert.True(BuildBudget.CanAfford(int.MaxValue, int.MaxValue));

            Assert.Equal(uint.MaxValue, BuildBudget.TsoBudgetFor(null)); // no family => unlimited
            Assert.Equal(0u, BuildBudget.TsoBudgetFor(0));
            Assert.Equal(0u, BuildBudget.TsoBudgetFor(-100));
        }

        // ---- family delete / eviction -----------------------------------------------------

        [Fact]
        public void DeletingAFamilyMergesGuidsWithoutDuplicatesOrLeaks()
        {
            var merged = CasEdit.MergeFamilyGuids(new uint[] { 1, 2, 3 }, new uint[] { 4, 2 }, new uint[] { 1 });
            Assert.Equal(new uint[] { 2, 3, 4 }, merged);
        }

        [Fact]
        public void OnlyTheFamilysOwnHomeLotIsEverOverwritten()
        {
            // travel lots must never be written back over their shipped template.
            Assert.True(LotPersistence.IsPersistableLot(10, 10));
            Assert.False(LotPersistence.IsPersistableLot(21, 10)); // Downtown
            Assert.False(LotPersistence.IsPersistableLot(45, 10)); // Vacation
            Assert.False(LotPersistence.IsPersistableLot(10, null));
        }
    }
}
