using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The <c>-path</c> startup override must survive every quoting/separator form a player
    /// might type, because when it silently fails the game reports the TS1 data as missing
    /// even when the path is correct.
    /// </summary>
    public sealed class GamePathArgumentTests
    {
        [Theory]
        [InlineData(@"C:\The Sims", "C:/The Sims/")]
        [InlineData(@"C:\The Sims\", "C:/The Sims/")]
        [InlineData(@"C:\The Sims/", "C:/The Sims/")]
        [InlineData(@"C:/The Sims", "C:/The Sims/")]
        public void NormalizesBackslashesAndTrailingSlash(string raw, string expected)
        {
            Assert.Equal(expected, GamePathArgument.Normalize(raw));
        }

        [Theory]
        [InlineData(@":C:\The Sims")]      // -path:C:\The Sims
        [InlineData(@"=C:\The Sims")]      // -path=C:\The Sims
        [InlineData(@":""C:\The Sims""")]  // -path:"C:\The Sims" (quotes already stripped by shell as value only)
        [InlineData(@": ""C:\The Sims""")] // with a space after the separator
        [InlineData(@"""C:\The Sims""")]   // bare quoted value
        public void StripsTheSeparatorAndOptionalQuotes(string raw)
        {
            Assert.Equal("C:/The Sims/", GamePathArgument.Normalize(raw));
        }

        [Fact]
        public void PreservesSpacesInThePath()
        {
            Assert.Equal("D:/Program Files/Sims/", GamePathArgument.Normalize(@":D:\Program Files\Sims"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(":")]
        [InlineData("=")]
        [InlineData("\"\"")]
        public void ReturnsNullForAnEmptyValue(string raw)
        {
            Assert.Null(GamePathArgument.Normalize(raw));
        }
    }
}
