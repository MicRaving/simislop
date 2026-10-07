/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Entities;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The live lot has to keep working while a Part B capability is on.
    ///
    /// <para>
    /// <b>What is pinned here.</b> A lot on which nothing can be built, bought or interacted with
    /// is the failure every one of these three symptoms describes, and the one thing they share is
    /// a budget decision: build/buy charges and interaction funds all go through
    /// <c>CanTransactBudgetForFamily</c> on the <see cref="FSO.SimAntics.Entities.VMAvavatar"/>'s
    /// family. So the seam B5 introduced - an injected family authority that replaces "whatever
    /// family the lot happens to have activated" - is the seam under test.
    /// </para>
    /// <para>
    /// <b>The trap these tests exist for.</b> The number a family authority is asked with is an
    /// avatar's <c>VMPersonDataVariable.TS1FamilyNumber</c>, and
    /// <c>VMTS1LotState.ActivateFamily</c> writes <c>CurrentFamily.ChunkID</c> into it. The
    /// neighbourhood's own family table is keyed on <c>FAMI.HouseNumber</c> instead, and families
    /// and lots are both numbered from one, so the two ranges overlap and a lookup keyed on the
    /// wrong one answers with a real family rather than with nothing. The wrong family then pays -
    /// or refuses to pay - for the player's builds, purchases and money-spending interactions, and a
    /// wrong family with no money refuses all three. That is a lot where nothing works, and it is
    /// reachable only with <c>NeighbourhoodWorldServices</c> on, which is why the lot is fine with
    /// every flag off.
    /// </para>
    /// <para>
    /// <b>No VM, no content manager, no device.</b> The rule is deliberately a pure static
    /// (<see cref="SimitoneGlobalLink.IsTheFamilyAskedFor"/>) so the whole of it is reachable from
    /// here, and the screen's own resolver is pinned by reading its source - which is the only way
    /// to assert anything about a method on a screen that needs a graphics device to construct.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldLiveLotTests
    {
        /// <summary>
        /// A family with the three numbers a resolver can be confused between: its own chunk id,
        /// the house number it lives at, and a budget.
        /// </summary>
        private static FAMI Family(ushort chunkId, int houseNumber, int budget)
        {
            return new FAMI
            {
                ChunkID = chunkId,
                ChunkLabel = "",
                ChunkType = "FAMI",
                HouseNumber = houseNumber,
                Budget = budget
            };
        }

        /// <summary>A link over an arbitrary family authority, and a record of the lots it asked to go to.</summary>
        private static SimitoneGlobalLink Link(Func<short, FAMI> authority, List<int> transitions = null)
        {
            return new SimitoneGlobalLink(
                authority,
                lot => { if (transitions != null) transitions.Add(lot); },
                true);
        }

        // ---- the rule --------------------------------------------------------------------------------

        [Fact]
        public void AFamilyIsOnlyAcceptedWhenItsOwnChunkIdIsTheNumberAskedFor()
        {
            var family = Family(chunkId: 3, houseNumber: 27, budget: 500);

            Assert.True(SimitoneGlobalLink.IsTheFamilyAskedFor(3, family));
            //a family that lives at house 3 is not the family numbered 3
            Assert.False(SimitoneGlobalLink.IsTheFamilyAskedFor(27, family));
            Assert.False(SimitoneGlobalLink.IsTheFamilyAskedFor(1, family));
            //"knows nothing" is a false, not a throw
            Assert.False(SimitoneGlobalLink.IsTheFamilyAskedFor(3, null));
        }

        [Fact]
        public void NoFamilyIsNumberedBelowZero()
        {
            var family = Family(chunkId: 300, houseNumber: 0, budget: 0);

            Assert.False(SimitoneGlobalLink.IsTheFamilyAskedFor(-1, family));
            Assert.False(SimitoneGlobalLink.IsTheFamilyAskedFor(short.MinValue, family));
            Assert.True(SimitoneGlobalLink.IsTheFamilyAskedFor(300, family));
        }

        // ---- the failure this is about ----------------------------------------------------------------

        [Fact]
        public void AnAuthorityKeyedOnHouseNumbersIsTreatedAsKnowingNothing()
        {
            //the neighbourhood as it actually ships it: FamilyForHouse is keyed on HouseNumber.
            var byHouse = new Dictionary<short, FAMI>
            {
                { 2, Family(chunkId: 7, houseNumber: 2, budget: 50) },
                { 3, Family(chunkId: 8, houseNumber: 3, budget: 0) }
            };
            var link = Link(id => byHouse.TryGetValue(id, out var found) ? found : null);

            //asking for family 7 gets back the household that happens to live at house 7 - which
            //here is neither family 7 nor anybody the avatar belongs to.
            Assert.Null(link.ResolveFamily(null, (short)7));
            //and with no VM there is no current family either, so the answer is Maxis's: nobody is
            //charged and nothing is refused, which is the stub's answer exactly.
            Assert.Equal(uint.MaxValue, link.GetBudgetForFamily(null, (VMEntity)null));
            Assert.True(link.CanTransactBudgetForFamily(null, (VMEntity)null, -1000000));

            //the household that would otherwise have been charged is untouched
            Assert.Equal(50, byHouse[2].Budget);
            Assert.Equal(0, byHouse[3].Budget);
        }

        [Fact]
        public void AMistakeHereCannotRefuseABuildOrAPurchase()
        {
            //The regression a player sees. Every one of these three is a live-lot path that asks
            //"can this family afford it", and with a mis-keyed authority the answer came from
            //somebody else's bank balance - so a broke household somewhere else in the
            //neighbourhood refused every build, every purchase and every interaction that spent
            //money. With the answer verified, none of them is.
            var broke = Family(chunkId: 9, houseNumber: 4, budget: 0);
            var link = Link(id => id == 9 ? null : broke);

            //a transaction whose parties this link cannot find is Maxis's business and succeeds,
            //which is what keeps the build queue draining and buy mode placing
            Assert.True(link.PerformTransaction(null, false, 1, 2, 100));
            Assert.True(link.PerformTransaction(null, true, 1, 2, 100));
            Assert.Equal(0, broke.Budget);
        }

        [Fact]
        public void ACorrectlyKeyedAuthorityStillOwnsTheMoney()
        {
            //The fix must not have cost the capability its one job. A resolver keyed on chunk id is
            //accepted, charged and refused exactly as it should be.
            var rich = Family(chunkId: 7, houseNumber: 22, budget: 1000);
            var link = Link(id => id == 7 ? rich : null);

            Assert.Same(rich, link.ResolveFamily(null, (short)7));
            Assert.True(link.CanTransactBudgetForFamily(rich, -1000));
            Assert.False(link.CanTransactBudgetForFamily(rich, -1001));
            Assert.True(link.TransactBudgetForFamily(rich, -1000));
            Assert.Equal(0, rich.Budget);
            Assert.False(link.TransactBudgetForFamily(rich, -1));
        }

        // ---- the screen's own resolver ----------------------------------------------------------------

        [Fact]
        public void TheScreensFamilyAuthorityLooksTheFamilyUpByChunkIdAndNotByHouseNumber()
        {
            var source = ReadClientSource("UI", "Screens", "TS1GameScreen.cs");

            var code = source
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("///", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("*", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("/*", StringComparison.Ordinal))
                .ToList();

            //The failure: TS1FamilyNumber is a FAMI ChunkID (VMTS1LotState.ActivateFamily writes
            //CurrentFamily.ChunkID into it) and GetFamilyForHouse is keyed on FAMI.HouseNumber.
            //Where the two ranges overlap that answers with a real, wrong family.
            var byHouse = code
                .Where(line => line.Contains("GetFamilyForHouse"))
                .ToList();
            Assert.True(byHouse.Count == 0,
                "B5's family authority must not be looked up by house number. These lines do it:\n"
                + string.Join("\n", byHouse));

            //and the answer has to be the chunk, verified against its own id.
            Assert.True(code.Any(line => line.Contains("GetFamily(")),
                "B5's family authority must fetch the FAMI chunk by its own id.");
            Assert.True(code.Any(line => line.Contains("IsTheFamilyAskedFor")),
                "B5's family authority must be checked against the number it was asked for.");

            //and it is wired into the link under the name the resolver now has.
            Assert.True(source.Any(line => line.Contains("ResolveFamilyByChunkId,")),
                "the global link must be constructed over ResolveFamilyByChunkId.");
            Assert.False(source.Any(line => line.Contains("ResolveFamilyForHouse")),
                "the house-numbered resolver is gone; it was the bug.");
        }

        [Fact]
        public void ALotSwitchThisScreenCannotPerformDoesNotLeaveTheLotPaused()
        {
            var source = ReadClientSource("UI", "Screens", "TS1GameScreen.cs");
            var text = string.Join("\n", source);

            //VMLotSwitch pauses the lot so the requesting tick finishes, and Update only acts on a
            //positive SwitchLot. A destination of 0 - a family with no house, or a lot number that
            //arrives as uint.MaxValue - therefore left vm.SpeedMultiplier at 0 for the rest of the
            //session, which is a lot that cannot be built on, bought in or interacted with at all.
            //The pause has to be handed back on that path.
            var start = text.IndexOf("private void VMLotSwitch", StringComparison.Ordinal);
            Assert.True(start >= 0, "could not find VMLotSwitch in TS1GameScreen.cs");
            var body = text.Substring(start, Math.Min(2400, text.Length - start));

            Assert.True(body.Contains("vm.SpeedMultiplier = resuming"),
                "VMLotSwitch must restore the speed when it cannot switch, or the lot is frozen.");
            Assert.True(body.Contains("ActiveFamily?.HouseNumber"),
                "VMLotSwitch must survive a session with no active family; a throw here escapes into"
                + " the VM's event invoke and leaves the lot paused.");
        }

        [Fact]
        public void TheFlaggedPathsOnlyEverPauseForASwitchThatIsAboutToHappen()
        {
            //The rest of the switch block, unchanged and still gated on SwitchLot > 0: the pause
            //above is a pause *for the switch*, so the two have to be read together.
            var source = ReadClientSource("UI", "Screens", "TS1GameScreen.cs");
            var text = string.Join("\n", source);

            var start = text.IndexOf("if (SwitchLot > 0)", StringComparison.Ordinal);
            Assert.True(start >= 0, "could not find the switch block in TS1GameScreen.cs");

            //the travel capability still performs the very same five steps, behind its flag, and
            //the clock handshake is still the first step of the stash on both paths.
            var travel = text.IndexOf(
                "else if (NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel))",
                StringComparison.Ordinal);
            var handshake = text.IndexOf("HandshakeClockOnSave();", start, StringComparison.Ordinal);
            var end = text.IndexOf("SwitchLot = -1;", start, StringComparison.Ordinal);
            Assert.True(travel > start && end > travel,
                "the travel capability must still perform the very same switch, behind its flag.");
            Assert.True(handshake > start && handshake < end,
                "the clock handshake is still the first step of the stash, inside the switch.");
        }

        // ---- helpers ---------------------------------------------------------------------------------

        private static string[] ReadClientSource(params string[] parts)
        {
            var root = RepositoryRoot();
            Assert.NotNull(root);
            var path = Path.Combine(new[] { root, "Client", "Simitone", "Simitone.Client" }
                .Concat(parts).ToArray());
            Assert.True(File.Exists(path), "could not find " + path);
            return File.ReadAllLines(path);
        }

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Client")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }
    }
}