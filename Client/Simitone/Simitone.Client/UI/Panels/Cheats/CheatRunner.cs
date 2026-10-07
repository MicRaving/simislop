using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Simitone.Client.UI.Panels.Cheats
{
    /// <summary>
    /// Runs a parsed <see cref="CheatCommand"/> against the lot.
    ///
    /// <para>
    /// Every cheat here goes through the engine's own state rather than a parallel copy of it:
    /// the clock is <c>VMClock</c>, the needs are <c>VMMotiveData</c> on the avatar, the skills
    /// and the personality are person data on the avatar, and the freeze is
    /// <see cref="MotiveDecayFreeze"/>. A cheat that keeps its own record of what it did would
    /// be a cheat that desynchronises from the game the moment anything else touched the same
    /// value - and would not survive a save.
    /// </para>
    ///
    /// <para>
    /// The money cheats and <c>moveobjects</c> are not here: they are
    /// <c>VMCheatContext</c> commands the VM executes, and the cheat box sends them as such.
    /// This runs the cheats the client has to carry out itself, and every one of them has a
    /// named hook it can be checked against.
    /// </para>
    ///
    /// <para>
    /// Who a family-wide cheat applies to is read from the lot rather than taken as an argument,
    /// because the player does not name a Sim when they type <c>setallmotives</c>: the family is
    /// the lot's current family, which is the same set the household panels list.
    /// </para>
    /// </summary>
    public sealed class CheatRunner
    {
        private readonly VM vm;
        private readonly Func<VMAvatar> activeSim;

        public CheatRunner(VM vm, Func<VMAvatar> activeSim = null)
        {
            this.vm = vm;
            this.activeSim = activeSim;
        }

        /// <summary>The skills <c>killskills</c> clears: TS1's six, as the person data slots the game stores them in.</summary>
        public static readonly VMPersonDataVariable[] SkillSlots =
        {
            VMPersonDataVariable.CookingSkill,
            VMPersonDataVariable.MechanicalSkill,
            VMPersonDataVariable.CharismaSkill,
            VMPersonDataVariable.LogicSkill,
            VMPersonDataVariable.CreativitySkill,
            VMPersonDataVariable.BodySkill
        };

        /// <summary>The five personality traits <c>killtraits</c> clears, as their person data slots.</summary>
        public static readonly VMPersonDataVariable[] TraitSlots =
        {
            VMPersonDataVariable.NicePersonality,
            VMPersonDataVariable.ActivePersonality,
            VMPersonDataVariable.GenerousPersonality,
            VMPersonDataVariable.PlayfulPersonality,
            VMPersonDataVariable.OutgoingPersonality,
            VMPersonDataVariable.NeatPersonality
        };

        /// <summary>
        /// The level <c>setallmotives</c> fills a need to. The engine's own maximum, and the
        /// value a fresh Sim is created with.
        /// </summary>
        public const short FullMotive = 100;

        /// <summary>
        /// Runs a cheat, and reports what it did.
        /// </summary>
        /// <remarks>
        /// Returns false for a cheat that matched a keyword but had nothing to act on - a lot
        /// that has not finished loading, a family with no Sims on it - which the cheat box
        /// answers with the same error sound as a line it does not recognise. The two really are
        /// the same thing to a player: the command did not do anything.
        /// </remarks>
        /// <param name="command">The parsed command.</param>
        /// <param name="message">A line for the log saying what was done.</param>
        public bool Run(CheatCommand command, out string message)
        {
            message = null;
            if (command == null || !command.IsKnown) return false;

            switch (command.Action)
            {
                case CheatAction.SetHour: return SetHour(command, out message);
                case CheatAction.MaxMotives: return MaxMotives(out message);
                case CheatAction.KillSkills: return ClearSlots(SkillSlots, "skills", out message);
                case CheatAction.KillTraits: return ClearSlots(TraitSlots, "personality traits", out message);
                case CheatAction.FreezeNeeds: return FreezeNeeds(command, out message);
                case CheatAction.ListCheats: return ListCheats(out message);
                default: return false;
            }
        }

        /// <summary>
        /// The hooks a cheat reaches, for the log and for the tests. A cheat with no entry here
        /// is a cheat whose effect cannot be pointed at.
        /// </summary>
        public static string HookOf(CheatAction action)
        {
            return action switch
            {
                CheatAction.SetHour => "VMContext.Clock (Hours/Minutes/MinuteFractions)",
                CheatAction.MaxMotives => "VMAvatar.SetMotiveData for each of VMTS1MotiveDecay.DecrementMotives",
                CheatAction.KillSkills => "VMAvatar.SetPersonData for each of TS1's six skill slots",
                CheatAction.KillTraits => "VMAvatar.SetPersonData for each of the five personality slots",
                CheatAction.FreezeNeeds => "VMAvatar person data slot 29 (Cheats), enforced by MotiveDecayFreeze",
                CheatAction.ListCheats => "none: the cheat box shows CheatCommands.HelpText",
                _ => "none"
            };
        }

        private bool SetHour(CheatCommand command, out string message)
        {
            if (vm?.Context?.Clock == null || command.Number == null) return Fail(out message);
            var hour = command.Number.Value;
            // The clock is three fields, not one: hours, minutes, and the fraction within the
            // minute. Setting only the hour would leave the display at whatever minute it
            // happened to be in, and a Sim whose schedule is due in four minutes would act on
            // the new hour at the old minute - the sort of thing that reads as the clock being
            // broken rather than as a cheat having been typed.
            vm.Context.Clock.Hours = hour;
            vm.Context.Clock.Minutes = 0;
            vm.Context.Clock.MinuteFractions = 0;
            message = "Clock set to " + hour.ToString() + ":00.";
            SimitoneLog.Info("Cheat set_hour: " + message);
            return true;
        }

        private bool MaxMotives(out string message)
        {
            var family = FamilyOnLot();
            if (family.Count == 0) return Fail(out message);
            int filled = 0;
            foreach (var sim in family)
            {
                foreach (var motive in MotiveDecayFreeze.HeldMotives)
                {
                    if (sim.GetMotiveData(motive) >= FullMotive) continue;
                    sim.SetMotiveData(motive, FullMotive);
                    filled++;
                }
            }
            message = "Filled " + filled + " needs across " + family.Count + " Sims.";
            SimitoneLog.Info("Cheat setallmotives: " + message);
            return true;
        }

        private bool ClearSlots(VMPersonDataVariable[] slots, string what, out string message)
        {
            var family = FamilyOnLot();
            if (family.Count == 0) return Fail(out message);
            int cleared = 0;
            foreach (var sim in family)
            {
                foreach (var slot in slots)
                {
                    if (sim.GetPersonData(slot) == 0) continue;
                    sim.SetPersonData(slot, 0);
                    cleared++;
                }
            }
            message = "Cleared " + cleared + " " + what + " values across " + family.Count + " Sims.";
            SimitoneLog.Info("Cheat cleared " + what + ": " + message);
            return true;
        }

        private bool FreezeNeeds(CheatCommand command, out string message)
        {
            var frozen = !command.WantsOff;
            var targets = frozen ? FreezeTargets() : AllOnLot();
            if (targets.Count == 0) return Fail(out message);
            foreach (var sim in targets)
            {
                MotiveDecayFreeze.SetFrozen(sim, frozen);
                // A lift has to forget what was being held, or the next freeze would start from a
                // snapshot of needs as they were before the Sim was allowed to live for a while.
                if (!frozen) MotiveDecayFreeze.Forget(sim);
            }
            message = (frozen ? "Froze needs for " : "Unfroze needs for ") + targets.Count + " Sims.";
            SimitoneLog.Info("Cheat freezeneeds: " + message);
            return true;
        }

        private bool ListCheats(out string message)
        {
            message = CheatCommands.HelpText();
            return true;
        }

        /// <summary>
        /// Who <c>freezeneeds</c> applies to: the Sim the player has selected if there is one,
        /// and the whole family otherwise. A cheat that did nothing because the player had not
        /// clicked a Sim first would be the more annoying of the two mistakes, so the selection
        /// is a shortcut rather than a requirement.
        /// </summary>
        private List<VMAvatar> FreezeTargets()
        {
            var selected = activeSim?.Invoke();
            if (selected != null) return new List<VMAvatar> { selected };
            return FamilyOnLot();
        }

        /// <summary>
        /// The lot's current family, as the Sims of it that are actually here. The family record
        /// lists everyone including anyone away, so the two filters are not the same and the
        /// difference is what stops a cheat reporting a change to somebody who is not in the room.
        /// </summary>
        private List<VMAvatar> FamilyOnLot()
        {
            var family = vm?.TS1State?.CurrentFamily;
            if (family?.RuntimeSubset == null) return new List<VMAvatar>();
            var onLot = vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>()
                .Where(x => family.RuntimeSubset.Contains(x.Object?.OBJ?.GUID ?? 0)).ToList();
            return onLot;
        }

        private List<VMAvatar> AllOnLot()
        {
            return vm?.Context?.ObjectQueries?.Avatars.OfType<VMAvatar>().ToList() ?? new List<VMAvatar>();
        }

        private bool Fail(out string message)
        {
            message = null;
            return false;
        }
    }
}
