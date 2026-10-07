using System;
using System.IO;
using FSO.Common;
using Simitone.Client;
using Simitone.Client.UI.Screens;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The move-in half of <see cref="GameController.EnterGameMode"/>'s lot-name protocol.
    ///
    /// <para>
    /// <c>EnterGameMode</c> parses a name like <c>"!n4"</c> and hands the number to
    /// <c>StartMoveIn</c>. It used to do that with
    /// <c>int.Parse(lotName.Substring(2))</c> behind a <c>lotName.Length &gt; 1</c> guard, which
    /// checked the <em>flag</em> and never checked that a number followed - and ran inside a
    /// <c>GameThread.NextUpdate</c> callback with nothing catching. So <c>"!n"</c>, <c>"!nX"</c>
    /// or a null name reached <c>int.Parse</c> (or <c>lotName.Length</c>) and threw
    /// <see cref="FormatException"/> / <see cref="NullReferenceException"/> on the game thread:
    /// the process-death case the rest of this codebase's catch discipline exists to avoid.
    /// </para>
    ///
    /// <para>
    /// The protocol is now a pure function, so every malformed shape is an assertion here rather
    /// than something only a running game could produce. "The game opens an ordinary lot" is a
    /// defined answer, not a fallback: every caller that passes a plain house path gets it.
    /// </para>
    ///
    /// <para>
    /// In the non-parallel collection because a rejected-but-prefixed name is logged - and
    /// <c>SimitoneLog</c> resolves its path from the process-global
    /// <see cref="FSOEnvironment.UserDir"/>, which this class redirects at a temp directory so
    /// the log cannot land in the test output folder.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class MoveInRequestTests : IDisposable
    {
        private readonly string _originalUserDir;
        private readonly string _root;

        public MoveInRequestTests()
        {
            _originalUserDir = FSOEnvironment.UserDir;
            _root = Path.Combine(Path.GetTempPath(), "simitone-movein-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            FSOEnvironment.UserDir = _root;
        }

        public void Dispose()
        {
            FSOEnvironment.UserDir = _originalUserDir;
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                //best effort
            }
            catch (UnauthorizedAccessException)
            {
                //best effort
            }
        }

        [Theory]
        [InlineData("!n4", NeighSelectionMode.MoveIn, 4)]
        [InlineData("!m4", NeighSelectionMode.MoveInMagic, 4)]
        [InlineData("!n0", NeighSelectionMode.MoveIn, 0)]
        [InlineData("!n21", NeighSelectionMode.MoveIn, 21)]
        [InlineData("!n1234", NeighSelectionMode.MoveIn, 1234)]
        public void AWellFormedRequestIsTheModeAndTheNumberAfterIt(string lotName,
            NeighSelectionMode expectedMode, int expectedLot)
        {
            var mode = GameController.ResolveEntryRequest(lotName, out var lot);

            Assert.Equal(expectedMode, mode);
            Assert.Equal(expectedLot, lot);
        }

        [Theory]
        [InlineData("UserData/Houses/House01.iff")]
        [InlineData("")]
        [InlineData("!")]
        [InlineData("!n")]
        [InlineData("!m")]
        [InlineData("!x4")]
        [InlineData("!N4")]
        [InlineData("!nX")]
        [InlineData("!n4.5")]
        [InlineData("!n 4")]
        [InlineData("!n-1")]
        [InlineData("!n+4")]
        [InlineData("n4")]
        [InlineData(" !n4")]
        [InlineData(null)]
        public void AnythingElseIsAnOrdinaryLotRatherThanAThrow(string lotName)
        {
            // Each of these reached int.Parse (or the .Length deref) before. All of them now open
            // the lot normally, which is what a plain house path has always done.
            var mode = GameController.ResolveEntryRequest(lotName, out var lot);

            Assert.Equal(NeighSelectionMode.Normal, mode);
            Assert.Equal(0, lot);
        }

        [Fact]
        public void TheLotNumberIsParsedTheSameWayWhateverTheCulture()
        {
            // A locale whose digits are not ASCII would otherwise let int.Parse-with-current-culture
            // accept a name the English player cannot type, and reject one they can.
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("ar-EG");

                Assert.Equal(NeighSelectionMode.MoveIn,
                    GameController.ResolveEntryRequest("!n4", out var arabic));
                Assert.Equal(4, arabic);

                // Arabic-Indic digits are not accepted as a lot number, so this is an ordinary lot
                // rather than a surprise value.
                Assert.Equal(NeighSelectionMode.Normal,
                    GameController.ResolveEntryRequest("!n٤", out _));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void ARequestForANegativeOrEmptyNumberIsNotAMoveIn()
        {
            // TryParse with NumberStyles.None, so "-1" and "+4" cannot reach StartMoveIn as a lot.
            // -1 is the VM's "gameplay disabled" multiplier, and a move-in for it would be a very
            // confusing thing to have asked for.
            Assert.Equal(NeighSelectionMode.Normal, GameController.ResolveEntryRequest("!n-1", out _));
            Assert.Equal(NeighSelectionMode.Normal, GameController.ResolveEntryRequest("!n+4", out _));
        }

        [Fact]
        public void EveryModeStillHasItsOwnDistinctName()
        {
            // The prefix switch has to keep the two markers apart; a "!" followed by anything else
            // is not a move-in of either kind.
            Assert.NotEqual(NeighSelectionMode.MoveIn, NeighSelectionMode.MoveInMagic);
            Assert.Equal(NeighSelectionMode.MoveIn,
                GameController.ResolveEntryRequest("!n1", out _));
            Assert.Equal(NeighSelectionMode.MoveInMagic,
                GameController.ResolveEntryRequest("!m1", out _));
        }
    }
}
