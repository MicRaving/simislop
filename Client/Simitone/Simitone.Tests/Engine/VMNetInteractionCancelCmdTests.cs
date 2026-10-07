using FSO.SimAntics.NetPlay.Model.Commands;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMNetInteractionCancelCmd"/> - the net command the queue display's X sends to
    /// cancel an interaction.
    ///
    /// <para>
    /// This used to live alongside the cancel-policy tests and is kept on its own now that they
    /// are gone: it is about the command's behaviour when the lot is moving on underneath it, not
    /// about how a cancel is decided.
    /// </para>
    /// </summary>
    public sealed class VMNetInteractionCancelCmdTests
    {
        [Fact]
        public void ACancelForASimThatIsNoLongerThereIsRefusedRatherThanThrown()
        {
            // Net commands are executed while a tick's commands are applied, so a throw here is not
            // contained to the sim that asked: it unwinds the command loop for every command in the
            // tick, on every peer. A cancel naming a sim that has already left the lot is ordinary
            // traffic - a queue display's X clicked as the lot unloads.
            Assert.False(new VMNetInteractionCancelCmd().Execute(null, null));
        }
    }
}