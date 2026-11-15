using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Simitone.Client.UI.Panels.Cheats
{
    /// <summary>
    /// The "freeze needs" cheat, and the only part of it that decides anything.
    ///
    /// <para>
    /// <b>The data, not a flag.</b> Whether a Sim's needs are frozen is read out of the Sim
    /// itself: <see cref="VMPersonDataVariable.Cheats"/>, person data slot 29. That is the slot
    /// retail TS1 used for its cheats, it is what the engine's own motive decay already consults
    /// (<c>VMAvatarMotiveDecay</c> returns early when it is set), and it is saved with the Sim -
    /// so the answer to "are this Sim's needs frozen" survives a save and a reload instead of
    /// living in a bool somewhere that a loaded lot has never heard of. Nothing here adds a
    /// parallel flag that could disagree with the data.
    /// </para>
    ///
    /// <para>
    /// <b>Why the client enforces it at all.</b> The engine's check is in
    /// <c>VMAvatarMotiveDecay</c>, which is the TSO decay model. A TS1 lot runs
    /// <c>VMTS1MotiveDecay</c> instead, and that one has no such check - so on the game this
    /// actually ships, setting slot 29 changes nothing on its own. Rather than patch the decay
    /// class (which is the engine's, and which the TS1 tuning constants live in), the freeze is
    /// enforced from here: once a minute, any motive the decay pass has lowered is put back.
    /// </para>
    ///
    /// <para>
    /// <b>Only decay is undone.</b> Restoring the value a Sim started the freeze with would also
    /// undo a Sim eating a meal or going to bed, which is not what "freeze my needs" means and
    /// would leave a Sim unable to act on its own hunger. So the rule is one-sided: a motive may
    /// go up freely and is remembered at its new value, and only a drop is undone. A Sim whose
    /// needs are frozen still gets hungry-looking, still starves at a table, and still sleeps -
    /// it just never gets worse for having done nothing.
    /// </para>
    /// </summary>
    public static class MotiveDecayFreeze
    {
        /// <summary>
        /// The person data slot that says a Sim's needs are frozen. Retail's own cheat slot, and
        /// the one the engine's motive decay already reads.
        /// </summary>
        public const VMPersonDataVariable FreezeSlot = VMPersonDataVariable.Cheats;

        /// <summary>
        /// What the freeze writes into <see cref="FreezeSlot"/>: nonzero, which is all the
        /// engine's own check asks for.
        /// </summary>
        public const short FrozenValue = 1;

        /// <summary>What the freeze writes into <see cref="FreezeSlot"/> when it is lifted.</summary>
        public const short UnfrozenValue = 0;

        /// <summary>
        /// The motives a freeze holds: the ones TS1's decay pass actually touches, read off the
        /// decay class rather than written out here, so a change to what decays is a change to
        /// what freezes without a second list drifting out of step.
        /// </summary>
        public static IReadOnlyList<VMMotive> HeldMotives => VMTS1MotiveDecay.DecrementMotives;

        /// <summary>
        /// The decision, on its own: a motive the freeze is holding, and the value the engine
        /// currently has. Returns the value that should be stored, and the value that should be
        /// written back.
        /// </summary>
        /// <remarks>
        /// Split out from the walk over the lot so the rule can be tested without a VM, and so
        /// the answer to the two questions that matter is written down in one place: a motive
        /// that has <em>risen</em> is accepted and becomes the new held value, and one that has
        /// <em>fallen</em> is restored to what it was.
        /// </remarks>
        /// <param name="held">The value this motive was last seen at, or null if it is not held.</param>
        /// <param name="current">The value the engine has now.</param>
        /// <param name="nextHeld">The value to remember from now on.</param>
        /// <returns>The value to write to the engine, which is <paramref name="current"/> unless it dropped.</returns>
        public static short Resolve(short? held, short current, out short nextHeld)
        {
            if (held.HasValue && current < held.Value)
            {
                // decay: put it back and keep holding it there.
                nextHeld = held.Value;
                return held.Value;
            }
            nextHeld = current;
            return current;
        }

        /// <summary>
        /// Whether the Sim's own data says its needs are frozen. The one place the slot is read,
        /// so "is it frozen" has a single answer.
        /// </summary>
        public static bool IsFrozen(VMAvatar avatar)
        {
            if (avatar == null) return false;
            try
            {
                return avatar.GetPersonData(FreezeSlot) > 0;
            }
            catch (Exception)
            {
                // An avatar with no thread has no person data to read; treat it as not frozen
                // rather than letting a cheat take the lot down.
                return false;
            }
        }

        /// <summary>
        /// Marks a Sim's needs frozen, or lets them decay again.
        ///
        /// <remarks>
        /// Writing the data is the whole of the cheat's effect; the undoing of decay is
        /// <see cref="Update"/>, which is what reads that data back. Deliberately not both in
        /// one place, so that a Sim frozen by anything else - a script, a future cheat - is
        /// frozen by exactly the same code path.
        /// </remarks>
        /// <param name="avatar">The Sim to change.</param>
        /// <param name="frozen">Whether to freeze or unfreeze.</param>
        public static void SetFrozen(VMAvatar avatar, bool frozen)
        {
            if (avatar == null) return;
            avatar.SetPersonData(FreezeSlot, frozen ? FrozenValue : UnfrozenValue);
        }

        /// <summary>
        /// Undoes decay for every frozen Sim on the lot. Called once a game-minute.
        /// </summary>
        /// <remarks>
        /// The cost when nothing is frozen is one person-data read per Sim and one comparison
        /// per motive, which is why this is a straight walk rather than something scheduled: a
        /// cheat that is off most of the time has to be free when it is.
        /// </remarks>
        /// <param name="vm">The lot, or null when there is not one.</param>
        /// <returns>How many motives were put back.</returns>
        public static int Update(VM vm)
        {
            if (vm?.Context?.ObjectQueries == null) return 0;
            int restored = 0;
            foreach (var avatar in vm.Context.ObjectQueries.Avatars)
            {
                if (avatar is not VMAvatar sim) continue;
                if (!IsFrozen(sim)) continue;
                restored += RestoreDecay(sim);
            }
            return restored;
        }

        /// <summary>
        /// Puts back whatever the decay pass has lowered on one Sim, and remembers anything that
        /// has risen.
        /// </summary>
        private static int RestoreDecay(VMAvatar sim)
        {
            int restored = 0;
            foreach (var motive in HeldMotives)
            {
                var current = sim.GetMotiveData(motive);
                var write = Resolve(HeldValuesFor(sim).TryGetValue(motive, out var held) ? held : null,
                    current, out short nextHeld);
                if (write != current)
                {
                    sim.SetMotiveData(motive, write);
                    restored++;
                }
                Hold(sim, motive, nextHeld);
            }
            return restored;
        }

        /// <summary>
        /// The values this freeze is holding for one Sim.
        ///
        /// <remarks>
        /// Keyed by the avatar instance, in a <see cref="ConditionalWeakTable"/>, so an avatar
        /// that leaves the lot takes its record with it rather than leaking it. A Sim removed and
        /// put back is a new instance and starts a fresh hold.
        /// </remarks>
        private static readonly ConditionalWeakTable<VMAvatar, Dictionary<VMMotive, short>> Holds = new();

        private static Dictionary<VMMotive, short> HeldValuesFor(VMAvatar sim)
        {
            return Holds.GetValue(sim, _ => new Dictionary<VMMotive, short>());
        }

        private static void Hold(VMAvatar sim, VMMotive motive, short value)
        {
            HeldValuesFor(sim)[motive] = value;
        }

        /// <summary>
        /// Forgets what was being held for a Sim, so a freeze that is lifted and set again starts
        /// from the values the Sim has now rather than from a stale snapshot.
        /// </summary>
        public static void Forget(VMAvatar sim)
        {
            if (sim == null) return;
            Holds.Remove(sim);
        }
    }
}
