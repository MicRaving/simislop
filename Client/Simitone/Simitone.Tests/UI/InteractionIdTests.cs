using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using Simitone.Client.Utils;
using System;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// <see cref="InteractionId"/> - recovering the interaction number a pie-menu entry names, after
    /// the pie menu has flattened a 32-bit <c>TTAB</c> index into the 8-bit
    /// <c>VMPieMenuInteraction.ID</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "Cooked dinner cannot be clicked and interacted with; the eat action is
    /// completely broken" regression, pinned at the one decision that carries it. The chain is:
    /// <c>TTABInteraction.TTAIndex</c> (uint) is written into <c>VMPieMenuInteraction.ID</c> (byte),
    /// the pie menu copies that byte into its own item, and the click sends it back as
    /// <c>VMNetInteractionCmd.Interaction</c> (ushort) - where the engine resolves it with
    /// <c>VMEntity.GetAction</c>, which indexes <c>InteractionByIndex</c> by <c>(uint)interaction</c>.
    /// Everything downstream of the pie menu is wider than the pie menu's own field, so an
    /// interaction numbered 256 or above is aliased onto the one 256 below it before it is ever
    /// looked up, and a click on "Eat" queues a different interaction or none at all.
    /// </para>
    /// <para>
    /// The tests below assert the property that matters - the number the client sends resolves back
    /// to the entry the player clicked - rather than the shape of any one call, so they hold
    /// whichever side the repair is made on.
    /// </para>
    /// </remarks>
    public sealed class InteractionIdTests
    {
        /// <summary>
        /// A pie entry as the engine builds it: the row from the tree table, and the 8-bit copy of
        /// its index the pie menu stored alongside. The second argument is deliberately the
        /// truncated value, because that is the defect being reproduced - if the helper ever starts
        /// reading the byte, these fail.
        /// </summary>
        private static VMPieMenuInteraction Entry(uint ttaIndex, string name = "Eat")
        {
            return new VMPieMenuInteraction
            {
                Name = name,
                Entry = new TTABInteraction { TTAIndex = ttaIndex },
                ID = (byte)ttaIndex,   // what VMEntity.GetPieMenu does today
            };
        }

        [Fact]
        public void AnInteractionNumberedAbove255SurvivesThePieMenu()
        {
            // The case that broke. 300 stored in a byte is 44, so a client that sent the pie's own
            // ID asked for interaction 44 - whatever that is - and never for 300.
            var eat = Entry(300);

            Assert.Equal(300, InteractionId.Resolve(eat));
            Assert.Equal(44, eat.ID);            // the byte the pie stored - the lossy value
            Assert.False(InteractionId.SurvivesByteField(300));
            Assert.True(InteractionId.WasTruncated(eat));
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(44u)]
        [InlineData(255u)]
        [InlineData(256u)]
        [InlineData(300u)]
        [InlineData(4095u)]
        [InlineData(65535u)]
        public void TheNumberSentResolvesBackToTheEntryThePlayerClicked(uint ttaIndex)
        {
            // Stated as a round trip through a real dictionary rather than as a comparison, because
            // the round trip is the thing that is broken: the engine's own lookup keys
            // InteractionByIndex by the full uint, so "did the right number arrive" is the only
            // question, and it is answerable without a VM, a lot or a graphics device.
            var eat = Entry(ttaIndex);
            var table = new System.Collections.Generic.Dictionary<uint, TTABInteraction>
            {
                { ttaIndex, eat.Entry },
            };

            var sent = InteractionId.Resolve(eat);

            Assert.True(table.ContainsKey((uint)sent),
                $"interaction {ttaIndex} was sent as {sent}, which names a different entry");
            Assert.Equal(ttaIndex, table[(uint)sent].TTAIndex);
        }

        [Fact]
        public void TwoHighNumberedInteractionsStayDistinct()
        {
            // The aliasing is not just "off by a bit", it is a collision: every interaction from
            // 256 up maps onto the one 256 below it. Two entries of a counter's own table - the
            // shape of a food object's pie - must therefore still be told apart after the trip.
            var eat = Entry(300, "Eat");
            var serve = Entry(301, "Serve");

            var sentEat = InteractionId.Resolve(eat);
            var sentServe = InteractionId.Resolve(serve);

            Assert.NotEqual(sentEat, sentServe);
            Assert.Equal(300, sentEat);
            Assert.Equal(301, sentServe);
        }

        [Fact]
        public void ALowInteractionIsUnchangedByTheRepair()
        {
            // The overwhelmingly common case. Resolving from the table row rather than the byte must
            // be a no-op for anything that already fit, or every working interaction changes.
            for (uint index = 0; index <= byte.MaxValue; index++)
            {
                var option = Entry(index);

                Assert.Equal((int)index, InteractionId.Resolve(option));
                Assert.False(InteractionId.WasTruncated(option));
                Assert.True(InteractionId.SurvivesByteField(index));
            }
        }

        [Fact]
        public void AnEntryWithNoTableRowFallsBackToItsByte()
        {
            // Autonomy's motive-ad placeholder entry (VMThread.EvaluateCheck) carries no
            // TTABInteraction, and a hand-built pie may not either. The byte is all there is, and
            // widening it is the right answer - it must not throw and must not answer 0.
            var orphan = new VMPieMenuInteraction { Name = "Eat", ID = 7 };

            Assert.Equal(7, InteractionId.Resolve(orphan));
            Assert.False(InteractionId.WasTruncated(orphan));
        }

        [Fact]
        public void NothingResolvedIsEverOutOfRangeForTheWire()
        {
            // The pie menu refuses anything it cannot send (UIPieMenu.PieButtonClick), so the helper
            // must never produce one. A TTA index is a uint and the command is a ushort, so an
            // index above 65535 is the case to be sure about.
            Assert.Equal(InteractionId.MaxInteraction, InteractionId.Resolve(Entry(70000)));
            Assert.Equal(0, InteractionId.Resolve(null));
        }

        [Fact]
        public void TheWidestInteractionTheClientCanNameIsTheWidestTheWireCarries()
        {
            // Both net commands declare Interaction as a ushort, and the engine looks interactions
            // up by the full uint - so 65535 is the number the whole path agrees on, and it is what
            // the range check in the pie menu is written against.
            Assert.Equal(ushort.MaxValue, InteractionId.MaxInteraction);
            Assert.Equal(ushort.MaxValue, InteractionId.Resolve(Entry(ushort.MaxValue)));
        }

        [Fact]
        public void InteractionZeroIsSendable()
        {
            // The regression behind "I cannot start and queue actions": the pie menu refused every
            // entry whose number was <= 0, but the first row of a tree table is numbered 0 and the
            // engine resolves it with InteractionByIndex[(uint)0]. A "Use" entry is exactly that
            // row, and refusing it queued nothing.
            Assert.True(InteractionId.IsSendable(0));
            Assert.True(InteractionId.IsSendable(InteractionId.Resolve(Entry(0, "Use"))));
            Assert.Equal(0, InteractionId.Resolve(Entry(0, "Use")));

            // Only a number the wire cannot carry is refused.
            Assert.False(InteractionId.IsSendable(-1));
            Assert.True(InteractionId.IsSendable(InteractionId.MaxInteraction));
            Assert.False(InteractionId.IsSendable(InteractionId.MaxInteraction + 1));
        }
    }
}
