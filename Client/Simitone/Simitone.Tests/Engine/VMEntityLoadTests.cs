using FSO.SimAntics;
using System;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// Loading an entity's multitile master, and the rest of the load-time rules that keep a
    /// partially-installed content database from taking the lot down with it.
    ///
    /// <para>
    /// A multitile member's master is named by guid, not carried in the save, so it has to be
    /// re-resolved against whatever this machine has installed. The constructor path already
    /// tolerates a missing object definition - <c>VM.LoadAsync</c> records a <c>VMLoadError</c>,
    /// marks the entity failed and moves on - and the <em>load</em> path did not, so the one
    /// check that was left out turned "I do not have that custom object" into "cannot open the
    /// lot".
    /// </para>
    /// </summary>
    public sealed class VMEntityLoadTests
    {
        [Fact]
        public void GuidZeroMeansThisEntityHasNoMaster()
        {
            // Which is what every non-multitile object saves, and the lookup must not even be
            // asked: guid 0 is a statement, not a query.
            var asked = new List<uint>();

            Assert.Null(VMEntity.ResolveMaster<string>(0, guid => { asked.Add(guid); return "master"; }));
            Assert.Empty(asked);
        }

        [Fact]
        public void AnInstalledMasterResolves()
        {
            // The valid case, unchanged: the load reads the master's OBJ and its tree table.
            Assert.Equal("master-0x1", VMEntity.ResolveMaster<string>(1, guid => "master-0x" + guid.ToString("X")));
        }

        [Fact]
        public void AMasterThatIsNotInstalledIsNotFatal()
        {
            // The bug. A save written on a machine that had the master, opened on one that does
            // not, must come up without a master rather than throwing out of the load.
            var resolved = VMEntity.ResolveMaster<string>(0xABCD1234, guid => null);

            Assert.Null(resolved);
        }

        [Fact]
        public void OnlyTheMasterTheSaveNamedIsLookedUp()
        {
            var asked = new List<uint>();

            VMEntity.ResolveMaster<string>(0x1234, guid => { asked.Add(guid); return null; });

            Assert.Equal(new uint[] { 0x1234 }, asked);
        }

        [Fact]
        public void ThereIsNoLookupToAsk()
        {
            // A VM built with no content provider at all still has to load its entities.
            Assert.Null(VMEntity.ResolveMaster<string>(0x1234, null));
        }
    }
}
