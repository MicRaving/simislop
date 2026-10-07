using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.NetPlay.EODs;
using FSO.SimAntics.NetPlay.EODs.Handlers;
using FSO.SimAntics.NetPlay.EODs.Utils;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The TS1 null guards in the EOD (minigame) framework -
    /// <see cref="VMEODPlatformState"/> and the plugins that use it.
    ///
    /// <para>
    /// The EOD framework runs the retail The Sims Online minigame plugins inside FreeSO's server
    /// VM, and a TS1 lot is driven by that same VM. So every plugin is loaded on a lot where the
    /// TSO platform state it was written against does not exist: <c>VMEntity.TSOState</c> is
    /// <c>PlatformState as VMTSOEntityState</c> and <c>VM.TSOState</c> is
    /// <c>PlatformState as VMTSOLotState</c>, while <c>VM.Init</c> puts a
    /// <c>VMTS1ObjectState</c> / <c>VMTS1LotState</c> there instead.
    /// </para>
    ///
    /// <para>
    /// Thirty-three owner checks and one community-lot read dereferenced one of those two
    /// properties with no guard, in seven plugins. From a panel's plaintext message the
    /// <c>NullReferenceException</c> was swallowed by <c>VMNetEODMessageCmd.Verify</c>, so the
    /// <c>*_owner_show</c> / <c>*_owner_init</c> reply was dropped and the owner panel never
    /// opened with nothing recorded anywhere. From <c>OnConnection</c> and from the casino state
    /// machines nothing caught it at all, so <c>VMThread</c> tore the interaction down - and in
    /// the state machine's case that was <em>player</em> mode, not owner mode: the casinos could
    /// not leave their betting round.
    /// </para>
    ///
    /// <para>
    /// What is pinned here is the shape of the degradation rather than a crash that no longer
    /// happens. Two properties have to hold together, and they pull in opposite directions: on a
    /// TSO lot nothing may change, and on a TS1 lot the answer has to be a decision rather than a
    /// throw. The first is why <c>IsOwner</c> is still the same comparison and not "not broken, not
    /// owner, be safe" - including for the degenerate <c>OwnerID == 0</c> object. The second is why
    /// "no platform state" is answered "no owner" and not "nobody may use this panel": player mode
    /// is the mode that matters and its economy already moves TS1 family funds.
    /// </para>
    ///
    /// <para>
    /// The last test is the one that keeps the fix from eroding. A guard is only as good as its
    /// coverage, and coverage of "thirty-three sites" is a number a later edit can make a lie, so
    /// the EOD plugin sources are read and checked against the raw casts that used to be there.
    /// Every handler <c>VMEODServer.IDToHandler</c> names is included, which means a plugin added
    /// to that table is covered without this file being told about it.
    /// </para>
    /// </summary>
    public sealed class VMEODPlatformStateTests
    {
        /// <summary>
        /// Expressions that read a TSO platform state without asking whether there is one. Each of
        /// these is a cast whose result is dereferenced, and each of them throws on a TS1 lot.
        /// </summary>
        private static readonly string[] UnguardedCasts =
        {
            "(VMTSOObjectState)",
            "(VMTSOEntityState)",
            "PlatformState as VMTSO",
            ".TSOState as VMTSO",
            ".TSOState.CommunityLot",
        };

        /// <summary>The one file in the EOD tree that is allowed to name these shapes.</summary>
        private const string GuardSource = "VMEODPlatformState.cs";

        // ---- owner resolution -----------------------------------------------------------------

        [Fact]
        public void AnObjectWithNoPlatformStateIsOwnedByNobody()
        {
            // This is the TS1 case, and it is the whole reason the class exists. A null state is
            // "no owner", not a NullReferenceException - and not an error either, because the
            // answer is what the plugins were always going to get for anybody but an owner.
            Assert.False(VMEODPlatformState.IsOwner(null, 0));
            Assert.False(VMEODPlatformState.IsOwner(null, 1));
            Assert.False(VMEODPlatformState.IsOwner(null, uint.MaxValue));
        }

        [Theory]
        [InlineData(42u, 42u, true)]
        [InlineData(42u, 43u, false)]
        [InlineData(0u, 1u, false)]
        [InlineData(1u, 0u, false)]
        [InlineData(uint.MaxValue, uint.MaxValue, true)]
        public void TheOwnerIsTheAvatarWhosePersistIdTheObjectCarries(uint ownerID, uint avatarID, bool expected)
        {
            // A TSO lot, where the object does have a TSO state. Exactly the comparison the
            // plugins used to make inline, so this is the "nothing changed" half of the fix.
            var state = new VMTSOObjectState { OwnerID = ownerID };

            Assert.Equal(expected, VMEODPlatformState.IsOwner(state, avatarID));
        }

        [Fact]
        public void AnUnownedTsoObjectIsStillComparedRatherThanShortCircuited()
        {
            // OwnerID 0 is what a public or un-bought TSO object carries, and it is compared
            // against the avatar's id like any other value. Reading "no owner id" as "this object
            // belongs to nobody, whatever the client says" would be a behaviour change on TSO
            // lots dressed up as a null guard, and it is the mistake a defensive helper invites.
            var unowned = new VMTSOObjectState { OwnerID = 0 };

            Assert.True(VMEODPlatformState.IsOwner(unowned, 0));
            Assert.False(VMEODPlatformState.IsOwner(unowned, 1));
        }

        [Fact]
        public void OwnerResolutionIgnoresAnAvatarlessClient()
        {
            // The old code read client.Avatar.PersistID, which is the same throw by another route.
            // A client with no avatar is not an owner. Not reachable from the guarded call sites
            // today - they all dereference client.Avatar themselves - but it is the shape the next
            // one might have.
            Assert.False(VMEODPlatformState.IsOwner(null, null));
        }

        [Fact]
        public void TheOwnerRefusalIsKeyedPerPluginRatherThanPerProcess()
        {
            // "Logged once" has to mean once per plugin. Two plugins open on a TS1 lot have two
            // separate reasons to be missing, and a single shared key would let whichever was
            // opened first silence the other for the rest of the session.
            var blackjack = VMEODPlatformState.RefusalKey(0x2B2FC514);
            var roulette = VMEODPlatformState.RefusalKey(0x0B2A6B83);

            Assert.NotEqual(blackjack, roulette);
            Assert.StartsWith(VMEODPlatformState.RefusalKeyPrefix, blackjack);
            // the hex id, so the line can be matched against VMEODServer.IDToHandler by hand
            Assert.Contains("2B2FC514", blackjack);
            Assert.Equal(blackjack, VMEODPlatformState.RefusalKey(0x2B2FC514));
        }

        [Fact]
        public void TheOwnerRefusalSaysWhyItIsRefusingAndWhatItCosts()
        {
            // A refusal nobody can revisit is a refusal nobody can fix. Both halves are in the
            // constant rather than only in the log line so the log line cannot lose them: why
            // there is no owner on a TS1 lot, and that player mode is unaffected.
            Assert.Contains("TS1", VMEODPlatformState.NoOwnerReason);
            Assert.Contains("VMTS1ObjectState", VMEODPlatformState.NoOwnerReason);
            Assert.True(VMEODPlatformState.NoOwnerReason.Length > 80,
                "the reason is " + VMEODPlatformState.NoOwnerReason.Length + " characters, which is"
                + " too short to be the explanation of a missing feature");
        }

        // ---- the cooldown guard ----------------------------------------------------------------

        [Fact]
        public void NoLotStateIsNotACommunityLot()
        {
            // The other half of the crash, in VMEODCooldownEventPlugin. vm.TSOState is null on a
            // TS1 lot and the plugin read .CommunityLot straight off it while choosing which
            // callback to build. "No" is the safe direction: the SansCommunity cooldown modes
            // only substitute plugin persist for the database *on a community lot*, so a lot that
            // cannot be one keeps the database path it would have had anyway.
            Assert.False(VMEODPlatformState.IsCommunityLot((VMTSOLotState)null));
        }

        [Theory]
        [InlineData((byte)11, true)]      // property category 11 is the community lot
        [InlineData((byte)0, false)]       // residential
        [InlineData((byte)7, false)]       // the "welcome" category
        [InlineData((byte)255, false)]
        public void TheCommunityLotGuardAgreesWithTheLotStatesOwnAnswer(byte category, bool expected)
        {
            var state = new VMTSOLotState { PropertyCategory = category };

            Assert.Equal(state.CommunityLot, VMEODPlatformState.IsCommunityLot(state));
            Assert.Equal(expected, VMEODPlatformState.IsCommunityLot(state));
        }

        // ---- the object-broken guard -----------------------------------------------------------

        [Fact]
        public void AnObjectWithNoPlatformStateIsNotBroken()
        {
            // Also read straight off a platform state, by the three casino state machines on every
            // betting round: ((VMTSOEntityState)Controller.Invoker.PlatformState as
            // VMTSOObjectState).Broken. On a TS1 lot that threw every round, so the game could
            // never leave Betting_Round - a player-mode crash, not an owner-mode one. "Not
            // broken" is the right answer and not merely the safe one: a TS1 object has no wear
            // counter, no repair interaction and no break, so it cannot be broken.
            Assert.False(VMEODPlatformState.IsBroken((VMTSOObjectState)null));
            Assert.False(VMEODPlatformState.IsBroken((FSO.SimAntics.VMEntity)null));
        }

        [Fact]
        public void ABrandNewTsoObjectIsNotBroken()
        {
            // A fresh state must not read as broken, or the guard would have inverted the
            // behaviour it exists to preserve.
            Assert.False(VMEODPlatformState.IsBroken(new VMTSOObjectState()));
        }

        [Fact]
        public void TheBrokenSentinelStillReadsAsBroken()
        {
            // QtrDaysSinceLastRepair is a byte and 255 is the "broken" sentinel, so a guard that
            // reached for a bool or an int instead would quietly stop seeing broken objects.
            var broken = new VMTSOObjectState { QtrDaysSinceLastRepair = 255 };
            var worn = new VMTSOObjectState { QtrDaysSinceLastRepair = 40 };

            Assert.True(broken.Broken);
            Assert.True(VMEODPlatformState.IsBroken(broken));
            Assert.False(VMEODPlatformState.IsBroken(worn));
        }

        // ---- coverage: no raw cast is left in the EOD tree --------------------------------------

        [Fact]
        public void NoEodHandlerReadsATsoPlatformStateWithoutAskingForOneFirst()
        {
            // The regression guard, and the reason the fix cannot quietly rot. "Thirty-three sites
            // were fixed" is a claim about a past edit; this is a claim about the present tree,
            // derived from VMEODServer.IDToHandler so a plugin added to that table tomorrow is
            // covered without anyone updating this file.
            var offenders = new List<string>();

            foreach (var handler in HandlerTypes())
            {
                var file = Path.Combine(HandlersDirectory(), handler.Name + ".cs");
                if (!File.Exists(file))
                {
                    offenders.Add(handler.Name + ": no source file at " + file);
                    continue;
                }
                offenders.AddRange(Offenders(handler.Name, File.ReadAllText(file)));
            }

            Assert.Empty(offenders);
        }

        [Fact]
        public void NoOtherEodSourceReadsATsoPlatformStateWithoutAskingForOneFirst()
        {
            // The handlers are the plugins; this is the framework around them - the host, the
            // server, the model and the utils - which is where a new EOD would reach for a TSO
            // state before reaching for the guard.
            var offenders = new List<string>();

            foreach (var file in new DirectoryInfo(EodDirectory()).EnumerateFiles("*.cs", SearchOption.AllDirectories))
            {
                if (file.Name == GuardSource) continue;   // it names these shapes in order to forbid them
                offenders.AddRange(Offenders(file.Name, File.ReadAllText(file.FullName)));
            }

            Assert.Empty(offenders);
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static List<string> Offenders(string label, string source)
        {
            // Comments are not code. A guard is *documented* by naming the cast it replaced, and
            // those mentions are exactly the strings being searched for - stripping comments
            // first is what lets the reason for the guard sit next to the guard.
            var code = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            code = Regex.Replace(code, @"//[^\n]*", "");

            return UnguardedCasts
                .Where(cast => code.Contains(cast))
                .Select(cast => label + " still contains '" + cast + "'")
                .ToList();
        }

        private static IEnumerable<Type> HandlerTypes()
        {
            return VMEODServer.IDToHandler.Values
                .Distinct()
                .Where(x => x.IsSubclassOf(typeof(VMEODHandler)))
                .OrderBy(x => x.Name);
        }

        private static string HandlersDirectory()
        {
            return Path.Combine(EodDirectory(), "Handlers");
        }

        /// <summary>
        /// The EOD plugin source folder, found by walking up out of the test output until the
        /// marker file turns up, so a change to the build layout does not turn this into "file not
        /// found". Fails loudly rather than skipping: a scan that silently found nothing would
        /// report every plugin as clean.
        /// </summary>
        private static string EodDirectory()
        {
            var marker = Path.Combine("FreeSO", "TSOClient", "tso.simantics", "NetPlay", "EODs", "VMEODServer.cs");
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, marker);
                if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not find the FreeSO EOD sources (looked for " + marker + ") walking up from "
                + AppContext.BaseDirectory + ". These tests read the plugin sources on purpose, so "
                + "they cannot pass by finding nothing.");
        }
    }
}
