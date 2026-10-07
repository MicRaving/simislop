using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Covers the pure validation rules that <c>TS1CASScreen</c> now applies to user- and
    /// content-derived input.
    ///
    /// The headline case is <see cref="CasValidation.BodyTypeCode"/>: the CAS screen used to do
    /// <c>body.Substring(body.IndexOf("_") - 3, 3)</c> inline in <c>CASToNeighGen</c>, which
    /// threw <c>ArgumentOutOfRangeException</c> out of the accept-button click handler for any
    /// body string without a 3 character prefix before an underscore. The remaining members lock
    /// in the family size cap and the name sanitisation that backstops the Save button.
    /// </summary>
    public sealed class CasValidationTests
    {
        [Fact]
        public void BodyTypeCode_ReturnsThreeCharsBeforeUnderscore()
        {
            //the normal TS1 shape: the 3 char body type code sits just before the first underscore.
            Assert.Equal("mab", CasValidation.BodyTypeCode("mab_body"));
            Assert.Equal("fca", CasValidation.BodyTypeCode("fca_huao"));
        }

        [Fact]
        public void BodyTypeCode_UsesFirstUnderscore()
        {
            //only the first underscore delimits the code, matching the original IndexOf("_") call.
            Assert.Equal("mab", CasValidation.BodyTypeCode("mab_x_y_z"));
        }

        [Fact]
        public void BodyTypeCode_NoUnderscore_ReturnsFirstThreeChars()
        {
            //the crash case: IndexOf returns -1, and the original Substring(-4, 3) threw.
            Assert.Equal("abc", CasValidation.BodyTypeCode("abcdef"));
        }

        [Fact]
        public void BodyTypeCode_UnderscoreWithNoPrefix_ReturnsFirstThreeChars()
        {
            //underscore at index 0: nothing precedes it, so no code exists. Must not throw.
            Assert.Equal("_x", CasValidation.BodyTypeCode("_x"));
        }

        [Fact]
        public void BodyTypeCode_ShortPrefix_ReturnsWhatIsAvailable()
        {
            //Fewer than 3 characters before the underscore: there is no complete code, so the
            //best-effort prefix of up to 3 characters is returned (which may include the
            //underscore itself). This is the specified fallback, not the 3 chars before '_'.
            Assert.Equal("ab_", CasValidation.BodyTypeCode("ab_cd"));
            Assert.Equal("a_b", CasValidation.BodyTypeCode("a_bc"));
            //total length under 3: the whole input comes back.
            Assert.Equal("a_", CasValidation.BodyTypeCode("a_"));
            Assert.Equal("_", CasValidation.BodyTypeCode("_"));
        }

        [Fact]
        public void BodyTypeCode_ShortInputWithoutUnderscore_ReturnsWholeInput()
        {
            Assert.Equal("ab", CasValidation.BodyTypeCode("ab"));
            Assert.Equal("a", CasValidation.BodyTypeCode("a"));
        }

        [Fact]
        public void BodyTypeCode_NullOrEmpty_ReturnsEmptyString()
        {
            Assert.Equal("", CasValidation.BodyTypeCode(null));
            Assert.Equal("", CasValidation.BodyTypeCode(""));
        }

        [Fact]
        public void BodyTypeCode_MalformedInputs_NeverThrow()
        {
            //this is the regression test for the crash: a spread of malformed content strings,
            //including the exact shapes that used to throw from the click handler.
            string[] malformed = { null, "", "_", "__", "a_", "ab_", "_abc", "___", "ab", "a", "mab_body", "x_y", "mab_", "  _  " };
            foreach (var input in malformed)
            {
                var result = CasValidation.BodyTypeCode(input);
                Assert.NotNull(result);
                Assert.True(result.Length <= 3);
            }
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(7, true)]
        [InlineData(8, false)]
        [InlineData(9, false)]
        [InlineData(100, false)]
        [InlineData(-1, false)]
        public void CanAddFamilyMember_CountVersusCap_ExpectedOutcome(int currentCount, bool expected)
        {
            Assert.Equal(expected, CasValidation.CanAddFamilyMember(currentCount));
        }

        [Fact]
        public void CanAddFamilyMember_CapIsEight()
        {
            Assert.Equal(8, CasValidation.MaxSimsPerFamily);
            //the last add that succeeds is the 8th, ie. a family that already has 7.
            Assert.True(CasValidation.CanAddFamilyMember(7));
            Assert.False(CasValidation.CanAddFamilyMember(8));
        }

        [Fact]
        public void IsValidFamilyName_NullEmptyOrWhitespace_IsFalse()
        {
            Assert.False(CasValidation.IsValidFamilyName(null));
            Assert.False(CasValidation.IsValidFamilyName(""));
            Assert.False(CasValidation.IsValidFamilyName("   "));
            Assert.False(CasValidation.IsValidFamilyName("\t\n "));
        }

        [Fact]
        public void IsValidFamilyName_NormalName_IsTrue()
        {
            Assert.True(CasValidation.IsValidFamilyName("Bella Goth"));
            Assert.True(CasValidation.IsValidFamilyName("  Bella Goth  "));
        }

        [Fact]
        public void IsValidFamilyName_LengthBoundary_IsMax32()
        {
            Assert.Equal(32, CasValidation.MaxNameLength);
            Assert.True(CasValidation.IsValidFamilyName(new string('a', 32)));
            Assert.False(CasValidation.IsValidFamilyName(new string('a', 33)));
        }

        [Fact]
        public void SanitizeSimName_TrimsAndTruncates()
        {
            Assert.Equal("Bella", CasValidation.SanitizeSimName("  Bella \t"));
            Assert.Equal(new string('a', 32), CasValidation.SanitizeSimName(new string('a', 33)));
            //already at the cap: unchanged
            Assert.Equal(new string('b', 32), CasValidation.SanitizeSimName(new string('b', 32)));
        }

        [Fact]
        public void SanitizeSimName_NullOrBlank_BecomesEmpty()
        {
            Assert.Equal("", CasValidation.SanitizeSimName(null));
            Assert.Equal("", CasValidation.SanitizeSimName(""));
            Assert.Equal("", CasValidation.SanitizeSimName("   "));
        }

        [Fact]
        public void SanitizeFamilyName_TrimsAndTruncates()
        {
            Assert.Equal("Goth", CasValidation.SanitizeFamilyName("  Goth \t"));
            Assert.Equal(new string('c', 32), CasValidation.SanitizeFamilyName(new string('c', 40)));
            Assert.Equal(new string('d', 32), CasValidation.SanitizeFamilyName(new string('d', 32)));
        }

        [Fact]
        public void SanitizeFamilyName_NullOrBlank_BecomesEmpty()
        {
            Assert.Equal("", CasValidation.SanitizeFamilyName(null));
            Assert.Equal("", CasValidation.SanitizeFamilyName(""));
            Assert.Equal("", CasValidation.SanitizeFamilyName("  \t "));
        }

        [Fact]
        public void SanitizedNames_AreAlwaysAcceptedByIsValidFamilyName()
        {
            //SanitizeFamilyName is the backstop in SaveFamily: whatever it returns must pass
            //IsValidFamilyName, otherwise a long/whitespace name would be silently dropped.
            Assert.True(CasValidation.IsValidFamilyName(CasValidation.SanitizeFamilyName(new string('e', 100))));
            Assert.True(CasValidation.IsValidFamilyName(CasValidation.SanitizeFamilyName("  Dynasty  ")));
            //a whitespace-only name sanitizes to empty, which is then correctly rejected.
            Assert.False(CasValidation.IsValidFamilyName(CasValidation.SanitizeFamilyName("   ")));
        }
    }
}
