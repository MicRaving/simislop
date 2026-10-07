using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Engine.Utils;
using FSO.SimAntics.Model;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMMemory.GetEntityDefinitionVar"/> on the object version word - the low and
    /// high halves of a 32-bit version, which have to reassemble.
    ///
    /// <para>
    /// The bug was a modulo where a mask was meant. <c>objd.Version % 0xFFFF</c> is not the low 16
    /// bits; it is the low 16 bits <em>minus the carry out of bit 15</em>, because 65535 is one
    /// less than 65536. The very next line, <c>Version2</c>, does the <c>&gt;&gt; 16</c> half of the
    /// same split correctly, so a script reading both halves got a low word that did not belong to
    /// the same version as the high word.
    /// </para>
    ///
    /// <para>
    /// The reassembly is the assertion. Reading the two halves and putting them back together has
    /// to give the version that went in, for every version, and the interesting cases are the ones
    /// where the old expression was wrong: any version with bit 15 or bit 16 set, which is most of
    /// them - <c>0x12345</c> is the worked example in the finding, and <c>% 0xFFFF</c> turns it
    /// into <c>0x22C6</c>.
    /// </para>
    /// </summary>
    public sealed class VMOBJDVersionTests
    {
        /// <summary>
        /// The two halves, put back together. This is the property a tuning tool, a debug view or
        /// a mod asking "how old is this object" actually depends on.
        /// </summary>
        private static uint Reassembled(uint version)
        {
            var objd = new OBJD { Version = version };

            // The context argument is unused for these two variables; it is part of the signature
            // because the same switch also serves the raw-data slots, which do need it.
            var low = (ushort)VMMemory.GetEntityDefinitionVar(objd, VMOBJDVariable.Version1, null);
            var high = (ushort)VMMemory.GetEntityDefinitionVar(objd, VMOBJDVariable.Version2, null);

            return (uint)(low | (high << 16));
        }

        [Theory]
        [InlineData(0x00000000u)]
        [InlineData(0x00000001u)]
        [InlineData(0x0000FFFFu)]
        [InlineData(0x00010000u)]   // bit 16 - the carry the modulo lost
        [InlineData(0x00012345u)]   // the worked example
        [InlineData(0x0000FFFFu | 0x00010000u)]  // every bit the low half can hold, plus the carry
        [InlineData(0x00008000u)]   // bit 15 alone, the other end of the borrow
        [InlineData(0x7FFF1234u)]
        [InlineData(0xFFFFFFFFu)]
        public void TheTwoHalvesReassembleIntoTheVersionThatWentIn(uint version)
        {
            Assert.Equal(version, Reassembled(version));
        }

        [Fact]
        public void TheLowHalfIsTheLowSixteenBits()
        {
            // 0x12345 % 0xFFFF is 0x22C6, not 0x2345: the modulo takes the carry out of bit 15
            // off the bottom. This is the finding's arithmetic, made into an assertion.
            var objd = new OBJD { Version = 0x00012345 };

            Assert.Equal((short)0x2345, VMMemory.GetEntityDefinitionVar(objd, VMOBJDVariable.Version1, null));
        }

        [Fact]
        public void TheHighHalfIsTheTopSixteenBits()
        {
            // Unchanged by the fix, and the half that made the mismatch visible.
            var objd = new OBJD { Version = 0xABCD1234 };

            Assert.Equal(unchecked((short)0xABCD), VMMemory.GetEntityDefinitionVar(objd, VMOBJDVariable.Version2, null));
        }

        [Fact]
        public void AHighBitInTheLowWordSurvives()
        {
            // The borrow only shows up when the low word's top bit is set - 0xFFFF, 0x8000 and up.
            // Every one of these is a version the old expression got wrong, and all of them are
            // ordinary shipping values.
            foreach (var version in new uint[] { 0x0000FFFF, 0x00008000, 0x0000C000, 0x0000FFFF })
            {
                var objd = new OBJD { Version = version };
                Assert.Equal((short)(version & 0xFFFF),
                    VMMemory.GetEntityDefinitionVar(objd, VMOBJDVariable.Version1, null));
            }
        }

        [Fact]
        public void TheModuloIsNotTheMaskForAnyVersionWithABitSixteenOrAboveSet()
        {
            // Stated as the general fact, so the size of the bug is not a matter of opinion.
            // `v % 0xFFFF` folds the high half back in as if 0xFFFF were 0x10000, so it is off by
            // the high half for every version at or above 0x10000 - which is every version the
            // game has ever written. Below that the two agree, except at 0xFFFF itself.
            for (uint version = 0; version < 0x20000; version++)
            {
                var masked = version & 0xFFFF;
                var modulo = version % 0xFFFF;

                if (version >= 0x10000) Assert.NotEqual(masked, modulo);
                else if (version != 0xFFFF) Assert.Equal(masked, modulo);
                else Assert.NotEqual(masked, modulo);
            }
        }

        [Fact]
        public void TheWorkedExampleIsOffByExactlyTheCarry()
        {
            // 0x12345 % 0xFFFF is 0x2346, not 0x2345: the carry out of bit 15 comes off the
            // bottom. Every version at or above 0x10000 is wrong by its own high half.
            Assert.Equal(0x2346u, 0x12345u % 0xFFFFu);
            Assert.Equal(0x2345u, 0x12345u & 0xFFFFu);
        }
    }
}
