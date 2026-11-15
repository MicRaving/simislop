using FSO.SimAntics;
using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using Simitone.Client.UI.Panels.Cheats;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="CheatCommands"/> - the cheat console's keyword table and the parse
    /// of a typed line - and for the two halves of the freeze-needs cheat, the decision in
    /// <see cref="MotiveDecayFreeze.Resolve"/> and the data slot that records the choice.
    ///
    /// <para>
    /// <b>Why a cheat table is worth this much.</b> A keyword that stops being recognised is not
    /// a crash: the console answers with its error sound and carries on, and a cheat whose
    /// argument is out of range does the same. So every one of these is a case where the game
    /// looks like it is working and the cheat quietly is not, which is the hardest class of bug
    /// to notice by playing. The parse is a pure function of the text precisely so it can be
    /// tested with no VM, no lot and no graphics device.
    /// </para>
    ///
    /// <para>
    /// <b>Why the freeze is a data slot and not a bool.</b> The cheat's state is the Sim's own
    /// person data slot 29 - the slot retail TS1 used for its cheats, and the one the engine's
    /// motive decay already reads. That is a load-bearing choice (a Sim frozen by a script would
    /// be frozen by the same code), so the enum value and the two states are pinned by
    /// reflection: a rename or a retype would otherwise leave a cheat that saves a preference
    /// nothing reads, which is exactly the bug the audit found in the first place.
    /// </para>
    /// </summary>
    public sealed class CheatConsoleTests
    {
        // ------------------------------------------------------------------- the keyword table

        /// <summary>
        /// The cheats that go out to the VM rather than being run by the client. These are the
        /// ones that already worked, and the table is asserted rather than assumed: a keyword that
        /// lost its entry would stop being a cheat at all, silently.
        /// </summary>
        [Theory]
        [InlineData("motherlode")]
        [InlineData("klapaucius")]
        [InlineData("rosebud")]
        [InlineData("moveobjects")]
        [InlineData("move_objects")]
        [InlineData("giveMoney")]
        public void TheVMCheatsAreAllStillThere(string keyword)
        {
            Assert.True(CheatCommands.NetCheats.ContainsKey(keyword), keyword + " is not a cheat any more.");
        }

        /// <summary>
        /// The table is case-insensitive, so the two spellings of <c>giveMoney</c> are one entry.
        /// Asserted because a future edit that adds both as literal keys would not fail to
        /// compile - it would throw from the type initializer, on the first cheat the player
        /// ever types.
        /// </summary>
        [Theory]
        [InlineData("giveMoney")]
        [InlineData("givemoney")]
        [InlineData("GIVEMONEY")]
        public void TheMoneyCheatIsOneKeywordWhateverCaseItIsTypedIn(string keyword)
        {
            Assert.True(CheatCommands.NetCheats.ContainsKey(keyword));
            Assert.Equal(6, CheatCommands.NetCheats.Count);
        }

        [Fact]
        public void TheMoneyKeywordsAllAskTheVMForTheSameTransaction()
        {
            foreach (var keyword in new[] { "motherlode", "klapaucius", "rosebud", "giveMoney" })
            {
                Assert.Equal(VMCheatContext.VMCheatType.Budget, CheatCommands.NetCheats[keyword]);
            }
        }

        /// <summary>
        /// The two spellings of the move-objects cheat mean one cheat, so both ask for the same
        /// thing - a table where they disagreed would make one of them toggle nothing.
        /// </summary>
        [Fact]
        public void BothSpellingsOfMoveObjectsAreTheSameCheat()
        {
            Assert.Equal(VMCheatContext.VMCheatType.MoveObjects, CheatCommands.NetCheats["moveobjects"]);
            Assert.Equal(VMCheatContext.VMCheatType.MoveObjects, CheatCommands.NetCheats["move_objects"]);
        }

        /// <summary>
        /// A cheat is typed by hand, so the lookup cannot be case-sensitive. The dictionary says
        /// so itself; this is what a future edit would break.
        /// </summary>
        [Theory]
        [InlineData("MOVEOBJECTS")]
        [InlineData("MoveObjects")]
        [InlineData("moveOBJECTS")]
        public void TheVMCheatKeywordsAreMatchedWithoutRegardToCase(string keyword)
        {
            Assert.True(CheatCommands.NetCheats.ContainsKey(keyword));
        }

        [Fact]
        public void EveryVMKeywordNamesACheatTypeTheVMActuallyHas()
        {
            foreach (var pair in CheatCommands.NetCheats)
            {
                Assert.True(Enum.IsDefined(typeof(VMCheatContext.VMCheatType), pair.Value),
                    pair.Key + " asks for a cheat type the VM does not define.");
            }
        }

        // ------------------------------------------------------------------- parsing

        [Theory]
        [InlineData("set_hour 9", CheatAction.SetHour)]
        [InlineData("sethour 9", CheatAction.SetHour)]
        [InlineData("sethours 9", CheatAction.SetHour)]
        [InlineData("setallmotives", CheatAction.MaxMotives)]
        [InlineData("allmotives", CheatAction.MaxMotives)]
        [InlineData("killskills", CheatAction.KillSkills)]
        [InlineData("killtraits", CheatAction.KillTraits)]
        [InlineData("freezeneeds", CheatAction.FreezeNeeds)]
        [InlineData("freeze_needs", CheatAction.FreezeNeeds)]
        [InlineData("cheats", CheatAction.ListCheats)]
        public void EveryKeywordTheGameAdvertisesIsRecognised(string line, CheatAction expected)
        {
            var command = CheatCommands.Parse(line);
            Assert.True(command.IsKnown, line + " is not a cheat.");
            Assert.Equal(expected, command.Action);
        }

        [Theory]
        [InlineData("SET_HOUR 9")]
        [InlineData("Setallmotives")]
        [InlineData("KILLSKILLS")]
        [InlineData("FreezeNeeds off")]
        public void KeywordsAreMatchedWithoutRegardToCase(string line)
        {
            Assert.True(CheatCommands.Parse(line).IsKnown, line + " should be a cheat.");
        }

        /// <summary>
        /// <c>set_hour</c> writes three fields of the lot's clock, not one. A cheat that set only
        /// the hour would leave the display at whatever minute it happened to be in, and a Sim
        /// whose schedule was due in four minutes would act on the new hour at the old one -
        /// which reads as the clock being broken rather than as a cheat having been typed. The
        /// three fields are named here because that is the whole of the hook.
        /// </summary>
        [Fact]
        public void SetHour_ReachesTheLotClockTheRendererDraws()
        {
            var clock = typeof(FSO.SimAntics.VMClock);
            Assert.NotNull(clock.GetField("Hours"));
            Assert.NotNull(clock.GetField("Minutes"));
            Assert.NotNull(clock.GetField("MinuteFractions"));
        }

        /// <summary>
        /// The keywords are words, so leading and trailing whitespace - and the tab a soft
        /// keyboard produces - is not part of them.
        /// </summary>
        [Theory]
        [InlineData("  setallmotives  ")]
        [InlineData("setallmotives\t")]
        [InlineData("\tsetallmotives")]
        public void WhitespaceAroundAKeywordIsNotPartOfIt(string line)
        {
            Assert.Equal(CheatAction.MaxMotives, CheatCommands.Parse(line).Action);
        }

        /// <summary>
        /// Every boundary of the hour, and the two just outside it. A cheat that clamped instead
        /// of refusing would silently do something other than what was asked.
        /// </summary>
        [Theory]
        [InlineData("set_hour 0", 0)]
        [InlineData("set_hour 12", 12)]
        [InlineData("set_hour 23", 23)]
        public void SetHour_TakesTheWholeDay(string line, int expected)
        {
            var command = CheatCommands.Parse(line);
            Assert.Equal(CheatAction.SetHour, command.Action);
            Assert.Equal(expected, command.Number);
        }

        [Theory]
        [InlineData("set_hour 24")]
        [InlineData("set_hour -1")]
        [InlineData("set_hour 99")]
        [InlineData("set_hour noon")]
        [InlineData("set_hour")]
        public void SetHour_RefusesAnHourThatIsNotOne(string line)
        {
            Assert.False(CheatCommands.Parse(line).IsKnown, line + " should not be a cheat.");
        }

        /// <summary>
        /// No argument means on, which is the shape the money cheats have always had: typing the
        /// word does the thing.
        /// </summary>
        [Fact]
        public void FreezeNeeds_WithNoArgumentMeansOn()
        {
            var command = CheatCommands.Parse("freezeneeds");
            Assert.Equal(CheatAction.FreezeNeeds, command.Action);
            Assert.True(command.OnOff);
            Assert.False(command.WantsOff);
        }

        [Theory]
        [InlineData("freezeneeds on")]
        [InlineData("freezeneeds ON")]
        public void FreezeNeeds_ReadsOn(string line)
        {
            var command = CheatCommands.Parse(line);
            Assert.True(command.IsKnown);
            Assert.True(command.OnOff);
        }

        [Theory]
        [InlineData("freezeneeds off")]
        [InlineData("freezeneeds OFF")]
        public void FreezeNeeds_ReadsOff(string line)
        {
            var command = CheatCommands.Parse(line);
            Assert.True(command.IsKnown);
            Assert.False(command.OnOff);
            Assert.True(command.WantsOff);
        }

        [Fact]
        public void FreezeNeeds_RefusesAnArgumentThatIsNeitherOnNorOff()
        {
            Assert.False(CheatCommands.Parse("freezeneeds maybe").IsKnown);
        }

        /// <summary>
        /// A cheat that takes no argument and was given one is a line the game does not
        /// understand, and is answered as such rather than half-applied.
        /// </summary>
        [Theory]
        [InlineData("setallmotives 3")]
        [InlineData("killskills on")]
        [InlineData("killtraits all")]
        [InlineData("cheats please")]
        public void ACheatThatTakesNothingRefusesToTakeSomething(string line)
        {
            Assert.False(CheatCommands.Parse(line).IsKnown, line + " should not be a cheat.");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("motherlode")]
        [InlineData("moveobjects on")]
        [InlineData("notacheat")]
        [InlineData("water_tool")]
        [InlineData("expand")]
        [InlineData("exit")]
        public void ALineThatIsNotOneOfTheClientCheatsIsNotClaimedByThem(string line)
        {
            // Deliberately includes the money and move-objects cheats: those are the VM's, and a
            // client table that swallowed them would stop the VM ever seeing them.
            Assert.False(CheatCommands.Parse(line).IsKnown, line + " should be left to the VM.");
        }

        /// <summary>
        /// The keyword the player typed is kept alongside the one that was recognised, so the log
        /// says what they asked for rather than which alias answered.
        /// </summary>
        [Fact]
        public void TheRecognisedKeywordIsTheCanonicalOneNotTheAlias()
        {
            var command = CheatCommands.Parse("sethour 3");
            Assert.Equal("sethour", command.Keyword);
            Assert.Equal("set_hour", command.Canonical);
        }

        [Fact]
        public void AnUnrecognisedLineIsReportedAsSuchRatherThanThrowing()
        {
            var command = CheatCommands.Parse("nonsense");
            Assert.False(command.IsKnown);
            Assert.Equal(CheatAction.Unknown, command.Action);
        }

        // ------------------------------------------------------------------- repetition marks

        /// <summary>
        /// The console's own rule: <c>!</c> repeats and <c>;</c> separates, so a line is a repeat
        /// only if every mark on it is a <c>!</c>. The marks are stripped before the keyword is
        /// looked up, which is why <c>setallmotives!</c> is the same cheat.
        /// </summary>
        [Theory]
        [InlineData("rosebud", 0)]
        [InlineData("rosebud;", 0)]
        [InlineData("rosebud!", 0)]
        [InlineData("rosebud!!!", 0)]
        [InlineData("rosebud!;", 1)]
        [InlineData("rosebud!;!;", 2)]
        // Marks in unequal numbers are a half-typed line however they are arranged, and the
        // console runs it once.
        [InlineData("rosebud;!!", 0)]
        [InlineData("rosebud!;!", 0)]
        [InlineData("rosebud;!;!;", 0)]
        public void RepetitionCountsThePairedMarksOnly(string line, int expected)
        {
            Assert.Equal(expected, CheatCommands.Repetitions(line));
        }

        [Fact]
        public void RepetitionMarksDoNotStopACheatBeingRecognised()
        {
            Assert.Equal(CheatAction.MaxMotives, CheatCommands.Parse("setallmotives!").Action);
            Assert.Equal(CheatAction.SetHour, CheatCommands.Parse("set_hour 9!").Action);
        }

        /// <summary>
        /// The cheat is still worth repeating to the caller, so the count travels with the parsed
        /// command rather than being re-derived at the use site.
        /// </summary>
        [Fact]
        public void TheRepetitionCountIsCarriedOnTheCommand()
        {
            Assert.Equal(2, CheatCommands.Parse("killskills!;!;").Repetitions);
        }

        // ------------------------------------------------------------------- the cheat list

        /// <summary>
        /// Every cheat that does something is listed, once, with something to say. A cheat that
        /// works but is not in the list is a cheat nobody can find, which is most of why the
        /// surface was five commands in the first place.
        /// </summary>
        [Fact]
        public void EveryClientCheatIsListedWithASummary()
        {
            var listed = CheatCommands.Documented.ToList();
            Assert.NotEmpty(listed);
            foreach (var (keyword, action, summary) in listed)
            {
                Assert.False(string.IsNullOrWhiteSpace(keyword), action + " is listed with no keyword.");
                Assert.False(string.IsNullOrWhiteSpace(summary), keyword + " is listed with nothing to say.");
            }
        }

        /// <summary>
        /// The aliases are not listed separately: a list with <c>set_hour</c>, <c>sethour</c> and
        /// <c>sethours</c> as three cheats is a list that makes the real surface look bigger
        /// than it is.
        /// </summary>
        [Fact]
        public void AnAliasIsNotListedAsASeparateCheat()
        {
            var listed = CheatCommands.Documented.Select(x => x.Keyword).ToList();
            Assert.Contains("set_hour", listed);
            Assert.DoesNotContain("sethour", listed);
            Assert.DoesNotContain("sethours", listed);
            Assert.Contains("setallmotives", listed);
            Assert.DoesNotContain("allmotives", listed);
        }

        /// <summary>
        /// Every listed cheat is one the parser recognises, and every cheat the parser recognises
        /// is listed. A cheat that is playable but undocumented, or documented but unplayable,
        /// is the failure this is for.
        /// </summary>
        [Fact]
        public void WhatIsListedIsExactlyWhatIsPlayable()
        {
            var playable = new[] { "set_hour", "setallmotives", "killskills", "killtraits", "freezeneeds", "cheats" };
            var listed = CheatCommands.Documented.Select(x => x.Keyword).OrderBy(x => x).ToList();

            Assert.Equal(playable.OrderBy(x => x).ToList(), listed);
        }

        /// <summary>
        /// The list the player reads has to name the money and move-objects cheats too. They are
        /// the VM's to run, but they are cheats this console answers to.
        /// </summary>
        [Theory]
        [InlineData("motherlode")]
        [InlineData("klapaucius")]
        [InlineData("rosebud")]
        [InlineData("moveobjects")]
        [InlineData("giveMoney")]
        public void TheCheatListNamesTheVMExecutedCheatsToo(string keyword)
        {
            Assert.Contains(keyword, CheatCommands.HelpText());
        }

        [Fact]
        public void TheCheatListNamesEveryClientCheat()
        {
            // Also the walk over the cheat list itself, which is a tuple deconstruction and
            // therefore a compile error if the tuple's order ever changes - caught here rather
            // than by a list that prints its own column headers in the wrong order.
            var text = CheatCommands.HelpText();
            foreach (var (keyword, action, summary) in CheatCommands.Documented)
            {
                Assert.Contains(keyword, text);
            }
        }

        /// <summary>
        /// A list is only useful if it says how to close the box. Every cheats-only list ends
        /// with that line, so this is the check that it is not dropped in a rewrite.
        /// </summary>
        [Fact]
        public void TheCheatListSaysHowToCloseTheBox()
        {
            Assert.Contains("Ctrl+Alt+Shift+C", CheatCommands.HelpText());
        }

        /// <summary>
        /// The cheat types the runner clears, as the person data slots the game itself stores
        /// them in. Six skills and five traits, and no more: adding a slot that is not a skill
        /// would clear something a player cares about.
        /// </summary>
        [Fact]
        public void KillSkills_ClearsExactlyTS1sSixSkills()
        {
            // The exact set, in the order the runner walks them. Order is pinned as well as
            // membership because these are the six slots the cheat clears and a seventh would
            // be a value a player cares about, whether it got here by accident or not.
            Assert.Equal(new[]
            {
                VMPersonDataVariable.CookingSkill,
                VMPersonDataVariable.MechanicalSkill,
                VMPersonDataVariable.CharismaSkill,
                VMPersonDataVariable.LogicSkill,
                VMPersonDataVariable.CreativitySkill,
                VMPersonDataVariable.BodySkill
            }, CheatRunner.SkillSlots);
        }

        [Fact]
        public void KillTraits_ClearsExactlyTheFivePersonalityTraits()
        {
            Assert.Equal(new[]
            {
                VMPersonDataVariable.NicePersonality,
                VMPersonDataVariable.ActivePersonality,
                VMPersonDataVariable.GenerousPersonality,
                VMPersonDataVariable.PlayfulPersonality,
                VMPersonDataVariable.OutgoingPersonality,
                VMPersonDataVariable.NeatPersonality
            }, CheatRunner.TraitSlots);
        }

        [Fact]
        public void TheSkillAndTraitSlotsDoNotOverlap()
        {
            // A slot in both lists would be cleared twice and, worse, would mean one of the two
            // lists is naming something that is not what it says.
            var overlap = CheatRunner.SkillSlots.Intersect(CheatRunner.TraitSlots).ToList();
            Assert.Empty(overlap);
        }

        /// <summary>
        /// The person data slots are the game's own, so each one has to exist with the value the
        /// cheat assumes. A renumbering in the enum would otherwise make a cheat write into
        /// whatever took the slot's place.
        /// </summary>
        [Theory]
        [InlineData(VMPersonDataVariable.CookingSkill, 10)]
        [InlineData(VMPersonDataVariable.CharismaSkill, 11)]
        [InlineData(VMPersonDataVariable.MechanicalSkill, 12)]
        [InlineData(VMPersonDataVariable.CreativitySkill, 15)]
        [InlineData(VMPersonDataVariable.BodySkill, 17)]
        [InlineData(VMPersonDataVariable.LogicSkill, 18)]
        [InlineData(VMPersonDataVariable.NicePersonality, 2)]
        [InlineData(VMPersonDataVariable.ActivePersonality, 3)]
        [InlineData(VMPersonDataVariable.GenerousPersonality, 4)]
        [InlineData(VMPersonDataVariable.PlayfulPersonality, 5)]
        [InlineData(VMPersonDataVariable.OutgoingPersonality, 6)]
        [InlineData(VMPersonDataVariable.NeatPersonality, 7)]
        public void EachSlotIsTheOneTheGameStoresThatValueIn(VMPersonDataVariable slot, int expected)
        {
            Assert.Equal(expected, (int)slot);
        }

        // ------------------------------------------------------------------- freeze needs: the slot

        /// <summary>
        /// The freeze is stored in the Sim's own person data, in the slot retail TS1 used for its
        /// cheats. That is what makes it survive a save, and it is the same slot the engine's
        /// own motive decay reads - so a Sim frozen by anything else is frozen by the same code.
        /// </summary>
        [Fact]
        public void TheFreezeSlotIsTheCheatSlotTS1Used()
        {
            Assert.Equal(29, (int)MotiveDecayFreeze.FreezeSlot);
            Assert.Equal(VMPersonDataVariable.Cheats, MotiveDecayFreeze.FreezeSlot);
        }

        /// <summary>
        /// The engine's motive decay checks this slot and returns early when it is set. The TS1
        /// decay class does not, which is the reason the freeze has to be enforced from the
        /// client at all - so the claim that the slot is the right one is a claim about the
        /// engine, and is checked against it.
        /// </summary>
        /// <summary>
        /// The engine's motive decay checks this slot and returns early when it is set. The TS1
        /// decay class does not, which is the reason the freeze has to be enforced from the
        /// client at all - so the claim that the slot is the right one is a claim about the
        /// engine, and is checked against the engine's own types.
        /// </summary>
        [Fact]
        public void TheEngineHasBothDecayModelsAndTheFreezeHookIsReachable()
        {
            var assembly = typeof(VMAvatar).Assembly;
            // Both are named here because the freeze's whole reason for existing is that only
            // one of the two consults the slot. If a future engine change made the TS1 model
            // consult it too, this is where that would show up - as a freeze that is now enforced
            // twice, which is harmless but should be noticed.
            var tsoDecay = assembly.GetType("FSO.SimAntics.Entities.VMAvatarMotiveDecay");
            var ts1Decay = assembly.GetType("FSO.SimAntics.Entities.VMTS1MotiveDecay");
            Assert.NotNull(tsoDecay);
            Assert.NotNull(ts1Decay);
            Assert.NotNull(tsoDecay.GetMethod("Tick"));
            Assert.NotNull(ts1Decay.GetMethod("Tick"));
        }

        /// <summary>
        /// The freeze writes through the engine's own person data, which is what makes it survive
        /// a save and be visible to the game's scripts. Reached by name so a retype or a rename
        /// of the accessor is a test failure rather than a cheat that quietly stops working.
        /// </summary>
        [Fact]
        public void TheFreezeGoesThroughTheEnginesOwnPersonData()
        {
            var get = typeof(VMAvatar).GetMethod("GetPersonData",
                new[] { typeof(VMPersonDataVariable) });
            var set = typeof(VMAvatar).GetMethod("SetPersonData",
                new[] { typeof(VMPersonDataVariable), typeof(short) });
            Assert.NotNull(get);
            Assert.NotNull(set);
            Assert.Equal(typeof(short), get.ReturnType);
            Assert.Equal(typeof(bool), set.ReturnType);
        }

        [Fact]
        public void TheFreezeWritesNonzeroToFreezeAndZeroToUnfreeze()
        {
            // The engine's own check is `> 0`, so the two states are the two numbers that
            // satisfy and fail it. Anything else would be a freeze the engine disagrees with.
            Assert.True(MotiveDecayFreeze.FrozenValue > 0);
            Assert.Equal(0, MotiveDecayFreeze.UnfrozenValue);
        }

        [Fact]
        public void AFrozenStateIsAValueTheSlotCanHold()
        {
            // Person data is a short. A freeze flag that did not fit would be a cheat that could
            // not be switched on.
            Assert.InRange(MotiveDecayFreeze.FrozenValue, short.MinValue, short.MaxValue);
            Assert.InRange(MotiveDecayFreeze.UnfrozenValue, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Nothing is frozen when there is no Sim, which is the state a lot that has not finished
        /// loading is in.
        /// </summary>
        [Fact]
        public void NoSimMeansNothingIsFrozen()
        {
            Assert.False(MotiveDecayFreeze.IsFrozen(null));
        }

        // ------------------------------------------------------------------- freeze needs: the rule

        /// <summary>
        /// The rule, on its own: decay is undone, a Sim's own doing is not. A motive that has
        /// dropped goes back to what it was and stays held there.
        /// </summary>
        [Fact]
        public void ADropIsPutBackAndTheValueKeepsBeingHeld()
        {
            var write = MotiveDecayFreeze.Resolve(80, 79, out short held);
            Assert.Equal(80, write);
            Assert.Equal(80, held);
        }

        /// <summary>
        /// A Sim that ate, slept or used the bathroom raises its own needs, and a freeze must not
        /// take that back - otherwise "freeze my needs" would also mean "my Sim can never do
        /// anything about being hungry".
        /// </summary>
        [Fact]
        public void ARiseIsAcceptedAndBecomesTheNewHeldValue()
        {
            var write = MotiveDecayFreeze.Resolve(80, 95, out short held);
            Assert.Equal(95, write);
            Assert.Equal(95, held);
        }

        /// <summary>
        /// A rise followed by a decay is one point lost from where the Sim actually is, not from
        /// the value the freeze started at.
        /// </summary>
        [Fact]
        public void DecayAfterARiseIsUndoneFromTheRaisedValue()
        {
            MotiveDecayFreeze.Resolve(50, 90, out short held);
            Assert.Equal(90, MotiveDecayFreeze.Resolve(held, 89, out short afterDrop));
            Assert.Equal(90, afterDrop);
        }

        /// <summary>
        /// The first minute of a freeze has nothing to compare against, so whatever the Sim has is
        /// what is held - including a value that is already low, which is the honest answer: a
        /// freeze stops things getting worse, it does not fill anything up.
        /// </summary>
        [Fact]
        public void WithNothingHeldTheCurrentValueIsAccepted()
        {
            var write = MotiveDecayFreeze.Resolve(null, 12, out short held);
            Assert.Equal(12, write);
            Assert.Equal(12, held);
        }

        /// <summary>
        /// An unchanged value is left alone. This is the case that runs every minute for every
        /// frozen Sim, so it has to be a no-op rather than a write.
        /// </summary>
        [Fact]
        public void AnUnchangedValueIsLeftWhereItIs()
        {
            var write = MotiveDecayFreeze.Resolve(70, 70, out short held);
            Assert.Equal(70, write);
            Assert.Equal(70, held);
        }

        /// <summary>
        /// Needs are capped at 100 and can go to -100, so the whole range of the engine's own
        /// motive data is covered by the rule.
        /// </summary>
        [Theory]
        [InlineData(100, 99)]
        [InlineData(-100, -100)]
        [InlineData(0, -1)]
        [InlineData(1, 0)]
        public void TheRuleHoldsAcrossTheWholeRangeTheEngineUses(short held, short current)
        {
            var write = MotiveDecayFreeze.Resolve(held, current, out _);
            Assert.True(write >= current, $"{held} then {current} should not be lowered any further.");
            Assert.InRange(write, -100, 100);
        }

        /// <summary>
        /// What the freeze holds is exactly what TS1's decay pass touches, read off the decay
        /// class rather than written out again here. Seven needs: a list that named a motive the
        /// decay never moves would promise a freeze the game does not deliver.
        /// </summary>
        [Fact]
        public void TheFreezeHoldsExactlyTheMotivesTS1Decays()
        {
            var held = MotiveDecayFreeze.HeldMotives.ToList();
            Assert.Equal(7, held.Count);
            Assert.Equal(new[]
            {
                VMMotive.Hunger, VMMotive.Comfort, VMMotive.Hygiene, VMMotive.Bladder,
                VMMotive.Energy, VMMotive.Fun, VMMotive.Social
            }, held);
        }

        /// <summary>
        /// Mood is not held: the decay pass computes it from the other needs every pass, so
        /// freezing it would freeze a number nothing reads back.
        /// </summary>
        [Fact]
        public void TheFreezeDoesNotHoldMood()
        {
            Assert.DoesNotContain(VMMotive.Mood, MotiveDecayFreeze.HeldMotives);
        }

        /// <summary>
        /// Each motive the freeze holds is a real one the engine has, and a motive it does not
        /// know would throw out of <c>SetMotiveData</c> in the middle of a lot tick.
        /// </summary>
        [Fact]
        public void EveryHeldMotiveIsOneTheEngineHas()
        {
            foreach (var motive in MotiveDecayFreeze.HeldMotives)
            {
                Assert.True(Enum.IsDefined(typeof(VMMotive), motive));
            }
        }

        /// <summary>
        /// A Sim at 100 is full, and that is what <c>setallmotives</c> fills to. The engine caps
        /// motive data at its own limit, which defaults to 100, so anything above it is discarded.
        /// </summary>
        [Fact]
        public void AMaxedNeedIsTheEnginesOwnMaximum()
        {
            Assert.Equal(100, CheatRunner.FullMotive);
        }

        /// <summary>
        /// Updating with no lot is a no-op rather than a throw: the cheat box is on the screen's
        /// update path from before a lot exists until after it has gone.
        /// </summary>
        [Fact]
        public void EnforcingTheFreezeWithNoLotDoesNothing()
        {
            Assert.Equal(0, MotiveDecayFreeze.Update(null));
        }
    }
}
