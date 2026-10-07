/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics;
using FSO.SimAntics.Model;
using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// How badly a sim wants something, as far as the plumbbob is concerned.
    /// </summary>
    /// <remarks>
    /// Three bands rather than a continuous value, because the plumbbob is a decoration: the
    /// only thing a player reads off it is "is this sim in trouble", and a band is the smallest
    /// answer that can say that. The thresholds are on the motive scale the engine actually
    /// uses (-100 to 100, see <c>VMAvatar.SetMotiveData</c>).
    /// </remarks>
    public enum PlumbbobUrgency
    {
        /// <summary>Nothing is pressing. The plumbbob turns at its resting rate.</summary>
        Calm = 0,

        /// <summary>Something is getting low. The plumbbob turns a little faster.</summary>
        Concerned,

        /// <summary>Something has run out. The plumbbob spins.</summary>
        Urgent,
    }

    /// <summary>
    /// What the plumbbob above one sim should look like this frame.
    /// </summary>
    /// <remarks>
    /// A struct rather than a class on purpose: <c>UILotControl.Update</c> evaluates one of
    /// these per avatar per frame, and a heap allocation at that rate is exactly the kind of
    /// thing the plumbbob's own update loop has never had.
    /// </remarks>
    public readonly struct PlumbbobPresentation
    {
        /// <summary>The motive the sim needs most.</summary>
        public VMMotive Motive { get; }

        /// <summary>That motive's value, in the engine's -100..100 scale.</summary>
        public short Value { get; }

        /// <summary>The band <see cref="Value"/> falls in.</summary>
        public PlumbbobUrgency Urgency { get; }

        /// <summary>
        /// Radians per second to add to the plumbbob's <em>resting</em> spin for this band.
        ///
        /// <para>
        /// This is a spin rate and not a colour or a size because those are the only other
        /// things a client can reach, and they are both out of reach: the diamond's size is the
        /// hard-coded <c>Matrix.CreateScale(1.33f)</c> in
        /// <c>FSO.Vitaboy.Avatar.DrawHeadObject</c> and its colour comes from the textures
        /// inside whichever <c>.fsom</c> mesh is loaded, both of which are engine-side. Adding
        /// this to the existing spin is what makes a sim in trouble visible from across the lot
        /// without touching any of the behaviour the resting spin already has.
        /// </para>
        /// </summary>
        public float SpinBoost { get; }

        internal PlumbbobPresentation(VMMotive motive, short value, PlumbbobUrgency urgency, float spinBoost)
        {
            Motive = motive;
            Value = value;
            Urgency = urgency;
            SpinBoost = spinBoost;
        }
    }

    /// <summary>
    /// Which of a sim's needs the plumbbob above their head should be showing, and how fast it
    /// should turn because of it.
    ///
    /// <para>
    /// <b>Which need.</b> TS1 cycles a sim through its needs in a fixed order, and that order
    /// is what the game itself used: hunger first, then bladder, then energy, and so on down to
    /// the two that are only a mood. The winner is simply the lowest value in that order's
    /// terms - the most drained need wins, and a tie goes to whichever need comes first in
    /// <see cref="Priority"/>, which is the order a cycle would have picked.
    /// </para>
    ///
    /// <para>
    /// <b>Why not mood.</b> <c>VMMotive.Mood</c> is left out on purpose: it is a mood, not a
    /// need, and the live-mode button next to the plumbbob is already drawn as a mood bar
    /// (<c>UILiveButton</c> reads it every frame). Two diamonds fighting over the same number
    /// would be worse than one.
    /// </para>
    ///
    /// <para>
    /// Everything below <see cref="Evaluate"/> is pure - shorts in, a struct out - so the whole
    /// rule is testable without a VM, a lot or a graphics device. <see cref="ForAvatar"/> is the
    /// only part that touches the engine, and it is eight array reads.
    /// </para>
    /// </summary>
    public static class PlumbbobMotive
    {
        /// <summary>
        /// A need at or above this is comfortable. The engine clamps motives to -100..100, so
        /// this is a little under half of the full scale rather than its top.
        /// </summary>
        public const short CalmThreshold = 40;

        /// <summary>
        /// A need at or above this is not yet urgent, but it is low enough to show. Anything
        /// below it has run out.
        /// </summary>
        public const short ConcernedThreshold = 0;

        /// <summary>Extra spin for a comfortable sim: none, because none is the resting look.</summary>
        public const float CalmSpinBoost = 0f;

        /// <summary>Extra spin for a sim with a low need.</summary>
        public const float ConcernedSpinBoost = 1.2f;

        /// <summary>Extra spin for a sim with a need that has run out.</summary>
        public const float UrgentSpinBoost = 2.5f;

        /// <summary>
        /// How many needs are watched, and therefore how wide the per-frame scratch buffer is.
        /// </summary>
        /// <remarks>
        /// A constant because <c>stackalloc</c> needs one - there is no way to size the buffer
        /// from <see cref="Priority"/> at run time without giving up the only thing the buffer is
        /// for. The two have to agree, so the tests assert that
        /// <see cref="Priority"/> has exactly this many entries; adding a ninth need means
        /// changing this too.
        /// </remarks>
        public const int WatchedNeeds = 8;

        /// <summary>
        /// The needs the plumbbob watches, in the order a cycle would visit them.
        /// </summary>
        /// <remarks>
        /// A static array rather than an enum so the order is data a test can assert on, and
        /// indexed with a plain <c>for</c> so the per-frame evaluation allocates nothing. The
        /// values are the engine's own <see cref="VMMotive"/> names, which is what makes the
        /// order checkable against the game rather than against a comment.
        /// </remarks>
        public static readonly VMMotive[] Priority =
        {
            VMMotive.Hunger,
            VMMotive.Bladder,
            VMMotive.Energy,
            VMMotive.Hygiene,
            VMMotive.Comfort,
            VMMotive.Fun,
            VMMotive.Social,
            VMMotive.Room,
        };

        /// <summary>
        /// Whether the plumbbob shows motive state at all.
        ///
        /// <para>
        /// On by default, and there is no ini key for it: <c>GlobalSettings</c> and
        /// <c>WorldConfig</c> have no plumbbob-shaped setting to hang this off, and both are
        /// engine files this change is not allowed to touch. <b>Follow-up:</b> the Options page
        /// should own this - add a boolean to <c>GlobalSettings</c> and have the Options panel
        /// write it, replacing this field. The call site
        /// (<c>UILotControl.Update</c>) already asks once per frame and does nothing when it is
        /// false, so wiring it to a setting is a one-line change there.
        /// </para>
        /// <para>
        /// <b>Not a hover feature.</b> Worth saying plainly, because "the plumbbob shows up when
        /// I point at a character and tells me nothing" is a real complaint and it is about the
        /// auto-follow panel, not this. The head object is attached from two facts about the sim
        /// alone - is it the one being controlled, and is it category 87, TS1's "at work or at
        /// school" crowd - and <see cref="ForAvatar"/> is asked about every sim carrying a
        /// diamond on every frame, with the cursor nowhere in it. The two are opposites. The
        /// panel was a response to the player pointing at somebody and had nothing to say; the
        /// spin is a standing fact about the sim, and spin rate is the only channel a client
        /// reaches for it, since the diamond's colour and size are both engine-side. Scoping the
        /// spin to the hovered sim would take away the one glanceable "that one is in trouble"
        /// cue on a lot of thirty, so it is left alone deliberately rather than by omission.
        /// </para>
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// The band a single motive value falls in.
        /// </summary>
        /// <remarks>
        /// The two thresholds are inclusive at the bottom, so a need sitting exactly on zero is
        /// still "concerned" and only a genuinely negative one is urgent. That matches how the
        /// live-mode motive bars read: full bar is not yet a problem, empty bar is.
        /// </remarks>
        public static PlumbbobUrgency UrgencyFor(short value)
        {
            if (value >= CalmThreshold) return PlumbbobUrgency.Calm;
            if (value >= ConcernedThreshold) return PlumbbobUrgency.Concerned;
            return PlumbbobUrgency.Urgent;
        }

        /// <summary>The extra spin, in radians per second, a band is worth.</summary>
        public static float SpinBoostFor(PlumbbobUrgency urgency)
        {
            switch (urgency)
            {
                case PlumbbobUrgency.Urgent: return UrgentSpinBoost;
                case PlumbbobUrgency.Concerned: return ConcernedSpinBoost;
                default: return CalmSpinBoost;
            }
        }

        /// <summary>
        /// Decides what a sim's plumbbob should be showing from their eight needs.
        /// </summary>
        /// <param name="hunger">Hunger, -100..100.</param>
        /// <param name="bladder">Bladder, -100..100.</param>
        /// <param name="energy">Energy, -100..100.</param>
        /// <param name="hygiene">Hygiene, -100..100.</param>
        /// <param name="comfort">Comfort, -100..100.</param>
        /// <param name="fun">Fun, -100..100.</param>
        /// <param name="social">Social, -100..100.</param>
        /// <param name="room">Room, -100..100.</param>
        /// <remarks>
        /// The parameters are in <see cref="Priority"/> order, and that is load-bearing rather
        /// than cosmetic: the values go into a <c>stackalloc</c> positionally and the winner is
        /// reported as <c>Priority[worst]</c>, so any other order silently reports the wrong
        /// motive. The tests pin the two together.
        /// <para>
        /// Reads every need rather than short-circuiting on the first low one, so the winner is
        /// always the lowest of all of them. The values live in a <c>stackalloc</c> of
        /// <see cref="WatchedNeeds"/> shorts, so the whole call is allocation-free - which is the
        /// only reason it is safe to run per avatar per frame.
        /// </para>
        /// </remarks>
        public static PlumbbobPresentation Evaluate(short hunger, short bladder, short energy, short hygiene,
            short comfort, short fun, short social, short room)
        {
            Span<short> values = stackalloc short[WatchedNeeds]
            {
                hunger, bladder, energy, hygiene, comfort, fun, social, room,
            };

            var worst = 0;
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] < values[worst]) worst = i;
            }

            var value = values[worst];
            var urgency = UrgencyFor(value);
            return new PlumbbobPresentation(Priority[worst], value, urgency, SpinBoostFor(urgency));
        }

        /// <summary>
        /// The same decision, read off a sim.
        /// </summary>
        /// <param name="avatar">The sim. A null avatar reads as a perfectly content one.</param>
        /// <remarks>
        /// The only part of this file that touches the engine, and the only reason it can stay
        /// cheap: <c>VMAvatar.GetMotiveData</c> is a bounds check and an array read, and this
        /// calls it eight times. No LINQ, no enumerators, no allocation.
        /// <para>
        /// The avatar is the whole of the input. There is no cursor, no hovered object and no
        /// screen in the signature, and none is consulted, so the call site can - and does - ask
        /// about every sim on the lot rather than only the one under the mouse. That is what
        /// makes it safe to be answered for one sim per frame in a loop over
        /// <c>ObjectQueries.Avatars</c>: two consecutive calls about two different sims cannot
        /// influence each other, and the tests pin that.
        /// </para>
        /// </remarks>
        public static PlumbbobPresentation ForAvatar(VMAvatar avatar)
        {
            if (avatar == null) return Evaluate(100, 100, 100, 100, 100, 100, 100, 100);

            return Evaluate(
                avatar.GetMotiveData(VMMotive.Hunger),
                avatar.GetMotiveData(VMMotive.Bladder),
                avatar.GetMotiveData(VMMotive.Energy),
                avatar.GetMotiveData(VMMotive.Hygiene),
                avatar.GetMotiveData(VMMotive.Comfort),
                avatar.GetMotiveData(VMMotive.Fun),
                avatar.GetMotiveData(VMMotive.Social),
                avatar.GetMotiveData(VMMotive.Room));
        }
    }
}
