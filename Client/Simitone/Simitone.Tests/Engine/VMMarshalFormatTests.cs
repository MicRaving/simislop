using FSO.SimAntics.Marshals;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// F-09: every array length in a serialised lot comes straight off the wire, and none of
    /// them used to be checked against anything.
    ///
    /// <para>
    /// <c>VMMarshal.Deserialize</c> is the parser for the blob inside the player's
    /// <c>House{NN}.iff</c> <c>FSOV</c> chunk and for the travel slot - the two files the whole
    /// data-integrity brief is about. Five counts come off it: the compressed length, and the
    /// entity, thread, multitile-group and global counts. Each went directly into
    /// <c>new T[n]</c> or <c>ReadBytes(n)</c>, so a truncated or bit-rotted save produced
    /// <c>OverflowException</c> for a negative value and <c>OutOfMemoryException</c> for a
    /// large one - a two-gigabyte allocation for a file of a few kilobytes - both raised from
    /// inside the parse, neither of them saying "this save is damaged", and neither caught by
    /// the caller that exists precisely to catch a damaged save and offer the backup. The
    /// player saw a crash instead of a way back.
    /// </para>
    ///
    /// <para>
    /// The fixture is a real lot serialised field by field, with the byte offset of each count
    /// recorded as it is written; a damaged variant is the same bytes with four of them
    /// overwritten. The undamaged bytes are deserialised first in every test, so a broken
    /// fixture fails loudly rather than making the guard look effective. The caller side of
    /// this - that a damaged slot is declined rather than loaded - is in
    /// <see cref="Simitone.Tests.UI.DamagedTravelSlotTests"/>.
    /// </para>
    /// </summary>
    public sealed class VMMarshalFormatTests
    {
        #region the baseline: nothing here may have become unreadable

        /// <summary>
        /// The compatibility requirement, stated first so every other test in the file is
        /// measured against it: a lot written by this build still reads. The bounds added to the
        /// counts are all far above what a real lot reaches, and the remaining-bytes test is far
        /// below it, so nothing here may change.
        /// </summary>
        [Fact]
        public void AGoodLotStillDeserialises()
        {
            var bytes = VMMarshalFixture.Build(out _);

            var marshal = VMMarshalFixture.Read(bytes);

            Assert.Equal(VMMarshal.LATEST_VERSION, marshal.Version);
            Assert.True(marshal.TS1);
            Assert.NotNull(marshal.Context);
            Assert.Equal(3, marshal.Entities.Length);
            Assert.Equal(3, marshal.Threads.Length);
            Assert.Equal(2, marshal.MultitileGroups.Length);
            Assert.Equal(38, marshal.GlobalState.Length);
        }

        /// <summary>
        /// The same for a lot at the shapes that matter: empty, one object, and a lot with far
        /// more globals than a community house uses. A count of zero is legal and must stay
        /// legal - it is what an empty lot is.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(1, 1, 1)]
        [InlineData(200, 20, 1024)]
        public void ALotOfAnyPlausibleShapeStillDeserialises(int objects, int groups, int globals)
        {
            var bytes = VMMarshalFixture.Build(out _, objects, groups, globals);

            var marshal = VMMarshalFixture.Read(bytes);

            Assert.Equal(objects, marshal.Entities.Length);
            Assert.Equal(objects, marshal.Threads.Length);
            Assert.Equal(groups, marshal.MultitileGroups.Length);
            Assert.Equal(globals, marshal.GlobalState.Length);
        }

        #endregion

        #region F-09 - a damaged count is reported, not acted on

        /// <summary>
        /// The regression, once per count field and once per way of being wrong. A negative value
        /// reached <c>new T[n]</c> and threw <c>OverflowException</c>; a huge one reached it and
        /// threw <c>OutOfMemoryException</c>; a plausible-but-too-large one - the case bit rot
        /// actually produces - allocated a large array and then walked off the end of the data,
        /// and the player saw a crash from somewhere in the middle of <c>VM.Load</c>.
        /// </summary>
        public static IEnumerable<object[]> DamagedCounts()
        {
            foreach (var field in new[] { "entity count", "thread count", "multitile group count", "global count" })
            {
                yield return new object[] { field, -1 };
                yield return new object[] { field, int.MinValue };
                yield return new object[] { field, int.MaxValue };
                yield return new object[] { field, 100_000_000 };
                //plausible, and still not a real lot: fifty thousand objects is a big one
                yield return new object[] { field, 50_000 };
            }
        }

        [Theory]
        [MemberData(nameof(DamagedCounts))]
        public void ADamagedCountIsReportedWithItsOwnName(string field, int value)
        {
            var bytes = VMMarshalFixture.Build(out var offsets);
            var damaged = VMMarshalFixture.WithCount(bytes, offsets[field], value);

            var thrown = Assert.Throws<MarshalFormatException>(() => VMMarshalFixture.Read(damaged));
            Assert.Contains(field, thrown.Message);
            Assert.Contains("damaged", thrown.Message);
        }

        /// <summary>
        /// The compressed length, which is the one of the five that is a <c>ReadBytes</c> rather
        /// than an array: a value near <see cref="int.MaxValue"/> made
        /// <c>BinaryReader.ReadBytes</c> attempt a two-gigabyte allocation for a house file of a
        /// few kilobytes. The payload behind the number in this fixture is a valid gzip stream,
        /// so the only thing wrong with these is the number.
        /// </summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        [InlineData(1_000_000_000)]
        [InlineData(4096)]        // more than the file holds, and still a small number
        public void ADamagedCompressedLengthIsReported(int value)
        {
            var compressed = VMMarshalFixture.BuildCompressed(out var lengthOffset);
            var damaged = VMMarshalFixture.WithCount(compressed, lengthOffset, value);

            var thrown = Assert.Throws<MarshalFormatException>(() => VMMarshalFixture.Read(damaged));
            Assert.Contains("compressed marshal length", thrown.Message);
        }

        /// <summary>
        /// A whole compressed save - the flag, a valid length, a valid gzip payload - still
        /// reads. The length bound must not be tight enough to refuse a real one.
        /// </summary>
        [Fact]
        public void ACompressedLotStillDeserialises()
        {
            var compressed = VMMarshalFixture.BuildCompressed(out _);

            var marshal = VMMarshalFixture.Read(compressed);

            Assert.True(marshal.Compressed);
            Assert.Equal(3, marshal.Entities.Length);
            Assert.Equal(38, marshal.GlobalState.Length);
        }

        /// <summary>
        /// A count field that is not there at all - the file cut short, which is what a full disk
        /// or an interrupted write leaves. The old code read past the end and took
        /// <c>EndOfStreamException</c> from the middle of a loop.
        /// </summary>
        [Fact]
        public void ALotThatEndsAtACountIsReportedAsTruncated()
        {
            var bytes = VMMarshalFixture.Build(out var offsets);
            //the context is complete, so the reader gets as far as the first count and finds
            //the stream already over. Cutting further in - inside the entity array - throws
            //EndOfStreamException from the entity reader instead, which is a different reader
            //and outside what the count bounds are for.
            var truncated = bytes.Take(offsets["entity count"]).ToArray();

            var thrown = Assert.Throws<MarshalFormatException>(() => VMMarshalFixture.Read(truncated));
            Assert.Contains("truncated", thrown.Message);
            Assert.Contains("entity count", thrown.Message);
        }

        /// <summary>
        /// A global count is a number of two-byte values, so the remaining-bytes test is halved
        /// for it. One past the number of shorts that could fit in what is left is the value
        /// that must still be caught.
        /// </summary>
        [Fact]
        public void AGlobalCountIsBoundedByTheBytesItWouldNeed()
        {
            var bytes = VMMarshalFixture.Build(out var offsets);
            var remainingAfter = bytes.Length - (offsets["global count"] + 4);
            var tooMany = (remainingAfter / 2) + 1;

            var damaged = VMMarshalFixture.WithCount(bytes, offsets["global count"], tooMany);
            var thrown = Assert.Throws<MarshalFormatException>(() => VMMarshalFixture.Read(damaged));
            Assert.Contains("global count", thrown.Message);
        }

        /// <summary>
        /// One past the number of entity type bytes that could fit. This is the value that looks
        /// most innocent of all the damaged ones - a few hundred, in a few hundred bytes - and it
        /// is the one a maximum alone cannot tell from a real lot. Only looking at what is left
        /// to read catches it.
        /// </summary>
        [Fact]
        public void ACountLargerThanTheDataIsRejectedEvenWhenItIsSmall()
        {
            var bytes = VMMarshalFixture.Build(out var offsets);
            var remainingAfter = bytes.Length - (offsets["entity count"] + 4);

            var damaged = VMMarshalFixture.WithCount(bytes, offsets["entity count"], remainingAfter + 1);
            var thrown = Assert.Throws<MarshalFormatException>(() => VMMarshalFixture.Read(damaged));
            Assert.Contains("only", thrown.Message);
            Assert.Contains("bytes left", thrown.Message);
        }

        #endregion

        #region a real file, not just a byte array

        /// <summary>
        /// Every caller in the game hands <c>VMMarshal.Deserialize</c> a reader over a
        /// <c>FileStream</c> - the travel slot and the CAS lot both do - so the remaining-bytes
        /// test has to work there and not only over a byte array. The bounds are the same; only
        /// the stream differs.
        /// </summary>
        [Fact]
        public void ADamagedLotOverAFileStreamIsRejectedToo()
        {
            var bytes = VMMarshalFixture.Build(out var offsets);
            var damaged = VMMarshalFixture.WithCount(bytes, offsets["entity count"], int.MaxValue);
            var path = Path.Combine(Path.GetTempPath(), "simitone-vm-" + Guid.NewGuid().ToString("N") + ".fsov");
            File.WriteAllBytes(path, damaged);
            try
            {
                using (var file = new BinaryReader(File.OpenRead(path)))
                {
                    Assert.Throws<MarshalFormatException>(() => new VMMarshal().Deserialize(file));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        #endregion
    }
}
