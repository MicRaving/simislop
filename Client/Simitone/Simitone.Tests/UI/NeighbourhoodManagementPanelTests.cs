/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The pure half of the Neighbourhood Management page: the captions a slot row shows, the
    /// rules that decide which of the six action buttons are offered, the reasons shown instead of
    /// a button that would be refused, and the wording of the confirmation a neighbourhood switch
    /// goes through.
    ///
    /// <para>
    /// These are all in <see cref="NeighbourhoodPanelModel"/>, deliberately apart from the
    /// <c>UIMobileDialog</c> that draws them, because a graphics device is not available to a
    /// unit test and a caption that cannot be asserted is a caption that drifts.
    /// </para>
    ///
    /// <para>
    /// The recurring theme is <b>agreement with the backend</b>. The page and
    /// <see cref="NeighbourhoodManager"/> enforce the same rules from two ends - a greyed-out
    /// button in one, a <c>false</c> and a logged reason in the other - and the failure mode that
    /// matters is the two disagreeing. So the rules here are asserted against the backend's own
    /// predicates where it exposes them (<see cref="NeighbourhoodManager.IsValidName"/>,
    /// <see cref="NeighbourhoodManager.IsKnownSlot"/>, <see cref="NeighbourhoodManager.SlotName"/>)
    /// and against the backend's own guards where it does not (the primary slot and the open slot
    /// are re-implemented, so those are pinned against a state table rather than against a call
    /// that would have to touch a disk).
    /// </para>
    ///
    /// <para>
    /// The switch rules are the one place where nothing can be checked against a backend call at
    /// all - <c>InitSpecific</c> is a void method on a live provider that discards the session,
    /// and there is no predicate to ask. So the whole table is pinned here instead, exhaustively,
    /// because that table is the only thing standing between a player and a switch that quietly
    /// does the wrong thing: opening the neighbourhood that is already open, opening a folder that
    /// is not a neighbourhood at all, opening one underneath a live lot or a pending move-in, or
    /// opening one when the page cannot name what it would be closing.
    /// </para>
    /// </summary>
    /// <para>
    /// In the non-parallel collection because one test redirects <c>FSOEnvironment.UserDir</c> to
    /// a throwaway folder - a process-global, and one that every other test that touches a save
    /// path reads. See <see cref="NonParallelCollection"/>;
    /// <c>GlobalMutationGuardTests</c> checks the membership.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class NeighbourhoodManagementPanelTests
    {
        /// <summary>A rooted user directory. Nothing here reads it - only the path arithmetic does.</summary>
        private static readonly string UserDir =
            Path.Combine(Path.GetTempPath(), "simitone-nhood-panel").Replace('\\', '/') + "/";

        private static IEnumerable<int> EverySlot()
        {
            for (var id = NeighbourhoodManager.MinSlotId; id <= NeighbourhoodManager.MaxSlotId; id++)
            {
                yield return id;
            }
        }

        // ---- row titles ---------------------------------------------------------------------

        [Fact]
        public void AnUnrenamedSlotShowsItsDefaultLabel()
        {
            Assert.Equal("Neighbourhood 1", NeighbourhoodPanelModel.RowName(0, null));
            Assert.Equal("Neighbourhood 9", NeighbourhoodPanelModel.RowName(8, null));
        }

        [Fact]
        public void ARenamedSlotShowsThePlayersName()
        {
            Assert.Equal("Springfield", NeighbourhoodPanelModel.RowName(3, "Springfield"));
        }

        /// <summary>
        /// The name comes out of a JSON sidecar a player can hand-edit, so the row goes through the
        /// backend's own sanitiser rather than trusting it - the same definition of "displayable"
        /// the read path uses.
        /// </summary>
        [Fact]
        public void AHandEditedNameIsSanitisedRatherThanShownRaw()
        {
            Assert.Equal("a b", NeighbourhoodPanelModel.RowName(2, "a/b"));
            Assert.Equal("a b", NeighbourhoodPanelModel.RowName(2, "  a   b  "));
        }

        /// <summary>An unusable custom name falls back to the default label, not to an empty row.</summary>
        [Fact]
        public void AnUnusableNameFallsBackToTheDefaultLabel()
        {
            Assert.Equal("Neighbourhood 4", NeighbourhoodPanelModel.RowName(3, "///"));
            Assert.Equal("Neighbourhood 4", NeighbourhoodPanelModel.RowName(3, "   "));
        }

        [Fact]
        public void ThereIsNoRowTitleForASlotThatDoesNotExist()
        {
            Assert.Null(NeighbourhoodPanelModel.RowName(-1, "Springfield"));
            Assert.Null(NeighbourhoodPanelModel.RowName(9, "Springfield"));
        }

        /// <summary>
        /// Nine rows with nine titles: the page is useless if two of them read the same, and a
        /// numbering slip in <c>DefaultSlotName</c> would be invisible otherwise.
        /// </summary>
        [Fact]
        public void EverySlotHasItsOwnTitle()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in EverySlot())
            {
                Assert.True(seen.Add(NeighbourhoodPanelModel.RowName(id, null)),
                    "two slots share the title '" + NeighbourhoodPanelModel.RowName(id, null) + "'");
            }
        }

        // ---- row areas ----------------------------------------------------------------------

        [Theory]
        [InlineData(0, "Residential")]
        [InlineData(1, "Downtown")]
        [InlineData(2, "Magictown")]
        [InlineData(3, "Studiottown")]
        [InlineData(4, "Vacation")]
        [InlineData(5, "Community")]
        public void SlotsWithADedicatedAreaSayWhich(int id, string expected)
        {
            Assert.Equal(expected, NeighbourhoodPanelModel.RowArea(id));
        }

        /// <summary>
        /// Slots 7..9 have no area template in a The Sims install. The row says so rather than
        /// showing a blank, because a blank is indistinguishable from a bug.
        /// </summary>
        [Theory]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void SlotsWithNoAreaTemplateSaySo(int id)
        {
            Assert.Equal(NeighbourhoodPanelModel.NoAreaTemplate, NeighbourhoodPanelModel.RowArea(id));
        }

        [Fact]
        public void ThereIsNoAreaForASlotThatDoesNotExist()
        {
            Assert.Null(NeighbourhoodPanelModel.RowArea(-1));
            Assert.Null(NeighbourhoodPanelModel.RowArea(9));
        }

        // ---- row state ----------------------------------------------------------------------

        [Fact]
        public void AStateIsInUseOnlyWhenTheSlotHasAFolder()
        {
            Assert.Equal(NeighbourhoodPanelModel.StateInUse, NeighbourhoodPanelModel.RowState(true));
            Assert.Equal(NeighbourhoodPanelModel.StateMissing, NeighbourhoodPanelModel.RowState(false));
        }

        // ---- row detail ---------------------------------------------------------------------

        [Fact]
        public void ADetailLineNamesTheFolderTheAreaAndTheState()
        {
            Assert.Equal("UserData2  -  Downtown  -  " + NeighbourhoodPanelModel.StateInUse,
                NeighbourhoodPanelModel.RowDetail(1, null, true));
        }

        [Fact]
        public void ADetailLineSaysNotCreatedForAFreeSlot()
        {
            Assert.Equal("UserData3  -  Magictown  -  " + NeighbourhoodPanelModel.StateMissing,
                NeighbourhoodPanelModel.RowDetail(2, null, false));
        }

        /// <summary>
        /// The primary's folder is the bare "UserData" - the one slot whose name is not the
        /// pattern, and the one a player is most likely to go looking for on disk.
        /// </summary>
        [Fact]
        public void ThePrimarySlotSaysUserDataAndNotUserData1()
        {
            Assert.Equal("UserData  -  Residential  -  " + NeighbourhoodPanelModel.StateInUse,
                NeighbourhoodPanelModel.RowDetail(0, null, true));
        }

        [Fact]
        public void ARenamedSlotIsMarkedAsNamed()
        {
            Assert.Contains(NeighbourhoodPanelModel.RenamedMarker, NeighbourhoodPanelModel.RowDetail(3, "Springfield", true));
        }

        [Fact]
        public void AnUnrenamedSlotIsNotMarkedAsNamed()
        {
            Assert.DoesNotContain(NeighbourhoodPanelModel.RenamedMarker, NeighbourhoodPanelModel.RowDetail(3, null, true));
        }

        [Fact]
        public void TheOpenSlotIsMarkedAsOpen()
        {
            Assert.Contains(NeighbourhoodPanelModel.LoadedMarker, NeighbourhoodPanelModel.RowDetail(3, null, true, true));
        }

        [Fact]
        public void OnlyTheOpenSlotIsMarkedAsOpen()
        {
            Assert.DoesNotContain(NeighbourhoodPanelModel.LoadedMarker, NeighbourhoodPanelModel.RowDetail(4, null, true, false));
        }

        [Fact]
        public void ADetailLineCanCarryBothMarkersAtOnce()
        {
            var detail = NeighbourhoodPanelModel.RowDetail(3, "Springfield", true, true);
            Assert.Contains(NeighbourhoodPanelModel.RenamedMarker, detail);
            Assert.Contains(NeighbourhoodPanelModel.LoadedMarker, detail);
        }

        [Fact]
        public void ThereIsNoDetailLineForASlotThatDoesNotExist()
        {
            Assert.Null(NeighbourhoodPanelModel.RowDetail(-1, "Springfield", true, true));
            Assert.Null(NeighbourhoodPanelModel.RowDetail(9, null, true));
        }

        /// <summary>
        /// Every slot's detail line leads with its own folder, so a player reading their
        /// <c>Documents\Simitone</c> directory can match a row to a folder. This is the one place
        /// the slot convention is visible in the UI, so it is asserted for all nine.
        /// </summary>
        [Fact]
        public void EveryDetailLineLeadsWithThatSlotsOwnFolder()
        {
            foreach (var id in EverySlot())
            {
                var parts = NeighbourhoodPanelModel.RowDetail(id, null, true)
                    .Split(new[] { NeighbourhoodPanelModel.Separator }, StringSplitOptions.None);

                Assert.Equal(NeighbourhoodManager.SlotFolderName(id), parts[0]);
                Assert.Equal(3, parts.Length);
            }
        }

        /// <summary>
        /// The name in a row is the one string that came off disk, so it must not be able to carry a
        /// separator or a control character into a label.
        /// </summary>
        [Fact]
        public void NoRowTitleCanCarryAPathSeparatorOrAControlCharacter()
        {
            var hostile = new[] { "a/b", "a\\b", "a\nb", "a\tb", "a\u0001b", "a:b", "a*b" };
            foreach (var raw in hostile)
            {
                foreach (var id in EverySlot())
                {
                    var title = NeighbourhoodPanelModel.RowName(id, raw);
                    Assert.DoesNotContain("/", title);
                    Assert.DoesNotContain("\\", title);
                    foreach (var c in title)
                    {
                        Assert.False(char.IsControl(c), "the title '" + title + "' carries a control character");
                    }
                }
            }
        }

        // ---- which slot is open -------------------------------------------------------------

        [Fact]
        public void TheOpenSlotIsFoundFromTheProvidersOwnPath()
        {
            foreach (var id in EverySlot())
            {
                var providerPath = NeighbourhoodManager.SlotPath(UserDir, id) + "/";
                Assert.Equal((int?)id, NeighbourhoodPanelModel.LoadedSlotId(providerPath, UserDir));
            }
        }

        /// <summary>
        /// The provider builds its path with <c>Path.Combine(FSOEnvironment.UserDir, udName + "/")</c>
        /// while <c>UserDir</c> itself arrives with forward slashes, so the two sides routinely
        /// disagree about separators and the provider's always has a trailing one. Missing that
        /// would leave the open slot looking like any other and re-enable Delete on it.
        /// </summary>
        [Fact]
        public void TheOpenSlotIsFoundThroughMixedSeparatorsAndATrailingSlash()
        {
            var providerPath = NeighbourhoodManager.SlotPath(UserDir, 5) + "/";
            Assert.Contains("/", providerPath);
            Assert.Equal(5, NeighbourhoodPanelModel.LoadedSlotId(providerPath, UserDir));
        }

        [Fact]
        public void AnUntrailingProviderPathIsAlsoRecognised()
        {
            Assert.Equal(2, NeighbourhoodPanelModel.LoadedSlotId(
                NeighbourhoodManager.SlotPath(UserDir, 2), UserDir));
        }

        [Fact]
        public void NoOpenSlotIsReportedWhenContentIsNotUp()
        {
            Assert.Null(NeighbourhoodPanelModel.LoadedSlotId(null, UserDir));
        }

        [Fact]
        public void NoOpenSlotIsReportedWhenThereIsNoUserDirectory()
        {
            Assert.Null(NeighbourhoodPanelModel.LoadedSlotId(
                NeighbourhoodManager.SlotPath(UserDir, 1), null));
            Assert.Null(NeighbourhoodPanelModel.LoadedSlotId(
                NeighbourhoodManager.SlotPath(UserDir, 1), ""));
        }

        /// <summary>
        /// A path that names no slot must not be guessed at. Treating an unknown path as the
        /// primary would permanently disable Delete on the one slot that is never deletable anyway,
        /// and treating it as slot 1 would be worse.
        /// </summary>
        [Fact]
        public void APathThatNamesNoSlotReportsNoOpenSlot()
        {
            Assert.Null(NeighbourhoodPanelModel.LoadedSlotId(Path.Combine(UserDir, "Neighbourhoods"), UserDir));
            Assert.Null(NeighbourhoodPanelModel.LoadedSlotId(UserDir, UserDir));
        }

        // ---- the initial selection -----------------------------------------------------------

        [Fact]
        public void TheFirstExistingSlotIsSelectedOnOpen()
        {
            var exists = new[] { false, true, false, true, false, false, false, false, false };
            Assert.Equal(1, NeighbourhoodPanelModel.InitialSelection(exists));
        }

        [Fact]
        public void TheFirstEmptySlotIsSelectedWhenNothingExistsYet()
        {
            Assert.Equal(0, NeighbourhoodPanelModel.InitialSelection(new bool[9]));
        }

        [Fact]
        public void ThePrimaryIsSelectedWhenEverySlotIsInUse()
        {
            Assert.Equal(NeighbourhoodManager.PrimarySlotId,
                NeighbourhoodPanelModel.InitialSelection(new bool[] { true, true, true, true, true, true, true, true, true }));
        }

        [Fact]
        public void ThePrimaryIsSelectedWhenThereIsNothingToGoOn()
        {
            Assert.Equal(NeighbourhoodManager.PrimarySlotId, NeighbourhoodPanelModel.InitialSelection(null));
            Assert.Equal(NeighbourhoodManager.PrimarySlotId, NeighbourhoodPanelModel.InitialSelection(new bool[0]));
        }

        /// <summary>Whatever the state, the page opens on a slot it can actually do things to.</summary>
        [Fact]
        public void TheInitialSelectionIsAlwaysAKnownSlot()
        {
            var states = new[]
            {
                new bool[9], new bool[] { true },
                new bool[] { true, true, true, true, true, true, true, true, true },
                new bool[] { false, false, false, false, false, false, false, false, true }
            };

            foreach (var state in states)
            {
                Assert.True(NeighbourhoodManager.IsKnownSlot(NeighbourhoodPanelModel.InitialSelection(state)));
            }
        }

        // ---- New (blank) ---------------------------------------------------------------------

        [Fact]
        public void ANewNeighbourhoodIsOfferedWhileAnySlotIsFree()
        {
            foreach (var id in EverySlot())
            {
                Assert.True(NeighbourhoodPanelModel.CanCreateNew(id));
            }
        }

        /// <summary>
        /// <c>FirstEmptySlot</c> answers <c>-1</c> when all nine are in use, and that is the only
        /// value the button has to refuse.
        /// </summary>
        [Fact]
        public void ANewNeighbourhoodIsNotOfferedWhenAllNineAreInUse()
        {
            Assert.False(NeighbourhoodPanelModel.CanCreateNew(-1));
        }

        [Fact]
        public void ANewNeighbourhoodIsNotOfferedForAnImpossibleSlot()
        {
            Assert.False(NeighbourhoodPanelModel.CanCreateNew(9));
            Assert.False(NeighbourhoodPanelModel.CanCreateNew(-2));
        }

        // ---- Copy from TS1 --------------------------------------------------------------------

        [Fact]
        public void ACopyIsOfferedWhenAnInstallIsKnown()
        {
            Assert.True(NeighbourhoodPanelModel.CanCopyFromInstall(0, "D:/Games/The Sims/"));
        }

        /// <summary>
        /// The backend has no default for <c>CopyFromInstall</c>, so a build that never resolved an
        /// install cannot copy at all. The button is off with
        /// <see cref="NeighbourhoodPanelModel.NoInstallPath"/> rather than on and failing.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ACopyIsNotOfferedWithoutAnInstallPath(string install)
        {
            Assert.False(NeighbourhoodPanelModel.CanCopyFromInstall(1, install));
        }

        [Fact]
        public void ACopyIsNotOfferedForASlotThatDoesNotExist()
        {
            Assert.False(NeighbourhoodPanelModel.CanCopyFromInstall(9, "D:/Games/The Sims/"));
        }

        // ---- Rename / Reset name ---------------------------------------------------------------

        [Fact]
        public void AnExistingSlotCanBeRenamed()
        {
            Assert.True(NeighbourhoodPanelModel.CanRename(3, true));
        }

        [Fact]
        public void ASlotThatDoesNotExistCannotBeRenamed()
        {
            Assert.False(NeighbourhoodPanelModel.CanRename(3, false));
        }

        [Fact]
        public void AnImpossibleSlotCannotBeRenamed()
        {
            Assert.False(NeighbourhoodPanelModel.CanRename(-1, true));
            Assert.False(NeighbourhoodPanelModel.CanRename(9, true));
        }

        [Fact]
        public void ANamedSlotCanHaveItsNameReset()
        {
            Assert.True(NeighbourhoodPanelModel.CanResetName(3, "Springfield"));
        }

        /// <summary>Resetting nothing would rewrite the sidecar to the file it already had.</summary>
        [Fact]
        public void AnUnnamedSlotCannotHaveItsNameReset()
        {
            Assert.False(NeighbourhoodPanelModel.CanResetName(3, null));
            Assert.False(NeighbourhoodPanelModel.CanResetName(3, ""));
        }

        [Fact]
        public void AnImpossibleSlotCannotHaveItsNameReset()
        {
            Assert.False(NeighbourhoodPanelModel.CanResetName(9, "Springfield"));
        }

        // ---- Delete ---------------------------------------------------------------------------

        [Fact]
        public void AnOrdinaryUnusedSlotCanBeDeleted()
        {
            Assert.True(NeighbourhoodPanelModel.CanDelete(3, null));
            Assert.True(NeighbourhoodPanelModel.CanDelete(3, 1));
        }

        /// <summary>
        /// The primary is what Simitone booted from, and the backend refuses it outright. The button
        /// has to be off before it is pressed, and the reason has to be visible.
        /// </summary>
        [Fact]
        public void ThePrimarySlotCanNeverBeDeleted()
        {
            Assert.False(NeighbourhoodPanelModel.CanDelete(NeighbourhoodManager.PrimarySlotId, null));
            Assert.False(NeighbourhoodPanelModel.CanDelete(NeighbourhoodManager.PrimarySlotId, 4));
        }

        [Fact]
        public void ThePrimarySlotsRefusalSaysWhy()
        {
            var refusal = NeighbourhoodPanelModel.DeleteRefusal(NeighbourhoodManager.PrimarySlotId, null);
            Assert.NotNull(refusal);
            Assert.Contains("starts from", refusal);
        }

        /// <summary>The open neighbourhood has its files held by the session, so it is protected too.</summary>
        [Fact]
        public void TheOpenSlotCanNeverBeDeleted()
        {
            foreach (var id in EverySlot())
            {
                Assert.False(NeighbourhoodPanelModel.CanDelete(id, id));
            }
        }

        [Fact]
        public void TheOpenSlotsRefusalSaysWhy()
        {
            var refusal = NeighbourhoodPanelModel.DeleteRefusal(4, 4);
            Assert.NotNull(refusal);
            Assert.Contains("open right now", refusal);
        }

        [Fact]
        public void AnImpossibleSlotCanNeverBeDeleted()
        {
            Assert.False(NeighbourhoodPanelModel.CanDelete(-1, null));
            Assert.False(NeighbourhoodPanelModel.CanDelete(9, null));
        }

        /// <summary>
        /// The whole state table, because the rule is "not the primary and not the open one" and a
        /// pair of spot checks would not catch an <c>||</c> that had become an <c>&amp;&amp;</c>.
        /// </summary>
        [Fact]
        public void DeleteIsOfferedExactlyForTheSlotsThatAreNeitherPrimaryNorOpen()
        {
            foreach (var id in EverySlot())
            {
                foreach (var loaded in EverySlot())
                {
                    var expected = id != NeighbourhoodManager.PrimarySlotId && id != loaded;
                    Assert.Equal(expected, NeighbourhoodPanelModel.CanDelete(id, loaded));
                }
            }
        }

        [Fact]
        public void ADeleteRefusalIsAlwaysExplained()
        {
            foreach (var id in new[] { -1, 0, 9 })
            {
                foreach (var loaded in new int?[] { null, 0, 4 })
                {
                    var refusal = NeighbourhoodPanelModel.DeleteRefusal(id, loaded);
                    Assert.False(NeighbourhoodPanelModel.CanDelete(id, loaded));
                    Assert.False(string.IsNullOrWhiteSpace(refusal));
                }
            }
        }

        [Fact]
        public void ThereIsNoRefusalForASlotThatCanBeDeleted()
        {
            Assert.Null(NeighbourhoodPanelModel.DeleteRefusal(3, null));
            Assert.Null(NeighbourhoodPanelModel.DeleteRefusal(3, 5));
        }

        // ---- the action column's button set -----------------------------------------------------

        /// <summary>
        /// The seven controls the page offers, in the order they are stacked: the six actions on
        /// the selected slot, then the footer's Close.
        /// </summary>
        /// <remarks>
        /// This is the button set the page builds its column from, asserted as a set rather than
        /// as six constructor lines. The page used to write its captions and its plate colour
        /// straight into a <c>UIButton</c> per action, which meant no test could see the column at
        /// all: a dropped action, a reordered column, or a caption that no longer matched its
        /// string-table id would all have shown up only by playing the game.
        /// </remarks>
        [Fact]
        public void TheColumnOffersTheSevenButtonsItIsDocumentedTo()
        {
            var captions = new List<string>();
            foreach (var entry in NeighbourhoodPanelModel.ActionButtons)
            {
                captions.Add(entry.Caption);
            }
            captions.Add(NeighbourhoodPanelModel.CloseCaption);

            Assert.Equal(new[] { "Open", "New (blank)", "Copy from TS1", "Rename...", "Reset name", "Delete", "Close" },
                captions);
        }

        /// <summary>
        /// The action enum and the button table are one list, not two. A seventh action in the
        /// enum with no button would be a rule with nothing to press it; a button with no action
        /// would be a control the page cannot re-gate when the selection changes.
        /// </summary>
        [Fact]
        public void EveryActionHasExactlyOneButtonAndEveryButtonHasAnAction()
        {
            var declared = Enum.GetValues(typeof(NeighbourhoodPanelModel.NeighbourhoodAction));
            var seen = new HashSet<NeighbourhoodPanelModel.NeighbourhoodAction>();

            foreach (var entry in NeighbourhoodPanelModel.ActionButtons)
            {
                Assert.True(seen.Add(entry.Action),
                    "two buttons claim the action " + entry.Action);
            }

            Assert.Equal(declared.Length, seen.Count);
            foreach (NeighbourhoodPanelModel.NeighbourhoodAction action in declared)
            {
                Assert.True(seen.Contains(action), "the action " + action + " has no button");
            }
        }

        /// <summary>
        /// The table is written in stacking order, and the page indexes its buttons by the action's
        /// numeric value - so the table's order has to be the enum's declaration order or the
        /// column draws its buttons in a different order to the one the layout assumes.
        /// </summary>
        [Fact]
        public void TheButtonsAreListedInTheOrderTheyAreStacked()
        {
            var declared = (NeighbourhoodPanelModel.NeighbourhoodAction[])
                Enum.GetValues(typeof(NeighbourhoodPanelModel.NeighbourhoodAction));

            Assert.Equal(declared.Length, NeighbourhoodPanelModel.ActionButtons.Count);
            for (var i = 0; i < declared.Length; i++)
            {
                Assert.Equal(declared[i], NeighbourhoodPanelModel.ActionButtons[i].Action);
            }
        }

        /// <summary>
        /// Every button says something, and says it in its own words. A blank caption is a control
        /// the player can only guess at, and two buttons reading the same is a column where
        /// picking the wrong one is easy.
        /// </summary>
        [Fact]
        public void EveryButtonHasItsOwnNonBlankCaption()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in NeighbourhoodPanelModel.ActionButtons)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Caption),
                    "the " + entry.Action + " button has no caption");
                Assert.True(seen.Add(entry.Caption), "two buttons both read '" + entry.Caption + "'");
            }

            Assert.True(seen.Add(NeighbourhoodPanelModel.CloseCaption),
                "Close reads the same as an action button");
        }

        /// <summary>
        /// Close is not one of the six actions. It acts on the page rather than on the selected
        /// slot, it is never greyed out, and the layout's whole job is to stop the column pushing
        /// into it - so counting it as a seventh action would put it inside the arithmetic that
        /// exists to keep it clear.
        /// </summary>
        [Fact]
        public void CloseIsNotOneOfTheActions()
        {
            Assert.Equal(6, NeighbourhoodPanelModel.ActionCount);
            Assert.NotEqual("Close", NeighbourhoodPanelModel.ActionButtons[0].Caption);
            Assert.NotEqual("Close", NeighbourhoodPanelModel.ActionButtons[5].Caption);
        }

        /// <summary>
        /// The column's height is a function of how many buttons it holds, and the layout is given
        /// that count. This pins the two to the same number: were the count to come from anywhere
        /// but the button table, a seventh button would be drawn outside the space the layout
        /// reserved and the arithmetic's "never reaches the footer" guarantee would stop meaning
        /// anything.
        /// </summary>
        [Fact]
        public void TheColumnIsAsTallAsTheButtonTableSaysItIs()
        {
            var count = NeighbourhoodPanelModel.ActionCount;
            Assert.Equal(NeighbourhoodPanelModel.ActionButtons.Count, count);

            foreach (var desktop in new[] { true, false })
            {
                var l = LayoutFor(desktop);
                var v = NeighbourhoodPanelLayout.Arrange(4000, 96, 96, l[0], 10, count, l[2], l[1]);

                Assert.Equal(96 + (count * (l[0] + 10)), v.ActionBottom);
            }
        }

        // ---- the rename gate --------------------------------------------------------------------

        /// <summary>
        /// The OK button is gated on the backend's own predicate. Anything that diverges is a
        /// button that either refuses a name the backend would take, or accepts one it would not.
        /// </summary>
        [Theory]
        [InlineData("Springfield")]
        [InlineData("Springfield - The Simpons")]
        [InlineData("Springfield.KS")]
        [InlineData(".hidden")]
        [InlineData("a b")]
        [InlineData("123456789012345678901234567890123456789012345678")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("Springfield/Ogdenville")]
        [InlineData("Springfield\\Ogdenville")]
        [InlineData("Springfield:Ogdenville")]
        [InlineData("Springfield*")]
        [InlineData("Springfield?")]
        [InlineData("Springfield\"")]
        [InlineData("Springfield<")]
        [InlineData("Springfield>")]
        [InlineData("Springfield|")]
        [InlineData("Springfield\n")]
        [InlineData("Springfield\u0001")]
        [InlineData("1234567890123456789012345678901234567890123456789")]
        public void TheRenameGateIsExactlyTheBackendsPredicate(string typed)
        {
            Assert.Equal(NeighbourhoodManager.IsValidName(typed), NeighbourhoodPanelModel.CanAcceptRename(typed));
        }

        /// <summary>A rejected name always comes with something to read, and never with nothing.</summary>
        [Fact]
        public void EveryRefusedNameIsExplained()
        {
            var corpus = new[]
            {
                null, "", "   ", ".", "..", "a/b", "a\\b", "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b",
                "a\nb", "a\u0001b", "a\u007f", new string('x', 49)
            };

            foreach (var typed in corpus)
            {
                if (NeighbourhoodPanelModel.CanAcceptRename(typed))
                {
                    Assert.Null(NeighbourhoodPanelModel.RenameRejection(typed));
                    continue;
                }

                var rejection = NeighbourhoodPanelModel.RenameRejection(typed);
                Assert.False(string.IsNullOrWhiteSpace(rejection));
            }
        }

        [Fact]
        public void AnAcceptableNameHasNoRejection()
        {
            Assert.Null(NeighbourhoodPanelModel.RenameRejection("Springfield"));
        }

        [Fact]
        public void AnEmptyFieldAsksForAName()
        {
            Assert.Equal(NeighbourhoodPanelModel.RenameRejection(null), NeighbourhoodPanelModel.RenameRejection("   "));
        }

        [Fact]
        public void TheRelativePathSpellingsAreCalledOut()
        {
            Assert.Equal(NeighbourhoodPanelModel.RenameRejection("."), NeighbourhoodPanelModel.RenameRejection(".."));
        }

        /// <summary>
        /// The message names the length rule, so a player who cannot type a name is told the limit
        /// rather than just being stopped.
        /// </summary>
        [Fact]
        public void AnOverlongNameSaysHowLongIsTooLong()
        {
            var rejection = NeighbourhoodPanelModel.RenameRejection(new string('x', 49));
            Assert.Contains(NeighbourhoodManager.MaxNameLength.ToString(), rejection);
        }

        [Fact]
        public void ASeparatorIsCalledOut()
        {
            Assert.Equal(NeighbourhoodPanelModel.RenameRejection("a/b"), NeighbourhoodPanelModel.RenameRejection("a\\b"));
        }

        /// <summary>Every reserved character produces the same message, naming the whole set.</summary>
        [Fact]
        public void EveryReservedCharacterProducesTheSameMessage()
        {
            var expected = NeighbourhoodPanelModel.RenameRejection("a:b");
            foreach (var c in NeighbourhoodPanelModel.ReservedNameChars)
            {
                Assert.Equal(expected, NeighbourhoodPanelModel.RenameRejection("a" + c + "b"));
            }
            Assert.Contains(NeighbourhoodPanelModel.ReservedNameChars, expected);
        }

        /// <summary>
        /// The character list in the message and the character list the backend refuses are one
        /// rule. If the backend ever grows a character and the message does not, the player is told
        /// about the wrong one.
        /// </summary>
        [Fact]
        public void EveryReservedCharacterIsRefusedByTheBackend()
        {
            foreach (var c in NeighbourhoodPanelModel.ReservedNameChars)
            {
                Assert.False(NeighbourhoodManager.IsValidName("a" + c + "b"));
            }
        }

        // ---- status lines ---------------------------------------------------------------------

        [Theory]
        [InlineData(0, "Created UserData.")]
        [InlineData(3, "Created UserData4.")]
        [InlineData(8, "Created UserData9.")]
        public void ABlankCreationSaysWhichFolderWasMade(int id, string expected)
        {
            Assert.Equal(expected, NeighbourhoodPanelModel.CreatedBlank(id));
        }

        [Fact]
        public void ACopySaysWhereItCameFrom()
        {
            Assert.Equal("Copied UserData4 from The Sims.", NeighbourhoodPanelModel.CopiedFromInstall(3));
        }

        [Fact]
        public void ADeletionSaysWhichFolderWent()
        {
            Assert.Equal("Deleted UserData4.", NeighbourhoodPanelModel.DeletedSlot(3));
        }

        [Fact]
        public void ARenameSaysWhatTheSlotIsNowCalled()
        {
            Assert.Equal("Renamed UserData4 to Springfield.", NeighbourhoodPanelModel.RenamedSlot(3, "Springfield"));
        }

        [Fact]
        public void ANameResetSaysTheDefaultIsBack()
        {
            Assert.Equal("UserData4 is back to its default name.", NeighbourhoodPanelModel.NameReset(3));
        }

        /// <summary>
        /// A status line for an id that is not a slot still has to say something, because the status
        /// is the only feedback a refused operation gives.
        /// </summary>
        [Fact]
        public void AStatusLineForAnImpossibleSlotStillSaysSomething()
        {
            Assert.Contains("slot 99", NeighbourhoodPanelModel.CreatedBlank(99));
            Assert.Contains("slot -1", NeighbourhoodPanelModel.DeletedSlot(-1));
        }

        [Fact]
        public void TheFailureStatusPointsAtTheLog()
        {
            Assert.Contains("log", NeighbourhoodPanelModel.ActionFailed);
        }

        /// <summary>
        /// The two whole-page messages are only ever shown when there is genuinely nothing to do,
        /// so they have to be non-empty and have to mention the thing that caused them.
        /// </summary>
        [Fact]
        public void TheWholePageMessagesNameTheirCause()
        {
            Assert.Contains("nine", NeighbourhoodPanelModel.AllSlotsInUse);
            Assert.Contains("install", NeighbourhoodPanelModel.NoInstallPath);
        }

        // ---- Open (switching neighbourhoods) ---------------------------------------------------

        /// <summary>An ordinary slot, with a known and different neighbourhood open, is openable.</summary>
        [Fact]
        public void AnOrdinarySlotCanBeOpened()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0);
            Assert.True(NeighbourhoodPanelModel.CanOpen(facts));
            Assert.Null(NeighbourhoodPanelModel.OpenRefusal(facts));
        }

        /// <summary>
        /// The whole state table for the two facts that decide it: "the slot has a folder the
        /// provider could open" and "it is not the one already open". Two spot checks would not
        /// catch a <c>||</c> that had become an <c>&amp;&amp;</c>, and a sign slip here is a button
        /// that either discards a live session or is greyed out for no reason.
        /// </summary>
        [Fact]
        public void OpenIsOfferedExactlyForTheSlotsThatExistAreOpenableAndAreNotTheOpenOne()
        {
            foreach (var id in EverySlot())
            {
                foreach (var loaded in EverySlot())
                {
                    foreach (var openable in new[] { true, false })
                    {
                        var facts = new NeighbourhoodOpenFacts(id, true, openable, loaded);
                        var expected = openable && id != loaded;
                        Assert.Equal(expected, NeighbourhoodPanelModel.CanOpen(facts));
                    }
                }
            }
        }

        /// <summary>The open neighbourhood cannot be re-opened - it is the one being closed.</summary>
        [Fact]
        public void TheOpenSlotCanNeverBeOpened()
        {
            foreach (var id in EverySlot())
            {
                var facts = new NeighbourhoodOpenFacts(id, true, true, id);
                Assert.False(NeighbourhoodPanelModel.CanOpen(facts));
                Assert.False(string.IsNullOrWhiteSpace(NeighbourhoodPanelModel.OpenRefusal(facts)));
            }
        }

        [Fact]
        public void AFreeSlotCannotBeOpened()
        {
            Assert.False(NeighbourhoodPanelModel.CanOpen(new NeighbourhoodOpenFacts(3, false, false, 0)));
        }

        /// <summary>
        /// A folder that is not a real neighbourhood would be discovered by
        /// <c>InitSpecific</c> throwing on its first line of IFF loading, with the open
        /// neighbourhood already half torn down. The page has to catch that before it starts.
        /// </summary>
        [Fact]
        public void AFolderThatIsNotANeighbourhoodCannotBeOpened()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, false, 0);
            Assert.False(NeighbourhoodPanelModel.CanOpen(facts));
            Assert.Contains(TS1UserData.NeighborhoodResourceName,
                NeighbourhoodPanelModel.OpenRefusal(facts));
        }

        /// <summary>
        /// <c>null</c> is "we could not work out what is open", not "nothing is open". Switching
        /// then would discard a neighbourhood whose folder we cannot name, and a failed switch
        /// would have nothing to put back.
        /// </summary>
        [Fact]
        public void NothingIsOpenedWhenNoSlotIsKnownToBeOpen()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, null);
            Assert.False(NeighbourhoodPanelModel.CanOpen(facts));
            Assert.Contains("cannot tell", NeighbourhoodPanelModel.OpenRefusal(facts));
        }

        /// <summary>
        /// Switching underneath a live lot would swap out the very data it was loaded from while
        /// its VM is still holding it.
        /// </summary>
        [Fact]
        public void NothingIsOpenedWhileALotIsLoaded()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0, inLot: true);
            Assert.False(NeighbourhoodPanelModel.CanOpen(facts));
            Assert.Contains("lot", NeighbourhoodPanelModel.OpenRefusal(facts));
        }

        /// <summary>
        /// A pending move-in would buy the next lot picked - in the neighbourhood that was just
        /// opened - for a family of the one that was closed.
        /// </summary>
        [Fact]
        public void NothingIsOpenedWhileAFamilyIsWaitingToMoveIn()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0, moveInPending: true);
            Assert.False(NeighbourhoodPanelModel.CanOpen(facts));
            Assert.Contains("move in", NeighbourhoodPanelModel.OpenRefusal(facts));
        }

        [Fact]
        public void AnImpossibleSlotCanNeverBeOpened()
        {
            Assert.False(NeighbourhoodPanelModel.CanOpen(new NeighbourhoodOpenFacts(-1, true, true, 0)));
            Assert.False(NeighbourhoodPanelModel.CanOpen(new NeighbourhoodOpenFacts(9, true, true, 0)));
        }

        /// <summary>
        /// Every way a switch can be refused has to say why. A greyed-out button on its own is not
        /// something a player can act on, and this one is the only button on the page that can
        /// refuse for six different reasons.
        /// </summary>
        [Fact]
        public void EveryOpenRefusalIsExplained()
        {
            var refusals = new[]
            {
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(-1, true, true, 0)),
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(9, true, true, 0)),
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, false, false, 0)),
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, true, null)),
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, true, 3)),
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, false, 0)),
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, true, 0, inLot: true)),
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, true, 0, moveInPending: true)),
            };

            foreach (var refusal in refusals)
            {
                Assert.False(string.IsNullOrWhiteSpace(refusal));
            }
        }

        /// <summary>
        /// A slot with no folder is the most ordinary refusal there is, and it must not be
        /// reported as the pathological "we cannot tell which one is open" - the player would go
        /// looking in the log for a fault that is not there.
        /// </summary>
        [Fact]
        public void AFreeSlotSaysItIsEmptyRatherThanThatSomethingIsBroken()
        {
            var refusal = NeighbourhoodPanelModel.OpenRefusal(
                new NeighbourhoodOpenFacts(3, false, false, null));
            Assert.DoesNotContain("cannot tell", refusal);
        }

        [Fact]
        public void TheOpenSlotsRefusalSaysItIsAlreadyOpen()
        {
            var refusal = NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(4, true, true, 4));
            Assert.Contains("open right now", refusal);
        }

        /// <summary>
        /// The button and the reason have to be the same question, asked twice: a page that
        /// greyed the button out without a refusal (or the reverse) would leave the player with a
        /// dead button and no idea why.
        /// </summary>
        [Fact]
        public void TheOpenButtonIsEnabledExactlyWhenThereIsNoRefusal()
        {
            foreach (var id in new[] { -1, 0, 3, 9 })
            {
                foreach (var loaded in new int?[] { null, 0, 3 })
                {
                    foreach (var exists in new[] { true, false })
                    {
                        foreach (var openable in new[] { true, false })
                        {
                            foreach (var inLot in new[] { true, false })
                            {
                                foreach (var moving in new[] { true, false })
                                {
                                    var facts = new NeighbourhoodOpenFacts(
                                        id, exists, openable, loaded, inLot, moving);
                                    Assert.Equal(NeighbourhoodPanelModel.OpenRefusal(facts) == null,
                                        NeighbourhoodPanelModel.CanOpen(facts));
                                }
                            }
                        }
                    }
                }
            }
        }

        // ---- the save-before-switch rule -------------------------------------------------------

        [Fact]
        public void ASaveIsOfferedWhenTheOpenNeighbourhoodHasUnsavedChanges()
        {
            Assert.True(NeighbourhoodPanelModel.ShouldOfferSave(
                new NeighbourhoodOpenFacts(3, true, true, 0, dirtyAvatars: 1)));
        }

        [Fact]
        public void ASaveIsOfferedWhenALotIsLoaded()
        {
            Assert.True(NeighbourhoodPanelModel.ShouldOfferSave(
                new NeighbourhoodOpenFacts(3, true, true, 0, inLot: true)));
        }

        /// <summary>
        /// Nothing unsaved means nothing to offer. A "save and open" button on a clean
        /// neighbourhood would be a lie about there being something at stake.
        /// </summary>
        [Fact]
        public void ASaveIsNotOfferedWhenThereIsNothingToLose()
        {
            Assert.False(NeighbourhoodPanelModel.ShouldOfferSave(
                new NeighbourhoodOpenFacts(3, true, true, 0)));
            Assert.False(NeighbourhoodPanelModel.ShouldOfferSave(
                new NeighbourhoodOpenFacts(3, true, true, 0, dirtyAvatars: 0)));
        }

        [Fact]
        public void ASaveIsOfferedExactlyWhenThereIsSomethingUnsaved()
        {
            foreach (var inLot in new[] { true, false })
            {
                foreach (var moving in new[] { true, false })
                {
                    foreach (var dirty in new[] { 0, 1, 4, 100 })
                    {
                        var facts = new NeighbourhoodOpenFacts(3, true, true, 0, inLot, moving, dirty);
                        Assert.Equal(inLot || dirty > 0, NeighbourhoodPanelModel.ShouldOfferSave(facts));
                    }
                }
            }
        }

        // ---- the confirmation's wording --------------------------------------------------------

        [Fact]
        public void TheConfirmationNamesTheSlotItIsOpening()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0);
            var message = NeighbourhoodPanelModel.OpenConfirmMessage(facts, "Springfield", "Old Town");
            Assert.Contains("Springfield", message);
        }

        /// <summary>
        /// The confirmation's whole job is the warning, so both wordings carry it. The version
        /// that cannot name the open neighbourhood is the version a player would misread as "it
        /// is only changing a view".
        /// </summary>
        [Fact]
        public void TheConfirmationAlwaysWarnsThatTheOpenNeighbourhoodIsLost()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0);
            foreach (var loaded in new[] { "Old Town", null, "" })
            {
                var message = NeighbourhoodPanelModel.OpenConfirmMessage(facts, "Springfield", loaded);
                Assert.Contains("closed", message);
                Assert.Contains("not saved", message);
                Assert.Contains("will be lost", message);
            }
        }

        [Fact]
        public void TheConfirmationNamesTheNeighbourhoodThatIsBeingClosedWhenItKnowsIt()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0);
            Assert.Contains("Old Town",
                NeighbourhoodPanelModel.OpenConfirmMessage(facts, "Springfield", "Old Town"));
        }

        /// <summary>
        /// No name to put in the sentence means the sentence has to change shape, not print a
        /// blank where the name goes.
        /// </summary>
        [Fact]
        public void AnUnknownOpenNeighbourhoodLeavesTheSentenceReadable()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0);
            foreach (var loaded in new[] { null, "" })
            {
                var message = NeighbourhoodPanelModel.OpenConfirmMessage(facts, "Springfield", loaded);
                Assert.DoesNotContain(", ,", message);
                Assert.DoesNotContain(",  will", message);
                Assert.Contains("Springfield", message);
            }
        }

        /// <summary>An unnamed target still reads as a sentence.</summary>
        [Fact]
        public void AnUnknownTargetIsNamedAsSomethingRatherThanLeftBlank()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0);
            var message = NeighbourhoodPanelModel.OpenConfirmMessage(facts, null, "Old Town");
            Assert.Contains("this neighbourhood", message);
        }

        [Fact]
        public void TheCleanConfirmationIsJustTheWarning()
        {
            var facts = new NeighbourhoodOpenFacts(3, true, true, 0);
            var message = NeighbourhoodPanelModel.OpenConfirmMessage(facts, "Springfield", "Old Town");
            Assert.Equal(NeighbourhoodPanelModel.OpenConfirmBody
                    .Replace("{0}", "Springfield").Replace("{1}", "Old Town"),
                message);
        }

        /// <summary>
        /// The three answers are only spelled out when there are three, and when they are they
        /// have to name all three: the confirm's own buttons are captioned Yes, No and Cancel, and
        /// "No" meaning "open it anyway" is the trap this paragraph exists to defuse.
        /// </summary>
        [Fact]
        public void TheConfirmationOnlyExplainsTheButtonsWhenASaveIsOnOffer()
        {
            var clean = new NeighbourhoodOpenFacts(3, true, true, 0);
            var dirty = new NeighbourhoodOpenFacts(3, true, true, 0, dirtyAvatars: 2);

            Assert.DoesNotContain("Cancel", NeighbourhoodPanelModel.OpenConfirmMessage(clean, "A", "B"));
            Assert.Contains(NeighbourhoodPanelModel.OpenConfirmSaveGuide,
                NeighbourhoodPanelModel.OpenConfirmMessage(dirty, "A", "B"));
        }

        [Theory]
        [InlineData("Yes")]
        [InlineData("No")]
        [InlineData("Cancel")]
        public void TheSaveGuideNamesEveryAnswerTheConfirmOffers(string answer)
        {
            Assert.Contains(answer, NeighbourhoodPanelModel.OpenConfirmSaveGuide);
        }

        /// <summary>The guide has to be a separate paragraph, or it reads as more warning.</summary>
        [Fact]
        public void TheSaveGuideIsItsOwnParagraph()
        {
            var dirty = new NeighbourhoodOpenFacts(3, true, true, 0, dirtyAvatars: 1);
            var message = NeighbourhoodPanelModel.OpenConfirmMessage(dirty, "Springfield", "Old Town");
            Assert.Contains("\n\n" + NeighbourhoodPanelModel.OpenConfirmSaveGuide, message);
        }

        // ---- what a switch reports --------------------------------------------------------------

        [Theory]
        [InlineData(0, "Opened UserData.")]
        [InlineData(3, "Opened UserData4.")]
        [InlineData(8, "Opened UserData9.")]
        public void AnOpenSaysWhichFolderWasOpened(int id, string expected)
        {
            Assert.Equal(expected, NeighbourhoodPanelModel.OpenedSlot(id));
        }

        [Fact]
        public void AnOpenForAnImpossibleSlotStillSaysSomething()
        {
            Assert.Contains("slot 99", NeighbourhoodPanelModel.OpenedSlot(99));
        }

        /// <summary>
        /// The two failure messages have to be distinguishable, because they promise opposite
        /// things: one says the session is untouched, the other says it is not. A player who is
        /// told "still open" when it is not will go looking for their neighbourhood in the one
        /// place it is not.
        /// </summary>
        [Fact]
        public void TheTwoFailureMessagesPromiseOppositeThings()
        {
            Assert.Contains("still open", NeighbourhoodPanelModel.OpenFailed);
            Assert.DoesNotContain("still open", NeighbourhoodPanelModel.OpenFailedFatal);
            Assert.Contains("Restart", NeighbourhoodPanelModel.OpenFailedFatal);
        }

        /// <summary>
        /// The reason for a failure is in the log - an exception from inside the provider is not
        /// something to put in a one-line dialog - so all four have to point there.
        /// </summary>
        [Fact]
        public void EveryFailureMessagePointsAtTheLog()
        {
            Assert.Contains("log", NeighbourhoodPanelModel.OpenFailed);
            Assert.Contains("log", NeighbourhoodPanelModel.OpenFailedFatal);
            Assert.Contains("log", NeighbourhoodPanelModel.OpenSaveFailed);
            Assert.Contains("log", NeighbourhoodPanelModel.OpenMapFailed);
        }

        /// <summary>
        /// A switch that worked and a map that could not be drawn are not the same failure, and
        /// the message has to keep them apart: "it is still open" is true of the data and false of
        /// what the player is looking at.
        /// </summary>
        [Fact]
        public void AFailedMapIsNotReportedAsAFailedSwitch()
        {
            Assert.Contains("was opened", NeighbourhoodPanelModel.OpenMapFailed);
            Assert.Contains("map could not be drawn", NeighbourhoodPanelModel.OpenMapFailed);
            Assert.DoesNotContain("still open", NeighbourhoodPanelModel.OpenMapFailed);
        }

        /// <summary>
        /// A save that did not happen is not a switch that did not happen: the message has to say
        /// that nothing was switched, or the player will find out later that their changes are
        /// gone.
        /// </summary>
        [Fact]
        public void AFailedSaveSaysThatNothingWasSwitched()
        {
            Assert.Contains("nothing was switched", NeighbourhoodPanelModel.OpenSaveFailed);
        }

        [Fact]
        public void TheSwitchConfirmationsAllShareOneTitle()
        {
            Assert.False(string.IsNullOrWhiteSpace(NeighbourhoodPanelModel.OpenTitle));
        }

        // ---- the page's vertical arrangement ----------------------------------------------------

        /// <summary>
        /// The page's own numbers for one build, so the tests below read as the two builds rather
        /// than as a wall of literals.
        ///
        /// <para>
        /// The button height is not a number the page chose: <c>UIBigButton</c> is a
        /// <c>UIButton</c> and takes its height from its plate, and the two plates are
        /// <c>button.png</c> (485x48) and <c>button.mobile.png</c> (600x100). These are the
        /// artwork's own numbers, and <see cref="TheButtonHeightsAreThePlatesOwnHeights"/> asserts
        /// them against the shipped PNGs rather than trusting this list.
        /// </para>
        /// </summary>
        private static int[] LayoutFor(bool desktop)
        {
            return desktop
                ? new[] { 48, 12, 42, 660 }   // button height, detail size, row height, max height
                : new[] { 100, 22, 70, 960 };
        }

        /// <summary>The height the page is never built shorter than: what its buttons need.</summary>
        private static int FloorFor(bool desktop)
        {
            var l = LayoutFor(desktop);
            return NeighbourhoodPanelLayout.RequiredHeight(96, 96, l[0], 10, NeighbourhoodPanelModel.ActionCount);
        }

        // ---- the plates the buttons are drawn from -------------------------------------------

        /// <summary>
        /// The pixel height of one of the shipped button plates, read out of the PNG's own header.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Parsed from the IHDR chunk rather than through an imaging library: the only two numbers
        /// wanted are the width and the height, they sit at fixed offsets in the first 24 bytes of
        /// any PNG, and a graphics-dependent reader would make a headless test process need a
        /// device just to check a number.
        /// </para>
        ///
        /// <para>
        /// This is the assertion that makes the button heights above trustworthy. A
        /// <c>UIBigButton</c> has no height setter - it draws the plate's own height - so a
        /// constant in the page that stops matching the artwork is not a cosmetic drift: the
        /// column's pitch and the footer's offset are both derived from it, and a plate that grew
        /// would quietly start drawing over the button below it.
        /// </para>
        /// </remarks>
        private static int PlateHeight(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Content", "uigraphics", "common", fileName);
            Assert.True(File.Exists(path), "the shipped plate " + fileName + " is not next to the tests.");

            var header = new byte[24];
            using (var stream = File.OpenRead(path))
            {
                Assert.True(stream.Read(header, 0, header.Length) == header.Length,
                    fileName + " is too short to be a PNG.");
            }

            //bytes 0..7 are the signature and 8..15 the IHDR chunk header; the dimensions are the
            //four big-endian bytes at 16 and 20
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            Assert.True((header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G'),
                fileName + " is not a PNG.");
            return height;
        }

        /// <summary>
        /// The button heights the page's layout is written against are the shipped plates' own
        /// heights, on both builds. Desktop draws <c>button.png</c> and touch draws
        /// <c>button.mobile.png</c>; the touch and desktop art are not interchangeable, which is
        /// most of why the touch page is so much taller.
        /// </summary>
        [Fact]
        public void TheButtonHeightsAreThePlatesOwnHeights()
        {
            Assert.Equal(PlateHeight("button.png"), LayoutFor(true)[0]);
            Assert.Equal(PlateHeight("button.mobile.png"), LayoutFor(false)[0]);
        }

        /// <summary>
        /// The two colourways are not the same shape, which is the second reason the column is all
        /// one of them.
        /// </summary>
        /// <remarks>
        /// The column's pitch is a single number for all six buttons, so a button whose plate is a
        /// different height from the rest would draw into its neighbour rather than leaving the
        /// gap the arithmetic reserved. <c>greenbutton.png</c> is two pixels taller than
        /// <c>button.png</c>, so a green button anywhere in the desktop column is a two-pixel
        /// overlap - which is the concrete form of the class remarks' point that green means
        /// "the affirmative answer" on this page, and nothing here is one.
        /// </remarks>
        [Fact]
        public void TheTwoButtonColourwaysAreNotInterchangeable()
        {
            Assert.NotEqual(PlateHeight("button.png"), PlateHeight("greenbutton.png"));
        }

        /// <summary>
        /// The action column is the only part of the page whose height does not come from the
        /// dialog's own, so it is the only thing that can push past the footer. This is the rule
        /// that stops it: the page is never built shorter than its buttons need, so the last one
        /// and Close always have the page between them.
        /// </summary>
        /// <remarks>
        /// Swept from the floor upwards, in steps, rather than over a list of heights written out
        /// here. A fixed sample reads well until the plates change: a 640px page clears a floor
        /// of 630 and not one of 837, and the failure it reports is a height the page can never
        /// be built at, because the floor is what stops it. Everything at or above the floor is a
        /// height the page can actually be shown at, so that is the whole of what this promises.
        /// </remarks>
        [Fact]
        public void TheLastButtonNeverReachesTheFooter()
        {
            foreach (var desktop in new[] { true, false })
            {
                var l = LayoutFor(desktop);
                var floor = FloorFor(desktop);

                //the floor itself, and every pixel above it up to a page taller than either build
                //allows - which is what a window with room to spare produces
                for (var height = floor; height <= l[3] + 200; height += 7)
                {
                    var v = NeighbourhoodPanelLayout.Arrange(height, 96, 96, l[0], 10,
                        NeighbourhoodPanelModel.ActionCount, l[2], l[1]);
                    Assert.True(v.ActionBottom + 10 <= v.FooterTop,
                        "the button column runs into the footer at " + height + "px ("
                        + (desktop ? "desktop" : "touch") + ")");
                }
            }
        }

        /// <summary>
        /// The floor is the height the arithmetic says the column needs, so this pins that it is
        /// neither a page too short (which would overlap) nor a wasteful one (which would leave a
        /// short window short of page it could have used).
        /// </summary>
        [Fact]
        public void TheFloorIsExactlyWhatTheButtonColumnNeeds()
        {
            foreach (var desktop in new[] { true, false })
            {
                var l = LayoutFor(desktop);
                var floor = FloorFor(desktop);

                //the arithmetic, spelled out: the column, a gap, then the footer row less the
                //quarter of a button its controls sit above
                Assert.Equal(96 + (6 * (l[0] + 10)) + 10 + 96 - (l[0] / 4), floor);

                var v = NeighbourhoodPanelLayout.Arrange(floor, 96, 96, l[0], 10,
                    NeighbourhoodPanelModel.ActionCount, l[2], l[1]);
                Assert.Equal(96 + (6 * (l[0] + 10)), v.ActionBottom);

                //at the floor the gap between the last button and the footer is exactly the gap
                //between two stacked elements, and not one pixel less
                Assert.Equal(v.ActionBottom + 10, v.FooterTop);
            }
        }

        /// <summary>
        /// The other half of the rule: the hint has a three-line floor, and a page with room for
        /// the buttons and the footer has room for it. Without this, "never overlaps" would be
        /// satisfied by an explanation that is never shown.
        /// </summary>
        [Fact]
        public void TheExplanationGetsItsThreeLinesWhereThereIsRoomForThem()
        {
            foreach (var desktop in new[] { true, false })
            {
                var l = LayoutFor(desktop);
                var v = NeighbourhoodPanelLayout.Arrange(l[3], 96, 96, l[0], 10,
                    NeighbourhoodPanelModel.ActionCount, l[2], l[1]);

                Assert.True(v.HintHeight >= 3 * l[1],
                    "the explanation got " + v.HintHeight + "px on a " + l[3] + "px page ("
                    + (desktop ? "desktop" : "touch") + ")");
            }
        }

        /// <summary>
        /// The explanation gives way rather than the Close button: its height is clamped to the
        /// room, so it can never be drawn over the footer at any height - including the ones where
        /// there is no room at all.
        /// </summary>
        [Fact]
        public void TheExplanationUnderTheButtonsNeverRunsIntoTheFooter()
        {
            foreach (var desktop in new[] { true, false })
            {
                var l = LayoutFor(desktop);
                for (var height = 1; height <= l[3] + 200; height += 7)
                {
                    var v = NeighbourhoodPanelLayout.Arrange(height, 96, 96, l[0], 10,
                        NeighbourhoodPanelModel.ActionCount, l[2], l[1]);
                    Assert.True(v.HintHeight >= 0, "the hint was given a negative height");
                    Assert.True(v.HintTop + v.HintHeight <= Math.Max(v.FooterTop, v.HintTop),
                        "the hint runs into the footer on a " + height + "px page ("
                        + (desktop ? "desktop" : "touch") + ")");
                }
            }
        }

        /// <summary>The action column starts under the title band and grows downwards only.</summary>
        [Fact]
        public void TheButtonsStartBelowTheTitleAndStackDownwards()
        {
            var l = LayoutFor(true);
            var v = NeighbourhoodPanelLayout.Arrange(l[3], 96, 96, l[0], 10,
                NeighbourhoodPanelModel.ActionCount, l[2], l[1]);

            Assert.Equal(96 + 6 * (l[0] + 10), v.ActionBottom);
            Assert.Equal(v.ActionBottom + 10, v.HintTop);
            Assert.True(v.HintTop > 96, "the column starts inside the title band");
        }

        /// <summary>
        /// The footer is the one thing that must not move, so its position is the height's own
        /// arithmetic and not a function of how many buttons there are. A seventh button must not
        /// push Close down the page.
        /// </summary>
        [Fact]
        public void TheFooterDoesNotMoveWhenTheButtonColumnGrows()
        {
            var l = LayoutFor(true);
            var quarter = l[0] / 4;

            for (var buttons = 0; buttons <= 8; buttons++)
            {
                var v = NeighbourhoodPanelLayout.Arrange(l[3], 96, 96, l[0], 10, buttons, l[2], l[1]);
                Assert.Equal(l[3] - 96 + quarter, v.FooterTop);
            }
        }

        /// <summary>
        /// The slot list takes what is left, and is never shorter than one row - a list shorter
        /// than a row cannot show a row, which would make the page look empty.
        /// </summary>
        [Fact]
        public void TheSlotListFillsThePageAndIsNeverShorterThanARow()
        {
            var desktop = LayoutFor(true);
            Assert.Equal(desktop[3] - 96 - 96, NeighbourhoodPanelLayout.Arrange(desktop[3], 96, 96,
                desktop[0], 10, NeighbourhoodPanelModel.ActionCount, desktop[2], desktop[1]).ListHeight);

            var touch = LayoutFor(false);
            Assert.Equal(touch[2], NeighbourhoodPanelLayout.Arrange(200, 96, 96,
                touch[0], 10, NeighbourhoodPanelModel.ActionCount, touch[2], touch[1]).ListHeight);
        }

        /// <summary>
        /// The page is capped at the build's maximum height, so a ceiling set below what the
        /// column needs would clamp the page to a height at which the last button is drawn over
        /// Close. This is the assertion that caught the touch page when the buttons became the
        /// game's real 100px plate: 740 was a valid ceiling for 64px buttons and an overlap for
        /// 100px ones.
        /// </summary>
        [Fact]
        public void NeitherBuildCapsThePageBelowWhatTheColumnNeeds()
        {
            foreach (var desktop in new[] { true, false })
            {
                var l = LayoutFor(desktop);
                var floor = FloorFor(desktop);

                Assert.True(floor <= l[3],
                    "the " + (desktop ? "desktop" : "touch") + " page is capped at " + l[3]
                    + "px but its column needs " + floor + "px");
            }
        }

        /// <summary>
        /// The build's own height is what the page is actually shown at on a window with room to
        /// spare, so that is the height the no-overlap rule has to hold at. Asserted separately
        /// from the floor because "never overlaps at the minimum" and "never overlaps at the
        /// height players see" fail independently: a cap raised past the floor without checking
        /// where the footer went would slide the explanation out of existence.
        /// </summary>
        [Fact]
        public void TheColumnAndTheFooterAreClearOfEachOtherAtTheHeightThePageIsShownAt()
        {
            foreach (var desktop in new[] { true, false })
            {
                var l = LayoutFor(desktop);
                var v = NeighbourhoodPanelLayout.Arrange(l[3], 96, 96, l[0], 10,
                    NeighbourhoodPanelModel.ActionCount, l[2], l[1]);

                Assert.True(v.HintTop < v.FooterTop,
                    "the column and the footer meet on a " + l[3] + "px page ("
                    + (desktop ? "desktop" : "touch") + ")");
            }
        }
    }
}
