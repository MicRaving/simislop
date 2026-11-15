using FSO.Common;
using FSO.Content.TS1;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Marshals;
using Simitone.Client.UI.Services;
using Simitone.Client.Utils;
using Simitone.Tests.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// What a damaged lot save has to do at the call site, end to end.
    ///
    /// <para>
    /// The bounds added to <c>VMMarshal.Deserialize</c>'s counts are worth nothing on their own.
    /// They are worth something because of what they do to this path: a travel slot whose lot
    /// data is truncated or bit-rotted must come back as <c>false</c> with a line in the log, so
    /// the game loads the shipped template and the player keeps a way back. Before, a count of
    /// a few hundred thousand meant <c>new VMEntityMarshal[n]</c> followed by a walk off the end
    /// of the data, and a count near <see cref="int.MaxValue"/> meant a two-gigabyte allocation -
    /// so the player saw a crash from inside content load instead of a lot they could still play.
    /// </para>
    ///
    /// <para>
    /// The slot is built with the real <see cref="TravelSave"/> framing and written with the
    /// real <c>AtomicFileCommit</c>, so the file is exactly the shape the loader meets, and the
    /// payload inside it is a real <see cref="VMMarshal"/> stream with one count overwritten. No
    /// VM, no world, no graphics device, no UI, no TS1 install.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class DamagedTravelSlotTests : IDisposable
    {
        /// <summary>A lot the family does not live on, so it is a travel lot with a slot.</summary>
        private const int TravelLot = 42;

        /// <summary>The lot the active family lives on, which the slot records.</summary>
        private const int FamilyHouse = 21;

        private readonly string _tempRoot;
        private readonly string _originalUserDir;

        public DamagedTravelSlotTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-damaged-slot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            _originalUserDir = FSOEnvironment.UserDir;
            FSOEnvironment.UserDir = _tempRoot;
        }

        public void Dispose()
        {
            FSOEnvironment.UserDir = _originalUserDir;
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

        /// <summary>
        /// Every way of damaging one of the four in-body counts, each of which used to be a
        /// crash rather than a declined slot.
        /// </summary>
        public static IEnumerable<object[]> DamagedCounts()
        {
            var fields = new Dictionary<string, string>
            {
                //the test's short name, and the name VMMarshal's own message uses
                { "entity", "entity count" },
                { "thread", "thread count" },
                { "group", "multitile group count" },
                { "global", "global count" },
            };
            foreach (var value in new[] { -1, int.MinValue, int.MaxValue, 100_000_000, 50_000 })
            {
                foreach (var pair in fields)
                {
                    yield return new object[] { pair.Value, value };
                }
            }
        }

        [Theory]
        [MemberData(nameof(DamagedCounts))]
        public void ADamagedTravelSlotIsDeclinedRatherThanLoaded(string messageField, int value)
        {
            var bytes = VMMarshalFixture.Build(out var offsets);
            WriteSlot(VMMarshalFixture.WithCount(bytes, offsets[messageField], value));

            VMMarshal restored;
            var loaded = TryRestore(out restored);

            Assert.False(loaded, "a slot whose " + messageField + " is " + value + " is not a slot to load");
            Assert.Null(restored);
            //nothing came out of the loader, and the file is still on disk for the player to
            //recover by hand
            Assert.True(File.Exists(TravelSave.SlotPathFor(_tempRoot, TravelLot)));
        }

        /// <summary>
        /// The same for the compressed length, which is a <c>ReadBytes</c> rather than an array
        /// and the one that could ask for two gigabytes.
        /// </summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        [InlineData(100_000_000)]
        public void ADamagedTravelSlotWithABadCompressedLengthIsDeclined(int value)
        {
            var compressed = VMMarshalFixture.BuildCompressed(out var lengthOffset);
            WriteSlot(VMMarshalFixture.WithCount(compressed, lengthOffset, value));

            VMMarshal restored;
            Assert.False(TryRestore(out restored));
            Assert.Null(restored);
        }

        /// <summary>
        /// A slot that is intact still loads. Without this, "it always returns false" would pass
        /// every test above.
        /// </summary>
        [Fact]
        public void AnIntactTravelSlotIsStillOfferedForLoading()
        {
            WriteSlot(VMMarshalFixture.Build(out _));

            VMMarshal restored;
            Assert.True(TryRestore(out restored), "an intact slot must still be offered");
            Assert.NotNull(restored);
            Assert.Equal(3, restored.Entities.Length);
        }

        #region the loader, as the game calls it

        private bool TryRestore(out VMMarshal marshal)
        {
            //the real service, with no dialogs: the load path never touches one, and a null
            //here is the only way to reach it without a UI
            var service = new SaveService(null);
            var family = new FAMI
            {
                ChunkID = 1,
                ChunkType = "FAMI",
                ChunkLabel = "Family",
                HouseNumber = FamilyHouse,
            };

            return service.TryRestoreTravelSlot(
                "House" + TravelLot.ToString().PadLeft(2, '0') + ".iff",
                TravelLot, family, out marshal);
        }

        private void WriteSlot(byte[] marshalBytes)
        {
            AtomicFileCommit.Write(
                TravelSave.SlotPathFor(_tempRoot, TravelLot),
                TravelSave.BuildSlot(FamilyHouse, marshalBytes));
        }

        #endregion
    }
}
