using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FSO.Content.TS1;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the cross-session half of travel-lot persistence: where a travel lot's save lives,
    /// who may claim it, and the header that makes a stale or foreign slot recognisable.
    ///
    /// <para>
    /// A travel lot (Downtown 21-31, Vacation 40-49, Studio 81-89, Magic 90-99) is a copy of a
    /// template shipped with the game, so <see cref="LotPersistence"/> refuses to write it back to
    /// its own <c>House{NN}.iff</c>. Without somewhere else to put the lot, that refusal is the
    /// long-standing "cannot save on vacation" behaviour; the slot is what makes saving there
    /// work. The decisions are path arithmetic, an integer comparison and a byte layout, so all of
    /// them are pinned here without a VM, a graphics device, Content, audio or a TS1 install.
    /// </para>
    ///
    /// <para>
    /// Two failure modes are data-destroying if they regress, and both are tested explicitly:
    /// writing over a shipped template (a slot path that ever collides with a house path), and
    /// handing one family's saved vacation to another family (a slot loaded without the family
    /// check). The file-level tests commit real bytes through <see cref="AtomicFileCommit"/>
    /// into a throwaway temp directory, so the on-disk round trip is exercised rather than just
    /// the two functions in isolation.
    /// </para>
    /// </summary>
    public sealed class TravelSaveTests : IDisposable
    {
        private readonly string _tempRoot;

        public TravelSaveTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-travel-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
            }
            catch (IOException)
            {
                //best effort - a leftover temp dir must never fail the test run
            }
            catch (UnauthorizedAccessException)
            {
                //same: a locked handle on Windows must not turn into a test failure
            }
        }

        /// <summary>A stand-in for a serialised VMMarshal blob: opaque, recognisable bytes.</summary>
        private static byte[] MarshalBytes(byte fill) => Enumerable.Repeat(fill, 64).ToArray();

        #region 1. slot path naming

        [Theory]
        [InlineData(1, "House01.fsov")]
        [InlineData(5, "House05.fsov")]
        [InlineData(20, "House20.fsov")]
        [InlineData(26, "House26.fsov")]
        [InlineData(42, "House42.fsov")]
        [InlineData(85, "House85.fsov")]
        [InlineData(92, "House92.fsov")]
        [InlineData(99, "House99.fsov")]
        public void ASlotLivesInTheTravelLotsFolderUnderTheUserDirectory(int houseId, string fileName)
        {
            var path = TravelSave.SlotPathFor("C:/Users/Test/Documents/Simitone", houseId);

            Assert.Equal("C:/Users/Test/Documents/Simitone/TravelLots/" + fileName, path);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(99)]
        [InlineData(100)]
        public void SingleDigitLotNumbersArePaddedToTwoJustLikeTheHouseFiles(int houseId)
        {
            //The claim this test makes is that the travel slot and the house file it shadows are
            //the *same lot's* two save shapes, so their names have to agree. That is a claim
            //about two independent pieces of production code, and it can only be checked by
            //asking both of them.
            //
            //It used to build the expected value with `houseId.ToString().PadLeft(2, '0')` -
            //the same expression TravelSave.SlotPathFor uses - and compare that expression with
            //itself. It could not fail, and it could not have detected the divergence it names:
            //TravelSave.SlotPathFor formats with CultureInfo.InvariantCulture and
            //TS1NeighborhoodProvider.HousePathFor used the culture-sensitive ToString, so the two
            //disagreed in any locale that spells its digits differently and the test said
            //nothing. `expected.Substring(0, 7)` against `Path.GetFileName(slot).Substring(0, 7)`
            //was the same claim restated.
            //
            //Both sides are now asked directly. If either one changes how it pads - or which
            //culture it pads in - this fails.
            var slot = TravelSave.SlotPathFor("C:/Users/Test/Documents/Simitone", houseId);
            var house = TS1NeighborhoodProvider.HousePathFor("C:/Users/Test/Documents/Simitone", houseId);

            Assert.Equal(Path.GetFileNameWithoutExtension(house), Path.GetFileNameWithoutExtension(slot));
            var stem = Path.GetFileNameWithoutExtension(slot);
            Assert.StartsWith("House", stem);
            //and the whole point of the padding: a one-digit lot is still two characters of
            //number, so the two files can never end up one digit apart in name space
            Assert.Equal(Math.Max(2, houseId.ToString(CultureInfo.InvariantCulture).Length), stem.Length - 5);
            Assert.Equal(".iff", Path.GetExtension(house));
            Assert.Equal(".fsov", Path.GetExtension(slot));
        }

        [Theory]
        [InlineData("de-DE")]
        [InlineData("ar-SA")]
        [InlineData("fa-IR")]
        [InlineData("th-TH")]
        [InlineData("hi-IN")]
        public void TheHouseNameAndTheSlotNameAreIdenticalInEveryCulture(string cultureName)
        {
            //The regression the previous version of the test above could not see. .NET formats an
            //integer with ASCII digits in every culture, so today this passes - and the point is
            //that it will *keep* passing, because both halves now say InvariantCulture rather
            //than one of them saying ToString(). A culture that substituted digits would give one
            //lot two file names and silently split its state across both.
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);
                foreach (var houseId in new[] { 1, 7, 10, 42, 100 })
                {
                    var slot = TravelSave.SlotPathFor("Simitone", houseId);
                    var house = TS1NeighborhoodProvider.HousePathFor("Simitone", houseId);
                    Assert.Equal(Path.GetFileNameWithoutExtension(house),
                        Path.GetFileNameWithoutExtension(slot));
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void ATrailingSeparatorOnTheUserDirectoryDoesNotDoubleUp()
        {
            //FSOEnvironment.UserDir is set to ".../Simitone/" with a trailing slash, so this is
            //the form the game actually passes in.
            var withSlash = TravelSave.SlotPathFor("C:/Users/Test/Documents/Simitone/", 42);
            var withoutSlash = TravelSave.SlotPathFor("C:/Users/Test/Documents/Simitone", 42);

            Assert.Equal(withoutSlash, withSlash);
            Assert.DoesNotContain("//", withSlash.Replace("C:/", "C:"));
        }

        [Fact]
        public void BackslashesInTheUserDirectoryAreNormalisedToForwardSlashes()
        {
            var path = TravelSave.SlotPathFor("C:\\Users\\Test\\Documents\\Simitone", 42);

            Assert.Equal("C:/Users/Test/Documents/Simitone/TravelLots/House42.fsov", path);
            Assert.DoesNotContain("\\", path);
        }

        [Fact]
        public void NoUserDirectoryMeansNoSlotPath()
        {
            //returning null lets the caller skip the slot save and carry on, instead of throwing
            //on a malformed string from a half-initialised environment
            Assert.Null(TravelSave.SlotPathFor(null, 42));
            Assert.Null(TravelSave.SlotPathFor("", 42));
        }

        [Fact]
        public void ASlotPathIsNeverAHousePath()
        {
            //the whole point of the slot is that nothing that walks the user's house files can
            //find it: it is not an IFF, it is not in the Houses folder, and the lot loader's
            //House{NN}.iff naming cannot collide with it.
            for (int house = 1; house <= 100; house++)
            {
                var slot = TravelSave.SlotPathFor("Simitone", house);
                var file = Path.GetFileName(slot);

                Assert.EndsWith(".fsov", file);
                Assert.DoesNotContain("Houses", slot);
                Assert.NotEqual("House" + house.ToString().PadLeft(2, '0') + ".iff", file);

                //AtomicFileCommit's side files must not be loadable as slots either
                Assert.NotEqual("House" + house.ToString().PadLeft(2, '0') + ".iff.bak", file + ".bak");
                Assert.NotEqual("House" + house.ToString().PadLeft(2, '0') + ".iff.tmp", file + ".tmp");
            }
        }

        [Fact]
        public void EveryTravelLotGetsItsOwnSlot()
        {
            var paths = Enumerable.Range(1, 100)
                .Select(h => TravelSave.SlotPathFor("Simitone", h))
                .ToArray();

            Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        #endregion

        #region 2. who may claim a slot

        [Theory]
        [InlineData(21)]
        [InlineData(26)]
        [InlineData(42)]
        [InlineData(99)]
        public void ASlotIsRestoredForTheFamilyThatSavedIt(int family)
        {
            Assert.True(TravelSave.ShouldRestoreSlot(family, family));
        }

        [Theory]
        [InlineData(1, 2)]
        [InlineData(26, 21)]
        [InlineData(21, 26)]
        public void ASlotIsNeverRestoredIntoAnotherFamily(int slotFamily, int activeFamily)
        {
            //one slot per lot, many families visit it. loading family A's vacation into family B
            //would hand B A's Sims, A's budget and A's relationship state.
            Assert.False(TravelSave.ShouldRestoreSlot(slotFamily, activeFamily));
        }

        [Fact]
        public void ASlotIsNotRestoredWithoutAKnownFamilyOnEitherSide()
        {
            Assert.False(TravelSave.ShouldRestoreSlot(null, 21));
            Assert.False(TravelSave.ShouldRestoreSlot(21, null));
            Assert.False(TravelSave.ShouldRestoreSlot(null, null));
        }

        [Fact]
        public void TheUnknownFamilySentinelBelongsToNoFamily()
        {
            //a slot written by a session with no active family records UnknownFamily, which is
            //int.MinValue precisely so it can never match a real lot number
            for (int family = -100; family <= 100; family++)
            {
                Assert.False(TravelSave.ShouldRestoreSlot(TravelSave.UnknownFamily, family));
            }
            Assert.Equal(int.MinValue, TravelSave.UnknownFamily);
        }

        [Fact]
        public void SlotOwnershipIsTheOnlyRestoreCondition()
        {
            //the two checks are complementary rather than nested: either one alone is unsafe, so
            //both have to be stated in the same terms.
            for (int slotFamily = 1; slotFamily <= 3; slotFamily++)
            {
                for (int activeFamily = 1; activeFamily <= 3; activeFamily++)
                {
                    Assert.Equal(slotFamily == activeFamily, TravelSave.ShouldRestoreSlot(slotFamily, activeFamily));
                }
            }
        }

        #endregion

        #region 3. is this lot a travel lot

        [Fact]
        public void ATravelLotIsAnyLotThatIsNotTheFamiliesHome()
        {
            //family 7 travelling: downtown 26, vacation 42, studio 85, magic 92
            Assert.True(TravelSave.IsTravelLot(26, 7));
            Assert.True(TravelSave.IsTravelLot(42, 7));
            Assert.True(TravelSave.IsTravelLot(85, 7));
            Assert.True(TravelSave.IsTravelLot(92, 7));
            //a neighbour's house is a travel lot too, and is likewise never written
            Assert.True(TravelSave.IsTravelLot(8, 7));
            Assert.False(TravelSave.IsTravelLot(7, 7));
        }

        [Fact]
        public void NoFamilyMeansEveryLotCountsAsATravelLot()
        {
            //defensive only: IsTravelLot is a negation, so the family check has to live in
            //ShouldRestoreSlot, which refuses when either side is unknown. This test pins that
            //the two cannot drift apart - a family-less session is a read-only lot preview.
            Assert.True(TravelSave.IsTravelLot(26, null));
            Assert.True(TravelSave.IsTravelLot(0, null));
            Assert.False(TravelSave.ShouldRestoreSlot(26, null));
        }

        [Fact]
        public void IsTravelLotIsExactlyTheOppositeOfTheWriteGuard()
        {
            for (int lot = 1; lot <= 100; lot++)
            {
                foreach (var family in new int?[] { null, 0, 1, 7, 26, 42, 99, 100 })
                {
                    Assert.Equal(!LotPersistence.IsPersistableLot(lot, family), TravelSave.IsTravelLot(lot, family));
                }
            }
        }

        #endregion

        #region 4. header round trip and rejection

        [Fact]
        public void ABuiltSlotStartsWithTheMagicAndTheCurrentVersion()
        {
            var slot = TravelSave.BuildSlot(21, MarshalBytes(0x5A));

            Assert.Equal(TravelSave.HeaderLength + 64, slot.Length);
            Assert.Equal(TravelSave.Magic, Encoding.ASCII.GetString(slot, 0, 4));
            Assert.Equal(4, TravelSave.Magic.Length);
            Assert.Equal(TravelSave.FormatVersion, BitConverter.ToInt32(slot, 4));
            Assert.Equal(12, TravelSave.HeaderLength);
        }

        [Fact]
        public void ABuiltSlotsHeaderReadsBackTheFamilyThatSavedIt()
        {
            var slot = TravelSave.BuildSlot(42, MarshalBytes(0x11));

            int version, family;
            Assert.True(TravelSave.IsValidHeader(slot, out version, out family));

            Assert.Equal(TravelSave.FormatVersion, version);
            Assert.Equal(42, family);
            Assert.True(TravelSave.ShouldRestoreSlot(family, 42));
        }

        [Fact]
        public void ABuiltSlotsHeaderIsWrittenLittleEndianLikeTheMarshalItWraps()
        {
            //VMMarshal.SerializeInto uses a default BinaryWriter, so the whole file has to be one
            //byte order or the marshal could not be read straight back at the fixed offset
            var slot = TravelSave.BuildSlot(42, MarshalBytes(0x22));

            //version 1 == 0x00000001, low byte first
            Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, slot.Skip(4).Take(4).ToArray());
            //family 42 == 0x0000002A, low byte first
            Assert.Equal(new byte[] { 0x2A, 0x00, 0x00, 0x00 }, slot.Skip(8).Take(4).ToArray());

            //and both halves of that agree with what a BinaryReader would hand back
            int version, family;
            Assert.True(TravelSave.IsValidHeader(slot, out version, out family));
            Assert.Equal(1, version);
            Assert.Equal(42, family);
        }

        [Fact]
        public void TheMarshalPayloadSitsAtTheFixedHeaderOffsetUntouched()
        {
            var marshal = MarshalBytes(0x7B);
            var slot = TravelSave.BuildSlot(21, marshal);

            var payload = slot.Skip(TravelSave.HeaderLength).ToArray();
            Assert.Equal(marshal, payload);
        }

        [Fact]
        public void AnEmptyMarshalPayloadIsStillAValidSlotHeader()
        {
            //AtomicFileCommit accepts a zero-length payload, so a header-only file has to be
            //readable as a header - and then rejected by VMMarshal.Deserialize, which is the
            //correct place for that failure.
            var slot = TravelSave.BuildSlot(21, new byte[0]);

            Assert.Equal(TravelSave.HeaderLength, slot.Length);
            int version, family;
            Assert.True(TravelSave.IsValidHeader(slot, out version, out family));
            Assert.Equal(21, family);
        }

        [Fact]
        public void ASlotSavedWithNoActiveFamilyRecordsTheSentinel()
        {
            var slot = TravelSave.BuildSlot(null, MarshalBytes(0x33));

            int version, family;
            Assert.True(TravelSave.IsValidHeader(slot, out version, out family));
            Assert.Equal(TravelSave.UnknownFamily, family);
            Assert.False(TravelSave.ShouldRestoreSlot(family, 21));
            Assert.False(TravelSave.ShouldRestoreSlot(family, null));
        }

        [Fact]
        public void AFileWithTheWrongMagicIsNotASlot()
        {
            //a slot is a bare blob with no IFF wrapper, so the magic is the only thing separating
            //a real slot from any other binary that lands in the folder
            foreach (var wrong in new[] { "SLTU", "sltt", "FSOv", "\0\0\0\0" })
            {
                var slot = TravelSave.BuildSlot(21, MarshalBytes(0x44));
                for (int i = 0; i < 4; i++) slot[i] = (byte)wrong[i];

                int version, family;
                Assert.False(TravelSave.IsValidHeader(slot, out version, out family));
            }
        }

        [Fact]
        public void AFileWithTheWrongVersionIsNotASlot()
        {
            //a slot from a build with a different payload layout must be ignored rather than fed
            //to a VMMarshal that may not understand it
            foreach (var version in new[] { 0, -1, 2, 99, int.MaxValue, int.MinValue })
            {
                var slot = TravelSave.BuildSlot(21, MarshalBytes(0x55));
                var bytes = BitConverter.GetBytes(version);
                Array.Copy(bytes, 0, slot, 4, 4);

                int readVersion, family;
                Assert.False(TravelSave.IsValidHeader(slot, out readVersion, out family));
            }
        }

        [Fact]
        public void AFileTooShortToHoldAHeaderIsNotASlot()
        {
            var full = TravelSave.BuildSlot(21, MarshalBytes(0x66));

            for (int length = 0; length < TravelSave.HeaderLength; length++)
            {
                int version, family;
                Assert.False(TravelSave.IsValidHeader(full.Take(length).ToArray(), out version, out family));
            }
        }

        [Fact]
        public void NoDataIsNotASlot()
        {
            int version, family;
            Assert.False(TravelSave.IsValidHeader(null, out version, out family));
            Assert.False(TravelSave.IsValidHeader(new byte[0], out version, out family));
        }

        [Fact]
        public void AFailedHeaderReadReportsSafeDefaults()
        {
            //callers log the family out of the header when a slot is rejected, so a failed read
            //must not hand them a number that could be mistaken for a real owner
            int version, family;
            Assert.False(TravelSave.IsValidHeader(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, out version, out family));
            Assert.Equal(0, version);
            Assert.Equal(TravelSave.UnknownFamily, family);
        }

        [Fact]
        public void ASlotCannotBeBuiltWithoutAMarshal()
        {
            Assert.Throws<ArgumentNullException>(() => TravelSave.BuildSlot(21, null));
        }

        #endregion

        #region 5. the marshal preamble the loader must check before deserialising

        /// <summary>
        /// A real <c>VMMarshal.SerializeInto</c> stream begins with these four bytes, and
        /// <c>Deserialize</c> refuses - silently - to read anything without them. This is the
        /// shape <c>TS1GameScreen.TryRestoreTravelSlot</c> requires before it builds a marshal.
        /// </summary>
        private static byte[] MarshalWithPreamble(byte fill)
        {
            var bytes = MarshalBytes(fill);
            for (int i = 0; i < 4; i++) bytes[i] = (byte)TravelSave.MarshalMagic[i];
            return bytes;
        }

        [Fact]
        public void ASlotBuiltFromARealMarshalCarriesTheMarshalPreambleAtTheHeaderOffset()
        {
            var slot = TravelSave.BuildSlot(21, MarshalWithPreamble(0x9A));

            Assert.True(TravelSave.HasMarshalPreamble(slot, TravelSave.HeaderLength));
            Assert.Equal(TravelSave.MarshalMagic, Encoding.ASCII.GetString(slot, TravelSave.HeaderLength, 4));
        }

        [Fact]
        public void ASlotWithoutTheMarshalPreambleIsRecognisedAsUnusable()
        {
            //the silent-return trap: VMMarshal.Deserialize hands back a hollow marshal instead of
            //throwing, so the payload has to be rejected at the byte level before it is read
            foreach (var wrong in new[] { "FSOu", "fsOv", "FSOV", "\0\0\0\0" })
            {
                var payload = MarshalWithPreamble(0x9B);
                for (int i = 0; i < 4; i++) payload[i] = (byte)wrong[i];
                var slot = TravelSave.BuildSlot(21, payload);

                //the slot header is perfectly valid - it is the marshal inside that is not
                int version, family;
                Assert.True(TravelSave.IsValidHeader(slot, out version, out family));
                Assert.False(TravelSave.HasMarshalPreamble(slot, TravelSave.HeaderLength));
            }
        }

        [Fact]
        public void AnEmptyPayloadHasNoMarshalPreamble()
        {
            var slot = TravelSave.BuildSlot(21, new byte[0]);

            int version, family;
            Assert.True(TravelSave.IsValidHeader(slot, out version, out family));
            Assert.False(TravelSave.HasMarshalPreamble(slot, TravelSave.HeaderLength));
        }

        [Fact]
        public void ATruncatedPayloadCannotBeReadAsAMarshal()
        {
            for (int payloadLength = 0; payloadLength < 4; payloadLength++)
            {
                var slot = TravelSave.BuildSlot(21, MarshalWithPreamble(0x9C).Take(payloadLength).ToArray());
                Assert.False(TravelSave.HasMarshalPreamble(slot, TravelSave.HeaderLength));
            }
        }

        [Fact]
        public void ThePreambleCheckRejectsOffsetsWithNoRoomForIt()
        {
            var slot = TravelSave.BuildSlot(21, MarshalWithPreamble(0x9D));

            //offset 0 is the slot's own header, which never carries the marshal magic
            Assert.False(TravelSave.HasMarshalPreamble(slot, 0));
            Assert.False(TravelSave.HasMarshalPreamble(slot, -1));
            Assert.False(TravelSave.HasMarshalPreamble(slot, slot.Length));
            Assert.False(TravelSave.HasMarshalPreamble(slot, slot.Length - 3));
            Assert.False(TravelSave.HasMarshalPreamble(null, TravelSave.HeaderLength));
        }

        #endregion

        #region 6. the on-disk round trip Save and the loader perform

        [Fact]
        public void ASlotSurvivesTheSameAtomicCommitTheHomeLotSaveUses()
        {
            //the exact shape of WriteTravelSlot -> TryRestoreTravelSlot: build, commit atomically,
            //read the whole file back, validate the header, then hand the payload on at the
            //fixed offset. Only the VMMarshal deserialisation itself needs a VM, so it stops there.
            var userDir = _tempRoot;
            var path = TravelSave.SlotPathFor(userDir, 42);
            var first = TravelSave.BuildSlot(42, MarshalWithPreamble(0x01));
            var second = TravelSave.BuildSlot(42, MarshalWithPreamble(0x02));

            var firstBackup = AtomicFileCommit.Write(path, first);
            Assert.Null(firstBackup);
            Assert.True(Directory.Exists(Path.GetDirectoryName(path)), "the TravelLots folder must be created on demand");
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            var backup = AtomicFileCommit.Write(path, second);
            Assert.Equal(path + ".bak", backup);

            var read = File.ReadAllBytes(path);
            int version, family;
            Assert.True(TravelSave.IsValidHeader(read, out version, out family));
            Assert.Equal(TravelSave.FormatVersion, version);
            Assert.True(TravelSave.ShouldRestoreSlot(family, 42));
            Assert.True(TravelSave.HasMarshalPreamble(read, TravelSave.HeaderLength),
                "a committed slot must survive the round trip as a loadable marshal");
            Assert.Equal(MarshalWithPreamble(0x02), read.Skip(TravelSave.HeaderLength).ToArray());

            //and the previous slot is still recoverable by hand
            Assert.Equal(first, File.ReadAllBytes(path + ".bak"));
        }

        [Fact]
        public void TwoFamiliesSharingATravelLotOverwriteOneSlotAndTheHeaderDecidesWhichIsUsable()
        {
            //the real multi-family shape of a vacation lot: one slot per lot, so the second
            //family's save wins the file, and the first family is then correctly refused it.
            var path = TravelSave.SlotPathFor(_tempRoot, 42);

            AtomicFileCommit.Write(path, TravelSave.BuildSlot(21, MarshalWithPreamble(0xAA)));
            AtomicFileCommit.Write(path, TravelSave.BuildSlot(26, MarshalWithPreamble(0xBB)));

            var read = File.ReadAllBytes(path);
            int version, family;
            Assert.True(TravelSave.IsValidHeader(read, out version, out family));
            Assert.Equal(26, family);

            Assert.False(TravelSave.ShouldRestoreSlot(family, 21));
            Assert.True(TravelSave.ShouldRestoreSlot(family, 26));
            Assert.Equal(MarshalWithPreamble(0xBB), read.Skip(TravelSave.HeaderLength).ToArray());
        }

        [Fact]
        public void AnEmptyUserDirectoryLeavesNoSlotOnDiskToBeMisread()
        {
            //SlotPathFor is the only thing standing between a missing user directory and a save
            //into the install folder, so a null path must mean "write nothing"
            Assert.Null(TravelSave.SlotPathFor(null, 42));
            Assert.Empty(Directory.GetFileSystemEntries(_tempRoot));
        }

        #endregion

        #region 7. recovering a lot number from a path

        [Fact]
        public void AHousePathYieldsItsLotNumber()
        {
            AssertHouseId("UserData/Houses/House21.iff", 21);
            AssertHouseId("C:/Users/Test/Documents/Simitone/Houses/House26.iff", 26);
            AssertHouseId("C:/Users/Test/Documents/Simitone/Houses/House05.iff", 5);
            AssertHouseId("C:\\Users\\Test\\Houses\\House92.iff", 92);
            AssertHouseId("House42.iff", 42);
            AssertHouseId("house42.iff", 42);
            AssertHouseId("House100.iff", 100);
            AssertHouseId("House26.IFF", 26);
        }

        private static void AssertHouseId(string path, int expected)
        {
            int house;
            Assert.True(TravelSave.TryParseHouseIdFromPath(path, out house),
                "\"" + path + "\" should yield lot " + expected);
            Assert.Equal(expected, house);
        }

        [Theory]
        [InlineData("House26.iff.bak")]   // an AtomicFileCommit side file must never be a lot
        [InlineData("House26.iff.tmp")]
        [InlineData("House26.fsov.bak")]  // nor may a slot's own backup
        [InlineData("House.iff")]         // no number at all
        [InlineData("House")]
        [InlineData("Houses/House")]      // the folder, not a file
        [InlineData("Houses")]
        [InlineData("VacationBeach.iff")] // a named lot, not a numbered house
        [InlineData("House2a.iff")]
        [InlineData("House-1.iff")]
        [InlineData("House 1.iff")]
        [InlineData("House1a")]
        [InlineData("")]
        [InlineData(null)]
        public void AnythingThatIsNotAPlainNumberedHousePathIsRejected(string path)
        {
            //a wrong number here would load the wrong lot's state, so the parse is deliberately
            //strict rather than best-effort
            int house;
            Assert.False(TravelSave.TryParseHouseIdFromPath(path, out house));
            Assert.Equal(0, house);
        }

        [Fact]
        public void ARecoveredLotNumberIsTheKeyASlotWouldBeStoredUnder()
        {
            //the fallback path: when a caller does not thread the number through, the path must
            //still resolve to exactly the lot the slot file is named after.
            var userDir = "C:/Users/Test/Documents/Simitone";

            foreach (var house in new[] { 1, 5, 21, 26, 42, 85, 92, 99, 100 })
            {
                var housePath = userDir + "/Houses/House" + house.ToString().PadLeft(2, '0') + ".iff";

                int parsed;
                Assert.True(TravelSave.TryParseHouseIdFromPath(housePath, out parsed));
                Assert.Equal(house, parsed);
                Assert.EndsWith("/House" + house.ToString().PadLeft(2, '0') + ".fsov", TravelSave.SlotPathFor(userDir, parsed));
            }
        }

        #endregion
    }
}
