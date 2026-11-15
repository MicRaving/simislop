using System.Collections.Generic;
using System.IO;
using System.Text;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals.Hollow;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The load-side guards of <c>VM.HollowLoad</c> that can be tested without a content manager:
    /// the bad-magic refusal and the skip decision for a missing definition / duplicate object id.
    /// </summary>
    public sealed class HollowLoadHardeningTests
    {
        [Fact]
        public void AStreamWithoutTheFSOhMagicIsRefusedWithAnExplanation()
        {
            using (var reader = new BinaryReader(new MemoryStream(Encoding.ASCII.GetBytes("NOPE\0\0\0\0\0"))))
            {
                var thrown = Assert.Throws<InvalidDataException>(() => new VMHollowMarshal().Deserialize(reader));
                Assert.Contains("FSOh", thrown.Message);
                Assert.Contains("NOPE", thrown.Message);
            }
        }

        [Fact]
        public void AnEntityWhoseGuidHasNoDefinitionIsSkipped()
        {
            var reason = VM.HollowEntitySkipReason(5, 0xABCD1234u, false, new Dictionary<short, VMEntity>());
            Assert.NotNull(reason);
            Assert.Contains("ABCD1234", reason);
        }

        [Fact]
        public void ADuplicateObjectIdIsSkippedAndAFreshOneIsLoaded()
        {
            var ids = new Dictionary<short, VMEntity> { { 7, null } };
            Assert.Contains("duplicate", VM.HollowEntitySkipReason(7, 1u, true, ids));
            Assert.Null(VM.HollowEntitySkipReason(8, 1u, true, ids));
            Assert.Null(VM.HollowEntitySkipReason(8, 1u, true, null));
        }
    }
}
